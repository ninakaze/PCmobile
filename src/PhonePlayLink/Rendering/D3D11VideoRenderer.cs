using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using ImGuiNET;
using PhonePlayLink.Core;
using PhonePlayLink.Input;
using PhonePlayLink.Media;
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
/// Renderer Direct3D 11 độ trễ siêu thấp:
/// - SwapChain FlipDiscard + DXGI_PRESENT_ALLOW_TEARING (bỏ qua VSync, Present Immediate)
/// - Hỗ trợ Zero-Copy GPU-to-GPU từ FFmpeg D3D11VA (AV_PIX_FMT_D3D11)
/// - Pixel Shader chuyển đổi NV12 và YUV420P (I420) -> RGBA ngay trên GPU
/// - Hỗ trợ giữ tỷ lệ khung hình (Letterbox/Pillarbox) hoặc kéo đầy màn hình
/// </summary>
public sealed unsafe class D3D11VideoRenderer : IDisposable
{
    private const string ShaderSource = """
        struct VSOut {
            float4 pos : SV_Position;
            float2 uv  : TEXCOORD0;
        };

        VSOut VSMain(uint id : SV_VertexID) {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.uv = uv;
            o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            return o;
        }

        Texture2D<float>  TexY  : register(t0);
        Texture2D<float2> TexUV : register(t1);
        Texture2D<float>  TexU  : register(t1);
        Texture2D<float>  TexV  : register(t2);
        SamplerState LinearSampler : register(s0);

        cbuffer ColorParams : register(b0) {
            float IsFullRange;
            float IsBt709;
            float2 Pad;
        };

        float3 YuvToRgb(float y, float u, float v) {
            if (IsFullRange < 0.5) {
                y = (y - 16.0 / 255.0) * (255.0 / 219.0);
            }
            u = u - 0.5;
            v = v - 0.5;
            if (IsFullRange < 0.5) {
                u *= (255.0 / 224.0);
                v *= (255.0 / 224.0);
            }

            float3 rgb;
            if (IsBt709 > 0.5) {
                rgb.r = y + 1.5748 * v;
                rgb.g = y - 0.1873 * u - 0.4681 * v;
                rgb.b = y + 1.8556 * u;
            } else {
                rgb.r = y + 1.4020 * v;
                rgb.g = y - 0.3441 * u - 0.7141 * v;
                rgb.b = y + 1.7720 * u;
            }
            return saturate(rgb);
        }

        float4 PSNv12(VSOut input) : SV_Target {
            float y = TexY.Sample(LinearSampler, input.uv);
            float2 uv = TexUV.Sample(LinearSampler, input.uv);
            return float4(YuvToRgb(y, uv.x, uv.y), 1.0);
        }

        float4 PSI420(VSOut input) : SV_Target {
            float y = TexY.Sample(LinearSampler, input.uv);
            float u = TexU.Sample(LinearSampler, input.uv);
            float v = TexV.Sample(LinearSampler, input.uv);
            return float4(YuvToRgb(y, u, v), 1.0);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct ColorParamsBuffer
    {
        public float IsFullRange;
        public float IsBt709;
        public float Pad0;
        public float Pad1;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly bool _allowTearing;

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _psNv12;
    private readonly ID3D11PixelShader _psI420;
    private readonly ID3D11SamplerState _linearSampler;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11Buffer _colorParamsCb;

    private ID3D11RenderTargetView? _backBufferRtv;
    private int _windowWidth;
    private int _windowHeight;

    // Tài nguyên GPU cho frame NV12 (dùng chung cho D3D11VA zero-copy và SW NV12)
    private ID3D11Texture2D? _nv12GpuTex;
    private ID3D11ShaderResourceView? _nv12SrvY;
    private ID3D11ShaderResourceView? _nv12SrvUv;

    // Tài nguyên upload CPU -> GPU khi fallback Software
    private ID3D11Texture2D? _cpuTexY;
    private ID3D11Texture2D? _cpuTexU;
    private ID3D11Texture2D? _cpuTexV;
    private ID3D11Texture2D? _cpuTexUv;
    private ID3D11ShaderResourceView? _cpuSrvY;
    private ID3D11ShaderResourceView? _cpuSrvU;
    private ID3D11ShaderResourceView? _cpuSrvV;
    private ID3D11ShaderResourceView? _cpuSrvUv;

    private int _videoWidth;
    private int _videoHeight;
    private AVPixelFormat _lastUploadedFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _hasFrame;
    private bool _lastIsFullRange;
    private bool _lastIsBt709 = true;
    private bool _disposed;

    public IntPtr DeviceNativePointer => _device.NativePointer;
    public IntPtr ContextNativePointer => _context.NativePointer;
    public ID3D11Device Device => _device;
    public ID3D11DeviceContext Context => _context;
    public bool AllowTearingSupported => _allowTearing;
    public int VideoWidth => _videoWidth;
    public int VideoHeight => _videoHeight;

    public D3D11VideoRenderer(IntPtr hwnd, int width, int height)
    {
        _windowWidth = Math.Max(1, width);
        _windowHeight = Math.Max(1, height);

        FeatureLevel[] featureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1];
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

        try
        {
            _device = D3D11.D3D11CreateDevice(DriverType.Hardware, flags, featureLevels);
        }
        catch
        {
            // Fallback không yêu cầu VideoSupport nếu driver không hỗ trợ
            _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels);
        }

        _context = _device.ImmediateContext;

        // Bật bảo vệ đa luồng cho ID3D11Device vì FFmpeg D3D11VA và Render Thread dùng chung device
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            mt?.SetMultithreadProtected(true);
        }

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice1>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        using (var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
        {
            _allowTearing = factory5 is not null && factory5.PresentAllowTearing;
        }

        var scDesc = new SwapChainDescription1
        {
            Width = (uint)_windowWidth,
            Height = (uint)_windowHeight,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = _allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, scDesc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        CreateBackBufferRtv();

        ReadOnlyMemory<byte> vsBytecode = Compiler.Compile(ShaderSource, "VSMain", "VideoShader.hlsl", "vs_5_0");
        ReadOnlyMemory<byte> psNv12Bytecode = Compiler.Compile(ShaderSource, "PSNv12", "VideoShader.hlsl", "ps_5_0");
        ReadOnlyMemory<byte> psI420Bytecode = Compiler.Compile(ShaderSource, "PSI420", "VideoShader.hlsl", "ps_5_0");

        _vertexShader = _device.CreateVertexShader(vsBytecode.Span);
        _psNv12 = _device.CreatePixelShader(psNv12Bytecode.Span);
        _psI420 = _device.CreatePixelShader(psI420Bytecode.Span);

        _linearSampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _rasterizerState = _device.CreateRasterizerState(RasterizerDescription.CullNone);
        _colorParamsCb = _device.CreateBuffer(
            (uint)Unsafe.SizeOf<ColorParamsBuffer>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);

        Logger.Ok("D3D11", $"Khởi tạo Direct3D11 thành công (AllowTearing={_allowTearing}, FlipDiscard Immediate)");
    }

    public void ResetVideoSurface()
    {
        _hasFrame = false;
        _videoWidth = 0;
        _videoHeight = 0;
        _lastUploadedFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _windowWidth && height == _windowHeight)
            return;

        _windowWidth = width;
        _windowHeight = height;

        _context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), null);
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;

        SwapChainFlags flags = _allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None;
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, flags).CheckError();
        CreateBackBufferRtv();
    }

    public bool RenderFrame(
        DecodedVideoFrame? newFrame,
        bool preserveAspectRatio,
        LatencyStats stats,
        ImGuiD3D11Backend? imguiBackend = null,
        ImDrawDataPtr? imguiDrawData = null,
        float reservedRightPx = 0f)
    {
        long renderStart = Stopwatch.GetTimestamp();
        bool renderedNewFrame = false;

        if (newFrame is not null)
        {
            UploadOrCopyFrame(newFrame);
            _hasFrame = true;
            renderedNewFrame = true;
        }

        if (_backBufferRtv is null)
            return renderedNewFrame;

        _context.OMSetRenderTargets(_backBufferRtv);
        _context.ClearRenderTargetView(_backBufferRtv, new Color4(0.071f, 0.082f, 0.110f, 1f));

        if (_hasFrame && _videoWidth > 0 && _videoHeight > 0)
        {
            var viewport = ComputeViewport(preserveAspectRatio, reservedRightPx);
            _context.RSSetViewport(viewport);
            _context.RSSetState(_rasterizerState);

            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _context.IASetInputLayout(null);
            _context.VSSetShader(_vertexShader);
            _context.PSSetSampler(0, _linearSampler);

            UpdateColorParams();
            _context.PSSetConstantBuffer(0, _colorParamsCb);

            if (_lastUploadedFormat == AVPixelFormat.AV_PIX_FMT_D3D11 && _nv12SrvY is not null && _nv12SrvUv is not null)
            {
                _context.PSSetShader(_psNv12);
                _context.PSSetShaderResources(0, [_nv12SrvY, _nv12SrvUv]);
                _context.Draw(4, 0);
            }
            else if (_lastUploadedFormat == AVPixelFormat.AV_PIX_FMT_NV12 && _cpuSrvY is not null && _cpuSrvUv is not null)
            {
                _context.PSSetShader(_psNv12);
                _context.PSSetShaderResources(0, [_cpuSrvY, _cpuSrvUv]);
                _context.Draw(4, 0);
            }
            else if (_lastUploadedFormat == AVPixelFormat.AV_PIX_FMT_YUV420P && _cpuSrvY is not null && _cpuSrvU is not null && _cpuSrvV is not null)
            {
                _context.PSSetShader(_psI420);
                _context.PSSetShaderResources(0, [_cpuSrvY, _cpuSrvU, _cpuSrvV]);
                _context.Draw(4, 0);
            }
        }

        if (imguiBackend is not null && imguiDrawData is { } drawData)
        {
            imguiBackend.RenderDrawData(drawData);
        }

        // Present Immediate (SyncInterval = 0 + AllowTearing) loại bỏ hoàn toàn chờ VSync
        PresentFlags presentFlags = _allowTearing ? PresentFlags.AllowTearing : PresentFlags.None;
        _swapChain.Present(0, presentFlags);

        if (renderedNewFrame && newFrame is not null)
        {
            long now = Stopwatch.GetTimestamp();
            double renderMs = Stopwatch.GetElapsedTime(renderStart, now).TotalMilliseconds;
            stats.RecordRenderedFrameAt(newFrame.PacketRecvTimestamp, renderMs, now);
        }

        return renderedNewFrame;
    }

    private void UploadOrCopyFrame(DecodedVideoFrame frameWrapper)
    {
        AVFrame* frame = frameWrapper.Pointer;
        if (frame == null) return;

        int w = frame->width;
        int h = frame->height;
        var fmt = (AVPixelFormat)frame->format;

        _lastIsFullRange = frame->color_range == AVColorRange.AVCOL_RANGE_JPEG;
        _lastIsBt709 = frame->colorspace != AVColorSpace.AVCOL_SPC_BT470BG &&
                       frame->colorspace != AVColorSpace.AVCOL_SPC_SMPTE170M;

        if (fmt == AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            EnsureNv12GpuResources(w, h);
            IntPtr rawSrcPtr = (IntPtr)frame->data[0];
            uint arraySlice = (uint)(nint)frame->data[1];

            // AddRef trước khi bọc vào ID3D11Texture2D để Dispose chỉ trả lại ref mà ta vừa tăng
            Marshal.AddRef(rawSrcPtr);
            using var srcTex = new ID3D11Texture2D(rawSrcPtr);
            _context.CopySubresourceRegion(
                _nv12GpuTex!,
                0,
                0,
                0,
                0,
                srcTex,
                arraySlice,
                new Box(0, 0, 0, w, h, 1));

            _lastUploadedFormat = AVPixelFormat.AV_PIX_FMT_D3D11;
        }
        else if (fmt == AVPixelFormat.AV_PIX_FMT_NV12)
        {
            EnsureCpuNv12Resources(w, h);
            UploadPlane(_cpuTexY!, frame->data[0], frame->linesize[0], w, h);
            UploadPlane(_cpuTexUv!, frame->data[1], frame->linesize[1], w, h / 2);
            _lastUploadedFormat = AVPixelFormat.AV_PIX_FMT_NV12;
        }
        else if (fmt is AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P)
        {
            EnsureCpuI420Resources(w, h);
            UploadPlane(_cpuTexY!, frame->data[0], frame->linesize[0], w, h);
            UploadPlane(_cpuTexU!, frame->data[1], frame->linesize[1], w / 2, h / 2);
            UploadPlane(_cpuTexV!, frame->data[2], frame->linesize[2], w / 2, h / 2);
            _lastUploadedFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
        }
        else
        {
            throw new NotSupportedException($"Pixel format chưa được renderer hỗ trợ: {fmt}");
        }

        _videoWidth = w;
        _videoHeight = h;
    }

    private void UploadPlane(ID3D11Texture2D texture, byte* srcData, int srcPitch, int rowBytes, int height)
    {
        MappedSubresource mapped = _context.Map(texture, 0, MapMode.WriteDiscard);
        try
        {
            byte* dst = (byte*)mapped.DataPointer;
            int dstPitch = (int)mapped.RowPitch;
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(srcData + (y * srcPitch), dst + (y * dstPitch), dstPitch, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(texture, 0);
        }
    }

    private void UpdateColorParams()
    {
        MappedSubresource mapped = _context.Map(_colorParamsCb, 0, MapMode.WriteDiscard);
        try
        {
            var cb = (ColorParamsBuffer*)mapped.DataPointer;
            cb->IsFullRange = _lastIsFullRange ? 1f : 0f;
            cb->IsBt709 = _lastIsBt709 ? 1f : 0f;
            cb->Pad0 = 0f;
            cb->Pad1 = 0f;
        }
        finally
        {
            _context.Unmap(_colorParamsCb, 0);
        }
    }

    private Viewport ComputeViewport(bool preserveAspectRatio, float reservedRightPx)
    {
        VideoViewport vp = ViewportMapper.ComputeViewport(
            _windowWidth,
            _windowHeight,
            _videoWidth,
            _videoHeight,
            preserveAspectRatio,
            reservedRightPx);
        return new Viewport(vp.X, vp.Y, vp.Width, vp.Height);
    }

    private void CreateBackBufferRtv()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferRtv = _device.CreateRenderTargetView(backBuffer);
    }

    private void EnsureNv12GpuResources(int width, int height)
    {
        int alignedW = (width + 1) & ~1;
        int alignedH = (height + 1) & ~1;
        if (_nv12GpuTex is not null && _videoWidth == width && _videoHeight == height)
            return;

        DisposeNv12GpuResources();

        var desc = new Texture2DDescription
        {
            Width = (uint)alignedW,
            Height = (uint)alignedH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        };

        _nv12GpuTex = _device.CreateTexture2D(desc);
        _nv12SrvY = _device.CreateShaderResourceView(_nv12GpuTex, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
        });
        _nv12SrvUv = _device.CreateShaderResourceView(_nv12GpuTex, new ShaderResourceViewDescription
        {
            Format = Format.R8G8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
        });
    }

    private void EnsureCpuNv12Resources(int width, int height)
    {
        if (_cpuTexY is not null && _cpuTexUv is not null && _videoWidth == width && _videoHeight == height)
            return;

        DisposeCpuResources();
        (_cpuTexY, _cpuSrvY) = CreateDynamicTextureAndSrv(width, height, Format.R8_UNorm);
        (_cpuTexUv, _cpuSrvUv) = CreateDynamicTextureAndSrv((width + 1) / 2, (height + 1) / 2, Format.R8G8_UNorm);
    }

    private void EnsureCpuI420Resources(int width, int height)
    {
        if (_cpuTexY is not null && _cpuTexU is not null && _cpuTexV is not null && _videoWidth == width && _videoHeight == height)
            return;

        DisposeCpuResources();
        (_cpuTexY, _cpuSrvY) = CreateDynamicTextureAndSrv(width, height, Format.R8_UNorm);
        (_cpuTexU, _cpuSrvU) = CreateDynamicTextureAndSrv((width + 1) / 2, (height + 1) / 2, Format.R8_UNorm);
        (_cpuTexV, _cpuSrvV) = CreateDynamicTextureAndSrv((width + 1) / 2, (height + 1) / 2, Format.R8_UNorm);
    }

    private (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv) CreateDynamicTextureAndSrv(int width, int height, Format format)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write
        };

        var tex = _device.CreateTexture2D(desc);
        var srv = _device.CreateShaderResourceView(tex);
        return (tex, srv);
    }

    private void DisposeNv12GpuResources()
    {
        _nv12SrvUv?.Dispose(); _nv12SrvUv = null;
        _nv12SrvY?.Dispose(); _nv12SrvY = null;
        _nv12GpuTex?.Dispose(); _nv12GpuTex = null;
    }

    private void DisposeCpuResources()
    {
        _cpuSrvUv?.Dispose(); _cpuSrvUv = null;
        _cpuSrvV?.Dispose(); _cpuSrvV = null;
        _cpuSrvU?.Dispose(); _cpuSrvU = null;
        _cpuSrvY?.Dispose(); _cpuSrvY = null;
        _cpuTexUv?.Dispose(); _cpuTexUv = null;
        _cpuTexV?.Dispose(); _cpuTexV = null;
        _cpuTexU?.Dispose(); _cpuTexU = null;
        _cpuTexY?.Dispose(); _cpuTexY = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeNv12GpuResources();
        DisposeCpuResources();
        _backBufferRtv?.Dispose();
        _colorParamsCb.Dispose();
        _rasterizerState.Dispose();
        _linearSampler.Dispose();
        _psI420.Dispose();
        _psNv12.Dispose();
        _vertexShader.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
