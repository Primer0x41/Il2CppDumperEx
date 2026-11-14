using System;
using System.IO;
using System.Text;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class PELoaderTests
{
    [Fact]
    public void PELoader_Load_StaticallyMapsSectionsWithoutLoadLibrary()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "pe_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        string pePath = Path.Combine(tmpDir, "mock_game.dll");

        try
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                // DOS Header (64 bytes)
                bw.Write((ushort)0x5A4D); // e_magic = "MZ"
                for (int i = 0; i < 29; i++) bw.Write((ushort)0);
                bw.Write((uint)0x80); // e_lfanew = 128

                // Padding to e_lfanew (128)
                while (ms.Position < 0x80) bw.Write((byte)0);

                // PE Signature
                bw.Write(0x4550u); // "PE\0\0"

                // FileHeader (20 bytes)
                bw.Write((ushort)0x8664); // Machine = AMD64 (x64)
                bw.Write((ushort)2);      // NumberOfSections = 2
                bw.Write(0u);             // TimeDateStamp
                bw.Write(0u);             // PointerToSymbolTable
                bw.Write(0u);             // NumberOfSymbols
                bw.Write((ushort)240);    // SizeOfOptionalHeader (240 for 64-bit)
                bw.Write((ushort)0x2022); // Characteristics = DLL | EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE

                long optHeaderPos = ms.Position;
                // OptionalHeader64 (240 bytes)
                bw.Write((ushort)0x20b);  // Magic = PE32+ (64-bit)
                bw.Write((byte)14);       // MajorLinkerVersion
                bw.Write((byte)0);        // MinorLinkerVersion
                bw.Write(0x1000u);        // SizeOfCode
                bw.Write(0x1000u);        // SizeOfInitializedData
                bw.Write(0u);             // SizeOfUninitializedData
                bw.Write(0x1000u);        // AddressOfEntryPoint
                bw.Write(0x1000u);        // BaseOfCode
                bw.Write(0x180000000UL);  // ImageBase = 0x180000000
                bw.Write(0x1000u);        // SectionAlignment = 4096
                bw.Write(0x200u);         // FileAlignment = 512
                bw.Write((ushort)6); bw.Write((ushort)0); // OS Version
                bw.Write((ushort)0); bw.Write((ushort)0); // Image Version
                bw.Write((ushort)6); bw.Write((ushort)0); // Subsystem Version
                bw.Write(0u);             // Win32VersionValue
                bw.Write(0x3000u);        // SizeOfImage
                bw.Write(0x200u);         // SizeOfHeaders
                bw.Write(0u);             // CheckSum
                bw.Write((ushort)3);      // Subsystem = Windows CUI
                bw.Write((ushort)0x160);  // DllCharacteristics
                bw.Write(0x100000UL);     // SizeOfStackReserve
                bw.Write(0x1000UL);       // SizeOfStackCommit
                bw.Write(0x100000UL);     // SizeOfHeapReserve
                bw.Write(0x1000UL);       // SizeOfHeapCommit
                bw.Write(0u);             // LoaderFlags
                bw.Write(16u);            // NumberOfRvaAndSizes

                // Pad OptionalHeader to 240 bytes
                while (ms.Position < optHeaderPos + 240) bw.Write((byte)0);

                // Section 1: .text (40 bytes)
                byte[] textName = new byte[8];
                Encoding.ASCII.GetBytes(".text").CopyTo(textName, 0);
                bw.Write(textName);
                bw.Write(0x1000u);        // VirtualSize
                bw.Write(0x1000u);        // VirtualAddress
                bw.Write(0x200u);         // SizeOfRawData
                bw.Write(0x200u);         // PointerToRawData
                bw.Write(0u); bw.Write(0u); bw.Write((ushort)0); bw.Write((ushort)0);
                bw.Write(0x60000020u);    // Characteristics: CODE | EXECUTE | READ

                // Section 2: .data (40 bytes)
                byte[] dataName = new byte[8];
                Encoding.ASCII.GetBytes(".data").CopyTo(dataName, 0);
                bw.Write(dataName);
                bw.Write(0x1000u);        // VirtualSize
                bw.Write(0x2000u);        // VirtualAddress
                bw.Write(0x200u);         // SizeOfRawData
                bw.Write(0x400u);         // PointerToRawData
                bw.Write(0u); bw.Write(0u); bw.Write((ushort)0); bw.Write((ushort)0);
                bw.Write(0xC0000040u);    // Characteristics: INITIALIZED_DATA | READ | WRITE

                // Pad headers to FileAlignment (0x200 = 512 bytes)
                while (ms.Position < 0x200) bw.Write((byte)0);

                // Section 1 data (.text at raw offset 0x200)
                byte[] textData = new byte[0x200];
                textData[0] = 0x90; // NOP
                textData[1] = 0xC3; // RET
                bw.Write(textData);

                // Section 2 data (.data at raw offset 0x400)
                byte[] dataData = new byte[0x200];
                dataData[0] = 0xDE;
                dataData[1] = 0xAD;
                dataData[2] = 0xBE;
                dataData[3] = 0xEF;
                bw.Write(dataData);

                File.WriteAllBytes(pePath, ms.ToArray());
            }

            var pe = PELoader.Load(pePath);
            Assert.NotNull(pe);
            Assert.Equal(0x180000000UL, pe.ImageBase);

            // Verify section 2 virtual address mapping
            ulong mappedOffset = pe.MapVATR(0x180000000UL + 0x2000);
            Assert.Equal(0x2000UL, mappedOffset);

            pe.Position = mappedOffset;
            byte b0 = pe.ReadByte();
            byte b1 = pe.ReadByte();
            Assert.Equal(0xDE, b0);
            Assert.Equal(0xAD, b1);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }
    }
}
