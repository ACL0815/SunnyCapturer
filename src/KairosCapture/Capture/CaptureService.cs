using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KairosCapture.Capture;

public static class CaptureService
{
    private static int _capturing;

    internal static bool IsCapturing => Volatile.Read(ref _capturing) != 0;
    internal static event EventHandler? CaptureStateChanged;

    internal static CaptureSessionResult? LastSession { get; private set; }

    internal static void Remember(CaptureSessionResult result)
    {
        if (result.Completion is CaptureCompletion.Copied or CaptureCompletion.Saved or CaptureCompletion.Ocr or CaptureCompletion.Pinned && result.Editor != null)
            LastSession = result;
    }

    internal static async Task<CaptureSessionResult> CaptureEditedAsync(Window owner, AppSettings settings, CaptureMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            return new(CaptureCompletion.Cancelled);

        Visibility visibility = owner.Visibility;
        WindowState state = owner.WindowState;
        bool ownerClosed = false;
        IntPtr previousForeground = GetForegroundWindow();
        RegionOverlay? overlay = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnOwnerClosed(object? sender, EventArgs args)
        {
            ownerClosed = true;
            lifetime.Cancel();
            overlay?.Cancel();
        }
        owner.Closed += OnOwnerClosed;
        try
        {
            CaptureStateChanged?.Invoke(null, EventArgs.Empty);
            owner.Hide();
            if (visibility == Visibility.Visible) await Task.Delay(100, lifetime.Token);
            if (ownerClosed)
                return new(CaptureCompletion.Cancelled);

            lifetime.Token.ThrowIfCancellationRequested();
            // A tray action may have shown the owner during the delay. Hide again without
            // yielding, then wait for this application's queued compositor work to finish.
            owner.Hide();
            Marshal.ThrowExceptionForHR(DwmFlush());
            CaptureSessionResult? previous = mode == CaptureMode.Resume ? LastSession : null;
            if (mode == CaptureMode.Resume && previous == null) return new(CaptureCompletion.Cancelled);
            Rectangle bounds = previous?.Bounds ?? GetVirtualBounds();
            BitmapSource screenshot = previous?.Screenshot ?? CaptureDesktop(bounds);
            lifetime.Token.ThrowIfCancellationRequested();
            overlay = new RegionOverlay(screenshot, bounds, settings: settings, ocrMode: mode == CaptureMode.Ocr, editor: previous?.Editor) { Owner = owner };
            if (previous != null) overlay.SetRegion(previous.Region);
            else if (mode == CaptureMode.Fullscreen)
            {
                var monitor = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
                Rectangle selected = Rectangle.Intersect(bounds, monitor);
                overlay.SetRegion(new Int32Rect(selected.X - bounds.X, selected.Y - bounds.Y, selected.Width, selected.Height));
            }
            using CancellationTokenRegistration cancelSelection = lifetime.Token.Register(() =>
            {
                if (!owner.Dispatcher.HasShutdownStarted)
                    owner.Dispatcher.BeginInvoke(new Action(() => overlay.Cancel()));
            });
            lifetime.Token.ThrowIfCancellationRequested();
            overlay.ShowDialog();
            lifetime.Token.ThrowIfCancellationRequested();
            if (ownerClosed)
                return new(CaptureCompletion.Cancelled);
            if (overlay.Failure is not null)
                throw overlay.Failure;
            Remember(overlay.Result);
            return overlay.Result;
        }
        catch (OperationCanceledException) when (ownerClosed && !cancellationToken.IsCancellationRequested)
        {
            return new(CaptureCompletion.Cancelled);
        }
        finally
        {
            try
            {
                overlay?.Cancel();
                owner.Closed -= OnOwnerClosed;
                if (!ownerClosed)
                {
                    owner.WindowState = state;
                    if (overlay?.Result.Completion is CaptureCompletion.Copied or CaptureCompletion.Saved or CaptureCompletion.Ocr or CaptureCompletion.Pinned)
                    {
                        owner.Hide();
                    }
                    else if (visibility == Visibility.Visible)
                    {
                        owner.Show();
                        owner.Activate();
                    }
                    else
                    {
                        owner.Visibility = visibility;
                    }
                }
                // The overlay activated itself. Return focus for a pin or an explicit copy-and-return request.
                if ((overlay?.Result.Completion == CaptureCompletion.Pinned || overlay?.Result.ReturnToPreviousForeground == true) && previousForeground != IntPtr.Zero && IsWindow(previousForeground))
                    SetForegroundWindow(previousForeground);
            }
            finally
            {
                Interlocked.Exchange(ref _capturing, 0);
                CaptureStateChanged?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    // Requires the shell's PerMonitorV2 manifest: these metrics are physical pixels.
    private static Rectangle GetVirtualBounds()
    {
        if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-4)))
            throw new InvalidOperationException("Die Aufnahme benötigt die PerMonitorV2-DPI-Einstellung des Anwendungsmanifests.");
        var bounds = new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77),
            GetSystemMetrics(78), GetSystemMetrics(79));
        CaptureGeometry.ValidateSize(bounds.Width, bounds.Height);
        return bounds;
    }

    private static BitmapSource CaptureDesktop(Rectangle bounds)
    {
        CaptureGeometry.ValidateSize(bounds.Width, bounds.Height);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            // Normal desktop capture only; protected content may remain black.
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        BitmapData bits = bitmap.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height),
            ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            BitmapSource image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96,
                PixelFormats.Bgr32, null, bits.Scan0, checked(bits.Stride * bounds.Height), bits.Stride);
            image.Freeze();
            return image;
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}

internal static class CaptureGeometry
{
    internal static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > 64L * 1024 * 1024)
            throw new InvalidOperationException($"Das Bildraster ({width} × {height}) ist ungültig oder zu groß. " +
                "Unterstützt werden maximal 32768 Pixel pro Seite und insgesamt 64 Megapixel (67108864 Pixel).");
    }
}
