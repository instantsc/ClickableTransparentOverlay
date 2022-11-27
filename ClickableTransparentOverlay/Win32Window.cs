using System;
using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Microsoft.Win32.SafeHandles;

namespace ClickableTransparentOverlay;

internal sealed class Win32Window : IDisposable
{
    public HWND Handle;
    public Rectangle Dimensions;

    public unsafe Win32Window(string wndClass, int width, int height, int x, int y, string title, WINDOW_STYLE style, WINDOW_EX_STYLE exStyle)
    {
        Dimensions = new Rectangle(x, y, width, height);
        Handle = PInvoke.CreateWindowEx(exStyle, wndClass, title, style,
            Dimensions.X, Dimensions.Y, Dimensions.Width, Dimensions.Height, HWND.Null, new SafeFileHandle(IntPtr.Zero, false), new SafeFileHandle(IntPtr.Zero, false), default);
    }

    public void PumpEvents()
    {
        if (PInvoke.PeekMessage(out var msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero && PInvoke.DestroyWindow(Handle))
        {
            Handle = HWND.Null;
        }
    }
}
