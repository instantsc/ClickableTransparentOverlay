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
    private readonly string title;
    private readonly Format format;

    private Win32Window window;
    private ID3D11Device device;
    private ID3D11DeviceContext deviceContext;
    private IDXGISwapChain swapChain;
    private ID3D11Texture2D backBuffer;
    private ID3D11RenderTargetView renderView;

    private ImGuiRenderer renderer;
    private ImGuiInputHandler inputhandler;

    private bool _disposedValue;
    private IntPtr selfPointer;
    private Thread renderThread;
    private volatile CancellationTokenSource cancellationTokenSource;
    private volatile bool overlayIsReady;

    private bool replaceFont = false;
    private ushort[]? fontCustomGlyphRange;
    private string fontPathName;
    private float fontSize;
    private FontGlyphRangeType fontLanguage;

    private Dictionary<string, (IntPtr Handle, uint Width, uint Height)> loadedTexturesPtrs;

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
    /// <param name="DPIAware">
    /// should the overlay scale with windows scale value or not.
    /// </param>
    public Overlay(bool DPIAware) : this("Overlay", DPIAware)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Overlay"/> class.
    /// </summary>
    /// <param name="windowTitle">
    /// Title of the window created by the overlay
    /// </param>
    /// <param name="DPIAware">
    /// should the overlay scale with windows scale value or not.
    /// </param>
    public Overlay(string windowTitle, bool DPIAware)
    {
        VSync = true;
        _disposedValue = false;
        overlayIsReady = false;
        title = windowTitle;
        cancellationTokenSource = new();
        format = Format.R8G8B8A8_UNorm;
        loadedTexturesPtrs = new();
        if (DPIAware)
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
        renderThread = new Thread(async () =>
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_10_0 }, out device, out deviceContext);
            selfPointer = Kernel32.GetModuleHandle().ReleaseOwnership();
            var wndClass = new User32.WNDCLASSEX
            {
                cbSize = (uint)Unsafe.SizeOf<User32.WNDCLASSEX>(),
                style = User32.WindowClassStyles.CS_HREDRAW | User32.WindowClassStyles.CS_VREDRAW | User32.WindowClassStyles.CS_PARENTDC,
                lpfnWndProc = WndProc,
                hInstance = selfPointer,
                hCursor = User32.LoadCursor(IntPtr.Zero, (int)SystemCursor.IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                hIcon = IntPtr.Zero,
                lpszClassName = "WndClass",
            };

            User32.RegisterClassEx(wndClass);
            window = new Win32Window(wndClass.lpszClassName, 800, 600, 0, 0, title,
                User32.WindowStyles.WS_POPUP, User32.WindowStylesEx.WS_EX_ACCEPTFILES | User32.WindowStylesEx.WS_EX_TOPMOST);
            renderer = new ImGuiRenderer(device, deviceContext, 800, 600);
            inputhandler = new ImGuiInputHandler(window.Handle);
            overlayIsReady = true;
            await PostInitialized();
            User32.ShowWindow(window.Handle, ShowWindowCommand.SW_MAXIMIZE);
            Utils.InitTransparency(window.Handle);
            renderer.Start();
            RunInfiniteLoop(cancellationTokenSource.Token);
        });

        renderThread.Start();
        await WaitHelpers.SpinWait(() => overlayIsReady);
    }

    /// <summary>
    /// Starts the overlay and waits for the overlay window to be closed.
    /// </summary>
    /// <returns>A task that finishes once the overlay window closes</returns>
    public virtual async Task Run()
    {
        if (!overlayIsReady)
        {
            await Start();
        }

        await WaitHelpers.SpinWait(() => cancellationTokenSource.IsCancellationRequested);
    }

    /// <summary>
    /// Safely Closes the Overlay.
    /// </summary>
    public virtual void Close()
    {
        cancellationTokenSource.Cancel();
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

        fontPathName = pathName;
        fontSize = size;
        fontLanguage = language;
        replaceFont = true;
        fontCustomGlyphRange = null;
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

        fontPathName = pathName;
        fontSize = size;
        fontCustomGlyphRange = glyphRange;
        replaceFont = true;
        return true;
    }

    /// <summary>
    /// Enable or disable the vsync on the overlay.
    /// </summary>
    public bool VSync;

    /// <summary>
    /// Gets or sets the position of the overlay window.
    /// </summary>
    public Point Position
    {
        get { return window.Dimensions.Location; }

        set
        {
            if (window.Dimensions.Location != value)
            {
                User32.MoveWindow(window.Handle, value.X, value.Y, window.Dimensions.Width, window.Dimensions.Width, true);
                window.Dimensions.Location = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the size of the overlay window.
    /// </summary>
    public Size Size
    {
        get { return window.Dimensions.Size; }
        set
        {
            if (window.Dimensions.Size != value)
            {
                User32.MoveWindow(window.Handle, window.Dimensions.X, window.Dimensions.X, value.Width, value.Height, true);
                window.Dimensions.Size = value;
            }
        }
    }

    /// <summary>
    /// Gets the number of displays available on the computer.
    /// </summary>
    public static int NumberVideoDisplays
    {
        get { return User32.GetSystemMetrics(User32.SystemMetric.SM_CMONITORS); }
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
        if (loadedTexturesPtrs.TryGetValue(filePath, out var data))
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
            handle = renderer.CreateImageTexture(image, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
            width = (uint)image.Width;
            height = (uint)image.Height;
            loadedTexturesPtrs.Add(filePath, new(handle, width, height));
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
        if (loadedTexturesPtrs.TryGetValue(name, out var data))
        {
            handle = data.Handle;
        }
        else
        {
            handle = renderer.CreateImageTexture(image, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
            loadedTexturesPtrs.Add(name, new(handle, (uint)image.Width, (uint)image.Height));
        }
    }

    /// <summary>
    /// Removes the image from the Overlay.
    /// </summary>
    /// <param name="key">name or pathname which was used to add the image in the first place.</param>
    /// <returns> true if the image is removed otherwise false.</returns>
    public bool RemoveImage(string key)
    {
        if (loadedTexturesPtrs.Remove(key, out var data))
        {
            return renderer.RemoveImageTexture(data.Handle);
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
            renderThread?.Join();
            foreach (var key in loadedTexturesPtrs.Keys.ToArray())
            {
                RemoveImage(key);
            }

            cancellationTokenSource?.Dispose();
            swapChain?.Release();
            backBuffer?.Release();
            renderView?.Release();
            renderer?.Dispose();
            window?.Dispose();
            deviceContext?.Release();
            device?.Release();
        }

        if (selfPointer != IntPtr.Zero)
        {
            _ = User32.UnregisterClass(title, selfPointer);
            selfPointer = IntPtr.Zero;
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
        float deltaTime = 0f;
        var clearColor = new Color4(0.0f);
        while (!token.IsCancellationRequested)
        {
            deltaTime = stopwatch.ElapsedTicks / (float)Stopwatch.Frequency;
            stopwatch.Restart();
            window.PumpEvents();
            Utils.SetOverlayClickable(window.Handle, inputhandler.Update());
            renderer.Update(deltaTime, () => { Render(); });
            deviceContext.OMSetRenderTargets(renderView);
            deviceContext.ClearRenderTargetView(renderView, clearColor);
            renderer.Render();
            if (VSync)
            {
                swapChain.Present(1, PresentFlags.None); // Present with vsync
            }
            else
            {
                swapChain.Present(0, PresentFlags.None); // Present without vsync
            }

            ReplaceFontIfRequired();
        }
    }

    private void ReplaceFontIfRequired()
    {
        if (replaceFont && renderer != null)
        {
            renderer.UpdateFontTexture(fontPathName, fontSize, fontCustomGlyphRange, fontLanguage);
            replaceFont = false;
        }
    }

    private void OnResize()
    {
        if (renderView == null) //first show
        {
            using var dxgiFactory = device.QueryInterface<IDXGIDevice>().GetParent<IDXGIAdapter>().GetParent<IDXGIFactory>();
            var swapchainDesc = new SwapChainDescription()
            {
                BufferCount = 1,
                BufferDescription = new ModeDescription(window.Dimensions.Width, window.Dimensions.Height, format),
                Windowed = true,
                OutputWindow = window.Handle,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.Discard,
                BufferUsage = Usage.RenderTargetOutput,
            };

            swapChain = dxgiFactory.CreateSwapChain(device, swapchainDesc);
            dxgiFactory.MakeWindowAssociation(window.Handle, WindowAssociationFlags.IgnoreAll);

            backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
            renderView = device.CreateRenderTargetView(backBuffer);
        }
        else
        {
            renderView.Dispose();
            backBuffer.Dispose();

            swapChain.ResizeBuffers(1, window.Dimensions.Width, window.Dimensions.Height, format, SwapChainFlags.None);

            backBuffer = swapChain.GetBuffer<ID3D11Texture2D1>(0);
            renderView = device.CreateRenderTargetView(backBuffer);
        }

        renderer.Resize(window.Dimensions.Width, window.Dimensions.Height);
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
                        window.Dimensions.Width = Utils.Loword(lp);
                        window.Dimensions.Height = Utils.Hiword(lp);
                        OnResize();
                        break;
                    default:
                        break;
                }

                break;
            case WindowMessage.Destroy:
                Close();
                break;
            default:
                break;
        }

        return false;
    }

    private IntPtr WndProc(HWND hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (overlayIsReady)
        {
            if (inputhandler.ProcessMessage((WindowMessage)msg, wParam, lParam) ||
                ProcessMessage((WindowMessage)msg, wParam, lParam))
            {
                return IntPtr.Zero;
            }
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
