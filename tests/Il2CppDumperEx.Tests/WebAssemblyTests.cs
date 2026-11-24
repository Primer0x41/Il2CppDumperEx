using System.IO;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumperEx.Tests;

public class WebAssemblyTests
{
    [Fact]
    public void CreateMemory_WithNoSections_ReturnsEmptyMemoryWithoutNullReference()
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(0x6D736100u); // "\0asm"
        bw.Write(1);          // version 1

        ms.Position = 0;
        var wasm = new WebAssembly(ms);
        var memory = wasm.CreateMemory();

        Assert.NotNull(memory);
    }

    [Fact]
    public void CreateMemory_WithNonDataSectionOnly_ReturnsEmptyMemory()
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(0x6D736100u); // "\0asm"
        bw.Write(1);          // version 1

        // Type section: id = 1, length = 2, body = [0, 0]
        bw.Write((byte)1);
        bw.Write((byte)2);
        bw.Write(new byte[] { 0, 0 });

        ms.Position = 0;
        var wasm = new WebAssembly(ms);
        var memory = wasm.CreateMemory();

        Assert.NotNull(memory);
    }

    [Fact]
    public void CreateMemory_WithEmptyDataSection_ReturnsEmptyMemory()
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(0x6D736100u); // "\0asm"
        bw.Write(1);          // version 1

        // Data section: id = 11, length = 1, count = 0
        bw.Write((byte)11);
        bw.Write((byte)1);
        bw.Write((byte)0);

        ms.Position = 0;
        var wasm = new WebAssembly(ms);
        var memory = wasm.CreateMemory();

        Assert.NotNull(memory);
    }

    [Fact]
    public void CreateMemory_WithDataSectionExceedingFileLength_AllocatesSufficientBuffer()
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(0x6D736100u); // "\0asm"
        bw.Write(1);          // version 1

        // Data section: id = 11, length = 12
        bw.Write((byte)11);
        bw.Write((byte)12);
        // count = 1
        bw.Write((byte)1);
        // index = 0
        bw.Write((byte)0);
        // i32.const = 0x41
        bw.Write((byte)0x41);
        // offset = 0x80 (LEB128: 0x80, 0x01 = 128)
        bw.Write((byte)0x80);
        bw.Write((byte)0x01);
        // end = 0x0B
        bw.Write((byte)0x0B);
        // data length = 4
        bw.Write((byte)4);
        bw.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        // Total stream size is < 30 bytes, but offset + length is 132 bytes.
        ms.Position = 0;
        var wasm = new WebAssembly(ms);
        var memory = wasm.CreateMemory();

        Assert.NotNull(memory);
        Assert.True(memory.Length >= 132);
        memory.Position = 128;
        Assert.Equal(0xDE, memory.ReadByte());
        Assert.Equal(0xAD, memory.ReadByte());
        Assert.Equal(0xBE, memory.ReadByte());
        Assert.Equal(0xEF, memory.ReadByte());
    }
}
