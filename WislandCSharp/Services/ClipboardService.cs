using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wisland.Services;

/// <summary>
/// 剪贴板条目（对应 Python clipboard.py 的 item dict）。
/// </summary>
public class ClipboardEntry
{
    public int Id { get; set; }
    public string Kind { get; set; } = "text";    // "text" | "image"
    public string? Text { get; set; }
    public string? ImagePath { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }
}

/// <summary>
/// 剪贴板服务接口：监控剪贴板变化，维护并持久化历史记录。
/// </summary>
public interface IClipboardService
{
    IReadOnlyList<ClipboardEntry> History { get; }
    event EventHandler? Changed;
    /// <summary>绑定窗口句柄（必须在 Start 前调用）。</summary>
    void Initialize(IntPtr hwnd);
    void Start();
    void Stop();
    void SetText(string text);
    /// <summary>把某条历史记录放回剪贴板（文本或图片）。</summary>
    void CopyEntry(ClipboardEntry entry);
    string? GetText();
    void ClearHistory();
    /// <summary>切换置顶状态。</summary>
    void TogglePin(int id);
    /// <summary>删除单条记录。</summary>
    void Remove(int id);
    /// <summary>由 WndProc 在收到 WM_CLIPBOARDUPDATE (0x031D) 时调用。</summary>
    void OnClipboardUpdated();
}

/// <summary>
/// 剪贴板服务实现：AddClipboardFormatListener 监听剪贴板变化，
/// 历史记录持久化到 data/clipboard.json（对应 Python clipboard.json）。
/// </summary>
public class ClipboardService : IClipboardService
{
    private const int TextMax = 20_000;   // 对应 Python TEXT_MAX

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly string _storePath;
    private readonly List<ClipboardEntry> _history = new();
    private readonly object _lock = new();
    private IntPtr _hwnd;
    private bool _running;
    private int _nextId = 1;
    private readonly int _max;

    public event EventHandler? Changed;

    public IReadOnlyList<ClipboardEntry> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    public ClipboardService(IConfigService config)
    {
        _max = Math.Max(1, config.Get("clipboard.max", 10));
        _storePath = Path.Combine(config.DataDir, "clipboard.json");
        LoadHistory();
    }

    public void Initialize(IntPtr hwnd) => _hwnd = hwnd;

    public void Start()
    {
        if (_running || _hwnd == IntPtr.Zero) return;
        try { AddClipboardFormatListener(_hwnd); _running = true; }
        catch { }
    }

    public void Stop()
    {
        if (!_running) return;
        try { RemoveClipboardFormatListener(_hwnd); } catch { }
        _running = false;
    }

    /// <summary>WM_CLIPBOARDUPDATE 到达时调用（UI/STA 线程）。</summary>
    public void OnClipboardUpdated()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                AddImage(System.Windows.Clipboard.GetImage());
                return;
            }
            var text = System.Windows.Clipboard.GetText();
            AddText(text);
        }
        catch { }
    }

    /// <summary>把某条记录放回剪贴板。</summary>
    public void CopyEntry(ClipboardEntry entry)
    {
        try
        {
            if (entry.Kind == "image" && !string.IsNullOrEmpty(entry.ImagePath) && File.Exists(entry.ImagePath))
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(entry.ImagePath);
                img.EndInit();
                img.Freeze();
                System.Windows.Clipboard.SetImage(img);
                return;
            }
            if (!string.IsNullOrEmpty(entry.Text)) System.Windows.Clipboard.SetText(entry.Text);
        }
        catch { }
    }

    public void SetText(string text)
    {
        try { System.Windows.Clipboard.SetText(text); } catch { }
        AddText(text);
    }

    public string? GetText()
    {
        try { return System.Windows.Clipboard.GetText(); }
        catch { return null; }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            foreach (var e in _history) TryDeleteImage(e);
            _history.Clear();
        }
        SaveHistory();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePin(int id)
    {
        lock (_lock)
        {
            var item = _history.FirstOrDefault(h => h.Id == id);
            if (item == null) return;
            item.Pinned = !item.Pinned;
        }
        SaveHistory();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(int id)
    {
        lock (_lock)
        {
            foreach (var e in _history.Where(h => h.Id == id).ToList()) TryDeleteImage(e);
            _history.RemoveAll(h => h.Id == id);
        }
        SaveHistory();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AddText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > TextMax) text = text[..TextMax];

        lock (_lock)
        {
            // 与最近一条相同：去重，不重复入库
            if (_history.Count > 0 && _history[0].Kind == "text" && _history[0].Text == text)
                return;
            _history.Insert(0, new ClipboardEntry { Id = _nextId++, Kind = "text", Text = text, Time = DateTime.Now });
            while (_history.Count > _max) _history.RemoveAt(_history.Count - 1);
        }
        SaveHistory();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>图片条目：保存缩略图（最长边 480）到 data/clip_images，只保留路径。</summary>
    private void AddImage(System.Windows.Media.Imaging.BitmapSource? src)
    {
        if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return;
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(_storePath) ?? ".", "clip_images");
            Directory.CreateDirectory(dir);

            var scale = Math.Min(1.0, 480.0 / Math.Max(src.PixelWidth, src.PixelHeight));
            var bmp = scale < 1.0
                ? new System.Windows.Media.Imaging.TransformedBitmap(src,
                    new System.Windows.Media.ScaleTransform(scale, scale))
                : src;

            // 用内容哈希做文件名：同一张图重复复制时天然去重
            byte[] bytes;
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                bytes = ms.ToArray();
            }
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
            var path = Path.Combine(dir, $"clip_{hash}.png");

            var key = path;
            lock (_lock)
            {
                if (_history.Any(h => h.Kind == "image" && h.ImagePath == key))
                    return;
            }
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);

            lock (_lock)
            {
                _history.Insert(0, new ClipboardEntry
                {
                    Id = _nextId++,
                    Kind = "image",
                    ImagePath = key,
                    Text = $"{src.PixelWidth}×{src.PixelHeight}",
                    Time = DateTime.Now,
                });
                while (_history.Count > _max)
                {
                    var dropped = _history[_history.Count - 1];
                    _history.RemoveAt(_history.Count - 1);
                    TryDeleteImage(dropped);
                }
            }
            SaveHistory();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    private static void TryDeleteImage(ClipboardEntry e)
    {
        if (e.Kind != "image" || string.IsNullOrEmpty(e.ImagePath)) return;
        try { if (File.Exists(e.ImagePath)) File.Delete(e.ImagePath); } catch { }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var items = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(_storePath));
            if (items == null) return;
            lock (_lock)
            {
                _history.Clear();
                _history.AddRange(items);
                _nextId = _history.Count > 0 ? _history.Max(h => h.Id) + 1 : 1;
            }
        }
        catch { }
    }

    private void SaveHistory()
    {
        try
        {
            string json;
            lock (_lock)
                json = JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storePath, json);
        }
        catch { }
    }
}