using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using PhonePlayLink.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace PhonePlayLink.Rendering;

/// <summary>
/// Backend render Dear ImGui (ImGui.NET) trực tiếp bằng Vortice.Direct3D11 với overhead tối thiểu.
/// Chia sẻ chính ID3D11Device & ID3D11DeviceContext của D3D11VideoRenderer.
/// </summary>
public sealed unsafe class ImGuiD3D11Backend : IDisposable
{
    private const string ImGuiShaderSource = """
        cbuffer VertexConstantBuffer : register(b0) {
            float4x4 ProjectionMatrix;
        };

        struct VS_INPUT {
            float2 pos : POSITION;
            float2 uv  : TEXCOORD0;
            float4 col : COLOR0;
        };

        struct PS_INPUT {
            float4 pos : SV_POSITION;
            float4 col : COLOR0;
            float2 uv  : TEXCOORD0;
        };

        PS_INPUT VSMain(VS_INPUT input) {
            PS_INPUT output;
            output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));
            output.col = input.col;
            output.uv  = input.uv;
            return output;
        }

        sampler sampler0 : register(s0);
        Texture2D texture0 : register(t0);

        float4 PSMain(PS_INPUT input) : SV_Target {
            return input.col * texture0.Sample(sampler0, input.uv);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11DepthStencilState _depthStencilState;
    private readonly ID3D11SamplerState _fontSampler;
    private readonly ID3D11Texture2D _fontTexture;
    private readonly ID3D11ShaderResourceView _fontSrv;

    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private int _vertexBufferSize = 5000;
    private int _indexBufferSize = 10000;
    private bool _disposed;

    public ImFontPtr HeadingFont { get; private set; }

    public ImGuiD3D11Backend(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        IntPtr imguiCtx = ImGui.CreateContext();
        ImGui.SetCurrentContext(imguiCtx);

        ImGuiIOPtr io = ImGui.GetIO();
        io.NativePtr->IniFilename = null; // Không ghi imgui.ini ra thư mục làm việc
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        ImFontPtr headingFont = ApplyStyleAndFonts(io);
        HeadingFont = headingFont;

        ReadOnlyMemory<byte> vsBytes = Compiler.Compile(ImGuiShaderSource, "VSMain", "ImGuiD3D11.hlsl", "vs_5_0");
        ReadOnlyMemory<byte> psBytes = Compiler.Compile(ImGuiShaderSource, "PSMain", "ImGuiD3D11.hlsl", "ps_5_0");

        _vertexShader = _device.CreateVertexShader(vsBytes.Span);
        _pixelShader = _device.CreatePixelShader(psBytes.Span);

        var inputElements = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0, InputClassification.PerVertexData, 0)
        };
        _inputLayout = _device.CreateInputLayout(inputElements, vsBytes.Span);

        _constantBuffer = _device.CreateBuffer(
            (uint)Unsafe.SizeOf<Matrix4x4>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);

        var blendDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false
        };
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blendState = _device.CreateBlendState(blendDesc);

        _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            ScissorEnable = true,
            DepthClipEnable = true
        });

        _depthStencilState = _device.CreateDepthStencilState(DepthStencilDescription.None);
        _fontSampler = _device.CreateSamplerState(SamplerDescription.LinearWrap);

        (_fontTexture, _fontSrv) = CreateFontTexture(io);
        CreateBuffers(_vertexBufferSize, _indexBufferSize);

        Logger.Ok("ImGui", "Đã khởi tạo ImGui.NET D3D11 Backend (Zero-Overhead Overlay)");
    }

    public void BeginFrame(int width, int height, float deltaSeconds)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Vector2(Math.Max(1, width), Math.Max(1, height));
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = deltaSeconds > 0f ? deltaSeconds : (1f / 60f);
        ImGui.NewFrame();
    }

    public void RenderDrawData(ImDrawDataPtr drawData)
    {
        if (_disposed || !drawData.Valid || drawData.CmdListsCount == 0 || drawData.DisplaySize.X <= 0 || drawData.DisplaySize.Y <= 0)
            return;

        if (_vertexBuffer is null || _vertexBufferSize < drawData.TotalVtxCount)
        {
            _vertexBuffer?.Dispose();
            _vertexBufferSize = Math.Max(drawData.TotalVtxCount + 5000, _vertexBufferSize * 2);
            _vertexBuffer = _device.CreateBuffer(
                (uint)(_vertexBufferSize * sizeof(ImDrawVert)),
                BindFlags.VertexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write);
        }

        if (_indexBuffer is null || _indexBufferSize < drawData.TotalIdxCount)
        {
            _indexBuffer?.Dispose();
            _indexBufferSize = Math.Max(drawData.TotalIdxCount + 10000, _indexBufferSize * 2);
            _indexBuffer = _device.CreateBuffer(
                (uint)(_indexBufferSize * sizeof(ushort)),
                BindFlags.IndexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write);
        }

        // Upload vertex + index buffer
        MappedSubresource vtxRes = _context.Map(_vertexBuffer, 0, MapMode.WriteDiscard);
        MappedSubresource idxRes = _context.Map(_indexBuffer, 0, MapMode.WriteDiscard);
        try
        {
            var vtxDst = (ImDrawVert*)vtxRes.DataPointer;
            var idxDst = (ushort*)idxRes.DataPointer;

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                ImDrawListPtr cmdList = drawData.CmdLists[n];
                long vtxBytes = (long)cmdList.VtxBuffer.Size * sizeof(ImDrawVert);
                long idxBytes = (long)cmdList.IdxBuffer.Size * sizeof(ushort);
                Buffer.MemoryCopy((void*)cmdList.VtxBuffer.Data, vtxDst, vtxBytes, vtxBytes);
                Buffer.MemoryCopy((void*)cmdList.IdxBuffer.Data, idxDst, idxBytes, idxBytes);
                vtxDst += cmdList.VtxBuffer.Size;
                idxDst += cmdList.IdxBuffer.Size;
            }
        }
        finally
        {
            _context.Unmap(_vertexBuffer, 0);
            _context.Unmap(_indexBuffer, 0);
        }

        // Cập nhật Orthographic Projection Matrix
        float L = drawData.DisplayPos.X;
        float R = drawData.DisplayPos.X + drawData.DisplaySize.X;
        float T = drawData.DisplayPos.Y;
        float B = drawData.DisplayPos.Y + drawData.DisplaySize.Y;
        var mvp = new Matrix4x4(
            2f / (R - L), 0f, 0f, (R + L) / (L - R),
            0f, 2f / (T - B), 0f, (T + B) / (B - T),
            0f, 0f, 0.5f, 0.5f,
            0f, 0f, 0f, 1f);

        MappedSubresource cbRes = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        try
        {
            *(Matrix4x4*)cbRes.DataPointer = mvp;
        }
        finally
        {
            _context.Unmap(_constantBuffer, 0);
        }

        _context.RSSetViewport(new Viewport(0, 0, drawData.DisplaySize.X, drawData.DisplaySize.Y));
        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffer(0, _vertexBuffer, (uint)sizeof(ImDrawVert), 0);
        _context.IASetIndexBuffer(_indexBuffer, Format.R16_UInt, 0);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetSampler(0, _fontSampler);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(_depthStencilState, 0);
        _context.RSSetState(_rasterizerState);

        int globalVtxOffset = 0;
        int globalIdxOffset = 0;
        Vector2 clipOff = drawData.DisplayPos;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[n];
            for (int cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++)
            {
                ImDrawCmdPtr pcmd = cmdList.CmdBuffer[cmdI];
                if (pcmd.UserCallback != IntPtr.Zero)
                    continue;

                Vector4 cr = pcmd.ClipRect;
                int left = (int)(cr.X - clipOff.X);
                int top = (int)(cr.Y - clipOff.Y);
                int right = (int)(cr.Z - clipOff.X);
                int bottom = (int)(cr.W - clipOff.Y);
                if (right <= left || bottom <= top)
                    continue;

                _context.RSSetScissorRect(new Vortice.RawRect(left, top, right, bottom));

                // Hiện tại chỉ có 1 Font Atlas texture
                _context.PSSetShaderResource(0, _fontSrv);
                _context.DrawIndexed(
                    pcmd.ElemCount,
                    (uint)(pcmd.IdxOffset + globalIdxOffset),
                    (int)(pcmd.VtxOffset + globalVtxOffset));
            }
            globalIdxOffset += cmdList.IdxBuffer.Size;
            globalVtxOffset += cmdList.VtxBuffer.Size;
        }

        // Khôi phục BlendState & DepthStencilState về mặc định để không ảnh hưởng Video Render Pass tiếp theo
        _context.OMSetBlendState(null, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(null, 0);
    }

    private void CreateBuffers(int vtxCount, int idxCount)
    {
        _vertexBuffer = _device.CreateBuffer(
            (uint)(vtxCount * sizeof(ImDrawVert)),
            BindFlags.VertexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);
        _indexBuffer = _device.CreateBuffer(
            (uint)(idxCount * sizeof(ushort)),
            BindFlags.IndexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);
    }

    private (ID3D11Texture2D, ID3D11ShaderResourceView) CreateFontTexture(ImGuiIOPtr io)
    {
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);

        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        };

        var subData = new SubresourceData((IntPtr)pixels, (uint)(width * bytesPerPixel), 0);
        ID3D11Texture2D tex = _device.CreateTexture2D(texDesc, [subData]);
        ID3D11ShaderResourceView srv = _device.CreateShaderResourceView(tex);

        io.Fonts.SetTexID(srv.NativePointer);
        io.Fonts.ClearTexData();
        return (tex, srv);
    }

    private static ImFontPtr ApplyStyleAndFonts(ImGuiIOPtr io)
    {
        ImGui.StyleColorsDark();
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 8f;
        style.ChildRounding = 6f;
        style.FrameRounding = 6f;
        style.PopupRounding = 8f;
        style.TabRounding = 6f;
        style.ScrollbarRounding = 6f;
        style.GrabRounding = 4f;
        style.WindowBorderSize = 1f;
        style.ChildBorderSize = 1f;
        style.FrameBorderSize = 1f;
        style.WindowPadding = new Vector2(12f, 12f);
        style.FramePadding = new Vector2(8f, 5f);
        style.ItemSpacing = new Vector2(8f, 6f);

        // Dark Gaming Palette: Nền #12151C, Panel #1A1F2B, Accent Cyan #00B4D8
        var colors = style.Colors;
        colors[(int)ImGuiCol.Text] = new Vector4(0.92f, 0.94f, 0.98f, 1.00f);
        colors[(int)ImGuiCol.TextDisabled] = new Vector4(0.55f, 0.60f, 0.70f, 1.00f);
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.071f, 0.082f, 0.110f, 0.95f); // #12151C
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0.102f, 0.122f, 0.169f, 0.94f);  // #1A1F2B
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.102f, 0.122f, 0.169f, 0.98f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.18f, 0.22f, 0.30f, 0.85f);
        colors[(int)ImGuiCol.FrameBg] = new Vector4(0.102f, 0.122f, 0.169f, 1.00f);  // #1A1F2B
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.14f, 0.17f, 0.24f, 1.00f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.00f, 0.45f, 0.56f, 0.75f);
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.071f, 0.082f, 0.110f, 1.00f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.09f, 0.13f, 0.20f, 1.00f);
        colors[(int)ImGuiCol.Button] = new Vector4(0.000f, 0.706f, 0.847f, 0.86f);   // #00B4D8
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.11f, 0.78f, 0.92f, 1.00f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.00f, 0.58f, 0.71f, 1.00f);
        colors[(int)ImGuiCol.Header] = new Vector4(0.000f, 0.706f, 0.847f, 0.35f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.000f, 0.706f, 0.847f, 0.55f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.000f, 0.706f, 0.847f, 0.75f);
        colors[(int)ImGuiCol.Tab] = new Vector4(0.102f, 0.122f, 0.169f, 1.00f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.000f, 0.706f, 0.847f, 0.65f);
        colors[(int)ImGuiCol.TabSelected] = new Vector4(0.000f, 0.706f, 0.847f, 0.90f);
        colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.000f, 0.706f, 0.847f, 1.00f);
        colors[(int)ImGuiCol.SliderGrabActive] = new Vector4(0.25f, 0.86f, 0.97f, 1.00f);
        colors[(int)ImGuiCol.CheckMark] = new Vector4(0.000f, 0.82f, 0.96f, 1.00f);
        colors[(int)ImGuiCol.Separator] = new Vector4(0.20f, 0.25f, 0.34f, 0.80f);

        // Nạp font hệ thống có hỗ trợ đầy đủ glyph tiếng Việt
        string[] candidates =
        [
            @"C:\Windows\Fonts\segoeui.ttf",
            @"C:\Windows\Fonts\arial.ttf",
            @"C:\Windows\Fonts\tahoma.ttf"
        ];
        foreach (string fontPath in candidates)
        {
            if (File.Exists(fontPath))
            {
                io.Fonts.AddFontFromFileTTF(fontPath, 16f, null, io.Fonts.GetGlyphRangesVietnamese());
                return io.Fonts.AddFontFromFileTTF(fontPath, 23f, null, io.Fonts.GetGlyphRangesVietnamese());
            }
        }
        return io.Fonts.AddFontDefault();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _fontSrv.Dispose();
        _fontTexture.Dispose();
        _fontSampler.Dispose();
        _depthStencilState.Dispose();
        _rasterizerState.Dispose();
        _blendState.Dispose();
        _constantBuffer.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();

        ImGui.DestroyContext();
    }
}
