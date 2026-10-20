using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KairosCapture.Capture;
using KairosCapture.Services;
using CaptureMode = KairosCapture.Capture.CaptureMode;
using Microsoft.Win32;

namespace KairosCapture;

public partial class MainWindow : Window
{
    private readonly bool _diagnosticsMode;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _accountCts;
    private GlobalHotkey? _hotkey;
    private bool _refreshing, _closed, _exiting;
    private IReadOnlyList<OpenAiModel> _models = Array.Empty<OpenAiModel>();
    private bool _accountPreferenceApplied;
    private int _loadingAccounts;
    private Exception? _loadError;
    private string? _loadFailure;
    private readonly Func<string, bool> _confirmReset;
    internal AppSettings Settings { get; }
    internal OpenAiService OpenAi { get; }
    internal Task OpenAiInitialization { get; }
    internal event Action? SettingsChanged;
    internal event Action<CaptureCompletion, string?>? CaptureCompleted;
    internal string OcrStatus => OpenAi.StoredSessionUnreadable ? "OCR: gespeicherte Anmeldung nicht lesbar · Einstellungen öffnen" :
        !OpenAi.IsLoaded ? (_loadingAccounts > 0 ? "OCR: OpenAI-Konto wird geladen…" : "OCR: Konto nicht geladen · erneut versuchen") : OpenAi.ActiveAccount is { IsSignedIn: true } account
        ? $"OCR: {(account.Email.Length > 0 ? account.Email : account.Label)} · {(!OpenAi.CanUsePlan ? "Planfreigabe fehlt" : Settings.Model.Length > 0 ? Settings.Model : "Modell fehlt")}" : "OCR: nicht verbunden";

    public MainWindow(bool diagnosticsMode = false) : this(diagnosticsMode, null) { }

    internal MainWindow(bool diagnosticsMode, OpenAiService? openAi, Func<string, bool>? confirmReset = null)
    {
        _diagnosticsMode = diagnosticsMode;
        _confirmReset = confirmReset ?? (diagnosticsMode ? _ => false : message =>
            MessageBox.Show(this, message, "Gespeicherte Anmeldung zurücksetzen", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        Settings = diagnosticsMode ? new() : AppSettings.Load();
        InitializeComponent();
        HotkeyBox.Text = Settings.Hotkey;
        FolderText.Text = Settings.SaveFolder;
        AskSave.IsChecked = Settings.SaveWithoutDialog;
        SaveCopy.IsChecked = Settings.SaveOnCopy;
        AutoCopy.IsChecked = Settings.AutoCopyOcr;
        OpenAi = openAi ?? (diagnosticsMode ? AppDiagnostics.CreateIsolatedService() : new OpenAiService());
        RefreshAccounts();
        if (!diagnosticsMode)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            StartupCheck.IsChecked = key?.GetValue("KairosCapture") is string;
        }
        SourceInitialized += (_, _) => GlassWindow.Apply(this);
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) RefreshAccounts();
            if (IsVisible && !_diagnosticsMode && OpenAi.ActiveAccount?.IsSignedIn == true && _models.Count == 0)
                await RunAccountActionAsync(LoadModelsAsync);
        };
        OpenAiInitialization = InitializeOpenAiAsync();
    }

    private async Task InitializeOpenAiAsync()
    {
        try
        {
            await EnsureOpenAiLoadedAsync(_lifetime.Token);
            if (_closed) return;
            SetStatus(OpenAi.ActiveAccount?.IsSignedIn == true ? "Mit ChatGPT verbunden." : "Mit ChatGPT verbinden, um Text zu erkennen.");
            if (!_diagnosticsMode && IsVisible && OpenAi.ActiveAccount?.IsSignedIn == true)
                await RunAccountActionAsync(LoadModelsAsync);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.LogException("OpenAI-Konto laden", ex); if (!_closed) SetStatus(_loadFailure!); }
    }

    internal async Task EnsureOpenAiLoadedAsync(CancellationToken ct)
    {
        _loadingAccounts++;
        if (!OpenAi.IsLoaded)
        {
            _loadError = null;
            _loadFailure = null;
            SetStatus("OpenAI-Konto wird geladen…");
            RefreshAccounts();
        }
        try
        {
            await OpenAi.EnsureLoadedAsync(ct);
            if (_closed) return;
            if (!_accountPreferenceApplied)
            {
                if (OpenAi.Accounts.Any(a => a.Key == Settings.AccountKey)) await OpenAi.SelectAccountAsync(Settings.AccountKey, ct);
                _accountPreferenceApplied = true;
            }
            _loadError = null;
            _loadFailure = null;
        }
        catch (OperationCanceledException)
        {
            if (!_closed && !OpenAi.IsLoaded) SetStatus("OpenAI-Konto nicht geladen · erneut versuchen");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loadError = ex;
            _loadFailure = "OpenAI-Konto konnte nicht geladen werden. " + OpenAiDiagnostics.UserMessage(ex) +
                (ex is OpenAiUserException { Error: OpenAiUserError.CredentialsUnreadable } ? "" :
                    " Bitte Verbinden, Modelle aktualisieren oder Texterkennung erneut wählen.");
            throw;
        }
        finally { _loadingAccounts--; if (!_closed) RefreshAccounts(); }
    }

    internal void InitializeHotkey()
    {
        if (_diagnosticsMode || _hotkey != null) return;
        _hotkey = new GlobalHotkey(this, () => _ = CaptureAsync(), Settings.Hotkey);
        _hotkey.RegistrationFailed += SetStatus;
        if (!_hotkey.Register(out string? error)) SetStatus(error!);
    }

    internal void SaveSettings()
    {
        try { if (!_diagnosticsMode) Settings.Save(); }
        catch (Exception ex) { ReportError("Einstellungen konnten nicht gespeichert werden", ex); }
        SettingsChanged?.Invoke();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Settings.SaveWithoutDialog = AskSave.IsChecked == true;
        Settings.SaveOnCopy = SaveCopy.IsChecked == true;
        Settings.AutoCopyOcr = AutoCopy.IsChecked == true;
        SaveSettings();
    }

    private void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!HotkeyGesture.TryCreate(Keyboard.Modifiers, key, out var gesture)) return;
        if (_diagnosticsMode) { SetStatus("Tastenkürzel werden in der Vorschau nicht registriert."); return; }
        InitializeHotkey();
        if (!_hotkey!.TryChange(gesture, out string? error)) { SetStatus(error!); return; }
        HotkeyBox.Text = Settings.Hotkey = gesture.ToString();
        SaveSettings();
        SetStatus("Tastenkürzel ist sofort aktiv: " + Settings.Hotkey);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Speicherordner für Aufnahmen", InitialDirectory = Settings.SaveFolder };
        if (dialog.ShowDialog(this) != true) return;
        FolderText.Text = Settings.SaveFolder = dialog.FolderName;
        SaveSettings();
    }

    internal void OpenSaveFolder()
    {
        try { Directory.CreateDirectory(Settings.SaveFolder); GlassWindow.Open(Settings.SaveFolder); }
        catch (Exception ex) { ReportError("Ordner konnte nicht geöffnet werden", ex); }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenSaveFolder();
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try { string path = Path.Combine(AppSettings.DirectoryPath, "logs"); Directory.CreateDirectory(path); GlassWindow.Open(path); }
        catch (Exception ex) { ReportError("Protokollordner konnte nicht geöffnet werden", ex); }
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsMode) return;
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (StartupCheck.IsChecked == true)
                key.SetValue("KairosCapture", "\"" + (Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad fehlt.")) + "\"");
            else key.DeleteValue("KairosCapture", false);
        }
        catch (Exception ex) { StartupCheck.IsChecked = !(StartupCheck.IsChecked == true); ReportError("Autostart konnte nicht geändert werden", ex); }
    }

    internal void RefreshAccounts()
    {
        _refreshing = true;
        AccountPicker.ItemsSource = OpenAi.Accounts;
        AccountPicker.SelectedItem = OpenAi.Accounts.FirstOrDefault(a => a.Key == OpenAi.ActiveAccount?.Key);
        AccountStatus.Text = _loadError is not null && (!OpenAi.IsLoaded || OpenAi.StoredSessionUnreadable) ? OpenAiDiagnostics.UserMessage(_loadError) :
            OpenAi.StoredSessionUnreadable ? "Die gespeicherte Anmeldung ist nicht lesbar. Gespeicherte Anmeldung zurücksetzen oder erneut versuchen." :
            !OpenAi.IsLoaded ? _loadingAccounts > 0 ? "OpenAI-Konto wird geladen…" :
            _loadFailure ?? "OpenAI-Konto nicht geladen · erneut versuchen" :
            OpenAi.ActiveAccount?.IsSignedIn == true ? (OpenAi.CanUsePlan ? "Mit ChatGPT verbunden" : "Mit ChatGPT verbunden · Freigabe zur Nutzung des Plans fehlt") :
                "Mit ChatGPT verbinden, um Text aus Aufnahmen zu erkennen.";
        ResetSessionButton.Visibility = OpenAi.StoredSessionUnreadable
            ? Visibility.Visible : Visibility.Collapsed;
        _refreshing = false;
        SettingsChanged?.Invoke();
    }

    private async void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || AccountPicker.SelectedItem is not OpenAiAccount account || _accountCts != null) return;
        await RunAccountActionAsync(async ct =>
        {
            await OpenAi.SelectAccountAsync(account.Key, ct);
            Settings.AccountKey = account.Key;
            _models = Array.Empty<OpenAiModel>();
            ModelPicker.ItemsSource = _models;
            SaveSettings(); RefreshAccounts();
            if (account.IsSignedIn) await LoadModelsAsync(ct);
        });
    }
    private async Task RunAccountActionAsync(Func<CancellationToken, Task> action, bool loadFirst = true)
    {
        if (_closed || _accountCts != null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _accountCts = operation;
        AccountPicker.IsEnabled = LoginButton.IsEnabled = AddAccountButton.IsEnabled = SignOutButton.IsEnabled = RefreshButton.IsEnabled = false;
        ResetSessionButton.IsEnabled = false;
        CancelLoginButton.Visibility = Visibility.Visible;
        try { if (loadFirst) await EnsureOpenAiLoadedAsync(operation.Token); await action(operation.Token); }
        catch (OperationCanceledException ex) { if (!_closed) SetStatus("Vorgang abgebrochen." + OpenAiDiagnostics.ResetPendingMessage(ex)); }
        catch (Exception ex)
        {
            if (ex is OpenAiUserException { Error: OpenAiUserError.CredentialsUnreadable }) _loadError = ex;
            App.LogException("OpenAI", ex);
            if (!_closed) SetStatus(OpenAiDiagnostics.UserMessage(ex));
        }
        finally
        {
            _accountCts = null;
            if (!_closed)
            {
                AccountPicker.IsEnabled = LoginButton.IsEnabled = AddAccountButton.IsEnabled = SignOutButton.IsEnabled = RefreshButton.IsEnabled = true;
                ResetSessionButton.IsEnabled = true;
                CancelLoginButton.Visibility = Visibility.Collapsed;
                RefreshAccounts();
            }
        }
    }
    internal async Task ConnectAsync(bool addAccount = false)
    {
        await RunAccountActionAsync(async ct =>
        {
            SetStatus("Anmeldung im Browser abschließen…");
            try { await OpenAi.SignInAsync(addAccount ? null : OpenAi.ActiveAccount?.Key, ct); }
            finally
            {
                // Identity-only consent also creates a valid, active account before reporting its missing plan permission.
                if (!_closed)
                {
                    Settings.AccountKey = OpenAi.ActiveAccount?.Key ?? "";
                    _models = Array.Empty<OpenAiModel>(); ModelPicker.ItemsSource = _models;
                    SaveSettings(); RefreshAccounts();
                }
            }
            if (_closed) return;
            await LoadModelsAsync(ct);
            if (_models.Count > 0) SetStatus("Mit ChatGPT verbunden · Texterkennung ist bereit.");
        });
    }
    private async void Login_Click(object sender, RoutedEventArgs e) => await ConnectAsync();
    private async void ResetSession_Click(object sender, RoutedEventArgs e) => await ResetStoredSessionAsync();
    internal async Task ResetStoredSessionAsync()
    {
        if (!_confirmReset("Die gespeicherten Anmeldedateien im Ordner „" + OpenAi.SessionDirectory +
            "“ werden ohne Löschung in dessen Unterordner session-backup-<Zeitstempel>-<GUID> verschoben. " +
            "Ein unterbrochenes Zurücksetzen in session-reset.pending wird fortgesetzt. Danach ist eine neue Anmeldung mit ChatGPT erforderlich. " +
            "Eine inzwischen wieder lesbare Anmeldung wird übernommen. Fortfahren?")) return;
        await RunAccountActionAsync(async ct =>
        {
            string? backup = await OpenAi.ResetStoredSessionAsync(ct);
            _loadError = null;
            _loadFailure = null;
            _accountPreferenceApplied = true;
            if (backup is null)
            {
                // A recovered store keeps the saved account choice instead of falling back to the first account.
                if (OpenAi.Accounts.Any(a => a.Key == Settings.AccountKey)) await OpenAi.SelectAccountAsync(Settings.AccountKey, ct);
                else if (OpenAi.ActiveAccount is { } active) { Settings.AccountKey = active.Key; SaveSettings(); }
                SetStatus(OpenAi.Accounts.Any() ? "Die gespeicherte Anmeldung ist wieder lesbar." :
                    "Keine gespeicherte Anmeldung vorhanden. Bitte mit ChatGPT anmelden.");
                return;
            }
            Settings.AccountKey = "";
            Settings.Model = "";
            _models = Array.Empty<OpenAiModel>();
            ModelPicker.ItemsSource = _models;
            SaveSettings();
            SetStatus("Gespeicherte Anmeldung zurückgesetzt. Sicherung: " + backup + ". Sie können sich jetzt mit ChatGPT anmelden.");
        }, loadFirst: false);
    }
    private async void AddAccount_Click(object sender, RoutedEventArgs e) => await ConnectAsync(true);
    private void CancelLogin_Click(object sender, RoutedEventArgs e) => _accountCts?.Cancel();
    private async void SignOut_Click(object sender, RoutedEventArgs e) => await RunAccountActionAsync(async ct =>
    {
        string? warning = await OpenAi.SignOutAsync(ct);
        _models = Array.Empty<OpenAiModel>(); ModelPicker.ItemsSource = _models;
        SetStatus(warning ?? "Abgemeldet.");
    });
    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RunAccountActionAsync(LoadModelsAsync);
    private async Task LoadModelsAsync(CancellationToken ct)
    {
        await EnsureOpenAiLoadedAsync(ct);
        if (OpenAi.ActiveAccount?.IsSignedIn != true) { SetStatus("Zuerst mit ChatGPT verbinden."); return; }
        var models = await OpenAi.GetModelsAsync(ct);
        if (_closed) return;
        _models = models;
        _refreshing = true;
        ModelPicker.ItemsSource = models;
        ModelPicker.SelectedItem = OpenAiService.SelectOcrModel(models, Settings.Model);
        _refreshing = false;
        if (ModelPicker.SelectedItem is OpenAiModel model) { Settings.Model = model.Slug; SaveSettings(); }
        else if (Settings.Model.Length > 0) { Settings.Model = ""; SaveSettings(); }
        if (models.Count == 0) SetStatus("Dieses Konto stellt derzeit kein OCR-Modell bereit. Konto und Freigaben in ChatGPT prüfen.");
    }
    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && ModelPicker.SelectedItem is OpenAiModel model) { Settings.Model = model.Slug; SaveSettings(); }
    }

    internal async Task CaptureAsync(CaptureMode mode = CaptureMode.Region)
    {
        if (_closed || _diagnosticsMode || CaptureService.IsCapturing) return;
        try
        {
            CaptureSessionResult result = await CaptureService.CaptureEditedAsync(this, Settings, mode, _lifetime.Token);
            if (_closed) return;
            if (result.SavedPath is { } path)
            {
                RememberSavedFile(path);
            }
            if (result.Completion == CaptureCompletion.Pinned && result.Image != null)
            {
                ((App)Application.Current).ShowPin(result, Settings);
            }
            else if (result.Completion == CaptureCompletion.Ocr && result.Image != null)
            {
                var window = new OcrResultWindow(this, result.Image, result.Bounds, result.Region);
                window.Show();
            }
            else if (result.Completion is CaptureCompletion.Copied or CaptureCompletion.Saved)
                CaptureCompleted?.Invoke(result.Completion, result.SavedPath);
            SettingsChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowFromTray(); ReportError("Aufnahme fehlgeschlagen", ex); }
    }

    internal void RememberSavedFile(string path)
    {
        Settings.RecentFiles = new[] { path }.Concat(Settings.RecentFiles).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        SaveSettings();
    }

    public void ShowFromTray()
    {
        if (_closed) return;
        RefreshAccounts();
        Show(); WindowState = WindowState.Normal; Activate();
    }
    internal void SetStatus(string text) { if (!_closed) StatusText.Text = text; }
    private void ReportError(string action, Exception ex) { App.LogException(action, ex); SetStatus(action + ": " + ex.Message); }
    internal void ShowUnexpectedError() => SetStatus("Die Aktion konnte nicht abgeschlossen werden. Details stehen im Protokoll.");
    public Task RequestExitAsync()
    {
        _exiting = true; _lifetime.Cancel(); _accountCts?.Cancel();
        Close(); ((App)Application.Current).ExitApplication();
        return Task.CompletedTask;
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_diagnosticsMode && !_exiting && !((App)Application.Current).IsExiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true; _lifetime.Cancel(); _accountCts?.Cancel(); _hotkey?.Dispose(); OpenAi.Dispose();
        base.OnClosed(e);
    }
}
