using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace KairosCapture;

public partial class App : Application
{
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private bool _hiddenNoticeShown;
    private bool _diagnostics;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _diagnostics = e.Args.Length > 0 && e.Args[0] is "--self-test" or "--self-check" or "--render-preview";
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
            if (e.Args.Length > 1 || (e.Args.Length == 1 && e.Args[0] != "--demo"))
                throw new ArgumentException("Aufruf: KairosCapture [--demo | --self-test <Ordner> | --render-preview <PNG-Datei>]");
            var window = new MainWindow();
            MainWindow = window;
            InitializeTray(window);
            if (e.Args.Length == 1) window.LoadImage(AppDiagnostics.CreateDemoBitmap(), "Beispiel.png");
            window.Show();
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

    private void InitializeTray(MainWindow window)
    {
        string? executable = Environment.ProcessPath;
        if (executable is not null) _trayIcon = Icon.ExtractAssociatedIcon(executable);
        _trayIcon ??= (Icon)SystemIcons.Application.Clone();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Kairos Capture öffnen", null, (_, _) => Dispatcher.Invoke(window.ShowFromTray));
        menu.Items.Add("Bereich aufnehmen  ·  Strg+Alt+A", null, (_, _) => Dispatcher.InvokeAsync(async () => await window.CaptureAsync()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Dispatcher.InvokeAsync(async () => await window.RequestExitAsync()));
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "Kairos Capture · Strg+Alt+A", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(window.ShowFromTray);
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(window.ShowFromTray);
    }

    public void NotifyHidden()
    {
        if (_hiddenNoticeShown || _tray is null) return;
        _hiddenNoticeShown = true;
        _tray.ShowBalloonTip(3000, "Kairos Capture bleibt bereit", "Strg+Alt+A nimmt einen Bereich auf. Im Tray-Menü kannst du Kairos beenden.", Forms.ToolTipIcon.Info);
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
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    internal static void LogException(string action, Exception exception)
    {
        try
        {
            // Never persist exception messages: provider URLs, tokens and image text can occur in them.
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairosCapture", "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "application.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {action}: {exception.GetType().FullName}{Environment.NewLine}{new StackTrace(exception, false)}{Environment.NewLine}");
        }
        catch (Exception) { /* Diagnostics must never stop capture or error recovery. */ }
    }
}
