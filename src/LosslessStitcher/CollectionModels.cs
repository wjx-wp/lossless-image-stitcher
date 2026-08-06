using System;
using System.Collections.Generic;
using System.Drawing;

namespace LosslessStitcher
{
    public enum CollectionOutputFormat
    {
        Jpeg,
        Png
    }

    public sealed class CollectionItem
    {
        public string Path;
        public string Caption;
        public int Width;
        public int Height;
    }

    public sealed class CollectionSettings
    {
        // Zero means that the layout calculator chooses a balanced column count.
        public int Columns;
        public int PosterWidth;
        public int Gap;
        public int Margin;
        public int FontSize;
        public int JpegQuality;
        public Color Background;
        public CollectionOutputFormat OutputFormat;
    }

    public sealed class CollectionPlacedItem
    {
        public CollectionItem Item;
        public Rectangle ImageBox;
        public Rectangle LabelBox;
    }

    public sealed class CollectionLayout
    {
        public int Width;
        public int Height;
        public int Rows;
        public int Columns;
        public List<CollectionPlacedItem> Items;
        public long PixelCount;
    }

    public static class CollectionLayoutCalculator
    {
        private const int MaximumDimension = 65000;
        private const long MaximumPixelCount = 250000000L;

        public static CollectionLayout Calculate(
            IList<CollectionItem> items,
            CollectionSettings settings)
        {
            if (items == null)
            {
                throw new ArgumentNullException("items");
            }

            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            if (items.Count == 0)
            {
                throw new ArgumentException("至少需要一张图片。", "items");
            }

            ValidateSettings(settings);
            List<double> ratios = new List<double>(items.Count);
            int index;
            for (index = 0; index < items.Count; index++)
            {
                CollectionItem item = items[index];
                if (item == null || String.IsNullOrWhiteSpace(item.Path))
                {
                    throw new ArgumentException("第 " + (index + 1) + " 张图片无效。", "items");
                }

                if (item.Width <= 0 || item.Height <= 0)
                {
                    throw new ArgumentException("第 " + (index + 1) + " 张图片尺寸无效。", "items");
                }

                if (String.IsNullOrWhiteSpace(item.Caption))
                {
                    throw new ArgumentException("第 " + (index + 1) + " 张图片的显示名称为空。", "items");
                }

                ratios.Add(item.Height / (double)item.Width);
            }

            ratios.Sort();
            double medianRatio;
            if ((ratios.Count & 1) == 0)
            {
                medianRatio = (ratios[(ratios.Count / 2) - 1] + ratios[ratios.Count / 2]) / 2D;
            }
            else
            {
                medianRatio = ratios[ratios.Count / 2];
            }

            medianRatio = Math.Max(0.75D, Math.Min(2.2D, medianRatio));
            int imageHeight = Math.Max(1, (int)Math.Round(settings.PosterWidth * medianRatio));
            int labelHeight = Math.Max(56, checked((settings.FontSize * 2) + 24));
            int cellHeight = checked(imageHeight + labelHeight);
            int columns = settings.Columns > 0
                ? Math.Min(settings.Columns, items.Count)
                : ChooseAutomaticColumns(items.Count, settings, cellHeight);
            int rows = (items.Count + columns - 1) / columns;

            long widthLong = checked(
                ((long)settings.Margin * 2L) +
                ((long)columns * settings.PosterWidth) +
                ((long)Math.Max(0, columns - 1) * settings.Gap));
            long heightLong = checked(
                ((long)settings.Margin * 2L) +
                ((long)rows * cellHeight) +
                ((long)Math.Max(0, rows - 1) * settings.Gap));
            ValidateOutputSize(widthLong, heightLong);

            int width = (int)widthLong;
            int height = (int)heightLong;
            List<CollectionPlacedItem> placements = new List<CollectionPlacedItem>(items.Count);
            int itemIndex = 0;
            int row;
            for (row = 0; row < rows; row++)
            {
                int itemsInRow = Math.Min(columns, items.Count - itemIndex);
                long rowWidth = checked(
                    ((long)itemsInRow * settings.PosterWidth) +
                    ((long)Math.Max(0, itemsInRow - 1) * settings.Gap));
                int rowStartX = (int)((widthLong - rowWidth) / 2L);
                int y = checked(settings.Margin + (row * (cellHeight + settings.Gap)));
                int column;
                for (column = 0; column < itemsInRow; column++)
                {
                    int x = checked(rowStartX + (column * (settings.PosterWidth + settings.Gap)));
                    placements.Add(new CollectionPlacedItem
                    {
                        Item = items[itemIndex++],
                        ImageBox = new Rectangle(x, y, settings.PosterWidth, imageHeight),
                        LabelBox = new Rectangle(x, checked(y + imageHeight), settings.PosterWidth, labelHeight)
                    });
                }
            }

            return new CollectionLayout
            {
                Width = width,
                Height = height,
                Rows = rows,
                Columns = columns,
                Items = placements,
                PixelCount = checked((long)width * height)
            };
        }

        private static int ChooseAutomaticColumns(
            int itemCount,
            CollectionSettings settings,
            int cellHeight)
        {
            // Larger batches need more than twelve columns to avoid a very tall
            // sheet. The score still chooses a small number for normal batches.
            int maximumColumns = Math.Min(itemCount, 40);
            int bestColumns = 1;
            double bestScore = Double.MaxValue;
            int bestEmpty = Int32.MaxValue;

            int columns;
            for (columns = 1; columns <= maximumColumns; columns++)
            {
                int rows = (itemCount + columns - 1) / columns;
                int slots = rows * columns;
                int empty = slots - itemCount;
                double width = (settings.Margin * 2D) +
                    (columns * settings.PosterWidth) +
                    (Math.Max(0, columns - 1) * settings.Gap);
                double height = (settings.Margin * 2D) +
                    (rows * cellHeight) +
                    (Math.Max(0, rows - 1) * settings.Gap);
                double aspect = width / height;
                double emptyRate = empty / (double)slots;
                int lastRowCount = itemCount - ((rows - 1) * columns);

                // Empty slots are weighted heavily so clean factor layouts win
                // unless they would make the finished sheet extremely elongated.
                double score = Math.Abs(Math.Log(aspect)) + (4.0D * emptyRate);
                if (lastRowCount == 1 && rows > 1)
                {
                    score += 0.15D;
                }

                if (aspect > 1.6D || aspect < 0.5D)
                {
                    score += 0.25D;
                }

                if (score < bestScore - 0.000001D ||
                    (Math.Abs(score - bestScore) <= 0.000001D && empty < bestEmpty))
                {
                    bestScore = score;
                    bestColumns = columns;
                    bestEmpty = empty;
                }
            }

            return bestColumns;
        }

        private static void ValidateSettings(CollectionSettings settings)
        {
            if (settings.Columns < 0 || settings.Columns > 1000)
            {
                throw new ArgumentOutOfRangeException("settings.Columns", "列数必须为自动或正整数。 ");
            }

            if (settings.PosterWidth < 100 || settings.PosterWidth > 10000)
            {
                throw new ArgumentOutOfRangeException("settings.PosterWidth", "单张海报宽度必须在 100 到 10000 像素之间。 ");
            }

            if (settings.Gap < 0 || settings.Margin < 0)
            {
                throw new ArgumentOutOfRangeException("settings", "间距和边距不能为负数。 ");
            }

            if (settings.FontSize < 6 || settings.FontSize > 500)
            {
                throw new ArgumentOutOfRangeException("settings.FontSize", "国家名字号超出支持范围。 ");
            }

            if (settings.JpegQuality < 1 || settings.JpegQuality > 100)
            {
                throw new ArgumentOutOfRangeException("settings.JpegQuality", "JPEG 质量必须在 1 到 100 之间。 ");
            }

            if (settings.OutputFormat != CollectionOutputFormat.Jpeg &&
                settings.OutputFormat != CollectionOutputFormat.Png)
            {
                throw new ArgumentOutOfRangeException("settings.OutputFormat");
            }
        }

        private static void ValidateOutputSize(long width, long height)
        {
            if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension)
            {
                throw new InvalidOperationException(
                    "合集尺寸过大（单边不能超过 " + MaximumDimension + " 像素）。请减小单张海报宽度或增加列数。 ");
            }

            long pixels = checked(width * height);
            if (pixels > MaximumPixelCount)
            {
                throw new InvalidOperationException(
                    "合集展开后超过 2.5 亿像素。请减小单张海报宽度，或拆成多个合集。 ");
            }
        }
    }
}
