using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KairosCapture.Capture;
using KairosCapture.Services;
using Microsoft.Win32;
using DrawingRectangle = System.Drawing.Rectangle;

namespace KairosCapture;

internal sealed class OcrResultWindow : Window
{
    private readonly MainWindow _settings;
    private BitmapSource? _image;
    private byte[]? _png;
    private readonly Dictionary<string, string> _translations = new(StringComparer.Ordinal);
    private readonly ComboBox _translation;
    private readonly TextBlock _status;
    private readonly TextBox _text;
    private readonly ProgressBar _progress;
    private readonly Button _retry, _connect, _cancel, _copy, _save;
    private CancellationTokenSource? _operation;
    private bool _closed;
    internal bool HasImage => _image != null;
    internal TextBox ResultText => _text;
    internal string Status => _status.Text;
    internal ComboBox TranslationPicker => _translation;

    internal OcrResultWindow(MainWindow settings, BitmapSource image, DrawingRectangle bounds, Int32Rect region, bool diagnostics = false)
    {
        _settings = settings; _image = image;
        Title = "Kairos · Texterkennung"; Width = 440; Height = 350; MinWidth = 400; MinHeight = 290;
        Topmost = true; ShowInTaskbar = false; Background = new SolidColorBrush(Color.FromRgb(19, 21, 30));
        WindowStartupLocation = diagnostics ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        var root = new Border { Style = (Style)FindResource("GlassPanel"), Padding = new Thickness(18) };
        TextElement.SetForeground(root, (Brush)FindResource("TextBrush"));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        var title = new ContentControl { Content = GlassWindow.Caption(this, "Ocr", "Text aus deiner Aufnahme"), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextBrush") };
        header.Children.Add(title);
        var translationRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        translationRow.Children.Add(new TextBlock { Text = "Übersetzen", VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("LavenderBrush"), Margin = new Thickness(0, 0, 10, 0) });
        _translation = new ComboBox { ItemsSource = OpenAiOcr.Translations, DisplayMemberPath = "Label", SelectedIndex = 0,
            IsEditable = false, Background = (Brush)FindResource("GlassFill"), BorderBrush = (Brush)FindResource("GlassStroke"),
            Foreground = (Brush)FindResource("TextBrush"), ToolTip = "Erkannten Text in diese Sprache übersetzen" };
        translationRow.Children.Add(_translation); header.Children.Add(translationRow);
        _status = new TextBlock { Text = "Texterkennung wird vorbereitet…", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 9), FontSize = 12, Foreground = (Brush)FindResource("LavenderBrush") };
        header.Children.Add(_status); grid.Children.Add(header);
        _progress = new ProgressBar { Height = 3, Foreground = (Brush)FindResource("LavenderBrush"), IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(_progress, 1); grid.Children.Add(_progress);
        _text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsReadOnly = true, Padding = new Thickness(10), FontSize = 14, MinHeight = 70 };
        Grid.SetRow(_text, 2); grid.Children.Add(_text);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _connect = Add(actions, "Mit ChatGPT verbinden", "Account", async () =>
        {
            _settings.ShowFromTray(); await _settings.ConnectAsync();
            if (!_closed) { Activate(); await RecognizeAsync(); }
        });
        _retry = Add(actions, "Erneut versuchen", "Ocr", async () => await RecognizeAsync());
        _cancel = Add(actions, "Abbrechen", "Close", () => { _operation?.Cancel(); return Task.CompletedTask; });
        _copy = Add(actions, "Kopieren", "Copy", () => { CopyText(); return Task.CompletedTask; });
        _save = Add(actions, "Als .txt speichern", "Save", () => { SaveText(); return Task.CompletedTask; });
        Add(actions, "Schließen", "Close", () => { Close(); return Task.CompletedTask; });
        Grid.SetRow(actions, 3); grid.Children.Add(actions); root.Child = grid; Content = root;
        SetActions(false, false);
        _translation.SelectionChanged += async (_, _) =>
        {
            _operation?.Cancel();
            _operation = null;
            await RecognizeAsync();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        SourceInitialized += (_, _) =>
        {
            GlassWindow.Apply(this);
            if (!diagnostics) PlaceNear(bounds, region);
        };
        if (!diagnostics) ContentRendered += async (_, _) => await RecognizeAsync();
    }

    private Button Add(Panel panel, string text, string glyph, Func<Task> action)
    {
        var button = new Button { Content = GlassWindow.Caption(this, glyph, text), Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 5, 5) };
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { if (!_closed) ShowFailure(OpenAiDiagnostics.UserMessage(ex)); App.LogException("OCR result action", ex); } };
        panel.Children.Add(button); return button;
    }
    private void SetActions(bool running, bool result)
    {
        _cancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _retry.Visibility = !running && !result ? Visibility.Visible : Visibility.Collapsed;
        _connect.Visibility = !running && !result ? Visibility.Visible : Visibility.Collapsed;
        _copy.Visibility = _save.Visibility = result ? Visibility.Visible : Visibility.Collapsed;
        _progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _text.IsReadOnly = running || !result;
    }
    internal async Task RecognizeAsync()
    {
        if (_closed || _operation != null || _image == null) return;
        string? language = (_translation.SelectedItem as OpenAiTranslation)?.Language;
        string cacheKey = language ?? "";
        if (_translations.TryGetValue(cacheKey, out string? cached)) { ShowResult(cached, language); return; }
        using var operation = new CancellationTokenSource(); _operation = operation;
        bool Current() => !_closed && ReferenceEquals(_operation, operation) && !operation.IsCancellationRequested;
        SetActions(true, false); _text.Clear(); _status.Text = language is null ? "Text wird erkannt…" : "Text wird erkannt und übersetzt…";
        try
        {
            await _settings.EnsureOpenAiLoadedAsync(operation.Token);
            if (!Current()) return;
            if (_settings.OpenAi.ActiveAccount?.IsSignedIn != true)
            { ShowFailure("Mit ChatGPT verbinden, dann erneut versuchen. Deine Aufnahme bleibt hier erhalten."); return; }
            var models = await _settings.OpenAi.GetModelsAsync(operation.Token);
            if (!Current()) return;
            var model = OpenAiService.SelectOcrModel(models, _settings.Settings.Model);
            if (model == null) { ShowFailure("Kein Modell verfügbar. Konto und Freigaben in den Einstellungen prüfen, dann erneut versuchen."); return; }
            _settings.Settings.Model = model.Slug; _settings.SaveSettings();
            _png ??= ImageOutput.EncodePng(_image);
            var progress = new Progress<string>(part => { if (Current()) _text.AppendText(part); });
            string text = await _settings.OpenAi.ExtractTextAsync(_png, model.Slug, progress, operation.Token, language);
            if (!Current()) return;
            _translations[cacheKey] = text;
            ShowResult(text, language);
        }
        catch (OperationCanceledException) { if (!_closed && ReferenceEquals(_operation, operation)) ShowFailure("Texterkennung abgebrochen. Deine Aufnahme bleibt für einen neuen Versuch erhalten."); }
        catch (Exception ex)
        {
            if (Current()) ShowFailure(OpenAiDiagnostics.UserMessage(ex));
            App.LogException("OCR", ex);
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
                if (!_closed) _progress.Visibility = _cancel.Visibility = Visibility.Collapsed;
            }
        }
    }
    private void ShowResult(string text, string? language)
    {
        _text.Text = text; SetActions(false, true);
        string result = language is null ? "Erkannt" : "Übersetzt";
        _status.Text = text.Length == 0 ? "Kein Text gefunden. Du kannst den Text hier selbst ergänzen." : result + " · du kannst den Text direkt bearbeiten.";
        if (_settings.Settings.AutoCopyOcr && text.Length > 0)
        {
            try { Clipboard.SetText(text); _status.Text = result + " · automatisch in die Zwischenablage kopiert."; }
            catch (ExternalException) { _status.Text = result + " · Zwischenablage belegt. Bitte „Kopieren“ erneut wählen."; }
        }
    }
    private void ShowFailure(string message) { _status.Text = message; SetActions(false, false); }
    private void CopyText()
    {
        if (_text.Text.Length == 0) return;
        Clipboard.SetText(_text.Text); _status.Text = "Text kopiert.";
    }
    private void SaveText()
    {
        var dialog = new SaveFileDialog { Title = "Erkannten Text speichern", Filter = "Textdatei (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true,
            FileName = $"Kairos {DateTime.Now:yyyy-MM-dd HHmmss}.txt" };
        if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, _text.Text); _status.Text = "Text gespeichert."; }
    }
    internal void SetPreviewResult()
    {
        _text.Text = "Klarheit im nächsten Ausschnitt\n\nEine gute Idee beginnt mit dem, was du siehst.\n\nFesthalten. Markieren. Weitergeben.";
        _status.Text = "Erkannt · automatisch in die Zwischenablage kopiert."; SetActions(false, true);
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true; _operation?.Cancel(); _image = null; _png = null; _translations.Clear(); base.OnClosed(e);
    }
    internal static DrawingRectangle ResultBounds(DrawingRectangle desktop, Int32Rect region, DrawingRectangle work, double scale)
    {
        int width = Math.Min(work.Width, (int)Math.Ceiling(440 * scale));
        int height = Math.Min(work.Height, (int)Math.Ceiling(350 * scale));
        int x = Math.Clamp(desktop.X + region.X + region.Width + 12, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(desktop.Y + region.Y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new DrawingRectangle(x, y, width, height);
    }
    private void PlaceNear(DrawingRectangle bounds, Int32Rect region)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int anchorX = bounds.X + region.X + region.Width, anchorY = bounds.Y + region.Y;
        var work = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(anchorX - 1, anchorY)).WorkingArea;
        // Move the HWND onto the destination monitor before asking for its DPI.
        // The posted placement runs after WPF's synchronous WM_DPICHANGED handling.
        SetWindowPos(handle, IntPtr.Zero, work.Left, work.Top, 0, 0, 0x0015);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closed) return;
            uint dpi = GetDpiForWindow(handle);
            DrawingRectangle target = ResultBounds(bounds, region, work, (dpi == 0 ? 96 : dpi) / 96d);
            SetWindowPos(handle, new IntPtr(-1), target.X, target.Y, target.Width, target.Height, 0x0010);
        }));
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
