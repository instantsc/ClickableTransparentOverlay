using Vanara.PInvoke;
using System;
using System.Diagnostics;

namespace ClickableTransparentOverlay.Win32;

public static class Utils
{
    public static int Loword(int number) => number & 0x0000FFFF;
    public static int Hiword(int number) => number >> 16;

    /// <summary>
    /// Gets a value indicating whether the overlay is clickable or not.
    /// </summary>
    internal static bool IsClickable { get; private set; } = true;

    private static User32.WindowStylesEx _clickable = 0;
    private static User32.WindowStylesEx _notClickable = 0;

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
        return (User32.GetKeyState((int)nVirtKey) & 0x8000) != 0;
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
    internal static void InitTransparency(IntPtr handle)
    {
        _clickable = (User32.WindowStylesEx)User32.GetWindowLongPtr(handle, User32.WindowLongFlags.GWL_EXSTYLE);
        _notClickable = _clickable | User32.WindowStylesEx.WS_EX_LAYERED | User32.WindowStylesEx.WS_EX_TRANSPARENT;
        var margins = new DwmApi.MARGINS(-1);
        DwmApi.DwmExtendFrameIntoClientArea(handle, in margins);
        SetOverlayClickable(handle, true);
    }

    /// <summary>
    /// Enables (clickable) / Disables (not clickable) the Window keyboard/mouse inputs.
    /// NOTE: This function depends on InitTransparency being called when the Window was created.
    /// </summary>
    /// <param name="handle">Veldrid window handle in IntPtr format.</param>
    /// <param name="wantClickable">Set to true if you want to make the window clickable otherwise false.</param>
    internal static bool? SetOverlayClickable(IntPtr handle, bool wantClickable)
    {
        if (IsClickable ^ wantClickable)
        {
            if (wantClickable)
            {
                User32.SetWindowLong(handle, User32.WindowLongFlags.GWL_EXSTYLE, (int)_clickable);
                User32.SetFocus(handle);
            }
            else
            {
                User32.SetWindowLong(handle, User32.WindowLongFlags.GWL_EXSTYLE, (int)_notClickable);
            }

            return IsClickable = wantClickable;
        }

        return null;
    }
}
