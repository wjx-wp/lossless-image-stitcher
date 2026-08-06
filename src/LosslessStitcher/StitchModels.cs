using System;
using System.Collections.Generic;
using System.Drawing;

namespace LosslessStitcher
{
    public enum StitchDirection
    {
        Vertical,
        Horizontal
    }

    public enum StitchAlignment
    {
        Start,
        Center,
        End
    }

    public sealed class StitchSource
    {
        public string Path;
        public int Width;
        public int Height;
    }

    public sealed class StitchSettings
    {
        public StitchDirection Direction;
        public StitchAlignment Alignment;
        public int Spacing;
        public int Margin;
        public Color Background;
        public bool TransparentBackground;
    }

    public sealed class PlacedImage
    {
        public StitchSource Source;
        public int X;
        public int Y;
    }

    public sealed class StitchLayout
    {
        public int Width;
        public int Height;
        public List<PlacedImage> Images;
        public long PixelCount;
    }

    public static class LayoutCalculator
    {
        public static StitchLayout Calculate(IList<StitchSource> sources, StitchSettings settings)
        {
            if (sources == null)
            {
                throw new ArgumentNullException("sources");
            }

            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            if (sources.Count == 0)
            {
                throw new ArgumentException("At least one source image is required.", "sources");
            }

            ValidateSettings(settings);

            long crossSize = 0;
            long alongSize = 0;

            checked
            {
                for (int index = 0; index < sources.Count; index++)
                {
                    StitchSource source = sources[index];
                    ValidateSource(source, index);

                    long sourceCross;
                    long sourceAlong;
                    if (settings.Direction == StitchDirection.Vertical)
                    {
                        sourceCross = source.Width;
                        sourceAlong = source.Height;
                    }
                    else
                    {
                        sourceCross = source.Height;
                        sourceAlong = source.Width;
                    }

                    crossSize = Math.Max(crossSize, sourceCross);
                    alongSize += sourceAlong;
                }

                alongSize += (long)settings.Spacing * (sources.Count - 1);
            }

            long marginTwice;
            long totalWidth;
            long totalHeight;
            checked
            {
                marginTwice = (long)settings.Margin * 2L;
                if (settings.Direction == StitchDirection.Vertical)
                {
                    totalWidth = crossSize + marginTwice;
                    totalHeight = alongSize + marginTwice;
                }
                else
                {
                    totalWidth = alongSize + marginTwice;
                    totalHeight = crossSize + marginTwice;
                }
            }

            int width = ToValidDimension(totalWidth, "width");
            int height = ToValidDimension(totalHeight, "height");
            List<PlacedImage> placements = new List<PlacedImage>(sources.Count);
            long cursor = settings.Margin;

            for (int index = 0; index < sources.Count; index++)
            {
                StitchSource source = sources[index];
                long x;
                long y;

                if (settings.Direction == StitchDirection.Vertical)
                {
                    x = GetAlignedPosition(settings.Margin, crossSize, source.Width, settings.Alignment);
                    y = cursor;
                    cursor = checked(cursor + source.Height);
                }
                else
                {
                    x = cursor;
                    y = GetAlignedPosition(settings.Margin, crossSize, source.Height, settings.Alignment);
                    cursor = checked(cursor + source.Width);
                }

                placements.Add(new PlacedImage
                {
                    Source = source,
                    X = ToValidCoordinate(x, "x"),
                    Y = ToValidCoordinate(y, "y")
                });

                if (index + 1 < sources.Count)
                {
                    cursor = checked(cursor + settings.Spacing);
                }
            }

            return new StitchLayout
            {
                Width = width,
                Height = height,
                Images = placements,
                PixelCount = checked((long)width * height)
            };
        }

        private static void ValidateSettings(StitchSettings settings)
        {
            if (settings.Direction != StitchDirection.Vertical &&
                settings.Direction != StitchDirection.Horizontal)
            {
                throw new ArgumentOutOfRangeException("settings.Direction");
            }

            if (settings.Alignment != StitchAlignment.Start &&
                settings.Alignment != StitchAlignment.Center &&
                settings.Alignment != StitchAlignment.End)
            {
                throw new ArgumentOutOfRangeException("settings.Alignment");
            }

            if (settings.Spacing < 0)
            {
                throw new ArgumentOutOfRangeException("settings.Spacing", "Spacing cannot be negative.");
            }

            if (settings.Margin < 0)
            {
                throw new ArgumentOutOfRangeException("settings.Margin", "Margin cannot be negative.");
            }
        }

        private static void ValidateSource(StitchSource source, int index)
        {
            if (source == null)
            {
                throw new ArgumentException("Source image at index " + index + " is null.", "sources");
            }

            if (source.Width <= 0 || source.Height <= 0)
            {
                throw new ArgumentException(
                    "Source image at index " + index + " has invalid dimensions.",
                    "sources");
            }
        }

        private static long GetAlignedPosition(
            long margin,
            long availableSize,
            long itemSize,
            StitchAlignment alignment)
        {
            long remainder = availableSize - itemSize;
            switch (alignment)
            {
                case StitchAlignment.Start:
                    return margin;
                case StitchAlignment.Center:
                    return checked(margin + (remainder / 2L));
                case StitchAlignment.End:
                    return checked(margin + remainder);
                default:
                    throw new ArgumentOutOfRangeException("alignment");
            }
        }

        private static int ToValidDimension(long value, string name)
        {
            if (value <= 0 || value > int.MaxValue)
            {
                throw new OverflowException("The calculated " + name + " is outside the supported range.");
            }

            return (int)value;
        }

        private static int ToValidCoordinate(long value, string name)
        {
            if (value < 0 || value > int.MaxValue)
            {
                throw new OverflowException("The calculated " + name + " coordinate is outside the supported range.");
            }

            return (int)value;
        }
    }
}
