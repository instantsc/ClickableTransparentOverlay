﻿// <auto-generated />
namespace ClickableTransparentOverlay
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using ImGuiNET;
    using NativeLibraryLoader;
    using Veldrid;

    /// <summary>
    /// A modified version of ImGui.NET.SampleProgram's ImGuiController.
    /// Manages input for ImGui and handles rendering ImGui's DrawLists with Veldrid.
    /// </summary>
    public sealed class ImGuiController : IDisposable
    {
#pragma warning disable CS0169 // Force Copy this DLL
        private readonly DefaultPathResolver useless;
#pragma warning restore CS0169

        private readonly IntPtr fontAtlasID = (IntPtr)1;

        // Image trackers
        private readonly Dictionary<TextureView, ResourceSetInfo> setsByView
            = new Dictionary<TextureView, ResourceSetInfo>();

        private readonly Dictionary<Texture, TextureView> autoViewsByTexture
            = new Dictionary<Texture, TextureView>();

        private readonly Dictionary<IntPtr, ResourceSetInfo> viewsById
            = new Dictionary<IntPtr, ResourceSetInfo>();

        private readonly List<IDisposable> ownedResources
            = new List<IDisposable>();

        private int lastAssignedID = 100;

        private GraphicsDevice gd;
        private bool frameBegun;

        // Veldrid objects
        private DeviceBuffer vertexBuffer;
        private DeviceBuffer indexBuffer;
        private DeviceBuffer projMatrixBuffer;
        private Texture fontTexture;
        private TextureView fontTextureView;
        private Shader vertexShader;
        private Shader fragmentShader;
        private ResourceLayout layout;
        private ResourceLayout textureLayout;
        private Pipeline pipeline;
        private ResourceSet mainResourceSet;
        private ResourceSet fontTextureResourceSet;

        private int windowWidth;
        private int windowHeight;
        private Vector2 scaleFactor = Vector2.One;

        /// <summary>
        /// Initializes a new instance of the <see cref="ImGuiController"/> class.
        /// </summary>
        /// <param name="gd">
        /// Graphic Device
        /// </param>
        /// <param name="outputDescription">
        /// Output Description
        /// </param>
        /// <param name="width">
        /// SDL2Window Window width
        /// </param>
        /// <param name="height">
        /// SDL2Window Window height
        /// </param>
        /// <param name="fps">
        /// desired FPS of the ImGui Overlay
        /// </param>
        public ImGuiController(GraphicsDevice gd, OutputDescription outputDescription, int width, int height, int fps)
        {
            this.gd = gd;
            this.windowWidth = width;
            this.windowHeight = height;

            IntPtr context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);

            ImGui.GetIO().Fonts.AddFontDefault();

            this.CreateDeviceResources(gd, outputDescription);
            SetKeyMappings();

            this.SetPerFrameImGuiData(1f / fps);

            ImGui.NewFrame();
            this.frameBegun = true;
        }

        /// <summary>
        /// Updates the ImGui about the SDL2Window Size
        /// </summary>
        /// <param name="width">
        /// Width of the SDL2Window
        /// </param>
        /// <param name="height">
        /// Height of the SDL2Window
        /// </param>
        public void WindowResized(int width, int height)
        {
            this.windowWidth = width;
            this.windowHeight = height;
        }

        /// <summary>
        /// Disposes the resources acquired by the ImGuiController class.
        /// </summary>
        public void DestroyDeviceObjects()
        {
            this.Dispose();
        }

        /// <summary>
        /// Initializes different resources for ImGui Controller class.
        /// </summary>
        /// <param name="gd">
        /// Graphic Device
        /// </param>
        /// <param name="outputDescription">
        /// Output Description
        /// </param>
        public void CreateDeviceResources(GraphicsDevice gd, OutputDescription outputDescription)
        {
            this.gd = gd;
            ResourceFactory factory = gd.ResourceFactory;
            this.vertexBuffer = factory.CreateBuffer(new BufferDescription(10000, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            this.vertexBuffer.Name = "ImGui.NET Vertex Buffer";
            this.indexBuffer = factory.CreateBuffer(new BufferDescription(2000, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            this.indexBuffer.Name = "ImGui.NET Index Buffer";
            this.RecreateFontDeviceTexture(gd);

            this.projMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            this.projMatrixBuffer.Name = "ImGui.NET Projection Buffer";

            byte[] vertexShaderBytes = this.LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-vertex", ShaderStages.Vertex);
            byte[] fragmentShaderBytes = this.LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-frag", ShaderStages.Fragment);
            this.vertexShader = factory.CreateShader(new ShaderDescription(ShaderStages.Vertex, vertexShaderBytes, "VS"));
            this.fragmentShader = factory.CreateShader(new ShaderDescription(ShaderStages.Fragment, fragmentShaderBytes, "FS"));

            VertexLayoutDescription[] vertexLayouts = new VertexLayoutDescription[]
            {
                new VertexLayoutDescription(
                    new VertexElementDescription("in_position", VertexElementSemantic.Position, VertexElementFormat.Float2),
                    new VertexElementDescription("in_texCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                    new VertexElementDescription("in_color", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm))
            };

            this.layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ProjectionMatrixBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
            this.textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));

            GraphicsPipelineDescription pd = new GraphicsPipelineDescription(
                BlendStateDescription.SingleAlphaBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, true),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(vertexLayouts, new[] { this.vertexShader, this.fragmentShader }),
                new ResourceLayout[] { this.layout, this.textureLayout },
                outputDescription);
            this.pipeline = factory.CreateGraphicsPipeline(ref pd);

            this.mainResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                this.layout, this.projMatrixBuffer, gd.PointSampler));

            this.fontTextureResourceSet = factory.CreateResourceSet(
                new ResourceSetDescription(this.textureLayout, this.fontTextureView));
        }

        /// <summary>
        /// Gets or creates a handle for a texture to be drawn with ImGui.
        /// Pass the returned handle to Image() or ImageButton().
        /// </summary>
        /// <param name="factory">
        /// Resource Factory
        /// </param>
        /// <param name="textureView">
        /// Texture View
        /// </param>
        /// <returns>
        /// Creates ImGui Binding
        /// </returns>
        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, TextureView textureView)
        {
            if (!this.setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
            {
                ResourceSet resourceSet = factory.CreateResourceSet(
                    new ResourceSetDescription(this.textureLayout, textureView));

                rsi = new ResourceSetInfo(this.GetNextImGuiBindingID(), resourceSet);

                this.setsByView.Add(textureView, rsi);
                this.viewsById.Add(rsi.ImGuiBinding, rsi);
                this.ownedResources.Add(resourceSet);
            }

            return rsi.ImGuiBinding;
        }

        /// <summary>
        /// Gets or creates a handle for a texture to be drawn with ImGui.
        /// Pass the returned handle to Image() or ImageButton().
        /// </summary>
        /// <param name="factory">
        /// Resource Factory
        /// </param>
        /// <param name="texture">
        /// Texture information
        /// </param>
        /// <returns>
        /// Pointer to the resource
        /// </returns>
        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, Texture texture)
        {
            if (!this.autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
            {
                textureView = factory.CreateTextureView(texture);
                this.autoViewsByTexture.Add(texture, textureView);
                this.ownedResources.Add(textureView);
            }

            return this.GetOrCreateImGuiBinding(factory, textureView);
        }

        /// <summary>
        /// Retrieves the shader texture binding for the given helper handle.
        /// </summary>
        /// <param name="imGuiBinding">
        /// ImGui Binding resource pointer.
        /// </param>
        /// <returns>
        /// Resource
        /// </returns>
        public ResourceSet GetImageResourceSet(IntPtr imGuiBinding)
        {
            if (!this.viewsById.TryGetValue(imGuiBinding, out ResourceSetInfo tvi))
            {
                throw new InvalidOperationException("No registered ImGui binding with id " + imGuiBinding.ToString());
            }

            return tvi.ResourceSet;
        }

        /// <summary>
        /// Clears the cache images.
        /// </summary>
        public void ClearCachedImageResources()
        {
            foreach (IDisposable resource in this.ownedResources)
            {
                resource.Dispose();
            }

            this.ownedResources.Clear();
            this.setsByView.Clear();
            this.viewsById.Clear();
            this.autoViewsByTexture.Clear();
            this.lastAssignedID = 100;
        }

        /// <summary>
        /// Recreates the device texture used to render text.
        /// </summary>
        /// <param name="gd">
        /// Graphic Device
        /// </param>
        public unsafe void RecreateFontDeviceTexture(GraphicsDevice gd)
        {
            ImGuiIOPtr io = ImGui.GetIO();

            // Build
            byte* pixels;
            int width, height, bytesPerPixel;
            io.Fonts.GetTexDataAsRGBA32(out pixels, out width, out height, out bytesPerPixel);

            // Store our identifier
            io.Fonts.SetTexID(this.fontAtlasID);

            this.fontTexture = gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                (uint)width,
                (uint)height,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled));
            this.fontTexture.Name = "ImGui.NET Font Texture";
            gd.UpdateTexture(
                this.fontTexture,
                (IntPtr)pixels,
                (uint)(bytesPerPixel * width * height),
                0,
                0,
                0,
                (uint)width,
                (uint)height,
                1,
                0,
                0);
            this.fontTextureView = gd.ResourceFactory.CreateTextureView(this.fontTexture);

            io.Fonts.ClearTexData();
        }

        /// <summary>
        /// Renders the ImGui draw list data.
        /// This method requires a <see cref="GraphicsDevice"/> because it may create new DeviceBuffers if the size of vertex
        /// or index data has increased beyond the capacity of the existing buffers.
        /// A <see cref="CommandList"/> is needed to submit drawing and resource update commands.
        /// </summary>
        /// <param name="gd">
        /// Graphic Device
        /// </param>
        /// <param name="cl">
        /// Command List
        /// </param>
        public void Render(GraphicsDevice gd, CommandList cl)
        {
            if (this.frameBegun)
            {
                this.frameBegun = false;
                ImGui.Render();
                this.RenderImDrawData(ImGui.GetDrawData(), gd, cl);
            }
        }

        /// <summary>
        /// Initilizes a new frame
        /// </summary>
        /// <param name="deltaSeconds">
        /// FPS delay
        /// </param>
        public void InitlizeFrame(float deltaSeconds)
        {
            if (this.frameBegun)
            {
                ImGui.Render();
            }

            this.SetPerFrameImGuiData(deltaSeconds);

            this.frameBegun = true;
            ImGui.NewFrame();
        }

        /// <summary>
        /// Frees all graphics resources used by the renderer.
        /// </summary>
        public void Dispose()
        {
            this.vertexBuffer.Dispose();
            this.indexBuffer.Dispose();
            this.projMatrixBuffer.Dispose();
            this.fontTexture.Dispose();
            this.fontTextureView.Dispose();
            this.vertexShader.Dispose();
            this.fragmentShader.Dispose();
            this.layout.Dispose();
            this.textureLayout.Dispose();
            this.pipeline.Dispose();
            this.mainResourceSet.Dispose();
            this.fontTextureResourceSet.Dispose();

            foreach (IDisposable resource in this.ownedResources)
            {
                resource.Dispose();
            }
        }

        /// <summary>
        /// Allows ImGui to identify the Keys
        /// </summary>
        private static void SetKeyMappings()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.KeyMap[(int)ImGuiKey.Tab] = (int)System.Windows.Forms.Keys.Tab;
            io.KeyMap[(int)ImGuiKey.LeftArrow] = (int)System.Windows.Forms.Keys.Left;
            io.KeyMap[(int)ImGuiKey.RightArrow] = (int)System.Windows.Forms.Keys.Right;
            io.KeyMap[(int)ImGuiKey.UpArrow] = (int)System.Windows.Forms.Keys.Up;
            io.KeyMap[(int)ImGuiKey.DownArrow] = (int)System.Windows.Forms.Keys.Down;
            io.KeyMap[(int)ImGuiKey.PageUp] = (int)System.Windows.Forms.Keys.PageUp;
            io.KeyMap[(int)ImGuiKey.PageDown] = (int)System.Windows.Forms.Keys.PageDown;
            io.KeyMap[(int)ImGuiKey.Home] = (int)System.Windows.Forms.Keys.Home;
            io.KeyMap[(int)ImGuiKey.End] = (int)System.Windows.Forms.Keys.End;
            io.KeyMap[(int)ImGuiKey.Delete] = (int)System.Windows.Forms.Keys.Delete;
            io.KeyMap[(int)ImGuiKey.Backspace] = (int)System.Windows.Forms.Keys.Back;
            io.KeyMap[(int)ImGuiKey.Enter] = (int)System.Windows.Forms.Keys.Enter;
            io.KeyMap[(int)ImGuiKey.Escape] = (int)System.Windows.Forms.Keys.Escape;

            // io.KeyMap[(int)ImGuiKey.COUNT] = (int)System.Windows.Forms.Keys.un;
            io.KeyMap[(int)ImGuiKey.Insert] = (int)System.Windows.Forms.Keys.Insert;
            io.KeyMap[(int)ImGuiKey.Space] = (int)System.Windows.Forms.Keys.Space;
            io.KeyMap[(int)ImGuiKey.A] = (int)System.Windows.Forms.Keys.A;
            io.KeyMap[(int)ImGuiKey.C] = (int)System.Windows.Forms.Keys.C;
            io.KeyMap[(int)ImGuiKey.V] = (int)System.Windows.Forms.Keys.V;
            io.KeyMap[(int)ImGuiKey.X] = (int)System.Windows.Forms.Keys.X;
            io.KeyMap[(int)ImGuiKey.Y] = (int)System.Windows.Forms.Keys.Y;
            io.KeyMap[(int)ImGuiKey.Z] = (int)System.Windows.Forms.Keys.Z;
        }

        /// <summary>
        /// Get the Next ImGui Binding ID.
        /// </summary>
        /// <returns>
        /// ImGui next binding ID.
        /// </returns>
        private IntPtr GetNextImGuiBindingID()
        {
            int newID = this.lastAssignedID++;
            return (IntPtr)newID;
        }

        /// <summary>
        /// Loading Shader Code
        /// </summary>
        /// <param name="factory">
        /// Resource Factory
        /// </param>
        /// <param name="name">
        /// Shader file name
        /// </param>
        /// <param name="stage">
        /// Shader stage
        /// </param>
        /// <returns>
        /// Returns shader byte code
        /// </returns>
        private byte[] LoadEmbeddedShaderCode(ResourceFactory factory, string name, ShaderStages stage)
        {
            switch (factory.BackendType)
            {
                case GraphicsBackend.Direct3D11:
                    string resourceName = name + ".hlsl.bytes";
                    return this.GetEmbeddedResourceBytes(resourceName);
                default:
                    throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Get embedded resource file in bytes
        /// </summary>
        /// <param name="resourceName">
        /// Name of the resource file
        /// </param>
        /// <returns>
        /// Byte code of the resource file
        /// </returns>
        private byte[] GetEmbeddedResourceBytes(string resourceName)
        {
            Assembly assembly = typeof(ImGuiController).Assembly;
            using (Stream s = assembly.GetManifestResourceStream(resourceName))
            {
                byte[] ret = new byte[s.Length];
                s.Read(ret, 0, (int)s.Length);
                return ret;
            }
        }

        /// <summary>
        /// Sets per-frame data based on the associated window.
        /// This is called by Update(float).
        /// </summary>
        /// <param name="deltaSeconds">
        /// FPS delay
        /// </param>
        private void SetPerFrameImGuiData(float deltaSeconds)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new Vector2(
                this.windowWidth / this.scaleFactor.X,
                this.windowHeight / this.scaleFactor.Y);
            io.DisplayFramebufferScale = this.scaleFactor;
            io.DeltaTime = deltaSeconds; // DeltaTime is in seconds.
        }

        /// <summary>
        /// Draw the ImGui graphic data
        /// </summary>
        /// <param name="draw_data">
        /// ImGui data to draw
        /// </param>
        /// <param name="gd">
        /// Graphic Device
        /// </param>
        /// <param name="cl">
        /// Command List
        /// </param>
        private void RenderImDrawData(ImDrawDataPtr draw_data, GraphicsDevice gd, CommandList cl)
        {
            uint vertexOffsetInVertices = 0;
            uint indexOffsetInElements = 0;

            if (draw_data.CmdListsCount == 0)
            {
                return;
            }

            uint totalVBSize = (uint)(draw_data.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>());
            if (totalVBSize > this.vertexBuffer.SizeInBytes)
            {
                gd.DisposeWhenIdle(this.vertexBuffer);
                this.vertexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    (uint)(totalVBSize * 1.5f), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            }

            uint totalIBSize = (uint)(draw_data.TotalIdxCount * sizeof(ushort));
            if (totalIBSize > this.indexBuffer.SizeInBytes)
            {
                gd.DisposeWhenIdle(this.indexBuffer);
                this.indexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    (uint)(totalIBSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            }

            for (int i = 0; i < draw_data.CmdListsCount; i++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdListsRange[i];

                cl.UpdateBuffer(
                    this.vertexBuffer,
                    vertexOffsetInVertices * (uint)Unsafe.SizeOf<ImDrawVert>(),
                    cmd_list.VtxBuffer.Data,
                    (uint)(cmd_list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>()));

                cl.UpdateBuffer(
                    this.indexBuffer,
                    indexOffsetInElements * sizeof(ushort),
                    cmd_list.IdxBuffer.Data,
                    (uint)(cmd_list.IdxBuffer.Size * sizeof(ushort)));

                vertexOffsetInVertices += (uint)cmd_list.VtxBuffer.Size;
                indexOffsetInElements += (uint)cmd_list.IdxBuffer.Size;
            }

            // Setup orthographic projection matrix into our constant buffer
            ImGuiIOPtr io = ImGui.GetIO();
            Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(
                0f,
                io.DisplaySize.X,
                io.DisplaySize.Y,
                0.0f,
                -1.0f,
                1.0f);

            this.gd.UpdateBuffer(this.projMatrixBuffer, 0, ref mvp);

            cl.SetVertexBuffer(0, this.vertexBuffer);
            cl.SetIndexBuffer(this.indexBuffer, IndexFormat.UInt16);
            cl.SetPipeline(this.pipeline);
            cl.SetGraphicsResourceSet(0, this.mainResourceSet);

            draw_data.ScaleClipRects(io.DisplayFramebufferScale);

            // Render command lists
            int vtx_offset = 0;
            int idx_offset = 0;
            for (int n = 0; n < draw_data.CmdListsCount; n++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdListsRange[n];
                for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
                {
                    ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
                    if (pcmd.UserCallback != IntPtr.Zero)
                    {
                        throw new NotImplementedException();
                    }
                    else
                    {
                        if (pcmd.TextureId != IntPtr.Zero)
                        {
                            if (pcmd.TextureId == this.fontAtlasID)
                            {
                                cl.SetGraphicsResourceSet(
                                    1, this.fontTextureResourceSet);
                            }
                            else
                            {
                                cl.SetGraphicsResourceSet(
                                    1, this.GetImageResourceSet(pcmd.TextureId));
                            }
                        }

                        cl.SetScissorRect(
                            0,
                            (uint)pcmd.ClipRect.X,
                            (uint)pcmd.ClipRect.Y,
                            (uint)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
                            (uint)(pcmd.ClipRect.W - pcmd.ClipRect.Y));

                        cl.DrawIndexed(pcmd.ElemCount, 1, (uint)idx_offset, vtx_offset, 0);
                    }

                    idx_offset += (int)pcmd.ElemCount;
                }

                vtx_offset += cmd_list.VtxBuffer.Size;
            }
        }

        /// <summary>
        /// ResourceSetInfo
        /// </summary>
        private struct ResourceSetInfo
        {
            public readonly IntPtr ImGuiBinding;
            public readonly ResourceSet ResourceSet;

            public ResourceSetInfo(IntPtr imGuiBinding, ResourceSet resourceSet)
            {
                this.ImGuiBinding = imGuiBinding;
                this.ResourceSet = resourceSet;
            }
        }
    }
}
