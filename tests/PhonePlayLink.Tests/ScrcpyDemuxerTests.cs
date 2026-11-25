using System.Buffers.Binary;
using System.Text;
using PhonePlayLink.Adb;
using PhonePlayLink.Scrcpy;

namespace PhonePlayLink.Tests;

public class ScrcpyDemuxerTests
{
    [Fact]
    public void ParseDevicesOutput_ParsesUsbTcpAndUnauthorized()
    {
        const string sample = """
            List of devices attached
            R5CX12345AB            device product:e3qxxx model:SM_S928B device:e3q transport_id:1
            192.168.1.50:5555      device product:husky model:Pixel_8_Pro device:husky transport_id:2
            ABCD9999               unauthorized usb:1-2 transport_id:3

            """;

        var devices = AdbClient.ParseDevicesOutput(sample);

        Assert.Equal(3, devices.Count);

        Assert.Equal("R5CX12345AB", devices[0].Serial);
        Assert.True(devices[0].IsOnline);
        Assert.True(devices[0].IsUsb);
        Assert.Equal("SM_S928B", devices[0].Model);

        Assert.Equal("192.168.1.50:5555", devices[1].Serial);
        Assert.True(devices[1].IsOnline);
        Assert.False(devices[1].IsUsb);

        Assert.Equal("ABCD9999", devices[2].Serial);
        Assert.False(devices[2].IsOnline);
    }

    [Fact]
    public async Task ReadDeviceNameAndInitialSession_ParsesScrcpyV5Header()
    {
        using var ms = new MemoryStream();

        byte[] nameBuf = new byte[ScrcpyConstants.DeviceNameLength];
        Encoding.UTF8.GetBytes("Pixel 8 Pro").CopyTo(nameBuf, 0);
        ms.Write(nameBuf);

        byte[] codecBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(codecBuf, ScrcpyConstants.CodecIdH264);
        ms.Write(codecBuf);

        byte[] sessionHdr = new byte[12];
        sessionHdr[0] = 0x80; // MSB = 1 => session packet
        sessionHdr[3] = 0x01; // client resized
        BinaryPrimitives.WriteInt32BigEndian(sessionHdr.AsSpan(4, 4), 1080);
        BinaryPrimitives.WriteInt32BigEndian(sessionHdr.AsSpan(8, 4), 2400);
        ms.Write(sessionHdr);

        ms.Position = 0;

        string name = await ScrcpyDemuxer.ReadDeviceNameAsync(ms, CancellationToken.None);
        uint codec = await ScrcpyDemuxer.ReadCodecIdAsync(ms, CancellationToken.None);
        ScrcpySessionMeta meta = await ScrcpyDemuxer.ReadInitialVideoSessionAsync(ms, CancellationToken.None);

        Assert.Equal("Pixel 8 Pro", name);
        Assert.Equal(ScrcpyConstants.CodecIdH264, codec);
        Assert.Equal(1080, meta.Width);
        Assert.Equal(2400, meta.Height);
        Assert.True(meta.ClientResized);
    }

    [Fact]
    public async Task VideoReader_MergesConfigPacketWithNextMediaPacket_AndDetectsOrientationChange()
    {
        using var ms = new MemoryStream();

        byte[] spsPps = [0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1E];
        byte[] idrFrame = [0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84];

        WriteMediaPacket(ms, ScrcpyConstants.PacketFlagConfig, spsPps);
        WriteMediaPacket(ms, ScrcpyConstants.PacketFlagKeyFrame | 12345UL, idrFrame);

        // Giả lập xoay màn hình gửi session header mới
        byte[] rotatedSession = new byte[12];
        rotatedSession[0] = 0x80;
        BinaryPrimitives.WriteInt32BigEndian(rotatedSession.AsSpan(4, 4), 2400);
        BinaryPrimitives.WriteInt32BigEndian(rotatedSession.AsSpan(8, 4), 1080);
        ms.Write(rotatedSession);

        ms.Position = 0;
        var reader = new ScrcpyDemuxer.VideoReader(ms, ScrcpyConstants.CodecIdH264);

        using ScrcpyVideoPacket p1 = await reader.ReadNextAsync(CancellationToken.None);
        Assert.Equal(ScrcpyPacketKind.MediaPacket, p1.Kind);
        Assert.True(p1.IsKeyFrame);
        Assert.Equal(12345L, p1.Pts);
        Assert.Equal([.. spsPps, .. idrFrame], p1.Payload.ToArray());

        using ScrcpyVideoPacket p2 = await reader.ReadNextAsync(CancellationToken.None);
        Assert.Equal(ScrcpyPacketKind.SessionChanged, p2.Kind);
        Assert.Equal(2400, p2.SessionMeta.Width);
        Assert.Equal(1080, p2.SessionMeta.Height);

        using ScrcpyVideoPacket p3 = await reader.ReadNextAsync(CancellationToken.None);
        Assert.Equal(ScrcpyPacketKind.EndOfStream, p3.Kind);
    }

    [Fact]
    public async Task AudioReader_ReadsConfigAndAudioFrame()
    {
        using var ms = new MemoryStream();
        byte[] opusHead = Encoding.ASCII.GetBytes("OpusHead\x01\x02\x38\x01\x80\xBB\x00\x00\x00\x00\x00");
        byte[] opusFrame = [0xFC, 0xFF, 0xFE];

        WriteMediaPacket(ms, ScrcpyConstants.PacketFlagConfig, opusHead);
        WriteMediaPacket(ms, 999UL, opusFrame);

        ms.Position = 0;
        var reader = new ScrcpyDemuxer.AudioReader(ms);

        using ScrcpyAudioPacket cfg = await reader.ReadNextAsync(CancellationToken.None);
        Assert.False(cfg.IsEndOfStream);
        Assert.True(cfg.IsConfig);
        Assert.Equal(opusHead, cfg.Payload.ToArray());

        using ScrcpyAudioPacket media = await reader.ReadNextAsync(CancellationToken.None);
        Assert.False(media.IsEndOfStream);
        Assert.False(media.IsConfig);
        Assert.Equal(999L, media.Pts);
        Assert.Equal(opusFrame, media.Payload.ToArray());
    }

    private static void WriteMediaPacket(Stream stream, ulong ptsAndFlags, byte[] payload)
    {
        byte[] hdr = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(0, 8), ptsAndFlags);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(8, 4), (uint)payload.Length);
        stream.Write(hdr);
        stream.Write(payload);
    }
}
