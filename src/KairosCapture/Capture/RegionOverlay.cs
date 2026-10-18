using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KairosCapture.Editor;
using DrawingRectangle = System.Drawing.Rectangle;

namespace KairosCapture.Capture;

/// <summary>One physical desktop raster, one live object editor, and a movable export crop.</summary>
internal sealed class RegionOverlay : Window
{
    private static readonly ControlTemplate ToolbarButtonTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="ButtonFrame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
              BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="4"
              TextElement.Foreground="{TemplateBinding Foreground}" TextElement.FontFamily="{TemplateBinding FontFamily}"
              TextElement.FontSize="{TemplateBinding FontSize}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5" /></Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ButtonFrame" Property="BorderBrush" Value="#D3B576" /></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="ButtonFrame" Property="Background" Value="#7D4BE3" /></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    [Flags]
    internal enum FrameHandle { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
    private enum FrameGesture { None, New, Move, Resize }
    private readonly DrawingRectangle _bounds;
    private readonly bool _editing;
    private readonly Canvas _scene;
    private readonly FrameChrome _chrome;
    private readonly Border _toolbar;
    private readonly TextBlock _label, _error;
    private readonly Button _undo, _redo;
    private IntPtr _handle;
    private bool _closed, _rawAccepted;
    private FrameGesture _gesture;
    private FrameHandle _frameHandle;
    private Point _start;
    private Int32Rect _before;

    internal AnnotationCanvas Editor { get; }
    internal Int32Rect Selection { get; private set; }
    internal Exception? Failure { get; private set; }
    internal CaptureSessionResult Result { get; private set; } = new(CaptureCompletion.Cancelled);
    internal bool IsClosed => _closed;
    internal bool ToolbarVisible => _toolbar.Visibility == Visibility.Visible;
    internal FrameworkElement PreviewRoot => _scene;
    internal FrameworkElement Toolbar => _toolbar;

    internal RegionOverlay(BitmapSource screenshot, DrawingRectangle bounds, bool editing = true)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        CaptureGeometry.ValidateSize(bounds.Width, bounds.Height);
        if (screenshot.PixelWidth != bounds.Width || screenshot.PixelHeight != bounds.Height)
            throw new ArgumentException("Bildraster und Desktopgrenzen müssen übereinstimmen.", nameof(screenshot));
        _bounds = bounds;
        _editing = editing;
        Title = "Kairos Capture – auf dem Bildschirm bearbeiten";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = 0;
        Width = Height = 1;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Foreground = Brush("#F4F2FA");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        FlowDirection = FlowDirection.LeftToRight;
        Cursor = Cursors.Cross;
        // No layout rounding: one scene unit maps to exactly one physical desktop pixel.
        UseLayoutRounding = false;
        _scene = new Canvas { Width = bounds.Width, Height = bounds.Height, Background = Brushes.Black, ClipToBounds = true };
        TextElement.SetForeground(_scene, Foreground);
        TextElement.SetFontFamily(_scene, FontFamily);
        TextElement.SetFontSize(_scene, FontSize);
        var image = new Image { Source = screenshot, Width = bounds.Width, Height = bounds.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        _scene.Children.Add(image);
        Editor = new AnnotationCanvas(screenshot) { IsHitTestVisible = false, Clip = new RectangleGeometry(Rect.Empty) };
        _scene.Children.Add(Editor);
        _chrome = new FrameChrome(this) { Width = bounds.Width, Height = bounds.Height, IsHitTestVisible = false };
        _scene.Children.Add(_chrome);
        _label = new TextBlock { Margin = new Thickness(6, 2, 6, 5) };
        _error = new TextBlock { Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var stack = new StackPanel();
        stack.Children.Add(_label);
        var tools = new WrapPanel();
        (EditorTool Tool, string Label)[] choices = {
            (EditorTool.Select, "Auswahl"), (EditorTool.Rectangle, "Rechteck"), (EditorTool.Ellipse, "Ellipse"),
            (EditorTool.Arrow, "Pfeil"), (EditorTool.Pen, "Stift"), (EditorTool.Text, "Text"),
            (EditorTool.Highlight, "Markieren"), (EditorTool.Redact, "Schwärzen") };
        foreach (var choice in choices)
        {
            var button = AddButton(tools, choice.Label, () => { Editor.Tool = choice.Tool; UpdateTools(); Editor.Focus(); });
            button.Tag = choice.Tool;
        }
        stack.Children.Add(tools);
        var style = new WrapPanel();
        foreach (string colour in new[] { "#7D4BE3", "#B097E9", "#D3B576", "#FFFFFF", "#EF6461", "#000000" })
        {
            var swatch = AddButton(style, "●", () => Editor.Stroke = Brush(colour));
            swatch.Foreground = Brush(colour);
            swatch.ToolTip = "Farbe " + colour + " · ändert auch das ausgewählte Objekt";
        }
        style.Children.Add(new TextBlock { Text = "Dicke", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        foreach (double thickness in new[] { 1d, 3, 6, 12, 24 })
            AddButton(style, thickness.ToString(CultureInfo.InvariantCulture), () => Editor.Thickness = thickness);
        _undo = AddButton(style, "Rückgängig", Editor.Undo);
        _redo = AddButton(style, "Wiederholen", Editor.Redo);
        AddButton(style, "Löschen", Editor.DeleteSelected);
        stack.Children.Add(style);
        var outputs = new WrapPanel();
        AddButton(outputs, "Kopieren", () => Complete(CaptureCompletion.Copied));
        AddButton(outputs, "Speichern", () => Complete(CaptureCompletion.Saved));
        AddButton(outputs, "OCR", () => Complete(CaptureCompletion.Ocr));
        AddButton(outputs, "Werkbank", () => Complete(CaptureCompletion.Workbench));
        AddButton(outputs, "Abbrechen", Cancel);
        stack.Children.Add(outputs);
        stack.Children.Add(_error);
        _toolbar = new Border { Background = Brush("#12171C"), BorderBrush = Brush("#7D4BE3"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6),
            Width = Math.Min(800, Math.Max(1, bounds.Width - 16)), MaxHeight = Math.Max(1, bounds.Height - 16),
            Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, Visibility = Visibility.Collapsed };
        _scene.Children.Add(_toolbar);
        RenderOptions.SetBitmapScalingMode(_scene, BitmapScalingMode.NearestNeighbor);
        Content = new Viewbox { Stretch = Stretch.Fill, Child = _scene };
        Editor.Changed += OnEditorChanged;
        UpdateTools();
    }

    private void OnEditorChanged(object? sender, EventArgs e) => UpdateTools();

    private static SolidColorBrush Brush(string colour) => new((Color)ColorConverter.ConvertFromString(colour));

    private static Button AddButton(Panel panel, string label, Action click)
    {
        var button = new Button { Style = null, Template = ToolbarButtonTemplate, Content = label, Margin = new Thickness(2), Padding = new Thickness(8, 5, 8, 5),
            Background = Brush("#090E13"), Foreground = Brush("#F4F2FA"), BorderBrush = Brush("#B097E9"),
            BorderThickness = new Thickness(1), FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
            MinHeight = 30, Focusable = false };
        button.Click += (_, _) => click();
        panel.Children.Add(button);
        return button;
    }

    private void UpdateTools()
    {
        if (_undo != null) _undo.IsEnabled = Editor.CanUndo;
        if (_redo != null) _redo.IsEnabled = Editor.CanRedo;
        if (_toolbar?.Child is ScrollViewer { Content: StackPanel stack } && stack.Children[1] is WrapPanel tools)
            foreach (UIElement child in tools.Children)
                if (child is Button { Tag: EditorTool tool } button)
                    button.Background = Brush(Editor.Tool == tool ? "#7D4BE3" : "#090E13");
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
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!_closed) PlaceOverDesktop(); }));
    }

    private void PlaceOverDesktop()
    {
        if (_handle == IntPtr.Zero || _closed) return;
        if (!SetWindowPos(_handle, new IntPtr(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0040))
        {
            Failure = new Win32Exception(Marshal.GetLastWin32Error(), "Das Overlay konnte nicht über dem Desktop platziert werden.");
            Cancel();
            return;
        }
        var origin = new NativePoint();
        if (!GetClientRect(_handle, out NativeRect client) || !ClientToScreen(_handle, ref origin))
            Failure = new Win32Exception(Marshal.GetLastWin32Error(), "Die physischen Fenstergrenzen konnten nicht geprüft werden.");
        else if (origin.X != _bounds.X || origin.Y != _bounds.Y || client.Right - client.Left != _bounds.Width || client.Bottom - client.Top != _bounds.Height)
            Failure = new InvalidOperationException("Das Overlay deckt das Desktopraster nicht pixelgenau ab.");
        if (Failure != null) Cancel();
    }

    // An ancestry guard also works before a presentation source exists (offscreen route checks).
    internal bool IsControlSource(DependencyObject? source)
    {
        for (DependencyObject? node = source; node != null; node = ParentOf(node))
            if (node == _toolbar || node is TextBox) return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) => node is Visual || node is System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (IsControlSource(e.OriginalSource as DependencyObject)) return;
        if (!ReadCursor(out Point point)) return;
        if (!BeginFrameGesture(point, Keyboard.IsKeyDown(Key.Space))) return;
        e.Handled = true;
        Focus();
        if (!CaptureMouse()) Cancel();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_gesture != FrameGesture.None)
        {
            if (ReadCursor(out Point point)) UpdateFrameGesture(point);
            e.Handled = true;
        }
        else if (!IsControlSource(e.OriginalSource as DependencyObject) && ReadCursor(out Point point))
        {
            FrameHandle handle = HitFrame(point);
            Cursor = handle switch {
                FrameHandle.Left or FrameHandle.Right => Cursors.SizeWE,
                FrameHandle.Top or FrameHandle.Bottom => Cursors.SizeNS,
                FrameHandle.Left | FrameHandle.Top or FrameHandle.Right | FrameHandle.Bottom => Cursors.SizeNWSE,
                FrameHandle.Right | FrameHandle.Top or FrameHandle.Left | FrameHandle.Bottom => Cursors.SizeNESW,
                _ => Selection.IsEmpty ? Cursors.Cross : null };
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_gesture == FrameGesture.None) return;
        if (ReadCursor(out Point point)) UpdateFrameGesture(point);
        EndFrameGesture();
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (e.OriginalSource == this && _gesture != FrameGesture.None) EndFrameGesture(true);
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
        // The inline text box owns Space, Ctrl+C, Delete and local undo, including Esc to discard text.
        if (IsControlSource(e.OriginalSource as DependencyObject) && Editor.IsEditingText) return;
        if (Editor.IsEditingText) return;
        e.Handled = HandleSessionKey(e.Key, Keyboard.Modifiers);
    }

    internal bool HandleSessionKey(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (Editor.IsEditingText) return false;
        if (key == Key.Escape) { Cancel(); return true; }
        if (Selection.IsEmpty) return false;
        if ((key == Key.C && modifiers == ModifierKeys.Control) || (key == Key.Enter && modifiers == ModifierKeys.None))
            return Complete(CaptureCompletion.Copied);
        if (key == Key.S && modifiers == ModifierKeys.Control) return Complete(CaptureCompletion.Saved);
        if (Editor.HandleKey(key, modifiers)) return true;
        if (key == Key.V && modifiers == ModifierKeys.None) { Editor.Tool = EditorTool.Select; UpdateTools(); return true; }
        return false;
    }

    internal bool BeginFrameGesture(Point point, bool move = false)
    {
        if (_closed || _gesture != FrameGesture.None) return false;
        _before = Selection;
        _start = Clamp(point);
        _frameHandle = HitFrame(_start);
        if (Selection.IsEmpty) _gesture = FrameGesture.New;
        else if (move && _editing) _gesture = FrameGesture.Move;
        else if (_frameHandle != FrameHandle.None && _editing) _gesture = FrameGesture.Resize;
        else if (_editing && Editor.Tool == EditorTool.Select &&
            new Rect(Selection.X, Selection.Y, Selection.Width, Selection.Height).Contains(_start) && !Editor.HasAnnotationAt(_start))
            _gesture = FrameGesture.Move;
        else return false;
        Editor.EndGesture();
        Editor.CommitText();
        _toolbar.Visibility = Visibility.Collapsed;
        return true;
    }

    internal void UpdateFrameGesture(Point point)
    {
        if (_gesture == FrameGesture.None) return;
        point = Clamp(point);
        int dx = (int)Math.Round(point.X - _start.X), dy = (int)Math.Round(point.Y - _start.Y);
        if (_gesture == FrameGesture.New)
        {
            int x = (int)_start.X, y = (int)_start.Y, endX = (int)point.X, endY = (int)point.Y;
            SetRegion(new Int32Rect(Math.Min(x, endX), Math.Min(y, endY), Math.Abs(endX - x), Math.Abs(endY - y)));
        }
        else if (_gesture == FrameGesture.Move)
            SetRegion(new Int32Rect(Math.Clamp(_before.X + dx, 0, _bounds.Width - _before.Width),
                Math.Clamp(_before.Y + dy, 0, _bounds.Height - _before.Height), _before.Width, _before.Height));
        else
        {
            int l = _before.X, r = l + _before.Width, t = _before.Y, b = t + _before.Height;
            if (_frameHandle.HasFlag(FrameHandle.Left)) l = Math.Clamp(l + dx, 0, r - 2);
            if (_frameHandle.HasFlag(FrameHandle.Right)) r = Math.Clamp(r + dx, l + 2, _bounds.Width);
            if (_frameHandle.HasFlag(FrameHandle.Top)) t = Math.Clamp(t + dy, 0, b - 2);
            if (_frameHandle.HasFlag(FrameHandle.Bottom)) b = Math.Clamp(b + dy, t + 2, _bounds.Height);
            SetRegion(new Int32Rect(l, t, r - l, b - t));
        }
    }

    internal void EndFrameGesture(bool cancel = false)
    {
        if (_gesture == FrameGesture.None) return;
        _gesture = FrameGesture.None;
        if (cancel) SetRegion(_before);
        if (Selection.Width < 2 || Selection.Height < 2) SetRegion(Int32Rect.Empty);
        if (!_editing && !Selection.IsEmpty)
        {
            _rawAccepted = true;
            Close();
            return;
        }
        RefreshRegion();
    }

    private Point Clamp(Point point) => new(Math.Round(Math.Clamp(point.X, 0, _bounds.Width)), Math.Round(Math.Clamp(point.Y, 0, _bounds.Height)));

    internal void SetRegion(Int32Rect region)
    {
        if (region.X < 0 || region.Y < 0 || region.Width < 0 || region.Height < 0 ||
            (long)region.X + region.Width > _bounds.Width || (long)region.Y + region.Height > _bounds.Height)
            throw new ArgumentOutOfRangeException(nameof(region));
        Selection = region;
        RefreshRegion();
    }

    private void RefreshRegion()
    {
        bool ready = Selection.Width >= 2 && Selection.Height >= 2;
        Editor.Clip = new RectangleGeometry(ready ? new Rect(Selection.X, Selection.Y, Selection.Width, Selection.Height) : Rect.Empty);
        Editor.IsHitTestVisible = _editing && ready && _gesture == FrameGesture.None;
        _toolbar.Visibility = _editing && ready && _gesture == FrameGesture.None ? Visibility.Visible : Visibility.Collapsed;
        _label.Text = $"{Selection.Width} × {Selection.Height} px   ·   Leertaste + Ziehen: Bereich verschieben   ·   Esc: abbrechen";
        PositionToolbar();
        _chrome.InvalidateVisual();
    }

    internal Rect ToolbarBounds { get; private set; }
    private void PositionToolbar()
    {
        if (_toolbar.Visibility != Visibility.Visible) return;
        _toolbar.Measure(new Size(_toolbar.Width, _toolbar.MaxHeight));
        Size size = _toolbar.DesiredSize;
        double x = Math.Clamp(Selection.X, 0, Math.Max(0, _bounds.Width - size.Width));
        double y = Selection.Y + Selection.Height + 12;
        if (y + size.Height > _bounds.Height) y = Selection.Y - size.Height - 12;
        y = Math.Clamp(y, 0, Math.Max(0, _bounds.Height - size.Height));
        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
        ToolbarBounds = new Rect(x, y, size.Width, size.Height);
    }

    internal (FrameHandle Handle, Point Center)[] FrameHandles()
    {
        double l = Selection.X, t = Selection.Y, r = l + Selection.Width, b = t + Selection.Height;
        return new[] { (FrameHandle.Left | FrameHandle.Top, new Point(l,t)), (FrameHandle.Right | FrameHandle.Top,new Point(r,t)),
            (FrameHandle.Right | FrameHandle.Bottom,new Point(r,b)), (FrameHandle.Left | FrameHandle.Bottom,new Point(l,b)),
            (FrameHandle.Left,new Point(l,(t+b)/2)), (FrameHandle.Right,new Point(r,(t+b)/2)),
            (FrameHandle.Top,new Point((l+r)/2,t)), (FrameHandle.Bottom,new Point((l+r)/2,b)) };
    }

    private FrameHandle HitFrame(Point point)
    {
        if (Selection.IsEmpty) return FrameHandle.None;
        FrameHandle result = FrameHandle.None;
        double best = double.MaxValue;
        foreach (var handle in FrameHandles())
        {
            Vector d = point - handle.Center;
            if (Math.Abs(d.X) <= 7 && Math.Abs(d.Y) <= 7 && d.LengthSquared < best)
            { best = d.LengthSquared; result = handle.Handle; }
        }
        return result;
    }

    internal bool Complete(CaptureCompletion completion)
    {
        if (_closed || Selection.IsEmpty || completion == CaptureCompletion.Cancelled) return false;
        if (completion == CaptureCompletion.Copied)
            return ExecuteOutput(completion, image => { ImageOutput.Copy(image); return true; });
        if (completion == CaptureCompletion.Saved)
            return ExecuteOutput(completion, image => ImageOutput.Save(image, this) != null);
        Editor.EndGesture();
        Editor.CommitText();
        _scene.Children.Remove(Editor); // Detach before shell takes ownership; preserve objects and history.
        Editor.Clip = null;
        Result = new CaptureSessionResult(completion, Editor, Selection);
        Close();
        return true;
    }

    internal bool ExecuteOutput(CaptureCompletion completion, Func<BitmapSource, bool> write)
    {
        if (_closed || Selection.IsEmpty) return false;
        try
        {
            if (!write(Editor.Flatten(Selection))) return false;
            Result = new CaptureSessionResult(completion);
            Close();
            return true;
        }
        catch (Exception exception)
        {
            _error.Text = "Ausgabe fehlgeschlagen: " + exception.Message;
            _error.Visibility = Visibility.Visible;
            PositionToolbar();
            return false;
        }
    }

    internal void Cancel()
    {
        if (_closed) return;
        Result = new CaptureSessionResult(CaptureCompletion.Cancelled);
        Selection = Int32Rect.Empty;
        _gesture = FrameGesture.None;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        Editor.Changed -= OnEditorChanged;
        _gesture = FrameGesture.None;
        if (!_rawAccepted && Result.Completion == CaptureCompletion.Cancelled) Selection = Int32Rect.Empty;
        if (IsMouseCaptured) ReleaseMouseCapture();
        base.OnClosed(e);
    }

    private bool ReadCursor(out Point cursor)
    {
        if (!GetPhysicalCursorPos(out NativePoint point))
        {
            Failure = new Win32Exception(Marshal.GetLastWin32Error(), "Die Zeigerposition konnte nicht gelesen werden.");
            cursor = default;
            Cancel();
            return false;
        }
        cursor = new Point((long)point.X - _bounds.X, (long)point.Y - _bounds.Y);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    private sealed class FrameChrome(RegionOverlay overlay) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            var full = new Rect(0, 0, Width, Height);
            Int32Rect s = overlay.Selection;
            var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
            mask.Children.Add(new RectangleGeometry(full));
            if (s.Width > 0 && s.Height > 0) mask.Children.Add(new RectangleGeometry(new Rect(s.X, s.Y, s.Width, s.Height)));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 9, 14, 19)), null, mask);
            if (s.Width <= 0 || s.Height <= 0) return;
            dc.DrawRectangle(null, new Pen(Brush("#B097E9"), 1), new Rect(s.X + .5, s.Y + .5, Math.Max(0, s.Width - 1), Math.Max(0, s.Height - 1)));
            if (!overlay._editing || overlay._gesture != FrameGesture.None) return;
            foreach (var handle in overlay.FrameHandles())
                dc.DrawRectangle(Brush("#D3B576"), new Pen(Brush("#090E13"), 1), new Rect(handle.Center.X - 4, handle.Center.Y - 4, 8, 8));
        }
    }
}
