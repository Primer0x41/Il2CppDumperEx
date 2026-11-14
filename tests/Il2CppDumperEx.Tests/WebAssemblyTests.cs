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
}
