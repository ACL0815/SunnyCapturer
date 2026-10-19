using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace KairosCapture.Capture;

/// <summary>Owns a configurable hotkey on the owner's Dispatcher.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private static int _nextId;
    private readonly Window _owner;
    private readonly Action _onPressed;
    private int _id;
    private HotkeyGesture _gesture = new(System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.A);
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;
    private bool _requested;
    private bool _nativeEnabled = true;
    private bool _disposed;

    public string? LastError { get; private set; }

    /// <summary>Raised on the owner's Dispatcher when a suspended hotkey cannot be restored.</summary>
    public event Action<string>? RegistrationFailed;

    public GlobalHotkey(Window owner, Action onPressed, string hotkey = "Strg+Alt+A")
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(onPressed);
        owner.Dispatcher.VerifyAccess();
        if (HotkeyGesture.TryParse(hotkey, out var gesture)) _gesture = gesture;
        _owner = owner;
        _onPressed = onPressed;
        _id = Interlocked.Increment(ref _nextId);
        if (_id > 0xBFFF)
            throw new InvalidOperationException("Zu viele Hotkey-Instanzen wurden erzeugt.");
        _owner.Closed += OnOwnerClosed;
        _owner.IsEnabledChanged += OnOwnerIsEnabledChanged;
        CaptureService.CaptureStateChanged += OnCaptureStateChanged;
    }

    public bool Register(out string? error)
    {
        _owner.Dispatcher.VerifyAccess();
        error = LastError = null;
        if (_disposed)
        {
            error = LastError = "Das Hotkey-Objekt oder sein Fenster wurde bereits geschlossen.";
            return false;
        }
        if (_registered)
            return true;

        try
        {
            if (_source is null)
            {
                // EnsureHandle also supports registration before the window's first Show().
                _handle = new WindowInteropHelper(_owner).EnsureHandle();
                _source = HwndSource.FromHwnd(_handle)
                    ?? throw new InvalidOperationException("Das Fenster besitzt keine Windows-Nachrichtenquelle.");
                _nativeEnabled = IsWindowEnabled(_handle);
                _source.AddHook(OnWindowMessage);
            }
            _requested = true;
            return UpdateRegistration(out error);
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is Win32Exception)
        {
            ReleaseRegistration();
            if (_source is not null && !_source.IsDisposed)
                _source.RemoveHook(OnWindowMessage);
            _source = null;
            _requested = false;
            error = LastError = $"{_gesture} konnte nicht registriert werden: {exception.Message}";
            return false;
        }
    }

    internal bool TryChange(HotkeyGesture gesture, out string? error)
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) { error = "Der Hotkey wurde bereits geschlossen."; return false; }
        if (gesture == _gesture) return Register(out error);
        if (_source is null) Register(out _);
        if (_source is null || !_owner.IsEnabled || !_nativeEnabled || CaptureService.IsCapturing)
        { error = "Das Tastenkürzel kann nach der laufenden Aufnahme oder dem Dialog geändert werden."; return false; }
        int candidate = Interlocked.Increment(ref _nextId);
        if (candidate > 0xBFFF) { error = "Zu viele Tastenkürzeländerungen."; return false; }
        // Reserve the new combination before releasing the old one. A conflict never
        // interrupts the existing shortcut or steals a different application's shortcut.
        if (!RegisterHotKey(_handle, candidate, gesture.NativeModifiers, gesture.VirtualKey))
        { error = $"{gesture} ist bereits belegt oder nicht verfügbar. Das bisherige Tastenkürzel bleibt aktiv."; return false; }
        ReleaseRegistration();
        _id = candidate;
        _gesture = gesture;
        _registered = _requested = true;
        error = LastError = null;
        return true;
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x000A && !_disposed) // WM_ENABLE: modal dialogs use native EnableWindow, not WPF IsEnabled.
        {
            _nativeEnabled = wParam != IntPtr.Zero;
            RefreshRegistration();
        }
        if (message == 0x0312 && wParam.ToInt64() == _id && _registered && !_disposed &&
            _owner.IsEnabled && _nativeEnabled && !CaptureService.IsCapturing &&
            (_handle == IntPtr.Zero || IsWindowEnabled(_handle))) // WM_HOTKEY
        {
            handled = true;
            _onPressed();
        }
        return IntPtr.Zero;
    }

    private bool UpdateRegistration(out string? error)
    {
        error = null;
        if (_disposed || !_requested)
            return true;
        if (!_owner.IsEnabled || !_nativeEnabled || CaptureService.IsCapturing)
        {
            // Ignoring WM_HOTKEY alone is insufficient: RegisterHotKey consumes the
            // combination system-wide. Release it for the entire blocked interval.
            ReleaseRegistration();
            return true;
        }
        if (_registered)
            return true;
        if (!RegisterHotKey(_handle, _id, _gesture.NativeModifiers, _gesture.VirtualKey))
        {
            int code = Marshal.GetLastWin32Error();
            error = LastError = $"{_gesture} konnte nicht registriert werden; die Kombination ist möglicherweise bereits belegt. " +
                $"Windows-Fehler {code}: {new Win32Exception(code).Message}";
            return false;
        }
        LastError = null;
        _registered = true;
        return true;
    }

    private void ReleaseRegistration()
    {
        if (_registered)
            UnregisterHotKey(_handle, _id);
        _registered = false;
    }

    private void RefreshRegistration()
    {
        if (!UpdateRegistration(out string? error))
            RegistrationFailed?.Invoke(error!);
    }

    private void OnOwnerIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => RefreshRegistration();

    private void OnCaptureStateChanged(object? sender, EventArgs args)
    {
        if (_owner.Dispatcher.CheckAccess())
            RefreshRegistration();
        else if (!_owner.Dispatcher.HasShutdownStarted)
            _owner.Dispatcher.BeginInvoke(new Action(RefreshRegistration));
    }

    private void OnOwnerClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed)
            return;
        _disposed = true;
        _owner.Closed -= OnOwnerClosed;
        _owner.IsEnabledChanged -= OnOwnerIsEnabledChanged;
        CaptureService.CaptureStateChanged -= OnCaptureStateChanged;
        _requested = false;
        ReleaseRegistration();
        if (_source is not null && !_source.IsDisposed)
            _source.RemoveHook(OnWindowMessage);
        _registered = false;
        _source = null;
        _handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
}
