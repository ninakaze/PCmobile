using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PhonePlayLink.Core;
using PhonePlayLink.Scrcpy;

namespace PhonePlayLink.Media;

/// <summary>
/// Giải mã stream âm thanh từ scrcpy (mặc định Opus 48kHz Stereo) về PCM S16LE 48kHz Stereo độ trễ thấp.
/// </summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    public const int OutputSampleRate = 48_000;
    public const int OutputChannels = 2;

    private readonly AVCodecID _codecId;
    private AVCodecContext* _codecCtx;
    private SwrContext* _swrCtx;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private byte[] _pcmBuffer = new byte[48_000 * 2 * 2]; // tối đa 1s dự phòng
    private bool _opened;
    private bool _disposed;

    public AudioDecoder(uint scrcpyCodecId)
    {
        _codecId = MapCodecId(scrcpyCodecId);
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_packet == null || _frame == null)
            throw new OutOfMemoryException("Không thể cấp phát AVPacket/AVFrame cho AudioDecoder.");

        // Với RAW PCM không có config packet
        if (_codecId == AVCodecID.AV_CODEC_ID_PCM_S16LE)
        {
            EnsureOpened(ReadOnlySpan<byte>.Empty);
        }
    }

    private static AVCodecID MapCodecId(uint scrcpyCodecId) => scrcpyCodecId switch
    {
        ScrcpyConstants.CodecIdOpus => AVCodecID.AV_CODEC_ID_OPUS,
        ScrcpyConstants.CodecIdAac  => AVCodecID.AV_CODEC_ID_AAC,
        ScrcpyConstants.CodecIdFlac => AVCodecID.AV_CODEC_ID_FLAC,
        ScrcpyConstants.CodecIdRaw  => AVCodecID.AV_CODEC_ID_PCM_S16LE,
        _ => throw new NotSupportedException($"Audio codec ID không được hỗ trợ: 0x{scrcpyCodecId:X8}")
    };

    private void EnsureOpened(ReadOnlySpan<byte> extradata)
    {
        if (_opened)
            return;

        AVCodec* codec = ffmpeg.avcodec_find_decoder(_codecId);
        if (codec == null)
            throw new NotSupportedException($"FFmpeg không tìm thấy audio decoder cho {_codecId}.");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
            throw new OutOfMemoryException("Không thể cấp phát AVCodecContext cho audio.");

        _codecCtx->sample_rate = OutputSampleRate;
        ffmpeg.av_channel_layout_default(&_codecCtx->ch_layout, OutputChannels);
        if (_codecId == AVCodecID.AV_CODEC_ID_FLAC || _codecId == AVCodecID.AV_CODEC_ID_PCM_S16LE)
            _codecCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;

        if (!extradata.IsEmpty)
        {
            int allocSize = extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE;
            _codecCtx->extradata = (byte*)ffmpeg.av_mallocz((ulong)allocSize);
            _codecCtx->extradata_size = extradata.Length;
            extradata.CopyTo(new Span<byte>(_codecCtx->extradata, extradata.Length));
        }

        int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0)
            throw new InvalidOperationException($"Không thể mở audio decoder {_codecId}: {FfmpegInitializer.GetErrorMessage(ret)}");

        _opened = true;
        Logger.Ok("AudioDec", $"Đã mở audio decoder {_codecId} ({OutputSampleRate}Hz, {OutputChannels}ch)");
    }

    public int DecodePacket(ReadOnlySpan<byte> payload, bool isConfig, out ReadOnlySpan<byte> pcmS16Le)
    {
        pcmS16Le = ReadOnlySpan<byte>.Empty;
        if (_disposed || payload.IsEmpty)
            return 0;

        if (isConfig)
        {
            if (!_opened)
            {
                EnsureOpened(payload);
            }
            return 0;
        }

        if (!_opened)
        {
            EnsureOpened(ReadOnlySpan<byte>.Empty);
        }

        if (_codecId == AVCodecID.AV_CODEC_ID_PCM_S16LE)
        {
            pcmS16Le = payload;
            return payload.Length;
        }

        fixed (byte* ptr = payload)
        {
            ffmpeg.av_packet_unref(_packet);
            _packet->data = ptr;
            _packet->size = payload.Length;
            int sendRet = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
            _packet->data = null;
            _packet->size = 0;
            if (sendRet < 0)
                return 0;
        }

        int totalBytesWritten = 0;
        while (true)
        {
            ffmpeg.av_frame_unref(_frame);
            int recvRet = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (recvRet < 0)
                break;

            int samplesOut = ConvertFrameToS16(_frame, totalBytesWritten);
            if (samplesOut > 0)
                totalBytesWritten += samplesOut * OutputChannels * 2;
        }

        pcmS16Le = _pcmBuffer.AsSpan(0, totalBytesWritten);
        return totalBytesWritten;
    }

    private int ConvertFrameToS16(AVFrame* frame, int bufferOffset)
    {
        EnsureResampler(frame);

        int maxOutSamples = (int)ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(_swrCtx, frame->sample_rate) + frame->nb_samples,
            OutputSampleRate,
            frame->sample_rate,
            AVRounding.AV_ROUND_UP);

        int requiredBytes = bufferOffset + maxOutSamples * OutputChannels * 2;
        if (_pcmBuffer.Length < requiredBytes)
        {
            Array.Resize(ref _pcmBuffer, requiredBytes * 2);
        }

        fixed (byte* outPtr = &_pcmBuffer[bufferOffset])
        {
            byte* outData = outPtr;
            int converted = ffmpeg.swr_convert(
                _swrCtx,
                &outData,
                maxOutSamples,
                frame->extended_data,
                frame->nb_samples);
            return converted > 0 ? converted : 0;
        }
    }

    private void EnsureResampler(AVFrame* frame)
    {
        if (_swrCtx != null)
            return;

        AVChannelLayout outLayout = default;
        ffmpeg.av_channel_layout_default(&outLayout, OutputChannels);

        AVChannelLayout inLayout = frame->ch_layout;
        if (inLayout.nb_channels <= 0)
            ffmpeg.av_channel_layout_default(&inLayout, OutputChannels);

        SwrContext* swr = null;
        int ret = ffmpeg.swr_alloc_set_opts2(
            &swr,
            &outLayout,
            AVSampleFormat.AV_SAMPLE_FMT_S16,
            OutputSampleRate,
            &inLayout,
            (AVSampleFormat)frame->format,
            frame->sample_rate > 0 ? frame->sample_rate : OutputSampleRate,
            0,
            null);

        if (ret < 0 || swr == null)
            throw new InvalidOperationException($"swr_alloc_set_opts2 thất bại: {FfmpegInitializer.GetErrorMessage(ret)}");

        ret = ffmpeg.swr_init(swr);
        if (ret < 0)
        {
            ffmpeg.swr_free(&swr);
            throw new InvalidOperationException($"swr_init thất bại: {FfmpegInitializer.GetErrorMessage(ret)}");
        }

        _swrCtx = swr;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_swrCtx != null)
        {
            SwrContext* s = _swrCtx;
            ffmpeg.swr_free(&s);
            _swrCtx = null;
        }

        if (_codecCtx != null)
        {
            AVCodecContext* ctx = _codecCtx;
            ffmpeg.avcodec_free_context(&ctx);
            _codecCtx = null;
        }

        if (_frame != null)
        {
            AVFrame* f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_packet != null)
        {
            AVPacket* p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }
    }
}
