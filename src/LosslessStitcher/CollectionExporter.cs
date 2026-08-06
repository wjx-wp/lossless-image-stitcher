using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace LosslessStitcher
{
    public static class CollectionExporter
    {
        public static void Export(
            CollectionLayout layout,
            CollectionSettings settings,
            string outputPath,
            Func<bool> isCancelled,
            Action<int, string> progress)
        {
            Validate(layout, settings, outputPath);
            ThrowIfCancelled(isCancelled);
            Report(progress, 0, "正在创建合集画布…");

            Bitmap canvas;
            try
            {
                canvas = new Bitmap(layout.Width, layout.Height, PixelFormat.Format24bppRgb);
            }
            catch (OutOfMemoryException exception)
            {
                throw new InvalidOperationException(
                    "无法分配合集画布内存。请减小单张海报宽度，或拆成多个合集。",
                    exception);
            }

            using (canvas)
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                ConfigureGraphics(graphics);
                graphics.Clear(Color.FromArgb(
                    255,
                    settings.Background.R,
                    settings.Background.G,
                    settings.Background.B));

                int index;
                for (index = 0; index < layout.Items.Count; index++)
                {
                    ThrowIfCancelled(isCancelled);
                    DrawItem(graphics, layout.Items[index], settings);
                    int percent = 3 + (int)(((long)(index + 1) * 87L) / layout.Items.Count);
                    Report(
                        progress,
                        percent,
                        "正在处理第 " + (index + 1) + " / " + layout.Items.Count + " 张…");
                }

                ThrowIfCancelled(isCancelled);
                Report(progress, 93, settings.OutputFormat == CollectionOutputFormat.Jpeg
                    ? "正在进行高质量 JPEG 压缩…"
                    : "正在写入 PNG…");
                SaveCanvas(canvas, settings, outputPath);
            }

            ThrowIfCancelled(isCancelled);
            Report(progress, 100, "合集导出完成");
        }

        private static void DrawItem(
            Graphics graphics,
            CollectionPlacedItem placed,
            CollectionSettings settings)
        {
            Bitmap source = null;
            try
            {
                // The collection intentionally scales each poster down. Loading a
                // target-sized bitmap avoids keeping both a full decoded image and
                // a second full-size ARGB clone beside the collection canvas.
                source = ImageLoader.LoadThumbnail(
                    placed.Item.Path,
                    placed.ImageBox.Width,
                    placed.ImageBox.Height);
                Rectangle destination = FitInside(source.Size, placed.ImageBox);
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(
                        source,
                        destination,
                        0,
                        0,
                        source.Width,
                        source.Height,
                        GraphicsUnit.Pixel,
                        attributes);
                }

                using (Pen border = new Pen(Color.FromArgb(205, 211, 219), 1F))
                {
                    graphics.DrawRectangle(
                        border,
                        destination.X,
                        destination.Y,
                        Math.Max(0, destination.Width - 1),
                        Math.Max(0, destination.Height - 1));
                }
            }
            finally
            {
                if (source != null)
                {
                    source.Dispose();
                }
            }

            DrawCaption(graphics, placed.Item.Caption, placed.LabelBox, settings.FontSize);
        }

        private static Rectangle FitInside(Size source, Rectangle box)
        {
            double scale = Math.Min(
                box.Width / (double)source.Width,
                box.Height / (double)source.Height);
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            return new Rectangle(
                box.X + ((box.Width - width) / 2),
                box.Y + ((box.Height - height) / 2),
                width,
                height);
        }

        private static void DrawCaption(Graphics graphics, string caption, Rectangle labelBox, int preferredSize)
        {
            Rectangle textBox = Rectangle.Inflate(labelBox, -10, -6);
            float minimumSize = Math.Max(12F, preferredSize * 0.62F);
            float fontSize = preferredSize;

            using (StringFormat format = new StringFormat())
            using (Brush textBrush = new SolidBrush(Color.FromArgb(26, 32, 44)))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.LineLimit;

                Font font = null;
                try
                {
                    while (fontSize >= minimumSize)
                    {
                        if (font != null)
                        {
                            font.Dispose();
                        }

                        font = CreateCaptionFont(fontSize);
                        SizeF measured = graphics.MeasureString(
                            caption,
                            font,
                            Math.Max(1, textBox.Width),
                            format);
                        if (measured.Height <= textBox.Height + 1F)
                        {
                            break;
                        }

                        fontSize -= 2F;
                    }

                    graphics.DrawString(caption, font, textBrush, textBox, format);
                }
                finally
                {
                    if (font != null)
                    {
                        font.Dispose();
                    }
                }
            }
        }

        private static Font CreateCaptionFont(float size)
        {
            try
            {
                return new Font("Arial", size, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif, size, FontStyle.Bold, GraphicsUnit.Pixel);
            }
        }

        private static void ConfigureGraphics(Graphics graphics)
        {
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        private static void SaveCanvas(
            Bitmap canvas,
            CollectionSettings settings,
            string outputPath)
        {
            if (settings.OutputFormat == CollectionOutputFormat.Png)
            {
                canvas.Save(outputPath, ImageFormat.Png);
                return;
            }

            ImageCodecInfo jpegCodec = FindEncoder(ImageFormat.Jpeg.Guid);
            if (jpegCodec == null)
            {
                throw new InvalidOperationException("当前系统没有可用的 JPEG 编码器。 ");
            }

            using (EncoderParameters parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality,
                    (long)settings.JpegQuality);
                canvas.Save(outputPath, jpegCodec, parameters);
            }
        }

        private static ImageCodecInfo FindEncoder(Guid formatGuid)
        {
            ImageCodecInfo[] encoders = ImageCodecInfo.GetImageEncoders();
            int index;
            for (index = 0; index < encoders.Length; index++)
            {
                if (encoders[index].FormatID == formatGuid)
                {
                    return encoders[index];
                }
            }

            return null;
        }

        private static void Validate(
            CollectionLayout layout,
            CollectionSettings settings,
            string outputPath)
        {
            if (layout == null)
            {
                throw new ArgumentNullException("layout");
            }

            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            if (String.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("输出路径不能为空。", "outputPath");
            }

            if (layout.Width <= 0 || layout.Height <= 0 ||
                layout.Items == null || layout.Items.Count == 0)
            {
                throw new ArgumentException("合集布局无效。", "layout");
            }
        }

        private static void ThrowIfCancelled(Func<bool> isCancelled)
        {
            if (isCancelled != null && isCancelled())
            {
                throw new OperationCanceledException("合集导出已取消。 ");
            }
        }

        private static void Report(Action<int, string> progress, int percent, string message)
        {
            if (progress != null)
            {
                progress(percent, message);
            }
        }
    }
}
