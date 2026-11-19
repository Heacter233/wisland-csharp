using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisland.Plugins;
using Wisland.Services;

namespace HelloPlugin;

/// <summary>
/// 示例插件：演示宿主契约的完整用法，同时作为插件开发规范的"可执行样板"。
///
/// 要点：
///   1. 必须有无参构造函数；
///   2. OnLoad 只做初始化，不要阻塞（宿主 8 秒超时）；
///   3. UI 一律用 RegisterSettingsSection 延迟构建，不要在 OnLoad 里创建控件；
///   4. 颜色/字体用宿主的主题资源（AppText / AppCard ...），否则切主题会错色；
///   5. OnUnload 释放自己的资源（定时器由宿主托管，会自动释放）。
/// </summary>
public sealed class HelloPlugin : IWislandPlugin
{
    private IPluginContext? _ctx;
    private IDisposable? _timer;
    private int _ticks;

    public void OnLoad(IPluginContext context)
    {
        _ctx = context;
        context.Log.Info($"示例插件已加载，宿主版本 {context.HostVersion}");

        // 设置面板：延迟构建（宿主在设置页打开时才调用）
        context.RegisterSettingsSection("示例插件", "演示消息推送与宿主定时器", BuildPanel);

        // 宿主托管的定时器：卸载时自动释放；最小间隔 100ms
        _timer = context.RegisterTimer(TimeSpan.FromSeconds(3), () =>
        {
            _ticks++;
            _ctx?.Log.Debug($"定时器第 {_ticks} 次触发");
        });
    }

    public void OnUnload()
    {
        // 定时器由宿主托管的 IDisposable，这里主动释放也可以
        _timer?.Dispose();
        _timer = null;
        _ctx?.Log.Info("示例插件已卸载");
        _ctx = null;
    }

    // ---------------- 设置面板（UI 规范示范） ----------------

    private FrameworkElement BuildPanel()
    {
        var root = new StackPanel();

        // 说明文字：用 T3 样式（小号次要色），自动跟随主题
        root.Children.Add(Styled(new TextBlock
        {
            Text = "这个面板由插件提供，宿主只负责放到设置页里。",
            TextWrapping = TextWrapping.Wrap,
        }, "T3"));

        // 输入框：用宿主的 AppBox 样式
        var input = new TextBox
        {
            Text = "来自示例插件的问候",
            Margin = new Thickness(0, 10, 0, 0),
            Height = 34,
        };
        input.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        root.Children.Add(input);

        // 按钮行：用 PillBtn
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var send = new Button { Content = "推送到消息页", Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        send.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        send.Click += (_, _) =>
        {
            try
            {
                _ctx?.PushMessage(new PluginMessage
                {
                    Source = "hello",
                    SourceName = "示例插件",
                    Title = string.IsNullOrWhiteSpace(input.Text) ? "你好" : input.Text,
                    Body = "这是一条由插件推送的消息，点我打开仓库",
                    Level = "info",
                    IconGlyph = "\uE8BD",          // Segoe MDL2 Assets
                    IconColor = "#4C8DFF",
                    Uri = "https://github.com/Heacter233/wisland-csharp",
                });
                _ctx?.RequestExpand("msg");     // 展开到消息页
            }
            catch (Exception ex)
            {
                _ctx?.Log.Error("推送消息失败", ex);
            }
        };
        row.Children.Add(send);

        var count = new Button { Content = "查看定时器计数", Height = 28 };
        count.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        count.Click += (_, _) =>
        {
            _ctx?.PushMessage(new PluginMessage
            {
                Source = "hello",
                SourceName = "示例插件",
                Title = "定时器",
                Body = $"已触发 {_ticks} 次",
                Level = "info",
            });
        };
        row.Children.Add(count);

        root.Children.Add(row);

        // 当前声明的权限（给用户看的透明度）
        root.Children.Add(Styled(new TextBlock
        {
            Text = "声明权限：" + string.Join(" · ", _ctx?.Manifest.Permissions ?? new System.Collections.Generic.List<string>()),
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        }, "T3"));

        return root;
    }

    /// <summary>用宿主主题样式修饰控件（样式不存在时退回普通控件，不抛异常）。</summary>
    private static T Styled<T>(T element, string styleKey) where T : FrameworkElement
    {
        try
        {
            if (Application.Current?.TryFindResource(styleKey) is Style s)
                element.Style = s;
        }
        catch { }
        return element;
    }
}