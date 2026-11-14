using System;
using System.IO;
using System.Text;
using Il2CppDumper;
using Xunit;
using ZstdSharp;

namespace Il2CppDumperEx.Tests;

public class NsoDecompressionTests
{
    private static byte[] BuildSyntheticNso(
        uint flags,
        byte[] textPayload,
        int decompressedTextSize,
        byte[] roPayload,
        int decompressedRoSize,
        byte[] dataPayload,
        int decompressedDataSize)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        bw.Write(0x304F534Eu); // Magic 'NSO0'
        bw.Write(0u);         // Version
        bw.Write(0u);         // Reserved
        bw.Write(flags);      // Flags

        uint textOffset = 0x100;
        bw.Write(textOffset);                  // TextSegment.FileOffset
        bw.Write(0u);                          // TextSegment.MemoryOffset
        bw.Write((uint)decompressedTextSize);  // TextSegment.DecompressedSize
        bw.Write(0u);                          // ModuleOffset

        uint roOffset = textOffset + (uint)textPayload.Length;
        bw.Write(roOffset);                    // RoDataSegment.FileOffset
        bw.Write(0u);                          // RoDataSegment.MemoryOffset
        bw.Write((uint)decompressedRoSize);    // RoDataSegment.DecompressedSize

        uint dataOffset = roOffset + (uint)roPayload.Length;
        uint totalSize = dataOffset + (uint)dataPayload.Length;
        bw.Write(totalSize);                   // ModuleFileSize

        bw.Write(dataOffset);                  // DataSegment.FileOffset
        bw.Write(0u);                          // DataSegment.MemoryOffset
        bw.Write((uint)decompressedDataSize);  // DataSegment.DecompressedSize

        bw.Write(0u);                          // BssSize
        bw.Write(new byte[0x20]);              // DigestBuildID

        bw.Write((uint)textPayload.Length);    // TextCompressedSize
        bw.Write((uint)roPayload.Length);      // RoDataCompressedSize
        bw.Write((uint)dataPayload.Length);    // DataCompressedSize

        bw.Write(new byte[0x1C]);              // Padding
        bw.Write(0u); bw.Write(0u);            // APIInfo
        bw.Write(0u); bw.Write(0u);            // DynStr
        bw.Write(0u); bw.Write(0u);            // DynSym

        bw.Write(new byte[0x20]);              // TextHash
        bw.Write(new byte[0x20]);              // RoDataHash
        bw.Write(new byte[0x20]);              // DataHash

        // Segment payloads
        bw.Write(textPayload);
        bw.Write(roPayload);
        bw.Write(dataPayload);

        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Nso_WhenAlreadyUncompressed_ReturnsSameInstance()
    {
        byte[] text = new byte[64];
        byte[] ro = new byte[64];
        byte[] data = new byte[64];

        byte[] nsoBytes = BuildSyntheticNso(0, text, text.Length, ro, ro.Length, data, data.Length);
        using var stream = new MemoryStream(nsoBytes);
        var nso = new NSO(stream);

        var result = nso.UnCompress();
        Assert.Same(nso, result);
    }

    [Fact]
    public void Nso_ZstandardDecompression_DecompressesCorrectly()
    {
        byte[] payloadData = Encoding.UTF8.GetBytes("NintendoSwitch_Zstandard_Compressed_Text_Segment_Payload_1234567890!");
        byte[] rawText = new byte[32 + payloadData.Length];
        payloadData.CopyTo(rawText.AsSpan(32));

        using var compressor = new Compressor(1);
        byte[] compressedText = compressor.Wrap(rawText).ToArray();

        byte[] ro = new byte[32];
        byte[] data = new byte[32];

        // Flags: 1 (text compressed) | 0x80 (ZBIC compression)
        uint flags = 1u | 0x80u;
        byte[] nsoBytes = BuildSyntheticNso(flags, compressedText, rawText.Length, ro, ro.Length, data, data.Length);

        using var stream = new MemoryStream(nsoBytes);
        var nso = new NSO(stream);
        var decompressedNso = nso.UnCompress();

        Assert.NotNull(decompressedNso);
        decompressedNso.Position = 0x100;
        byte[] readBack = decompressedNso.ReadBytes(rawText.Length);
        Assert.Equal(rawText, readBack);
    }

    [Fact]
    public void Nso_ZstandardDecompression_SizeMismatch_ThrowsInvalidDataException()
    {
        byte[] rawText = Encoding.UTF8.GetBytes("TestPayloadData");
        using var compressor = new Compressor(1);
        byte[] compressedText = compressor.Wrap(rawText).ToArray();

        byte[] ro = new byte[16];
        byte[] data = new byte[16];

        // Specify wrong decompressed size (+100 bytes)
        uint flags = 1u | 0x80u;
        byte[] nsoBytes = BuildSyntheticNso(flags, compressedText, rawText.Length + 100, ro, ro.Length, data, data.Length);

        using var stream = new MemoryStream(nsoBytes);
        var nso = new NSO(stream);

        Assert.Throws<InvalidDataException>(() => nso.UnCompress());
    }
}
