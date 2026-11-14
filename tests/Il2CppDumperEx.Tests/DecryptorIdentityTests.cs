using System.Security.Cryptography;
using Il2CppDumperEx.Core;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class DecryptorIdentityTests
{
    private static byte[] CreateMockNep2File(int size, byte seedByte = 42)
    {
        if (size < 136)
            throw new ArgumentException("Mock NEP2 file must be >= 136 bytes", nameof(size));

        byte[] buffer = new byte[size];
        buffer[0] = 0xAF;
        buffer[1] = 0x1B;
        buffer[2] = 0xB1;
        buffer[3] = 0xFA;

        buffer[4] = 0x00;
        buffer[5] = 0x00;
        buffer[6] = 0x01;
        buffer[7] = 0x00;

        for (int i = 0; i < size - 8; i++)
        {
            buffer[8 + i] = (byte)((i * 31 + seedByte) & 0xFF);
        }

        return buffer;
    }

    private static string ComputeSha256(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data));
    }

    // Golden vectors Covering 0-block, 1-block, 8-block, 9-block (1152-byte cycle boundary), 10-block, 18-block, 25-block,
    // and various trailing-byte scenarios (0, 1, 15, 33, 50, 77, 99, 127).
    public static IEnumerable<object[]> GetGoldenVectorCases()
    {
        yield return [0, 0, "99FCF3153D8C5A0C02BB0B011682717B5AA10D312DEBED5C0A96482549B699F0"];
        yield return [0, 1, "5658CA25039DDE78D8C89D72B905A71748BB68D9A9DFC80CE91CC7B590896883"];
        yield return [0, 50, "4F16F3E27CB843F3FD336B91AEBDF93D49E53EA8EE103223A719582CA524905A"];
        yield return [0, 127, "BD5D0EB6D1EEEAC4FB07C8C8D641FEA1C262EAD93426379D25FA512A1EE7B925"];
        yield return [1, 0, "E206A3FBAB49D27AFC171642219B763E6C4AD80036AEBAC5494FFFB53C25088F"];
        yield return [1, 1, "40DC4002B9917CDFB10315290CADD6FA06F4421D94BED332145D9AE280CF469A"];
        yield return [1, 77, "140936F4D3BA34F97B1C4802443E6A2526C8E89BC92E77D8F1389AEED2888963"];
        yield return [8, 0, "D84D579BB456FEA1DD3DB5512DCB968BAE60E69B6F5D3F51BF1CC3CB1B73F434"];
        yield return [8, 33, "5CFA5327BCE9D367046A9DAD1DE74ABA62F84B33F894F5B650692766F05FB278"];
        yield return [9, 0, "37CF7AA68FD0F347ECE407CD289C1F257718CD31DF6B00772770E5243C70F802"];
        yield return [9, 15, "8E7552C28631EF5A82DA13EAE7F418A5515BC92E5B6C204C2D6D26184F5A858A"];
        yield return [10, 0, "78E43AEFE0ED717AEBBC2E87354459AC3A5227066B6B463DD06A138199F1BDEA"];
        yield return [10, 99, "9780F0BC224892D6C20551FB2C556B7AF6DF8346F1000C5893A3A2B8E1D258CE"];
        yield return [18, 0, "01D9C6D985F558CF2133CF9020798DE0AF4F128665D33A5603C5274D165C70DC"];
        yield return [25, 4, "D927D9796E68A3A3DBF275AED5B6722D2B209B8C3C7611503E4381B26A862371"];
    }

    [Theory]
    [MemberData(nameof(GetGoldenVectorCases))]
    public void DecryptBytes_MatchesGoldenReferenceVectors(int blockCount, int trailing, string expectedSha256)
    {
        int totalSize = 136 + blockCount * 128 + trailing;
        byte[] input = CreateMockNep2File(totalSize, seedByte: (byte)(blockCount + trailing));

        byte[] actual = Nep2Decryptor.DecryptBytes(input, outputVersion: 31);

        Assert.Equal(totalSize, actual.Length);
        string actualHash = ComputeSha256(actual);
        Assert.Equal(expectedSha256, actualHash);
    }

    [Fact]
    public void DecryptFile_Streaming_MatchesGoldenReference()
    {
        // 25 blocks + 43 trailing = 136 + 25*128 + 43 = 3379 bytes
        const int totalSize = 3379;
        const string expectedSha256 = "6A8CEFE0204B6F040DDBA993BF1CF891E7321A14CCD64F84E186E4A46F93B3B2";
        byte[] input = CreateMockNep2File(totalSize, seedByte: 123);

        string tempDir = Path.Combine(Path.GetTempPath(), "il2cppdumper_stream_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string inPath = Path.Combine(tempDir, "input.dat");
        string outPath = Path.Combine(tempDir, "output.dat");

        try
        {
            File.WriteAllBytes(inPath, input);
            Nep2Decryptor.DecryptFile(inPath, outPath, outputVersion: 31, chunkSize: 500);

            byte[] actual = File.ReadAllBytes(outPath);
            Assert.Equal(totalSize, actual.Length);
            Assert.Equal(expectedSha256, ComputeSha256(actual));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void DecryptBytes_LargePayload_MatchesGoldenReference()
    {
        // 5 MB payload (approx 40,960 blocks)
        const int totalSize = 5 * 1024 * 1024 + 136 + 42;
        const string expectedSha256 = "E0721604557068024E14B97B5A4BC9C394D2ACD3E5A6F5499B90F34CEE067BAE";
        byte[] input = CreateMockNep2File(totalSize, seedByte: 77);

        byte[] actual = Nep2Decryptor.DecryptBytes(input, outputVersion: 31);

        Assert.Equal(totalSize, actual.Length);
        Assert.Equal(expectedSha256, ComputeSha256(actual));
    }

    [Fact]
    public void DecryptBytes_VersionHeaderCustomization()
    {
        // Test custom output version 29 vs default 31
        byte[] input = CreateMockNep2File(264, seedByte: 1);

        byte[] actual31 = Nep2Decryptor.DecryptBytes(input, outputVersion: 31);
        Assert.Equal(31, BitConverter.ToInt32(actual31, 4));

        byte[] actual29 = Nep2Decryptor.DecryptBytes(input, outputVersion: 29);
        Assert.Equal(29, BitConverter.ToInt32(actual29, 4));
    }

    [Fact]
    public void DecryptBytes_ThrowsMetadataTooSmall_WhenUnder136Bytes()
    {
        byte[] small = new byte[135];
        small[0] = 0xAF; small[1] = 0x1B; small[2] = 0xB1; small[3] = 0xFA;
        Assert.Throws<MetadataTooSmallException>(() => Nep2Decryptor.DecryptBytes(small));
    }

    [Fact]
    public void DecryptBytes_ThrowsInvalidMagic_WhenMagicMismatches()
    {
        byte[] badMagic = new byte[200];
        badMagic[0] = 0x00; badMagic[1] = 0x00; badMagic[2] = 0x00; badMagic[3] = 0x00;
        Assert.Throws<InvalidMagicException>(() => Nep2Decryptor.DecryptBytes(badMagic));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(104)]
    [InlineData(110)]
    public void DecryptBytes_ThrowsAlreadyDecrypted_WhenHeaderVersionIsValidMetadataVersion(int version)
    {
        byte[] decrypted = new byte[200];
        decrypted[0] = 0xAF; decrypted[1] = 0x1B; decrypted[2] = 0xB1; decrypted[3] = 0xFA;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(decrypted.AsSpan(4, 4), version);
        Assert.Throws<AlreadyDecryptedException>(() => Nep2Decryptor.DecryptBytes(decrypted));
    }
}
