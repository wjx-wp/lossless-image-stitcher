using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace LosslessStitcher
{
    public static class ImageLoader
    {
        private const int ExifOrientationId = 0x0112;
        private const int MaximumExifMetadataBytes = 16 * 1024 * 1024;

        public static Size ReadDisplaySize(string path)
        {
            ValidatePath(path);
            int orientation = ReadExifOrientation(path);

            using (FileStream stream = OpenRead(path))
            using (Image image = Image.FromStream(stream, false, false))
            {
                if (SwapsDimensions(orientation))
                {
                    return new Size(image.Height, image.Width);
                }

                return new Size(image.Width, image.Height);
            }
        }

        public static Bitmap LoadThumbnail(string path, int maxWidth, int maxHeight)
        {
            ValidatePath(path);
            if (maxWidth <= 0)
            {
                throw new ArgumentOutOfRangeException("maxWidth", "Maximum width must be positive.");
            }

            if (maxHeight <= 0)
            {
                throw new ArgumentOutOfRangeException("maxHeight", "Maximum height must be positive.");
            }

            int orientation = ReadExifOrientation(path);
            using (FileStream stream = OpenRead(path))
            using (Image source = Image.FromStream(stream, false, false))
            {
                ApplyExifOrientation(source, orientation);
                Size targetSize = CalculateThumbnailSize(
                    source.Width,
                    source.Height,
                    maxWidth,
                    maxHeight);

                Bitmap thumbnail = new Bitmap(
                    targetSize.Width,
                    targetSize.Height,
                    PixelFormat.Format32bppArgb);

                try
                {
                    using (Graphics graphics = Graphics.FromImage(thumbnail))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.DrawImage(
                            source,
                            new Rectangle(0, 0, targetSize.Width, targetSize.Height),
                            0,
                            0,
                            source.Width,
                            source.Height,
                            GraphicsUnit.Pixel);
                    }

                    return thumbnail;
                }
                catch
                {
                    thumbnail.Dispose();
                    throw;
                }
            }
        }

        public static Bitmap LoadNormalizedArgb(string path)
        {
            ValidatePath(path);
            int orientation = ReadExifOrientation(path);

            using (FileStream stream = OpenRead(path))
            using (Image source = Image.FromStream(stream, false, false))
            {
                ApplyExifOrientation(source, orientation);
                Bitmap normalized = new Bitmap(
                    source.Width,
                    source.Height,
                    PixelFormat.Format32bppArgb);

                try
                {
                    using (Graphics graphics = Graphics.FromImage(normalized))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.DrawImage(
                            source,
                            new Rectangle(0, 0, source.Width, source.Height),
                            0,
                            0,
                            source.Width,
                            source.Height,
                            GraphicsUnit.Pixel);
                    }

                    return normalized;
                }
                catch
                {
                    normalized.Dispose();
                    throw;
                }
            }
        }

        internal static int ReadExifOrientation(string path)
        {
            ValidatePath(path);

            bool recognizedContainer;
            int orientation;
            using (FileStream stream = OpenRead(path))
            {
                orientation = ReadContainerOrientation(stream, out recognizedContainer);
                if (orientation >= 1 && orientation <= 8)
                {
                    return orientation;
                }

                if (recognizedContainer)
                {
                    return 1;
                }
            }

            using (FileStream stream = OpenRead(path))
            using (Image image = Image.FromStream(stream, false, false))
            {
                orientation = ReadImageOrientation(image);
                return orientation >= 1 && orientation <= 8 ? orientation : 1;
            }
        }

        private static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        private static void ValidatePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An image path is required.", "path");
            }
        }

        private static int ReadImageOrientation(Image image)
        {
            int[] propertyIds = image.PropertyIdList;
            bool found = false;
            for (int index = 0; index < propertyIds.Length; index++)
            {
                if (propertyIds[index] == ExifOrientationId)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return 1;
            }

            try
            {
                PropertyItem property = image.GetPropertyItem(ExifOrientationId);
                byte[] value = property.Value;
                if (value == null || value.Length == 0)
                {
                    return 1;
                }

                if (value.Length >= 2)
                {
                    int littleEndian = value[0] | (value[1] << 8);
                    if (littleEndian >= 1 && littleEndian <= 8)
                    {
                        return littleEndian;
                    }

                    int bigEndian = (value[0] << 8) | value[1];
                    if (bigEndian >= 1 && bigEndian <= 8)
                    {
                        return bigEndian;
                    }
                }

                return value[0] >= 1 && value[0] <= 8 ? value[0] : 1;
            }
            catch (ArgumentException)
            {
                return 1;
            }
        }

        private static int ReadContainerOrientation(FileStream stream, out bool recognizedContainer)
        {
            recognizedContainer = false;
            if (stream.Length < 2)
            {
                return 0;
            }

            byte[] signature = new byte[8];
            int signatureLength = stream.Read(signature, 0, signature.Length);
            stream.Position = 0;

            if (signatureLength == 8 &&
                signature[0] == 0x89 && signature[1] == 0x50 &&
                signature[2] == 0x4E && signature[3] == 0x47 &&
                signature[4] == 0x0D && signature[5] == 0x0A &&
                signature[6] == 0x1A && signature[7] == 0x0A)
            {
                recognizedContainer = true;
                return ReadPngOrientation(stream);
            }

            if (signatureLength >= 2 && signature[0] == 0xFF && signature[1] == 0xD8)
            {
                recognizedContainer = true;
                return ReadJpegOrientation(stream);
            }

            return 0;
        }

        private static int ReadPngOrientation(FileStream stream)
        {
            stream.Position = 8;
            byte[] chunkType = new byte[4];

            while (stream.Position <= stream.Length - 12)
            {
                uint chunkLength;
                if (!TryReadUInt32BigEndian(stream, out chunkLength) ||
                    !ReadExactly(stream, chunkType, 0, chunkType.Length))
                {
                    return 0;
                }

                long dataLength = chunkLength;
                long remaining = stream.Length - stream.Position;
                if (dataLength > remaining - 4L)
                {
                    return 0;
                }

                bool isExif =
                    chunkType[0] == (byte)'e' && chunkType[1] == (byte)'X' &&
                    chunkType[2] == (byte)'I' && chunkType[3] == (byte)'f';

                if (isExif && dataLength <= MaximumExifMetadataBytes)
                {
                    byte[] metadata = new byte[(int)dataLength];
                    if (!ReadExactly(stream, metadata, 0, metadata.Length))
                    {
                        return 0;
                    }

                    int orientation = ReadTiffOrientation(metadata);
                    if (orientation >= 1 && orientation <= 8)
                    {
                        return orientation;
                    }

                    stream.Position += 4L;
                }
                else
                {
                    stream.Position += dataLength + 4L;
                }

                bool isEnd =
                    chunkType[0] == (byte)'I' && chunkType[1] == (byte)'E' &&
                    chunkType[2] == (byte)'N' && chunkType[3] == (byte)'D';
                if (isEnd)
                {
                    break;
                }
            }

            return 0;
        }

        private static int ReadJpegOrientation(FileStream stream)
        {
            stream.Position = 2;

            while (stream.Position < stream.Length)
            {
                int prefix = stream.ReadByte();
                if (prefix < 0)
                {
                    return 0;
                }

                if (prefix != 0xFF)
                {
                    continue;
                }

                int marker;
                do
                {
                    marker = stream.ReadByte();
                }
                while (marker == 0xFF);

                if (marker < 0 || marker == 0xD9 || marker == 0xDA)
                {
                    return 0;
                }

                if (marker == 0x00 || marker == 0x01 ||
                    (marker >= 0xD0 && marker <= 0xD8))
                {
                    continue;
                }

                int lengthHigh = stream.ReadByte();
                int lengthLow = stream.ReadByte();
                if (lengthHigh < 0 || lengthLow < 0)
                {
                    return 0;
                }

                int segmentLength = (lengthHigh << 8) | lengthLow;
                if (segmentLength < 2)
                {
                    return 0;
                }

                int dataLength = segmentLength - 2;
                if (dataLength > stream.Length - stream.Position)
                {
                    return 0;
                }

                if (marker == 0xE1)
                {
                    byte[] metadata = new byte[dataLength];
                    if (!ReadExactly(stream, metadata, 0, metadata.Length))
                    {
                        return 0;
                    }

                    int orientation = ReadTiffOrientation(metadata);
                    if (orientation >= 1 && orientation <= 8)
                    {
                        return orientation;
                    }
                }
                else
                {
                    stream.Position += dataLength;
                }
            }

            return 0;
        }

        private static int ReadTiffOrientation(byte[] metadata)
        {
            int tiffStart = 0;
            if (metadata.Length >= 6 &&
                metadata[0] == (byte)'E' && metadata[1] == (byte)'x' &&
                metadata[2] == (byte)'i' && metadata[3] == (byte)'f' &&
                metadata[4] == 0 && metadata[5] == 0)
            {
                tiffStart = 6;
            }

            if (metadata.Length - tiffStart < 8)
            {
                return 0;
            }

            bool littleEndian;
            if (metadata[tiffStart] == (byte)'I' && metadata[tiffStart + 1] == (byte)'I')
            {
                littleEndian = true;
            }
            else if (metadata[tiffStart] == (byte)'M' && metadata[tiffStart + 1] == (byte)'M')
            {
                littleEndian = false;
            }
            else
            {
                return 0;
            }

            if (ReadUInt16(metadata, tiffStart + 2, littleEndian) != 42)
            {
                return 0;
            }

            uint relativeIfdOffset = ReadUInt32(metadata, tiffStart + 4, littleEndian);
            long ifdOffsetLong = (long)tiffStart + relativeIfdOffset;
            if (ifdOffsetLong < 0 || ifdOffsetLong > metadata.Length - 2)
            {
                return 0;
            }

            int ifdOffset = (int)ifdOffsetLong;
            int entryCount = ReadUInt16(metadata, ifdOffset, littleEndian);
            int entriesStart = ifdOffset + 2;

            for (int index = 0; index < entryCount; index++)
            {
                long entryOffsetLong = (long)entriesStart + ((long)index * 12L);
                if (entryOffsetLong > metadata.Length - 12)
                {
                    return 0;
                }

                int entryOffset = (int)entryOffsetLong;
                int tag = ReadUInt16(metadata, entryOffset, littleEndian);
                if (tag != ExifOrientationId)
                {
                    continue;
                }

                int type = ReadUInt16(metadata, entryOffset + 2, littleEndian);
                uint count = ReadUInt32(metadata, entryOffset + 4, littleEndian);
                if (type != 3 || count == 0)
                {
                    return 0;
                }

                int value;
                if (count == 1)
                {
                    value = ReadUInt16(metadata, entryOffset + 8, littleEndian);
                }
                else
                {
                    uint relativeValueOffset = ReadUInt32(metadata, entryOffset + 8, littleEndian);
                    long valueOffsetLong = (long)tiffStart + relativeValueOffset;
                    if (valueOffsetLong < 0 || valueOffsetLong > metadata.Length - 2)
                    {
                        return 0;
                    }

                    value = ReadUInt16(metadata, (int)valueOffsetLong, littleEndian);
                }

                return value >= 1 && value <= 8 ? value : 0;
            }

            return 0;
        }

        private static ushort ReadUInt16(byte[] data, int offset, bool littleEndian)
        {
            if (littleEndian)
            {
                return (ushort)(data[offset] | (data[offset + 1] << 8));
            }

            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static uint ReadUInt32(byte[] data, int offset, bool littleEndian)
        {
            if (littleEndian)
            {
                return (uint)(
                    data[offset] |
                    (data[offset + 1] << 8) |
                    (data[offset + 2] << 16) |
                    (data[offset + 3] << 24));
            }

            return (uint)(
                (data[offset] << 24) |
                (data[offset + 1] << 16) |
                (data[offset + 2] << 8) |
                data[offset + 3]);
        }

        private static bool TryReadUInt32BigEndian(Stream stream, out uint value)
        {
            int first = stream.ReadByte();
            int second = stream.ReadByte();
            int third = stream.ReadByte();
            int fourth = stream.ReadByte();
            if (first < 0 || second < 0 || third < 0 || fourth < 0)
            {
                value = 0;
                return false;
            }

            value = ((uint)first << 24) |
                    ((uint)second << 16) |
                    ((uint)third << 8) |
                    (uint)fourth;
            return true;
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
                count -= read;
            }

            return true;
        }

        private static bool SwapsDimensions(int orientation)
        {
            return orientation >= 5 && orientation <= 8;
        }

        private static void ApplyExifOrientation(Image image, int orientation)
        {
            switch (orientation)
            {
                case 2:
                    image.RotateFlip(RotateFlipType.RotateNoneFlipX);
                    break;
                case 3:
                    image.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    break;
                case 4:
                    image.RotateFlip(RotateFlipType.Rotate180FlipX);
                    break;
                case 5:
                    image.RotateFlip(RotateFlipType.Rotate90FlipX);
                    break;
                case 6:
                    image.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    break;
                case 7:
                    image.RotateFlip(RotateFlipType.Rotate270FlipX);
                    break;
                case 8:
                    image.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    break;
            }
        }

        private static Size CalculateThumbnailSize(
            int sourceWidth,
            int sourceHeight,
            int maxWidth,
            int maxHeight)
        {
            if (sourceWidth <= maxWidth && sourceHeight <= maxHeight)
            {
                return new Size(sourceWidth, sourceHeight);
            }

            long widthLimitedHeight = (long)maxWidth * sourceHeight;
            long heightLimitedWidth = (long)maxHeight * sourceWidth;

            if (widthLimitedHeight <= heightLimitedWidth)
            {
                int targetWidth = Math.Min(sourceWidth, maxWidth);
                long roundedHeight =
                    ((long)sourceHeight * targetWidth) + (sourceWidth / 2L);
                int targetHeight = (int)Math.Max(1L, roundedHeight / sourceWidth);
                targetHeight = Math.Min(targetHeight, maxHeight);
                return new Size(targetWidth, targetHeight);
            }
            else
            {
                int targetHeight = Math.Min(sourceHeight, maxHeight);
                long roundedWidth =
                    ((long)sourceWidth * targetHeight) + (sourceHeight / 2L);
                int targetWidth = (int)Math.Max(1L, roundedWidth / sourceHeight);
                targetWidth = Math.Min(targetWidth, maxWidth);
                return new Size(targetWidth, targetHeight);
            }
        }
    }
}
