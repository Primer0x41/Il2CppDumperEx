using System;
using System.Buffers.Binary;
using System.IO;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class MachoFatTests
{
    [Fact]
    public void MachoFat_ParsesSlicesCorrectly()
    {
        // Build mock Fat Mach-O with 2 slices (one 32-bit slice, one 64-bit slice)
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        // Fat header
        bw.Write(BinaryPrimitives.ReverseEndianness(0xCAFEBABEu)); // magic
        bw.Write(BinaryPrimitives.ReverseEndianness(2u));          // nfat_arch = 2

        uint slice1Offset = 0x1000;
        uint slice1Size = 0x200;
        uint slice2Offset = 0x2000;
        uint slice2Size = 0x400;

        // Arch 1: 32-bit
        bw.Write(BinaryPrimitives.ReverseEndianness(7u)); // cputype = x86
        bw.Write(BinaryPrimitives.ReverseEndianness(3u)); // cpusubtype
        bw.Write(BinaryPrimitives.ReverseEndianness(slice1Offset));
        bw.Write(BinaryPrimitives.ReverseEndianness(slice1Size));
        bw.Write(BinaryPrimitives.ReverseEndianness(12u)); // align

        // Arch 2: 64-bit
        bw.Write(BinaryPrimitives.ReverseEndianness(0x0100000Cu)); // cputype = ARM64
        bw.Write(BinaryPrimitives.ReverseEndianness(0u));          // cpusubtype
        bw.Write(BinaryPrimitives.ReverseEndianness(slice2Offset));
        bw.Write(BinaryPrimitives.ReverseEndianness(slice2Size));
        bw.Write(BinaryPrimitives.ReverseEndianness(14u)); // align

        // Slice 1 body at 0x1000
        ms.Position = slice1Offset;
        bw.Write(0xFEEDFACEu); // 32-bit Mach-O magic
        for (int i = 0; i < slice1Size - 4; i++) bw.Write((byte)0xAA);

        // Slice 2 body at 0x2000
        ms.Position = slice2Offset;
        bw.Write(0xFEEDFACFu); // 64-bit Mach-O magic
        for (int i = 0; i < slice2Size - 4; i++) bw.Write((byte)0xBB);

        ms.Position = 0;
        var fat = new MachoFat(ms);

        Assert.Equal(2, fat.fats.Length);
        Assert.Equal(0xFEEDFACEu, fat.fats[0].magic);
        Assert.Equal(0xFEEDFACFu, fat.fats[1].magic);

        byte[] slice2Bytes = fat.GetMacho(1);
        Assert.Equal((int)slice2Size, slice2Bytes.Length);
        Assert.Equal(0xFEEDFACFu, BitConverter.ToUInt32(slice2Bytes, 0));
    }

    [Fact]
    public void MachoFat_ParsesLittleEndianFatMachOCorrectly()
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        // FAT_CIGAM (little-endian fat header): bytes BE BA FE CA
        bw.Write(new byte[] { 0xBE, 0xBA, 0xFE, 0xCA });
        // nfat_arch = 1 (little-endian)
        bw.Write(1u);

        uint sliceOffset = 0x1000;
        uint sliceSize = 0x200;

        // Arch 1: 64-bit ARM64 (little-endian fields)
        bw.Write(0x0100000Cu); // cputype
        bw.Write(0u);          // cpusubtype
        bw.Write(sliceOffset); // offset (little-endian)
        bw.Write(sliceSize);   // size (little-endian)
        bw.Write(14u);         // align

        // Slice body at 0x1000
        ms.Position = sliceOffset;
        bw.Write(0xFEEDFACFu); // 64-bit Mach-O magic
        for (int i = 0; i < sliceSize - 4; i++) bw.Write((byte)0xCC);

        ms.Position = 0;
        var fat = new MachoFat(ms);

        Assert.Single(fat.fats);
        Assert.Equal(sliceOffset, fat.fats[0].offset);
        Assert.Equal(sliceSize, fat.fats[0].size);
        Assert.Equal(0xFEEDFACFu, fat.fats[0].magic);

        byte[] sliceBytes = fat.GetMacho(0);
        Assert.Equal((int)sliceSize, sliceBytes.Length);
        Assert.Equal(0xFEEDFACFu, BitConverter.ToUInt32(sliceBytes, 0));
    }
}
