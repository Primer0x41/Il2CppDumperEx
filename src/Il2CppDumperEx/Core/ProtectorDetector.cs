using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Il2CppDumperEx.Core;

public record ProtectorMatch(
    string Name,
    string Vendor,
    string Evidence,
    bool IsDefinite
);

public record EntropyResult(
    double Entropy,
    bool IsPacked
);

public record ProtectionReport(
    bool Detected,
    List<ProtectorMatch> Matches,
    EntropyResult? CodeEntropy,
    string Summary
);

public static class ProtectorDetector
{
    // High-confidence library names associated with commercial packers and anti-tamper engines
    private static readonly (string FileName, string Name, string Vendor)[] KnownProtectorLibraries =
    [
        ("libsecmain.so", "SecNeo", "Bangcle"),
        ("libsecexe.so", "SecNeo", "Bangcle"),
        ("libtersafe.so", "ACE (Anti-Cheat Expert)", "Tencent"),
        ("libanogs.so", "ACE (Anti-Cheat Expert)", "Tencent"),
        ("libshell.so", "Legu Protector", "Tencent"),
        ("libtup.so", "Legu Protector", "Tencent"),
        ("libnesec.so", "YiDun (Dun)", "NetEase"),
        ("libcrashsdk.so", "YiDun (Dun)", "NetEase"),
        ("libjiagu.so", "Jiagu", "Qihoo 360"),
        ("libjiagu_art.so", "Jiagu", "Qihoo 360"),
        ("libjiagu_x86.so", "Jiagu", "Qihoo 360"),
        ("libexec.so", "Ijiami", "Ijiami"),
        ("libexecmain.so", "Ijiami", "Ijiami"),
        ("libbaiduprotect.so", "Baidu Protect", "Baidu"),
        ("ACE-BASE.dll", "ACE (Anti-Cheat Expert)", "Tencent"),
        ("ACE-DRV64.sys", "ACE (Anti-Cheat Expert)", "Tencent")
    ];

    // High-confidence section names in PE/ELF executables
    private static readonly (string SectionName, string Name, string Vendor)[] KnownProtectorSections =
    [
        (".vmp0", "VMProtect", "VMProtect Software"),
        (".vmp1", "VMProtect", "VMProtect Software"),
        (".vmp2", "VMProtect", "VMProtect Software"),
        (".themida", "Themida / WinLicense", "Oreans"),
        (".winlic0", "WinLicense", "Oreans"),
        (".winlic1", "WinLicense", "Oreans"),
        (".bind", "Denuvo Anti-Tamper", "Denuvo / Irdeto"),
        (".arch", "Denuvo Anti-Tamper", "Denuvo / Irdeto"),
        (".secneo", "SecNeo", "Bangcle"),
        (".bangcle", "SecNeo", "Bangcle"),
        (".ne_protect", "YiDun", "NetEase")
    ];

    /// <summary>
    /// Computes Shannon entropy: H(X) = -SUM(P(x) * log2(P(x))) over byte values [0..255].
    /// Values range from 0.0 (uniform bytes) to 8.0 (completely random / encrypted / compressed).
    /// </summary>
    public static double CalculateShannonEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0.0;

        Span<int> frequencies = stackalloc int[256];
        foreach (byte b in data)
        {
            frequencies[b]++;
        }

        double entropy = 0.0;
        double total = data.Length;

        for (int i = 0; i < 256; i++)
        {
            int count = frequencies[i];
            if (count > 0)
            {
                double p = count / total;
                entropy -= p * Math.Log2(p);
            }
        }

        return entropy;
    }

    /// <summary>
    /// Analyzes the game root, binary file, and optional metadata for protector and packer signatures.
    /// </summary>
    public static ProtectionReport Analyze(string gameRoot, string binaryPath, byte[]? metadataBytes = null)
    {
        List<ProtectorMatch> matches = [];

        if (Directory.Exists(gameRoot))
        {
            try
            {
                var enumOptions = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
                foreach (var file in Directory.EnumerateFiles(gameRoot, "*.*", enumOptions))
                {
                    string fileName = Path.GetFileName(file);
                    foreach (var (libName, name, vendor) in KnownProtectorLibraries)
                    {
                        if (fileName.Equals(libName, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(new ProtectorMatch(name, vendor, $"File: {fileName}", IsDefinite: true));
                        }
                    }

                    if (fileName.Equals("0Oo0oo0o0.dat", StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(new ProtectorMatch("Legu / ACE Asset Wrapper", "Tencent", $"Asset: {fileName}", IsDefinite: true));
                    }
                }
            }
            catch { }
        }

        if (File.Exists(binaryPath))
        {
            try
            {
                using var fs = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] headChunk = new byte[Math.Min(65536, fs.Length)];
                int read = fs.Read(headChunk, 0, headChunk.Length);

                string headStr = System.Text.Encoding.Latin1.GetString(headChunk, 0, read);
                foreach (var (secName, name, vendor) in KnownProtectorSections)
                {
                    if (headStr.Contains(secName, StringComparison.Ordinal))
                    {
                        matches.Add(new ProtectorMatch(name, vendor, $"Section: {secName}", IsDefinite: true));
                    }
                }
            }
            catch { }
        }

        if (metadataBytes != null && metadataBytes.Length >= 8)
        {
            if (MetadataValidator.IsEncryptedMetadata(metadataBytes))
            {
                matches.Add(new ProtectorMatch("NEP2 Metadata Encryption", "NetEase", "Magic: 0xFAB11BAF", IsDefinite: true));
            }
        }

        EntropyResult? entropyResult = null;
        if (File.Exists(binaryPath))
        {
            try
            {
                byte[] codeSample = ExtractCodeSample(binaryPath);
                if (codeSample.Length > 0)
                {
                    double entropy = CalculateShannonEntropy(codeSample);
                    bool isPacked = entropy > 7.45;
                    entropyResult = new EntropyResult(entropy, isPacked);
                }
            }
            catch { }
        }

        var distinctMatches = matches
            .GroupBy(m => $"{m.Name}_{m.Vendor}")
            .Select(g => g.First())
            .ToList();

        bool detected = distinctMatches.Count > 0 || (entropyResult?.IsPacked ?? false);
        string summary = detected
            ? (distinctMatches.Count > 0
                ? string.Join(", ", distinctMatches.Select(m => $"{m.Name} ({m.Vendor})"))
                : $"High entropy ({entropyResult!.Entropy:F2}/8.0 - probable packing)")
            : "Clean (No known commercial protectors detected)";

        return new ProtectionReport(
            Detected: detected,
            Matches: distinctMatches,
            CodeEntropy: entropyResult,
            Summary: summary
        );
    }

    private static byte[] ExtractCodeSample(string binaryPath)
    {
        using var fs = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLen = fs.Length;
        if (fileLen < 64) return [];

        const int maxSample = 256 * 1024;
        byte[] ident = new byte[64];
        fs.ReadExactly(ident, 0, 64);

        if (ident[0] == 'M' && ident[1] == 'Z')
        {
            int peOffset = BitConverter.ToInt32(ident, 0x3C);
            if (peOffset > 0 && peOffset + 24 < fileLen)
            {
                fs.Position = peOffset;
                byte[] peSig = new byte[4];
                fs.ReadExactly(peSig, 0, 4);
                if (peSig[0] == 'P' && peSig[1] == 'E' && peSig[2] == 0 && peSig[3] == 0)
                {
                    fs.Position = peOffset + 4 + 16; // Skip to SizeOfOptionalHeader
                    byte[] optHdrSizeBuf = new byte[2];
                    fs.ReadExactly(optHdrSizeBuf, 0, 2);
                    ushort optHdrSize = BitConverter.ToUInt16(optHdrSizeBuf, 0);

                    long sectionTableOffset = peOffset + 24 + optHdrSize;
                    fs.Position = peOffset + 4 + 2; // NumberOfSections
                    byte[] numSecBuf = new byte[2];
                    fs.ReadExactly(numSecBuf, 0, 2);
                    ushort numSections = BitConverter.ToUInt16(numSecBuf, 0);

                    fs.Position = sectionTableOffset;
                    for (int s = 0; s < numSections; s++)
                    {
                        byte[] secHeader = new byte[40];
                        if (fs.Read(secHeader, 0, 40) < 40) break;
                        string secName = System.Text.Encoding.ASCII.GetString(secHeader, 0, 8).TrimEnd('\0');
                        uint rawDataSize = BitConverter.ToUInt32(secHeader, 16);
                        uint rawDataPtr = BitConverter.ToUInt32(secHeader, 20);
                        uint characteristics = BitConverter.ToUInt32(secHeader, 36);

                        if (secName.Equals(".text", StringComparison.OrdinalIgnoreCase) || (characteristics & 0x20000000) != 0)
                        {
                            if (rawDataPtr > 0 && rawDataSize > 0 && rawDataPtr < fileLen)
                            {
                                int toRead = (int)Math.Min(maxSample, Math.Min(rawDataSize, fileLen - rawDataPtr));
                                byte[] code = new byte[toRead];
                                fs.Position = rawDataPtr;
                                fs.ReadExactly(code, 0, toRead);
                                return code;
                            }
                        }
                    }
                }
            }
        }

        if (ident[0] == 0x7F && ident[1] == (byte)'E' && ident[2] == (byte)'L' && ident[3] == (byte)'F')
        {
            byte elfClass = ident[4]; // 1 = 32-bit, 2 = 64-bit
            if (elfClass == 2 && fileLen >= 64) // ELF64
            {
                fs.Position = 32; // e_phoff
                byte[] phoffBuf = new byte[8];
                fs.ReadExactly(phoffBuf, 0, 8);
                long phoff = BitConverter.ToInt64(phoffBuf, 0);

                fs.Position = 54; // e_phentsize (2), e_phnum (2)
                byte[] phInfo = new byte[4];
                fs.ReadExactly(phInfo, 0, 4);
                ushort phentsize = BitConverter.ToUInt16(phInfo, 0);
                ushort phnum = BitConverter.ToUInt16(phInfo, 2);

                if (phoff > 0 && phentsize >= 32 && phoff < fileLen)
                {
                    for (int p = 0; p < phnum; p++)
                    {
                        fs.Position = phoff + (p * phentsize);
                        byte[] phead = new byte[Math.Min(56, (int)phentsize)];
                        if (fs.Read(phead, 0, phead.Length) < phead.Length) break;

                        uint p_type = BitConverter.ToUInt32(phead, 0);
                        uint p_flags = BitConverter.ToUInt32(phead, 4);
                        ulong p_offset = BitConverter.ToUInt64(phead, 8);
                        ulong p_filesz = BitConverter.ToUInt64(phead, 32);

                        if (p_type == 1 && (p_flags & 1) != 0 && p_filesz > 0 && (long)p_offset < fileLen)
                        {
                            int toRead = (int)Math.Min(maxSample, Math.Min((long)p_filesz, fileLen - (long)p_offset));
                            byte[] code = new byte[toRead];
                            fs.Position = (long)p_offset;
                            fs.ReadExactly(code, 0, toRead);
                            return code;
                        }
                    }
                }
            }
        }

        // Fallback: sliding window near code offset or default head slice
        long fallbackOffset = Math.Min(4096, Math.Max(0, fileLen - maxSample));
        int fallbackRead = (int)Math.Min(maxSample, fileLen - fallbackOffset);
        byte[] fallback = new byte[fallbackRead];
        fs.Position = fallbackOffset;
        fs.ReadExactly(fallback, 0, fallbackRead);
        return fallback;
    }
}
