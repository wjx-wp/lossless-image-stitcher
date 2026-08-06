using System;
using System.IO;
using System.IO.Compression;

namespace LosslessStitcher
{
    /// <summary>
    /// Writes an 8-bit RGBA PNG one scanline at a time.
    /// </summary>
    internal sealed class PngStreamWriter : IDisposable
    {
        private const int BytesPerPixel = 4;
        private const int IdatChunkSize = 64 * 1024;

        private static readonly byte[] PngSignature =
        {
            137, 80, 78, 71, 13, 10, 26, 10
        };

        private static readonly byte[] IhdrType = { 73, 72, 68, 82 };
        private static readonly byte[] IdatType = { 73, 68, 65, 84 };
        private static readonly byte[] IendType = { 73, 69, 78, 68 };
        private static readonly uint[] CrcTable = CreateCrcTable();

        private readonly int _height;
        private readonly int _rowByteCount;
        private readonly byte[] _previousRow;
        private readonly byte[] _filteredRow;
        private readonly Adler32 _adler32;

        private FileStream _output;
        private IdatChunkStream _idatOutput;
        private DeflateStream _deflateOutput;
        private int _rowsWritten;
        private bool _completed;
        private bool _disposed;

        internal PngStreamWriter(string path, int width, int height)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An output path is required.", "path");
            }

            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException("width", "PNG width must be greater than zero.");
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException("height", "PNG height must be greater than zero.");
            }

            _height = height;
            _rowByteCount = checked(width * BytesPerPixel);
            _previousRow = new byte[_rowByteCount];
            _filteredRow = new byte[checked(_rowByteCount + 1)];
            _adler32 = new Adler32();

            try
            {
                _output = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    IdatChunkSize,
                    FileOptions.SequentialScan);

                _output.Write(PngSignature, 0, PngSignature.Length);
                WriteHeader(_output, width, height);

                _idatOutput = new IdatChunkStream(_output, IdatChunkSize);

                // DeflateStream on .NET Framework writes an RFC 1951 raw DEFLATE
                // stream. PNG requires that stream inside an RFC 1950 zlib wrapper.
                _idatOutput.WriteByte(0x78);
                _idatOutput.WriteByte(0x9c);
                _deflateOutput = new DeflateStream(
                    _idatOutput,
                    CompressionMode.Compress,
                    true);
            }
            catch
            {
                Abort();
                throw;
            }
        }

        internal void WriteRgbaRow(byte[] row)
        {
            EnsureWritable();

            if (row == null)
            {
                throw new ArgumentNullException("row");
            }

            if (row.Length != _rowByteCount)
            {
                throw new ArgumentException(
                    "The RGBA row length must equal width * 4 bytes.",
                    "row");
            }

            if (_rowsWritten >= _height)
            {
                throw new InvalidOperationException("All PNG rows have already been written.");
            }

            try
            {
                _filteredRow[0] = 4; // PNG filter method: Paeth.

                for (int index = 0; index < _rowByteCount; index++)
                {
                    int left = index >= BytesPerPixel ? row[index - BytesPerPixel] : 0;
                    int above = _previousRow[index];
                    int upperLeft = index >= BytesPerPixel
                        ? _previousRow[index - BytesPerPixel]
                        : 0;
                    int predictor = PaethPredictor(left, above, upperLeft);

                    _filteredRow[index + 1] = unchecked((byte)(row[index] - predictor));
                }

                _adler32.Update(_filteredRow, 0, _filteredRow.Length);
                _deflateOutput.Write(_filteredRow, 0, _filteredRow.Length);
                Buffer.BlockCopy(row, 0, _previousRow, 0, _rowByteCount);
                _rowsWritten++;
            }
            catch
            {
                Abort();
                throw;
            }
        }

        internal void Complete()
        {
            if (_completed)
            {
                return;
            }

            if (_disposed)
            {
                throw new ObjectDisposedException("PngStreamWriter");
            }

            try
            {
                if (_rowsWritten != _height)
                {
                    throw new InvalidOperationException(
                        "The PNG cannot be completed until every scanline has been written.");
                }

                _deflateOutput.Dispose();
                _deflateOutput = null;

                byte[] adlerBytes = new byte[4];
                WriteUInt32BigEndian(adlerBytes, 0, _adler32.Value);
                _idatOutput.Write(adlerBytes, 0, adlerBytes.Length);
                _idatOutput.Finish();
                _idatOutput.Dispose();
                _idatOutput = null;

                WriteChunk(_output, IendType, null, 0, 0);
                _output.Flush();
                _output.Dispose();
                _output = null;

                _completed = true;
                _disposed = true;
            }
            catch
            {
                Abort();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Normal callers explicitly invoke Complete(). Dispose is deliberately
            // non-throwing so cancellation or an upstream exception is not hidden
            // by a secondary "missing scanlines" exception during using cleanup.
            Abort();
        }

        private void EnsureWritable()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The PNG has already been completed.");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException("PngStreamWriter");
            }
        }

        private void Abort()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_deflateOutput != null)
            {
                try
                {
                    _deflateOutput.Dispose();
                }
                catch
                {
                    // Preserve the exception that caused the abort.
                }

                _deflateOutput = null;
            }

            if (_idatOutput != null)
            {
                try
                {
                    _idatOutput.Dispose();
                }
                catch
                {
                    // Preserve the exception that caused the abort.
                }

                _idatOutput = null;
            }

            if (_output != null)
            {
                try
                {
                    _output.Dispose();
                }
                catch
                {
                    // Preserve the exception that caused the abort.
                }

                _output = null;
            }
        }

        private static int PaethPredictor(int left, int above, int upperLeft)
        {
            int estimate = left + above - upperLeft;
            int leftDistance = Math.Abs(estimate - left);
            int aboveDistance = Math.Abs(estimate - above);
            int upperLeftDistance = Math.Abs(estimate - upperLeft);

            if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
            {
                return left;
            }

            return aboveDistance <= upperLeftDistance ? above : upperLeft;
        }

        private static void WriteHeader(Stream output, int width, int height)
        {
            byte[] header = new byte[13];
            WriteUInt32BigEndian(header, 0, (uint)width);
            WriteUInt32BigEndian(header, 4, (uint)height);
            header[8] = 8;  // Bit depth.
            header[9] = 6;  // RGBA truecolour with alpha.
            header[10] = 0; // Compression method.
            header[11] = 0; // Filter method.
            header[12] = 0; // No interlace.
            WriteChunk(output, IhdrType, header, 0, header.Length);
        }

        private static void WriteChunk(
            Stream output,
            byte[] type,
            byte[] data,
            int offset,
            int count)
        {
            byte[] word = new byte[4];
            WriteUInt32BigEndian(word, 0, (uint)count);
            output.Write(word, 0, word.Length);
            output.Write(type, 0, type.Length);

            if (count != 0)
            {
                output.Write(data, offset, count);
            }

            uint crc = 0xffffffffU;
            crc = UpdateCrc(crc, type, 0, type.Length);

            if (count != 0)
            {
                crc = UpdateCrc(crc, data, offset, count);
            }

            WriteUInt32BigEndian(word, 0, crc ^ 0xffffffffU);
            output.Write(word, 0, word.Length);
        }

        private static uint UpdateCrc(uint crc, byte[] bytes, int offset, int count)
        {
            int end = offset + count;

            for (int index = offset; index < end; index++)
            {
                crc = CrcTable[(crc ^ bytes[index]) & 0xff] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] CreateCrcTable()
        {
            uint[] table = new uint[256];

            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xedb88320U ^ (value >> 1)
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }

        private static void WriteUInt32BigEndian(byte[] destination, int offset, uint value)
        {
            destination[offset] = (byte)(value >> 24);
            destination[offset + 1] = (byte)(value >> 16);
            destination[offset + 2] = (byte)(value >> 8);
            destination[offset + 3] = (byte)value;
        }

        private sealed class Adler32
        {
            private const uint Modulus = 65521;
            private const int MaximumBlockLength = 5552;

            private uint _sum1 = 1;
            private uint _sum2;

            internal uint Value
            {
                get { return (_sum2 << 16) | _sum1; }
            }

            internal void Update(byte[] bytes, int offset, int count)
            {
                while (count > 0)
                {
                    int blockLength = Math.Min(count, MaximumBlockLength);
                    int blockEnd = offset + blockLength;

                    while (offset < blockEnd)
                    {
                        _sum1 += bytes[offset++];
                        _sum2 += _sum1;
                    }

                    _sum1 %= Modulus;
                    _sum2 %= Modulus;
                    count -= blockLength;
                }
            }
        }

        private sealed class IdatChunkStream : Stream
        {
            private readonly Stream _output;
            private readonly byte[] _buffer;
            private int _bufferedByteCount;
            private bool _finished;

            internal IdatChunkStream(Stream output, int chunkSize)
            {
                _output = output;
                _buffer = new byte[chunkSize];
            }

            public override bool CanRead
            {
                get { return false; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return !_finished; }
            }

            public override long Length
            {
                get { throw new NotSupportedException(); }
            }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
                _output.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_finished)
                {
                    throw new InvalidOperationException("The IDAT stream has already been finished.");
                }

                if (buffer == null)
                {
                    throw new ArgumentNullException("buffer");
                }

                if (offset < 0 || count < 0 || buffer.Length - offset < count)
                {
                    throw new ArgumentOutOfRangeException("offset");
                }

                while (count > 0)
                {
                    int available = _buffer.Length - _bufferedByteCount;
                    int copyLength = Math.Min(available, count);
                    Buffer.BlockCopy(buffer, offset, _buffer, _bufferedByteCount, copyLength);
                    _bufferedByteCount += copyLength;
                    offset += copyLength;
                    count -= copyLength;

                    if (_bufferedByteCount == _buffer.Length)
                    {
                        EmitBufferedChunk();
                    }
                }
            }

            internal void Finish()
            {
                if (_finished)
                {
                    return;
                }

                if (_bufferedByteCount > 0)
                {
                    EmitBufferedChunk();
                }

                _finished = true;
            }

            private void EmitBufferedChunk()
            {
                WriteChunk(_output, IdatType, _buffer, 0, _bufferedByteCount);
                _bufferedByteCount = 0;
            }

            protected override void Dispose(bool disposing)
            {
                _finished = true;
                _bufferedByteCount = 0;
                base.Dispose(disposing);
            }
        }
    }
}
