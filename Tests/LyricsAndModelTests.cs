using System;
using System.IO;
using Wisland.Services;
using Xunit;

namespace Wisland.Tests;

/// <summary>LRC 歌词解析（多源歌词都收敛到这里，格式稍有差异就会错行）。</summary>
public sealed class LrcParseTests
{
    [Fact]
    public void 解析时间轴与文本()
    {
        var lrc = "[00:12.34]第一行\n[00:15.00]第二行";
        var lyr = LyricsService.ParseLrc(lrc);

        Assert.Equal(2, lyr.Times.Count);
        Assert.Equal(12.34, lyr.Times[0], 2);
        Assert.Equal("第一行", lyr.Lines[0]);
        Assert.Equal(15.0, lyr.Times[1], 2);
    }

    [Fact]
    public void 支持三位毫秒与冒号分隔()
    {
        var lyr = LyricsService.ParseLrc("[00:01:500]a\n[01:02.250]b");

        Assert.Equal(2, lyr.Times.Count);
        Assert.Equal(1.5, lyr.Times[0], 3);
        Assert.Equal(62.25, lyr.Times[1], 3);
    }

    [Fact]
    public void 忽略元数据行与空行()
    {
        var lyr = LyricsService.ParseLrc("[ti:歌名]\n[ar:歌手]\n\n[00:02.00]正文");

        Assert.Single(lyr.Times);
        Assert.Equal("正文", lyr.Lines[0]);
    }

    [Fact]
    public void 空输入返回空歌词()
    {
        var lyr = LyricsService.ParseLrc("");
        Assert.Empty(lyr.Times);
        Assert.Empty(lyr.Lines);
    }

    [Fact]
    public void 时间轴乱序也会排好()
    {
        var lyr = LyricsService.ParseLrc("[00:05.00]后\n[00:01.00]前");
        Assert.Equal("前", lyr.Lines[0]);
    }
}

/// <summary>模型文件有效性判断（下载器与模型库共用）。</summary>
public sealed class ModelValidityTests : IDisposable
{
    private readonly string _dir;

    public ModelValidityTests()
    {
        _dir = TestPaths.NewTempDir("model");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        TestPaths.TryDelete(_dir);
    }

    [Fact]
    public void 文件不存在视为无效()
    {
        Assert.False(ModelDownloader.IsValidFile(Path.Combine(_dir, "nope.bin"), 100, tokenFile: false));
    }

    [Fact]
    public void 明显偏小视为无效()
    {
        var p = Path.Combine(_dir, "small.bin");
        File.WriteAllBytes(p, new byte[100]);
        Assert.False(ModelDownloader.IsValidFile(p, 100L * 1024 * 1024, tokenFile: false));
    }

    [Fact]
    public void token文件只要求非空()
    {
        var p = Path.Combine(_dir, "tokens.txt");
        File.WriteAllText(p, "a\n");
        Assert.True(ModelDownloader.IsValidFile(p, 0, tokenFile: true));
    }
}
