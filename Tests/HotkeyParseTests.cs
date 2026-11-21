using Wisland.Services;
using Xunit;

namespace Wisland.Tests;

/// <summary>热键字符串解析：配置写错时不应该静默变成"无修饰键的裸键"。</summary>
public sealed class HotkeyParseTests
{
    [Theory]
    [InlineData("Ctrl+Alt+W", 0x0002u | 0x0001u, (uint)'W')]
    [InlineData("ctrl+shift+f5", 0x0002u | 0x0004u, 0x74u)]   // F5 = 0x74
    [InlineData("Win+Space", 0x0008u, 0x20u)]
    [InlineData("Alt+F12", 0x0001u, 0x7Bu)]
    public void 解析常见组合(string input, uint mod, uint vk)
    {
        var (m, k) = HotkeyService.ParseHotkey(input);
        Assert.Equal(mod, m);
        Assert.Equal(vk, k);
    }

    [Fact]
    public void 非法输入返回零值而不是抛异常()
    {
        var (m, k) = HotkeyService.ParseHotkey("");
        Assert.Equal(0u, m);
        Assert.Equal(0u, k);

        var (m2, k2) = HotkeyService.ParseHotkey("+++");
        Assert.Equal(0u, m2);
        Assert.Equal(0u, k2);
    }

    [Fact]
    public void 超出范围的F键不映射()
    {
        var (_, k) = HotkeyService.ParseHotkey("Ctrl+F99");
        Assert.Equal(0u, k);
    }
}
