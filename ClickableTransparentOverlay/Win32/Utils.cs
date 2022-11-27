using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ClickableTransparentOverlay.Win32;

public static class Utils
{
    public static int Loword(int number) => number & 0x0000FFFF;
    public static int Hiword(int number) => number >> 16;

    /// <summary>
    /// Gets a value indicating whether the overlay is clickable or not.
    /// </summary>
    internal static bool IsClickable { get; private set; } = true;

    private static WINDOW_EX_STYLE _clickable = 0;
    private static WINDOW_EX_STYLE _notClickable = 0;

    private static readonly Stopwatch sw = Stopwatch.StartNew();
    private static readonly long[] NVirtKeyTimeouts = new long[256]; // Total VirtKeys are 256.

    /// <summary>
    /// Returns true if the key is pressed.
    /// For keycode information visit: https://www.pinvoke.net/default.aspx/user32.getkeystate.
    ///
    /// This function can return True multiple times (in multiple calls) per keypress. It
    /// depends on how long the application user pressed the key for and how many times
    /// caller called this function while the key was pressed. Caller of this function is
    /// responsible to mitigate this behaviour.
    /// </summary>
    /// <param name="nVirtKey">key code to look.</param>
    /// <returns>weather the key is pressed or not.</returns>
    public static bool IsKeyPressed(VK nVirtKey)
    {
        return (PInvoke.GetKeyState((int)nVirtKey) & 0x8000) != 0;
    }

    /// <summary>
    /// A wrapper function around <see cref="IsKeyPressed"/> to ensure a single key-press
    /// yield single true even if the function is called multiple times.
    ///
    /// This function might miss a key-press, which may degrade the user-experience,
    /// so use this function to the minimum e.g. just to enable/disable/show/hide the overlay.
    /// And, it would be nice to allow application user to configure the timeout value to
    /// their liking.
    /// </summary>
    /// <param name="nVirtKey">key to look for, for details read <see cref="IsKeyPressed"/> description.</param>
    /// <param name="timeout">timeout in milliseconds</param>
    /// <returns>true if the key is pressed and key is not in timeout.</returns>
    public static bool IsKeyPressedAndNotTimeout(VK nVirtKey, int timeout = 200)
    {
        var actual = IsKeyPressed(nVirtKey);
        var currTime = sw.ElapsedMilliseconds;
        if (actual && currTime > NVirtKeyTimeouts[(int)nVirtKey])
        {
            NVirtKeyTimeouts[(int)nVirtKey] = currTime + timeout;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Allows the window to become transparent.
    /// </summary>
    /// <param name="handle">
    /// Window native pointer.
    /// </param>
    internal static void InitTransparency(HWND handle)
    {
        _clickable = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        _notClickable = _clickable | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        PInvoke.DwmExtendFrameIntoClientArea(handle, new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyBottomHeight = -1, cyTopHeight = -1 });
        SetOverlayClickable(handle, false);
    }

    /// <summary>
    /// Enables (clickable) / Disables (not clickable) the Window keyboard/mouse inputs.
    /// NOTE: This function depends on InitTransparency being called when the Window was created.
    /// </summary>
    /// <param name="handle">Veldrid window handle in IntPtr format.</param>
    /// <param name="wantClickable">Set to true if you want to make the window clickable otherwise false.</param>
    internal static bool? SetOverlayClickable(HWND handle, bool wantClickable)
    {
        if (IsClickable ^ wantClickable)
        {
            if (wantClickable)
            {
                PInvoke.SetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)_clickable);
                PInvoke.SetFocus(handle);
            }
            else
            {
                PInvoke.SetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)_notClickable);
            }

            return IsClickable = wantClickable;
        }

        return null;
    }
}
