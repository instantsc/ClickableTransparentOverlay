using Vanara.PInvoke;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ClickableTransparentOverlay.Win32;
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
    private readonly string _title;
    private readonly Format _format = Format.R8G8B8A8_UNorm;

    private Win32Window _window;
    private ID3D11Device _device;
    private ID3D11DeviceContext _deviceContext;
    private IDXGISwapChain _swapChain;
    private ID3D11Texture2D _backBuffer;
    private ID3D11RenderTargetView _renderView;

    private ImGuiRenderer _renderer;
    private ImGuiInputHandler _inputhandler;

    private bool _disposedValue = false;
    private IntPtr _selfPointer;
    private Thread _renderThread;
    private volatile CancellationTokenSource _cancellationTokenSource = new();
    private volatile bool _overlayIsReady = false;

    private bool _replaceFont = false;
    private ushort[]? _fontCustomGlyphRange;
    private string _fontPathName;
    private float _fontSize;
    private FontGlyphRangeType _fontLanguage;

    private Dictionary<string, (IntPtr Handle, uint Width, uint Height)> _loadedTexturesPtrs = new();

    #region Constructors

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
            User32.SetProcessDPIAware();
        }
    }

    #endregion

    #region PublicAPI

    /// <summary>
    /// Starts the overlay
    /// </summary>
    /// <returns>A Task that finishes once the overlay window is ready</returns>
    public async Task Start()
    {
        _renderThread = new Thread(() =>
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_10_0 }, out _device, out _deviceContext);
            _selfPointer = Kernel32.GetModuleHandle().ReleaseOwnership();
            var wndClass = new User32.WNDCLASSEX
            {
                cbSize = (uint)Unsafe.SizeOf<User32.WNDCLASSEX>(),
                style = User32.WindowClassStyles.CS_HREDRAW | User32.WindowClassStyles.CS_VREDRAW | User32.WindowClassStyles.CS_PARENTDC,
                lpfnWndProc = WndProc,
                hInstance = _selfPointer,
                hCursor = User32.LoadCursor(IntPtr.Zero, (int)SystemCursor.IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                hIcon = IntPtr.Zero,
                lpszClassName = "WndClass",
            };

            User32.RegisterClassEx(wndClass);
            _window = new Win32Window(wndClass.lpszClassName, 800, 600, 0, 0, _title,
                User32.WindowStyles.WS_POPUP, User32.WindowStylesEx.WS_EX_ACCEPTFILES | User32.WindowStylesEx.WS_EX_TOPMOST);
            _renderer = new ImGuiRenderer(_device, _deviceContext, 800, 600);
            _inputhandler = new ImGuiInputHandler(_window.Handle);
            _overlayIsReady = true;
            Task.Run(PostInitialized).Wait();
            User32.ShowWindow(_window.Handle, ShowWindowCommand.SW_MAXIMIZE);
            Utils.InitTransparency(_window.Handle);
            _renderer.Start();
            RunInfiniteLoop(_cancellationTokenSource.Token);
        });

        _renderThread.Start();
        await WaitHelpers.SpinWait(() => _overlayIsReady);
    }

    /// <summary>
    /// Starts the overlay and waits for the overlay window to be closed.
    /// </summary>
    /// <returns>A task that finishes once the overlay window closes</returns>
    public virtual async Task Run()
    {
        if (!_overlayIsReady)
        {
            await Start();
        }

        await WaitHelpers.SpinWait(() => _cancellationTokenSource.IsCancellationRequested);
    }

    /// <summary>
    /// Safely Closes the Overlay.
    /// </summary>
    public virtual void Close()
    {
        _cancellationTokenSource.Cancel();
    }

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

        _fontPathName = pathName;
        _fontSize = size;
        _fontLanguage = language;
        _replaceFont = true;
        _fontCustomGlyphRange = null;
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

        _fontPathName = pathName;
        _fontSize = size;
        _fontCustomGlyphRange = glyphRange;
        _replaceFont = true;
        return true;
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
                User32.MoveWindow(_window.Handle, value.X, value.Y, _window.Dimensions.Width, _window.Dimensions.Width, true);
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
                User32.MoveWindow(_window.Handle, _window.Dimensions.X, _window.Dimensions.X, value.Width, value.Height, true);
                _window.Dimensions.Size = value;
            }
        }
    }

    /// <summary>
    /// Gets the number of displays available on the computer.
    /// </summary>
    public static int NumberVideoDisplays => User32.GetSystemMetrics(User32.SystemMetric.SM_CMONITORS);

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
        if (_loadedTexturesPtrs.TryGetValue(filePath, out var data))
        {
            handle = data.Handle;
            width = data.Width;
            height = data.Height;
        }
        else
        {
            var configuration = Configuration.Default.Clone();
            configuration.PreferContiguousImageBuffers = true;
            using var image = Image.Load<Rgba32>(configuration, filePath);
            handle = _renderer.CreateImageTexture(image, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
            width = (uint)image.Width;
            height = (uint)image.Height;
            _loadedTexturesPtrs.Add(filePath, (handle, width, height));
        }
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
        if (_loadedTexturesPtrs.TryGetValue(name, out var data))
        {
            handle = data.Handle;
        }
        else
        {
            handle = _renderer.CreateImageTexture(image, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
            _loadedTexturesPtrs.Add(name, (handle, (uint)image.Width, (uint)image.Height));
        }
    }

    /// <summary>
    /// Removes the image from the Overlay.
    /// </summary>
    /// <param name="key">name or pathname which was used to add the image in the first place.</param>
    /// <returns> true if the image is removed otherwise false.</returns>
    public bool RemoveImage(string key)
    {
        if (_loadedTexturesPtrs.Remove(key, out var data))
        {
            return _renderer.RemoveImageTexture(data.Handle);
        }

        return false;
    }

    #endregion

    protected virtual void Dispose(bool disposing)
    {
        if (_disposedValue)
        {
            return;
        }

        if (disposing)
        {
            _renderThread?.Join();
            foreach (var key in _loadedTexturesPtrs.Keys.ToArray())
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

        if (_selfPointer != IntPtr.Zero)
        {
            _ = User32.UnregisterClass(_title, _selfPointer);
            _selfPointer = IntPtr.Zero;
        }

        _disposedValue = true;
    }

    /// <summary>
    /// Steps to execute after the overlay has fully initialized.
    /// </summary>
    protected virtual Task PostInitialized()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Abstract Task for creating the UI.
    /// </summary>
    /// <returns>Task that finishes once per frame</returns>
    protected abstract void Render();

    private void RunInfiniteLoop(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var clearColor = new Color4(0.0f);
        while (!token.IsCancellationRequested)
        {
            var deltaTime = stopwatch.ElapsedTicks / (float)Stopwatch.Frequency;
            stopwatch.Restart();
            _window.PumpEvents();
            Utils.SetOverlayClickable(_window.Handle, _inputhandler.Update());
            _renderer.Update(deltaTime, Render);
            _deviceContext.OMSetRenderTargets(_renderView);
            _deviceContext.ClearRenderTargetView(_renderView, clearColor);
            _renderer.Render();
            _swapChain.Present(VSync ? 1 : 0, PresentFlags.None);
            ReplaceFontIfRequired();
        }
    }

    private void ReplaceFontIfRequired()
    {
        if (_replaceFont && _renderer != null)
        {
            _renderer.UpdateFontTexture(_fontPathName, _fontSize, _fontCustomGlyphRange, _fontLanguage);
            _replaceFont = false;
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
                BufferDescription = new ModeDescription(_window.Dimensions.Width, _window.Dimensions.Height, _format),
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

            _swapChain.ResizeBuffers(1, _window.Dimensions.Width, _window.Dimensions.Height, _format, SwapChainFlags.None);

            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D1>(0);
            _renderView = _device.CreateRenderTargetView(_backBuffer);
        }

        _renderer.Resize(_window.Dimensions.Width, _window.Dimensions.Height);
    }

    private bool ProcessMessage(WindowMessage msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WindowMessage.Size:
                switch ((SizeMessage)wParam)
                {
                    case SizeMessage.SIZE_RESTORED:
                    case SizeMessage.SIZE_MAXIMIZED:
                        var lp = (int)lParam;
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

        return false;
    }

    private IntPtr WndProc(HWND hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_overlayIsReady &&
            (_inputhandler.ProcessMessage((WindowMessage)msg, wParam, lParam) ||
             ProcessMessage((WindowMessage)msg, wParam, lParam)))
        {
            return IntPtr.Zero;
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
