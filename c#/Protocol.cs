using System.Buffers.Binary;

namespace WiFiAutoStreamSync;

public static class Protocol
{
    public static readonly byte[] MagicHeader = [0xAA, 0x55];
    public const int TcpDataPort = 58421;
    public const int HttpManifestPort = 58422;
    public const int UdpBeaconPort = 58423;
    public const int ChunkStreamSize = 4 * 1024 * 1024; // 4MB chunks

    // Existing commands
    public const byte CmdPing = 0x00;
    public const byte CmdConfig = 0x01;
    public const byte CmdManifestExchange = 0x02;
    public const byte CmdFileStream = 0x03;
    public const byte CmdDelete = 0x04;
    public const byte CmdSyncEnd = 0x05;

    // Wireless File Browser & Remote Inspection Commands
    public const byte CmdGetDeviceInfo = 0x06;
    public const byte CmdListDir = 0x07;
    public const byte CmdPullFile = 0x08;
    public const byte CmdPushFileDirect = 0x09;
    public const byte CmdDeletePathDirect = 0x0A;
    public const byte CmdMkdirDirect = 0x0B;

    public static async ValueTask SendExactAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct = default)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Socket disconnected unexpectedly during stream read.");
            totalRead += read;
        }
    }

    public static async Task<bool> ReadAckAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] ack = new byte[1];
        await ReadExactAsync(stream, ack, ct).ConfigureAwait(false);
        return ack[0] == 0x00;
    }
}