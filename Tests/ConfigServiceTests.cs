using System;
using System.IO;
using System.Text.Json;
using Wisland.Services;
using Xunit;

namespace Wisland.Tests;

/// <summary>
/// 配置服务的行为测试。
///
/// 这是"写坏用户配置就是灾难"的那类代码：默认值合并、旧键迁移、损坏隔离与备份恢复，
/// 每一条都直接影响升级安全，所以优先覆盖。
/// </summary>
public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _dir;

    public ConfigServiceTests()
    {
        _dir = TestPaths.NewTempDir("config");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        TestPaths.TryDelete(_dir);
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private ConfigService NewService() => new(ConfigPath, _dir);

    [Fact]
    public void 缺省时使用内置默认值()
    {
        var cfg = NewService();
        Assert.Equal("top", cfg.Get("island.edge", ""));
        Assert.Equal(220, cfg.Get("island.hover_delay_ms", 0));
        Assert.Null(cfg.LastLoadError);
    }

    [Fact]
    public void 文件值覆盖默认值且未写的键回落默认()
    {
        File.WriteAllText(ConfigPath, """{ "island": { "edge": "bottom" } }""");

        var cfg = NewService();

        Assert.Equal("bottom", cfg.Get("island.edge", ""));
        Assert.Equal(450, cfg.Get("island.collapse_delay_ms", 0));   // 没写的键用默认
    }

    [Fact]
    public void 旧键clipboard_max迁移为max_items且保留用户值()
    {
        File.WriteAllText(ConfigPath, """{ "clipboard": { "max": 25 } }""");

        var cfg = NewService();

        Assert.Equal(25, cfg.Get("clipboard.max_items", 0));
        Assert.Equal(25, ClipboardService.ReadMax(cfg));
    }

    [Fact]
    public void 旧键hover_expand_ms迁移为hover_delay_ms且保留用户值()
    {
        File.WriteAllText(ConfigPath, """{ "island": { "hover_expand_ms": 480 } }""");

        var cfg = NewService();

        Assert.Equal(480, cfg.Get("island.hover_delay_ms", 0));
    }

    [Fact]
    public void 新键已存在时迁移不覆盖()
    {
        File.WriteAllText(ConfigPath,
            """{ "island": { "hover_expand_ms": 480, "hover_delay_ms": 120 } }""");

        var cfg = NewService();

        Assert.Equal(120, cfg.Get("island.hover_delay_ms", 0));
    }

    [Fact]
    public void 配置损坏时隔离原文件并从bak恢复()
    {
        // 先写一份合法配置，并手工造一个 .bak（等同"上一次成功落盘"）
        var good = """{ "island": { "edge": "left", "theme": "rouge" } }""";
        File.WriteAllText(ConfigPath + ".bak", good);
        File.WriteAllText(ConfigPath, """{ "island": { "edge": "le""");   // 半截 JSON

        var cfg = NewService();

        // 坏文件被改名保留
        Assert.NotNull(cfg.CorruptBackupPath);
        Assert.True(File.Exists(cfg.CorruptBackupPath!));
        // 从备份恢复出来的值生效
        Assert.Equal("left", cfg.Get("island.edge", ""));
        Assert.Equal("rouge", cfg.Get("island.theme", ""));
        Assert.NotNull(cfg.LastLoadError);
    }

    [Fact]
    public void 原子写会保留bak且文件内容可解析()
    {
        var cfg = NewService();
        cfg.Set("island.edge", "right");
        cfg.Save();

        Assert.True(File.Exists(ConfigPath));
        Assert.True(File.Exists(ConfigPath + ".bak"));

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.Equal("right", doc.RootElement.GetProperty("island").GetProperty("edge").GetString());
    }

    [Fact]
    public void 批量写入只触发一次Changed且不逐项落盘()
    {
        var cfg = NewService();
        var changes = 0;
        cfg.Changed += (_, _) => changes++;

        cfg.Set("island.edge", "bottom", save: false, notify: false);
        cfg.Set("island.pos", 0.25, save: false, notify: false);
        cfg.Save();
        cfg.NotifyChanged();

        Assert.Equal(1, changes);
        Assert.Equal("bottom", cfg.Get("island.edge", ""));
        Assert.Equal(0.25, cfg.Get("island.pos", 0.0), 3);
    }

    [Fact]
    public void 深合并保留默认分支()
    {
        File.WriteAllText(ConfigPath, """{ "notify": { "sound": { "enabled": true } } }""");

        var cfg = NewService();

        Assert.True(cfg.Get("notify.sound.enabled", false));
        Assert.Equal(3000, cfg.Get("notify.sound.cooldown_ms", 0));   // 同级其它默认值仍在
    }
}
