using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PhonePlayLink.Core;
using PhonePlayLink.Scrcpy;

namespace PhonePlayLink.Media;

/// <summary>
/// Bộ giải mã video FFmpeg độ trễ siêu thấp:
/// - Bật AV_CODEC_FLAG_LOW_DELAY và AV_CODEC_FLAG2_FAST
/// - Không dùng frame-threading (chỉ slice-threading) để tránh trễ pipeline
/// - Ưu tiên D3D11VA Zero-Copy chia sẻ ID3D11Device với Renderer, fallback sang DXVA2 hoặc CPU Software Decode
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private readonly IntPtr _d3d11DevicePtr;
    private readonly IntPtr _d3d11ContextPtr;
    private readonly bool _preferHardware;
    private readonly LatencyStats _stats;

    private AVCodecID _codecId;
    private AVCodecContext* _codecCtx;
    private AVBufferRef* _hwDeviceCtx;
    private AVPacket* _packet;
    private AVCodecContext_get_format _getFormatCallback;
    private AVPixelFormat _expectedHwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    private string _backendName = "Software";
    private bool _hardwareDisabledDueToError;
    private bool _disposed;

    public string BackendName => _backendName;

    public VideoDecoder(
        uint scrcpyCodecId,
        IntPtr d3d11DevicePtr,
        IntPtr d3d11ContextPtr,
        bool preferHardware,
        LatencyStats stats)
    {
        _codecId = MapCodecId(scrcpyCodecId);
        _d3d11DevicePtr = d3d11DevicePtr;
        _d3d11ContextPtr = d3d11ContextPtr;
        _preferHardware = preferHardware;
        _stats = stats;

        _getFormatCallback = SelectPixelFormat;
        _packet = ffmpeg.av_packet_alloc();
        if (_packet == null)
            throw new OutOfMemoryException("Không thể cấp phát AVPacket.");

        OpenDecoder(useHardware: _preferHardware);
    }

    private static AVCodecID MapCodecId(uint scrcpyCodecId) => scrcpyCodecId switch
    {
        ScrcpyConstants.CodecIdH264 => AVCodecID.AV_CODEC_ID_H264,
        ScrcpyConstants.CodecIdH265 => AVCodecID.AV_CODEC_ID_HEVC,
        ScrcpyConstants.CodecIdAv1  => AVCodecID.AV_CODEC_ID_AV1,
        ScrcpyConstants.CodecIdVp8  => AVCodecID.AV_CODEC_ID_VP8,
        ScrcpyConstants.CodecIdVp9  => AVCodecID.AV_CODEC_ID_VP9,
        _ => throw new NotSupportedException($"Video codec ID không được hỗ trợ: 0x{scrcpyCodecId:X8}")
    };

    private void OpenDecoder(bool useHardware)
    {
        CloseDecoderContextOnly();

        AVCodec* codec = ffmpeg.avcodec_find_decoder(_codecId);
        if (codec == null)
            throw new NotSupportedException($"FFmpeg không tìm thấy decoder cho {_codecId}.");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
            throw new OutOfMemoryException("Không thể cấp phát AVCodecContext.");

        // Cờ bắt buộc cho độ trễ siêu thấp
        _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

        // Tuyệt đối không dùng FF_THREAD_FRAME vì giữ nhiều frame trong buffer làm tăng độ trễ
        _codecCtx->thread_type = ffmpeg.FF_THREAD_SLICE;

        _expectedHwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        _backendName = "Software";

        if (useHardware && !_hardwareDisabledDueToError)
        {
            if (TryAttachD3D11Va(codec) || TryAttachDxva2(codec))
            {
                _codecCtx->get_format = _getFormatCallback;
                _codecCtx->thread_count = 1;
            }
            else
            {
                _codecCtx->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 4);
            }
        }
        else
        {
            _codecCtx->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 4);
        }

        int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0)
        {
            if (useHardware && !_hardwareDisabledDueToError)
            {
                Logger.Warn("Decoder", $"Mở HW decoder thất bại ({FfmpegInitializer.GetErrorMessage(ret)}), chuyển sang Software.");
                _hardwareDisabledDueToError = true;
                OpenDecoder(useHardware: false);
                return;
            }

            throw new InvalidOperationException($"avcodec_open2 thất bại: {FfmpegInitializer.GetErrorMessage(ret)}");
        }

        _stats.DecoderBackend = _backendName;
        Logger.Ok("Decoder", $"Video decoder sẵn sàng: {_codecId} | Backend={_backendName} | LOW_DELAY + FAST");
    }

    private bool TryAttachD3D11Va(AVCodec* codec)
    {
        if (_d3d11DevicePtr == IntPtr.Zero || _d3d11ContextPtr == IntPtr.Zero)
            return false;

        if (!SupportsHwConfig(codec, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVPixelFormat.AV_PIX_FMT_D3D11))
            return false;

        AVBufferRef* devRef = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (devRef == null)
            return false;

        try
        {
            var hwCtx = (AVHWDeviceContext*)devRef->data;
            var d3d11Ctx = (AVD3D11VADeviceContext*)hwCtx->hwctx;

            // Chia sẻ chính ID3D11Device của Renderer để Zero-Copy VRAM -> VRAM
            Marshal.AddRef(_d3d11DevicePtr);
            Marshal.AddRef(_d3d11ContextPtr);
            d3d11Ctx->device = (ID3D11Device*)_d3d11DevicePtr;
            d3d11Ctx->device_context = (ID3D11DeviceContext*)_d3d11ContextPtr;

            int ret = ffmpeg.av_hwdevice_ctx_init(devRef);
            if (ret < 0)
            {
                Logger.Warn("Decoder", $"av_hwdevice_ctx_init(D3D11VA) thất bại: {FfmpegInitializer.GetErrorMessage(ret)}");
                ffmpeg.av_buffer_unref(&devRef);
                return false;
            }

            _hwDeviceCtx = devRef;
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _expectedHwPixFmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            _backendName = "D3D11VA";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Decoder", $"Không thể gắn D3D11VA shared device: {ex.Message}");
            if (devRef != null)
                ffmpeg.av_buffer_unref(&devRef);
            return false;
        }
    }

    private bool TryAttachDxva2(AVCodec* codec)
    {
        if (!SupportsHwConfig(codec, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2, AVPixelFormat.AV_PIX_FMT_DXVA2_VLD))
            return false;

        AVBufferRef* devRef = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(&devRef, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2, null, null, 0);
        if (ret < 0 || devRef == null)
            return false;

        _hwDeviceCtx = devRef;
        _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
        _expectedHwPixFmt = AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;
        _backendName = "DXVA2";
        return true;
    }

    private static bool SupportsHwConfig(AVCodec* codec, AVHWDeviceType deviceType, AVPixelFormat pixFmt)
    {
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* cfg = ffmpeg.avcodec_get_hw_config(codec, i);
            if (cfg == null)
                return false;

            if ((cfg->methods & 0x01) != 0 && // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
                cfg->device_type == deviceType &&
                cfg->pix_fmt == pixFmt)
            {
                return true;
            }
        }
    }

    private AVPixelFormat SelectPixelFormat(AVCodecContext* ctx, AVPixelFormat* pixFmts)
    {
        for (AVPixelFormat* p = pixFmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == _expectedHwPixFmt)
                return *p;
        }

        // Fallback về định dạng software đầu tiên nếu HW không khả dụng cho stream này
        _backendName = "Software";
        _stats.DecoderBackend = _backendName;
        return *pixFmts;
    }

    public void ResetForSessionChange()
    {
        if (_codecCtx != null)
        {
            ffmpeg.avcodec_flush_buffers(_codecCtx);
        }
    }

    public void DecodePacket(
        ReadOnlySpan<byte> payload,
        long pts,
        bool isKeyFrame,
        long packetRecvTimestamp,
        LatestFrameSlot frameSlot)
    {
        if (_disposed || payload.IsEmpty)
            return;

        long decodeStart = Stopwatch.GetTimestamp();

        fixed (byte* ptr = payload)
        {
            ffmpeg.av_packet_unref(_packet);
            _packet->data = ptr;
            _packet->size = payload.Length;
            _packet->pts = pts == long.MinValue ? ffmpeg.AV_NOPTS_VALUE : pts;
            _packet->dts = _packet->pts;
            if (isKeyFrame)
                _packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;

            int sendRet = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
            _packet->data = null;
            _packet->size = 0;

            if (sendRet < 0)
            {
                if (sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    DrainFrames(decodeStart, packetRecvTimestamp, frameSlot);
                    return;
                }

                // Nếu HW decoder từ chối stream -> fallback mượt sang Software
                if (!_hardwareDisabledDueToError && _expectedHwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    Logger.Warn("Decoder", $"HW decode lỗi ({FfmpegInitializer.GetErrorMessage(sendRet)}), tự động chuyển sang Software.");
                    _hardwareDisabledDueToError = true;
                    OpenDecoder(useHardware: false);
                    // Thử decode lại gói hiện tại bằng Software
                    fixed (byte* ptr2 = payload)
                    {
                        _packet->data = ptr2;
                        _packet->size = payload.Length;
                        _packet->pts = pts == long.MinValue ? ffmpeg.AV_NOPTS_VALUE : pts;
                        _packet->dts = _packet->pts;
                        sendRet = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
                        _packet->data = null;
                        _packet->size = 0;
                    }
                }

                if (sendRet < 0)
                    return;
            }
        }

        DrainFrames(decodeStart, packetRecvTimestamp, frameSlot);
    }

    private void DrainFrames(long decodeStart, long packetRecvTimestamp, LatestFrameSlot frameSlot)
    {
        while (true)
        {
            AVFrame* rawFrame = ffmpeg.av_frame_alloc();
            if (rawFrame == null)
                return;

            int ret = ffmpeg.avcodec_receive_frame(_codecCtx, rawFrame);
            if (ret < 0)
            {
                ffmpeg.av_frame_free(&rawFrame);
                break;
            }

            AVFrame* outputFrame = rawFrame;
            if ((AVPixelFormat)rawFrame->format == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD)
            {
                // DXVA2 cần transfer về RAM NV12 để đưa lên D3D11 SRV
                AVFrame* swFrame = ffmpeg.av_frame_alloc();
                if (swFrame != null)
                {
                    swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                    int xfer = ffmpeg.av_hwframe_transfer_data(swFrame, rawFrame, 0);
                    ffmpeg.av_frame_free(&rawFrame);
                    if (xfer < 0)
                    {
                        ffmpeg.av_frame_free(&swFrame);
                        continue;
                    }
                    outputFrame = swFrame;
                }
            }

            double decodeMs = Stopwatch.GetElapsedTime(decodeStart).TotalMilliseconds;
            _stats.RecordDecode(decodeMs);
            frameSlot.Publish(new DecodedVideoFrame(outputFrame, packetRecvTimestamp), _stats);
        }
    }

    private void CloseDecoderContextOnly()
    {
        if (_codecCtx != null)
        {
            AVCodecContext* ctx = _codecCtx;
            ffmpeg.avcodec_free_context(&ctx);
            _codecCtx = null;
        }

        if (_hwDeviceCtx != null)
        {
            AVBufferRef* hw = _hwDeviceCtx;
            ffmpeg.av_buffer_unref(&hw);
            _hwDeviceCtx = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CloseDecoderContextOnly();
        if (_packet != null)
        {
            AVPacket* p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }
    }
}
