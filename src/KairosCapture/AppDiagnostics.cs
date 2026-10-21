using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KairosCapture.Capture;
using KairosCapture.Editor;
using KairosCapture.Services;

namespace KairosCapture;

internal sealed class SelfCheckFailure(string message) : Exception(message);

internal static class AppDiagnostics
{
    internal sealed record CheckResult(string Name, string Status, long DurationMs, IReadOnlyList<string> Errors);
    private sealed class OfflineHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond is null ? throw new InvalidOperationException("Network access is disabled in diagnostics mode.") : Task.FromResult(respond(request));
    }

    internal static OpenAiService CreateIsolatedService() => new(new HttpClient(new OfflineHandler()),
        new OpenAiStorage(Path.Combine(Path.GetTempPath(), "KairosCapture-diagnostics-" + Guid.NewGuid().ToString("N"))),
        _ => throw new InvalidOperationException("Browser access is disabled in diagnostics mode."), true);

    internal static int Run(string[] args)
    {
        if (args.Length == 2 && args[0] == "--openai-ocr-probe" && !string.IsNullOrWhiteSpace(args[1]))
        {
            string path = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AppSettings settings = AppSettings.Load();
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 120));
                drawing.DrawText(new FormattedText(OpenAiOcrTrace.SyntheticText, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 40, Brushes.Black, 1), new Point(24, 30));
            }
            var bitmap = new RenderTargetBitmap(640, 120, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] png = ImageOutput.EncodePng(bitmap);
            OpenAiProbeResult probe = Task.Run(() => OpenAiService.ProbeOcrAsync(png, settings.AccountKey, settings.Model)).GetAwaiter().GetResult();
            File.WriteAllLines(path, probe.Lines);
            foreach (string line in probe.Lines) Console.WriteLine(line);
            return probe.Passed ? 0 : 1;
        }
        if (args.Length is 1 or 2 && args[0] == "--openai-probe")
        {
            OpenAiProbeResult probe = Task.Run(OpenAiService.ProbeAsync).GetAwaiter().GetResult();
            string path = Path.GetFullPath(args.Length == 2 ? args[1] : Path.Combine("test-results", "openai-probe.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, probe.Lines);
            foreach (string line in probe.Lines) Console.WriteLine(line);
            return probe.Passed ? 0 : 1;
        }
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1])) return 2;
        string previewPath = Path.GetFullPath(args[1]);
        if (args[0] is "--render-preview" or "--render-settings-preview") { RenderPreview(previewPath); return 0; }
        if (args[0] == "--render-overlay-preview") { RenderOverlayPreview(previewPath); return 0; }
        if (args[0] == "--render-pin") { RenderPinPreview(previewPath); return 0; }
        if (args[0] is "--render-ocr-preview" or "--render-ocr-result") { RenderOcrPreview(previewPath); return 0; }
        if (args[0] == "--render-tray-preview") { RenderTrayPreview(previewPath); return 0; }
        if (args[0] == "--render-editor-preview")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
            File.WriteAllBytes(previewPath, ImageOutput.EncodePng(EditorSelfChecks.RenderPreview(CreateDemoBitmap())));
            return 0;
        }
        if (args[0] is not ("--self-test" or "--self-check")) return 2;
        string directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        var results = new List<CheckResult>();
        RunCheck(results, "capture", () => { CaptureSelfChecks.Run(); return Array.Empty<string>(); });
        RunCheck(results, "editor", () => { EditorSelfChecks.Run(); return Array.Empty<string>(); });
        RunCheck(results, "overlay", () => { OverlaySelfChecks.Run(); return Array.Empty<string>(); });
        RunCheck(results, "openai", OpenAiSelfChecks.Run);
        RunCheck(results, "shell", CheckShell);
        RunCheck(results, "settings", CheckSettings);
        RunCheck(results, "hotkey", CheckHotkey);
        RunCheck(results, "ocr-result", CheckOcrResult);
        RunCheck(results, "tray", CheckTray);
        bool passed = results.All(result => result.Status == "passed");
        WriteJson(Path.Combine(directory, "self-test.json"), new
        {
            schemaVersion = 1,
            timestampUtc = DateTimeOffset.UtcNow,
            passed,
            exitCode = passed ? 0 : 1,
            checks = results,
            limitations = new[] { "No real desktop capture, clipboard modification, browser login or live OCR request was performed.",
                "Mixed-DPI hardware, global hotkey/tray interaction and live account access require separate verification." }
        });
        return passed ? 0 : 1;
    }

    internal static void RunCheck(List<CheckResult> results, string name, Func<IReadOnlyList<string>> check)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<string> errors = check();
            results.Add(new CheckResult(name, errors.Count == 0 ? "passed" : "failed", watch.ElapsedMilliseconds, errors));
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            results.Add(new CheckResult(name, "failed", watch.ElapsedMilliseconds,
                new[] { cause is SelfCheckFailure ? cause.Message : OpenAiDiagnostics.Describe(cause) }));
        }
    }

    private static IReadOnlyList<string> CheckShell()
    {
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Shell checks require STA.");
        Require(App.IsStableInstall(@"C:\x\artifacts\KairosCapture-win-x64\KairosCapture.exe") &&
            !App.IsStableInstall(@"C:\x\artifacts\KairosCapture-0.4.0-win-x64\KairosCapture.exe") &&
            !App.IsStableInstall(@"C:\x\bin\Release\net10.0-windows\win-x64\KairosCapture.exe") &&
            !App.IsStableInstall(@"C:\Program Files\dotnet\dotnet.exe") && !App.IsStableInstall(null),
            "Only the stable publish folder may take over autostart.");
        string worktree = Path.Combine(Path.GetTempPath(), "kairos-worktree-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(worktree);
            string exe = Path.Combine(worktree, "artifacts", "KairosCapture-win-x64", "KairosCapture.exe");
            Directory.CreateDirectory(Path.Combine(worktree, ".git"));
            Require(App.IsStableInstall(exe), "The main checkout, whose .git is a folder, must keep autostart in sync.");
            Directory.Delete(Path.Combine(worktree, ".git"));
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");
            Require(!App.IsStableInstall(exe), "A publish folder inside a linked git worktree must not take over autostart.");
        }
        finally { Directory.Delete(worktree, true); }
        var window = new MainWindow(diagnosticsMode: true);
        try
        {
            FrameworkElement content = LayoutPreview(window, 560, 660);
            Require(content.ActualWidth == 560 && content.ActualHeight == 660, "Settings must arrange at compact dimensions.");
            foreach (string name in new[] { "HotkeyBox", "FolderText", "AskSave", "SaveCopy", "AccountPicker", "ModelPicker", "AutoCopy", "StartupCheck", "OverlayDiagnosticsCheck" })
                Require(window.FindName(name) is FrameworkElement, "Settings field missing: " + name);
            Require(window.FindName("CropCanvas") == null && !window.AllowDrop, "Settings must not host a workbench or image drop target.");
            Require(((TextBox)window.FindName("HotkeyBox")).Text == "Strg+Alt+A", "Default shortcut must be visible.");
            Require(((CheckBox)window.FindName("AutoCopy")).IsChecked == true && ((CheckBox)window.FindName("AskSave")).IsChecked != true,
                "Settings UI must reflect robust defaults.");
            foreach (string name in new[] { "AskSave", "SaveCopy", "AutoCopy", "StartupCheck" })
                Require(((CheckBox)window.FindName(name)).Foreground is SolidColorBrush { Color.R: > 180, Color.G: > 180, Color.B: > 180 }, "Settings toggles must retain a readable light foreground.");
            LayoutPreview(window, 500, 550);
            Require(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Shell checks must stay offscreen.");
        }
        finally { window.Close(); }
        CheckOpenAiReset();
        CheckOpenAiRecoveredReset();
        CheckOpenAiStoreStatus();
        CheckOpenAiStartupRetry();
        return Array.Empty<string>();
    }

    private static void CheckOpenAiStartupRetry()
    {
        foreach (string action in new[] { "models", "ocr", "connect" })
        {
            string directory = Path.Combine(Path.GetTempPath(), "KairosCapture-startup-check-" + Guid.NewGuid().ToString("N"));
            var storage = new OpenAiStorage(directory);
            var credentials = new OpenAiCredentials("startup-access", "startup-refresh", "startup-id", "startup-nonce",
                OpenAiProtocol.Scope.Split(' '), DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.MinValue);
            var first = new OpenAiRegistration(OpenAiProtocol.AccountKey("oaiapp_first", "first"), "oaiapp_first", "first", "first@example.invalid", "First", credentials);
            var preferred = first with { Key = OpenAiProtocol.AccountKey("oaiapp_preferred", "preferred"), ClientId = "oaiapp_preferred", Subject = "preferred" };
            int requests = 0;
            bool browserOpened = false;
            using var http = new HttpClient(new OfflineHandler(request =>
            {
                requests++;
                bool models = request.RequestUri!.AbsolutePath == "/v1/models";
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(models ? "{\"models\":[{\"slug\":\"startup-model\",\"display_name\":\"Startup\",\"visibility\":\"list\"}]}" :
                        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"startup text\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n",
                        Encoding.UTF8, models ? "application/json" : "text/event-stream")
                };
            }));
            MainWindow? window = null;
            OcrResultWindow? result = null;
            using var service = new OpenAiService(http, storage, _ => { browserOpened = true; throw new InvalidOperationException("Synthetic browser stop."); },
                loadTimeout: TimeSpan.FromMilliseconds(150));
            try
            {
                using FileStream gate = storage.LockAsync(CancellationToken.None).GetAwaiter().GetResult();
                storage.SaveAsync(new OpenAiState("urn:uuid:startup-test", new[] { first, preferred }), CancellationToken.None).GetAwaiter().GetResult();
                window = new MainWindow(true, service);
                window.Settings.AccountKey = preferred.Key;
                window.Settings.AutoCopyOcr = false;
                Require(!service.IsLoaded && window.AccountStatus.Text.Contains("wird geladen") && window.OcrStatus.Contains("wird geladen"),
                    "Startup must expose loading status in settings and tray.");
                AwaitOnDispatcher(window.OpenAiInitialization);
                Require(ReferenceEquals(window.OpenAi, service) && !service.IsLoaded && window.AccountStatus.Text.Contains("erneut") &&
                    window.OcrStatus.Contains("erneut") && window.StatusText.Text.Contains("Modelle aktualisieren") && requests == 0 &&
                    !service.StoredSessionUnreadable && window.ResetSessionButton.Visibility == Visibility.Collapsed,
                    "A busy startup must retain the service and explain how to retry.");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    bool propagated = false;
                    try { AwaitOnDispatcher(window.EnsureOpenAiLoadedAsync(cancelled.Token)); }
                    catch (OperationCanceledException) { propagated = true; }
                    Require(propagated, "M6: the UI load boundary swallowed caller cancellation.");
                    Require(window.OcrStatus.Contains("nicht geladen") && window.OcrStatus.Contains("erneut versuchen") &&
                        window.AccountStatus.Text.Contains("nicht geladen") && !window.AccountStatus.Text.Contains("wird geladen") &&
                        window.StatusText.Text.Contains("nicht geladen"),
                        "A cancelled load attempt must offer retry instead of claiming to still load.");
                }
                gate.Dispose();
                if (action == "models")
                {
                    window.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    async Task Finished() { while (!window.RefreshButton.IsEnabled) await Task.Delay(5); }
                    AwaitOnDispatcher(Finished());
                    Require(window.ModelPicker.Items.Count == 1 && requests == 1, "The Models button did not retry startup load.");
                }
                else if (action == "ocr")
                {
                    result = new OcrResultWindow(window, CreateDemoBitmap(), default, default, diagnostics: true);
                    AwaitOnDispatcher(result.RecognizeAsync());
                    Require(result.ResultText.Text == "startup text" && result.HasImage && requests == 2, "OCR click did not retry startup load and retain the image.");
                }
                else
                {
                    AwaitOnDispatcher(window.ConnectAsync());
                    Require(browserOpened && requests == 0, "Connect did not retry startup load before opening the fake browser.");
                }
                Require(service.IsLoaded && service.ActiveAccount?.Key == preferred.Key && window.AccountPicker.Items.Count == 2 &&
                    window.AccountStatus.Text.Contains("Mit ChatGPT verbunden") && !window.OcrStatus.Contains("nicht geladen"),
                    "Retry did not apply the saved account and refresh settings and tray.");
                AwaitOnDispatcher(service.SelectAccountAsync(first.Key, CancellationToken.None));
                AwaitOnDispatcher(window.EnsureOpenAiLoadedAsync(CancellationToken.None));
                Require(service.ActiveAccount?.Key == first.Key, "The saved account preference must be applied only once, never over a later choice.");
            }
            finally
            {
                result?.Close();
                window?.Close();
                string target = Path.GetFullPath(directory);
                Require(target.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "KairosCapture-startup-check-"), StringComparison.OrdinalIgnoreCase),
                    "Startup test cleanup escaped its temporary directory.");
                if (Directory.Exists(target)) Directory.Delete(target, true);
            }
        }
    }

    private static void CheckOpenAiReset()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KairosCapture-reset-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        byte[] damaged = { 1 };
        string temp = Path.Combine(directory, "openai-0000000000000000001-broken.tmp");
        File.WriteAllBytes(temp, damaged);
        bool browserOpened = false;
        using var http = new HttpClient(new OfflineHandler());
        using var service = new OpenAiService(http, new OpenAiStorage(directory),
            _ => { browserOpened = true; throw new InvalidOperationException("Synthetic browser stop."); });
        bool confirmed = false;
        string? confirmation = null;
        var window = new MainWindow(true, service, message => { confirmation = message; return confirmed; });
        try
        {
            AwaitOnDispatcher(window.OpenAiInitialization);
            var reset = window.FindName("ResetSessionButton") as Button;
            Require(!service.IsLoaded && reset is { Visibility: Visibility.Visible, IsEnabled: true } &&
                window.AccountStatus.Text.Contains("Gespeicherte Anmeldung zurücksetzen") &&
                window.AccountStatus.Text.Contains("session-backup-"),
                "W1: unreadable credentials provide no usable reset action and backup location.");
            window.Settings.AccountKey = "previous-account";
            window.Settings.Model = "previous-model";
            AwaitOnDispatcher(window.ResetStoredSessionAsync());
            Require(confirmation is not null && confirmation.Contains(directory, StringComparison.Ordinal) &&
                confirmation.Contains("neue Anmeldung", StringComparison.Ordinal) && confirmation.Contains("session-backup-", StringComparison.Ordinal) &&
                File.ReadAllBytes(temp).SequenceEqual(damaged) && !Directory.EnumerateDirectories(directory).Any() &&
                !service.IsLoaded && window.Settings.AccountKey == "previous-account" && window.Settings.Model == "previous-model",
                "W-A: declining the confirmation changed files, session or account preferences, or the dialog omitted folder and consequence.");
            confirmed = true;
            reset!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            async Task Finished() { while (!reset.IsEnabled) await Task.Delay(5); }
            AwaitOnDispatcher(Finished());
            string backup = Directory.GetDirectories(directory, "session-backup-*").Single();
            Require(service.IsLoaded && service.Accounts.Count == 0 && !File.Exists(temp) &&
                File.ReadAllBytes(Path.Combine(backup, Path.GetFileName(temp))).SequenceEqual(damaged) &&
                window.StatusText.Text.Contains(backup) && reset.Visibility == Visibility.Collapsed &&
                window.Settings.AccountKey == "" && window.Settings.Model == "",
                "W1: reset must preserve the encrypted file and expose a fresh usable session and backup path.");
            AwaitOnDispatcher(window.ConnectAsync());
            Require(browserOpened, "W1: Connect remained trapped in EnsureLoadedAsync after reset.");
        }
        finally
        {
            window.Close();
            Require(Path.GetFullPath(directory).StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "KairosCapture-reset-ui-"),
                StringComparison.OrdinalIgnoreCase), "Reset fixture cleanup escaped its temporary directory.");
            Directory.Delete(directory, true);
        }
    }

    private static void CheckOpenAiRecoveredReset()
    {
        // Scenarios: saved preference present, saved preference gone (adopt active), store recovered but empty.
        foreach (string scenario in new[] { "preferred", "missing", "empty" })
        {
            string directory = Path.Combine(Path.GetTempPath(), "KairosCapture-recovered-ui-" + Guid.NewGuid().ToString("N"));
            var storage = new OpenAiStorage(directory);
            using var http = new HttpClient(new OfflineHandler());
            using var service = new OpenAiService(http, storage);
            Directory.CreateDirectory(directory);
            string snapshot = Path.Combine(directory, "openai-session.bin");
            File.WriteAllBytes(snapshot, new byte[] { 1 });
            var window = new MainWindow(true, service, _ => true);
            try
            {
                AwaitOnDispatcher(window.OpenAiInitialization);
                var credentials = new OpenAiCredentials("recovered-access", "recovered-refresh", "recovered-id", "recovered-nonce",
                    OpenAiProtocol.Scope.Split(' '), DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.MinValue);
                var account = new OpenAiRegistration(OpenAiProtocol.AccountKey("oaiapp_recovered", "recovered"), "oaiapp_recovered", "recovered",
                    "recovered@example.invalid", "Recovered", credentials);
                // The preferred account is deliberately not the first one, so a first-account fallback is detected.
                var first = account with { Key = OpenAiProtocol.AccountKey("oaiapp_first", "first"), ClientId = "oaiapp_first", Subject = "first" };
                var accounts = scenario == "empty" ? Array.Empty<OpenAiRegistration>() : new[] { first, account };
                using (storage.LockAsync(CancellationToken.None).GetAwaiter().GetResult())
                    storage.SaveAsync(new OpenAiState("urn:uuid:recovered", accounts), CancellationToken.None).GetAwaiter().GetResult();
                byte[] before = File.ReadAllBytes(snapshot);
                window.Settings.AccountKey = scenario == "preferred" ? account.Key : "gone-account";
                window.Settings.Model = "keep-model";
                AwaitOnDispatcher(window.ResetStoredSessionAsync());
                string? expectedActive = scenario switch { "preferred" => account.Key, "missing" => first.Key, _ => null };
                string expectedStatus = scenario == "empty" ? "Keine gespeicherte Anmeldung vorhanden. Bitte mit ChatGPT anmelden." :
                    "Die gespeicherte Anmeldung ist wieder lesbar.";
                Require(window.StatusText.Text == expectedStatus && service.ActiveAccount?.Key == expectedActive &&
                    window.Settings.AccountKey == (expectedActive ?? "gone-account") && window.Settings.Model == "keep-model" &&
                    window.ResetSessionButton.Visibility == Visibility.Collapsed && File.ReadAllBytes(snapshot).SequenceEqual(before) &&
                    !Directory.EnumerateDirectories(directory).Any(), "W-A: recovered reset (" + scenario + ") did not keep the right account and explain recovery without moving files.");
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        }
    }

    private static void CheckOpenAiStoreStatus()
    {
        foreach (string action in new[] { "ocr", "models" })
        foreach (bool pending in new[] { false, true })
        {
            string directory = Path.Combine(Path.GetTempPath(), "KairosCapture-store-status-ui-" + Guid.NewGuid().ToString("N"));
            var storage = new OpenAiStorage(directory);
            var credentials = new OpenAiCredentials("status-access", "status-refresh", "status-id", "status-nonce",
                OpenAiProtocol.Scope.Split(' '), DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.MinValue);
            var account = new OpenAiRegistration(OpenAiProtocol.AccountKey("oaiapp_status", "status"), "oaiapp_status", "status",
                "status@example.invalid", "Status", credentials);
            using (storage.LockAsync(CancellationToken.None).GetAwaiter().GetResult())
                storage.SaveAsync(new OpenAiState("urn:uuid:status", new[] { account }), CancellationToken.None).GetAwaiter().GetResult();
            using var http = new HttpClient(new OfflineHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent("{\"models\":[]}", Encoding.UTF8, "application/json") }));
            using var service = new OpenAiService(http, storage);
            var window = new MainWindow(true, service);
            OcrResultWindow? result = null;
            try
            {
                AwaitOnDispatcher(window.OpenAiInitialization);
                Require(window.ResetSessionButton.Visibility == Visibility.Collapsed && !service.StoredSessionUnreadable,
                    "Readable credentials must not offer reset.");
                string snapshot = Path.Combine(directory, "openai-session.bin");
                byte[] original = File.ReadAllBytes(snapshot);
                if (pending) Directory.CreateDirectory(storage.ResetPath);
                else File.WriteAllBytes(snapshot, new byte[] { 1 });
                if (action == "ocr")
                {
                    result = new OcrResultWindow(window, CreateDemoBitmap(), default, default, diagnostics: true);
                    AwaitOnDispatcher(result.RecognizeAsync());
                }
                else
                {
                    window.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    async Task Finished() { while (!window.RefreshButton.IsEnabled) await Task.Delay(5); }
                    AwaitOnDispatcher(Finished());
                    Require(window.AccountStatus.Text.Contains("geschützten OpenAI-Kontodaten", StringComparison.Ordinal),
                        "X2: settings lost the account action's unreadable credentials error.");
                }
                using var tray = new TrayMenu(window, _ => { });
                tray.Refresh(false, false, 0);
                Require(service.StoredSessionUnreadable && window.ResetSessionButton.Visibility == Visibility.Visible &&
                    window.OcrStatus.Contains("nicht lesbar", StringComparison.Ordinal) &&
                    tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Any(item => item.Text?.Contains("nicht lesbar", StringComparison.Ordinal) == true),
                    "W-B: an unreadable store in the " + action + " path did not show reset in settings and unreadable status in tray.");
                if (pending) Directory.Delete(storage.ResetPath);
                else File.WriteAllBytes(snapshot, original);
                AwaitOnDispatcher(service.GetModelsAsync(CancellationToken.None));
                window.RefreshAccounts();
                Require(!service.StoredSessionUnreadable && window.ResetSessionButton.Visibility == Visibility.Collapsed &&
                    !window.OcrStatus.Contains("nicht lesbar", StringComparison.Ordinal), "W-B: successful loading did not clear the recovery action and tray status.");
            }
            finally { result?.Close(); window.Close(); Directory.Delete(directory, true); }
        }
    }

    private static IReadOnlyList<string> CheckSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kairos-settings-check-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "settings.json");
        try
        {
            var defaults = AppSettings.Load(file);
            using (var json = JsonDocument.Parse(JsonSerializer.Serialize(defaults)))
                Require(json.RootElement.TryGetProperty("OverlayDiagnosticsEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False,
                    "Overlay diagnostics must be a persisted setting that defaults to false.");
            Require(defaults.Hotkey == "Strg+Alt+A" && defaults.AutoCopyOcr && !defaults.SaveWithoutDialog && !defaults.SaveOnCopy &&
                defaults.SaveFolder == AppSettings.DefaultSaveFolder, "Missing settings must produce expected defaults.");
            var settings = new AppSettings { SaveFolder = Path.Combine(directory, "Bilder"), Hotkey = "Strg+Umschalt+F8", SaveWithoutDialog = true,
                SaveOnCopy = true, AutoCopyOcr = false, Model = "test-model", AccountKey = "test-account", WelcomeShown = true, RecentFiles = new[] { "test.png" } };
            settings.Save(file); var loaded = AppSettings.Load(file);
            Require(loaded.SaveFolder == settings.SaveFolder && loaded.Hotkey == settings.Hotkey && loaded.SaveWithoutDialog && loaded.SaveOnCopy && !loaded.AutoCopyOcr &&
                loaded.Model == "test-model" && loaded.AccountKey == "test-account" && loaded.WelcomeShown && loaded.RecentFiles.SequenceEqual(settings.RecentFiles), "Settings must roundtrip every preference.");
            settings.Save(file);
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic settings writes must leave no temporary files.");
            File.WriteAllText(file, "{\"AskBeforeSaving\":false,\"Hotkey\":\"Strg+Alt+F8\"}");
            var legacy = AppSettings.Load(file);
            Require(!legacy.SaveWithoutDialog && legacy.Hotkey == "Strg+Alt+F8",
                "Legacy AskBeforeSaving false must be ignored while keeping save dialog enabled and other preferences intact.");
            Require(!JsonSerializer.Serialize(legacy).Contains("AskBeforeSaving", StringComparison.Ordinal), "Legacy field must not be persisted again.");
            foreach (string corrupt in new[] { "{broken", "null", "[]", "{\"Hotkey\":null,\"SaveFolder\":null,\"Model\":null,\"RecentFiles\":null}" })
            {
                File.WriteAllText(file, corrupt); var recovered = AppSettings.Load(file);
                Require(recovered.Hotkey == "Strg+Alt+A" && recovered.AutoCopyOcr && recovered.SaveFolder == AppSettings.DefaultSaveFolder,
                    "Broken or partial settings must fall back safely.");
            }
            BitmapSource image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            var dialogSettings = new AppSettings { SaveFolder = Path.Combine(directory, "Dialog") };
            var owner = new Window();
            int prompts = 0;
            try
            {
                string? cancelled = ImageOutput.Save(image, owner, dialogSettings, (dialog, parent) =>
                {
                    prompts++;
                    Require(ReferenceEquals(parent, owner) && dialog.InitialDirectory == dialogSettings.SaveFolder && Directory.Exists(dialogSettings.SaveFolder),
                        "Save dialog must receive the owner and the created configured folder.");
                    Require(DateTime.TryParseExact(dialog.FileName, "'Kairos 'yyyy-MM-dd HHmmss'.png'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
                        dialog.DefaultExt == "png" && dialog.OverwritePrompt, "Save dialog must suggest the timestamped PNG filename and confirm overwrites.");
                    return false;
                });
                Require(prompts == 1 && cancelled == null && Directory.GetFiles(dialogSettings.SaveFolder).Length == 0,
                    "Default save must ask and cancellation must not create an image.");
                string chosen = Path.Combine(dialogSettings.SaveFolder, "Ausgewählt.png");
                Require(ImageOutput.Save(image, owner, dialogSettings, (dialog, _) => { prompts++; dialog.FileName = chosen; return true; }) == chosen && File.Exists(chosen),
                    "Confirmed dialog must write the selected filename.");
                dialogSettings.SaveWithoutDialog = true;
                Require(ImageOutput.Save(image, owner, dialogSettings, (_, _) => throw new InvalidOperationException("Opt-in direct save must bypass dialog.")) != null && prompts == 2,
                    "Only SaveWithoutDialog true may bypass the dialog.");
                Require(!owner.IsVisible && new WindowInteropHelper(owner).Handle == IntPtr.Zero, "Dialog injection must not show a native window.");
            }
            finally { owner.Close(); }
            var now = new DateTime(2026, 10, 19, 14, 5, 9);
            string saved = ImageOutput.SaveDirect(image, settings.SaveFolder, now);
            Require(saved == Path.Combine(settings.SaveFolder, "Kairos 2026-10-19 140509.png") && File.Exists(saved), "Direct save must create configured directory and exact filename.");
            string second = ImageOutput.SaveDirect(image, settings.SaveFolder, now);
            Require(second != saved && File.Exists(second) && ImageOutput.Load(saved).PixelWidth == 2, "Rapid saves must preserve both images.");
            return Array.Empty<string>();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static IReadOnlyList<string> CheckHotkey()
    {
        foreach (string text in new[] { "Ctrl+Alt+A", "Strg+Alt+A", "Control+Shift+F12", "Win+7", "Alt+F24" })
        {
            Require(HotkeyGesture.TryParse(text, out var gesture) && gesture.VirtualKey > 0 && (gesture.NativeModifiers & 0x4000) != 0, "Valid shortcut rejected: " + text);
            Require(HotkeyGesture.TryParse(gesture.ToString(), out var reparsed) && gesture == reparsed, "Hotkey format must roundtrip.");
        }
        foreach (string text in new[] { "", "A", "Shift+A", "Ctrl+Ctrl+A", "Alt+", "Ctrl+Escape", "Ctrl+None", "Ctrl+12345" })
            Require(!HotkeyGesture.TryParse(text, out _), "Invalid hotkey accepted: " + text);
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> CheckOcrResult()
    {
        var settings = new MainWindow(diagnosticsMode: true);
        var window = new OcrResultWindow(settings, CreateDemoBitmap(), default, default, diagnostics: true);
        try
        {
            Require(!window.TranslationPicker.IsEditable && window.TranslationPicker.Items.Count == 9 &&
                window.TranslationPicker.SelectedItem is OpenAiTranslation { Language: null }, "OCR must default to transcription with a closed translation whitelist.");
            Require(window.TranslationPicker.SelectedItem.ToString() == "Nicht übersetzen", "Translation selection must display its German label.");
            Task operation = window.RecognizeAsync();
            AwaitOnDispatcher(operation);
            Require(operation.IsCompletedSuccessfully && window.HasImage && window.Status.Contains("ChatGPT", StringComparison.Ordinal), "Missing account must explain next action and retain image without network.");
            var desktop = new System.Drawing.Rectangle(-1920, -1080, 4480, 2520);
            var work = new System.Drawing.Rectangle(-1920, -1080, 1920, 1040);
            foreach (double scale in new[] { 1d, 1.5, 2, 3 })
            {
                var placed = OcrResultWindow.ResultBounds(desktop, new Int32Rect(1700, 800, 200, 200), work, scale);
                Require(work.Contains(placed) && placed.Width == Math.Min(work.Width, (int)Math.Ceiling(440 * scale)), "OCR result placement must use target DPI and stay in its working area.");
            }
            LayoutPreview(window, 440, 350);
            window.SetPreviewResult();
            Require(!window.ResultText.IsReadOnly && window.ResultText.AcceptsReturn, "Recognized text must stay editable.");
            window.ResultText.Text = "Edited locally";
            Require(window.ResultText.Text == "Edited locally", "Result text must allow local editing.");
            Require(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "OCR checks must not show a native window.");
        }
        finally { window.Close(); settings.Close(); }
        Require(!window.HasImage, "Closing the result must release its image.");
        return Array.Empty<string>();
    }

    private static void AwaitOnDispatcher(Task operation)
    {
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (operation.IsCompleted || watch.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start();
        try { if (!operation.IsCompleted) Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(operation.IsCompleted, "Asynchronous UI check exceeded ten seconds.");
        operation.GetAwaiter().GetResult();
    }

    private static IReadOnlyList<string> CheckTray()
    {
        var settings = new MainWindow(diagnosticsMode: true);
        using var tray = new TrayMenu(settings, _ => throw new InvalidOperationException("Countdown must not start during layout."));
        try
        {
            var entries = tray.Menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
            Require(entries.Length == 10 && entries[0].Text!.StartsWith("Bereich aufnehmen", StringComparison.Ordinal) && entries[1].Text == "Vollbild aufnehmen" &&
                entries[2].DropDownItems.Count == 3 && entries[3].Text == "Text erkennen…" && entries[4].Text == "Letzte Aufnahme fortsetzen" &&
                Equals(entries[5].Tag, "Pin") && entries[6].Text == "Zuletzt gespeichert" && entries[7].Text!.StartsWith("OCR:", StringComparison.Ordinal) && entries[8].Text == "Einstellungen…" && entries[9].Text == "Beenden", "Tray menu must expose all requested actions in order.");
            Require(!entries[5].Available, "Close pins entry must be hidden with no pins.");
            tray.Refresh(false, true, 2);
            Require(entries[5].Available && entries[5].Text == "Angepinnte Bilder schließen (2)", "Close pins entry must appear with the live count.");
            tray.Refresh(false, false); Require(!entries[4].Enabled && entries.Take(4).All(i => i.Enabled), "Resume must be disabled without a session.");
            tray.Refresh(true, true); Require(entries.Take(5).All(i => !i.Enabled) && entries.Skip(5).All(i => i.Enabled), "Capture entries must disable during active capture.");
            tray.Refresh(false, true); Require(entries.Take(5).All(i => i.Enabled) && entries.All(i => i.Image != null), "Idle tray must expose resumed session and glyph icons.");
            using var icon = TrayMenu.RenderGlyph(settings, "Capture", 40);
            Require(icon.Width == 40 && icon.Height == 40, "Tray glyphs must render at requested DPI resolution.");
            Require(!tray.Menu.Visible && !settings.IsVisible, "Tray checks must not display menu or settings.");
            return Array.Empty<string>();
        }
        finally { settings.Close(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new SelfCheckFailure(message);
    }

    private static void RenderOverlayPreview(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BitmapSource bitmap = OverlaySelfChecks.RenderPreview(CreateDemoBitmap());
        File.WriteAllBytes(path, ImageOutput.EncodePng(bitmap));
        WriteJson(Path.ChangeExtension(path, ".json"), new { schemaVersion = 1, rendered = true,
            width = bitmap.PixelWidth, height = bitmap.PixelHeight,
            mode = "WPF live overlay / synthetic local image / offscreen only / no desktop capture" });
    }

    private static void RenderPinPreview(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BitmapSource demo = CreateDemoBitmap();
        var editor = new AnnotationCanvas(demo) { Tool = EditorTool.Rectangle, Stroke = Brushes.MediumPurple };
        editor.BeginGesture(new Point(300, 250)); editor.UpdateGesture(new Point(1010, 395)); editor.EndGesture();
        var region = new Int32Rect(280, 235, 800, 280);
        var pin = new PinWindow(editor.Flatten(region), default, region, new AppSettings());
        try
        {
            var canvas = new Canvas { Width = 980, Height = 460, Background = Brush("#DAD9DF") };
            FrameworkElement content = (FrameworkElement)pin.Content;
            pin.Content = null;
            content.Width = region.Width + 2 * PinWindow.ShadowPixels; content.Height = region.Height + 2 * PinWindow.ShadowPixels;
            Canvas.SetLeft(content, 78); Canvas.SetTop(content, 78); canvas.Children.Add(content);
            canvas.Measure(new Size(980, 460)); canvas.Arrange(new Rect(0, 0, 980, 460)); canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(980, 460, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            File.WriteAllBytes(path, ImageOutput.EncodePng(bitmap));
            WriteJson(Path.ChangeExtension(path, ".json"), new { rendered = true, width = 980, height = 460,
                mode = "WPF pin with separate shadow / flattened synthetic crop / offscreen only" });
        }
        finally { pin.Close(); }
    }

    internal static void RenderPreview(string path)
    {
        var window = new MainWindow(diagnosticsMode: true);
        try { RenderWindow(window, path, 560, 760); }
        finally { window.Close(); }
    }
    private static void RenderOcrPreview(string path)
    {
        var settings = new MainWindow(diagnosticsMode: true);
        var window = new OcrResultWindow(settings, CreateDemoBitmap(), default, default, diagnostics: true);
        try { window.SetPreviewResult(); RenderWindow(window, path, 440, 350); }
        finally { window.Close(); settings.Close(); }
    }
    private static void RenderTrayPreview(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = new MainWindow(diagnosticsMode: true);
        using var tray = new TrayMenu(settings, _ => { });
        try
        {
            tray.Refresh(false, true);
            tray.Menu.PerformLayout();
            System.Drawing.Size size = tray.Menu.GetPreferredSize(System.Drawing.Size.Empty);
            tray.Menu.Size = size;
            using var bitmap = new System.Drawing.Bitmap(size.Width, size.Height);
            tray.Menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            WriteJson(Path.ChangeExtension(path, ".json"), new { rendered = true, mode = "WinForms tray menu / offscreen / no NotifyIcon", width = size.Width, height = size.Height });
        }
        finally { settings.Close(); }
    }
    private static void RenderWindow(Window window, string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FrameworkElement content = LayoutPreview(window, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext dc = background.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(content);
        File.WriteAllBytes(path, ImageOutput.EncodePng(bitmap));
        WriteJson(Path.ChangeExtension(path, ".json"), new { rendered = true, width, height, mode = "WPF / isolated offscreen diagnostics / native DWM backdrop requires visible Windows window" });
    }
    private static FrameworkElement LayoutPreview(Window window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        return content;
    }

    private static void WriteJson(string path, object value) => File.WriteAllText(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    internal static BitmapSource CreateDemoBitmap()
    {
        const int width = 1280, height = 800;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            Brush ink = Brush("#252733"), muted = Brush("#737481"), purple = Brush("#7D4BE3");
            drawing.DrawRectangle(Brush("#FAF9F6"), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(Brush("#EFEDF2"), null, new Rect(0, 0, 246, height));
            drawing.DrawRectangle(Brush("#E6E1ED"), null, new Rect(245, 0, 1, height));
            Text(drawing, "K", 40, 35, 31, purple, true);
            Text(drawing, "STUDIO NOTES", 84, 45, 15, ink, true);
            Text(drawing, "ARBEITSBEREICH", 36, 131, 12, muted, true);
            drawing.DrawRoundedRectangle(Brush("#E1D7F4"), null, new Rect(24, 164, 198, 46), 7, 7);
            Text(drawing, "Projektübersicht", 42, 176, 16, Brush("#431B96"), true);
            Text(drawing, "Notizen", 42, 236, 16, muted);
            Text(drawing, "Entwürfe", 42, 290, 16, muted);
            Text(drawing, "Archiv", 42, 344, 16, muted);
            Text(drawing, "Lokales Beispieldokument", 33, 731, 12, muted);
            Text(drawing, "PROJEKT  /  START", 306, 52, 12, purple, true);
            Text(drawing, "Klarheit im nächsten Ausschnitt", 306, 109, 36, ink, true);
            Text(drawing, "Eine gute Idee beginnt mit dem, was du siehst.", 308, 171, 20, muted);
            drawing.DrawLine(new Pen(Brush("#DDD9E2"), 1), new Point(306, 223), new Point(1208, 223));
            Text(drawing, "Festhalten. Markieren. Weitergeben.", 307, 264, 26, ink, true);
            Text(drawing, "01", 309, 331, 14, purple, true);
            Text(drawing, "Den richtigen Bereich auswählen", 352, 326, 20, ink, true);
            Text(drawing, "Genau den Inhalt aufnehmen, der für dein Gegenüber zählt.", 352, 361, 17, muted);
            Text(drawing, "02", 309, 428, 14, purple, true);
            Text(drawing, "Das Wesentliche sichtbar machen", 352, 423, 20, ink, true);
            Text(drawing, "Mit Pfeilen, Formen und kurzen Notizen direkt im Bild.", 352, 458, 17, muted);
            drawing.DrawRoundedRectangle(Brush("#F0E9DD"), null, new Rect(306, 541, 902, 133), 10, 10);
            drawing.DrawRectangle(Brush("#D3B576"), null, new Rect(306, 541, 4, 133));
            Text(drawing, "Ein Gedanke für den nächsten Schritt", 333, 565, 19, ink, true);
            Text(drawing, "Ein klarer Ausschnitt spart eine lange Erklärung.", 333, 604, 18, muted);
            Text(drawing, "BEISPIELBILD · OHNE BILDSCHIRMAUFNAHME ERZEUGT", 307, 735, 11, muted);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Brush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    private static void Text(DrawingContext drawing, string text, double x, double y, double size, Brush brush, bool bold = false) =>
        drawing.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("de-DE"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStretches.Normal), size, brush, 1), new Point(x, y));
}
