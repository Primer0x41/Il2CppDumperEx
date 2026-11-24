using System;
using System.IO;
using System.Text;
using Xunit;
using Il2CppDumper;

namespace Il2CppDumperEx.Tests
{
    public class ProcessMapTests
    {
        [Fact]
        public void ReconstructsMemoryChunksFromMapsFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ProcessMapTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string mapsPath = Path.Combine(tempDir, "proc-maps.txt");
                string mapsContent = 
@"1000-2000 r-xp 00000000 00:00 12345 /data/app/lib/arm64/libil2cpp.so
2000-3000 rw-p 00001000 00:00 12345 /data/app/lib/arm64/libil2cpp.so
3000-4000 r--p 00000000 00:00 99999 /system/lib64/libc.so
";
                File.WriteAllText(mapsPath, mapsContent);

                string chunk1 = Path.Combine(tempDir, "proc-00001000-00002000.bin");
                byte[] data1 = new byte[0x1000];
                data1[0] = 0x7F; data1[1] = (byte)'E'; data1[2] = (byte)'L'; data1[3] = (byte)'F';
                File.WriteAllBytes(chunk1, data1);

                string chunk2 = Path.Combine(tempDir, "proc-00002000-00003000.bin");
                byte[] data2 = new byte[0x1000];
                data2[0] = 0xAA; data2[1] = 0xBB;
                File.WriteAllBytes(chunk2, data2);

                Assert.True(ProcessMapReader.IsProcessMapFile(mapsPath));

                bool success = ProcessMapReader.TryReconstructElf(mapsPath, out var elfStream, out ulong baseAddress);
                Assert.True(success);
                Assert.NotNull(elfStream);
                Assert.Equal(0x1000UL, baseAddress);
                Assert.Equal(0x2000, elfStream.Length);

                byte[] reconstructed = new byte[elfStream.Length];
                elfStream.ReadExactly(reconstructed, 0, reconstructed.Length);
                Assert.Equal(0x7F, reconstructed[0]);
                Assert.Equal((byte)'E', reconstructed[1]);
                Assert.Equal(0xAA, reconstructed[0x1000]);
                Assert.Equal(0xBB, reconstructed[0x1001]);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void ReconstructsSparseGapsWithoutHugeAllocation()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ProcessMapSparse_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string mapsPath = Path.Combine(tempDir, "proc-maps.txt");
                // Span > 256MB: 0x10000000 to 0x30000000 (512MB gap)
                string mapsContent = 
@"10000000-10001000 r-xp 00000000 00:00 12345 /data/app/lib/arm64/libil2cpp.so
30000000-30001000 rw-p 00001000 00:00 12345 /data/app/lib/arm64/libil2cpp.so
";
                File.WriteAllText(mapsPath, mapsContent);

                string chunk1 = Path.Combine(tempDir, "proc-10000000-10001000.bin");
                byte[] data1 = new byte[0x1000];
                data1[0] = 0x7F; data1[1] = (byte)'E'; data1[2] = (byte)'L'; data1[3] = (byte)'F';
                File.WriteAllBytes(chunk1, data1);

                string chunk2 = Path.Combine(tempDir, "proc-30000000-30001000.bin");
                byte[] data2 = new byte[0x1000];
                data2[0] = 0x11; data2[1] = 0x22;
                File.WriteAllBytes(chunk2, data2);

                bool success = ProcessMapReader.TryReconstructElf(mapsPath, out var elfStream, out ulong baseAddress);
                Assert.True(success);
                Assert.NotNull(elfStream);
                Assert.IsType<SparseMemoryStream>(elfStream);
                // Virtual span from 0x10000000 to 0x30001000 without allocating 512MB RAM
                Assert.Equal(0x20001000, elfStream.Length);

                byte[] head = new byte[4];
                elfStream.Position = 0;
                elfStream.ReadExactly(head, 0, 4);
                Assert.Equal(0x7F, head[0]);
                Assert.Equal((byte)'E', head[1]);

                // Verify reading at sparse virtual address gap returns zeros
                byte[] gap = new byte[2];
                elfStream.Position = 0x1000;
                elfStream.ReadExactly(gap, 0, 2);
                Assert.Equal(0, gap[0]);
                Assert.Equal(0, gap[1]);

                // Verify reading at chunk 2 virtual address (0x30000000 - 0x10000000 = 0x20000000)
                byte[] tail = new byte[2];
                elfStream.Position = 0x20000000;
                elfStream.ReadExactly(tail, 0, 2);
                Assert.Equal(0x11, tail[0]);
                Assert.Equal(0x22, tail[1]);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }
    }
}
