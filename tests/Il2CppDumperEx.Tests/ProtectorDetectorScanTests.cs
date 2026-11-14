using System;
using System.IO;
using Il2CppDumperEx.Core;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class ProtectorDetectorScanTests
{
    [Fact]
    public void Analyze_WhenGameRootDoesNotExist_CompletesGracefully()
    {
        string fakeDir = Path.Combine(Path.GetTempPath(), "non_existent_dir_" + Guid.NewGuid().ToString("N"));
        string fakeBin = Path.Combine(fakeDir, "fake.dll");

        var report = ProtectorDetector.Analyze(fakeDir, fakeBin);
        Assert.NotNull(report);
        Assert.False(report.Detected);
        Assert.Empty(report.Matches);
    }

    [Fact]
    public void Analyze_WithEmptyDirectoryAndMissingBinary_ReturnsEmptyReport()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "empty_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            string fakeBin = Path.Combine(tmpDir, "does_not_exist.dll");
            var report = ProtectorDetector.Analyze(tmpDir, fakeBin);

            Assert.NotNull(report);
            Assert.False(report.Detected);
            Assert.Empty(report.Matches);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void Analyze_NestedSubdirectories_ScansWithoutUnauthorizedAccessException()
    {
        string rootDir = Path.Combine(Path.GetTempPath(), "nested_prot_" + Guid.NewGuid().ToString("N"));
        string subDir1 = Path.Combine(rootDir, "SubFolderA");
        string subDir2 = Path.Combine(subDir1, "SubFolderB");

        Directory.CreateDirectory(subDir2);

        try
        {
            // Place detector file in deeply nested folder
            File.WriteAllBytes(Path.Combine(subDir2, "0Oo0oo0o0.dat"), [0x01, 0x02, 0x03]);
            string mockBin = Path.Combine(rootDir, "libil2cpp.so");
            File.WriteAllBytes(mockBin, new byte[64]);

            var report = ProtectorDetector.Analyze(rootDir, mockBin);
            Assert.True(report.Detected);
            Assert.Contains(report.Matches, m => m.Vendor == "Tencent" && m.Name.Contains("Legu"));
        }
        finally
        {
            try { Directory.Delete(rootDir, true); } catch { }
        }
    }
}
