using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace KairosCapture.Capture;

internal sealed class RegionOverlay : Window
{
    private readonly DrawingRectangle _bounds;
    private readonly OverlaySurface _surface;
    private IntPtr _handle;
    private DrawingPoint _start;
    private bool _dragging;
    private bool _accepted;
    private bool _closed;

    internal Int32Rect Selection { get; private set; }
    internal Exception? Failure { get; private set; }

    internal RegionOverlay(BitmapSource screenshot, DrawingRectangle bounds)
    {
        _bounds = bounds;
        Title = "Kairos Capture – Bereich auswählen";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = 1;
        Height = 1;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        _surface = new OverlaySurface(screenshot);
        Content = _surface;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        PlaceOverDesktop();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PlaceOverDesktop();
        Activate();
        Focus();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF may apply a monitor's suggested rectangle after WM_DPICHANGED.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_closed)
                PlaceOverDesktop();
        }));
    }

    private void PlaceOverDesktop()
    {
        if (_handle != IntPtr.Zero && !SetWindowPos(_handle, new IntPtr(-1), _bounds.X, _bounds.Y,
                _bounds.Width, _bounds.Height, 0x0040)) // HWND_TOPMOST, SWP_SHOWWINDOW
        {
            Failure = new Win32Exception(Marshal.GetLastWin32Error(), "Das Auswahlfenster konnte nicht über dem Desktop platziert werden.");
            Close();
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        e.Handled = true;
        if (!TryReadCursor(out _start))
            return;
        _dragging = true;
        if (!CaptureMouse())
        {
            Cancel();
            return;
        }
        UpdateSelection(_start);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_dragging && TryReadCursor(out DrawingPoint cursor))
            UpdateSelection(cursor);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!_dragging)
            return;
        e.Handled = true;
        if (!TryReadCursor(out DrawingPoint cursor))
            return;
        UpdateSelection(cursor);
        _accepted = true;
        _dragging = false;
        ReleaseMouseCapture();
        Close();
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonDown(e);
        e.Handled = true;
        Cancel();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
            Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _dragging = false;
        if (!_accepted)
            Selection = Int32Rect.Empty;
        ReleaseMouseCapture();
        base.OnClosed(e);
    }

    private void Cancel()
    {
        Selection = Int32Rect.Empty;
        _dragging = false;
        Close();
    }

    private bool TryReadCursor(out DrawingPoint cursor)
    {
        // Both APIs use physical pixels under PerMonitorV2. Never infer screen DPI from WPF mouse events.
        if (!GetCursorPos(out NativePoint point) || !ScreenToClient(_handle, ref point))
        {
            Failure = new Win32Exception(Marshal.GetLastWin32Error(), "Die Zeigerposition konnte nicht gelesen werden.");
            cursor = default;
            Cancel();
            return false;
        }
        cursor = new DrawingPoint(point.X, point.Y);
        return true;
    }

    private void UpdateSelection(DrawingPoint cursor)
    {
        Selection = CaptureGeometry.Select(new DrawingRectangle(0, 0, _bounds.Width, _bounds.Height), _start, cursor);
        _surface.Selection = Selection;
        _surface.InvalidateVisual();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    private sealed class OverlaySurface : FrameworkElement
    {
        private readonly BitmapSource _screenshot;
        private readonly Brush _shade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
        private readonly Pen _outline = new Pen(new SolidColorBrush(Color.FromRgb(196, 181, 253)), 1.5);
        private readonly Brush _pill = new SolidColorBrush(Color.FromRgb(27, 24, 38));
        internal Int32Rect Selection { get; set; }

        internal OverlaySurface(BitmapSource screenshot) => _screenshot = screenshot;

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            var full = new Rect(0, 0, ActualWidth, ActualHeight);
            drawing.DrawImage(_screenshot, full);
            bool selected = Selection.Width > 0 && Selection.Height > 0;
            Rect region = selected
                ? new Rect(Selection.X * ActualWidth / _screenshot.PixelWidth,
                    Selection.Y * ActualHeight / _screenshot.PixelHeight,
                    Selection.Width * ActualWidth / _screenshot.PixelWidth,
                    Selection.Height * ActualHeight / _screenshot.PixelHeight)
                : Rect.Empty;

            if (selected)
            {
                drawing.DrawRectangle(_shade, null, new Rect(0, 0, full.Width, region.Top));
                drawing.DrawRectangle(_shade, null, new Rect(0, region.Bottom, full.Width, Math.Max(0, full.Height - region.Bottom)));
                drawing.DrawRectangle(_shade, null, new Rect(0, region.Top, region.Left, region.Height));
                drawing.DrawRectangle(_shade, null, new Rect(region.Right, region.Top, Math.Max(0, full.Width - region.Right), region.Height));
                drawing.DrawRectangle(null, _outline, region);
            }
            else
            {
                drawing.DrawRectangle(_shade, null, full);
            }

            string label = selected ? $"{Selection.Width} × {Selection.Height}   ·   Esc abbrechen" : "Bereich ziehen   ·   Esc abbrechen";
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double width = text.Width + 28;
            double height = text.Height + 16;
            double x = selected ? region.Left : (ActualWidth - width) / 2;
            double y = selected ? region.Bottom + 12 : 24;
            if (selected && y + height > ActualHeight - 12)
                y = region.Top - height - 12;
            x = Math.Clamp(x, 8, Math.Max(8, ActualWidth - width - 8));
            y = Math.Clamp(y, 8, Math.Max(8, ActualHeight - height - 8));
            drawing.DrawRoundedRectangle(_pill, _outline, new Rect(x, y, width, height), height / 2, height / 2);
            drawing.DrawText(text, new Point(x + 14, y + 8));
        }
    }
}
