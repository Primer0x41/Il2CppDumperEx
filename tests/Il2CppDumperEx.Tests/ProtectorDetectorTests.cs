using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Il2CppDumperEx.Core;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class ProtectorDetectorTests
{
    [Fact]
    public void CalculateShannonEntropy_ZeroAndMaxEntropy_BehavesDeterministically()
    {
        byte[] uniform = new byte[1024];
        Array.Fill(uniform, (byte)0x42);
        double uniformEntropy = ProtectorDetector.CalculateShannonEntropy(uniform);
        Assert.Equal(0.0, uniformEntropy, precision: 3);

        byte[] allBytes = new byte[256 * 10];
        for (int i = 0; i < allBytes.Length; i++)
        {
            allBytes[i] = (byte)(i % 256);
        }
        double maxEntropy = ProtectorDetector.CalculateShannonEntropy(allBytes);
        Assert.Equal(8.0, maxEntropy, precision: 3);

        byte[] randomBytes = new byte[4096];
        RandomNumberGenerator.Fill(randomBytes);
        double randEntropy = ProtectorDetector.CalculateShannonEntropy(randomBytes);
        Assert.True(randEntropy > 7.7, $"Expected high entropy but got {randEntropy}");
    }

    [Fact]
    public void ProtectorDetector_IdentifiesTencentAndSecNeo_FromFiles()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "md_prot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllBytes(Path.Combine(tmpDir, "libtersafe.so"), [0x7F, 0x45, 0x4C, 0x46]);
            File.WriteAllBytes(Path.Combine(tmpDir, "libsecmain.so"), [0x7F, 0x45, 0x4C, 0x46]);

            string mockBin = Path.Combine(tmpDir, "libil2cpp.so");
            File.WriteAllBytes(mockBin, Encoding.ASCII.GetBytes("This is normal compiled binary code without obfuscation."));

            var report = ProtectorDetector.Analyze(tmpDir, mockBin);

            Assert.True(report.Detected);
            Assert.Contains(report.Matches, m => m.Name.Contains("ACE") || m.Vendor == "Tencent");
            Assert.Contains(report.Matches, m => m.Name.Contains("SecNeo") || m.Vendor == "Bangcle");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void ProtectorDetector_IdentifiesVmProtectSection()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "md_vmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            string mockBin = Path.Combine(tmpDir, "GameAssembly.dll");
            byte[] header = Encoding.Latin1.GetBytes("MZ\x90\x00...PE\x00\x00...SectionHeaders... .vmp0 ... .vmp1 ...");
            File.WriteAllBytes(mockBin, header);

            var report = ProtectorDetector.Analyze(tmpDir, mockBin);

            Assert.True(report.Detected);
            Assert.Contains(report.Matches, m => m.Name == "VMProtect");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }
}
