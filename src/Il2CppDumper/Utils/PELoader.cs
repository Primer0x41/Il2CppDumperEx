using System;
using System.IO;
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
            reader.Position = dosHeader.Lfanew;
            if (reader.ReadUInt32() != 0x4550u) //Signature
            {
                throw new InvalidDataException("ERROR: Invalid PE file");
            }
            var fileHeader = reader.ReadClass<FileHeader>();
            if (fileHeader.Machine == 0x14c && Environment.Is64BitProcess)
            {
                throw new InvalidOperationException("The target is a 32-bit PE binary, which cannot be loaded in a 64-bit process.");
            }
            if (fileHeader.Machine == 0x8664 && !Environment.Is64BitProcess)
            {
                throw new InvalidOperationException("The target is a 64-bit PE binary, which cannot be loaded in a 32-bit process.");
            }
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
            var last = sections[^1];
            var size = last.VirtualAddress + last.VirtualSize;
            var peBuff = new byte[size];

            foreach (var section in sections)
            {
                if (section.PointerToRawData > 0 && section.SizeOfRawData > 0)
                {
                    int copyLen = (int)Math.Min(section.SizeOfRawData, section.VirtualSize);
                    if ((int)section.PointerToRawData + copyLen <= buff.Length &&
                        (int)section.VirtualAddress + copyLen <= peBuff.Length)
                    {
                        Buffer.BlockCopy(buff, (int)section.PointerToRawData, peBuff, (int)section.VirtualAddress, copyLen);
                    }
                }
            }

            var peMemory = new MemoryStream(peBuff);
            var writer = new BinaryWriter(peMemory, Encoding.UTF8, true);
            var headerSize = reader.Position;
            reader.Position = 0;
            var buff2 = reader.ReadBytes((int)headerSize);
            writer.Write(buff2);
            writer.Flush();
            writer.Close();
            peMemory.Position = 0;
            var pe = new PE(peMemory);
            pe.LoadFromMemory(imageBase);
            return pe;
        }
    }
}
