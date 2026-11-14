using System;
using System.Buffers.Binary;

namespace Il2CppDumper
{
    public static class ArmUtils
    {
        public static uint DecodeMov(byte[] asm)
        {
            var low = (ushort)(asm[2] + ((asm[3] & 0x70) << 4) + ((asm[1] & 0x04) << 9) + ((asm[0] & 0x0f) << 12));
            var high = (ushort)(asm[6] + ((asm[7] & 0x70) << 4) + ((asm[5] & 0x04) << 9) + ((asm[4] & 0x0f) << 12));
            return (uint)((high << 16) + low);
        }

        public static ulong DecodeAdr(ulong pc, byte[] inst)
        {
            uint ins = BinaryPrimitives.ReadUInt32LittleEndian(inst);
            long immlo = (ins >> 29) & 0x3;
            long immhi = (ins >> 5) & 0x7FFFF;
            long imm = (immhi << 2) | immlo;
            if ((imm & 0x100000) != 0)
                imm |= ~0x1FFFFFL;
            return pc + (ulong)imm;
        }

        public static ulong DecodeAdrp(ulong pc, byte[] inst)
        {
            pc &= 0xFFFFFFFFFFFFF000UL;
            uint ins = BinaryPrimitives.ReadUInt32LittleEndian(inst);
            long immlo = (ins >> 29) & 0x3;
            long immhi = (ins >> 5) & 0x7FFFF;
            long imm = (immhi << 2) | immlo;
            if ((imm & 0x100000) != 0)
                imm |= ~0x1FFFFFL;
            imm <<= 12;
            return pc + (ulong)imm;
        }

        public static ulong DecodeAdd(byte[] inst)
        {
            uint ins = BinaryPrimitives.ReadUInt32LittleEndian(inst);
            ulong imm12 = (ins >> 10) & 0xFFFUL;
            if ((ins & 0x00400000u) != 0)
                imm12 <<= 12;
            return imm12;
        }

        public static bool IsAdr(byte[] inst)
        {
            uint ins = BinaryPrimitives.ReadUInt32LittleEndian(inst);
            return (ins & 0x9F000000u) == 0x10000000u;
        }
    }
}
