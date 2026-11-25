using System.Diagnostics;
using FFmpeg.AutoGen;
using ImGuiNET;
using PhonePlayLink.Core;
using PhonePlayLink.Input;
using PhonePlayLink.Keymap;
using PhonePlayLink.Media;
using PhonePlayLink.Rendering;
using PhonePlayLink.Scrcpy;
using PhonePlayLink.UI;

namespace PhonePlayLink.Tests;

[CollectionDefinition("SdlWindow")]
public class SdlWindowTestCollection { }

[Collection("SdlWindow")]
public unsafe class MediaPipelineTests
{
    [Fact]
    public void AssetsAndFfmpegNativeLibraries_LoadSuccessfully()
    {
        AssetPaths assets = AssetPaths.Resolve();
        assets.ValidateAll();
        FfmpegInitializer.Initialize(assets.FfmpegDirectory);

        uint ver = ffmpeg.avcodec_version();
        Assert.True(ver > 0);
    }

    [Fact]
    public void EndToEnd_VideoDecodeAndD3D11Render_WorksOnHiddenSdlWindow()
    {
        AssetPaths assets = AssetPaths.Resolve();
        assets.ValidateAll();
        FfmpegInitializer.Initialize(assets.FfmpegDirectory);

        byte[] encodedH264Packet = EncodeSingleSyntheticH264Frame(640, 360);
        Assert.NotEmpty(encodedH264Packet);

        var stats = new LatencyStats();
        using var window = new SdlWindowHost("PhonePlayLink-Test", 640, 360, startFullscreen: false, preserveAspectRatio: true, hidden: true);
        using var renderer = new D3D11VideoRenderer(window.Hwnd, window.Width, window.Height);
        using var frameSlot = new LatestFrameSlot();
        using var decoder = new VideoDecoder(
            ScrcpyConstants.CodecIdH264,
            renderer.DeviceNativePointer,
            renderer.ContextNativePointer,
            preferHardware: true,
            stats);

        decoder.DecodePacket(encodedH264Packet, 0, isKeyFrame: true, Stopwatch.GetTimestamp(), frameSlot);

        using DecodedVideoFrame? decoded = frameSlot.TakeLatest();
        Assert.NotNull(decoded);
        Assert.Equal(640, decoded!.Width);
        Assert.Equal(360, decoded.Height);

        bool rendered = renderer.RenderFrame(decoded, preserveAspectRatio: true, stats);
        Assert.True(rendered);
        Assert.Equal(1, stats.RenderedFrames);
    }

    [Fact]
    public void EndToEnd_ImGuiD3D11Overlay_RendersEditorAndPlayHudWithoutErrors()
    {
        var stats = new LatencyStats();
        using var window = new SdlWindowHost("PhonePlayLink-ImGuiTest", 1280, 720, startFullscreen: false, preserveAspectRatio: true, hidden: true);
        using var renderer = new D3D11VideoRenderer(window.Hwnd, window.Width, window.Height);
        using var imgui = new ImGuiD3D11Backend(renderer.Device, renderer.Context);

        string profilesDir = ProfileManager.ResolveDefaultDirectory();
        var profileMgr = new ProfileManager(profilesDir);
        var (path, profile) = profileMgr.LoadInitialOrCreateDefault();
        using var ptrMgr = new MultiTouchPointerManager(new NoOpSender());
        var engine = new KeymapEngine(ptrMgr, profile);
        var overlay = new KeymapEditorOverlay(engine, profileMgr, path);

        // 1. Render chế độ Play HUD (F3)
        imgui.BeginFrame(window.Width, window.Height, 1f / 60f);
        overlay.BuildUi(window.Width, window.Height, 1920, 1080, true, 1f / 60f);
        ImGui.Render();
        renderer.RenderFrame(null, true, stats, imgui, ImGui.GetDrawData());

        // 2. Render chế độ Visual Keymap Editor (F1)
        overlay.ToggleEditor();
        Assert.True(overlay.IsEditorOpen);
        imgui.BeginFrame(window.Width, window.Height, 1f / 60f);
        overlay.BuildUi(window.Width, window.Height, 1920, 1080, true, 1f / 60f);
        ImGui.Render();
        renderer.RenderFrame(null, true, stats, imgui, ImGui.GetDrawData());
    }

    private sealed class NoOpSender : IScrcpyTouchSender
    {
        public (int Width, int Height) CurrentScreenSize => (1920, 1080);
        public bool IsScreenOn => true;
        public void UpdateScreenSize(int width, int height) { }
        public void SendTouchEvent(AndroidMotionAction action, ulong pointerId, int x, int y, float pressure) { }
        public void SetDisplayPower(bool powerOn) { }
        public void SendAndroidKeyPress(int androidKeyCode) { }
    }

    [Fact]
    public void AudioDecoder_OpensWithOpusConfigPacket()
    {
        AssetPaths assets = AssetPaths.Resolve();
        assets.ValidateAll();
        FfmpegInitializer.Initialize(assets.FfmpegDirectory);

        // Standard RFC 7845 OpusHead (19 bytes, 48kHz, 2 channels)
        byte[] opusHead =
        [
            (byte)'O', (byte)'p', (byte)'u', (byte)'s', (byte)'H', (byte)'e', (byte)'a', (byte)'d',
            0x01,       // version
            0x02,       // channels = 2
            0x38, 0x01, // pre-skip = 312
            0x80, 0xBB, 0x00, 0x00, // input sample rate = 48000
            0x00, 0x00, // output gain = 0
            0x00        // mapping family = 0
        ];

        using var audioDec = new AudioDecoder(ScrcpyConstants.CodecIdOpus);
        int bytes = audioDec.DecodePacket(opusHead, isConfig: true, out _);
        Assert.Equal(0, bytes);

        // Opus DTX / silence packet (TOC=0xF8, 3 bytes)
        byte[] silencePacket = [0xF8, 0xFF, 0xFE];
        int decodedBytes = audioDec.DecodePacket(silencePacket, isConfig: false, out ReadOnlySpan<byte> pcm);
        Assert.True(decodedBytes > 0);
        Assert.Equal(decodedBytes, pcm.Length);
    }

    private static byte[] EncodeSingleSyntheticH264Frame(int width, int height)
    {
        AVCodec* encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
        Assert.True(encoder != null, "Không tìm thấy H264 encoder trong FFmpeg build.");

        AVCodecContext* encCtx = ffmpeg.avcodec_alloc_context3(encoder);
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVPacket* pkt = ffmpeg.av_packet_alloc();

        try
        {
            encCtx->width = width;
            encCtx->height = height;
            encCtx->time_base = new AVRational { num = 1, den = 60 };
            encCtx->framerate = new AVRational { num = 60, den = 1 };
            encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            encCtx->gop_size = 1;
            encCtx->max_b_frames = 0;

            ffmpeg.av_opt_set(encCtx->priv_data, "tune", "zerolatency", 0);
            ffmpeg.av_opt_set(encCtx->priv_data, "preset", "ultrafast", 0);

            int openRet = ffmpeg.avcodec_open2(encCtx, encoder, null);
            Assert.True(openRet >= 0, $"avcodec_open2 encoder lỗi: {FfmpegInitializer.GetErrorMessage(openRet)}");

            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->width = width;
            frame->height = height;
            Assert.True(ffmpeg.av_frame_get_buffer(frame, 32) >= 0);

            // Tô màu frame thử nghiệm
            new Span<byte>(frame->data[0], frame->linesize[0] * height).Fill(128);
            new Span<byte>(frame->data[1], frame->linesize[1] * (height / 2)).Fill(100);
            new Span<byte>(frame->data[2], frame->linesize[2] * (height / 2)).Fill(180);
            frame->pts = 0;

            Assert.True(ffmpeg.avcodec_send_frame(encCtx, frame) >= 0);
            Assert.True(ffmpeg.avcodec_send_frame(encCtx, null) >= 0); // flush

            using var output = new MemoryStream();
            while (ffmpeg.avcodec_receive_packet(encCtx, pkt) >= 0)
            {
                output.Write(new ReadOnlySpan<byte>(pkt->data, pkt->size));
                ffmpeg.av_packet_unref(pkt);
            }

            return output.ToArray();
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&encCtx);
        }
    }
}
