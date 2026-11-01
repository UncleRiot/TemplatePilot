using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using TemplatePilot.Interop;
using WinRT;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace TemplatePilot.Views;

// Semantic Anchor: SearchPopupCompositionBackdropHost
// SSOT: SearchPopupWindow owns one native, non-activating composition underlay while Glass
// blur is enabled. The OS compositor samples the live desktop; no capture loops exist.
// Invariants: the underlay stays behind the interactive WPF HWND, shares its pixel bounds,
// never steals focus or input, and is destroyed when the popup closes or blur is disabled.
internal sealed class SearchPopupCompositionBackdrop : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoRedirectionBitmap = 0x00200000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpShowWindow = 0x0040;
    private const int DwmwaUseHostBackdropBrush = 17;
    private const int SwHide = 0;

    // Creating multiple DispatcherQueueControllers on the same WPF UI thread is invalid.
    // Keep the controller for the lifetime of the UI thread, shared by popup instances.
    private static IntPtr s_dispatcherQueueController;

    private IntPtr _window;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private SpriteVisual? _sprite;
    private CompositionBackdropBrush? _brush;
    private CompositionGeometricClip? _roundedClip;
    private CompositionRoundedRectangleGeometry? _roundedGeometry;

    private SearchPopupCompositionBackdrop(IntPtr foreground)
    {
        try
        {
            EnsureDispatcherQueue();
            _window = CreateWindowExW(
                WsExTransparent | WsExToolWindow | WsExNoRedirectionBitmap | WsExNoActivate,
                "STATIC", string.Empty, WsPopup, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            int enabled = 1;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(
                _window, DwmwaUseHostBackdropBrush, ref enabled, sizeof(int)));

            _compositor = new Compositor();
            // C#/WinRT projected objects cannot be directly cast to ComImport interfaces.
            // As<T> performs the native QueryInterface without a WinRT RCW cast.
            var interop = _compositor.As<ICompositorDesktopInterop>();
            Marshal.ThrowExceptionForHR(
                interop.CreateDesktopWindowTarget(_window, false, out IntPtr targetAbi));
            if (targetAbi == IntPtr.Zero)
            {
                throw new COMException("CreateDesktopWindowTarget returned no target.");
            }

            try
            {
                _target = MarshalInterface<DesktopWindowTarget>.FromAbi(targetAbi)
                    ?? throw new COMException("Could not marshal DesktopWindowTarget.");
            }
            finally
            {
                Marshal.Release(targetAbi);
            }

            _root = _compositor.CreateContainerVisual();
            _sprite = _compositor.CreateSpriteVisual();
            _brush = _compositor.CreateHostBackdropBrush();
            _sprite.Brush = _brush;
            // Match the popup's rounded surface without drawing a square backdrop
            // through the transparent WPF corners. Geometry uses physical pixels.
            _roundedGeometry = _compositor.CreateRoundedRectangleGeometry();
            _roundedClip = _compositor.CreateGeometricClip(_roundedGeometry);
            _sprite.Clip = _roundedClip;
            _root.Children.InsertAtTop(_sprite);
            _target.Root = _root;
            Sync(foreground);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static SearchPopupCompositionBackdrop Create(IntPtr foreground)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            throw new PlatformNotSupportedException("Host backdrop requires Windows 11.");
        }

        return new SearchPopupCompositionBackdrop(foreground);
    }

    // Semantic Anchor: ShortcutBlurStrengthSSOT
    // The existing stored 0..32 value controls the mix of the system-provided GPU blur.
    // HostBackdropBrush does not expose a configurable Gaussian radius; do not imply
    // that this changes the system's actual blur kernel.
    public void SetStrength(int value)
    {
        if (_sprite is not null)
        {
            _sprite.Opacity = Math.Clamp(value, 0, 32) / 32f;
        }
    }

    // Native window-position messages are received even while WPF DragMove is in its
    // modal move/size loop. Synchronization never waits for a DispatcherTimer tick.
    public void Sync(IntPtr foreground)
    {
        if (_window == IntPtr.Zero || _sprite is null)
        {
            return;
        }

        if (!User32WindowInterop.GetWindowRect(foreground, out Win32Rect rect)
            || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        _sprite.Size = new Vector2(rect.Width, rect.Height);
        if (_roundedGeometry is not null)
        {
            _roundedGeometry.Size = new Vector2(rect.Width, rect.Height);
            _roundedGeometry.CornerRadius = new Vector2(8, 8);
        }
        if (!SetWindowPos(_window, foreground, rect.Left, rect.Top, rect.Width, rect.Height,
                SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Hide()
    {
        if (_window != IntPtr.Zero)
        {
            ShowWindow(_window, SwHide);
        }
    }

    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
        _sprite?.Dispose();
        _sprite = null;
        _root?.Dispose();
        _root = null;
        _brush?.Dispose();
        _brush = null;
        _roundedClip?.Dispose();
        _roundedClip = null;
        _roundedGeometry?.Dispose();
        _roundedGeometry = null;
        _compositor?.Dispose();
        _compositor = null;
        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }

    private static void EnsureDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
        {
            return;
        }

        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2, // DQTYPE_THREAD_CURRENT
            ApartmentType = 2 // DQTAT_COM_STA; WPF UI thread is STA
        };
        int result = CreateDispatcherQueueController(options, out var queue);
        Marshal.ThrowExceptionForHR(result);
        s_dispatcherQueueController = queue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("coremessaging.dll", ExactSpelling = true)]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}

// Semantic Anchor: ShortcutNativeCompositionInterop
// .NET 8 C#/WinRT cannot cast projected WinRT wrappers through ComImport RCWs.
// SSOT: this generated IUnknown contract matches Windows.UI.Composition.Interop.h.
[GeneratedComInterface]
[Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
internal partial interface ICompositorDesktopInterop
{
    [PreserveSig]
    int CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
}
