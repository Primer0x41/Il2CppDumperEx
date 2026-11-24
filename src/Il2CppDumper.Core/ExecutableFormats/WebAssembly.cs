using System;
using System.IO;

namespace Il2CppDumper
{
    public sealed class WebAssembly : BinaryStream
    {
        private readonly DataSection[] dataSections;

        public WebAssembly(Stream stream) : base(stream)
        {
            Is32Bit = true;
            var magic = ReadUInt32();
            var version = ReadInt32();
            while (Position < Length)
            {
                var id = ReadULeb128();
                var len = ReadULeb128();
                if (id == 11)
                {
                    var count = ReadULeb128();
                    if ((count > 0 && Position >= Length) || (count > 0 && count > (Length - Position)) || count > 100_000)
                    {
                        throw new InvalidDataException($"Invalid WebAssembly data section count: {count}");
                    }
                    dataSections = new DataSection[count];
                    for (int i = 0; i < count; i++)
                    {
                        var dataSection = new DataSection();
                        dataSections[i] = dataSection;
                        dataSection.Index = ReadULeb128();
                        var opCode = ReadByte();
                        if (opCode != 0x41) //i32.const
                        {
                            throw new InvalidOperationException();
                        }
                        dataSection.Offset = ReadULeb128();
                        opCode = ReadByte();
                        if (opCode != 0xB) //end
                        {
                            throw new InvalidOperationException();
                        }
                        var dataLen = ReadULeb128();
                        if (dataLen > (Length - Position) || dataLen > int.MaxValue)
                        {
                            throw new InvalidDataException($"Invalid WebAssembly data segment length: {dataLen}");
                        }
                        dataSection.Data = ReadBytes((int)dataLen);
                    }
                    break;
                }
                Position += len;
            }
        }

        public WebAssemblyMemory CreateMemory()
        {
            if (dataSections == null || dataSections.Length == 0)
            {
                return new WebAssemblyMemory(new MemoryStream(), 0);
            }
            var last = dataSections[^1];
            var bssStart = last.Offset + (uint)last.Data.Length;
            long maxOffset = 0;
            foreach (var d in dataSections)
            {
                long end = (long)d.Offset + (uint)d.Data.Length;
                if (end > maxOffset) maxOffset = end;
            }
            long requiredCapacity = Math.Max((long)Length, maxOffset);
            const long MaxWasmMemory = 512L * 1024 * 1024; // 512 MB sane ceiling
            if (requiredCapacity > MaxWasmMemory)
            {
                throw new NotSupportedException($"WebAssembly linear memory size (0x{requiredCapacity:X}) exceeds maximum supported limit (512 MB).");
            }
            var stream = new MemoryStream(new byte[(int)requiredCapacity]);
            foreach (var dataSection in dataSections)
            {
                stream.Position = dataSection.Offset;
                stream.Write(dataSection.Data, 0, dataSection.Data.Length);
            }
            return new WebAssemblyMemory(stream, bssStart);
        }
    }
}
