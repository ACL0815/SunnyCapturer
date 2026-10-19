using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using KairosCapture.Services;
using Forms = System.Windows.Forms;

namespace KairosCapture;

public partial class App : Application
{
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private TrayMenu? _trayMenu;
    private DispatcherTimer? _countdown;
    private int _remaining;
    private string? _balloonFolder;
    private bool _diagnostics;
    private readonly List<Capture.PinWindow> _pins = new();
    internal int PinCount => _pins.Count;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _diagnostics = e.Args.Length > 0 && (e.Args[0] is "--self-test" or "--self-check" or "--openai-probe" || e.Args[0].StartsWith("--render-", StringComparison.Ordinal));
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) LogException("Unhandled exception", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("Background task", args.Exception);
            args.SetObserved();
        };
        try
        {
            if (_diagnostics)
            {
                int exit = AppDiagnostics.Run(e.Args);
                IsExiting = true;
                Shutdown(exit);
                return;
            }
            if (e.Args.Length > 1 || (e.Args.Length == 1 && e.Args[0] is not ("--demo" or "--settings")))
                throw new ArgumentException("Aufruf: KairosCapture [--demo | --self-test <Ordner> | --render-preview <PNG-Datei> | --render-overlay-preview <PNG-Datei>]");
            RefreshAutostartPath();
            var window = new MainWindow();
            MainWindow = window;
            InitializeTray(window);
            window.InitializeHotkey();
            if (e.Args.Length == 1) window.ShowFromTray();
            if (!window.Settings.WelcomeShown)
            {
                _tray?.ShowBalloonTip(4000, "Kairos Capture ist bereit", "Linksklick nimmt einen Bereich auf. Rechtsklick öffnet Aufnahmen und Einstellungen. Tastenkürzel: " + window.Settings.Hotkey, Forms.ToolTipIcon.Info);
                window.Settings.WelcomeShown = true; window.SaveSettings();
            }
        }
        catch (Exception exception)
        {
            LogException("Startup", exception);
            if (!_diagnostics)
                MessageBox.Show(exception is ArgumentException ? exception.Message :
                    "Kairos Capture konnte nicht gestartet werden. Prüfe, ob die .NET 10 Desktop Runtime für Windows x64 vorhanden ist.",
                    "Kairos Capture", MessageBoxButton.OK, MessageBoxImage.Error);
            IsExiting = true;
            Shutdown(1);
        }
    }

    private static void RefreshAutostartPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            // An absent value means autostart is off. Never enable it during a path migration.
            if (key?.GetValue("KairosCapture") is not string current || !IsStableInstall(Environment.ProcessPath)) return;
            string command = "\"" + Environment.ProcessPath + "\"";
            if (!string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
                key.SetValue("KairosCapture", command, Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex) { LogException("Autostart path update", ex); }
    }

    // Debug builds, worktrees and old versioned folders must not take over autostart.
    internal static bool IsStableInstall(string? processPath) =>
        processPath is not null &&
        string.Equals(Path.GetFileName(processPath), "KairosCapture.exe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileName(Path.GetDirectoryName(processPath)), "KairosCapture-win-x64", StringComparison.OrdinalIgnoreCase);

    private void InitializeTray(MainWindow window)
    {
        string? executable = Environment.ProcessPath;
        if (executable is not null) _trayIcon = Icon.ExtractAssociatedIcon(executable);
        _trayIcon ??= (Icon)SystemIcons.Application.Clone();
        _trayMenu = new TrayMenu(window, seconds => ToggleCountdown(window, seconds));
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "Kairos Capture · " + window.Settings.Hotkey, ContextMenuStrip = _trayMenu.Menu, Visible = true };
        _tray.MouseClick += async (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            if (_countdown?.IsEnabled == true) { CancelCountdown(window); return; }
            await window.CaptureAsync();
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonFolder != null)
            {
                try { GlassWindow.Open(_balloonFolder); }
                catch (Exception ex) { LogException("Balloon folder", ex); }
            }
            else window.ShowFromTray();
        };
        window.SettingsChanged += () => RefreshTray(window);
        window.CaptureCompleted += (completion, path) =>
        {
            _balloonFolder = path == null ? null : Path.GetDirectoryName(path);
            string text = path != null ? "Gespeichert – klicken öffnet den Ordner." : "Aufnahme in die Zwischenablage kopiert.";
            _tray.ShowBalloonTip(2000, completion == Capture.CaptureCompletion.Copied ? "Kopiert" : "Gespeichert", text, Forms.ToolTipIcon.None);
        };
        Capture.CaptureService.CaptureStateChanged += (_, _) =>
        {
            if (Capture.CaptureService.IsCapturing) CancelCountdown(window);
            RefreshTray(window);
        };
    }

    private void RefreshTray(MainWindow window)
    {
        _trayMenu?.Refresh(Capture.CaptureService.IsCapturing, Capture.CaptureService.LastSession != null);
        if (_tray != null && _countdown?.IsEnabled != true) _tray.Text = "Kairos Capture · " + window.Settings.Hotkey;
    }

    internal void ShowPin(Capture.CaptureSessionResult result, AppSettings settings)
    {
        var pin = new Capture.PinWindow(result.Image!, result.Bounds, result.Region, settings);
        _pins.Add(pin);
        pin.Closed += (_, _) => { _pins.Remove(pin); if (MainWindow is MainWindow main) RefreshTray(main); };
        pin.ImageSaved += path =>
        {
            if (MainWindow is MainWindow main) main.RememberSavedFile(path);
        };
        try { pin.Show(); }
        catch { pin.Close(); _pins.Remove(pin); throw; }
        if (MainWindow is MainWindow window) RefreshTray(window);
    }

    internal void ClosePins()
    {
        foreach (var pin in _pins.ToArray()) pin.Close();
    }

    private void ToggleCountdown(MainWindow window, int seconds)
    {
        if (_countdown?.IsEnabled == true) { CancelCountdown(window); return; }
        if (Capture.CaptureService.IsCapturing) return;
        _remaining = seconds;
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tray!.Text = $"Kairos · Aufnahme in {_remaining} s · Linksklick bricht ab";
        _countdown.Tick += async (_, _) =>
        {
            if (--_remaining > 0) { _tray.Text = $"Kairos · Aufnahme in {_remaining} s · Linksklick bricht ab"; return; }
            CancelCountdown(window);
            await window.CaptureAsync();
        };
        _countdown.Start();
    }
    private void CancelCountdown(MainWindow window)
    {
        _countdown?.Stop(); _countdown = null;
        if (_tray != null) _tray.Text = "Kairos Capture · " + window.Settings.Hotkey;
    }

    public void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;
        Shutdown();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("UI action", e.Exception);
        if (_diagnostics)
        {
            IsExiting = true;
            e.Handled = true;
            Shutdown(1);
            return;
        }
        if (MainWindow is MainWindow window)
        {
            window.ShowUnexpectedError();
            e.Handled = true;
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        ClosePins();
        _countdown?.Stop();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _trayMenu?.Dispose();
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    internal static void LogException(string action, Exception exception)
        => AppendLog($"{action}: {OpenAiDiagnostics.Describe(exception)}{Environment.NewLine}{new StackTrace(exception, false)}");

    internal static void LogOpenAiDiagnostic(string diagnostic) => AppendLog("OpenAI: " + diagnostic);

    private static void AppendLog(string diagnostic)
    {
        try
        {
            // Never persist exception messages: provider URLs, tokens and image text can occur in them.
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairosCapture", "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "application.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {diagnostic}{Environment.NewLine}");
        }
        catch (Exception) { /* Diagnostics must never stop capture or error recovery. */ }
    }
}
