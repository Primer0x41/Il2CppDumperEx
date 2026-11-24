using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Il2CppDumper
{
    public class PELoader
    {
        public static PE Load(string fileName)
        {
            var buff = File.ReadAllBytes(fileName);
            using var reader = new BinaryStream(new MemoryStream(buff));
            var dosHeader = reader.ReadClass<DosHeader>();
            if (dosHeader.Magic != 0x5A4D)
            {
                throw new InvalidDataException("ERROR: Invalid PE file");
            }
            if (dosHeader.Lfanew < 0 || dosHeader.Lfanew > buff.Length - 4)
            {
                throw new InvalidDataException("ERROR: Invalid PE header offset");
            }
            reader.Position = (ulong)dosHeader.Lfanew;
            if (reader.ReadUInt32() != 0x4550u) //Signature
            {
                throw new InvalidDataException("ERROR: Invalid PE file");
            }
            var fileHeader = reader.ReadClass<FileHeader>();
            var pos = reader.Position;
            ulong imageBase = 0;
            var optMagic = reader.ReadUInt16();
            reader.Position -= 2;
            if (optMagic == 0x10b)
            {
                var opt = reader.ReadClass<OptionalHeader>();
                imageBase = opt.ImageBase;
            }
            else if (optMagic == 0x20b)
            {
                var opt64 = reader.ReadClass<OptionalHeader64>();
                imageBase = opt64.ImageBase;
            }
            reader.Position = pos + fileHeader.SizeOfOptionalHeader;
            var sections = reader.ReadClassArray<SectionHeader>(fileHeader.NumberOfSections);
            if (sections.Length == 0)
            {
                throw new InvalidDataException("PE file contains no sections.");
            }
            ulong maxExtent = sections.Max(s => (ulong)s.VirtualAddress + Math.Max((ulong)s.VirtualSize, (ulong)s.SizeOfRawData));
            const ulong MaxPeImageSize = 512L * 1024 * 1024; // 512 MB ceiling
            if (maxExtent == 0 || maxExtent > MaxPeImageSize)
            {
                throw new InvalidDataException($"PE virtual image size (0x{maxExtent:X}) exceeds supported limit (512 MB).");
            }
            var size = (int)maxExtent;
            var peBuff = new byte[size];

            foreach (var section in sections)
            {
                if (section.PointerToRawData > 0 && section.SizeOfRawData > 0)
                {
                    uint effectiveVirtualSize = section.VirtualSize > 0 ? section.VirtualSize : section.SizeOfRawData;
                    ulong copyLen = Math.Min((ulong)section.SizeOfRawData, (ulong)effectiveVirtualSize);
                    if ((ulong)section.PointerToRawData <= (ulong)buff.Length &&
                        (ulong)section.VirtualAddress <= (ulong)peBuff.Length &&
                        (ulong)section.PointerToRawData + copyLen <= (ulong)buff.Length &&
                        (ulong)section.VirtualAddress + copyLen <= (ulong)peBuff.Length)
                    {
                        Buffer.BlockCopy(buff, (int)section.PointerToRawData, peBuff, (int)section.VirtualAddress, (int)copyLen);
                    }
                }
            }

            var peMemory = new MemoryStream(peBuff);
            var headerSize = reader.Position;
            if (headerSize > (ulong)peBuff.Length || headerSize > (ulong)buff.Length)
            {
                throw new InvalidDataException("PE header size exceeds image buffer.");
            }
            reader.Position = 0;
            var buff2 = reader.ReadBytes((int)headerSize);
            using (var writer = new BinaryWriter(peMemory, Encoding.UTF8, true))
            {
                writer.Write(buff2);
                writer.Flush();
            }
            peMemory.Position = 0;
            var pe = new PE(peMemory);
            pe.LoadFromMemory(imageBase);
            return pe;
        }
    }
}
