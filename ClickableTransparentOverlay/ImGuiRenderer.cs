using ImGuiNET;
using ImDrawIdx = System.UInt16;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.Mathematics;
using System.Numerics;
using System.Collections.Generic;
using System;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace ClickableTransparentOverlay;

internal sealed unsafe class ImGuiRenderer : IDisposable
{
    private const int VertexConstantBufferSize = 16 * 4;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _deviceContext;
    private ID3D11Buffer _vertexBuffer;
    private ID3D11Buffer _indexBuffer;
    private Blob _vertexShaderBlob;
    private ID3D11VertexShader _vertexShader;
    private ID3D11InputLayout _inputLayout;
    private ID3D11Buffer _constantBuffer;
    private Blob _pixelShaderBlob;
    private ID3D11PixelShader _pixelShader;
    private ID3D11SamplerState _fontSampler;
    private ID3D11RasterizerState _rasterizerState;
    private ID3D11BlendState _blendState;
    private ID3D11DepthStencilState _depthStencilState;
    private int _vertexBufferSize = 5000, _indexBufferSize = 10000;
    private readonly Dictionary<IntPtr, ID3D11ShaderResourceView> _textureResources = new();

    public ImGuiRenderer(ID3D11Device device, ID3D11DeviceContext deviceContext, int width, int height)
    {
        _device = device;
        _deviceContext = deviceContext;

        device.AddRef();
        deviceContext.AddRef();

        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset; // We can honor the ImDrawCmd::VtxOffset field, allowing for large meshes.
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        ImGui.StyleColorsDark();
        Resize(width, height);
        CreateDeviceObjects();
    }

    public void Start()
    {
        ImGui.NewFrame();
    }

    public void Update(float deltaTime, Action doRender)
    {
        var io = ImGui.GetIO();
        io.DeltaTime = deltaTime;
        ImGui.NewFrame();
        doRender?.Invoke();
        ImGui.Render();
    }

    public void Render()
    {
        var data = ImGui.GetDrawData();
        // Avoid rendering when minimized
        if (data.DisplaySize.X <= 0.0f || data.DisplaySize.Y <= 0.0f)
            return;

        var ctx = _deviceContext;

        if (_vertexBuffer == null || _vertexBufferSize < data.TotalVtxCount)
        {
            _vertexBuffer?.Release();

            _vertexBufferSize = data.TotalVtxCount + 5000;
            var desc = new BufferDescription(
                _vertexBufferSize * sizeof(ImDrawVert),
                BindFlags.VertexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write);
            _vertexBuffer = _device.CreateBuffer(desc);
        }

        if (_indexBuffer == null || _indexBufferSize < data.TotalIdxCount)
        {
            _indexBuffer?.Release();

            _indexBufferSize = data.TotalIdxCount + 10000;

            var desc = new BufferDescription(
                _indexBufferSize * sizeof(ImDrawIdx),
                BindFlags.IndexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write);

            _indexBuffer = _device.CreateBuffer(desc);
        }

        // Upload vertex/index data into a single contiguous GPU buffer
        var vertexResource = ctx.Map(_vertexBuffer, 0, MapMode.WriteDiscard);
        var indexResource = ctx.Map(_indexBuffer, 0, MapMode.WriteDiscard);
        var vertexResourcePointer = (ImDrawVert*)vertexResource.DataPointer;
        var indexResourcePointer = (ImDrawIdx*)indexResource.DataPointer;
        for (var n = 0; n < data.CmdListsCount; n++)
        {
            var cmdlList = data.CmdLists[n];

            var vertBytes = cmdlList.VtxBuffer.Size * sizeof(ImDrawVert);
            Buffer.MemoryCopy((void*)cmdlList.VtxBuffer.Data, vertexResourcePointer, vertBytes, vertBytes);

            var idxBytes = cmdlList.IdxBuffer.Size * sizeof(ImDrawIdx);
            Buffer.MemoryCopy((void*)cmdlList.IdxBuffer.Data, indexResourcePointer, idxBytes, idxBytes);

            vertexResourcePointer += cmdlList.VtxBuffer.Size;
            indexResourcePointer += cmdlList.IdxBuffer.Size;
        }

        ctx.Unmap(_vertexBuffer, 0);
        ctx.Unmap(_indexBuffer, 0);

        // Setup orthographic projection matrix into our constant buffer
        // Our visible imgui space lies from draw_data.DisplayPos (top left) to draw_data.DisplayPos+data_data.DisplaySize (bottom right). DisplayPos is (0,0) for single viewport apps.

        var constResource = ctx.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        var span = constResource.AsSpan<float>(VertexConstantBufferSize);
        var left = data.DisplayPos.X;
        var right = data.DisplayPos.X + data.DisplaySize.X;
        var top = data.DisplayPos.Y;
        var bottom = data.DisplayPos.Y + data.DisplaySize.Y;
        float[] mvp =
        {
            2.0f / (right - left), 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / (top - bottom), 0.0f, 0.0f,
            0.0f, 0.0f, 0.5f, 0.0f,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0.5f, 1.0f,
        };
        mvp.CopyTo(span);
        ctx.Unmap(_constantBuffer, 0);
        //BackupDX11State(ctx); // only required if imgui is injected + drawn on existing process.
        SetupRenderState(data, ctx);
        // Render command lists
        // (Because we merged all buffers into a single one, we maintain our own offset into them)
        var globalIdxOffset = 0;
        var globalVtxOffset = 0;
        for (var n = 0; n < data.CmdListsCount; n++)
        {
            var cmdList = data.CmdLists[n];
            for (var i = 0; i < cmdList.CmdBuffer.Size; i++)
            {
                var cmd = cmdList.CmdBuffer[i];
                if (cmd.UserCallback != IntPtr.Zero)
                {
                    throw new NotImplementedException("user callbacks not implemented");
                }

                ctx.RSSetScissorRect(
                    (int)cmd.ClipRect.X,
                    (int)cmd.ClipRect.Y,
                    (int)(cmd.ClipRect.Z - cmd.ClipRect.X),
                    (int)(cmd.ClipRect.W - cmd.ClipRect.Y));

                if (_textureResources.TryGetValue(cmd.GetTexID(), out var texture))
                {
                    ctx.PSSetShaderResource(0, texture);
                }

                ctx.DrawIndexed((int)cmd.ElemCount, (int)(cmd.IdxOffset + globalIdxOffset), (int)(cmd.VtxOffset + globalVtxOffset));
            }

            globalIdxOffset += cmdList.IdxBuffer.Size;
            globalVtxOffset += cmdList.VtxBuffer.Size;
        }

        //RestoreDX11State(ctx); // only required if imgui is injected + drawn on existing process.
    }

    public void Dispose()
    {
        if (_device == null)
            return;

        DeRegisterAllTexture();
        _fontSampler?.Release();
        _indexBuffer?.Release();
        _vertexBuffer?.Release();
        _blendState?.Release();
        _depthStencilState?.Release();
        _rasterizerState?.Release();
        _pixelShader?.Release();
        _pixelShaderBlob?.Release();
        _constantBuffer?.Release();
        _inputLayout?.Release();
        _vertexShader?.Release();
        _vertexShaderBlob?.Release();
    }

    public void Resize(int width, int height)
    {
        ImGui.GetIO().DisplaySize = new Vector2(width, height);
    }

    public IntPtr CreateImageTexture(Image<Rgba32> image, Format format)
    {
        var texDesc = new Texture2DDescription(format, image.Width, image.Height, 1, 1);
        Image<Rgba32>? imageCopy = null;
        try
        {
            if (!image.DangerousTryGetSinglePixelMemory(out var memory))
            {
                var configuration = image.GetConfiguration().Clone();
                configuration.PreferContiguousImageBuffers = true;
                imageCopy = image.Clone(configuration);
                if (!imageCopy.DangerousTryGetSinglePixelMemory(out memory))
                {
                    throw new Exception("Unable to get a contiguous memory for the provided image or clone it. " +
                                        "Make sure you set PreferContiguousImageBuffers for the image configuration");
                }
            }

            using var imageMemoryHandle = memory.Pin();
            var subResource = new SubresourceData(imageMemoryHandle.Pointer, texDesc.Width * 4);
            using var texture = _device.CreateTexture2D(texDesc, new[] { subResource });
            var resViewDesc = new ShaderResourceViewDescription(texture, ShaderResourceViewDimension.Texture2D, format, 0, texDesc.MipLevels);
            return RegisterTexture(_device.CreateShaderResourceView(texture, resViewDesc));
        }
        finally
        {
            imageCopy?.Dispose();
        }
    }

    public bool RemoveImageTexture(IntPtr handle)
    {
        using var tex = DeRegisterTexture(handle);
        return tex != null;
    }


    public void UpdateFontTexture(Overlay.FontLoadDelegate fontLoadFunc)
    {
        var io = ImGui.GetIO();
        DeRegisterTexture(io.Fonts.TexID)?.Dispose();
        io.Fonts.Clear();
        var config = ImGuiNative.ImFontConfig_ImFontConfig();
        fontLoadFunc(config);
        CreateFontsTexture();
        ImGuiNative.ImFontConfig_destroy(config);
    }

    public Overlay.FontLoadDelegate MakeFontLoadDelegate(string fontPathName, float fontSize, ushort[]? fontCustomGlyphRange, FontGlyphRangeType? fontLanguage)
    {
        return config =>
        {
            var io = ImGui.GetIO();

            if (fontPathName == "Default")
            {
                io.Fonts.AddFontDefault(config);
            }
            else if (fontCustomGlyphRange == null)
            {
                var glyphRange = fontLanguage switch
                {
                    FontGlyphRangeType.English => io.Fonts.GetGlyphRangesDefault(),
                    FontGlyphRangeType.ChineseSimplifiedCommon => io.Fonts.GetGlyphRangesChineseSimplifiedCommon(),
                    FontGlyphRangeType.ChineseFull => io.Fonts.GetGlyphRangesChineseFull(),
                    FontGlyphRangeType.Japanese => io.Fonts.GetGlyphRangesJapanese(),
                    FontGlyphRangeType.Korean => io.Fonts.GetGlyphRangesKorean(),
                    FontGlyphRangeType.Thai => io.Fonts.GetGlyphRangesThai(),
                    FontGlyphRangeType.Vietnamese => io.Fonts.GetGlyphRangesVietnamese(),
                    FontGlyphRangeType.Cyrillic => io.Fonts.GetGlyphRangesCyrillic(),
                    _ => throw new Exception($"Font Glyph Range (${fontLanguage}) is not supported.")
                };
                io.Fonts.AddFontFromFileTTF(fontPathName, fontSize, config, glyphRange);
            }
            else
            {
                fixed (ushort* p = &fontCustomGlyphRange[0])
                {
                    io.Fonts.AddFontFromFileTTF(fontPathName, fontSize, config, new IntPtr(p));
                }
            }
        };
    }

    private void SetupRenderState(ImDrawDataPtr drawData, ID3D11DeviceContext ctx)
    {
        var viewport = new Viewport(0f, 0f, drawData.DisplaySize.X, drawData.DisplaySize.Y, 0f, 1f);
        ctx.RSSetViewport(viewport);
        var stride = sizeof(ImDrawVert);
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vertexBuffer, stride);
        ctx.IASetIndexBuffer(_indexBuffer, sizeof(ImDrawIdx) == 2 ? Format.R16_UInt : Format.R32_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vertexShader);
        ctx.VSSetConstantBuffer(0, _constantBuffer);
        ctx.PSSetShader(_pixelShader);
        ctx.PSSetSampler(0, _fontSampler);
        ctx.GSSetShader(null);
        ctx.HSSetShader(null);
        ctx.DSSetShader(null);
        ctx.CSSetShader(null);

        ctx.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f));
        ctx.OMSetDepthStencilState(_depthStencilState);
        ctx.RSSetState(_rasterizerState);
    }

    private void CreateFontsTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height);
        var texDesc = new Texture2DDescription(Format.R8G8B8A8_UNorm, width, height, 1, 1);
        var subResource = new SubresourceData(pixels, texDesc.Width * 4);
        using var texture = _device.CreateTexture2D(texDesc, new[] { subResource });
        var resViewDesc = new ShaderResourceViewDescription(
            texture,
            ShaderResourceViewDimension.Texture2D,
            Format.R8G8B8A8_UNorm,
            0,
            texDesc.MipLevels);
        io.Fonts.SetTexID(RegisterTexture(_device.CreateShaderResourceView(texture, resViewDesc)));
        io.Fonts.ClearTexData();
    }

    void CreateFontSampler()
    {
        var samplerDesc = new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Wrap,
            TextureAddressMode.Wrap,
            TextureAddressMode.Wrap,
            0f,
            0,
            ComparisonFunction.Always,
            0f,
            0f);

        _fontSampler = _device.CreateSamplerState(samplerDesc);
    }

    private IntPtr RegisterTexture(ID3D11ShaderResourceView texture)
    {
        var imguiId = texture.NativePointer;
        _textureResources.TryAdd(imguiId, texture);
        return imguiId;
    }

    private ID3D11ShaderResourceView? DeRegisterTexture(IntPtr texturePtr)
    {
        if (_textureResources.Remove(texturePtr, out var texture))
        {
            return texture;
        }

        return null;
        }

    private void DeRegisterAllTexture()
    {
        foreach (var key in _textureResources.Keys.ToArray())
        {
            DeRegisterTexture(key)?.Release();
        }
    }

    private void CreateDeviceObjects()
    {
        var vertexShaderCode =
            @"
                    cbuffer vertexBuffer : register(b0)
                    {
                        float4x4 ProjectionMatrix;
                    };

                    struct VS_INPUT
                    {
                        float2 pos : POSITION;
                        float4 col : COLOR0;
                        float2 uv  : TEXCOORD0;
                    };

                    struct PS_INPUT
                    {
                        float4 pos : SV_POSITION;
                        float4 col : COLOR0;
                        float2 uv  : TEXCOORD0;
                    };

                    PS_INPUT main(VS_INPUT input)
                    {
                        PS_INPUT output;
                        output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));
                        output.col = input.col;
                        output.uv  = input.uv;
                        return output;
                    }";
        Compiler.Compile(vertexShaderCode, "main", "vs", "vs_4_0", out _vertexShaderBlob, out _);
        if (_vertexShaderBlob == null)
            throw new Exception("error compiling vertex shader");

        _vertexShader = _device.CreateVertexShader(_vertexShaderBlob);

        var inputElements = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0, InputClassification.PerVertexData, 0),
        };

        _inputLayout = _device.CreateInputLayout(inputElements, _vertexShaderBlob);

        var constBufferDesc = new BufferDescription(
            VertexConstantBufferSize,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);

        _constantBuffer = _device.CreateBuffer(constBufferDesc);

        var pixelShaderCode =
            @"struct PS_INPUT
                    {
                        float4 pos : SV_POSITION;
                        float4 col : COLOR0;
                        float2 uv  : TEXCOORD0;
                    };

                    sampler sampler0;
                    Texture2D texture0;

                    float4 main(PS_INPUT input) : SV_Target
                    {
                        return input.col * texture0.Sample(sampler0, input.uv);
                    }";
        Compiler.Compile(pixelShaderCode, "main", "ps", "ps_4_0", out _pixelShaderBlob, out _);
        if (_pixelShaderBlob == null)
            throw new Exception("error compiling pixel shader");

        _pixelShader = _device.CreatePixelShader(_pixelShaderBlob);

        var blendDesc = new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha);
        _blendState = _device.CreateBlendState(blendDesc);

        var rasterDesc = new RasterizerDescription(CullMode.None, FillMode.Solid)
        {
            MultisampleEnable = false,
            ScissorEnable = true
        };
        _rasterizerState = _device.CreateRasterizerState(rasterDesc);

        var depthDesc = new DepthStencilDescription(false, DepthWriteMask.All, ComparisonFunction.Always);
        _depthStencilState = _device.CreateDepthStencilState(depthDesc);

        CreateFontsTexture();
        CreateFontSampler();
    }

#if false
        void BackupDX11State(ID3D11DeviceContext ctx)
        {
            old.ScissorRectsCount = ctx.RSGetScissorRects();
            if (old.ScissorRectsCount > 0)
            {
                ctx.RSGetScissorRects(ref old.ScissorRectsCount, old.ScissorRects);
            }

            old.ViewportsCount = ctx.RSGetViewports();
            if (old.ViewportsCount > 0)
            {
                ctx.RSGetScissorRects(ref old.ViewportsCount, old.ScissorRects);
            }

            old.RS = ctx.RSGetState();
            old.BlendState = ctx.OMGetBlendState(out old.BlendFactor, out old.SampleMask);
            ctx.OMGetDepthStencilState(out old.DepthStencilState, out old.StencilRef);
            ctx.PSGetShaderResources(0, old.PSShaderResource);
            ctx.PSGetSamplers(0, old.PSSampler);
            ctx.PSGetShader(out old.PS, old.PSInstances, ref old.PSInstancesCount);
            ctx.VSGetShader(out old.VS, old.VSInstances, ref old.VSInstancesCount);
            ctx.VSGetConstantBuffers(0, old.VSConstantBuffer);
            ctx.GSGetShader(out old.GS, old.GSInstances, ref old.GSInstancesCount);
            old.PrimitiveTopology = ctx.IAGetPrimitiveTopology();
            ctx.IAGetIndexBuffer(out old.IndexBuffer, out old.IndexBufferFormat, out old.IndexBufferOffset);
            ctx.IAGetVertexBuffers(0, 1, old.VertexBuffer, old.VertexBufferStride, old.VertexBufferOffset);
            old.InputLayout = ctx.IAGetInputLayout();
        }

        void RestoreDX11State(ID3D11DeviceContext ctx)
        {
            ctx.RSSetScissorRects(old.ScissorRects);
            ctx.RSSetViewports(old.Viewports);
            ctx.RSSetState(old.RS);
            old.RS?.Release();
            ctx.OMSetBlendState(old.BlendState, old.BlendFactor, old.SampleMask);
            old.BlendState?.Release();
            ctx.OMSetDepthStencilState(old.DepthStencilState, old.StencilRef);
            old.DepthStencilState?.Release();
            ctx.PSSetShaderResources(0, old.PSShaderResource);
            old.PSShaderResource[0]?.Release();
            ctx.PSSetSamplers(0, old.PSSampler);
            old.PSSampler[0]?.Release();
            ctx.PSSetShader(old.PS, old.PSInstances, old.PSInstancesCount);
            old.PS?.Release();
            for (int i = 0; i < old.PSInstancesCount; i++) old.PSInstances[i]?.Release();
            ctx.VSSetShader(old.VS, old.VSInstances, old.VSInstancesCount);
            old.VS?.Release();
            ctx.VSSetConstantBuffers(0, old.VSConstantBuffer);
            old.VSConstantBuffer[0]?.Release();
            ctx.GSSetShader(old.GS, old.GSInstances, old.GSInstancesCount);
            for (int i = 0; i < old.VSInstancesCount; i++) old.VSInstances[i]?.Release();
            ctx.IASetPrimitiveTopology(old.PrimitiveTopology);
            ctx.IASetIndexBuffer(old.IndexBuffer, old.IndexBufferFormat, old.IndexBufferOffset);
            old.IndexBuffer?.Release();
            ctx.IASetVertexBuffers(0, 1, old.VertexBuffer, old.VertexBufferStride, old.VertexBufferOffset);
            old.VertexBuffer[0]?.Release();
            ctx.IASetInputLayout(old.InputLayout);
            old.InputLayout?.Release();
        }

        class BACKUP_DX11_STATE
        {
            public int ScissorRectsCount = 0, ViewportsCount = 0;
            public RawRect[] ScissorRects = new RawRect[16];
            public Viewport[] Viewports = new Viewport[16];
            public ID3D11RasterizerState RS = default;
            public ID3D11BlendState BlendState = default;
            public Color4 BlendFactor = default;
            public int SampleMask = 0;
            public int StencilRef = 0;
            public ID3D11DepthStencilState DepthStencilState = default;
            public ID3D11ShaderResourceView[] PSShaderResource = new ID3D11ShaderResourceView[1];
            public ID3D11SamplerState[] PSSampler = new ID3D11SamplerState[1];
            public ID3D11PixelShader PS = default;
            public ID3D11VertexShader VS = default;
            public ID3D11GeometryShader GS = default;
            public int PSInstancesCount = 256, VSInstancesCount = 256, GSInstancesCount = 256;
            public ID3D11ClassInstance[] PSInstances = new ID3D11ClassInstance[256];
            public ID3D11ClassInstance[] VSInstances = new ID3D11ClassInstance[256];
            public ID3D11ClassInstance[] GSInstances = new ID3D11ClassInstance[256];
            public PrimitiveTopology PrimitiveTopology = 0;
            public ID3D11Buffer IndexBuffer = default;
            public ID3D11Buffer[] VertexBuffer = new ID3D11Buffer[1], VSConstantBuffer = new ID3D11Buffer[1];
            public int IndexBufferOffset = 0;
            public int[] VertexBufferStride = new int[1], VertexBufferOffset = new int[1];
            public Format IndexBufferFormat = 0;
            public ID3D11InputLayout InputLayout = default;
        } readonly BACKUP_DX11_STATE old = new();
#endif
}
