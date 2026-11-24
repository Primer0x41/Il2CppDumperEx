using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Il2CppDumper
{
    /// <summary>
    /// A seekable, read-only stream representing a sparse virtual address space.
    /// Maps discrete memory segments to their original virtual address offsets
    /// without allocating physical RAM for unmapped gaps.
    /// </summary>
    public sealed class SparseMemoryStream : Stream
    {
        public readonly struct MemorySegment
        {
            public ulong VirtualStart { get; }
            public ulong VirtualEnd { get; }
            public byte[] Data { get; }

            public MemorySegment(ulong start, ulong end, byte[] data)
            {
                VirtualStart = start;
                VirtualEnd = end;
                Data = data;
            }
        }

        private readonly MemorySegment[] _segments;
        private readonly ulong _baseAddress;
        private readonly long _length;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Position cannot be negative.");
                _position = value;
            }
        }

        public SparseMemoryStream(ulong baseAddress, IEnumerable<MemorySegment> segments)
        {
            _baseAddress = baseAddress;
            _segments = segments.OrderBy(s => s.VirtualStart).ToArray();

            if (_segments.Length > 0)
            {
                ulong maxEnd = _segments.Max(s => s.VirtualEnd);
                _length = maxEnd >= baseAddress ? (long)(maxEnd - baseAddress) : 0;
            }
            else
            {
                _length = 0;
            }

            _position = 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (_position >= _length || count == 0)
                return 0;

            int totalRead = 0;
            ulong currentVa = _baseAddress + (ulong)_position;

            while (count > 0 && _position < _length)
            {
                // Find if currentVa lands inside a segment
                int segIdx = FindSegmentIndex(currentVa);
                if (segIdx >= 0)
                {
                    var seg = _segments[segIdx];
                    int offsetInSeg = (int)(currentVa - seg.VirtualStart);
                    int availableInSeg = Math.Max(0, seg.Data.Length - offsetInSeg);
                    int toRead = Math.Min(count, availableInSeg);

                    if (toRead > 0)
                    {
                        Buffer.BlockCopy(seg.Data, offsetInSeg, buffer, offset, toRead);
                        totalRead += toRead;
                        offset += toRead;
                        count -= toRead;
                        _position += toRead;
                        currentVa += (ulong)toRead;
                        continue;
                    }
                }

                // If not in a segment, we are in an unmapped gap.
                // Find distance to next segment or end of stream
                ulong nextSegStart = (ulong)(_baseAddress + (ulong)_length);
                for (int i = 0; i < _segments.Length; i++)
                {
                    if (_segments[i].VirtualStart > currentVa)
                    {
                        nextSegStart = _segments[i].VirtualStart;
                        break;
                    }
                }

                int gapBytes = (int)Math.Min((ulong)count, nextSegStart - currentVa);
                if (gapBytes <= 0) break;

                // Fill gap with zeros
                Array.Clear(buffer, offset, gapBytes);
                totalRead += gapBytes;
                offset += gapBytes;
                count -= gapBytes;
                _position += gapBytes;
                currentVa += (ulong)gapBytes;
            }

            return totalRead;
        }

        private int FindSegmentIndex(ulong va)
        {
            for (int i = 0; i < _segments.Length; i++)
            {
                if (va >= _segments[i].VirtualStart && va < _segments[i].VirtualEnd)
                {
                    return i;
                }
            }
            return -1;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (newPos < 0)
                throw new IOException("An attempt was made to move the position before the beginning of the stream.");

            _position = newPos;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
