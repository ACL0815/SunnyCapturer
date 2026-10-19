using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Controls.Primitives;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KairosCapture.Editor;
using DrawingRectangle = System.Drawing.Rectangle;

namespace KairosCapture.Capture;

/// <summary>One physical desktop raster, one live object editor, and a movable export crop.</summary>
internal sealed class RegionOverlay : Window
{
    [Flags]
    internal enum FrameHandle { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
    private enum FrameGesture { None, New, Move, Resize }
    private enum GestureEnd { None, Released, ReleasedMove, MouseUp, Deactivated, CaptureLost, Escape }
    private enum FrameChromeState { Closed, Raw, Dragging, Empty, Ready }
    private enum OverlayEvent { Created, Activated, Deactivated, ContentRendered, GestureBegan, GestureEnded, GestureCancelled,
        ReleasedMove, CaptureRequested, CaptureGained, CaptureLost, RegionChanged, ToolbarMeasured, PointerUpIdle, SecondPress, CursorReadFailed, Closed }
    private enum InputSource { None, Overlay, Editor, Control, Other }
    private static int _nextTraceId;
    private readonly int _traceId = Interlocked.Increment(ref _nextTraceId);
    private readonly Action<string> _logState;
    private int _traceLines, _traceDropped;
    private readonly DrawingRectangle _bounds;
    private readonly bool _editing;
    private readonly BitmapSource _screenshot;
    private readonly AppSettings _settings;
    private readonly bool _ocrMode;
    private readonly Func<BitmapSource, Window, AppSettings, string?> _saveImage;
    private readonly Action<BitmapSource> _copyImage;
    private readonly Image _glassImage;
    private readonly Border _chip;
    private readonly List<RadioButton> _toolButtons = new();
    private string? _savedPath;
    private readonly Canvas _scene;
    private readonly FrameChrome _chrome;
    private readonly Border _toolbar;
    private readonly TextBlock _label, _error;
    private readonly Button _undo, _redo;
    private IntPtr _handle;
    private bool _closed, _rawAccepted, _outputPending;
    private FrameGesture _gesture;
    private FrameHandle _frameHandle;
    private Point _start, _lastPoint;
    private GestureEnd _lastGestureEnd;
    private bool _pointerPromoted;
    private Int32Rect _before;

    internal AnnotationCanvas Editor { get; }
    internal Int32Rect Selection { get; private set; }
    internal Exception? Failure { get; private set; }
    internal CaptureSessionResult Result { get; private set; } = new(CaptureCompletion.Cancelled);
    internal bool IsClosed => _closed;
    internal bool IsFrameGestureActive => _gesture != FrameGesture.None;
    internal bool ToolbarVisible => _toolbar.Visibility == Visibility.Visible;
    internal FrameworkElement PreviewRoot => _scene;
    internal FrameworkElement Toolbar => _toolbar;
    private FrameChromeState ChromeState => _closed ? FrameChromeState.Closed : !_editing ? FrameChromeState.Raw :
        _gesture != FrameGesture.None ? FrameChromeState.Dragging :
        Selection.Width < 2 || Selection.Height < 2 ? FrameChromeState.Empty : FrameChromeState.Ready;

    internal RegionOverlay(BitmapSource screenshot, DrawingRectangle bounds, bool editing = true,
        AppSettings? settings = null, bool ocrMode = false, AnnotationCanvas? editor = null,
        Func<BitmapSource, Window, AppSettings, string?>? saveImage = null, Action<BitmapSource>? copyImage = null,
        Action<string>? logState = null)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        CaptureGeometry.ValidateSize(bounds.Width, bounds.Height);
        if (screenshot.PixelWidth != bounds.Width || screenshot.PixelHeight != bounds.Height)
            throw new ArgumentException("Bildraster und Desktopgrenzen müssen übereinstimmen.", nameof(screenshot));
        _bounds = bounds;
        _editing = editing;
        _screenshot = screenshot;
        _settings = settings ?? new();
        _ocrMode = ocrMode;
        _saveImage = saveImage ?? ((image, parent, preferences) => ImageOutput.Save(image, parent, preferences));
        _copyImage = copyImage ?? ImageOutput.Copy;
        _logState = logState ?? App.LogOverlayDiagnostic;
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
        Editor = editor ?? new AnnotationCanvas(screenshot);
        if (Editor.Parent != null || VisualTreeHelper.GetParent(Editor) != null) throw new InvalidOperationException("Der Editor gehört noch zu einem anderen Fenster.");
        Editor.SetEditingBounds(new Int32Rect(0, 0, bounds.Width, bounds.Height));
        Editor.IsHitTestVisible = false;
        Editor.Clip = new RectangleGeometry(Rect.Empty);
        _scene.Children.Add(Editor);
        _chrome = new FrameChrome(this) { Width = bounds.Width, Height = bounds.Height, IsHitTestVisible = false };
        _scene.Children.Add(_chrome);
        _label = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        _chip = new Border { Style = (Style)FindResource("GlassPanel"), Padding = new Thickness(10, 5, 10, 5),
            Child = _label, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _scene.Children.Add(_chip);
        _error = new TextBlock { Margin = new Thickness(8, 4, 8, 2), TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#FFE0A3"), Visibility = Visibility.Collapsed };
        var stack = new StackPanel();
        var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        (EditorTool Tool, string Label, string Key)[] choices = {
            (EditorTool.Select, "Auswahl", "V"), (EditorTool.Rectangle, "Rechteck", "R"), (EditorTool.Ellipse, "Ellipse", "E"),
            (EditorTool.Arrow, "Pfeil", "A"), (EditorTool.Pen, "Stift", "P"), (EditorTool.Text, "Text", "T"),
            (EditorTool.Highlight, "Markieren", "H"), (EditorTool.Redact, "Schwärzen", "B") };
        Style toolStyle = (Style)FindResource("GlassToolToggle");
        // The parallel resource writer may still supply the initial ToggleButton/RadioButton placeholder.
        if (toolStyle.BasedOn != null && !toolStyle.BasedOn.TargetType.IsAssignableFrom(toolStyle.TargetType)) toolStyle = toolStyle.BasedOn;
        foreach (var choice in choices)
        {
            var button = new RadioButton { Style = toolStyle,
                Content = GlassWindow.Icon(this, choice.Tool.ToString()), ToolTip = choice.Label + " · " + choice.Key,
                Tag = choice.Tool, GroupName = "OverlayTools", Focusable = false, Width = 34, Height = 34,
                Margin = new Thickness(1), Padding = new Thickness(5) };
            System.Windows.Automation.AutomationProperties.SetName(button, choice.Label);
            button.Click += (_, _) => { if (_closed || _outputPending) return; Editor.Tool = choice.Tool; UpdateTools(); Editor.Focus(); };
            _toolButtons.Add(button); tools.Children.Add(button);
        }
        AddDivider(tools);
        foreach (string colour in new[] { "#7D4BE3", "#B097E9", "#D3B576", "#FFFFFF", "#EF6461", "#000000" })
        {
            var swatch = new RadioButton { Style = (Style)FindResource("GlassSwatch"), Background = Brush(colour),
                GroupName = "OverlayColours", ToolTip = "Farbe " + colour + " · ausgewähltes Objekt ändern",
                Width = 24, Height = 30, Margin = new Thickness(0, 3, 0, 3), Focusable = false,
                IsChecked = colour == "#B097E9" };
            swatch.Click += (_, _) => { if (!_closed && !_outputPending) Editor.Stroke = Brush(colour); };
            tools.Children.Add(swatch);
        }
        var thicknessPicker = new ComboBox { Width = 58, Height = 32, MinHeight = 32, Margin = new Thickness(5, 2, 2, 2),
            ToolTip = "Strichdicke · ausgewähltes Objekt ändern", SelectedValuePath = "Tag" };
        foreach (double thickness in new[] { 1d, 3, 6, 12, 24 })
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = Math.Min(18, thickness + 2), Height = Math.Min(18, thickness + 2),
                Fill = Foreground, HorizontalAlignment = HorizontalAlignment.Center };
            thicknessPicker.Items.Add(new ComboBoxItem { Content = dot, Tag = thickness, ToolTip = thickness + " px", MinHeight = 26 });
        }
        thicknessPicker.SelectedValue = Editor.Thickness;
        thicknessPicker.SelectionChanged += (_, _) => { if (!_closed && !_outputPending && thicknessPicker.SelectedValue is double value) Editor.Thickness = value; };
        tools.Children.Add(thicknessPicker);
        AddDivider(tools);
        _undo = AddButton(tools, "Undo", "Rückgängig · Strg+Z", Editor.Undo);
        _redo = AddButton(tools, "Redo", "Wiederholen · Strg+Y", Editor.Redo);
        AddButton(tools, "Delete", "Löschen · Entf", Editor.DeleteSelected);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        AddButton(actions, "Ocr", "Text erkennen · O", () => _ = CompleteAsync(CaptureCompletion.Ocr));
        AddDivider(actions);
        AddButton(actions, "Close", "Abbrechen · Esc", Cancel);
        AddButton(actions, "Pin", "Anpinnen · I", () => _ = CompleteAsync(CaptureCompletion.Pinned));
        Button save = AddButton(actions, "Download", "Speichern · Strg+S", () => _ = CompleteAsync(CaptureCompletion.Saved));
        save.Tag = "Save";
        Button check = AddButton(actions, "Check", "Fertig · Bild kopieren und schließen · Enter / Strg+C", () => _ = CompleteAsync(CaptureCompletion.Copied));
        check.Style = (Style)FindResource("GlassConfirmButton");
        var toolbarRow = new Grid();
        toolbarRow.ColumnDefinitions.Add(new ColumnDefinition());
        toolbarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbarRow.Children.Add(tools);
        Grid.SetColumn(actions, 1); toolbarRow.Children.Add(actions);
        stack.Children.Add(toolbarRow); stack.Children.Add(_error);
        var grid = new Grid { ClipToBounds = true };
        _glassImage = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false,
            Effect = new BlurEffect { Radius = 18, RenderingBias = RenderingBias.Performance }, CacheMode = new BitmapCache(1) };
        // The backdrop must never contribute its source size to the toolbar's automatic height.
        var backdrop = new Canvas { IsHitTestVisible = false };
        backdrop.Children.Add(_glassImage);
        grid.Children.Add(backdrop);
        var glass = new Border { Style = (Style)FindResource("GlassPanel"), Child = stack, Padding = new Thickness(8) };
        grid.Children.Add(glass);
        _toolbar = new Border { Child = grid, CornerRadius = new CornerRadius(24),
            Width = Math.Min(1020, Math.Max(1, bounds.Width - 24)), MaxHeight = Math.Max(1, bounds.Height - 16),
            Visibility = Visibility.Collapsed };
        _scene.Children.Add(_toolbar);
        _toolbar.SizeChanged += (_, _) => TraceState(OverlayEvent.ToolbarMeasured);
        RenderOptions.SetBitmapScalingMode(_scene, BitmapScalingMode.NearestNeighbor);
        Content = new Viewbox { Stretch = Stretch.Fill, Child = _scene };
        Editor.Changed += OnEditorChanged;
        UpdateTools();
        TraceState(OverlayEvent.Created);
    }

    private void OnEditorChanged(object? sender, EventArgs e) => UpdateTools();

    private static SolidColorBrush Brush(string colour) => new((Color)ColorConverter.ConvertFromString(colour));

    private void AddDivider(Panel panel) => panel.Children.Add(new Border { Width = 1, Height = 22,
        Background = (Brush)FindResource("GlassStroke"), Margin = new Thickness(7, 7, 7, 7) });

    private Button AddButton(Panel panel, string glyph, string tooltip, Action click)
    {
        var button = new Button { Style = (Style)FindResource("GlassIconButton"), Content = GlassWindow.Icon(this, glyph),
            ToolTip = tooltip, Tag = glyph, Margin = new Thickness(1), Width = 34, Height = 34,
            Padding = new Thickness(5), Focusable = false };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => { if (!_closed && !_outputPending) click(); };
        panel.Children.Add(button);
        return button;
    }

    private void UpdateTools()
    {
        if (_undo != null) _undo.IsEnabled = Editor.CanUndo;
        if (_redo != null) _redo.IsEnabled = Editor.CanRedo;
        foreach (var button in _toolButtons) button.IsChecked = Equals(button.Tag, Editor.Tool);
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
        bool activated = Activate();
        Focus();
        TraceState(OverlayEvent.ContentRendered, data: Convert.ToInt32(activated));
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        TraceState(OverlayEvent.Activated);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        TraceState(OverlayEvent.Deactivated);
        FinishFrameGesture(GestureEnd.Deactivated);
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
        if (_outputPending) { e.Handled = true; return; }
        if (IsControlSource(e.OriginalSource as DependencyObject)) return;
        if (!ReadCursor(out Point point)) return;
        _pointerPromoted = e.StylusDevice != null;
        if (!HandlePointerDown(point, e.ClickCount, Keyboard.IsKeyDown(Key.Space))) return;
        e.Handled = true;
        if (_gesture == FrameGesture.None) return;
        Focus();
        bool captured = CaptureMouse();
        TraceState(OverlayEvent.CaptureRequested, data: Convert.ToInt32(captured));
        if (!captured) Cancel();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_gesture != FrameGesture.None)
        {
            if (ReadCursor(out Point point)) HandlePointerMove(point, e.LeftButton);
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
        if (_gesture == FrameGesture.None) { TraceState(OverlayEvent.PointerUpIdle, e.OriginalSource as DependencyObject); return; }
        if (ReadCursor(out Point point)) FinishFrameGesture(GestureEnd.MouseUp, point);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        TraceState(OverlayEvent.CaptureLost, e.OriginalSource as DependencyObject);
        if (e.OriginalSource == this) FinishFrameGesture(GestureEnd.CaptureLost);
    }

    protected override void OnGotMouseCapture(MouseEventArgs e)
    {
        base.OnGotMouseCapture(e);
        TraceState(OverlayEvent.CaptureGained, e.OriginalSource as DependencyObject);
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonDown(e);
        e.Handled = true;
        if (!_outputPending) Cancel();
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
        if (_closed || _outputPending) return true;
        if (Editor.IsEditingText) return false;
        if (key == Key.Escape)
        {
            if (!_editing) Cancel();
            else if (_gesture != FrameGesture.None) FinishFrameGesture(GestureEnd.Escape);
            else if (!Editor.HandleKey(Key.Escape)) Cancel();
            return true;
        }
        if (Selection.IsEmpty) return false;
        if ((key == Key.C && modifiers == ModifierKeys.Control) || (key == Key.Enter && modifiers == ModifierKeys.None))
        { _ = CompleteAsync(CaptureCompletion.Copied); return true; }
        if (key == Key.S && modifiers == ModifierKeys.Control) { _ = CompleteAsync(CaptureCompletion.Saved); return true; }
        if (Editor.HandleKey(key, modifiers)) return true;
        if (key == Key.O && modifiers == ModifierKeys.None) { _ = CompleteAsync(CaptureCompletion.Ocr); return true; }
        // P belongs to Stift; I (pInnen) is the free pin shortcut.
        if (key == Key.I && modifiers == ModifierKeys.None) { _ = CompleteAsync(CaptureCompletion.Pinned); return true; }
        if (modifiers == ModifierKeys.None)
        {
            EditorTool? tool = key switch { Key.V => EditorTool.Select, Key.R => EditorTool.Rectangle, Key.E => EditorTool.Ellipse,
                Key.A => EditorTool.Arrow, Key.P => EditorTool.Pen, Key.T => EditorTool.Text, Key.H => EditorTool.Highlight, Key.B => EditorTool.Redact, _ => null };
            if (tool != null) { Editor.Tool = tool.Value; UpdateTools(); return true; }
        }
        return false;
    }

    // Shared by routed pointer input and offline checks; completion uses the same path as Enter/Check.
    internal bool HandlePointerDown(Point point, int clickCount = 1, bool move = false)
    {
        if (_closed || _outputPending) return true;
        if (_gesture != FrameGesture.None) TraceState(OverlayEvent.SecondPress);
        // Only a double-click on plain selection content finishes; handles, Space-move and empty space keep their single-click gestures.
        if (clickCount >= 2 && !move && _editing && Selection.Width >= 2 && Selection.Height >= 2 &&
            new Rect(Selection.X, Selection.Y, Selection.Width, Selection.Height).Contains(point) &&
            HitFrame(Clamp(point)) == FrameHandle.None && !Editor.HasAnnotationAt(point))
        {
            _ = CompleteAsync(CaptureCompletion.Copied);
            return true;
        }
        return BeginFrameGesture(point, move);
    }

    internal bool BeginFrameGesture(Point point, bool move = false)
    {
        if (_closed || _outputPending || _gesture != FrameGesture.None) return false;
        _before = Selection;
        _start = Clamp(point);
        _frameHandle = HitFrame(_start);
        if (Selection.IsEmpty) _gesture = FrameGesture.New;
        else if (move && _editing) _gesture = FrameGesture.Move;
        else if (_frameHandle != FrameHandle.None && _editing && !Editor.HasAnnotationAt(_start)) _gesture = FrameGesture.Resize;
        else if (_editing && Editor.Tool == EditorTool.Select &&
            new Rect(Selection.X, Selection.Y, Selection.Width, Selection.Height).Contains(_start) && !Editor.HasAnnotationAt(_start))
            _gesture = FrameGesture.Move;
        else return false;
        _lastPoint = _start;
        _lastGestureEnd = GestureEnd.None;
        Editor.EndGesture();
        Editor.CommitText();
        RefreshRegion(OverlayEvent.GestureBegan);
        return true;
    }

    internal void UpdateFrameGesture(Point point)
    {
        if (_gesture == FrameGesture.None) return;
        point = _lastPoint = Clamp(point);
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

    internal void HandlePointerMove(Point point, MouseButtonState leftButton)
    {
        if (leftButton == MouseButtonState.Pressed) UpdateFrameGesture(point);
        else FinishFrameGesture(GestureEnd.ReleasedMove, point);
    }

    internal void EndFrameGesture(bool cancel = false) =>
        FinishFrameGesture(cancel ? GestureEnd.Escape : GestureEnd.Released);

    private void FinishFrameGesture(GestureEnd reason, Point? position = null)
    {
        if (_gesture == FrameGesture.None) return;
        bool cancel = reason is GestureEnd.Escape or GestureEnd.Deactivated or GestureEnd.CaptureLost;
        if (!cancel && position is Point point) UpdateFrameGesture(point);
        _lastGestureEnd = reason;
        if (reason == GestureEnd.ReleasedMove) TraceState(OverlayEvent.ReleasedMove);
        _gesture = FrameGesture.None;
        if (cancel) Selection = _before;
        if (Selection.Width < 2 || Selection.Height < 2) Selection = Int32Rect.Empty;
        RefreshRegion(cancel ? OverlayEvent.GestureCancelled : OverlayEvent.GestureEnded);
        // ReleaseMouseCapture synchronously raises capture loss and can synthesize a move.
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!_editing && !cancel && !Selection.IsEmpty)
        {
            _rawAccepted = true;
            Close();
            return;
        }
        if (_ocrMode && !cancel && !Selection.IsEmpty) _ = CompleteAsync(CaptureCompletion.Ocr);
    }

    private Point Clamp(Point point) => new(Math.Round(Math.Clamp(point.X, 0, _bounds.Width)), Math.Round(Math.Clamp(point.Y, 0, _bounds.Height)));

    internal void SetRegion(Int32Rect region)
    {
        if (_closed || _outputPending) return;
        if (region.X < 0 || region.Y < 0 || region.Width < 0 || region.Height < 0 ||
            (long)region.X + region.Width > _bounds.Width || (long)region.Y + region.Height > _bounds.Height)
            throw new ArgumentOutOfRangeException(nameof(region));
        Selection = region;
        RefreshRegion();
    }

    private void RefreshRegion(OverlayEvent reason = OverlayEvent.RegionChanged)
    {
        Visibility toolbarBefore = _toolbar.Visibility, chipBefore = _chip.Visibility;
        bool ready = Selection.Width >= 2 && Selection.Height >= 2;
        Editor.Clip = new RectangleGeometry(ready ? new Rect(Selection.X, Selection.Y, Selection.Width, Selection.Height) : Rect.Empty);
        Editor.IsHitTestVisible = ChromeState == FrameChromeState.Ready;
        _toolbar.Visibility = ChromeState == FrameChromeState.Ready ? Visibility.Visible : Visibility.Collapsed;
        _label.Text = $"{Selection.Width} × {Selection.Height}";
        _chip.Visibility = ChromeState == FrameChromeState.Ready ? Visibility.Visible : Visibility.Collapsed;
        PositionToolbar();
        _chrome.InvalidateVisual();
        if (toolbarBefore != _toolbar.Visibility || chipBefore != _chip.Visibility || reason != OverlayEvent.RegionChanged)
            TraceState(reason);
    }

    internal Rect ToolbarBounds { get; private set; }
    private void PositionToolbar()
    {
        if (_toolbar.Visibility != Visibility.Visible) return;
        _toolbar.Measure(new Size(_toolbar.Width, _toolbar.MaxHeight));
        Size size = _toolbar.DesiredSize;
        _glassImage.Width = size.Width;
        _glassImage.Height = size.Height;
        double x = Math.Clamp(Selection.X, 0, Math.Max(0, _bounds.Width - size.Width));
        double y = Selection.Y + Selection.Height + 12;
        if (y + size.Height > _bounds.Height) y = Selection.Y - size.Height - 12;
        y = Math.Clamp(y, 0, Math.Max(0, _bounds.Height - size.Height));
        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
        ToolbarBounds = new Rect(x, y, size.Width, size.Height);
        _toolbar.Child.Clip = new RectangleGeometry(new Rect(0, 0, size.Width, size.Height), 24, 24);
        int left = (int)x, top = (int)y;
        int width = Math.Min((int)Math.Ceiling(size.Width), _bounds.Width - left);
        int height = Math.Min((int)Math.Ceiling(size.Height), _bounds.Height - top);
        if (width > 0 && height > 0)
        {
            var crop = new CroppedBitmap(_screenshot, new Int32Rect(left, top, width, height)); crop.Freeze();
            _glassImage.Source = crop;
        }
        _chip.Measure(new Size(_bounds.Width, _bounds.Height));
        Canvas.SetLeft(_chip, Math.Clamp(Selection.X + 10, 0, Math.Max(0, _bounds.Width - _chip.DesiredSize.Width)));
        Canvas.SetTop(_chip, Math.Clamp(Selection.Y - _chip.DesiredSize.Height - 10, 0, Math.Max(0, _bounds.Height - _chip.DesiredSize.Height)));
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

    internal Task<bool> CompleteAsync(CaptureCompletion completion)
    {
        if (completion == CaptureCompletion.Copied)
            return Task.FromResult(ExecuteOutput(completion, image =>
            {
                // SaveOnCopy remains a direct file drop, independent of the explicit Save preference.
                if (_settings.SaveOnCopy)
                {
                    _savedPath = ImageOutput.SaveDirect(image, _settings.SaveFolder, DateTime.Now);
                }
                _copyImage(image); return true;
            }));
        if (completion == CaptureCompletion.Saved)
            return Task.FromResult(ExecuteOutput(completion, image => (_savedPath = _saveImage(image, this, _settings)) != null));
        return Task.FromResult(completion is CaptureCompletion.Ocr or CaptureCompletion.Pinned && ExecuteOutput(completion, _ => true));
    }

    internal bool ExecuteOutput(CaptureCompletion completion, Func<BitmapSource, bool> write)
    {
        if (_closed || _outputPending || Selection.IsEmpty || completion == CaptureCompletion.Cancelled) return false;
        _outputPending = true;
        try
        {
            Editor.EndGesture(); Editor.CommitText();
            if (Editor.IsMouseCaptured) Editor.ReleaseMouseCapture();
            BitmapSource image = Editor.Flatten(Selection);
            if (!write(image)) return false;
            _scene.Children.Remove(Editor);
            Editor.Clip = null;
            Result = new CaptureSessionResult(completion, Editor, Selection)
                { Screenshot = _screenshot, Bounds = _bounds, Image = completion is CaptureCompletion.Ocr or CaptureCompletion.Pinned ? image : null, SavedPath = _savedPath };
            Close();
            return true;
        }
        catch (Exception exception) { ShowOutputError(exception); return false; }
        finally { _outputPending = false; }
    }

    private void ShowOutputError(Exception exception)
    {
        if (_closed) return;
        _error.Text = "Ausgabe fehlgeschlagen: " + exception.Message;
        _error.Visibility = Visibility.Visible;
        PositionToolbar();
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
        Editor.EndGesture(); Editor.CommitText();
        if (Editor.IsMouseCaptured) Editor.ReleaseMouseCapture();
        _scene.Children.Remove(Editor);
        Editor.Clip = null;
        _gesture = FrameGesture.None;
        if (!_rawAccepted && Result.Completion == CaptureCompletion.Cancelled) Selection = Int32Rect.Empty;
        if (IsMouseCaptured) ReleaseMouseCapture();
        TraceState(OverlayEvent.Closed);
        base.OnClosed(e);
    }

    private void TraceState(OverlayEvent reason, DependencyObject? source = null, int data = 0)
    {
        if (_traceLines >= 20 || (_traceLines >= 19 && reason != OverlayEvent.Closed)) { _traceDropped++; return; }
        _traceLines++;
        InputSource origin = source == null ? InputSource.None : source == this ? InputSource.Overlay :
            source == Editor ? InputSource.Editor : IsControlSource(source) ? InputSource.Control : InputSource.Other;
        _logState(string.Create(CultureInfo.InvariantCulture,
            $"id={_traceId} event={reason} source={origin} gesture={_gesture} selection={Selection.X},{Selection.Y},{Selection.Width},{Selection.Height} " +
            $"toolbar={_toolbar.Visibility} chip={_chip.Visibility} chrome={ChromeState} handles={(ChromeState == FrameChromeState.Ready ? 8 : 0)} " +
            $"end={_lastGestureEnd} point={_lastPoint.X},{_lastPoint.Y} promoted={Convert.ToInt32(_pointerPromoted)} " +
            $"bounds={ToolbarBounds.X},{ToolbarBounds.Y},{ToolbarBounds.Width},{ToolbarBounds.Height} " +
            $"chipBounds={Canvas.GetLeft(_chip)},{Canvas.GetTop(_chip)},{_chip.ActualWidth},{_chip.ActualHeight} " +
            $"actual={_toolbar.ActualWidth},{_toolbar.ActualHeight} active={Convert.ToInt32(IsActive)} capture={Convert.ToInt32(IsMouseCaptured)} " +
            $"editorGesture={Convert.ToInt32(Editor.HasActiveGesture)} editing={Convert.ToInt32(_editing)} ocr={Convert.ToInt32(_ocrMode)} dropped={_traceDropped} data={data}"));
    }

    private bool ReadCursor(out Point cursor)
    {
        if (!GetPhysicalCursorPos(out NativePoint point))
        {
            int error = Marshal.GetLastWin32Error();
            TraceState(OverlayEvent.CursorReadFailed, data: error);
            Failure = new Win32Exception(error, "Die Zeigerposition konnte nicht gelesen werden.");
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
            if (overlay.ChromeState != FrameChromeState.Ready) return;
            foreach (var handle in overlay.FrameHandles())
                dc.DrawRectangle(Brush("#D3B576"), new Pen(Brush("#090E13"), 1), new Rect(handle.Center.X - 4, handle.Center.Y - 4, 8, 8));
        }
    }
}
