using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security;

namespace LosslessStitcher
{
    /// <summary>
    /// Reads supported PNG files one scanline at a time without passing pixel data
    /// through GDI+.  In particular, RGBA values are never premultiplied, so RGB
    /// values belonging to fully transparent pixels are retained exactly.
    /// </summary>
    internal sealed class PngRowReader : IDisposable
    {
        private static readonly byte[] PngSignature =
        {
            137, 80, 78, 71, 13, 10, 26, 10
        };

        private static readonly uint[] CrcTable = CreateCrcTable();

        private readonly FileStream _file;
        private readonly IdatRangeStream _deflateSource;
        private readonly DeflateStream _inflater;
        private readonly int _width;
        private readonly int _height;
        private readonly int _bytesPerPixel;
        private readonly int _sourceRowBytes;
        private readonly int _rgbaRowBytes;
        private readonly uint _expectedAdler32;
        private readonly byte[] _singleByte;
        private readonly bool _hasTransparentColor;
        private readonly byte _transparentRed;
        private readonly byte _transparentGreen;
        private readonly byte _transparentBlue;

        private byte[] _currentRow;
        private byte[] _previousRow;
        private int _nextRow;
        private uint _adlerA;
        private uint _adlerB;
        private bool _finished;
        private bool _disposed;

        private PngRowReader(FileStream file, ParsedPng parsed, uint expectedAdler32)
        {
            _file = file;
            _width = parsed.Width;
            _height = parsed.Height;
            _bytesPerPixel = parsed.ColorType == 2 ? 3 : 4;
            _sourceRowBytes = checked(_width * _bytesPerPixel);
            _rgbaRowBytes = checked(_width * 4);
            _expectedAdler32 = expectedAdler32;
            _hasTransparentColor = parsed.HasTransparentColor;
            _transparentRed = parsed.TransparentRed;
            _transparentGreen = parsed.TransparentGreen;
            _transparentBlue = parsed.TransparentBlue;
            _currentRow = new byte[_sourceRowBytes];
            _previousRow = new byte[_sourceRowBytes];
            _singleByte = new byte[1];
            _adlerA = 1;
            _adlerB = 0;

            // A PNG IDAT stream has a two-byte zlib header and a four-byte Adler-32
            // trailer.  .NET Framework's DeflateStream consumes raw RFC 1951 data,
            // so expose only the bytes between those wrappers.
            _deflateSource = new IdatRangeStream(
                file,
                parsed.IdatSegments,
                2,
                parsed.IdatLength - 6);
            _inflater = new DeflateStream(_deflateSource, CompressionMode.Decompress);
        }

        public int Width
        {
            get { return _width; }
        }

        public int Height
        {
            get { return _height; }
        }

        /// <summary>
        /// Opens only 8-bit, non-interlaced RGB or RGBA PNG files.  A valid PNG
        /// using another format returns false.  Once the PNG signature and a
        /// supported IHDR have been accepted, corrupt data is reported with
        /// InvalidDataException rather than silently falling back to another decoder.
        /// </summary>
        public static bool TryOpen(string path, out PngRowReader reader)
        {
            reader = null;

            if (String.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            FileStream file;
            try
            {
                file = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }

            try
            {
                ParsedPng parsed = Parse(file);
                ValidateZlibEnvelope(file, parsed);

                uint expectedAdler32 = ReadExpectedAdler32(file, parsed);
                reader = new PngRowReader(file, parsed, expectedAdler32);
                file = null;
                return true;
            }
            catch (UnsupportedPngException)
            {
                return false;
            }
            finally
            {
                if (file != null)
                {
                    file.Dispose();
                }
            }
        }

        /// <summary>
        /// Reads exactly one scanline into an exact-width RGBA buffer.
        /// </summary>
        public void ReadNextRgba(byte[] destination)
        {
            ThrowIfDisposed();

            if (destination == null)
            {
                throw new ArgumentNullException("destination");
            }

            if (destination.Length != _rgbaRowBytes)
            {
                throw new ArgumentException(
                    "The destination must contain exactly Width * 4 bytes.",
                    "destination");
            }

            if (_finished || _nextRow >= _height)
            {
                throw new EndOfStreamException("All PNG scanlines have already been read.");
            }

            ReadInflatedExactly(_singleByte, 0, 1);
            byte filter = _singleByte[0];
            if (filter > 4)
            {
                throw new InvalidDataException("The PNG scanline uses an unknown filter type.");
            }

            ReadInflatedExactly(_currentRow, 0, _sourceRowBytes);
            ReverseFilter(filter);
            CopyCurrentRowToRgba(destination);

            byte[] oldPrevious = _previousRow;
            _previousRow = _currentRow;
            _currentRow = oldPrevious;
            _nextRow++;

            if (_nextRow == _height)
            {
                FinishImage();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _inflater.Dispose();
            }
            finally
            {
                try
                {
                    _deflateSource.Dispose();
                }
                finally
                {
                    _file.Dispose();
                }
            }
        }

        private static ParsedPng Parse(FileStream file)
        {
            long fileLength = file.Length;
            if (fileLength < PngSignature.Length)
            {
                throw new UnsupportedPngException();
            }

            byte[] signature = new byte[PngSignature.Length];
            ReadFileExactly(file, signature, 0, signature.Length, false);

            int signatureIndex;
            for (signatureIndex = 0; signatureIndex < signature.Length; signatureIndex++)
            {
                if (signature[signatureIndex] != PngSignature[signatureIndex])
                {
                    throw new UnsupportedPngException();
                }
            }

            ParsedPng result = new ParsedPng();
            byte[] typeBytes = new byte[4];
            byte[] scratch = new byte[64 * 1024];
            bool sawHeader = false;
            bool sawPalette = false;
            bool sawTransparency = false;
            bool sawIdat = false;
            bool idatEnded = false;
            bool sawEnd = false;

            while (!sawEnd)
            {
                if (fileLength - file.Position < 12)
                {
                    throw new InvalidDataException("The PNG chunk stream is truncated.");
                }

                uint chunkLengthValue = ReadUInt32BigEndian(file);
                if (chunkLengthValue > Int32.MaxValue)
                {
                    throw new InvalidDataException("A PNG chunk exceeds the format's 31-bit size limit.");
                }

                int chunkLength = (int)chunkLengthValue;
                ReadFileExactly(file, typeBytes, 0, typeBytes.Length, true);
                ValidateChunkType(typeBytes);

                string chunkType = new string(new[]
                {
                    (char)typeBytes[0],
                    (char)typeBytes[1],
                    (char)typeBytes[2],
                    (char)typeBytes[3]
                });

                if (!sawHeader && chunkType != "IHDR")
                {
                    throw new InvalidDataException("IHDR must be the first PNG chunk.");
                }

                long dataOffset = file.Position;
                if ((long)chunkLength > fileLength - dataOffset - 4)
                {
                    throw new InvalidDataException("A PNG chunk extends beyond the end of the file.");
                }

                if (chunkType == "IHDR")
                {
                    if (sawHeader || chunkLength != 13)
                    {
                        throw new InvalidDataException("The PNG must contain one 13-byte IHDR chunk.");
                    }
                }
                else if (chunkType == "PLTE")
                {
                    if (sawPalette || sawTransparency || sawIdat || chunkLength == 0 ||
                        chunkLength > 768 || (chunkLength % 3) != 0)
                    {
                        throw new InvalidDataException("The PNG palette chunk is invalid or out of order.");
                    }

                    sawPalette = true;
                }
                else if (chunkType == "tRNS")
                {
                    if (sawTransparency || sawIdat ||
                        result.ColorType != 2 || chunkLength != 6)
                    {
                        throw new InvalidDataException("The PNG transparency chunk is invalid or out of order.");
                    }

                    sawTransparency = true;
                }
                else if (chunkType == "IDAT")
                {
                    if (idatEnded)
                    {
                        throw new InvalidDataException("PNG IDAT chunks must be consecutive.");
                    }

                    sawIdat = true;
                    if (chunkLength != 0)
                    {
                        result.IdatSegments.Add(new IdatSegment(dataOffset, chunkLength));
                    }

                    if (Int64.MaxValue - result.IdatLength < chunkLength)
                    {
                        throw new InvalidDataException("The combined PNG IDAT stream is too large.");
                    }

                    result.IdatLength += chunkLength;
                }
                else if (chunkType == "IEND")
                {
                    if (!sawIdat || chunkLength != 0)
                    {
                        throw new InvalidDataException("The PNG IEND chunk is invalid or premature.");
                    }

                    idatEnded = true;
                }
                else
                {
                    bool critical = (typeBytes[0] & 32) == 0;
                    if (critical)
                    {
                        throw new InvalidDataException("The PNG contains an unknown critical chunk.");
                    }

                    if (sawIdat)
                    {
                        idatEnded = true;
                    }
                }

                uint crc = UInt32.MaxValue;
                crc = UpdateCrc(crc, typeBytes, 0, typeBytes.Length);
                byte[] headerData = null;
                byte[] transparencyData = null;

                if (chunkType == "IHDR")
                {
                    headerData = new byte[13];
                    ReadFileExactly(file, headerData, 0, headerData.Length, true);
                    crc = UpdateCrc(crc, headerData, 0, headerData.Length);
                }
                else if (chunkType == "tRNS")
                {
                    transparencyData = new byte[6];
                    ReadFileExactly(file, transparencyData, 0, transparencyData.Length, true);
                    crc = UpdateCrc(crc, transparencyData, 0, transparencyData.Length);
                }
                else
                {
                    int remaining = chunkLength;
                    while (remaining != 0)
                    {
                        int request = Math.Min(remaining, scratch.Length);
                        ReadFileExactly(file, scratch, 0, request, true);
                        crc = UpdateCrc(crc, scratch, 0, request);
                        remaining -= request;
                    }
                }

                uint storedCrc = ReadUInt32BigEndian(file);
                crc ^= UInt32.MaxValue;
                if (crc != storedCrc)
                {
                    throw new InvalidDataException("A PNG chunk failed its CRC check.");
                }

                if (chunkType == "IHDR")
                {
                    ParseHeader(headerData, result);
                    sawHeader = true;
                }
                else if (chunkType == "tRNS")
                {
                    ParseTrueColorTransparency(transparencyData, result);
                }
                else if (chunkType == "IEND")
                {
                    sawEnd = true;
                    if (file.Position != fileLength)
                    {
                        throw new InvalidDataException("Unexpected data follows the PNG IEND chunk.");
                    }
                }
            }

            if (!sawHeader || !sawIdat || !sawEnd || result.IdatLength < 7)
            {
                throw new InvalidDataException("The PNG does not contain a complete image data stream.");
            }

            return result;
        }

        private static void ParseHeader(byte[] header, ParsedPng result)
        {
            uint width = ReadUInt32BigEndian(header, 0);
            uint height = ReadUInt32BigEndian(header, 4);
            byte bitDepth = header[8];
            byte colorType = header[9];
            byte compressionMethod = header[10];
            byte filterMethod = header[11];
            byte interlaceMethod = header[12];

            if (width == 0 || height == 0)
            {
                throw new InvalidDataException("PNG dimensions must be greater than zero.");
            }

            if (compressionMethod != 0 || filterMethod != 0)
            {
                throw new InvalidDataException("The PNG uses an invalid compression or filter method.");
            }

            if (interlaceMethod > 1)
            {
                throw new InvalidDataException("The PNG uses an invalid interlace method.");
            }

            if (colorType != 0 && colorType != 2 && colorType != 3 &&
                colorType != 4 && colorType != 6)
            {
                throw new InvalidDataException("The PNG uses an invalid color type.");
            }

            if (bitDepth != 8 || (colorType != 2 && colorType != 6) || interlaceMethod != 0)
            {
                throw new UnsupportedPngException();
            }

            if (width > Int32.MaxValue || height > Int32.MaxValue ||
                width > (uint)(Int32.MaxValue / 4))
            {
                throw new UnsupportedPngException();
            }

            result.Width = (int)width;
            result.Height = (int)height;
            result.ColorType = colorType;

            int bytesPerPixel = colorType == 2 ? 3 : 4;
            int rowBytes;
            try
            {
                rowBytes = checked(result.Width * bytesPerPixel);
                checked
                {
                    long ignored = (long)result.Height * (rowBytes + 1L);
                    if (ignored <= 0)
                    {
                        throw new OverflowException();
                    }
                }
            }
            catch (OverflowException)
            {
                throw new UnsupportedPngException();
            }
        }

        private static void ValidateZlibEnvelope(FileStream file, ParsedPng parsed)
        {
            byte[] header = new byte[2];
            ReadIdatBytes(file, parsed.IdatSegments, parsed.IdatLength, 0, header, 0, 2);

            int cmf = header[0];
            int flg = header[1];
            if ((cmf & 15) != 8 || (cmf >> 4) > 7 ||
                (((cmf << 8) | flg) % 31) != 0 || (flg & 32) != 0)
            {
                throw new InvalidDataException("The PNG IDAT stream has an invalid zlib header.");
            }
        }

        private static void ParseTrueColorTransparency(byte[] data, ParsedPng result)
        {
            uint red = ReadUInt16BigEndian(data, 0);
            uint green = ReadUInt16BigEndian(data, 2);
            uint blue = ReadUInt16BigEndian(data, 4);

            if (red > Byte.MaxValue || green > Byte.MaxValue || blue > Byte.MaxValue)
            {
                throw new InvalidDataException("The PNG transparency color exceeds its 8-bit sample range.");
            }

            result.HasTransparentColor = true;
            result.TransparentRed = (byte)red;
            result.TransparentGreen = (byte)green;
            result.TransparentBlue = (byte)blue;
        }

        private static uint ReadExpectedAdler32(FileStream file, ParsedPng parsed)
        {
            byte[] trailer = new byte[4];
            ReadIdatBytes(
                file,
                parsed.IdatSegments,
                parsed.IdatLength,
                parsed.IdatLength - trailer.Length,
                trailer,
                0,
                trailer.Length);
            return ReadUInt32BigEndian(trailer, 0);
        }

        private void ReadInflatedExactly(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read;
                try
                {
                    read = _inflater.Read(buffer, offset + total, count - total);
                }
                catch (IOException exception)
                {
                    throw new InvalidDataException("The PNG deflate stream is corrupt.", exception);
                }

                if (read == 0)
                {
                    throw new InvalidDataException("The PNG image data ends before all scanlines are present.");
                }

                UpdateAdler(buffer, offset + total, read);
                total += read;
            }
        }

        private void FinishImage()
        {
            int extra;
            try
            {
                extra = _inflater.Read(_singleByte, 0, 1);
            }
            catch (IOException exception)
            {
                throw new InvalidDataException("The PNG deflate stream is corrupt.", exception);
            }

            if (extra != 0)
            {
                throw new InvalidDataException("The PNG contains more scanline data than its IHDR declares.");
            }

            uint actualAdler32 = (_adlerB << 16) | _adlerA;
            if (actualAdler32 != _expectedAdler32)
            {
                throw new InvalidDataException("The PNG image data failed its Adler-32 check.");
            }

            _finished = true;
        }

        private void UpdateAdler(byte[] buffer, int offset, int count)
        {
            const uint modulus = 65521;

            // 5552 bytes is the largest conventional block that keeps both
            // accumulators within UInt32 before the modulo reductions.
            while (count != 0)
            {
                int blockLength = Math.Min(count, 5552);
                int blockEnd = offset + blockLength;
                while (offset < blockEnd)
                {
                    _adlerA += buffer[offset++];
                    _adlerB += _adlerA;
                }

                _adlerA %= modulus;
                _adlerB %= modulus;
                count -= blockLength;
            }
        }

        private void ReverseFilter(byte filter)
        {
            int i;
            switch (filter)
            {
                case 0:
                    return;

                case 1:
                    for (i = _bytesPerPixel; i < _sourceRowBytes; i++)
                    {
                        _currentRow[i] = unchecked((byte)(_currentRow[i] + _currentRow[i - _bytesPerPixel]));
                    }
                    return;

                case 2:
                    for (i = 0; i < _sourceRowBytes; i++)
                    {
                        _currentRow[i] = unchecked((byte)(_currentRow[i] + _previousRow[i]));
                    }
                    return;

                case 3:
                    for (i = 0; i < _sourceRowBytes; i++)
                    {
                        int left = i >= _bytesPerPixel ? _currentRow[i - _bytesPerPixel] : 0;
                        int above = _previousRow[i];
                        _currentRow[i] = unchecked((byte)(_currentRow[i] + ((left + above) >> 1)));
                    }
                    return;

                case 4:
                    for (i = 0; i < _sourceRowBytes; i++)
                    {
                        int left = i >= _bytesPerPixel ? _currentRow[i - _bytesPerPixel] : 0;
                        int above = _previousRow[i];
                        int upperLeft = i >= _bytesPerPixel ? _previousRow[i - _bytesPerPixel] : 0;
                        _currentRow[i] = unchecked((byte)(_currentRow[i] + Paeth(left, above, upperLeft)));
                    }
                    return;

                default:
                    throw new InvalidDataException("The PNG scanline uses an unknown filter type.");
            }
        }

        private void CopyCurrentRowToRgba(byte[] destination)
        {
            if (_bytesPerPixel == 4)
            {
                Buffer.BlockCopy(_currentRow, 0, destination, 0, _rgbaRowBytes);
                return;
            }

            int source = 0;
            int target = 0;
            while (source < _sourceRowBytes)
            {
                byte red = _currentRow[source];
                byte green = _currentRow[source + 1];
                byte blue = _currentRow[source + 2];
                destination[target] = red;
                destination[target + 1] = green;
                destination[target + 2] = blue;
                destination[target + 3] = _hasTransparentColor &&
                    red == _transparentRed &&
                    green == _transparentGreen &&
                    blue == _transparentBlue
                    ? (byte)0
                    : (byte)255;
                source += 3;
                target += 4;
            }
        }

        private static int Paeth(int left, int above, int upperLeft)
        {
            int estimate = left + above - upperLeft;
            int distanceLeft = Math.Abs(estimate - left);
            int distanceAbove = Math.Abs(estimate - above);
            int distanceUpperLeft = Math.Abs(estimate - upperLeft);

            if (distanceLeft <= distanceAbove && distanceLeft <= distanceUpperLeft)
            {
                return left;
            }

            if (distanceAbove <= distanceUpperLeft)
            {
                return above;
            }

            return upperLeft;
        }

        private static void ValidateChunkType(byte[] type)
        {
            int i;
            for (i = 0; i < type.Length; i++)
            {
                byte value = type[i];
                bool letter = (value >= (byte)'A' && value <= (byte)'Z') ||
                              (value >= (byte)'a' && value <= (byte)'z');
                if (!letter)
                {
                    throw new InvalidDataException("A PNG chunk has an invalid type code.");
                }
            }

            // The third letter is the PNG reserved bit and must currently be zero
            // (represented by an uppercase ASCII letter).
            if ((type[2] & 32) != 0)
            {
                throw new InvalidDataException("A PNG chunk sets the reserved type bit.");
            }
        }

        private static uint UpdateCrc(uint crc, byte[] buffer, int offset, int count)
        {
            int end = offset + count;
            while (offset < end)
            {
                crc = CrcTable[(crc ^ buffer[offset++]) & 255] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] CreateCrcTable()
        {
            uint[] table = new uint[256];
            int i;
            for (i = 0; i < table.Length; i++)
            {
                uint value = (uint)i;
                int bit;
                for (bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xedb88320U ^ (value >> 1)
                        : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }

        private static uint ReadUInt32BigEndian(Stream stream)
        {
            byte[] bytes = new byte[4];
            ReadFileExactly(stream, bytes, 0, bytes.Length, true);
            return ReadUInt32BigEndian(bytes, 0);
        }

        private static uint ReadUInt32BigEndian(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) |
                   ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }

        private static ushort ReadUInt16BigEndian(byte[] bytes, int offset)
        {
            return (ushort)(((uint)bytes[offset] << 8) | bytes[offset + 1]);
        }

        private static void ReadFileExactly(
            Stream stream,
            byte[] buffer,
            int offset,
            int count,
            bool recognizedPng)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    if (recognizedPng)
                    {
                        throw new InvalidDataException("The PNG file is truncated.");
                    }

                    throw new UnsupportedPngException();
                }

                total += read;
            }
        }

        private static void ReadIdatBytes(
            FileStream file,
            IList<IdatSegment> segments,
            long totalLength,
            long virtualOffset,
            byte[] destination,
            int destinationOffset,
            int count)
        {
            if (virtualOffset < 0 || count < 0 ||
                virtualOffset > totalLength - count)
            {
                throw new InvalidDataException("The PNG IDAT stream is truncated.");
            }

            long skip = virtualOffset;
            int segmentIndex = 0;
            while (segmentIndex < segments.Count && skip >= segments[segmentIndex].Length)
            {
                skip -= segments[segmentIndex].Length;
                segmentIndex++;
            }

            int remaining = count;
            while (remaining != 0)
            {
                if (segmentIndex >= segments.Count)
                {
                    throw new InvalidDataException("The PNG IDAT stream is truncated.");
                }

                IdatSegment segment = segments[segmentIndex];
                long available = segment.Length - skip;
                if (available == 0)
                {
                    segmentIndex++;
                    skip = 0;
                    continue;
                }

                int request = (int)Math.Min(available, remaining);
                file.Position = segment.Offset + skip;
                ReadFileExactly(file, destination, destinationOffset, request, true);

                destinationOffset += request;
                remaining -= request;
                segmentIndex++;
                skip = 0;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("PngRowReader");
            }
        }

        private sealed class ParsedPng
        {
            public readonly List<IdatSegment> IdatSegments = new List<IdatSegment>();
            public int Width;
            public int Height;
            public byte ColorType;
            public long IdatLength;
            public bool HasTransparentColor;
            public byte TransparentRed;
            public byte TransparentGreen;
            public byte TransparentBlue;
        }

        private struct IdatSegment
        {
            public readonly long Offset;
            public readonly long Length;

            public IdatSegment(long offset, long length)
            {
                Offset = offset;
                Length = length;
            }
        }

        /// <summary>
        /// Presents a bounded virtual slice of concatenated IDAT payloads.  It never
        /// owns the file, which lets PngRowReader control the final disposal order.
        /// </summary>
        private sealed class IdatRangeStream : Stream
        {
            private readonly FileStream _file;
            private readonly IList<IdatSegment> _segments;
            private readonly long _length;
            private int _segmentIndex;
            private long _offsetInSegment;
            private long _position;
            private bool _disposed;

            public IdatRangeStream(
                FileStream file,
                IList<IdatSegment> segments,
                long virtualOffset,
                long length)
            {
                if (file == null)
                {
                    throw new ArgumentNullException("file");
                }

                if (segments == null)
                {
                    throw new ArgumentNullException("segments");
                }

                if (virtualOffset < 0 || length <= 0)
                {
                    throw new InvalidDataException("The PNG deflate stream has an invalid length.");
                }

                _file = file;
                _segments = segments;
                _length = length;

                long skip = virtualOffset;
                while (_segmentIndex < _segments.Count &&
                       skip >= _segments[_segmentIndex].Length)
                {
                    skip -= _segments[_segmentIndex].Length;
                    _segmentIndex++;
                }

                if (_segmentIndex >= _segments.Count)
                {
                    throw new InvalidDataException("The PNG deflate stream is missing.");
                }

                _offsetInSegment = skip;
            }

            public override bool CanRead
            {
                get { return !_disposed; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return false; }
            }

            public override long Length
            {
                get
                {
                    ThrowIfStreamDisposed();
                    return _length;
                }
            }

            public override long Position
            {
                get
                {
                    ThrowIfStreamDisposed();
                    return _position;
                }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ThrowIfStreamDisposed();

                if (buffer == null)
                {
                    throw new ArgumentNullException("buffer");
                }

                if (offset < 0 || count < 0 || offset > buffer.Length - count)
                {
                    throw new ArgumentOutOfRangeException();
                }

                long remainingInRange = _length - _position;
                if (remainingInRange <= 0 || count == 0)
                {
                    return 0;
                }

                int wanted = (int)Math.Min(remainingInRange, count);
                int total = 0;

                while (total < wanted)
                {
                    if (_segmentIndex >= _segments.Count)
                    {
                        throw new InvalidDataException("The PNG IDAT stream is truncated.");
                    }

                    IdatSegment segment = _segments[_segmentIndex];
                    long available = segment.Length - _offsetInSegment;
                    if (available == 0)
                    {
                        _segmentIndex++;
                        _offsetInSegment = 0;
                        continue;
                    }

                    int request = (int)Math.Min(available, wanted - total);
                    _file.Position = segment.Offset + _offsetInSegment;
                    int read = _file.Read(buffer, offset + total, request);
                    if (read == 0)
                    {
                        throw new InvalidDataException("The PNG IDAT stream is truncated.");
                    }

                    total += read;
                    _position += read;
                    _offsetInSegment += read;

                    if (_offsetInSegment == segment.Length)
                    {
                        _segmentIndex++;
                        _offsetInSegment = 0;
                    }
                }

                return total;
            }

            public override void Flush()
            {
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
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                _disposed = true;
                base.Dispose(disposing);
            }

            private void ThrowIfStreamDisposed()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException("IdatRangeStream");
                }
            }
        }

        private sealed class UnsupportedPngException : Exception
        {
        }
    }
}
