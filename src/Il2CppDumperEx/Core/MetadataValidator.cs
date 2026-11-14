using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;

namespace Il2CppDumperEx.Core;

public static class MetadataValidator
{
    public const int MinSaneVersion = 16;
    public const int MaxSaneVersion = 110;

    public static bool IsValidMetadataVersion(long version) =>
        (version >= 16 && version <= 39) || (version >= 104 && version <= 110);

    public static int GetHeaderEntryCount(int version)
    {
        if (version >= 110) return 43;
        if (version >= 108) return 41;
        if (version >= 104) return 32;
        return 31;
    }

    private static readonly Regex IdentifierWordRegex = new(@"[A-Za-z0-9_.`]{3,}\0", RegexOptions.Compiled);

    public static readonly (string Prefix, int Version)[] UnityToMetadataMap =
    [
        ("6000.", 110),  // Unity 6 modern
        ("2023.3", 35),
        ("2023.2", 33),
        ("2023.1", 31),
        ("2022.3", 31),
        ("2022.2", 31),
        ("2022.1", 31),
        ("2021.3", 29),
        ("2021.2", 29),
        ("2021.1", 29),
        ("2020.3", 27),
        ("2020.2", 27),
        ("2020.1", 27),
        ("2019.4", 24),
        ("2019.3", 24),
        ("2019.2", 24),
        ("2019.1", 24),
        ("2018.", 24),
        ("2017.", 24),
        ("5.6", 23),
        ("5.5", 22),
        ("5.4", 21),
        ("5.3", 20)
    ];

    public static readonly int[] DefaultCandidateVersions = [31, 29, 27, 24, 110, 108, 106, 105, 104, 39, 38, 35, 33, 23, 22, 21, 20];

    public static readonly (int Version, string Label)[] SupportedVersionEntries =
    [
        (110, "Version 110 (Unity 6 / 6000.x)"),
        (108, "Version 108 (Unity 6)"),
        (106, "Version 106 (Unity 6)"),
        (105, "Version 105 (Unity 6)"),
        (104, "Version 104 (Unity 6)"),
        (39, "Version 39 (Unity 6 / 6000.x early)"),
        (38, "Version 38 (Unity 6 experimental)"),
        (35, "Version 35 (Unity 2023.3)"),
        (33, "Version 33 (Unity 2023.2)"),
        (31, "Version 31 (Unity 2022.1 - 2023.1)"),
        (29, "Version 29 (Unity 2021.1 - 2021.3)"),
        (27, "Version 27 (Unity 2020.1 - 2020.3)"),
        (24, "Version 24 (Unity 2017.1 - 2019.4)"),
        (23, "Version 23 (Unity 5.6)"),
        (22, "Version 22 (Unity 5.5)"),
        (21, "Version 21 (Unity 5.4)"),
        (20, "Version 20 (Unity 5.3)"),
        (19, "Version 19 (Unity 5.2)"),
        (16, "Version 16 (Unity 5.0 - 5.1)")
    ];

    public static bool IsEncryptedMetadata(string filePath)
    {
        if (!File.Exists(filePath)) return true;
        var fi = new FileInfo(filePath);
        if (fi.Length < 8) return true;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[8];
        if (fs.Read(header) < 8) return true;

        return IsEncryptedMetadata(header);
    }

    public static bool IsEncryptedMetadata(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8) return true;
        if (!header.Slice(0, 4).SequenceEqual(Nep2Decryptor.MagicBytes)) return true;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
        if (IsValidMetadataVersion(version))
        {
            return false; // Valid unencrypted
        }

        return true;
    }

    public static List<int> GetCandidateVersions(string? unityVersion, int? existingHeaderVersion = null)
    {
        List<int> candidates = [];

        if (existingHeaderVersion.HasValue &&
            IsValidMetadataVersion(existingHeaderVersion.Value))
        {
            candidates.Add(existingHeaderVersion.Value);
        }

        if (!string.IsNullOrEmpty(unityVersion))
        {
            foreach (var (prefix, metaVer) in UnityToMetadataMap)
            {
                if (unityVersion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (!candidates.Contains(metaVer))
                    {
                        candidates.Add(metaVer);
                    }
                    break;
                }
            }
        }

        foreach (var ver in DefaultCandidateVersions)
        {
            if (!candidates.Contains(ver) && IsValidMetadataVersion(ver))
            {
                candidates.Add(ver);
            }
        }

        for (int ver = MaxSaneVersion; ver >= MinSaneVersion; ver--)
        {
            if (!candidates.Contains(ver) && IsValidMetadataVersion(ver))
            {
                candidates.Add(ver);
            }
        }

        return candidates;
    }

    public static (bool Valid, string Reason, List<(uint Offset, int Size)> Entries) CheckMetadataHeader(
        ReadOnlySpan<byte> data,
        int version,
        long fileLen)
    {
        int entryStride = version >= 38 ? 12 : 8;
        int numEntries = GetHeaderEntryCount(version);
        int requiredHeaderLen = 8 + numEntries * entryStride;
        if (data.Length < requiredHeaderLen)
        {
            return (false, $"Header data too short ({data.Length} < {requiredHeaderLen} bytes)", []);
        }

        if (!data.Slice(0, 4).SequenceEqual(Nep2Decryptor.MagicBytes))
        {
            return (false, $"Invalid magic: {Convert.ToHexString(data.Slice(0, 4))}", []);
        }

        List<(uint Offset, int Size)> entries = new(numEntries);
        int acceptableCount = 0;
        int nonEmptyCount = 0;
        long largestSectionEnd = 0;

        for (int i = 0; i < numEntries; i++)
        {
            int pos = 8 + i * entryStride;
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos + 4, 4));
            entries.Add((offset, size));

            if (offset == 0 && size == 0)
            {
                acceptableCount++;
            }
            else if (offset >= 8 && size >= 0 && (offset + size) <= fileLen)
            {
                acceptableCount++;
                if (size > 0)
                {
                    nonEmptyCount++;
                    long endPos = offset + size;
                    if (endPos > largestSectionEnd)
                    {
                        largestSectionEnd = endPos;
                    }
                }
            }
        }

        int minAcceptable = version >= 38 ? (int)(numEntries * 0.70) : 14;
        int minNonEmpty = version >= 38 ? (int)(numEntries * 0.50) : 8;

        if (acceptableCount < minAcceptable)
        {
            return (false, $"Only {acceptableCount}/{minAcceptable} acceptable table entries", entries);
        }

        if (nonEmptyCount < minNonEmpty)
        {
            return (false, $"Only {nonEmptyCount}/{minNonEmpty} non-empty table entries", entries);
        }

        if (largestSectionEnd < 256 || largestSectionEnd > fileLen)
        {
            return (false, $"Largest section end ({largestSectionEnd}) must be >= 256 and <= file length ({fileLen})", entries);
        }

        return (true, "Header OK", entries);
    }

    public static (bool Valid, string Reason) CheckStringHeap(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0) return (false, "String section is empty");

        int chunkLen = chunk.Length;
        int printableCount = 0;
        int nullCount = 0;

        for (int i = 0; i < chunkLen; i++)
        {
            byte b = chunk[i];
            if ((b >= 32 && b <= 126) || b == 9 || b == 10 || b == 13)
            {
                printableCount++;
            }
            if (b == 0)
            {
                nullCount++;
            }
        }

        double printableRatio = (double)printableCount / chunkLen;
        if (printableRatio < 0.70)
        {
            return (false, $"String section printable ASCII ratio too low ({printableRatio:P1} < 70%)");
        }

        double nullRatio = (double)nullCount / chunkLen;
        if (nullRatio < 0.01)
        {
            return (false, $"String section null byte ratio too low ({nullRatio:P1} < 1%)");
        }

        // Identifier regex search in binary chunk
        string rawText = System.Text.Encoding.Latin1.GetString(chunk);
        var matches = IdentifierWordRegex.Matches(rawText);
        if (matches.Count < 12)
        {
            return (false, $"Too few identifier-like words in string section ({matches.Count} < 12)");
        }

        return (true, $"String heap OK ({matches.Count} identifiers, {printableRatio:P1} ASCII, {nullRatio:P1} nulls)");
    }

    public static (bool Valid, string Reason) ValidateMetadata(byte[] metadataBytes, int candidateVersion)
    {
        if (metadataBytes.Length < 256)
            return (false, $"Metadata size too small ({metadataBytes.Length} bytes)");

        var (validHeader, headerMsg, entries) = CheckMetadataHeader(metadataBytes.AsSpan(0, Math.Min(1024, metadataBytes.Length)), candidateVersion, metadataBytes.Length);
        if (!validHeader)
        {
            return (false, headerMsg);
        }

        if (candidateVersion < 38)
        {
            if (entries.Count < 3)
                return (false, "Insufficient table entries to locate string section");

            var (stringOffset, stringSize) = entries[2];
            if (stringOffset < 8 || stringSize <= 0 || (stringOffset + stringSize) > metadataBytes.Length)
            {
                return (false, $"Invalid string section bounds: offset 0x{stringOffset:X}, size 0x{stringSize:X}");
            }

            int toRead = Math.Min(stringSize, 65536);
            ReadOnlySpan<byte> stringChunk = metadataBytes.AsSpan((int)stringOffset, toRead);
            var (validHeap, heapMsg) = CheckStringHeap(stringChunk);
            if (!validHeap)
            {
                return (false, heapMsg);
            }
        }

        return (true, "Valid metadata");
    }

    public static void PatchMetadataVersion(Span<byte> metadataBytes, int newVersion)
    {
        if (metadataBytes.Length < 8)
            throw new ArgumentException("Metadata buffer too short to patch version.", nameof(metadataBytes));

        BinaryPrimitives.WriteInt32LittleEndian(metadataBytes.Slice(4, 4), newVersion);
    }
}
