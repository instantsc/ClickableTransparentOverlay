using ImGuiNET;
using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using ClickableTransparentOverlay.Win32;

namespace ClickableTransparentOverlay;

internal class ImGuiInputHandler
{
    private readonly HWND _hwnd;
    private ImGuiMouseCursor _lastCursor;

    public ImGuiInputHandler(HWND hwnd)
    {
        _hwnd = hwnd;
    }

    public bool Update()
    {
        var io = ImGui.GetIO();
        UpdateKeyModifiers(io);
        UpdateMousePosition(io, _hwnd);
        var mouseCursor = io.MouseDrawCursor ? ImGuiMouseCursor.None : ImGui.GetMouseCursor();
        if (mouseCursor != _lastCursor)
        {
            _lastCursor = mouseCursor;

            // only required if mouse icon changes
            // while mouse isn't moved otherwise redundant.
            // so practically it's redundant.
            UpdateMouseCursor(io, mouseCursor);
        }

        return io.WantCaptureMouse;
    }

    public bool ProcessMessage(WindowMessage msg, WPARAM wParam, LPARAM lParam)
    {
        if (ImGui.GetCurrentContext() == IntPtr.Zero)
            return false;

        var io = ImGui.GetIO();
        switch (msg)
        {
            case WindowMessage.LButtonDown:
            case WindowMessage.LButtonDoubleClick:
            case WindowMessage.LButtonUp:
                io.AddMouseButtonEvent(0, msg != WindowMessage.LButtonUp);
                break;
            case WindowMessage.RButtonDown:
            case WindowMessage.RButtonDoubleClick:
            case WindowMessage.RButtonUp:
                io.AddMouseButtonEvent(1, msg != WindowMessage.RButtonUp);
                break;
            case WindowMessage.MButtonDown:
            case WindowMessage.MButtonDoubleClick:
            case WindowMessage.MButtonUp:
                io.AddMouseButtonEvent(2, msg != WindowMessage.MButtonUp);
                break;
            case WindowMessage.XButtonDown:
            case WindowMessage.XButtonDoubleClick:
            case WindowMessage.XButtonUp:
                io.AddMouseButtonEvent(
                    GET_XBUTTON_WPARAM(wParam) == 1 ? 3 : 4,
                    msg != WindowMessage.XButtonUp);
                break;
            case WindowMessage.MouseWheel:
                io.AddMouseWheelEvent(0.0f, GET_WHEEL_DELTA_WPARAM(wParam) / WheelDelta);
                break;
            case WindowMessage.MouseHWheel:
                io.AddMouseWheelEvent(GET_WHEEL_DELTA_WPARAM(wParam) / WheelDelta, 0.0f);
                break;
            case WindowMessage.KeyDown:
            case WindowMessage.SysKeyDown:
            case WindowMessage.KeyUp:
            case WindowMessage.SysKeyUp:
                var isKeyDown = msg == WindowMessage.SysKeyDown || msg == WindowMessage.KeyDown;
                if (wParam.Value < 256 && TryMapKey((VK)wParam.Value, out var imguiKey))
                {
                    io.AddKeyEvent(imguiKey, isKeyDown);
                }

                break;
            case WindowMessage.Char:
                io.AddInputCharacterUTF16((ushort)wParam.Value);
                break;
            case WindowMessage.SetCursor:
                if (Utils.Loword((int)lParam.Value) == 1)
                {
                    var mouseCursor = io.MouseDrawCursor ? ImGuiMouseCursor.None : ImGui.GetMouseCursor();
                    _lastCursor = mouseCursor;
                    if (UpdateMouseCursor(io, mouseCursor))
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    private static void UpdateMousePosition(ImGuiIOPtr io, HWND handleWindow)
    {
        if (PInvoke.GetCursorPos(out var pos) && PInvoke.ScreenToClient(handleWindow, ref pos))
        {
            io.AddMousePosEvent(pos.X, pos.Y);
        }
    }

    private static void UpdateKeyModifiers(ImGuiIOPtr io)
    {
        io.AddKeyEvent(ImGuiKey.ModCtrl, Utils.IsKeyPressed(VK.CONTROL));
        io.AddKeyEvent(ImGuiKey.ModShift, Utils.IsKeyPressed(VK.SHIFT));
        io.AddKeyEvent(ImGuiKey.ModAlt, Utils.IsKeyPressed(VK.MENU));
        io.AddKeyEvent(ImGuiKey.ModSuper, Utils.IsKeyPressed(VK.LWIN));
    }

    private static unsafe bool UpdateMouseCursor(ImGuiIOPtr io, ImGuiMouseCursor requestedcursor)
    {
        if ((io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0)
            return false;

        if (requestedcursor == ImGuiMouseCursor.None)
        {
            PInvoke.SetCursor(HCURSOR.Null);
        }
        else
        {
            var cursor = requestedcursor switch
            {
                ImGuiMouseCursor.Arrow => SystemCursor.IDC_ARROW,
                ImGuiMouseCursor.TextInput => SystemCursor.IDC_IBEAM,
                ImGuiMouseCursor.ResizeAll => SystemCursor.IDC_SIZEALL,
                ImGuiMouseCursor.ResizeEW => SystemCursor.IDC_SIZEWE,
                ImGuiMouseCursor.ResizeNS => SystemCursor.IDC_SIZENS,
                ImGuiMouseCursor.ResizeNESW => SystemCursor.IDC_SIZENESW,
                ImGuiMouseCursor.ResizeNWSE => SystemCursor.IDC_SIZENWSE,
                ImGuiMouseCursor.Hand => SystemCursor.IDC_HAND,
                ImGuiMouseCursor.NotAllowed => SystemCursor.IDC_NO,
                _ => SystemCursor.IDC_ARROW
            };

            PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, (char*)(int)cursor));
        }

        return true;
    }

    private static bool TryMapKey(VK key, out ImGuiKey result)
    {
        static ImGuiKey KeyToImGuiKeyShortcut(VK keyToConvert, VK startKey1, ImGuiKey startKey2)
        {
            var changeFromStart1 = (int)keyToConvert - (int)startKey1;
            return startKey2 + changeFromStart1;
        }

        result = key switch
        {
            >= VK.F1 and <= VK.F12 => KeyToImGuiKeyShortcut(key, VK.F1, ImGuiKey.F1),
            >= VK.NUMPAD0 and <= VK.NUMPAD9 => KeyToImGuiKeyShortcut(key, VK.NUMPAD0, ImGuiKey.Keypad0),
            >= VK.KEY_A and <= VK.KEY_Z => KeyToImGuiKeyShortcut(key, VK.KEY_A, ImGuiKey.A),
            >= VK.KEY_0 and <= VK.KEY_9 => KeyToImGuiKeyShortcut(key, VK.KEY_0, ImGuiKey._0),
            VK.TAB => ImGuiKey.Tab,
            VK.LEFT => ImGuiKey.LeftArrow,
            VK.RIGHT => ImGuiKey.RightArrow,
            VK.UP => ImGuiKey.UpArrow,
            VK.DOWN => ImGuiKey.DownArrow,
            VK.PRIOR => ImGuiKey.PageUp,
            VK.NEXT => ImGuiKey.PageDown,
            VK.HOME => ImGuiKey.Home,
            VK.END => ImGuiKey.End,
            VK.INSERT => ImGuiKey.Insert,
            VK.DELETE => ImGuiKey.Delete,
            VK.BACK => ImGuiKey.Backspace,
            VK.SPACE => ImGuiKey.Space,
            VK.RETURN => ImGuiKey.Enter,
            VK.ESCAPE => ImGuiKey.Escape,
            VK.OEM_7 => ImGuiKey.Apostrophe,
            VK.OEM_COMMA => ImGuiKey.Comma,
            VK.OEM_MINUS => ImGuiKey.Minus,
            VK.OEM_PERIOD => ImGuiKey.Period,
            VK.OEM_2 => ImGuiKey.Slash,
            VK.OEM_1 => ImGuiKey.Semicolon,
            VK.OEM_PLUS => ImGuiKey.Equal,
            VK.OEM_4 => ImGuiKey.LeftBracket,
            VK.OEM_5 => ImGuiKey.Backslash,
            VK.OEM_6 => ImGuiKey.RightBracket,
            VK.OEM_3 => ImGuiKey.GraveAccent,
            VK.CAPITAL => ImGuiKey.CapsLock,
            VK.SCROLL => ImGuiKey.ScrollLock,
            VK.NUMLOCK => ImGuiKey.NumLock,
            VK.SNAPSHOT => ImGuiKey.PrintScreen,
            VK.PAUSE => ImGuiKey.Pause,
            VK.DECIMAL => ImGuiKey.KeypadDecimal,
            VK.DIVIDE => ImGuiKey.KeypadDivide,
            VK.MULTIPLY => ImGuiKey.KeypadMultiply,
            VK.SUBTRACT => ImGuiKey.KeypadSubtract,
            VK.ADD => ImGuiKey.KeypadAdd,
            VK.LSHIFT => ImGuiKey.LeftShift,
            VK.LCONTROL => ImGuiKey.LeftCtrl,
            VK.LMENU => ImGuiKey.LeftAlt,
            VK.LWIN => ImGuiKey.LeftSuper,
            VK.RSHIFT => ImGuiKey.RightShift,
            VK.RCONTROL => ImGuiKey.RightCtrl,
            VK.RMENU => ImGuiKey.RightAlt,
            VK.RWIN => ImGuiKey.RightSuper,
            VK.APPS => ImGuiKey.Menu,
            _ => ImGuiKey.None
        };

        return result != ImGuiKey.None;
    }

    private static readonly float WheelDelta = 120;

    private static int GET_WHEEL_DELTA_WPARAM(WPARAM wParam) => Utils.Hiword((int)(uint)wParam.Value);

    private static int GET_XBUTTON_WPARAM(WPARAM wParam) => Utils.Hiword((int)(uint)wParam.Value);
}
