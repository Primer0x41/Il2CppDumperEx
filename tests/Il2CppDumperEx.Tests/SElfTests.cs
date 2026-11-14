using System;
using System.IO;
using Xunit;
using Il2CppDumper;

namespace Il2CppDumperEx.Tests
{
    public class SElfTests
    {
        [Fact]
        public void DetectsSElfMagicCorrectly()
        {
            byte[] buffer = new byte[32];
            BitConverter.GetBytes(SElf.SELF_MAGIC).CopyTo(buffer, 0);

            Assert.True(SElf.IsSElf(buffer));

            byte[] nonSelf = new byte[32];
            Assert.False(SElf.IsSElf(nonSelf));
        }

        [Fact]
        public void UnwrapsFakeSelfContainer()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // SElf Header (32 bytes)
            writer.Write(SElf.SELF_MAGIC); // Magic
            writer.Write((byte)1);          // Version
            writer.Write((byte)1);          // Mode
            writer.Write((byte)1);          // Endian (Little)
            writer.Write((byte)0);          // Attributes
            writer.Write((uint)0);         // KeyType
            writer.Write((ushort)32);       // HeaderSize
            writer.Write((ushort)0);        // MetadataSize
            writer.Write((ulong)256);       // FileSize
            writer.Write((ushort)1);        // NumberOfEntries
            writer.Write((ushort)0);        // Flags
            writer.Write((uint)0);          // Padding

            // SElf Entry 0 (32 bytes)
            // SegmentIndex = 0 (bits 20-35), HasBlocks = 0x800
            ulong flags = 0x800 | (0UL << 20);
            ulong fileOffset = 256;
            ulong fileSize = 64;
            ulong memSize = 64;

            writer.Write(flags);
            writer.Write(fileOffset);
            writer.Write(fileSize);
            writer.Write(memSize);

            // Embedded ELF Header (64 bytes)
            byte[] elfIdent = new byte[16] { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0 }; // 64-bit ELF
            writer.Write(elfIdent);
            writer.Write((ushort)0xFE10); // e_type (ET_SCE_DYNEXEC)
            writer.Write((ushort)0x3E);   // e_machine (x86_64)
            writer.Write((uint)1);        // e_version
            writer.Write((ulong)0x1000);  // e_entry
            writer.Write((ulong)64);      // e_phoff (immediately follows 64-byte elf header)
            writer.Write((ulong)0);       // e_shoff
            writer.Write((uint)0);        // e_flags
            writer.Write((ushort)64);     // e_ehsize
            writer.Write((ushort)56);     // e_phentsize
            writer.Write((ushort)1);      // e_phnum
            writer.Write((ushort)0);      // e_shentsize
            writer.Write((ushort)0);      // e_shnum
            writer.Write((ushort)0);      // e_shstrndx

            // Program Header 0 (56 bytes)
            writer.Write((uint)1);        // p_type (PT_LOAD)
            writer.Write((uint)5);        // p_flags (r-x)
            writer.Write((ulong)0);       // p_offset (placeholder)
            writer.Write((ulong)0x1000);  // p_vaddr
            writer.Write((ulong)0x1000);  // p_paddr
            writer.Write((ulong)0);       // p_filesz (placeholder)
            writer.Write((ulong)0);       // p_memsz (placeholder)
            writer.Write((ulong)0x1000);  // p_align

            // Pad up to fileOffset 160
            while (ms.Position < (long)fileOffset)
            {
                writer.Write((byte)0);
            }

            // Segment payload data
            byte[] segmentData = new byte[64];
            for (int i = 0; i < segmentData.Length; i++) segmentData[i] = (byte)(i + 1);
            writer.Write(segmentData);

            ms.Flush();
            ms.Position = 0;

            using var elfStream = SElf.UnwrapToElf(ms);
            Assert.NotNull(elfStream);
            Assert.True((ulong)elfStream.Length >= fileOffset + fileSize);

            byte[] elfBytes = elfStream.ToArray();
            Assert.Equal(0x7F, elfBytes[0]);
            Assert.Equal((byte)'E', elfBytes[1]);
            Assert.Equal((byte)'L', elfBytes[2]);
            Assert.Equal((byte)'F', elfBytes[3]);

            ushort normalizedType = BitConverter.ToUInt16(elfBytes, 16);
            Assert.Equal(3, normalizedType);

            for (int i = 0; i < 64; i++)
            {
                Assert.Equal((byte)(i + 1), elfBytes[(long)fileOffset + i]);
            }
        }
    }
}
