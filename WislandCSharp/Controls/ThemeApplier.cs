using System;
using System.Windows;
using System.Windows.Media;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland.Controls;

/// <summary>把主题色板同步到 XAML 的 DynamicResource 画刷。</summary>
public static class ThemeApplier
{
    public static void Apply(ResourceDictionary res)
    {
        void Set(string key, Color c) => res[key] = new SolidColorBrush(c);

        Set("AppText", Themes.Text());
        Set("AppText2", Themes.Text2());
        Set("AppText3", Themes.Text3());
        Set("AppCard", Themes.Card());
        Set("AppCardHover", Themes.Hover());
        Set("AppHover", Themes.Hover());
        Set("AppPill", Themes.ActivePill());
        Set("AppPillFg", Themes.PillText());
        Set("AppTrack", Themes.Track());
        Set("AppFill", Themes.Fill());
        Set("AppOn", Themes.OnColor());
        Set("AppOnFg", Themes.OnFg());
        Set("AppUp", Themes.Current.Up);
        Set("AppDown", Themes.Current.Down);
        Set("AppOutline", Themes.Outline());
        Set("AppMenuBg", Themes.Current.MenuBg);
        Set("AppMenuFg", Themes.Current.MenuFg);
        Set("AppMenuBorder", Themes.Current.MenuBorder);
    }

    /// <summary>应用当前主题模式并刷新资源字典。</summary>
    public static void ApplyMode(ResourceDictionary res, string mode)
    {
        Themes.Mode = mode;
        Apply(res);
    }
}