using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Wisland.Services;

/// <summary>一条生词。</summary>
public sealed class WordEntry
{
    public string Word { get; set; } = "";
    public string Reading { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public int Reviews { get; set; }
    public bool Known { get; set; }
}

/// <summary>
/// 单词本：本地 JSON 持久化（data/wordbook.json），支持增删、标记掌握、复习计数。
/// </summary>
public sealed class WordbookService
{
    private readonly string _path;
    private readonly List<WordEntry> _items = new();
    private readonly object _lock = new();

    public event EventHandler? Changed;

    public WordbookService(IConfigService config)
    {
        _path = Path.Combine(config.DataDir, "wordbook.json");
        Load();
    }

    public IReadOnlyList<WordEntry> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public int Total { get { lock (_lock) return _items.Count; } }

    public int Learning { get { lock (_lock) return _items.Count(w => !w.Known); } }

    public bool Contains(string word)
    {
        var w = Normalize(word);
        if (w.Length == 0) return false;
        lock (_lock) return _items.Any(x => Normalize(x.Word) == w);
    }

    public bool Add(string word, string reading, string meaning, string source = "")
    {
        word = word.Trim();
        if (word.Length == 0) return false;
        if (word.Length > 80) word = word[..80];

        var key = Normalize(word);
        lock (_lock)
        {
            var exist = _items.FirstOrDefault(x => Normalize(x.Word) == key);
            if (exist != null)
            {
                // 已存在：补全释义
                if (exist.Meaning.Length == 0) exist.Meaning = meaning.Trim();
                if (exist.Reading.Length == 0) exist.Reading = reading.Trim();
                exist.Known = false;
            }
            else
            {
                _items.Insert(0, new WordEntry
                {
                    Word = word,
                    Reading = reading.Trim(),
                    Meaning = meaning.Trim(),
                    Source = source,
                });
            }
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Remove(string word)
    {
        var key = Normalize(word);
        lock (_lock) _items.RemoveAll(x => Normalize(x.Word) == key);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleKnown(string word)
    {
        var key = Normalize(word);
        lock (_lock)
        {
            var item = _items.FirstOrDefault(x => Normalize(x.Word) == key);
            if (item == null) return;
            item.Known = !item.Known;
            if (item.Known) item.Reviews++;
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>随机取一个还没掌握的词（复习用）。</summary>
    public WordEntry? PickReview()
    {
        lock (_lock)
        {
            var pool = _items.Where(x => !x.Known).ToList();
            if (pool.Count == 0) return null;
            return pool[Random.Shared.Next(pool.Count)];
        }
    }

    public void ClearKnown()
    {
        lock (_lock) _items.RemoveAll(x => x.Known);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<List<WordEntry>>(File.ReadAllText(_path));
            if (items == null) return;
            lock (_lock)
            {
                _items.Clear();
                _items.AddRange(items);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
            if (!AtomicFile.WriteAllText(_path, json, out var error))
                App.DebugNote("wordbook: 保存失败 " + error);
        }
        catch (Exception ex) { App.DebugNote("wordbook: 保存异常 " + ex.Message); }
    }
}