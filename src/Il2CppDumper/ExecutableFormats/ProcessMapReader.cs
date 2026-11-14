using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Il2CppDumper
{
    public class ProcessMapReader
    {
        public static bool IsProcessMapFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            string name = Path.GetFileName(filePath).ToLowerInvariant();
            if (name == "maps.txt" || name.EndsWith("-maps.txt") || name == "maps")
            {
                var fi = new FileInfo(filePath);
                return fi.Length <= 10 * 1024 * 1024; // typical maps file is under a few megabytes
            }
            return false;
        }

        public static bool TryReconstructElf(string mapsFilePath, out MemoryStream elfStream, out ulong baseAddress)
        {
            elfStream = null;
            baseAddress = 0;

            if (!File.Exists(mapsFilePath))
                return false;

            var lines = File.ReadAllLines(mapsFilePath);
            var mapRegex = new Regex(@"^(?<start>[0-9A-Fa-f]+)-(?<end>[0-9A-Fa-f]+)\s+(?<perms>[rwxp\-]{4})\s+(?<offset>[0-9A-Fa-f]+)\s+[0-9A-Fa-f]+:[0-9A-Fa-f]+\s+\d+\s+(?<path>.*)$");

            var regions = new List<(ulong Start, ulong End, string Perms, ulong Offset, string Path)>();
            foreach (var line in lines)
            {
                var match = mapRegex.Match(line.Trim());
                if (match.Success)
                {
                    ulong start = ulong.Parse(match.Groups["start"].Value, NumberStyles.HexNumber);
                    ulong end = ulong.Parse(match.Groups["end"].Value, NumberStyles.HexNumber);
                    string perms = match.Groups["perms"].Value;
                    ulong offset = ulong.Parse(match.Groups["offset"].Value, NumberStyles.HexNumber);
                    string path = match.Groups["path"].Value.Trim();

                    if (path.EndsWith("libil2cpp.so", StringComparison.OrdinalIgnoreCase))
                    {
                        regions.Add((start, end, perms, offset, path));
                    }
                }
            }

            if (regions.Count == 0)
                return false;

            ulong minStart = regions.Min(r => r.Start);
            ulong maxEnd = regions.Max(r => r.End);
            baseAddress = minStart;

            string dir = Path.GetDirectoryName(mapsFilePath) ?? ".";
            string fileName = Path.GetFileName(mapsFilePath);
            string prefix = fileName.EndsWith("-maps.txt", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^9]
                : (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName);

            // Look for dump chunks: <prefix>-<start>-<end>.bin
            var fileRegex = new Regex(@"-(?<start>[0-9A-Fa-f]+)-(?<end>[0-9A-Fa-f]+)\.bin$", RegexOptions.IgnoreCase);
            var chunkFiles = Directory.GetFiles(dir, "*.bin")
                .Select(f => new { Path = f, Match = fileRegex.Match(Path.GetFileName(f)) })
                .Where(x => x.Match.Success)
                .Select(x => new
                {
                    Path = x.Path,
                    Start = ulong.Parse(x.Match.Groups["start"].Value, NumberStyles.HexNumber),
                    End = ulong.Parse(x.Match.Groups["end"].Value, NumberStyles.HexNumber)
                })
                .Where(x => x.Start >= minStart && x.End <= maxEnd)
                .OrderBy(x => x.Start)
                .ToList();

            if (chunkFiles.Count == 0)
            {
                string fallbackSo = Path.Combine(dir, "libil2cpp.so");
                if (File.Exists(fallbackSo))
                {
                    byte[] soBytes = File.ReadAllBytes(fallbackSo);
                    elfStream = new MemoryStream(soBytes);
                    return true;
                }
                return false;
            }

            long totalChunkBytes = chunkFiles.Sum(c => (long)(c.End - c.Start));
            if (totalChunkBytes <= 0)
                return false;

            long totalSpan = (long)(maxEnd - minStart);
            const long MaxContiguousSpan = 256L * 1024 * 1024;

            if (totalSpan > 0 && totalSpan <= MaxContiguousSpan)
            {
                byte[] fullMemory = new byte[totalSpan];
                foreach (var chunk in chunkFiles)
                {
                    long destOffset = (long)(chunk.Start - minStart);
                    using var fs = File.OpenRead(chunk.Path);
                    long readLen = Math.Min(fs.Length, (long)(chunk.End - chunk.Start));
                    if (destOffset + readLen <= fullMemory.Length)
                    {
                        fs.ReadExactly(fullMemory, (int)destOffset, (int)readLen);
                    }
                }
                elfStream = new MemoryStream(fullMemory);
                return true;
            }
            else
            {
                // Sparse address space: segments are far apart (ASLR/bss).
                // Compact chunks sequentially into a unified stream to avoid allocating sparse address gaps.
                byte[] compactedMemory = new byte[totalChunkBytes];
                long writeCursor = 0;

                foreach (var chunk in chunkFiles)
                {
                    using var fs = File.OpenRead(chunk.Path);
                    long readLen = Math.Min(fs.Length, (long)(chunk.End - chunk.Start));
                    if (writeCursor + readLen <= compactedMemory.Length)
                    {
                        fs.ReadExactly(compactedMemory, (int)writeCursor, (int)readLen);
                        writeCursor += readLen;
                    }
                }

                elfStream = new MemoryStream(compactedMemory);
                return true;
            }
        }
    }
}
