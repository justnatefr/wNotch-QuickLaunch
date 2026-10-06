using System.Buffers.Binary;
using System.IO.Compression;

namespace QuickLaunch;

/// <summary>A minimal PNG writer for 32-bit RGBA pixels, so the plugin needs no imaging library.</summary>
public static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="rgba">Width × height pixels, top row first, 4 bytes each in R, G, B, A order, alpha not premultiplied.</param>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException("The pixel buffer does not match the size.", nameof(rgba));
        }

        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;  // bits per channel
        header[9] = 6;  // RGBA
        header[10] = 0; // deflate
        header[11] = 0; // standard filtering
        header[12] = 0; // not interlaced
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // no filter on this row
                zlib.Write(rgba.Slice(y * stride, stride));
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);

        Span<byte> typeBytes = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
