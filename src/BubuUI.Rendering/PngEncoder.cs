using System.IO.Compression;

namespace BubuUI.Rendering;

/// <summary>
/// 最小 PNG 编码器（RGBA8、无交错、filter 0）。
/// 只依赖 BCL，避免为一张调试图引入图像库；输出字节确定，可用于哈希对比。
/// </summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(RenderTarget target)
    {
        using var stream = new MemoryStream();
        Write(stream, target);
        return stream.ToArray();
    }

    public static void Write(string path, RenderTarget target)
    {
        using var stream = File.Create(path);
        Write(stream, target);
    }

    public static void Write(Stream stream, RenderTarget target)
    {
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr[0..4], (uint)target.Width);
        WriteBigEndian(ihdr[4..8], (uint)target.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(stream, "IHDR", ihdr);

        using var raw = new MemoryStream();
        for (int y = 0; y < target.Height; y++)
        {
            raw.WriteByte(0); // filter: none
            raw.Write(target.Pixels, y * target.Stride, target.Stride);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, (uint)data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crcBytes, crc ^ 0xFFFFFFFF);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WriteBigEndian(Span<byte> target, uint value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
