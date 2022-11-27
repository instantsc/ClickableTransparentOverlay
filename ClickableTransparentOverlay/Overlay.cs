using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using ClickableTransparentOverlay.Win32;
using ImGuiNET;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace ClickableTransparentOverlay;

/// <summary>
/// A class to create clickable transparent overlay on windows machine.
/// </summary>
public abstract class Overlay : IDisposable
{
    public unsafe delegate void FontLoadDelegate(ImFontConfig* fontConfig);

    private static readonly Color4 ClearColor = new Color4(0.0f);
    private const Format Format = Vortice.DXGI.Format.R8G8B8A8_UNorm;

    private Win32Window _window;
    private ID3D11Device _device;
    private ID3D11DeviceContext _deviceContext;
    private IDXGISwapChain _swapChain;
    private ID3D11Texture2D _backBuffer;
    private ID3D11RenderTargetView _renderView;
    private ImGuiRenderer _renderer;
    private ImGuiInputHandler _inputHandler;
    private FreeLibrarySafeHandle _selfPointer;
    private SafeHandle _iconPtr;

    private bool _disposed;
    private Thread _renderThread;
    private volatile int _overlayWasStarted;

    private readonly string _title;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly TaskCompletionSource _overlayReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _overlayClosedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<FontLoadDelegate> _fontUpdates = new();
    private readonly Queue<(WindowMessage msg, WPARAM wParam, LPARAM lParam)> _messageQueue = new();
    private readonly ConcurrentDictionary<string, (IntPtr Handle, uint Width, uint Height)> _loadedTexturePtrs = new();

    protected CancellationToken OverlayCloseToken => _cancellationTokenSource.Token;

    /// <summary>
    /// Initializes a new instance of the <see cref="Overlay"/> class.
    /// </summary>
    public Overlay() : this("Overlay")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Overlay"/> class.
    /// </summary>
    /// <param name="windowTitle">
    /// Title of the window created by the overlay
    /// </param>
    public Overlay(string windowTitle) : this(windowTitle, false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Overlay"/> class.
    /// </summary>
    /// <param name="dpiAware">
    /// should the overlay scale with windows scale value or not.
    /// </param>
    public Overlay(bool dpiAware) : this("Overlay", dpiAware)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Overlay"/> class.
    /// </summary>
    /// <param name="windowTitle">
    /// Title of the window created by the overlay
    /// </param>
    /// <param name="dpiAware">
    /// should the overlay scale with windows scale value or not.
    /// </param>
    public Overlay(string windowTitle, bool dpiAware)
    {
        _title = windowTitle;
        if (dpiAware)
        {
            PInvoke.SetProcessDPIAware();
        }
    }

    public void SetIcon(string fileName)
    {
        var newIcon = PInvoke.LoadImage(default, fileName, GDI_IMAGE_TYPE.IMAGE_ICON, 0, 0, IMAGE_FLAGS.LR_LOADFROMFILE);
        if (WindowHandle is { } windowHandle)
        {
            _overlayReadyTcs.Task.ContinueWith(_ =>
            {
                // ReSharper disable RedundantArgumentDefaultValue
                PInvoke.SendMessage(new HWND(windowHandle), PInvoke.WM_SETICON, 1, newIcon.DangerousGetHandle());
                PInvoke.SendMessage(new HWND(windowHandle), PInvoke.WM_SETICON, 0, newIcon.DangerousGetHandle());
                // ReSharper restore RedundantArgumentDefaultValue
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        if (Interlocked.Exchange(ref _iconPtr, newIcon) is var oldIcon && oldIcon?.DangerousGetHandle() is { } oldIconValue && oldIconValue != IntPtr.Zero)
        {
            PInvoke.DestroyIcon(new HICON(oldIconValue));
        }
    }

    /// <summary>
    /// Starts the overlay
    /// </summary>
    /// <returns>A Task that finishes once the overlay window is ready</returns>
    public async Task Start()
    {
        if (!TrySetFirstStart())
        {
            throw new Exception("Overlay is already started");
        }

        _renderThread = new Thread(() =>
        {
            try
            {
                unsafe
                {

                    D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                        new[] { FeatureLevel.Level_10_0 }, out _device, out _deviceContext);
                    _selfPointer = PInvoke.GetModuleHandle((string?)null);
                    const string wndClassName = "WndClass";
                    fixed (char* wndClassNamePtr = wndClassName)
                    {
                        var wndClass = new WNDCLASSEXW
                        {
                            cbSize = (uint)Unsafe.SizeOf<WNDCLASSEXW>(),
                            style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW | WNDCLASS_STYLES.CS_PARENTDC,
                            lpfnWndProc = WndProc,
                            hInstance = new HINSTANCE(_selfPointer.DangerousGetHandle()),
                            hCursor = PInvoke.LoadCursor(new HINSTANCE(IntPtr.Zero), PInvoke.IDC_ARROW),
                            hbrBackground = new HBRUSH(IntPtr.Zero),
                            hIcon = new HICON(_iconPtr.DangerousGetHandle()),
                            hIconSm = new HICON(_iconPtr.DangerousGetHandle()),
                            lpszClassName = new PCWSTR(wndClassNamePtr),
                        };

                        PInvoke.RegisterClassEx(wndClass);
                    }
                    _window = new Win32Window(wndClassName, 800, 600, 0, 0, _title,
                        WINDOW_STYLE.WS_POPUP, WINDOW_EX_STYLE.WS_EX_ACCEPTFILES | WINDOW_EX_STYLE.WS_EX_TOPMOST);
                    _renderer = new ImGuiRenderer(_device, _deviceContext, 800, 600);
                    _inputHandler = new ImGuiInputHandler(_window.Handle);
                    PInvoke.ShowWindow(_window.Handle, SHOW_WINDOW_CMD.SW_MAXIMIZE);
                    Utils.InitTransparency(_window.Handle);
                    _overlayReadyTcs.SetResult();
                }
            }
            catch (Exception ex)
            {
                _overlayReadyTcs.SetException(ex);
                return;
            }

            try
            {
                Task.Run(PostInitialized).Wait();
                while (_messageQueue.TryDequeue(out var message))
                {
                    ProcessMessage(message.msg, message.wParam, message.lParam);
                }

                RunFrameLoop(_cancellationTokenSource.Token);
                Task.Run(OnClosed).Wait();
                _overlayClosedTcs.SetResult();
            }
            catch (Exception ex)
            {
                _overlayClosedTcs.SetException(ex);
            }
        });

        _renderThread.Start();
        await _overlayReadyTcs.Task;
    }

    private bool TrySetFirstStart()
    {
        return Interlocked.CompareExchange(ref _overlayWasStarted, 1, 0) == 0;
    }


    /// <summary>
    /// Starts the overlay and waits for the overlay window to be closed.
    /// </summary>
    /// <returns>A task that finishes once the overlay window closes</returns>
    public virtual async Task Run()
    {
        await Start();
        await _overlayClosedTcs.Task;
    }

    /// <summary>
    /// Safely Closes the Overlay.
    /// </summary>
    public void Close() => _cancellationTokenSource.Cancel();

    /// <summary>
    /// Wait for complete overlay shutdown. Do not call from inside the render method
    /// </summary>
    /// <returns></returns>
    public async Task WaitForShutdown() => await _overlayClosedTcs.Task;

    /// <summary>
    /// Safely dispose all the resources created by the overlay
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Replaces the ImGui font with another one.
    /// </summary>
    /// <param name="pathName">pathname to the TTF font file.</param>
    /// <param name="size">font size to load.</param>
    /// <param name="language">supported language by the font.</param>
    /// <returns>true if the font replacement is valid otherwise false.</returns>
    public bool ReplaceFont(string pathName, int size, FontGlyphRangeType language)
    {
        if (!File.Exists(pathName))
        {
            return false;
        }

        _fontUpdates.Enqueue(_renderer.MakeFontLoadDelegate(pathName, size, null, language));
        return true;
    }

    /// <summary>
    /// Replaces the ImGui font with another one.
    /// </summary>
    /// <param name="pathName">pathname to the TTF font file.</param>
    /// <param name="size">font size to load.</param>
    /// <param name="glyphRange">custom glyph range of the font to load. Read <see cref="FontGlyphRangeType"/> for more detail.</param>
    /// <returns>>true if the font replacement is valid otherwise false.</returns>
    public bool ReplaceFont(string pathName, int size, ushort[] glyphRange)
    {
        if (!File.Exists(pathName))
        {
            return false;
        }

        _fontUpdates.Enqueue(_renderer.MakeFontLoadDelegate(pathName, size, glyphRange, null));
        return true;
    }

    public void ReplaceFont(FontLoadDelegate fontLoadDelegate)
    {
        _fontUpdates.Enqueue(fontLoadDelegate);
    }

    /// <summary>
    /// Enable or disable the vsync on the overlay.
    /// </summary>
    public bool VSync = true;

    /// <summary>
    /// Gets or sets the position of the overlay window.
    /// </summary>
    public Point Position
    {
        get => _window.Dimensions.Location;

        set
        {
            if (_window.Dimensions.Location != value)
            {
                PInvoke.MoveWindow(_window.Handle, value.X, value.Y, _window.Dimensions.Width, _window.Dimensions.Height, true);
                _window.Dimensions.Location = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the size of the overlay window.
    /// </summary>
    public Size Size
    {
        get => _window.Dimensions.Size;
        set
        {
            if (_window.Dimensions.Size != value)
            {
                PInvoke.MoveWindow(_window.Handle, _window.Dimensions.X, _window.Dimensions.Y, value.Width, value.Height, true);
                _window.Dimensions.Size = value;
            }
        }
    }

    public IntPtr? WindowHandle => _window?.Handle;

    /// <summary>
    /// Gets the number of displays available on the computer.
    /// </summary>
    public static int NumberVideoDisplays => PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CMONITORS);

    public bool TryGetImagePointer(string nameOrFilePath, out IntPtr handle)
    {
        if (_loadedTexturePtrs.TryGetValue(nameOrFilePath, out var data))
        {
            handle = data.Handle;
            return true;
        }

        handle = IntPtr.Zero;
        return false;
    }

    public bool HasImagePointer(string nameOrFilePath)
    {
        return _loadedTexturePtrs.TryGetValue(nameOrFilePath, out _);
    }

    public IntPtr GetImagePointer(string nameOrFilePath)
    {
        if (_loadedTexturePtrs.TryGetValue(nameOrFilePath, out var data))
        {
            return data.Handle;
        }

        throw new FileNotFoundException($"Texture by name: {nameOrFilePath} not found");
    }

    /// <summary>
    /// Adds the image to the Graphic Device as a texture.
    /// Then returns the pointer of the added texture. It also
    /// cache the image internally rather than creating a new texture on every call,
    /// so this function can be called multiple times per frame.
    /// </summary>
    /// <param name="filePath">Path to the image on disk.</param>
    /// <param name="srgb"> a value indicating whether pixel format is srgb or not.</param>
    /// <param name="handle">output pointer to the image in the graphic device.</param>
    /// <param name="width">width of the loaded texture.</param>
    /// <param name="height">height of the loaded texture.</param>
    public void AddOrGetImagePointer(string filePath, bool srgb, out IntPtr handle, out uint width, out uint height)
    {
        (handle, width, height) = _loadedTexturePtrs.GetOrAdd(filePath, path =>
        {
            var configuration = Configuration.Default.Clone();
            configuration.PreferContiguousImageBuffers = true;
            using var image = Image.Load<Rgba32>(configuration, path);
            var handle = _renderer.CreateImageTexture(image, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
            return (handle, (uint)image.Width, (uint)image.Height);
        });
    }

    public void AddOrGetImagePointer(string filePath, string name, bool srgb, out IntPtr handle, out uint width, out uint height)
    {
        (handle, width, height) = _loadedTexturePtrs.GetOrAdd(name, _ =>
        {
            var configuration = Configuration.Default.Clone();
            configuration.PreferContiguousImageBuffers = true;
            using var image = Image.Load<Rgba32>(configuration, filePath);
            var handle = _renderer.CreateImageTexture(image, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
            return (handle, (uint)image.Width, (uint)image.Height);
        });
    }

    /// <summary>
    /// Adds the image to the Graphic Device as a texture.
    /// Then returns the pointer of the added texture. It also
    /// cache the image internally rather than creating a new texture on every call,
    /// so this function can be called multiple times per frame.
    /// </summary>
    /// <param name="name">user friendly name given to the image.</param>
    /// <param name="image">Image data in <see cref="Image"> format.</param>
    /// <param name="srgb"> a value indicating whether pixel format is srgb or not.</param>
    /// <param name="handle">output pointer to the image in the graphic device.</param>
    public void AddOrGetImagePointer(string name, Image<Rgba32> image, bool srgb, out IntPtr handle)
    {
        (handle, _, _) = _loadedTexturePtrs.GetOrAdd(name, path =>
        {
            var handle = _renderer.CreateImageTexture(image, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
            return (handle, (uint)image.Width, (uint)image.Height);
        });
    }

    /// <summary>
    /// Removes the image from the Overlay.
    /// </summary>
    /// <param name="key">name or pathname which was used to add the image in the first place.</param>
    /// <returns> true if the image is removed otherwise false.</returns>
    public bool RemoveImage(string key)
    {
        if (_loadedTexturePtrs.Remove(key, out var data))
        {
            return _renderer.RemoveImageTexture(data.Handle);
        }

        return false;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _renderThread?.Join();
            foreach (var key in _loadedTexturePtrs.Keys.ToArray())
            {
                RemoveImage(key);
            }

            _cancellationTokenSource?.Dispose();
            _swapChain?.Release();
            _backBuffer?.Release();
            _renderView?.Release();
            _renderer?.Dispose();
            _window?.Dispose();
            _deviceContext?.Release();
            _device?.Release();
        }

        if (_selfPointer.DangerousGetHandle() != IntPtr.Zero)
        {
            _ = PInvoke.UnregisterClass(_title, _selfPointer);
            _selfPointer = default;
        }

        _disposed = true;
    }

    /// <summary>
    /// Steps to execute after the overlay has fully initialized.
    /// </summary>
    protected virtual Task PostInitialized()
    {
        return Task.CompletedTask;
    }

    protected virtual Task OnClosed()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Abstract Task for creating the UI.
    /// </summary>
    /// <returns>Task that finishes once per frame</returns>
    protected abstract void Render();

    private void RunFrameLoop(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        ReplaceFontIfRequired();
        _renderer.Start();
        while (!token.IsCancellationRequested)
        {
            var deltaTime = (float)stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
            _window.PumpEvents();
            if (Utils.SetOverlayClickable(_window.Handle, _inputHandler.Update()) == false)
            {
                FocusLost?.Invoke(this, EventArgs.Empty);
            }

            _renderer.Update(deltaTime, Render);
            _deviceContext.OMSetRenderTargets(_renderView);
            _deviceContext.ClearRenderTargetView(_renderView, ClearColor);
            _renderer.Render();
            _swapChain.Present(VSync ? 1 : 0, PresentFlags.None);
            ReplaceFontIfRequired();
            PostFrame();
        }
    }

    protected virtual void PostFrame()
    {
    }

    public event EventHandler FocusLost;

    private void ReplaceFontIfRequired()
    {
        while (_fontUpdates.TryDequeue(out var update))
        {
            _renderer.UpdateFontTexture(update);
        }
    }

    private void OnResize()
    {
        if (_renderView == null) //first show
        {
            using var dxgiFactory = _device.QueryInterface<IDXGIDevice>().GetParent<IDXGIAdapter>().GetParent<IDXGIFactory>();
            var swapchainDesc = new SwapChainDescription()
            {
                BufferCount = 1,
                BufferDescription = new ModeDescription(_window.Dimensions.Width, _window.Dimensions.Height, Format),
                Windowed = true,
                OutputWindow = _window.Handle,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.Discard,
                BufferUsage = Usage.RenderTargetOutput,
            };

            _swapChain = dxgiFactory.CreateSwapChain(_device, swapchainDesc);
            dxgiFactory.MakeWindowAssociation(_window.Handle, WindowAssociationFlags.IgnoreAll);

            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _renderView = _device.CreateRenderTargetView(_backBuffer);
        }
        else
        {
            _renderView.Dispose();
            _backBuffer.Dispose();

            _swapChain.ResizeBuffers(1, _window.Dimensions.Width, _window.Dimensions.Height, Format, SwapChainFlags.None);

            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D1>(0);
            _renderView = _device.CreateRenderTargetView(_backBuffer);
        }

        _renderer.Resize(_window.Dimensions.Width, _window.Dimensions.Height);
    }

    private bool ProcessMessage(WindowMessage type, WPARAM wParam, LPARAM lParam)
    {
        if (type is WindowMessage.Size or WindowMessage.Destroy && !_overlayReadyTcs.Task.IsCompleted)
        {
            _messageQueue.Enqueue((type, wParam, lParam));
            return true;
        }

        switch (type)
        {
            case WindowMessage.Size:
                switch ((SizeMessage)wParam.Value)
                {
                    case SizeMessage.SIZE_RESTORED:
                    case SizeMessage.SIZE_MAXIMIZED:
                        var lp = (int)lParam.Value;
                        _window.Dimensions.Width = Utils.Loword(lp);
                        _window.Dimensions.Height = Utils.Hiword(lp);
                        OnResize();
                        break;
                }

                break;
            case WindowMessage.Destroy:
                Close();
                break;
        }

        return _inputHandler?.ProcessMessage(type, wParam, lParam) == true;
    }

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (ProcessMessage((WindowMessage)msg, wParam, lParam))
        {
            return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
