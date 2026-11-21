using System;
using System.IO;
using System.Text.Json;
using Wisland.Plugins;
using Xunit;

namespace Wisland.Tests;

/// <summary>插件清单校验：非法清单必须被拒绝，且原因要能读懂。</summary>
public sealed class PluginManifestTests : IDisposable
{
    private readonly string _dir;

    public PluginManifestTests()
    {
        _dir = TestPaths.NewTempDir("manifest");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        TestPaths.TryDelete(_dir);
    }

    private PluginManifest? Load(string json, out string error)
    {
        var dir = Path.Combine(_dir, "demo");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "plugin.json");
        File.WriteAllText(path, json);
        // Validate() 会检查程序集文件是否存在：造一个占位文件
        File.WriteAllText(Path.Combine(dir, "Demo.dll"), "");
        return PluginManifest.TryLoad(path, out error);
    }

    private const string ValidJson = """
    {
      "id": "demo",
      "name": "演示",
      "version": "1.0.0",
      "entry": "Demo.Plugin",
      "assembly": "Demo.dll",
      "permissions": ["messages.push"]
    }
    """;

    [Fact]
    public void 合法清单可以加载()
    {
        var m = Load(ValidJson, out var error);
        Assert.NotNull(m);
        Assert.Equal("demo", m!.Id);
        Assert.True(m.Has("messages.push"));
        Assert.Equal("", error);
    }

    [Fact]
    public void 缺少必需字段时拒绝()
    {
        var m = Load("""{ "id": "demo", "name": "x" }""", out var error);
        Assert.Null(m);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void 权限名拼错时拒绝()
    {
        var json = ValidJson.Replace("\"messages.push\"", "\"messages.pushh\"");
        var m = Load(json, out var error);
        Assert.Null(m);
        Assert.Contains("权限", error);
    }

    [Fact]
    public void 程序集名带路径分隔符时拒绝()
    {
        var json = ValidJson.Replace("\"Demo.dll\"", "\"..\\\\evil.dll\"");
        var m = Load(json, out var error);
        Assert.Null(m);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void 非法id字符时拒绝()
    {
        var json = ValidJson.Replace("\"demo\"", "\"demo/../x\"");
        var m = Load(json, out _);
        Assert.Null(m);
    }

    [Fact]
    public void 缺少清单文件返回null()
    {
        var m = PluginManifest.TryLoad(Path.Combine(_dir, "nope.json"), out var error);
        Assert.Null(m);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
