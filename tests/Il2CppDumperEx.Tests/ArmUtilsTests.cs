using System;
using System.Buffers.Binary;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class ArmUtilsTests
{
    private static byte[] MakeInstruction(uint ins)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, ins);
        return bytes;
    }

    [Fact]
    public void IsAdr_IdentifiesAdrInstructionsCorrectly()
    {
        // ADR: bit 31 is 0, bits [28:24] are 10000 (0x10)
        byte[] adrZero = MakeInstruction(0x10000000u);
        byte[] adrWithOffset = MakeInstruction(0x10000020u);
        byte[] adrNeg = MakeInstruction(0x10FFFFE0u);

        // ADRP: bit 31 is 1
        byte[] adrp = MakeInstruction(0x90000000u);
        // ADD
        byte[] add = MakeInstruction(0x91004000u);

        Assert.True(ArmUtils.IsAdr(adrZero));
        Assert.True(ArmUtils.IsAdr(adrWithOffset));
        Assert.True(ArmUtils.IsAdr(adrNeg));

        Assert.False(ArmUtils.IsAdr(adrp));
        Assert.False(ArmUtils.IsAdr(add));
        Assert.False(ArmUtils.IsAdr(new byte[] { 0, 0, 0, 0 }));
    }

    [Theory]
    [InlineData(0x1000UL, 0, 0x1000UL)]             // offset = 0
    [InlineData(0x1000UL, 4, 0x1004UL)]             // offset = +4
    [InlineData(0x2000UL, 100, 0x2064UL)]           // offset = +100
    [InlineData(0x2000UL, -4, 0x1FFCUL)]            // offset = -4
    [InlineData(0x10000UL, -0x1000, 0xF000UL)]      // offset = -4096
    public void DecodeAdr_DecodesOffsetsAccurately(ulong pc, int offset, ulong expected)
    {
        // Encode ADR immediate (21-bit signed)
        long imm21 = offset & 0x1FFFFF;
        uint immlo = (uint)(imm21 & 0x3);
        uint immhi = (uint)((imm21 >> 2) & 0x7FFFF);

        uint ins = 0x10000000u | (immlo << 29) | (immhi << 5);
        byte[] inst = MakeInstruction(ins);

        ulong result = ArmUtils.DecodeAdr(pc, inst);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0x5000UL, 1, 0x6000UL)]             // +1 page (4096)
    [InlineData(0x5123UL, 1, 0x6000UL)]             // unaligned PC is masked to page boundary
    [InlineData(0x5000UL, 0, 0x5000UL)]             // +0 pages
    [InlineData(0x5000UL, -1, 0x4000UL)]            // -1 page
    [InlineData(0x10000UL, -2, 0xE000UL)]           // -2 pages
    public void DecodeAdrp_DecodesPageOffsetsAccurately(ulong pc, int pages, ulong expected)
    {
        // Encode ADRP immediate (21-bit signed page count)
        long imm21 = pages & 0x1FFFFF;
        uint immlo = (uint)(imm21 & 0x3);
        uint immhi = (uint)((imm21 >> 2) & 0x7FFFF);

        // ADRP: bit 31 is 1
        uint ins = 0x90000000u | (immlo << 29) | (immhi << 5);
        byte[] inst = MakeInstruction(ins);

        ulong result = ArmUtils.DecodeAdrp(pc, inst);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecodeAdd_DecodesImmediateAndShift()
    {
        // imm12 = 16, shift = 0
        uint insNoShift = 0x91000000u | (16u << 10);
        byte[] inst1 = MakeInstruction(insNoShift);
        Assert.Equal(16UL, ArmUtils.DecodeAdd(inst1));

        // imm12 = 16, shift = 12 (bit 22 = 1)
        uint insWithShift = 0x91000000u | (1u << 22) | (16u << 10);
        byte[] inst2 = MakeInstruction(insWithShift);
        Assert.Equal(16UL << 12, ArmUtils.DecodeAdd(inst2));
    }
}
