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

        public static bool TryReconstructElf(string mapsFilePath, out Stream elfStream, out ulong baseAddress)
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

            var segments = new List<SparseMemoryStream.MemorySegment>(chunkFiles.Count);
            foreach (var chunk in chunkFiles)
            {
                byte[] data = File.ReadAllBytes(chunk.Path);
                segments.Add(new SparseMemoryStream.MemorySegment(chunk.Start, chunk.End, data));
            }

            if (totalSpan > 0 && totalSpan <= MaxContiguousSpan)
            {
                byte[] fullMemory = new byte[totalSpan];
                foreach (var seg in segments)
                {
                    long destOffset = (long)(seg.VirtualStart - minStart);
                    if (destOffset + seg.Data.Length <= fullMemory.Length)
                    {
                        Buffer.BlockCopy(seg.Data, 0, fullMemory, (int)destOffset, seg.Data.Length);
                    }
                }
                elfStream = new MemoryStream(fullMemory);
                return true;
            }
            else
            {
                // Sparse address space: segments are far apart (ASLR/bss).
                // Use SparseMemoryStream to map chunks at their exact virtual addresses
                // without allocating physical memory for address gaps.
                elfStream = new SparseMemoryStream(minStart, segments);
                return true;
            }
        }
    }
}
