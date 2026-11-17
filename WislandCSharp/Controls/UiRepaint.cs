using System.Windows;
using System.Windows.Media;

namespace Wisland.Controls;

/// <summary>
/// 重绘辅助。
///
/// 背景：这个应用是 AllowsTransparency 的分层窗口，很多区域没有不透明背景。
/// 当子树内容变化（切分段、滚动、列表重建）时，WPF 只重绘变化的子树，
/// 腾出来的区域不会被父层重画 —— 表现为旧内容残留在毛玻璃上。
/// 这里提供沿祖先链/整棵子树强制重绘的工具，页面在切换内容后调用即可。
/// </summary>
public static class UiRepaint
{
    /// <summary>从 start 开始向上逐级重绘祖先（含自身）。</summary>
    public static void Ancestors(DependencyObject? start, int maxDepth = 32)
    {
        var d = start;
        for (var i = 0; i < maxDepth && d != null; i++)
        {
            if (d is UIElement el) el.InvalidateVisual();
            d = VisualTreeHelper.GetParent(d);
        }
    }

    /// <summary>递归重绘整棵子树（元素数量大时慎用）。</summary>
    public static void Subtree(DependencyObject? root)
    {
        if (root == null) return;
        if (root is UIElement el) el.InvalidateVisual();
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++) Subtree(VisualTreeHelper.GetChild(root, i));
    }
}