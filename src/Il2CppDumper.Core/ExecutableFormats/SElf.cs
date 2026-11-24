using System;
using System.IO;
using System.Linq;

namespace Il2CppDumper
{
    public class SElf
    {
        public const uint SELF_MAGIC = 0x1D3D154F; // Little endian: 0x4F, 0x15, 0x3D, 0x1D
        public const uint SELF_PRX_MAGIC = 0xEEF51454; // PS4 / PS5 PRX Signed Executable (0x54, 0x14, 0xF5, 0xEE)

        public struct SElfHeader
        {
            public uint Magic;
            public byte Version;
            public byte Mode;
            public byte Endian;
            public byte Attributes;
            public uint KeyType;
            public ushort HeaderSize;
            public ushort MetadataSize;
            public ulong FileSize;
            public ushort NumberOfEntries;
            public ushort Flags;
            public uint Padding;
        }

        public struct SElfEntry
        {
            public ulong Flags;
            public ulong FileOffset;
            public ulong EncryptedCompressedSize;
            public ulong MemorySize;

            public bool IsEncrypted => (Flags & 0x2) != 0;
            public bool IsDeflated => (Flags & 0x8) != 0;
            public bool HasBlocks => (Flags & 0x800) != 0;
            public ushort SegmentIndex => (ushort)((Flags & 0x000F_FFF0_0000) >> 20);
        }

        public static bool IsSElf(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 32)
                return false;
            uint magic = BitConverter.ToUInt32(bytes);
            return magic == SELF_MAGIC || magic == SELF_PRX_MAGIC;
        }

        /// <summary>
        /// Unwraps unencrypted SElf / FSelf into standard ELF64 memory stream.
        /// </summary>
        public static MemoryStream UnwrapToElf(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            stream.Position = 0;

            var header = new SElfHeader
            {
                Magic = reader.ReadUInt32(),
                Version = reader.ReadByte(),
                Mode = reader.ReadByte(),
                Endian = reader.ReadByte(),
                Attributes = reader.ReadByte(),
                KeyType = reader.ReadUInt32(),
                HeaderSize = reader.ReadUInt16(),
                MetadataSize = reader.ReadUInt16(),
                FileSize = reader.ReadUInt64(),
                NumberOfEntries = reader.ReadUInt16(),
                Flags = reader.ReadUInt16(),
                Padding = reader.ReadUInt32()
            };

            if (header.Magic != SELF_MAGIC && header.Magic != SELF_PRX_MAGIC)
                throw new InvalidDataException($"Invalid SElf magic: 0x{header.Magic:X8}");

            if (header.NumberOfEntries > 1024 || (ulong)(header.NumberOfEntries * 32) > (ulong)(stream.Length - stream.Position))
                throw new InvalidDataException($"Invalid SElf NumberOfEntries count: {header.NumberOfEntries}");

            var entries = new SElfEntry[header.NumberOfEntries];
            for (int i = 0; i < header.NumberOfEntries; i++)
            {
                entries[i] = new SElfEntry
                {
                    Flags = reader.ReadUInt64(),
                    FileOffset = reader.ReadUInt64(),
                    EncryptedCompressedSize = reader.ReadUInt64(),
                    MemorySize = reader.ReadUInt64()
                };
            }

            if (entries.Any(e => e.HasBlocks && e.IsEncrypted))
                throw new NotSupportedException("Encrypted SElf segments are not supported (retail signed ELF requires PlayStation keys).");

            if (entries.Any(e => e.HasBlocks && e.IsDeflated))
                throw new NotSupportedException("Compressed (deflated) SElf segments are not supported.");

            // The ELF header follows the SElf entries
            long elfHeaderOffset = stream.Position;
            byte[] elfIdent = reader.ReadBytes(16);
            if (elfIdent.Length < 4 || elfIdent[0] != 0x7F || elfIdent[1] != (byte)'E' || elfIdent[2] != (byte)'L' || elfIdent[3] != (byte)'F')
                throw new InvalidDataException("Invalid embedded ELF header within SElf container.");

            ushort e_type = reader.ReadUInt16();
            ushort e_machine = reader.ReadUInt16();
            uint e_version = reader.ReadUInt32();
            ulong e_entry = reader.ReadUInt64();
            ulong e_phoff = reader.ReadUInt64();
            ulong e_shoff = reader.ReadUInt64();
            uint e_flags = reader.ReadUInt32();
            ushort e_ehsize = reader.ReadUInt16();
            ushort e_phentsize = reader.ReadUInt16();
            ushort e_phnum = reader.ReadUInt16();
            ushort e_shentsize = reader.ReadUInt16();
            ushort e_shnum = reader.ReadUInt16();
            ushort e_shstrndx = reader.ReadUInt16();

            // Calculate overall ELF image bounds based on required segment destinations
            long maxElfOffset = (long)(e_phoff + (ulong)(e_phnum * e_phentsize));
            for (int i = 0; i < e_phnum; i++)
            {
                var entry = entries.FirstOrDefault(e => e.HasBlocks && e.SegmentIndex == i);
                if (entry.HasBlocks)
                {
                    long end = (long)(entry.FileOffset + entry.EncryptedCompressedSize);
                    if (end > maxElfOffset)
                        maxElfOffset = end;
                }
            }

            const long MaxElfBufferSize = 512L * 1024 * 1024; // 512 MB ceiling
            if (maxElfOffset <= 0 || maxElfOffset > MaxElfBufferSize)
                throw new InvalidDataException($"Invalid calculated ELF image size: 0x{maxElfOffset:X}");

            byte[] elfBuffer = new byte[maxElfOffset];

            stream.Position = elfHeaderOffset;
            int initialElfBytes = (int)(e_phoff + (ulong)(e_phnum * e_phentsize));
            byte[] headerData = reader.ReadBytes(initialElfBytes);
            Buffer.BlockCopy(headerData, 0, elfBuffer, 0, headerData.Length);

            if (e_type == 0xFE10 || e_type == 0xFE18)
            {
                BitConverter.GetBytes((ushort)3).CopyTo(elfBuffer, 16);
            }
            else if (e_type == 0xFE00)
            {
                BitConverter.GetBytes((ushort)2).CopyTo(elfBuffer, 16);
            }

            for (int i = 0; i < e_phnum; i++)
            {
                long phOffset = (long)(e_phoff + (ulong)(i * e_phentsize));
                uint p_type = BitConverter.ToUInt32(elfBuffer, (int)phOffset);

                var entry = entries.FirstOrDefault(e => e.HasBlocks && e.SegmentIndex == i);
                if (entry.HasBlocks)
                {
                    if ((long)entry.FileOffset < 0 || (long)entry.EncryptedCompressedSize < 0 ||
                        (long)entry.FileOffset + (long)entry.EncryptedCompressedSize > maxElfOffset)
                    {
                        throw new InvalidDataException($"SElf segment {i} out of bounds: offset 0x{entry.FileOffset:X}, size 0x{entry.EncryptedCompressedSize:X}");
                    }
                    stream.Position = (long)entry.FileOffset;
                    byte[] segmentData = reader.ReadBytes((int)entry.EncryptedCompressedSize);
                    Buffer.BlockCopy(segmentData, 0, elfBuffer, (int)entry.FileOffset, segmentData.Length);

                    BitConverter.GetBytes(entry.FileOffset).CopyTo(elfBuffer, (int)(phOffset + 8));
                    BitConverter.GetBytes(entry.EncryptedCompressedSize).CopyTo(elfBuffer, (int)(phOffset + 32));
                    BitConverter.GetBytes(entry.MemorySize).CopyTo(elfBuffer, (int)(phOffset + 40));
                }
            }

            // Remap non-PT_LOAD sub-segments (e.g. PT_DYNAMIC, PT_NOTE) that reside within a PT_LOAD segment
            for (int i = 0; i < e_phnum; i++)
            {
                var entry = entries.FirstOrDefault(e => e.HasBlocks && e.SegmentIndex == i);
                if (entry.HasBlocks)
                    continue;

                long phOffset = (long)(e_phoff + (ulong)(i * e_phentsize));
                ulong p_vaddr = BitConverter.ToUInt64(elfBuffer, (int)(phOffset + 16));
                ulong p_filesz = BitConverter.ToUInt64(elfBuffer, (int)(phOffset + 32));

                if (p_vaddr > 0 && p_filesz > 0)
                {
                    // Find containing PT_LOAD segment
                    for (int j = 0; j < e_phnum; j++)
                    {
                        var parentEntry = entries.FirstOrDefault(e => e.HasBlocks && e.SegmentIndex == j);
                        if (!parentEntry.HasBlocks)
                            continue;

                        long parentPhOffset = (long)(e_phoff + (ulong)(j * e_phentsize));
                        uint parent_p_type = BitConverter.ToUInt32(elfBuffer, (int)parentPhOffset);
                        if (parent_p_type != 1) // PT_LOAD = 1
                            continue;

                        ulong parent_vaddr = BitConverter.ToUInt64(elfBuffer, (int)(parentPhOffset + 16));
                        ulong parent_memsz = BitConverter.ToUInt64(elfBuffer, (int)(parentPhOffset + 40));
                        ulong parent_offset = BitConverter.ToUInt64(elfBuffer, (int)(parentPhOffset + 8));

                        if (p_vaddr >= parent_vaddr && (p_vaddr + p_filesz) <= (parent_vaddr + parent_memsz))
                        {
                            ulong relativeOffset = p_vaddr - parent_vaddr;
                            ulong newOffset = parent_offset + relativeOffset;
                            BitConverter.GetBytes(newOffset).CopyTo(elfBuffer, (int)(phOffset + 8));
                            break;
                        }
                    }
                }
            }

            return new MemoryStream(elfBuffer);
        }
    }
}
