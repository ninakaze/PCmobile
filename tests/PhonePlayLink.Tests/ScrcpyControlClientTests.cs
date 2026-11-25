using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PhonePlayLink.Scrcpy;

namespace PhonePlayLink.Tests;

public class ScrcpyControlClientTests
{
    [Fact]
    public void SerializeTouchEvent_WritesExact32BytesBigEndianScrcpyV5Format()
    {
        Span<byte> buf = stackalloc byte[ScrcpyConstants.InjectTouchEventPacketSize];

        ScrcpyControlClient.SerializeTouchEvent(
            buf,
            AndroidMotionAction.Move,
            pointerId: 7UL,
            x: 1234,
            y: 567,
            screenWidth: 2400,
            screenHeight: 1080,
            pressure: 1.0f,
            actionButton: 0,
            buttons: 0);

        Assert.Equal(ScrcpyConstants.ControlMsgTypeInjectTouchEvent, buf[0]);
        Assert.Equal((byte)AndroidMotionAction.Move, buf[1]);
        Assert.Equal(7UL, BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(2, 8)));
        Assert.Equal(1234, BinaryPrimitives.ReadInt32BigEndian(buf.Slice(10, 4)));
        Assert.Equal(567, BinaryPrimitives.ReadInt32BigEndian(buf.Slice(14, 4)));
        Assert.Equal((ushort)2400, BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(18, 2)));
        Assert.Equal((ushort)1080, BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(20, 2)));
        Assert.Equal((ushort)0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(22, 2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(24, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(28, 4)));
    }

    [Fact]
    public void FloatToU16FixedPoint_MatchesScrcpyBinaryH()
    {
        Assert.Equal((ushort)0x0000, ScrcpyControlClient.FloatToU16FixedPoint(0.0f));
        Assert.Equal((ushort)0x8000, ScrcpyControlClient.FloatToU16FixedPoint(0.5f));
        Assert.Equal((ushort)0xFFFF, ScrcpyControlClient.FloatToU16FixedPoint(1.0f));
        Assert.Equal((ushort)0xFFFF, ScrcpyControlClient.FloatToU16FixedPoint(1.5f));
    }

    [Fact]
    public async Task ControlClient_SendsRaw32BytePacketsOverTcp_AndReflectsRotatedScreenSize()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<Socket> acceptTask = listener.AcceptSocketAsync();
        using var clientSock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSock.ConnectAsync(IPAddress.Loopback, port);
        using Socket serverSock = await acceptTask;

        using var controlClient = new ScrcpyControlClient(clientSock, 1080, 2400);

        controlClient.SendTouchEvent(AndroidMotionAction.Down, pointerId: 2, x: 500, y: 1200, pressure: 1.0f);

        controlClient.UpdateScreenSize(2400, 1080);
        controlClient.SendTouchEvent(AndroidMotionAction.Up, pointerId: 2, x: 2000, y: 900, pressure: 0.0f);

        byte[] recv = new byte[64];
        int total = 0;
        while (total < 64)
        {
            int n = await serverSock.ReceiveAsync(recv.AsMemory(total, 64 - total), SocketFlags.None);
            Assert.True(n > 0);
            total += n;
        }

        // Gói 1: Portrait 1080x2400
        Assert.Equal(2, recv[0]);
        Assert.Equal((byte)AndroidMotionAction.Down, recv[1]);
        Assert.Equal(1080, BinaryPrimitives.ReadUInt16BigEndian(recv.AsSpan(18, 2)));
        Assert.Equal(2400, BinaryPrimitives.ReadUInt16BigEndian(recv.AsSpan(20, 2)));
        Assert.Equal((ushort)0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(recv.AsSpan(22, 2)));

        // Gói 2: Sau xoay Landscape 2400x1080
        Assert.Equal(2, recv[32]);
        Assert.Equal((byte)AndroidMotionAction.Up, recv[33]);
        Assert.Equal(2000, BinaryPrimitives.ReadInt32BigEndian(recv.AsSpan(42, 4)));
        Assert.Equal(900, BinaryPrimitives.ReadInt32BigEndian(recv.AsSpan(46, 4)));
        Assert.Equal(2400, BinaryPrimitives.ReadUInt16BigEndian(recv.AsSpan(50, 2)));
        Assert.Equal(1080, BinaryPrimitives.ReadUInt16BigEndian(recv.AsSpan(52, 2)));
        Assert.Equal((ushort)0x0000, BinaryPrimitives.ReadUInt16BigEndian(recv.AsSpan(54, 2)));
    }

    [Fact]
    public void SerializeSetDisplayPower_WritesExact2BytesSpec()
    {
        Span<byte> off = stackalloc byte[2];
        ScrcpyControlClient.SerializeSetDisplayPower(off, powerOn: false);
        Assert.Equal(ScrcpyConstants.ControlMsgTypeSetDisplayPower, off[0]);
        Assert.Equal((byte)0, off[1]);

        Span<byte> on = stackalloc byte[2];
        ScrcpyControlClient.SerializeSetDisplayPower(on, powerOn: true);
        Assert.Equal(ScrcpyConstants.ControlMsgTypeSetDisplayPower, on[0]);
        Assert.Equal((byte)1, on[1]);
    }

    [Fact]
    public async Task ControlClient_SetDisplayPower_SendsPacketsAndTracksPowerState()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<Socket> acceptTask = listener.AcceptSocketAsync();
        using var clientSock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSock.ConnectAsync(IPAddress.Loopback, port);
        using Socket serverSock = await acceptTask;

        using var controlClient = new ScrcpyControlClient(clientSock, 1080, 2400);
        Assert.True(controlClient.IsScreenOn);

        controlClient.SetDisplayPower(false);
        Assert.False(controlClient.IsScreenOn);

        controlClient.SetDisplayPower(true);
        Assert.True(controlClient.IsScreenOn);

        byte[] recv = new byte[4];
        int total = 0;
        while (total < 4)
        {
            int n = await serverSock.ReceiveAsync(recv.AsMemory(total, 4 - total), SocketFlags.None);
            Assert.True(n > 0);
            total += n;
        }

        Assert.Equal([10, 0, 10, 1], recv);
    }
}
