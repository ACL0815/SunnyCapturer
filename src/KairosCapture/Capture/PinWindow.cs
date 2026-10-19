using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KairosCapture.Capture;

/// <summary>A physical-pixel image surface. Shadow pixels are outside ImageBounds, never inside the capture.</summary>
internal sealed class PinWindow : Window
{
    internal const int ShadowPixels = 12;
    private readonly BitmapSource _source;
    private readonly AppSettings _settings;
    private readonly Image _image;
    private readonly Border _surface;
    private readonly Canvas _scene;
    private IntPtr _handle;
    private HwndSource? _hwndSource;
    private bool _closed, _dragging;
    private RegionOverlay.FrameHandle _edges;
    private Point _dragStart;
    private Rect _dragBounds;

    internal Rect ImageBounds { get; private set; }
    internal BitmapSource? ImageSource => _image.Source as BitmapSource;
    internal FrameworkElement PreviewRoot => _scene;
    internal event Action<string>? ImageSaved;

    internal PinWindow(BitmapSource source, System.Drawing.Rectangle desktop, Int32Rect selection, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (selection.Width != source.PixelWidth || selection.Height != source.PixelHeight || selection.Width <= 0 || selection.Height <= 0)
            throw new ArgumentException("Auswahl und angepinntes Bild müssen dasselbe Pixelraster haben.", nameof(selection));
        _source = source;
        _settings = settings;
        ImageBounds = new Rect((double)desktop.X + selection.X, (double)desktop.Y + selection.Y, selection.Width, selection.Height);
        Title = "Kairos · Angepinntes Bild";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        // Only appearance is nonactivating. An intentional click may focus the pin so Esc works locally.
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = 0; Width = Height = 1;
        UseLayoutRounding = false;
        _image = new Image { Source = source, Stretch = Stretch.Fill, IsHitTestVisible = false };
        // Shadow is a separate sibling: never send the sharp image pixels through an Effect intermediate.
        _surface = new Border { Background = Brushes.White, IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Color.FromRgb(9, 10, 18), BlurRadius = 12,
                ShadowDepth = 2, Direction = 270, Opacity = .28, RenderingBias = RenderingBias.Quality } };
        _scene = new Canvas { Background = Brushes.Transparent };
        _scene.Children.Add(_surface);
        _scene.Children.Add(_image);
        Content = new Viewbox { Stretch = Stretch.Fill, Child = _scene };
        ContextMenu = CreateMenu();
        RefreshSurface();
    }

    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("GlassPinContextMenu") };
        void Item(string label, string glyph, Action action)
        {
            var item = new MenuItem { Header = label, Icon = GlassWindow.Icon(this, glyph), Style = (Style)FindResource("GlassPinMenuItem") };
            item.Click += (_, _) => RunOutput(action);
            menu.Items.Add(item);
        }
        Item("Kopieren", "Copy", () => ImageOutput.Copy(_source));
        Item("Speichern…", "Download", SaveWithDialog);
        Item("Originalgröße", "Fullscreen", ResetSize);
        Item("Schließen", "Close", Close);
        return menu;
    }

    private void SaveWithDialog()
    {
        // Pin context saving always asks, even when overlay direct saving is enabled.
        string? path = ImageOutput.Save(_source, this, new AppSettings { SaveFolder = _settings.SaveFolder });
        if (path != null) ImageSaved?.Invoke(path);
    }

    private void RunOutput(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            App.LogException("Pinned image action", ex);
            MessageBox.Show(this, "Die Aktion konnte nicht abgeschlossen werden. Das angepinnte Bild bleibt geöffnet.",
                "Kairos Capture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void ResetSize() => SetImageBounds(new Rect(ImageBounds.Location, new Size(_source.PixelWidth, _source.PixelHeight)));

    internal void ZoomAt(Point physicalCursor, double factor) => SetImageBounds(PinGeometry.Zoom(ImageBounds, physicalCursor, factor));

    private void SetImageBounds(Rect bounds)
    {
        ImageBounds = bounds;
        RefreshSurface();
        PlaceWindow();
    }

    private void RefreshSurface()
    {
        // Integer image edges guarantee an exact 1:1 sample grid at original size at every monitor DPI.
        double width = Math.Round(ImageBounds.Width), height = Math.Round(ImageBounds.Height);
        _scene.Width = width + 2 * ShadowPixels; _scene.Height = height + 2 * ShadowPixels;
        _surface.Width = width; _surface.Height = height;
        _image.Width = width; _image.Height = height;
        Canvas.SetLeft(_surface, ShadowPixels); Canvas.SetTop(_surface, ShadowPixels);
        Canvas.SetLeft(_image, ShadowPixels); Canvas.SetTop(_image, ShadowPixels);
        RenderOptions.SetBitmapScalingMode(_image,
            width == _source.PixelWidth && height == _source.PixelHeight ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_handle);
        _hwndSource?.AddHook(WindowMessage);
        // Prime WPF's cached native limits too: merely handling this message would leave
        // Window.ArrangeOverride constrained to the default desktop tracking size.
        IntPtr limits = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>());
        try
        {
            Marshal.StructureToPtr(new MinMaxInfo(), limits, false);
            SendMessage(_handle, 0x0024, IntPtr.Zero, limits);
        }
        finally { Marshal.FreeHGlobal(limits); }
        PlaceWindow();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0024) // WM_GETMINMAXINFO, in physical pixels.
        {
            MinMaxInfo limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MaxTrackSize = limits.MaxSize = new NativePoint
                { X = PinGeometry.MaximumPixels + 2 * ShadowPixels, Y = PinGeometry.MaximumPixels + 2 * ShadowPixels };
            limits.MinTrackSize = new NativePoint { X = 1, Y = 1 };
            Marshal.StructureToPtr(limits, lParam, false);
            // Keep unhandled so Window's own hook receives and caches these updated limits.
        }
        return IntPtr.Zero;
    }

    protected override void OnContentRendered(EventArgs e) { base.OnContentRendered(e); PlaceWindow(); }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF updates its device transform; retain our physical image size instead of scaling it a second time.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!_closed) PlaceWindow(); }));
    }

    private void PlaceWindow()
    {
        if (_handle == IntPtr.Zero || _closed) return;
        if (!SetWindowPos(_handle, new IntPtr(-1), (int)Math.Round(ImageBounds.X) - ShadowPixels,
            (int)Math.Round(ImageBounds.Y) - ShadowPixels, (int)_scene.Width, (int)_scene.Height, 0x0010)) // SWP_NOACTIVATE
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var origin = new NativePoint();
        if (!GetClientRect(_handle, out NativeRect client) || !ClientToScreen(_handle, ref origin))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (client.Right - client.Left != (int)_scene.Width || client.Bottom - client.Top != (int)_scene.Height ||
            origin.X != (int)Math.Round(ImageBounds.X) - ShadowPixels || origin.Y != (int)Math.Round(ImageBounds.Y) - ShadowPixels)
            throw new InvalidOperationException("Die physische Bildfläche des Pins konnte nicht pixelgenau platziert werden.");
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2) { Close(); e.Handled = true; return; }
        if (!ReadCursor(out _dragStart)) return;
        _dragBounds = ImageBounds;
        _edges = HitEdges(_dragStart);
        _dragging = CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!ReadCursor(out Point point)) return;
        if (_dragging)
        {
            Vector delta = point - _dragStart;
            SetImageBounds(_edges == RegionOverlay.FrameHandle.None
                ? new Rect(_dragBounds.Location + delta, _dragBounds.Size)
                : PinGeometry.Resize(_dragBounds, _edges, delta));
        }
        else
        {
            var edge = HitEdges(point);
            Cursor = edge switch
            {
                RegionOverlay.FrameHandle.Left or RegionOverlay.FrameHandle.Right => Cursors.SizeWE,
                RegionOverlay.FrameHandle.Top or RegionOverlay.FrameHandle.Bottom => Cursors.SizeNS,
                RegionOverlay.FrameHandle.Left | RegionOverlay.FrameHandle.Top or RegionOverlay.FrameHandle.Right | RegionOverlay.FrameHandle.Bottom => Cursors.SizeNWSE,
                RegionOverlay.FrameHandle.Right | RegionOverlay.FrameHandle.Top or RegionOverlay.FrameHandle.Left | RegionOverlay.FrameHandle.Bottom => Cursors.SizeNESW,
                _ => Cursors.SizeAll
            };
        }
    }

    private RegionOverlay.FrameHandle HitEdges(Point point)
    {
        const double grip = 7;
        var edge = RegionOverlay.FrameHandle.None;
        if (Math.Abs(point.X - ImageBounds.Left) <= grip) edge |= RegionOverlay.FrameHandle.Left;
        else if (Math.Abs(point.X - ImageBounds.Right) <= grip) edge |= RegionOverlay.FrameHandle.Right;
        if (Math.Abs(point.Y - ImageBounds.Top) <= grip) edge |= RegionOverlay.FrameHandle.Top;
        else if (Math.Abs(point.Y - ImageBounds.Bottom) <= grip) edge |= RegionOverlay.FrameHandle.Bottom;
        return edge;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _dragging = false; }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ReadCursor(out Point point)) ZoomAt(point, Math.Pow(1.12, e.Delta / 120d));
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { RunOutput(() => ImageOutput.Copy(_source)); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { RunOutput(SaveWithDialog); e.Handled = true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _hwndSource?.RemoveHook(WindowMessage);
        _image.Source = null;
        if (ContextMenu != null) { ContextMenu.IsOpen = false; ContextMenu = null; }
        base.OnClosed(e);
    }

    private static bool ReadCursor(out Point point)
    {
        bool success = GetPhysicalCursorPos(out NativePoint native);
        point = new Point(native.X, native.Y);
        return success;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}

internal static class PinGeometry
{
    internal const double MinimumPixels = 48;
    internal const int MaximumPixels = 32768;

    private static double ClampWidth(double width, double aspect)
    {
        double maximum = Math.Min(MaximumPixels, MaximumPixels * aspect);
        // Extremely thin strips cannot reach 48px on the short side within the supported native extent.
        double minimum = Math.Min(maximum, Math.Max(MinimumPixels, MinimumPixels * aspect));
        return Math.Clamp(width, minimum, maximum);
    }

    internal static Rect Resize(Rect start, RegionOverlay.FrameHandle edges, Vector delta)
    {
        double aspect = start.Width / start.Height;
        bool left = edges.HasFlag(RegionOverlay.FrameHandle.Left), right = edges.HasFlag(RegionOverlay.FrameHandle.Right);
        bool top = edges.HasFlag(RegionOverlay.FrameHandle.Top), bottom = edges.HasFlag(RegionOverlay.FrameHandle.Bottom);
        double dw = left ? -delta.X : delta.X, dh = top ? -delta.Y : delta.Y;
        double width = left || right
            ? start.Width + (top || bottom ? (dw + dh / aspect) / (1 + 1 / (aspect * aspect)) : dw)
            : (start.Height + dh) * aspect;
        width = ClampWidth(width, aspect);
        double height = width / aspect;
        return new Rect(left ? start.Right - width : right ? start.Left : start.Left + (start.Width - width) / 2,
            top ? start.Bottom - height : bottom ? start.Top : start.Top + (start.Height - height) / 2, width, height);
    }

    internal static Rect Zoom(Rect start, Point cursor, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        double width = ClampWidth(start.Width * factor, start.Width / start.Height), scale = width / start.Width;
        return new Rect(cursor.X - (cursor.X - start.Left) * scale, cursor.Y - (cursor.Y - start.Top) * scale, width, start.Height * scale);
    }
}
