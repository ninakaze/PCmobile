using PhonePlayLink.Core;
using Silk.NET.SDL;

namespace PhonePlayLink.Media;

/// <summary>
/// Phát âm thanh PCM S16LE 48kHz Stereo qua SDL2 Audio Device với cơ chế giới hạn queue chống trễ tích tụ.
/// </summary>
public sealed unsafe class AudioPlayer : IDisposable
{
    private const ushort AudioS16Lsb = 0x8010;
    private const uint MaxQueuedBytes = (AudioDecoder.OutputSampleRate * AudioDecoder.OutputChannels * 2 * 60) / 1000; // ~60ms

    private readonly Sdl _sdl;
    private readonly uint _deviceId;
    private volatile bool _isMuted;
    private bool _disposed;

    public bool IsReady => _deviceId != 0;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            if (value && _deviceId != 0 && !_disposed)
            {
                _sdl.ClearQueuedAudio(_deviceId);
            }
        }
    }

    public double CurrentQueueMs
    {
        get
        {
            if (_disposed || _deviceId == 0)
                return 0.0;
            uint bytes = _sdl.GetQueuedAudioSize(_deviceId);
            return bytes * 1000.0 / (AudioDecoder.OutputSampleRate * AudioDecoder.OutputChannels * 2);
        }
    }

    public AudioPlayer(Sdl sdl)
    {
        _sdl = sdl;
        var desired = new AudioSpec
        {
            Freq = AudioDecoder.OutputSampleRate,
            Format = AudioS16Lsb,
            Channels = AudioDecoder.OutputChannels,
            Samples = 480 // ~10ms buffer ở 48kHz
        };

        AudioSpec obtained;
        _deviceId = _sdl.OpenAudioDevice((byte*)null, 0, &desired, &obtained, 0);
        if (_deviceId == 0)
        {
            Logger.Warn("AudioOut", $"Không mở được thiết bị phát âm thanh SDL2: {_sdl.GetErrorS()}");
            return;
        }

        _sdl.PauseAudioDevice(_deviceId, 0);
        Logger.Ok("AudioOut", $"Đã mở SDL2 Audio ({obtained.Freq}Hz, {obtained.Channels}ch, buffer={obtained.Samples} samples)");
    }

    public void EnqueuePcm(ReadOnlySpan<byte> pcm)
    {
        if (_disposed || _deviceId == 0 || _isMuted || pcm.IsEmpty)
            return;

        // Xả hàng đợi nếu âm thanh bị trễ quá 60ms để giữ đồng bộ sát thời gian thực với hình ảnh
        uint queued = _sdl.GetQueuedAudioSize(_deviceId);
        if (queued > MaxQueuedBytes)
        {
            _sdl.ClearQueuedAudio(_deviceId);
        }

        fixed (byte* ptr = pcm)
        {
            _sdl.QueueAudio(_deviceId, ptr, (uint)pcm.Length);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_deviceId != 0)
        {
            _sdl.ClearQueuedAudio(_deviceId);
            _sdl.CloseAudioDevice(_deviceId);
        }
    }
}
