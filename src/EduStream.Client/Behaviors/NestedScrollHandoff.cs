using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EduStream.Client.Behaviors;

/// <summary>파일 목록이 더 스크롤되지 않을 때 바깥 패널로 휠을 전달한다.</summary>
public static class NestedScrollHandoff
{
    public static void Handle(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not System.Windows.Controls.ListBox list) return;
        var inner = FindScroll(list);
        if (inner is null || (e.Delta > 0 ? inner.VerticalOffset > 0
                : inner.VerticalOffset < inner.ScrollableHeight)) return;

        for (var parent = VisualTreeHelper.GetParent(list); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer outer) continue;
            var lines = SystemParameters.WheelScrollLines;
            if (lines < 0)
            {
                if (e.Delta > 0) outer.PageUp(); else outer.PageDown();
            }
            else
                for (var i = 0; i < lines; i++)
                {
                    if (e.Delta > 0) outer.LineUp(); else outer.LineDown();
                }
            e.Handled = true;
            return;
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
}
