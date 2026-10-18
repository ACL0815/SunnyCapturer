using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace KairosCapture.Capture;

/// <summary>Owns Ctrl+Alt+A on the owner's Dispatcher; register and dispose on that thread.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private static int _nextId;
    private readonly Window _owner;
    private readonly Action _onPressed;
    private readonly int _id;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkey(Window owner, Action onPressed)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(onPressed);
        owner.Dispatcher.VerifyAccess();
        _owner = owner;
        _onPressed = onPressed;
        _id = Interlocked.Increment(ref _nextId);
        if (_id > 0xBFFF)
            throw new InvalidOperationException("Zu viele Hotkey-Instanzen wurden erzeugt.");
        _owner.Closed += OnOwnerClosed;
    }

    public bool Register(out string? error)
    {
        _owner.Dispatcher.VerifyAccess();
        error = null;
        if (_disposed)
        {
            error = "Das Hotkey-Objekt oder sein Fenster wurde bereits geschlossen.";
            return false;
        }
        if (_registered)
            return true;

        try
        {
            // EnsureHandle also supports registration before the window's first Show().
            _handle = new WindowInteropHelper(_owner).EnsureHandle();
            _source = HwndSource.FromHwnd(_handle)
                ?? throw new InvalidOperationException("Das Fenster besitzt keine Windows-Nachrichtenquelle.");
            if (!RegisterHotKey(_handle, _id, 0x4003, 0x41)) // MOD_NOREPEAT | MOD_CONTROL | MOD_ALT, A
            {
                int code = Marshal.GetLastWin32Error();
                error = $"Ctrl+Alt+A konnte nicht registriert werden; die Kombination ist möglicherweise bereits belegt. " +
                    $"Windows-Fehler {code}: {new Win32Exception(code).Message}";
                _source = null;
                return false;
            }
            _source.AddHook(OnWindowMessage);
            _registered = true;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is Win32Exception)
        {
            UnregisterHotKey(_handle, _id);
            _source = null;
            error = $"Ctrl+Alt+A konnte nicht registriert werden: {exception.Message}";
            return false;
        }
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt64() == _id && _registered && !_disposed) // WM_HOTKEY
        {
            handled = true;
            _onPressed();
        }
        return IntPtr.Zero;
    }

    private void OnOwnerClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed)
            return;
        _disposed = true;
        _owner.Closed -= OnOwnerClosed;
        if (_registered)
            UnregisterHotKey(_handle, _id);
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
}
