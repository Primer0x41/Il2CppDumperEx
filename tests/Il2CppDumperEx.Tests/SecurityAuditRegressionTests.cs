using System;
using System.IO;
using Il2CppDumper;
using Il2CppDumperEx.Core;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class SecurityAuditRegressionTests
{
    [Fact]
    public void DummyAssemblyExport_PathTraversalInModuleName_DoesNotEscapeDummyDirectory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sec_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dummyDir = Path.Combine(tempDir, "DummyDll");
        Directory.CreateDirectory(dummyDir);

        try
        {
            var assemblyName = new AssemblyNameDefinition("MaliciousAssembly", new Version(1, 0));
            // Module name attempting path traversal to parent directory
            using var assembly = AssemblyDefinition.CreateAssembly(assemblyName, @"..\..\escaped.dll", ModuleKind.Dll);

            // Directly test the sanitization pattern applied in DummyAssemblyExporter
            string rawName = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(assembly.MainModule.Name) ? assembly.Name.Name : assembly.MainModule.Name);
            string sanitized = Il2CppDecompiler.SanitizeFileName(rawName);
            string safeFileName = (string.IsNullOrWhiteSpace(sanitized) ? "Assembly" : sanitized) + ".dll";

            string destination = Path.GetFullPath(Path.Combine(dummyDir, safeFileName));
            string canonicalDummyDir = Path.GetFullPath(dummyDir);

            // Assert destination is strictly contained within dummyDir
            Assert.StartsWith(canonicalDummyDir + Path.DirectorySeparatorChar, destination, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(tempDir, "escaped.dll")));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("CON.dll", "_CON.dll")]
    [InlineData("PRN", "_PRN.dll")]
    [InlineData("AUX.dll", "_AUX.dll")]
    [InlineData("foo:bar*baz?.dll", "foo_bar_baz_.dll")]
    public void DummyAssemblyExport_ReservedOrIllegalNames_AreSanitized(string inputName, string expectedFileName)
    {
        string rawName = Path.GetFileNameWithoutExtension(inputName);
        string sanitized = Il2CppDecompiler.SanitizeFileName(rawName);
        string safeFileName = (string.IsNullOrWhiteSpace(sanitized) ? "Assembly" : sanitized) + ".dll";

        Assert.Equal(expectedFileName, safeFileName);
    }

    [Fact]
    public void BinaryStream_ReadClassArray_ExcessiveCount_ThrowsInvalidDataException()
    {
        byte[] tinyBuffer = new byte[16];
        using var ms = new MemoryStream(tinyBuffer);
        using var stream = new BinaryStream(ms);

        // Stream has only 16 bytes, but we request 100,000 elements
        Assert.Throws<InvalidDataException>(() => stream.ReadClassArray<int>(100_000));
    }

    [Fact]
    public void BinaryStream_ReadStringToNull_ExceedingMaxBytes_CapsReadLength()
    {
        // 100 non-null bytes followed by a null byte
        byte[] data = new byte[101];
        Array.Fill(data, (byte)'A', 0, 100);
        data[100] = 0;

        using var ms = new MemoryStream(data);
        using var stream = new BinaryStream(ms);

        // Limit read to 10 bytes
        string result = stream.ReadStringToNull(0, maxBytes: 10);
        Assert.Equal(10, result.Length);
        Assert.Equal("AAAAAAAAAA", result);
    }

    [Fact]
    public void WebAssembly_CorruptedSectionCount_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(0x6D736100u); // "\0asm"
        bw.Write(1);          // version 1

        // Data section: id = 11, length = 10
        bw.Write((byte)11);
        bw.Write((byte)10);
        // Maliciously huge count in LEB128 (0xFF, 0xFF, 0xFF, 0x7F)
        bw.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });

        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => new WebAssembly(ms));
    }

    [Fact]
    public void PELoader_ExcessiveVirtualExtent_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        // DOS Header (MZ)
        bw.Write((ushort)0x5A4D);
        for (int i = 0; i < 29; i++) bw.Write((ushort)0);
        bw.Write(0x80); // lfanew

        // Padding to 0x80
        while (ms.Position < 0x80) bw.Write((byte)0);

        // PE Signature
        bw.Write(0x4550u);

        // FileHeader
        bw.Write((ushort)0x8664); // Machine = AMD64
        bw.Write((ushort)1);      // NumberOfSections = 1
        bw.Write(0);              // TimeDateStamp
        bw.Write(0);              // PointerToSymbolTable
        bw.Write(0);              // NumberOfSymbols
        bw.Write((ushort)0xF0);   // SizeOfOptionalHeader = 240
        bw.Write((ushort)0x22);   // Characteristics = EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE

        // OptionalHeader64
        long optStart = ms.Position;
        bw.Write((ushort)0x20b);  // Magic = PE32+
        while (ms.Position < optStart + 0xF0) bw.Write((byte)0);

        // SectionHeader with massive VirtualAddress (0x7FFFF000)
        bw.Write(new byte[8]);          // Name
        bw.Write(0x1000u);              // VirtualSize
        bw.Write(0x7FFFF000u);          // VirtualAddress (near 2GB boundary)
        bw.Write(0x1000u);              // SizeOfRawData
        bw.Write(0x200u);               // PointerToRawData
        bw.Write(0u); bw.Write(0u); bw.Write((ushort)0); bw.Write((ushort)0); bw.Write(0x60000020u); // Characteristics

        // Pad to cover raw data
        while (ms.Position < 0x1200) bw.Write((byte)0);

        string tempPe = Path.Combine(Path.GetTempPath(), "pe_sec_test_" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(tempPe, ms.ToArray());

        try
        {
            Assert.Throws<InvalidDataException>(() => PELoader.Load(tempPe));
        }
        finally
        {
            try { File.Delete(tempPe); } catch { }
        }
    }
}
