using System;
using NAudio.Wave;

namespace Wisland.Services;

/// <summary>
/// 实时字幕的音频捕获：把「系统音频回环」或「麦克风」统一成 16kHz 单声道 float 采样。
///
/// - 回环（loopback）：用 <see cref="WasapiLoopbackCapture"/> 抓扬声器输出，
///   设备原生格式通常是 48kHz 立体声 float，需要下混 + 重采样到 16k。
/// - 麦克风：直接用 <see cref="WaveInEvent"/> 请求 16k 单声道 PCM16。
///
/// 输出按回调块（约 20~50ms）抛出 float[]，由 <see cref="LiveService"/> 做 VAD 与切句。
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int TargetRate = 16000;

    /// <summary>每块采样的 RMS 音量（0~1），供 UI 画电平。</summary>
    public event EventHandler<float>? Level;

    /// <summary>16kHz 单声道采样块。</summary>
    public event EventHandler<float[]>? Samples;

    private IWaveIn? _waveIn;
    private LinearResampler? _resampler;
    private int _channels = 1;
    private bool _isFloat;

    // 自动增益：WASAPI 回环抓到的电平通常只有音源本身的 1/3~1/10，
    // 不归一化的话 VAD 会判成静音、Whisper 也识别不出来。
    private const float AgcTargetRms = 0.10f;   // ≈ -20 dBFS
    private const float AgcMaxGain = 16f;
    private float _agcGain = 1f;

    /// <summary>自动增益（默认开）：把语音电平拉到约 -20 dBFS。</summary>
    public bool AgcEnabled { get; set; } = true;

    /// <summary>手动增益（1.0 = 不放大），与 AGC 叠加。</summary>
    public float ManualGain { get; set; } = 1f;

    public bool Running { get; private set; }

    /// <summary>启动捕获。source = "loopback" | "mic"；deviceIndex &lt; 0 表示默认设备。</summary>
    public void Start(string source, int deviceIndex = -1)
    {
        if (Running) return;

        if (string.Equals(source, "mic", StringComparison.OrdinalIgnoreCase))
        {
            var mic = new WaveInEvent
            {
                WaveFormat = new WaveFormat(TargetRate, 16, 1),
                BufferMilliseconds = 50,
                NumberOfBuffers = 3,
            };
            if (deviceIndex >= 0 && deviceIndex < WaveIn.DeviceCount)
                mic.DeviceNumber = deviceIndex;
            _channels = 1;
            _isFloat = false;
            _resampler = null;              // 已经是 16k 单声道
            mic.DataAvailable += OnData;
            mic.RecordingStopped += OnStopped;
            _waveIn = mic;
            mic.StartRecording();
        }
        else
        {
            var loop = new WasapiLoopbackCapture();   // 默认输出设备
            _channels = loop.WaveFormat.Channels;
            _isFloat = loop.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
            _resampler = loop.WaveFormat.SampleRate == TargetRate ? null : new LinearResampler(loop.WaveFormat.SampleRate);
            loop.DataAvailable += OnData;
            loop.RecordingStopped += OnStopped;
            _waveIn = loop;
            loop.StartRecording();
        }

        Running = true;
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _waveIn?.StopRecording(); } catch { }
        try { _waveIn?.Dispose(); } catch { }
        _waveIn = null;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        Running = false;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        try
        {
            // 1) 字节 → float（交错）
            var count = _isFloat ? e.BytesRecorded / 4 : e.BytesRecorded / 2;
            if (count <= 0) return;
            var interleaved = new float[count];
            if (_isFloat)
            {
                Buffer.BlockCopy(e.Buffer, 0, interleaved, 0, count * 4);
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var s = BitConverter.ToInt16(e.Buffer, i * 2);
                    interleaved[i] = s / 32768f;
                }
            }

            // 2) 下混到单声道
            float[] mono;
            var ch = Math.Max(1, _channels);
            if (ch == 1)
            {
                mono = interleaved;
            }
            else
            {
                var frames = count / ch;
                mono = new float[frames];
                for (var f = 0; f < frames; f++)
                {
                    var sum = 0f;
                    var baseIdx = f * ch;
                    for (var c = 0; c < ch; c++) sum += interleaved[baseIdx + c];
                    mono[f] = sum / ch;
                }
            }

            // 3) 重采样到 16k
            var pcm16k = _resampler?.Process(mono) ?? mono;
            if (pcm16k.Length == 0) return;

            // 4) 自动增益：把语音电平拉到约 -20 dBFS，低音量系统音频也能识别
            var rawRms = Rms(pcm16k);
            if (AgcEnabled && rawRms > 0.0005f)
            {
                var desired = Math.Clamp(AgcTargetRms / rawRms, 1f, AgcMaxGain);
                // 变安静时快速提增益，变响时缓慢回退，避免忽大忽小的"呼吸"
                var rate = desired > _agcGain ? 0.25f : 0.02f;
                _agcGain += (desired - _agcGain) * rate;
            }
            else if (!AgcEnabled)
            {
                _agcGain = 1f;
            }

            var gain = Math.Clamp(ManualGain, 0.1f, 16f) * _agcGain;
            if (Math.Abs(gain - 1f) > 0.001f)
            {
                for (var i = 0; i < pcm16k.Length; i++)
                    pcm16k[i] = Math.Clamp(pcm16k[i] * gain, -1f, 1f);
            }

            // 5) 电平：按 dBFS 映射到 0~1（-60dB..0dB），界面上的条才有参考意义
            var rms = Rms(pcm16k);
            var db = 20.0 * Math.Log10(Math.Max(rms, 1e-6));
            Level?.Invoke(this, (float)Math.Clamp((db + 60.0) / 60.0, 0, 1));

            Samples?.Invoke(this, pcm16k);
        }
        catch
        {
            // 单块解析失败不应中断整条链路
        }
    }

    private static float Rms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        var sum = 0.0;
        foreach (var s in samples) sum += (double)s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    public void Dispose() => Stop();

    /// <summary>线性插值重采样（足够字幕用途，开销远小于 MediaFoundation）。</summary>
    private sealed class LinearResampler
    {
        private readonly double _ratio;      // 输入率 / 输出率
        private double _pos;
        private float _last;

        public LinearResampler(int inputRate)
        {
            _ratio = (double)inputRate / TargetRate;
        }

        public float[] Process(float[] input)
        {
            if (input.Length == 0) return Array.Empty<float>();

            var outLen = (int)Math.Floor((input.Length - _pos) / _ratio);
            if (outLen <= 0)
            {
                _pos -= input.Length;
                return Array.Empty<float>();
            }

            var output = new float[outLen];
            for (var i = 0; i < outLen; i++)
            {
                var srcPos = _pos + i * _ratio;
                var idx = (int)srcPos;
                var frac = srcPos - idx;
                var a = idx <= 0 ? _last : input[Math.Min(idx, input.Length - 1)];
                var b = idx + 1 < input.Length ? input[idx + 1] : a;
                output[i] = (float)(a + (b - a) * frac);
            }

            _pos += outLen * _ratio - input.Length;   // 保留余量，避免块间断裂
            _last = input[^1];
            return output;
        }
    }
}
