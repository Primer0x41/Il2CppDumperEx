using System.Buffers.Binary;
using System.IO;

namespace Il2CppDumper
{
    public sealed class MachoFat : BinaryStream
    {
        public Fat[] fats;

        public MachoFat(Stream stream) : base(stream)
        {
            var magicBytes = ReadBytes(4);
            var magicBE = BinaryPrimitives.ReadUInt32BigEndian(magicBytes);
            bool isBigEndian = magicBE == 0xCAFEBABE;

            var sizeBytes = ReadBytes(4);
            var size = isBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(sizeBytes)
                : BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);

            if (size < 0 || size > 100)
                throw new InvalidDataException($"Invalid Mach-O fat architecture count: {size}");

            fats = new Fat[size];
            for (var i = 0; i < size; i++)
            {
                Position += 8;
                var offsetBytes = ReadBytes(4);
                var sliceSizeBytes = ReadBytes(4);
                fats[i] = new Fat
                {
                    offset = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(offsetBytes)
                        : BinaryPrimitives.ReadUInt32LittleEndian(offsetBytes),
                    size = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(sliceSizeBytes)
                        : BinaryPrimitives.ReadUInt32LittleEndian(sliceSizeBytes)
                };
                Position += 4;
            }
            for (var i = 0; i < size; i++)
            {
                Position = fats[i].offset;
                fats[i].magic = ReadUInt32();
            }
        }

        public byte[] GetMacho(int index)
        {
            Position = fats[index].offset;
            return ReadBytes((int)fats[index].size);
        }
    }
}
