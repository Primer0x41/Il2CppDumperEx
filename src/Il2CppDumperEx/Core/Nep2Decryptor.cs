using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Il2CppDumperEx.Core;

public class Nep2Exception : Exception
{
    public Nep2Exception(string message) : base(message) { }
    public Nep2Exception(string message, Exception inner) : base(message, inner) { }
}

public class MetadataTooSmallException : Nep2Exception
{
    public MetadataTooSmallException(string message) : base(message) { }
}

public class InvalidMagicException : Nep2Exception
{
    public InvalidMagicException(string message) : base(message) { }
}

public class AlreadyDecryptedException : Nep2Exception
{
    public AlreadyDecryptedException(string message) : base(message) { }
}

/// <summary>
/// Precomputed NEP2 keys, tiled 1152-byte mask cycle, and trailing byte masks.
/// </summary>
public sealed class Nep2KeySchedule
{
    public int PayloadSize { get; }
    public uint Seed { get; }
    public uint TempKey { get; }
    public byte[] Head128Bytes { get; }
    public byte[] KeyBlockRaw { get; }
    public uint[] KeyBlockDwords { get; }
    public uint[] ShortKey { get; }
    public uint[] LongKey { get; }
    public byte[] CycleMask1152 { get; }

    public Nep2KeySchedule(ReadOnlySpan<byte> rawHeader136, int payloadSize)
    {
        if (rawHeader136.Length < 136)
            throw new MetadataTooSmallException("Header must be at least 136 bytes to derive key schedule.");

        PayloadSize = payloadSize;

        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, (uint)payloadSize);
        Seed = Nep2Decryptor.CalculateCrc(seedBytes);

        ReadOnlySpan<byte> data128 = rawHeader136.Slice(8, 128);
        TempKey = Seed ^ BinaryPrimitives.ReadUInt32LittleEndian(data128.Slice(24, 4));

        byte[] keyBlockBytes = data128.ToArray();
        byte[] head128Bytes = data128.ToArray();
        Nep2Decryptor.Rc4(keyBlockBytes, TempKey);
        Nep2Decryptor.Rc4(head128Bytes, Seed);

        Head128Bytes = head128Bytes;
        KeyBlockRaw = keyBlockBytes;

        KeyBlockDwords = new uint[32];
        for (int i = 0; i < 32; i++)
        {
            KeyBlockDwords[i] = BinaryPrimitives.ReadUInt32LittleEndian(KeyBlockRaw.AsSpan(i * 4, 4));
        }

        ShortKey = new uint[4];
        ShortKey[0] = Seed ^ (BinaryPrimitives.ReadUInt32LittleEndian(data128.Slice(32, 4)) + 0x8195Eu);
        ShortKey[1] = TempKey ^ (BinaryPrimitives.ReadUInt32LittleEndian(data128.Slice(76, 4)) + 0x75568u);
        ShortKey[2] = Seed ^ (KeyBlockDwords[11] + 0x3482A5u);
        ShortKey[3] = TempKey ^ (KeyBlockDwords[15] + 0xA7498Du);

        LongKey = new uint[9];
        LongKey[0] = KeyBlockDwords[3] ^ 0x3914F13u;
        LongKey[1] = ShortKey[3] ^ 0x34E00Eu;
        LongKey[2] = LongKey[0] ^ KeyBlockDwords[7];
        LongKey[7] = ShortKey[2] ^ LongKey[1];
        LongKey[3] = LongKey[7] ^ KeyBlockDwords[8];
        LongKey[5] = ShortKey[1] ^ 0x836E4Cu;
        LongKey[4] = ShortKey[0] ^ LongKey[5];
        LongKey[6] = BinaryPrimitives.ReadUInt32LittleEndian(data128.Slice(16, 4)) ^ 0x31BB9B1u;
        LongKey[8] = BinaryPrimitives.ReadUInt32LittleEndian(data128.Slice(24, 4)) ^ ShortKey[0] ^ LongKey[5];

        // Precompute the four 128-byte block masks (32 uints each)
        uint[][] maskDwords = new uint[4][];
        for (int t = 0; t < 4; t++)
        {
            maskDwords[t] = new uint[32];
        }

        uint[] kb = KeyBlockDwords;
        uint[] sk = ShortKey;
        uint[] lk = LongKey;

        for (int j = 0; j < 32; j++)
        {
            maskDwords[0][j] = kb[j] ^ sk[kb[j] % 4] ^ (uint)j;
            maskDwords[1][j] = kb[j] ^ sk[lk[j % 9] % 4] ^ (uint)j;
            maskDwords[2][j] = kb[j] ^ lk[j % 9] ^ (uint)(32 - j);
            maskDwords[3][j] = kb[j] ^ sk[kb[j] % 4];
        }

        byte[][] maskBytes = new byte[4][];
        for (int t = 0; t < 4; t++)
        {
            maskBytes[t] = new byte[128];
            for (int j = 0; j < 32; j++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(maskBytes[t].AsSpan(j * 4, 4), maskDwords[t][j]);
            }
        }

        // Assemble 9-block (1152-byte) tiled cycle mask
        CycleMask1152 = new byte[9 * 128];
        for (int i = 0; i < 9; i++)
        {
            int decType = (int)(LongKey[i] % 4);
            Buffer.BlockCopy(maskBytes[decType], 0, CycleMask1152, i * 128, 128);
        }
    }

    public byte[] ComputeTrailingMask(int trailingBytes)
    {
        if (trailingBytes <= 0) return Array.Empty<byte>();

        byte[] mask = new byte[trailingBytes];
        for (int i = 0; i < trailingBytes; i++)
        {
            uint skIdx = ShortKey[i & 3];
            uint lkVal = LongKey[skIdx % 9];
            mask[i] = (byte)((KeyBlockRaw[i] ^ (lkVal % 0xFF) ^ (uint)i) & 0xFF);
        }
        return mask;
    }
}

/// <summary>
/// Hardened, streaming, zero-dependency C# implementation of the NEP2 Decryptor.
/// </summary>
public static class Nep2Decryptor
{
    public static readonly byte[] MagicBytes = [0xAF, 0x1B, 0xB1, 0xFA];
    public const int MinimumFileSize = 136;
    private const uint Poly = 0x9823D6Eu;
    private static readonly uint[] CrcTable = new uint[256];

    static Nep2Decryptor()
    {
        for (uint i = 0; i < 256; i++)
        {
            uint r = i;
            for (int j = 0; j < 8; j++)
            {
                if ((r & 1) != 0)
                {
                    r ^= Poly;
                }
                r >>= 1;
            }
            CrcTable[i] = r;
        }
    }

    public static uint CalculateCrc(ReadOnlySpan<byte> data)
    {
        uint value = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            uint idx = (value ^ b) & 0xFF;
            value = (CrcTable[idx] ^ (value >> 8)) + 0x10;
        }
        return ~value - 0x7D29C488u;
    }

    public static void Rc4(Span<byte> data, uint key)
    {
        Span<byte> keyBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, key);

        Span<byte> s = stackalloc byte[256];
        for (int idx = 0; idx < 256; idx++)
        {
            s[idx] = (byte)idx;
        }

        int j = 0;
        for (int idx = 0; idx < 256; idx++)
        {
            j = (j + s[idx] + keyBytes[idx & 3]) & 0xFF;
            byte tmp = s[idx];
            s[idx] = s[j];
            s[j] = tmp;
        }

        int i = 0;
        j = 0;
        for (int p = 0; p < data.Length; p++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            byte tmp = s[i];
            s[i] = s[j];
            s[j] = tmp;
            byte kVal = s[(s[i] + s[j]) & 0xFF];
            int kRot = (kVal << 6 | kVal >> 2) & 0xFF;
            data[p] ^= (byte)((kRot + 0x3A) & 0xFF);
        }
    }

    /// <summary>
    /// Vectorized/SIMD XOR between data and mask spans.
    /// </summary>
    public static void XorBuffers(Span<byte> target, ReadOnlySpan<byte> source, ReadOnlySpan<byte> mask)
    {
        int length = Math.Min(target.Length, Math.Min(source.Length, mask.Length));
        int offset = 0;

        // Hardware-accelerated SIMD
        int vectorSize = Vector<byte>.Count;
        while (offset + vectorSize <= length)
        {
            var vSource = new Vector<byte>(source.Slice(offset, vectorSize));
            var vMask = new Vector<byte>(mask.Slice(offset, vectorSize));
            (vSource ^ vMask).CopyTo(target.Slice(offset, vectorSize));
            offset += vectorSize;
        }

        // 64-bit word fallback
        while (offset + sizeof(ulong) <= length)
        {
            ulong s = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, sizeof(ulong)));
            ulong m = BinaryPrimitives.ReadUInt64LittleEndian(mask.Slice(offset, sizeof(ulong)));
            BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(offset, sizeof(ulong)), s ^ m);
            offset += sizeof(ulong);
        }

        // Byte remainder
        while (offset < length)
        {
            target[offset] = (byte)(source[offset] ^ mask[offset]);
            offset++;
        }
    }

    /// <summary>
    /// Decrypts raw NEP2 metadata bytes in memory.
    /// </summary>
    public static byte[] DecryptBytes(
        ReadOnlySpan<byte> rawData,
        uint outputVersion = 31,
        Action<long, long>? progressCallback = null)
    {
        if (rawData.Length < MinimumFileSize)
            throw new MetadataTooSmallException($"File too small ({rawData.Length} bytes, minimum {MinimumFileSize}).");

        if (!rawData.Slice(0, 4).SequenceEqual(MagicBytes))
            throw new InvalidMagicException($"Invalid magic: {Convert.ToHexString(rawData.Slice(0, 4))} (expected AF1BB1FA).");

        uint headerVal = BinaryPrimitives.ReadUInt32LittleEndian(rawData.Slice(4, 4));
        if (headerVal < 100)
            throw new AlreadyDecryptedException($"Input already appears decrypted (version {headerVal}).");

        int fileSize = rawData.Length;
        int payloadSize = fileSize - 8;
        var schedule = new Nep2KeySchedule(rawData.Slice(0, 136), payloadSize);

        ReadOnlySpan<byte> dataPayload = rawData.Slice(136);
        int totalBlocksBytes = (dataPayload.Length / 128) * 128;
        int trailingCount = dataPayload.Length % 128;

        byte[] output = new byte[fileSize];
        Span<byte> outSpan = output.AsSpan();

        // 1. Output Header (Magic + Version + Head 128 bytes)
        MagicBytes.CopyTo(outSpan.Slice(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(outSpan.Slice(4, 4), outputVersion);
        schedule.Head128Bytes.CopyTo(outSpan.Slice(8, 128));

        // 2. Full 128-byte blocks via 1152-byte cycle mask
        if (totalBlocksBytes > 0)
        {
            ReadOnlySpan<byte> blocksSource = rawData.Slice(136, totalBlocksBytes);
            Span<byte> blocksDest = outSpan.Slice(136, totalBlocksBytes);

            int cycleLen = schedule.CycleMask1152.Length; // 1152
            int fullCycles = totalBlocksBytes / cycleLen;
            for (int c = 0; c < fullCycles; c++)
            {
                int cycleOffset = c * cycleLen;
                XorBuffers(blocksDest.Slice(cycleOffset, cycleLen), blocksSource.Slice(cycleOffset, cycleLen), schedule.CycleMask1152);
            }

            int remainder = totalBlocksBytes % cycleLen;
            if (remainder > 0)
            {
                int remOffset = fullCycles * cycleLen;
                XorBuffers(blocksDest.Slice(remOffset, remainder), blocksSource.Slice(remOffset, remainder), schedule.CycleMask1152.AsSpan(0, remainder));
            }
        }

        progressCallback?.Invoke(136 + totalBlocksBytes, fileSize);

        // 3. Trailing bytes
        if (trailingCount > 0)
        {
            int startOffset = 136 + totalBlocksBytes;
            ReadOnlySpan<byte> trailingData = rawData.Slice(startOffset, trailingCount);
            byte[] trailingMask = schedule.ComputeTrailingMask(trailingCount);
            XorBuffers(outSpan.Slice(startOffset, trailingCount), trailingData, trailingMask);
        }

        progressCallback?.Invoke(fileSize, fileSize);
        return output;
    }

    /// <summary>
    /// Stream-decrypts NEP2 global-metadata.dat file chunk by chunk to maintain constant low memory usage.
    /// </summary>
    public static void DecryptFile(
        string inputPath,
        string outputPath,
        uint outputVersion = 31,
        Action<long, long>? progressCallback = null,
        int chunkSize = 1024 * 1152) // 1.12 MB aligned to 1152
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input file not found: {inputPath}");

        long fileLength = new FileInfo(inputPath).Length;
        if (fileLength < MinimumFileSize)
            throw new MetadataTooSmallException($"File too small ({fileLength} bytes, minimum {MinimumFileSize}).");

        using var fin = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);

        byte[] header136 = new byte[136];
        int readHdr = fin.Read(header136, 0, 136);
        if (readHdr < 136)
            throw new MetadataTooSmallException("Unexpected EOF while reading header.");

        if (!header136.AsSpan(0, 4).SequenceEqual(MagicBytes))
            throw new InvalidMagicException($"Invalid magic: {Convert.ToHexString(header136.AsSpan(0, 4))}.");

        uint headerVal = BinaryPrimitives.ReadUInt32LittleEndian(header136.AsSpan(4, 4));
        if (headerVal < 100)
            throw new AlreadyDecryptedException($"Input already appears decrypted (version {headerVal}).");

        int payloadSize = (int)(fileLength - 8);
        var schedule = new Nep2KeySchedule(header136, payloadSize);

        long payloadDataLen = fileLength - 136;
        long fullBlocksBytes = (payloadDataLen / 128) * 128;
        int trailingCount = (int)(payloadDataLen % 128);

        // Ensure chunk size is multiple of 1152
        if (chunkSize % 1152 != 0)
        {
            chunkSize = ((chunkSize / 1152) + 1) * 1152;
        }

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        using var fout = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);

        // 1. Write header
        fout.Write(MagicBytes, 0, 4);
        Span<byte> verBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(verBytes, outputVersion);
        fout.Write(verBytes);
        fout.Write(schedule.Head128Bytes, 0, 128);

        long bytesWritten = 136;
        progressCallback?.Invoke(bytesWritten, fileLength);

        // 2. Stream blocks
        byte[] buffer = new byte[chunkSize];
        byte[] extendedMask = new byte[chunkSize + 1152];
        // Populate extended mask with repeating 1152-byte cycle
        for (int i = 0; i < extendedMask.Length; i += 1152)
        {
            int toCopy = Math.Min(1152, extendedMask.Length - i);
            Buffer.BlockCopy(schedule.CycleMask1152, 0, extendedMask, i, toCopy);
        }

        long remainingBlocks = fullBlocksBytes;
        int cycleOffset = 0;

        while (remainingBlocks > 0)
        {
            int toRead = (int)Math.Min(remainingBlocks, chunkSize);
            int readBytes = fin.Read(buffer, 0, toRead);
            if (readBytes == 0) break;

            XorBuffers(buffer.AsSpan(0, readBytes), buffer.AsSpan(0, readBytes), extendedMask.AsSpan(cycleOffset, readBytes));
            fout.Write(buffer, 0, readBytes);

            cycleOffset = (cycleOffset + readBytes) % 1152;
            remainingBlocks -= readBytes;
            bytesWritten += readBytes;
            progressCallback?.Invoke(bytesWritten, fileLength);
        }

        // 3. Trailing bytes
        if (trailingCount > 0)
        {
            byte[] trailingBuffer = new byte[trailingCount];
            int readTrailing = fin.Read(trailingBuffer, 0, trailingCount);
            byte[] trailingMask = schedule.ComputeTrailingMask(trailingCount);
            XorBuffers(trailingBuffer.AsSpan(0, readTrailing), trailingBuffer.AsSpan(0, readTrailing), trailingMask.AsSpan(0, readTrailing));
            fout.Write(trailingBuffer, 0, readTrailing);
            bytesWritten += readTrailing;
            progressCallback?.Invoke(bytesWritten, fileLength);
        }
    }
}
