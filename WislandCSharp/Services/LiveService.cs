using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wisland.Services;

namespace Wisland.Services;

/// <summary>
/// 实时字幕服务：通过 Python 后台进程进行 ASR 识别和翻译。
/// 对应 Python live/service.py 中的 LiveService。
/// </summary>
public class LiveService : ILiveService, IDisposable
{
    // 管道通信
    private TcpClient? _tcpClient;
    private StreamReader? _pipeReader;
    private StreamWriter? _pipeWriter;
    private Process? _asrProcess;
    private readonly object _pipeLock = new();

    // 配置
    private string _modelsDir = "models";
    private string _audioSource = "loopback";
    private string _audioDevice = "";
    private int _blockMs = 80;

    // VAD 配置
    private double _vadThreshold = 0.4;
    private int _minSilenceMs = 350;
    private int _minSpeechMs = 200;
    private int _maxSegmentMs = 10000;

    // ASR 配置
    private bool _asrPartial = true;
    private string _asrDraftMode = "rolling";
    private int _draftIntervalMs = 500;
    private int _draftThreads = 4;
    private string _finalEngine = "parakeet";
    private int _finalThreads = 6;
    private bool _asrContextPrompt = false;

    // 翻译配置
    private bool _translateEnabled = true;
    private bool _translateLive = true;
    private int _liveMinIntervalMs = 900;
    private int _liveMinNewChars = 4;
    private int _contextLines = 3;

    // 运行状态
    private bool _running;
    private readonly object _runningLock = new();

    // 事件
    public event EventHandler<string>? Status;
    public event EventHandler<string>? Error;
    public event EventHandler<string>? Partial;    // 正在说的日语（流式草稿）
    public event EventHandler<(string sid, string jp)>? Segment;  // 整句日语
    public event EventHandler<(string sid, string zh)>? LiveZh;   // 实时译文
    public event EventHandler<(string sid, string zh)>? FinalZh;  // 最终译文
    public event EventHandler<float>? Level;     // 音量 0~1
    public event EventHandler<bool>? RunningChanged;  // 运行状态变化
    public event EventHandler<bool>? DownloadingChanged;  // 模型下载状态

    /// <summary>
    /// 是否正在运行
    /// </summary>
    public bool Running
    {
        get
        {
            lock (_runningLock)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// 初始化 (从配置加载设置)
    /// </summary>
    public void Initialize(string modelsDir = "models", string audioSource = "loopback",
        string audioDevice = "", int blockMs = 80)
    {
        _modelsDir = modelsDir;
        _audioSource = audioSource;
        _audioDevice = audioDevice;
        _blockMs = blockMs;
    }

    /// <summary>
    /// 启动实时字幕服务 (启动 Python ASR 后台进程)
    /// </summary>
    public async Task StartAsync()
    {
        lock (_runningLock)
        {
            if (_running) return;
            _running = true;
        }

        try
        {
            RunningChanged?.Invoke(this, true);

            // 查找 Python 解释器
            var pythonExe = FindPythonExe();
            if (pythonExe == null)
            {
                Error?.Invoke(this, "未找到 Python 解释器，请安装 Python");
                return;
            }

            // 构建启动参数
            var asrScript = Path.Combine(_modelsDir, "..", "live", "asr_pipe.py");
            if (!File.Exists(asrScript))
            {
                // 尝试其他路径
                asrScript = Path.Combine(AppContext.BaseDirectory, "live", "asr_pipe.py");
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{asrScript}\" --models-dir \"{_modelsDir}\" --source \"{_audioSource}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            _asrProcess = Process.Start(psi);
            if (_asrProcess == null)
            {
                Error?.Invoke(this, "无法启动 ASR 后台进程");
                return;
            }

            _pipeReader = new StreamReader(_asrProcess.StandardOutput.BaseStream, Encoding.UTF8);
            _pipeWriter = new StreamWriter(_asrProcess.StandardInput.BaseStream, Encoding.UTF8) { AutoFlush = true };

            // 启动读取线程
            _ = Task.Run(ReadPipeLoopAsync);

            Status?.Invoke(this, "实时字幕已启动");
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
            lock (_runningLock)
            {
                _running = false;
            }
            RunningChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// 停止实时字幕服务
    /// </summary>
    public async Task StopAsync(bool wait = true)
    {
        lock (_runningLock)
        {
            if (!_running) return;
            _running = false;
        }

        try
        {
            // 发送停止命令
            lock (_pipeLock)
            {
                _pipeWriter?.WriteLine("STOP");
                _pipeWriter?.Flush();
            }

            // 等待进程退出
            if (_asrProcess != null && wait)
            {
                _asrProcess.WaitForExit(5000);
                if (!_asrProcess.HasExited)
                    _asrProcess.Kill();
            }

            _pipeReader?.Dispose();
            _pipeWriter?.Dispose();
            _tcpClient?.Dispose();
            _asrProcess?.Dispose();

            _pipeReader = null;
            _pipeWriter = null;
            _tcpClient = null;
            _asrProcess = null;

            RunningChanged?.Invoke(this, false);
            Status?.Invoke(this, "实时字幕已停止");
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }

    /// <summary>
    /// 查找 Python 解释器
    /// </summary>
    private static string? FindPythonExe()
    {
        // 优先查找项目 .venv
        var venvPython = Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe");
        if (File.Exists(venvPython)) return venvPython;

        // 查找系统 python
        foreach (var name in new[] { "python.exe", "pythonw.exe", "python3.exe" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null && p.WaitForExit(3000))
                    return name;
            }
            catch { }
        }

        return null;
    }

    private async Task ReadPipeLoopAsync()
    {
        try
        {
            while (_running && _pipeReader != null && !_pipeReader.EndOfStream)
            {
                var line = await _pipeReader.ReadLineAsync();
                if (line == null) break;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var type))
                    {
                        var typeStr = type.GetString();

                        switch (typeStr)
                        {
                            case "status":
                                if (root.TryGetProperty("message", out var msg))
                                    Status?.Invoke(this, msg.GetString() ?? "");
                                break;

                            case "error":
                                if (root.TryGetProperty("message", out var errMsg))
                                    Error?.Invoke(this, errMsg.GetString() ?? "");
                                break;

                            case "partial":
                                if (root.TryGetProperty("text", out var partialText))
                                    Partial?.Invoke(this, partialText.GetString() ?? "");
                                break;

                            case "segment":
                                var sid = root.TryGetProperty("sid", out var s) ? s.GetString() : "";
                                var jp = root.TryGetProperty("text", out var t) ? t.GetString() : "";
                                Segment?.Invoke(this, (sid ?? "", jp ?? ""));
                                break;

                            case "live_zh":
                                var liveSid = root.TryGetProperty("sid", out var ls) ? ls.GetString() : "";
                                var liveZh = root.TryGetProperty("text", out var lt) ? lt.GetString() : "";
                                LiveZh?.Invoke(this, (liveSid ?? "", liveZh ?? ""));
                                break;

                            case "final_zh":
                                var finalSid = root.TryGetProperty("sid", out var fs) ? fs.GetString() : "";
                                var finalZh = root.TryGetProperty("text", out var ft) ? ft.GetString() : "";
                                FinalZh?.Invoke(this, (finalSid ?? "", finalZh ?? ""));
                                break;

                            case "level":
                                if (root.TryGetProperty("value", out var lvl))
                                    Level?.Invoke(this, lvl.GetSingle());
                                break;

                            case "running_changed":
                                if (root.TryGetProperty("value", out var rc))
                                    RunningChanged?.Invoke(this, rc.GetBoolean());
                                break;

                            case "downloading_changed":
                                if (root.TryGetProperty("value", out var dc))
                                    DownloadingChanged?.Invoke(this, dc.GetBoolean());
                                break;
                        }
                    }
                }
                catch
                {
                    // 跳过无法解析的行
                }
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
        finally
        {
            lock (_runningLock)
            {
                _running = false;
            }
            RunningChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// 发送控制命令到 ASR 后台进程
    /// </summary>
    public async Task SendCommandAsync(string command, object? parameters = null)
    {
        lock (_pipeLock)
        {
            if (_pipeWriter == null) return;

            var msg = new { type = command, parameters };
            var json = JsonSerializer.Serialize(msg);
            _pipeWriter.WriteLine(json);
            _pipeWriter.Flush();
        }
    }

    public void Dispose()
    {
        if (_running)
        {
            StopAsync(wait: true).Wait();
        }
    }
}

/// <summary>
/// 实时字幕服务接口
/// </summary>
public interface ILiveService
{
    bool Running { get; }

    event EventHandler<string>? Status;
    event EventHandler<string>? Error;
    event EventHandler<string>? Partial;
    event EventHandler<(string sid, string jp)>? Segment;
    event EventHandler<(string sid, string zh)>? LiveZh;
    event EventHandler<(string sid, string zh)>? FinalZh;
    event EventHandler<float>? Level;
    event EventHandler<bool>? RunningChanged;
    event EventHandler<bool>? DownloadingChanged;

    void Initialize(string modelsDir = "models", string audioSource = "loopback",
        string audioDevice = "", int blockMs = 80);

    Task StartAsync();
    Task StopAsync(bool wait = true);
    Task SendCommandAsync(string command, object? parameters = null);
}