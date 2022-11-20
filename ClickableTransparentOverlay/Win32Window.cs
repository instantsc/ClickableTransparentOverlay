using System;
using System.Drawing;
using Vanara.PInvoke;

namespace ClickableTransparentOverlay;

internal sealed class Win32Window : IDisposable
{
    public IntPtr Handle;
    public Rectangle Dimensions;

    public Win32Window(string wndClass, int width, int height, int x, int y, string title, User32.WindowStyles style, User32.WindowStylesEx exStyle)
    {
        Dimensions = new Rectangle(x, y, width, height);
        Handle = User32.CreateWindowEx(exStyle, wndClass, title, style,
            Dimensions.X, Dimensions.Y, Dimensions.Width, Dimensions.Height).ReleaseOwnership();
    }

    public void PumpEvents()
    {
        if (User32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, User32.PM.PM_REMOVE))
        {
            User32.TranslateMessage(in msg);
            User32.DispatchMessage(in msg);
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero && User32.DestroyWindow(Handle))
        {
            Handle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
