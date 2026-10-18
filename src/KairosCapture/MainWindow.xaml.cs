using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using KairosCapture.Capture;
using KairosCapture.Editor;
using KairosCapture.Services;

namespace KairosCapture;

public partial class MainWindow : Window
{
    private readonly OpenAiService _openAi;
    private readonly bool _diagnosticsMode;
    private readonly bool _ocrUnavailable;
    private AnnotationCanvas? _editor;
    private GlobalHotkey? _hotkey;
    private EditorTool _tool = EditorTool.Select;
    private Brush _stroke = new SolidColorBrush(Color.FromRgb(125, 75, 227));
    private double _thickness = 4;
    private bool _fit = true;
    private bool _settingZoom;
    private bool _refreshingAccounts;
    private bool _replacing;
    private bool _closing;
    private bool _disposed;
    private string _imageName = "Noch kein Bild";
    private byte[]? _savedHash;
    private int _revision;
    private int _savedRevision;
    private int _imageVersion;
    private CancellationTokenSource? _ocrCts;
    private Task? _ocrTask;
    private CancellationTokenSource? _accountCts;

    public MainWindow(bool diagnosticsMode = false)
    {
        _diagnosticsMode = diagnosticsMode;
        _ocrUnavailable = false;
        try { _openAi = diagnosticsMode ? AppDiagnostics.CreateIsolatedService() : new OpenAiService(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            // A damaged account store must not prevent local capture/edit/export, or overwrite that store.
            _openAi = AppDiagnostics.CreateIsolatedService();
            _ocrUnavailable = true;
            App.LogException("OpenAI initialization", ex);
        }
        InitializeComponent();
        RefreshAccounts();
        UpdateControls();
        if (_ocrUnavailable && !diagnosticsMode)
            SetStatus("Die gespeicherten OpenAI-Kontodaten sind nicht lesbar. Bildbearbeitung bleibt verfügbar; die Kontodatei wurde nicht verändert.", true);
        SourceInitialized += OnSourceInitialized;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) ApplyZoom(); }));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        int dark = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        if (_diagnosticsMode) return;
        _hotkey = new GlobalHotkey(this, () => _ = CaptureAsync());
        _hotkey.RegistrationFailed += OnHotkeyRegistrationFailed;
        if (!_hotkey.Register(out string? error))
            OnHotkeyRegistrationFailed(error ?? "Strg+Alt+A ist nicht verfügbar.");
    }

    private void OnHotkeyRegistrationFailed(string error)
    {
        HotkeyText.Text = "Hotkey nicht verfügbar · Aufnahme per Button";
        HotkeyText.ToolTip = error;
        SetStatus("Strg+Alt+A konnte nicht registriert werden. Die Aufnahme per Button funktioniert weiterhin.", true);
    }

    public void LoadImage(BitmapSource image, string name, bool unsaved = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        Dispatcher.VerifyAccess();
        _ocrCts?.Cancel();
        _imageVersion++;
        if (_editor != null) _editor.Changed -= Editor_Changed;
        _editor = new AnnotationCanvas(image) { Width = image.PixelWidth, Height = image.PixelHeight, Tool = _tool, Stroke = _stroke, Thickness = _thickness };
        _editor.Changed += Editor_Changed;
        ImageViewbox.Child = _editor;
        _imageName = name;
        _savedRevision = 0;
        _revision = unsaved ? 1 : 0;
        _savedHash = unsaved ? null : SHA256.HashData(ImageOutput.EncodePng(_editor.Flatten()));
        EmptyState.Visibility = Visibility.Collapsed;
        ImageScroll.Visibility = Visibility.Visible;
        DimensionsText.Text = string.Format(CultureInfo.CurrentCulture, "{0:N0} × {1:N0} px", image.PixelWidth, image.PixelHeight);
        OcrText.Clear();
        _fit = true;
        UpdateLayout();
        ApplyZoom();
        UpdateControls();
    }

    private void Editor_Changed(object? sender, EventArgs e) { _revision++; UpdateControls(); }

    private void UpdateControls()
    {
        if (!IsInitialized || _disposed) return;
        bool image = _editor != null;
        bool accountBusy = _accountCts != null;
        bool ocrBusy = _ocrCts != null;
        bool signedIn = _openAi.ActiveAccount?.IsSignedIn == true;
        CaptureButton.IsEnabled = OpenButton.IsEnabled = PasteButton.IsEnabled = EmptyCaptureButton.IsEnabled = !_replacing && !_closing;
        CopyButton.IsEnabled = SaveButton.IsEnabled = image && !_replacing;
        ToolDock.IsEnabled = DrawingSettings.IsEnabled = ZoomControls.IsEnabled = image;
        UndoButton.IsEnabled = _editor?.CanUndo == true;
        RedoButton.IsEnabled = _editor?.CanRedo == true;
        ModelPicker.IsEnabled = signedIn && !accountBusy && !ocrBusy;
        RefreshModelsButton.IsEnabled = signedIn && !accountBusy && !ocrBusy;
        ModelPlaceholder.Visibility = ModelPicker.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed;
        OcrButton.IsEnabled = ocrBusy || (image && signedIn && !accountBusy && !_replacing && ModelPicker.SelectedItem != null);
        OcrButton.Content = ocrBusy ? "Abbrechen" : "Text erkennen";
        OcrText.IsReadOnly = ocrBusy;
        CopyTextButton.IsEnabled = SaveTextButton.IsEnabled = OcrText.Text.Length > 0 && !ocrBusy;
        AccountPicker.IsEnabled = AddAccountButton.IsEnabled = !accountBusy && !_ocrUnavailable;
        LoginButton.IsEnabled = !_ocrUnavailable;
        LoginButton.Content = accountBusy ? "Abbrechen" : "Weiter mit ChatGPT";
        LoginButton.Visibility = !signedIn || accountBusy ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.IsEnabled = !accountBusy;
        SavedAccountsRow.Visibility = _openAi.Accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountStatus.Text = _ocrUnavailable && !_diagnosticsMode ? "Konto nicht verfügbar" : signedIn ? "Mit ChatGPT verbunden" : "Konto verbinden";
        AccountDetail.Text = signedIn ? (string.IsNullOrWhiteSpace(_openAi.ActiveAccount?.Email) ? _openAi.ActiveAccount?.Label : _openAi.ActiveAccount.Email) : "Direkt bei OpenAI anmelden";
        AccountDetail.ToolTip = AccountDetail.Text;
        FileNameText.Text = _imageName + (_revision != _savedRevision ? " •" : "");
        FileNameText.ToolTip = FileNameText.Text;
        Title = image ? FileNameText.Text + " — Kairos Capture" : "Kairos Capture";
    }

    private void SetStatus(string text, bool error = false)
    {
        if (_disposed) return;
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "GoldBrush" : "MutedBrush");
    }

    private void ReportError(string action, Exception exception)
    {
        App.LogException(action, exception);
        // Provider messages can contain request URLs or account data; keep them out of the UI.
        string reason = exception is OpenAiRequestException request ? request.Message :
            exception is OpenAiUserException userError ? userError.Message :
            exception is TimeoutException ? "Das Zeitlimit wurde erreicht. Versuche es später erneut." :
            exception is ArgumentException && action == "Texterkennung" ? "Das Bild ist für OCR zu groß oder ungültig. Versuche einen kleineren Ausschnitt." :
            exception is HttpRequestException ? "Prüfe deine Internetverbindung und versuche es erneut." :
            exception is UnauthorizedAccessException ? "Der Zugriff wurde verweigert. Wähle einen anderen Speicherort." :
            exception is IOException ? "Die Datei oder Zwischenablage ist gerade nicht verfügbar." : "Versuche es erneut.";
        SetStatus(action + " fehlgeschlagen. " + reason, true);
    }

    public async Task CaptureAsync(bool entireScreen = false)
    {
        if (_replacing || _closing || _disposed) return;
        _replacing = true;
        UpdateControls();
        try
        {
            if (!await PrepareReplacementAsync()) return;
            SetStatus("Bereich wählen · Esc bricht die Aufnahme ab.");
            BitmapSource? image = entireScreen ? await CaptureService.CaptureScreenAsync(this) : await CaptureService.CaptureRegionAsync(this);
            if (image == null) { SetStatus("Aufnahme abgebrochen."); return; }
            LoadImage(image, "Aufnahme " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".png", unsaved: true);
            ShowFromTray();
            SetStatus("Aufnahme bereit. Wähle links ein Werkzeug zum Markieren.");
        }
        catch (OperationCanceledException) { SetStatus("Aufnahme abgebrochen."); }
        catch (Exception ex) { ReportError("Aufnahme", ex); }
        finally { _replacing = false; UpdateControls(); }
    }

    private async void Capture_Click(object sender, RoutedEventArgs e) => await CaptureAsync();

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenAsync();
    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteAsync();

    private async Task OpenAsync(string? path = null)
    {
        if (_replacing || _closing) return;
        _replacing = true;
        UpdateControls();
        try
        {
            if (path == null)
            {
                var dialog = new OpenFileDialog { Title = "Bild öffnen", Filter = "Bilddateien (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
                if (dialog.ShowDialog(this) != true) return;
                path = dialog.FileName;
            }
            if (!IsSupportedImage(path)) { SetStatus("Bitte eine PNG-, JPG- oder BMP-Datei wählen.", true); return; }
            // Decode first: a corrupt file must never discard the current work.
            BitmapSource image = ImageOutput.Load(path);
            if (!await PrepareReplacementAsync()) return;
            LoadImage(image, Path.GetFileName(path));
            SetStatus("Bild geöffnet.");
        }
        catch (Exception ex) { ReportError("Bild öffnen", ex); }
        finally { _replacing = false; UpdateControls(); }
    }

    private async Task PasteAsync()
    {
        if (_replacing || _closing) return;
        _replacing = true;
        UpdateControls();
        try
        {
            BitmapSource? image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
            if (image == null) { SetStatus("Die Zwischenablage enthält kein Bild."); return; }
            if (!await PrepareReplacementAsync()) return;
            LoadImage(image, "Zwischenablage.png", unsaved: true);
            SetStatus("Bild aus der Zwischenablage eingefügt.");
        }
        catch (Exception ex) { ReportError("Einfügen", ex); }
        finally { _replacing = false; UpdateControls(); }
    }

    private async Task<bool> PrepareReplacementAsync()
    {
        await CancelOcrAsync();
        if (_editor == null) return true;
        _editor.CommitText();
        if (_revision == _savedRevision) return true;
        byte[] current = SHA256.HashData(ImageOutput.EncodePng(_editor.Flatten()));
        if (_savedHash != null && CryptographicOperations.FixedTimeEquals(current, _savedHash)) return true;
        MessageBoxResult result = ShowSavePrompt();
        return result == MessageBoxResult.No || (result == MessageBoxResult.Yes && SaveImage());
    }

    private MessageBoxResult ShowSavePrompt()
    {
        if (!IsVisible) ShowFromTray();
        var dialog = new Window { Owner = this, Title = "Änderungen speichern?", Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Änderungen speichern?", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Deine Markierungen wurden noch nicht gespeichert.", Margin = new Thickness(0, 10, 0, 23), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        MessageBoxResult result = MessageBoxResult.Cancel;
        foreach ((string label, MessageBoxResult choice) in new[] { ("Speichern", MessageBoxResult.Yes), ("Verwerfen", MessageBoxResult.No), ("Abbrechen", MessageBoxResult.Cancel) })
        {
            var button = new Button { Content = label, Margin = new Thickness(6, 0, 0, 0), IsDefault = choice == MessageBoxResult.Yes, IsCancel = choice == MessageBoxResult.Cancel };
            if (choice == MessageBoxResult.Yes) button.Style = (Style)FindResource("PrimaryButton");
            button.Click += (_, _) => { result = choice; dialog.Close(); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
        return result;
    }

    private bool SaveImage()
    {
        if (_editor == null) return false;
        try
        {
            _editor.CommitText();
            BitmapSource image = _editor.Flatten();
            string? path = ImageOutput.Save(image, this);
            if (path == null) return false;
            _savedHash = SHA256.HashData(ImageOutput.EncodePng(image));
            _savedRevision = _revision;
            _imageName = Path.GetFileName(path);
            UpdateControls();
            SetStatus("Bild gespeichert: " + _imageName);
            return true;
        }
        catch (Exception ex) { ReportError("Speichern", ex); return false; }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveImage();
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyImage();
    private void CopyImage()
    {
        if (_editor == null) return;
        try { _editor.CommitText(); ImageOutput.Copy(_editor.Flatten()); SetStatus("Bild in die Zwischenablage kopiert."); }
        catch (Exception ex) { ReportError("Kopieren", ex); }
    }

    private static bool IsSupportedImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp";
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_replacing && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1 && IsSupportedImage(files[0]) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1) await OpenAsync(files[0]);
        else SetStatus("Bitte genau eine PNG-, JPG- oder BMP-Datei ablegen.");
    }

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string name } && Enum.TryParse(name, out EditorTool tool))
        {
            _editor?.CommitText();
            _tool = tool;
            if (_editor != null) _editor.Tool = tool;
        }
    }
    private void Color_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Background: Brush brush }) { _stroke = brush; if (_editor != null) _editor.Stroke = brush; }
    }
    private void Thickness_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string value } } && double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out double thickness))
        { _thickness = thickness; if (_editor != null) _editor.Thickness = thickness; }
    }
    private void Undo_Click(object sender, RoutedEventArgs e) { _editor?.CommitText(); _editor?.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { _editor?.CommitText(); _editor?.Redo(); }

    private void Fit_Click(object sender, RoutedEventArgs e) { _fit = true; ApplyZoom(); }
    private double NativePixelScale => 1 / VisualTreeHelper.GetDpi(ImageViewbox).DpiScaleX;
    private void ActualSize_Click(object sender, RoutedEventArgs e) { _fit = false; SetZoom(NativePixelScale); }
    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) { if (_fit) ApplyZoom(); }
    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_settingZoom || _editor == null) return; _fit = false; SetZoom(e.NewValue / 100 * NativePixelScale); }
    private void ApplyZoom()
    {
        if (_editor == null) return;
        double scale = _fit ? Math.Min(NativePixelScale, Math.Min(Math.Max(1, ImageScroll.ViewportWidth - 56) / _editor.Width, Math.Max(1, ImageScroll.ViewportHeight - 56) / _editor.Height)) : ZoomSlider.Value / 100 * NativePixelScale;
        SetZoom(scale);
    }
    private void SetZoom(double scale)
    {
        if (_editor == null || !double.IsFinite(scale) || scale <= 0) return;
        ImageViewbox.Width = _editor.Width * scale;
        ImageViewbox.Height = _editor.Height * scale;
        _settingZoom = true;
        ZoomSlider.Value = Math.Clamp(scale / NativePixelScale * 100, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _settingZoom = false;
        ZoomText.Text = (scale / NativePixelScale).ToString("0 %", CultureInfo.CurrentCulture);
    }
    private void ImageScroll_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _editor == null) return;
        _fit = false;
        SetZoom(Math.Clamp(ImageViewbox.Width / _editor.Width * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.1 * NativePixelScale, 3 * NativePixelScale));
        e.Handled = true;
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // The editor owns Escape/Ctrl+Enter inside its text box; all native text edits stay native.
        if (Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is PasswordBox) return;
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.O: e.Handled = true; await OpenAsync(); return;
                case Key.V: e.Handled = true; await PasteAsync(); return;
                case Key.C: e.Handled = true; CopyImage(); return;
                case Key.S: e.Handled = true; SaveImage(); return;
                case Key.Z: e.Handled = true; _editor?.CommitText(); if ((modifiers & ModifierKeys.Shift) != 0) _editor?.Redo(); else _editor?.Undo(); return;
                case Key.Y: e.Handled = true; _editor?.CommitText(); _editor?.Redo(); return;
                case Key.D0: case Key.NumPad0: e.Handled = true; _fit = true; ApplyZoom(); return;
                case Key.D1: case Key.NumPad1: e.Handled = true; _fit = false; SetZoom(NativePixelScale); return;
            }
            return;
        }
        if (modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Delete && _editor != null) { _editor.DeleteSelected(); e.Handled = true; return; }
        if (e.Key == Key.Escape)
        {
            _editor?.HandleKey(Key.Escape);
            SelectTool(EditorTool.Select);
            e.Handled = true;
            return;
        }
        EditorTool? tool = e.Key switch { Key.V => EditorTool.Select, Key.A => EditorTool.Arrow, Key.R => EditorTool.Rectangle, Key.E => EditorTool.Ellipse, Key.P => EditorTool.Pen, Key.T => EditorTool.Text, Key.H => EditorTool.Highlight, Key.B => EditorTool.Redact, _ => null };
        if (tool.HasValue && _editor != null) { SelectTool(tool.Value); e.Handled = true; }
    }
    private void SelectTool(EditorTool tool)
    {
        foreach (object child in ToolDock.Children)
            if (child is RadioButton button && (string)button.Tag == tool.ToString()) { button.IsChecked = true; break; }
    }

    private void RefreshAccounts()
    {
        _refreshingAccounts = true;
        AccountPicker.ItemsSource = null;
        AccountPicker.ItemsSource = _openAi.Accounts;
        foreach (OpenAiAccount account in _openAi.Accounts)
            if (account.Key == _openAi.ActiveAccount?.Key) { AccountPicker.SelectedItem = account; break; }
        _refreshingAccounts = false;
        UpdateControls();
    }
    private async void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingAccounts || AccountPicker.SelectedItem is not OpenAiAccount account) return;
        await RunAccountActionAsync(async ct =>
        {
            _openAi.SelectAccount(account.Key);
            ModelPicker.ItemsSource = null;
            SetStatus(account.IsSignedIn ? "Konto gewechselt. Lade die aktuellen Modelle." : "Gespeichertes Konto gewählt. Melde dich erneut an.");
            if (account.IsSignedIn) await LoadModelsAsync(ct);
        });
    }
    private async Task RunAccountActionAsync(Func<CancellationToken, Task> action)
    {
        if (_accountCts != null || _ocrUnavailable || _diagnosticsMode || _disposed) return;
        using var cts = new CancellationTokenSource();
        _accountCts = cts;
        UpdateControls();
        try { await CancelOcrAsync(); await action(cts.Token); }
        catch (OperationCanceledException) { SetStatus("Kontovorgang abgebrochen."); }
        catch (Exception ex) { ReportError("Kontovorgang", ex); }
        finally { _accountCts = null; if (!_disposed) { RefreshAccounts(); UpdateControls(); } }
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_accountCts != null) { _accountCts.Cancel(); SetStatus("Anmeldung wird abgebrochen …"); return; }
        await SignInAsync(_openAi.ActiveAccount?.Key);
    }
    private async void AddAccount_Click(object sender, RoutedEventArgs e) => await SignInAsync(null);
    private async Task SignInAsync(string? key)
    {
        await RunAccountActionAsync(async ct =>
        {
            SetStatus("Anmeldung im Browser abschließen. Hier kannst du abbrechen.");
            await _openAi.SignInAsync(key, ct);
            ct.ThrowIfCancellationRequested();
            if (_openAi.ActiveAccount?.IsSignedIn != true) throw new InvalidOperationException("Sign-in did not complete.");
            ModelPicker.ItemsSource = null;
            SetStatus("Mit ChatGPT verbunden. Modelle werden geladen …");
            RefreshAccounts();
            await LoadModelsAsync(ct);
        });
    }
    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountActionAsync(async ct =>
        {
            string? warning = await _openAi.SignOutAsync(ct);
            ModelPicker.ItemsSource = null;
            SetStatus(string.IsNullOrWhiteSpace(warning) ? "Von ChatGPT abgemeldet." : "Lokal abgemeldet. Die serverseitige Abmeldung konnte nicht bestätigt werden.", !string.IsNullOrWhiteSpace(warning));
        });
    }
    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RunAccountActionAsync(LoadModelsAsync);
    private async Task LoadModelsAsync(CancellationToken ct)
    {
        string? selected = (ModelPicker.SelectedItem as OpenAiModel)?.Slug;
        SetStatus("Aktuelle Modelle werden geladen …");
        var models = await _openAi.GetModelsAsync(ct);
        ct.ThrowIfCancellationRequested();
        ModelPicker.ItemsSource = models;
        foreach (OpenAiModel model in models) if (model.Slug == selected) { ModelPicker.SelectedItem = model; break; }
        if (ModelPicker.SelectedItem == null && models.Count > 0) ModelPicker.SelectedIndex = 0;
        SetStatus(models.Count == 0 ? "Für dieses Konto wurden keine Modelle gefunden." : "Modelle geladen. Die Texterkennung startet erst per Button.", models.Count == 0);
    }
    private void Model_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized) UpdateControls(); }

    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrCts != null) { _ocrCts.Cancel(); SetStatus("Texterkennung wird abgebrochen …"); return; }
        if (_editor == null || ModelPicker.SelectedItem is not OpenAiModel model || _accountCts != null) return;
        _ocrTask = RecognizeAsync(model.Slug);
        await _ocrTask;
    }
    private async Task RecognizeAsync(string model)
    {
        using var cts = new CancellationTokenSource();
        _ocrCts = cts;
        string previous = OcrText.Text;
        var pending = new StringBuilder();
        int imageVersion = _imageVersion;
        string? accountKey = _openAi.ActiveAccount?.Key;
        UpdateControls();
        SetStatus("Text wird erkannt …");
        bool StillCurrent() => !_disposed && !cts.IsCancellationRequested && imageVersion == _imageVersion && accountKey == _openAi.ActiveAccount?.Key;
        try
        {
            _editor!.CommitText();
            byte[] png = ImageOutput.EncodePng(_editor.Flatten());
            var progress = new Progress<string>(delta =>
            {
                if (!StillCurrent() || !ReferenceEquals(_ocrCts, cts)) return;
                pending.Append(delta);
                OcrText.Text = pending.ToString();
                OcrText.ScrollToEnd();
            });
            string text = await _openAi.ExtractTextAsync(png, model, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!StillCurrent()) return;
            OcrText.Text = text;
            SetStatus(text.Length == 0 ? "Texterkennung abgeschlossen. Kein Text gefunden." : "Text erkannt. Du kannst ihn jetzt bearbeiten, kopieren oder speichern.");
        }
        catch (OperationCanceledException) { if (!_disposed && imageVersion == _imageVersion) { OcrText.Text = previous; SetStatus("Texterkennung abgebrochen. Vorheriger Text bleibt erhalten."); } }
        catch (Exception ex) { if (!_disposed && imageVersion == _imageVersion) { OcrText.Text = previous; ReportError("Texterkennung", ex); } }
        finally { _ocrCts = null; UpdateControls(); }
    }
    private async Task CancelOcrAsync()
    {
        _ocrCts?.Cancel();
        if (_ocrTask != null) await _ocrTask;
    }
    private void OcrText_Changed(object sender, TextChangedEventArgs e)
    {
        if (OcrPlaceholder == null) return;
        OcrPlaceholder.Visibility = OcrText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateControls();
    }
    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(OcrText.Text); SetStatus("Text in die Zwischenablage kopiert."); }
        catch (Exception ex) { ReportError("Text kopieren", ex); }
    }
    private void SaveText_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { Title = "Text speichern", Filter = "Textdatei (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true, OverwritePrompt = true, FileName = Path.GetFileNameWithoutExtension(_imageName) + ".txt" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, OcrText.Text, new UTF8Encoding(false));
            SetStatus("Text gespeichert.");
        }
        catch (Exception ex) { ReportError("Text speichern", ex); }
    }

    public void ShowFromTray()
    {
        if (_disposed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    internal void FitDocumentForDiagnostics() { _fit = true; ApplyZoom(); }
    internal void ShowUnexpectedError() => SetStatus("Die Aktion konnte nicht abgeschlossen werden. Speichere dein Bild und starte Kairos bei Bedarf neu.", true);
    public async Task RequestExitAsync()
    {
        if (_closing || _replacing) return;
        _closing = true;
        UpdateControls();
        try
        {
            if (!await PrepareReplacementAsync()) return;
            _accountCts?.Cancel();
            if (Application.Current is App app) app.ExitApplication();
            else Close();
        }
        catch (Exception ex) { ReportError("Beenden", ex); }
        finally { _closing = false; UpdateControls(); }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_diagnosticsMode && Application.Current is App app && !app.IsExiting)
        {
            e.Cancel = true;
            Hide();
            app.NotifyHidden();
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _disposed = true;
        _ocrCts?.Cancel();
        _accountCts?.Cancel();
        _hotkey?.Dispose();
        _openAi.Dispose();
        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
