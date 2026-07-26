using System.Windows.Automation;
using TemplatePilot.Interop;
using TemplatePilot.Models;
using System.Diagnostics;
using System.Windows;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;
using TemplatePilot.Helpers;

namespace TemplatePilot.Services;

public sealed class FocusValidationService
{
    private readonly ForegroundWindowService _foregroundWindowService;
    private readonly LoggingService _logging;

    public FocusValidationService(ForegroundWindowService foregroundWindowService, LoggingService logging)
    {
        _foregroundWindowService = foregroundWindowService;
        _logging = logging;
    }

    public bool IsValidTarget(WindowTargetContext target)
    {
        if (!target.IsValid)
        {
            return false;
        }

        if (!User32WindowInterop.IsWindow(target.ForegroundWindow))
        {
            return false;
        }

        if (_foregroundWindowService.IsOwnProcessWindow(target))
        {
            return false;
        }

        if (!IsDefaultInputDesktop())
        {
            _logging.Warning("Input desktop is not the default desktop.");
            return false;
        }

        return true;
    }

    public bool IsTargetStillValid(WindowTargetContext target)
    {
        return _foregroundWindowService.IsForegroundUnchanged(target) && IsValidTarget(target);
    }

    private static bool IsDefaultInputDesktop()
    {
        var name = User32DesktopInterop.GetInputDesktopName();
        return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
    }

}

public sealed class ForegroundWindowService
{
    public WindowTargetContext CaptureForeground()
    {
        var foreground = User32WindowInterop.GetForegroundWindow();

        if (foreground == IntPtr.Zero)
        {
            return new WindowTargetContext(IntPtr.Zero, IntPtr.Zero, 0, DateTime.UtcNow);
        }

        var threadId = User32WindowInterop.GetWindowThreadProcessId(foreground, out _);
        return new WindowTargetContext(foreground, IntPtr.Zero, threadId, DateTime.UtcNow);
    }

    public bool IsForegroundUnchanged(WindowTargetContext target)
    {
        return target.IsValid && User32WindowInterop.GetForegroundWindow() == target.ForegroundWindow;
    }

    public bool TryRestore(WindowTargetContext target)
    {
        return target.IsValid && User32WindowInterop.IsWindow(target.ForegroundWindow) && User32WindowInterop.SetForegroundWindow(target.ForegroundWindow);
    }

    public Rect GetSuggestedPopupRect(WindowTargetContext target)
    {
        if (target.IsValid && User32WindowInterop.GetWindowRect(target.ForegroundWindow, out var rect))
        {
            return new Rect(rect.Left + Math.Max(0, rect.Width - 480) / 2, rect.Top + Math.Max(0, rect.Height - 340) / 3, 480, 340);
        }

        var area = SystemParameters.WorkArea;
        return new Rect(area.Left + (area.Width - 480) / 2, area.Top + (area.Height - 340) / 3, 480, 340);
    }

    public bool IsOwnProcessWindow(WindowTargetContext target)
    {
        if (!target.IsValid)
        {
            return false;
        }

        User32WindowInterop.GetWindowThreadProcessId(target.ForegroundWindow, out var processId);
        return processId == Environment.ProcessId;
    }
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x544D;
    private readonly LoggingService _logging;
    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? HotkeyPressed;

    public GlobalHotkeyService(LoggingService logging)
    {
        _logging = logging;
    }

    public void Register(HotkeySettings settings)
    {
        EnsureSource();
        Unregister();

        var modifiers = ToModifiers(settings);
        var virtualKey = ToVirtualKey(settings.Key);

        if (!User32HotkeyInterop.RegisterHotKey(_source!.Handle, HotkeyId, modifiers, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "RegisterHotKey failed.");
        }

        _registered = true;
        _logging.Info($"Global hotkey registered: {settings.ToDisplayString()}.");
    }

    public void Reload(HotkeySettings settings)
    {
        Register(settings);
    }

    public void Unregister()
    {
        if (!_registered || _source is null)
        {
            return;
        }

        User32HotkeyInterop.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        _logging.Info("Global hotkey unregistered.");
    }

    private void EnsureSource()
    {
        if (_source is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("TemplatePilotHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32HotkeyInterop.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private static uint ToModifiers(HotkeySettings settings)
    {
        uint modifiers = User32HotkeyInterop.ModNoRepeat;

        if (settings.Control)
        {
            modifiers |= User32HotkeyInterop.ModControl;
        }

        if (settings.Shift)
        {
            modifiers |= User32HotkeyInterop.ModShift;
        }

        if (settings.Alt)
        {
            modifiers |= User32HotkeyInterop.ModAlt;
        }

        if (settings.Win)
        {
            modifiers |= User32HotkeyInterop.ModWin;
        }

        return modifiers;
    }

    private static uint ToVirtualKey(string keyText)
    {
        var normalized = keyText.Trim();

        if (normalized.Length == 1 && char.IsDigit(normalized[0]))
        {
            normalized = "D" + normalized;
        }

        if (!Enum.TryParse<Key>(normalized, ignoreCase: true, out var key))
        {
            throw new InvalidOperationException($"Unsupported hotkey key: {keyText}");
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            throw new InvalidOperationException($"Unsupported hotkey key: {keyText}");
        }

        return (uint)virtualKey;
    }

    public void Dispose()
    {
        Unregister();

        if (_source is null)
        {
            return;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}

public sealed class KeyboardInputService
{
    private const int KeyDownStateMask = 0x8000;

    public async Task<bool> WaitForHotkeyReleasedAsync(HotkeySettings settings, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var virtualKeys = GetHotkeyVirtualKeys(settings).Distinct().ToArray();
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start <= timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (virtualKeys.All(key => !IsKeyDown(key)))
            {
                return true;
            }

            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }



    public bool SendCtrlV()
    {
        return SendSequence(
            User32KeyboardInterop.KeyDown(User32KeyboardInterop.VkControl),
            User32KeyboardInterop.KeyDown(User32KeyboardInterop.VkV),
            User32KeyboardInterop.KeyUp(User32KeyboardInterop.VkV),
            User32KeyboardInterop.KeyUp(User32KeyboardInterop.VkControl));
    }

    private static IReadOnlyList<int> GetHotkeyVirtualKeys(HotkeySettings settings)
    {
        var keys = new List<int>();

        if (settings.Control)
        {
            keys.Add(User32KeyboardInterop.VkControl);
        }

        if (settings.Shift)
        {
            keys.Add(User32KeyboardInterop.VkShift);
        }

        if (settings.Alt)
        {
            keys.Add(User32KeyboardInterop.VkMenu);
        }

        if (settings.Win)
        {
            keys.Add(User32KeyboardInterop.VkLWin);
            keys.Add(User32KeyboardInterop.VkRWin);
        }

        var keyText = settings.Key.Trim();
        if (keyText.Length == 1 && char.IsDigit(keyText[0]))
        {
            keyText = "D" + keyText;
        }

        if (Enum.TryParse<Key>(keyText, ignoreCase: true, out var key))
        {
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey > 0)
            {
                keys.Add(virtualKey);
            }
        }

        return keys;
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (User32KeyboardInterop.GetAsyncKeyState(virtualKey) & KeyDownStateMask) != 0;
    }

    private static bool SendSequence(params Win32Input[] inputs)
    {
        var sent = User32KeyboardInterop.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Input>());
        return sent == inputs.Length;
    }
}
