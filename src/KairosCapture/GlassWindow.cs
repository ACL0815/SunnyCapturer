using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace KairosCapture;

internal static class GlassWindow
{
    internal static void Apply(Window window)
    {
        window.Icon = window.TryFindResource("AppIconSmall") as ImageSource ?? window.TryFindResource("AppIcon") as ImageSource;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        int dark = 1, backdrop = 3;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        if (DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) >= 0)
        {
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(handle, ref margins) >= 0)
            {
                if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
                window.Background = Brushes.Transparent;
                return;
            }
        }
        window.Background = new SolidColorBrush(Color.FromRgb(19, 21, 30));
    }

    internal static Path Icon(FrameworkElement context, string glyph) => new()
    {
        Data = context.TryFindResource("Glyph." + glyph) as Geometry ?? Geometry.Empty,
        Style = context.TryFindResource("GlassIcon") as Style,
        IsHitTestVisible = false
    };

    internal static object Caption(FrameworkElement context, string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Icon(context, glyph));
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    internal static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
}
