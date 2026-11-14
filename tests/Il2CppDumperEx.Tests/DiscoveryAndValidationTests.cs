using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Il2CppDumperEx.Core;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class DiscoveryAndValidationTests
{
    internal static byte[] BuildValidStringHeap(int size = 1024)
    {
        string[] words =
        [
            "System", "String", "Int32", "Boolean", "UnityEngine",
            "GameObject", "Transform", "MonoBehaviour", "Component",
            "Vector3", "Quaternion", "Camera", "AudioSource", "Collider"
        ];

        var ms = new MemoryStream();
        foreach (var w in words)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(w + "\0");
            ms.Write(bytes, 0, bytes.Length);
        }

        byte[] pad = Encoding.ASCII.GetBytes("field_name_identifier\0");
        while (ms.Length < size)
        {
            ms.Write(pad, 0, pad.Length);
        }

        byte[] result = new byte[size];
        ms.Position = 0;
        ms.Read(result, 0, size);
        return result;
    }

    internal static byte[] BuildMockMetadataFile(int version, int totalSize = 4096)
    {
        byte[] buf = new byte[totalSize];
        Span<byte> span = buf.AsSpan();

        // Magic + Version
        Nep2Decryptor.MagicBytes.CopyTo(span.Slice(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), version);

        int stride = version >= 38 ? 12 : 8;
        int numEntries = MetadataValidator.GetHeaderEntryCount(version);

        int strOffset = 1024;
        int strSize = 512;
        byte[] stringHeap = BuildValidStringHeap(strSize);
        Buffer.BlockCopy(stringHeap, 0, buf, strOffset, strSize);

        int activeEntries = Math.Max(24, (int)(numEntries * 0.75));
        for (int i = 0; i < numEntries; i++)
        {
            int pos = 8 + i * stride;
            uint off;
            int sz;

            if (i == 2 && version < 38)
            {
                off = (uint)strOffset;
                sz = strSize;
            }
            else if (i < activeEntries)
            {
                off = (uint)(1600 + i * 32);
                sz = 32;
            }
            else
            {
                off = 0;
                sz = 0;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(pos, 4), off);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos + 4, 4), sz);
            if (stride == 12)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(pos + 8, 4), 0);
            }
        }

        return buf;
    }

    [Fact]
    public void IsEncryptedMetadata_DetectsCorrectly()
    {
        byte[] unencrypted = new byte[100];
        Nep2Decryptor.MagicBytes.CopyTo(unencrypted.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(unencrypted.AsSpan(4, 4), 29);
        Assert.False(MetadataValidator.IsEncryptedMetadata(unencrypted));

        byte[] unencrypted39 = new byte[100];
        Nep2Decryptor.MagicBytes.CopyTo(unencrypted39.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(unencrypted39.AsSpan(4, 4), 39);
        Assert.False(MetadataValidator.IsEncryptedMetadata(unencrypted39));

        byte[] unencrypted110 = new byte[100];
        Nep2Decryptor.MagicBytes.CopyTo(unencrypted110.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(unencrypted110.AsSpan(4, 4), 110);
        Assert.False(MetadataValidator.IsEncryptedMetadata(unencrypted110));

        byte[] encryptedNep2 = new byte[100];
        Nep2Decryptor.MagicBytes.CopyTo(encryptedNep2.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(encryptedNep2.AsSpan(4, 4), 65536);
        Assert.True(MetadataValidator.IsEncryptedMetadata(encryptedNep2));

        byte[] badMagic = new byte[100];
        Assert.True(MetadataValidator.IsEncryptedMetadata(badMagic));
    }

    [Fact]
    public void GetCandidateVersions_MapsCorrectly()
    {
        Assert.Equal(110, MetadataValidator.GetCandidateVersions("6000.0.1f1")[0]);
        Assert.Equal(31, MetadataValidator.GetCandidateVersions("2022.3.8f1")[0]);
        Assert.Equal(29, MetadataValidator.GetCandidateVersions("2021.3.16f1")[0]);
        Assert.Equal(27, MetadataValidator.GetCandidateVersions("2020.3.1f1")[0]);
        Assert.Equal(24, MetadataValidator.GetCandidateVersions("2019.4.20f1")[0]);

        var all = MetadataValidator.GetCandidateVersions(null);
        foreach (var v in all)
        {
            Assert.True(MetadataValidator.IsValidMetadataVersion(v), $"Version {v} should be recognized as valid.");
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(27)]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(35)]
    [InlineData(38)]
    [InlineData(39)]
    [InlineData(104)]
    [InlineData(105)]
    [InlineData(106)]
    [InlineData(108)]
    [InlineData(110)]
    public void ValidateMetadata_Passes_AcrossVersions(int version)
    {
        byte[] mockMeta = BuildMockMetadataFile(version);
        var (valid, reason) = MetadataValidator.ValidateMetadata(mockMeta, version);
        Assert.True(valid, $"Validation failed for v{version}: {reason}");
    }

    [Fact]
    public void GameDiscovery_FindsMacOsFrameworkAndPlayStationBinaries()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "md_cross_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            string frameworkDir = Path.Combine(tmpDir, "Frameworks", "UnityFramework.framework");
            Directory.CreateDirectory(frameworkDir);
            File.WriteAllBytes(Path.Combine(frameworkDir, "UnityFramework"), [0xCF, 0xFA, 0xED, 0xFE]);

            string metaDir = Path.Combine(tmpDir, "Data", "il2cpp_data", "Metadata");
            Directory.CreateDirectory(metaDir);
            byte[] mockMeta = BuildMockMetadataFile(31, 2048);
            File.WriteAllBytes(Path.Combine(metaDir, "global-metadata.dec"), mockMeta);

            var discovered = GameDiscovery.DiscoverGame(tmpDir);

            Assert.True(discovered.IsIl2Cpp);
            Assert.EndsWith("UnityFramework", discovered.BinaryPath);
            Assert.EndsWith("global-metadata.dec", discovered.MetadataPath);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void GameDiscovery_ExtractsAndDiscoversApkArchive()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "md_apk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            string apkPath = Path.Combine(tmpDir, "TestGame.apk");
            using (var zip = ZipFile.Open(apkPath, ZipArchiveMode.Create))
            {
                var binEntry = zip.CreateEntry("lib/arm64-v8a/libil2cpp.so");
                using (var s = binEntry.Open())
                {
                    s.Write([0x7F, 0x45, 0x4C, 0x46]); // ELF magic
                }

                var metaEntry = zip.CreateEntry("assets/bin/Data/Managed/Metadata/global-metadata.dat");
                using (var s = metaEntry.Open())
                {
                    byte[] mockMeta = BuildMockMetadataFile(31, 2048);
                    s.Write(mockMeta);
                }
            }

            var discovered = GameDiscovery.DiscoverGame(apkPath);

            Assert.True(discovered.IsIl2Cpp);
            Assert.EndsWith("libil2cpp.so", discovered.BinaryPath);
            Assert.EndsWith("global-metadata.dat", discovered.MetadataPath);
            Assert.Equal("TestGame", discovered.GameName);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void GameDiscovery_DiscoversValidGameStructure()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "md_disco_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmpDir);
            string exePath = Path.Combine(tmpDir, "SampleGame.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]); // MZ

            string binPath = Path.Combine(tmpDir, "GameAssembly.dll");
            File.WriteAllBytes(binPath, [0x4D, 0x5A]);

            string dataDir = Path.Combine(tmpDir, "SampleGame_Data");
            string metaDir = Path.Combine(dataDir, "il2cpp_data", "Metadata");
            Directory.CreateDirectory(metaDir);

            string metaPath = Path.Combine(metaDir, "global-metadata.dat");
            File.WriteAllBytes(metaPath, BuildMockMetadataFile(29));

            File.WriteAllText(Path.Combine(dataDir, "app.info"), "2021.3.16f1");

            var game1 = GameDiscovery.DiscoverGame(exePath);
            Assert.Equal("SampleGame", game1.GameName);
            Assert.Equal(binPath, game1.BinaryPath);
            Assert.Equal(metaPath, game1.MetadataPath);
            Assert.Equal("2021.3.16f1", game1.UnityVersion);

            var game2 = GameDiscovery.DiscoverGame(tmpDir);
            Assert.Equal(binPath, game2.BinaryPath);

            var game3 = GameDiscovery.DiscoverGame(dataDir);
            Assert.Equal("SampleGame", game3.GameName);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void GameDiscovery_DetectsMonoGame_ThrowsNotIl2Cpp()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "md_mono_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmpDir);
            string dataDir = Path.Combine(tmpDir, "MonoGame_Data");
            string managedDir = Path.Combine(dataDir, "Managed");
            Directory.CreateDirectory(managedDir);
            File.WriteAllBytes(Path.Combine(managedDir, "Assembly-CSharp.dll"), [0x4D, 0x5A]);

            Assert.Throws<NotIl2CppGameException>(() => GameDiscovery.DiscoverGame(tmpDir));
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }
}
