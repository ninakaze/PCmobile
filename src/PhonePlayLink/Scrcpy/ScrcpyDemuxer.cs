using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace PhonePlayLink.Scrcpy;

public readonly record struct ScrcpySessionMeta(int Width, int Height, bool ClientResized);

public enum ScrcpyPacketKind
{
    EndOfStream,
    SessionChanged,
    MediaPacket
}

public readonly struct ScrcpyVideoPacket : IDisposable
{
    private readonly byte[]? _rentedBuffer;

    public ScrcpyPacketKind Kind { get; init; }
    public ScrcpySessionMeta SessionMeta { get; init; }
    public long Pts { get; init; }
    public bool IsKeyFrame { get; init; }
    public int Length { get; init; }

    public ReadOnlySpan<byte> Payload => _rentedBuffer is null ? ReadOnlySpan<byte>.Empty : _rentedBuffer.AsSpan(0, Length);
    public byte[]? RawBuffer => _rentedBuffer;

    public ScrcpyVideoPacket(byte[]? rentedBuffer, int length, long pts, bool isKeyFrame)
    {
        _rentedBuffer = rentedBuffer;
        Length = length;
        Pts = pts;
        IsKeyFrame = isKeyFrame;
        Kind = ScrcpyPacketKind.MediaPacket;
        SessionMeta = default;
    }

    public static ScrcpyVideoPacket Session(ScrcpySessionMeta meta) => new()
    {
        Kind = ScrcpyPacketKind.SessionChanged,
        SessionMeta = meta
    };

    public static ScrcpyVideoPacket Eos() => new()
    {
        Kind = ScrcpyPacketKind.EndOfStream
    };

    public void Dispose()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
        }
    }
}

public readonly struct ScrcpyAudioPacket : IDisposable
{
    private readonly byte[]? _rentedBuffer;

    public bool IsEndOfStream { get; init; }
    public bool IsConfig { get; init; }
    public long Pts { get; init; }
    public int Length { get; init; }

    public ReadOnlySpan<byte> Payload => _rentedBuffer is null ? ReadOnlySpan<byte>.Empty : _rentedBuffer.AsSpan(0, Length);
    public byte[]? RawBuffer => _rentedBuffer;

    public ScrcpyAudioPacket(byte[]? rentedBuffer, int length, long pts, bool isConfig)
    {
        _rentedBuffer = rentedBuffer;
        Length = length;
        Pts = pts;
        IsConfig = isConfig;
        IsEndOfStream = false;
    }

    public static ScrcpyAudioPacket Eos() => new() { IsEndOfStream = true };

    public void Dispose()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
        }
    }
}

/// <summary>
/// Giải mã luồng nhị phân của scrcpy-server v5.0.1 (Device Meta, Codec ID, Session Header, Frame Header, H.264/H.265 Config Merger).
/// </summary>
public static class ScrcpyDemuxer
{
    public static async Task<string> ReadDeviceNameAsync(Stream stream, CancellationToken ct)
    {
        byte[] buf = new byte[ScrcpyConstants.DeviceNameLength];
        if (!await ReadExactlyAsync(stream, buf, ct))
            throw new EndOfStreamException("Không đọc được Device Name từ socket scrcpy.");

        int nullIdx = Array.IndexOf(buf, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : buf.Length;
        return Encoding.UTF8.GetString(buf, 0, len).Trim();
    }

    public static async Task<uint> ReadCodecIdAsync(Stream stream, CancellationToken ct)
    {
        byte[] buf = new byte[4];
        if (!await ReadExactlyAsync(stream, buf, ct))
            throw new EndOfStreamException("Không đọc được Codec ID từ socket scrcpy.");

        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    public static async Task<ScrcpySessionMeta> ReadInitialVideoSessionAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[ScrcpyConstants.PacketHeaderSize];
        if (!await ReadExactlyAsync(stream, header, ct))
            throw new EndOfStreamException("Không đọc được Session Header ban đầu của video.");

        if (!IsSessionHeader(header))
            throw new InvalidDataException($"Gói đầu tiên không phải Session Header (byte[0]=0x{header[0]:X2}).");

        return ParseSessionHeader(header);
    }

    public static bool IsSessionHeader(ReadOnlySpan<byte> header12) => (header12[0] & 0x80) != 0;

    public static ScrcpySessionMeta ParseSessionHeader(ReadOnlySpan<byte> header12)
    {
        bool clientResized = (header12[3] & 1) != 0;
        int width = BinaryPrimitives.ReadInt32BigEndian(header12[4..8]);
        int height = BinaryPrimitives.ReadInt32BigEndian(header12[8..12]);
        return new ScrcpySessionMeta(width, height, clientResized);
    }

    /// <summary>
    /// Đọc gói video tiếp theo; tự động gộp packet CONFIG (SPS/PPS/VPS) vào đầu packet media tiếp theo cho H.264/H.265.
    /// </summary>
    public sealed class VideoReader
    {
        private readonly Stream _stream;
        private readonly bool _mergeConfigPackets;
        private readonly byte[] _headerBuf = new byte[ScrcpyConstants.PacketHeaderSize];
        private byte[]? _pendingConfig;

        public VideoReader(Stream stream, uint codecId)
        {
            _stream = stream;
            _mergeConfigPackets = codecId is ScrcpyConstants.CodecIdH264 or ScrcpyConstants.CodecIdH265;
        }

        public async ValueTask<ScrcpyVideoPacket> ReadNextAsync(CancellationToken ct)
        {
            while (true)
            {
                if (!await ReadExactlyAsync(_stream, _headerBuf, ct))
                    return ScrcpyVideoPacket.Eos();

                if (IsSessionHeader(_headerBuf))
                {
                    var meta = ParseSessionHeader(_headerBuf);
                    return ScrcpyVideoPacket.Session(meta);
                }

                ulong ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(_headerBuf.AsSpan(0, 8));
                uint packetSize = BinaryPrimitives.ReadUInt32BigEndian(_headerBuf.AsSpan(8, 4));
                if (packetSize == 0 || packetSize > 32 * 1024 * 1024)
                    throw new InvalidDataException($"Kích thước packet video bất thường: {packetSize}");

                bool isConfig = (ptsAndFlags & ScrcpyConstants.PacketFlagConfig) != 0;
                bool isKeyFrame = (ptsAndFlags & ScrcpyConstants.PacketFlagKeyFrame) != 0;
                long pts = isConfig ? long.MinValue : (long)(ptsAndFlags & ScrcpyConstants.PacketPtsMask);

                if (isConfig && _mergeConfigPackets)
                {
                    byte[] cfg = new byte[packetSize];
                    if (!await ReadExactlyAsync(_stream, cfg, ct))
                        return ScrcpyVideoPacket.Eos();
                    _pendingConfig = cfg;
                    continue; // Chờ media packet tiếp theo để prepend SPS/PPS
                }

                int configLen = _pendingConfig?.Length ?? 0;
                int totalLen = configLen + (int)packetSize;
                // Cấp phát thêm 64 byte AV_INPUT_BUFFER_PADDING_SIZE ở đuôi cho an toàn với FFmpeg bitstream reader
                byte[] rented = ArrayPool<byte>.Shared.Rent(totalLen + 64);
                try
                {
                    if (_pendingConfig is not null)
                    {
                        Buffer.BlockCopy(_pendingConfig, 0, rented, 0, configLen);
                        _pendingConfig = null;
                    }

                    if (!await ReadExactlyAsync(_stream, rented.AsMemory(configLen, (int)packetSize), ct))
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        return ScrcpyVideoPacket.Eos();
                    }

                    // Zero padding cuối buffer theo yêu cầu FFmpeg AVPacket
                    Array.Clear(rented, totalLen, 64);
                    return new ScrcpyVideoPacket(rented, totalLen, pts, isKeyFrame);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    throw;
                }
            }
        }
    }

    public sealed class AudioReader
    {
        private readonly Stream _stream;
        private readonly byte[] _headerBuf = new byte[ScrcpyConstants.PacketHeaderSize];

        public AudioReader(Stream stream)
        {
            _stream = stream;
        }

        public async ValueTask<ScrcpyAudioPacket> ReadNextAsync(CancellationToken ct)
        {
            if (!await ReadExactlyAsync(_stream, _headerBuf, ct))
                return ScrcpyAudioPacket.Eos();

            ulong ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(_headerBuf.AsSpan(0, 8));
            uint packetSize = BinaryPrimitives.ReadUInt32BigEndian(_headerBuf.AsSpan(8, 4));
            if (packetSize == 0 || packetSize > 4 * 1024 * 1024)
                throw new InvalidDataException($"Kích thước packet audio bất thường: {packetSize}");

            bool isConfig = (ptsAndFlags & ScrcpyConstants.PacketFlagConfig) != 0;
            long pts = isConfig ? long.MinValue : (long)(ptsAndFlags & ScrcpyConstants.PacketPtsMask);

            byte[] rented = ArrayPool<byte>.Shared.Rent((int)packetSize + 64);
            try
            {
                if (!await ReadExactlyAsync(_stream, rented.AsMemory(0, (int)packetSize), ct))
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    return ScrcpyAudioPacket.Eos();
                }

                Array.Clear(rented, (int)packetSize, 64);
                return new ScrcpyAudioPacket(rented, (int)packetSize, pts, isConfig);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                throw;
            }
        }
    }

    public static async ValueTask<bool> ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int n = await stream.ReadAsync(destination[totalRead..], ct);
            if (n == 0)
            {
                return false;
            }
            totalRead += n;
        }
        return true;
    }
}
