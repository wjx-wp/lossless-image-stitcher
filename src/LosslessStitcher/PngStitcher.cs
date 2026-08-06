using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LosslessStitcher
{
    public static class PngStitcher
    {
        public static void Export(
            StitchLayout layout,
            StitchSettings settings,
            string outputPath,
            Func<bool> isCancelled,
            Action<int, string> progress)
        {
            ValidateArguments(layout, settings, outputPath);

            SourceState[] states = CreateStates(layout);
            byte[] outputRow = new byte[checked(layout.Width * 4)];
            int lastProgress = -1;

            try
            {
                ThrowIfCancelled(isCancelled);
                Report(progress, 0, "正在准备导出...");
                lastProgress = 0;

                using (PngStreamWriter writer = new PngStreamWriter(outputPath, layout.Width, layout.Height))
                {
                    int y;
                    for (y = 0; y < layout.Height; y++)
                    {
                        ThrowIfCancelled(isCancelled);
                        FillBackground(outputRow, settings);

                        int i;
                        for (i = 0; i < states.Length; i++)
                        {
                            SourceState state = states[i];
                            PlacedImage placed = state.Placed;
                            int sourceY = y - placed.Y;

                            if (sourceY < 0 || sourceY >= placed.Source.Height)
                            {
                                continue;
                            }

                            if (state.Reader == null)
                            {
                                if (sourceY != 0)
                                {
                                    throw new InvalidDataException("源图行顺序无效：" + placed.Source.Path);
                                }

                                state.Reader = OpenSource(placed.Source);
                                state.Row = new byte[state.RowByteCount];
                            }

                            state.Reader.ReadNextRgba(state.Row);
                            Buffer.BlockCopy(
                                state.Row,
                                0,
                                outputRow,
                                checked(placed.X * 4),
                                state.Row.Length);

                            if (sourceY == placed.Source.Height - 1)
                            {
                                ISourceRowReader finishedReader = state.Reader;
                                state.Reader = null;
                                state.Row = null;
                                finishedReader.Dispose();
                            }
                        }

                        writer.WriteRgbaRow(outputRow);

                        int currentProgress = (int)(((long)(y + 1) * 99L) / layout.Height);
                        if (currentProgress > lastProgress)
                        {
                            Report(progress, currentProgress, "正在写入 PNG...");
                            lastProgress = currentProgress;
                        }
                    }

                    ThrowIfCancelled(isCancelled);
                    writer.Complete();
                }

                Report(progress, 100, "导出完成");
            }
            finally
            {
                DisposeStates(states);
            }
        }

        private static SourceState[] CreateStates(StitchLayout layout)
        {
            SourceState[] result = new SourceState[layout.Images.Count];
            int i;

            for (i = 0; i < layout.Images.Count; i++)
            {
                PlacedImage placed = layout.Images[i];
                if (placed == null || placed.Source == null)
                {
                    throw new ArgumentException("布局中包含空的图片项。", "layout");
                }

                StitchSource source = placed.Source;
                if (String.IsNullOrWhiteSpace(source.Path))
                {
                    throw new ArgumentException("源图片路径不能为空。", "layout");
                }

                if (source.Width <= 0 || source.Height <= 0)
                {
                    throw new ArgumentException("源图片尺寸必须大于零：" + source.Path, "layout");
                }

                long right = (long)placed.X + source.Width;
                long bottom = (long)placed.Y + source.Height;
                if (placed.X < 0 || placed.Y < 0 || right > layout.Width || bottom > layout.Height)
                {
                    throw new ArgumentException("源图片超出输出画布：" + source.Path, "layout");
                }

                int rowBytes;
                try
                {
                    rowBytes = checked(source.Width * 4);
                }
                catch (OverflowException exception)
                {
                    throw new ArgumentException("源图片单行过宽，无法分配行缓冲区：" + source.Path, "layout", exception);
                }

                result[i] = new SourceState(placed, rowBytes);
            }

            return result;
        }

        private static ISourceRowReader OpenSource(StitchSource source)
        {
            int orientation = ImageLoader.ReadExifOrientation(source.Path);

            if (orientation == 1)
            {
                PngRowReader pngReader;
                if (PngRowReader.TryOpen(source.Path, out pngReader))
                {
                    if (pngReader.Width == source.Width && pngReader.Height == source.Height)
                    {
                        return new DirectPngSource(pngReader);
                    }

                    pngReader.Dispose();
                }
            }

            return new GdiSource(source);
        }

        private static void FillBackground(byte[] row, StitchSettings settings)
        {
            byte red;
            byte green;
            byte blue;
            byte alpha;

            if (settings.TransparentBackground)
            {
                red = 0;
                green = 0;
                blue = 0;
                alpha = 0;
            }
            else
            {
                red = settings.Background.R;
                green = settings.Background.G;
                blue = settings.Background.B;
                alpha = settings.Background.A;
            }

            int i;
            for (i = 0; i < row.Length; i += 4)
            {
                row[i] = red;
                row[i + 1] = green;
                row[i + 2] = blue;
                row[i + 3] = alpha;
            }
        }

        private static void ValidateArguments(StitchLayout layout, StitchSettings settings, string outputPath)
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

            if (layout.Width <= 0 || layout.Height <= 0)
            {
                throw new ArgumentException("输出尺寸必须大于零。", "layout");
            }

            if (layout.Width > Int32.MaxValue / 4)
            {
                throw new ArgumentException("输出宽度过大，无法分配行缓冲区。", "layout");
            }

            if (layout.Images == null || layout.Images.Count == 0)
            {
                throw new ArgumentException("布局中至少需要一张图片。", "layout");
            }

        }

        private static void ThrowIfCancelled(Func<bool> isCancelled)
        {
            if (isCancelled != null && isCancelled())
            {
                throw new OperationCanceledException("导出已取消。");
            }
        }

        private static void Report(Action<int, string> progress, int percent, string message)
        {
            if (progress != null)
            {
                progress(percent, message);
            }
        }

        private static void DisposeStates(SourceState[] states)
        {
            if (states == null)
            {
                return;
            }

            int i;
            for (i = 0; i < states.Length; i++)
            {
                SourceState state = states[i];
                if (state == null)
                {
                    continue;
                }

                ISourceRowReader reader = state.Reader;
                state.Reader = null;
                state.Row = null;

                if (reader != null)
                {
                    try
                    {
                        reader.Dispose();
                    }
                    catch
                    {
                        // Cleanup must not hide the cancellation, decoding, or
                        // output exception that caused this finally block.
                    }
                }
            }
        }

        private interface ISourceRowReader : IDisposable
        {
            void ReadNextRgba(byte[] destination);
        }

        private sealed class SourceState
        {
            public readonly PlacedImage Placed;
            public readonly int RowByteCount;
            public byte[] Row;
            public ISourceRowReader Reader;

            public SourceState(PlacedImage placed, int rowByteCount)
            {
                Placed = placed;
                RowByteCount = rowByteCount;
            }
        }

        private sealed class DirectPngSource : ISourceRowReader
        {
            private PngRowReader _reader;

            public DirectPngSource(PngRowReader reader)
            {
                if (reader == null)
                {
                    throw new ArgumentNullException("reader");
                }

                _reader = reader;
            }

            public void ReadNextRgba(byte[] destination)
            {
                if (_reader == null)
                {
                    throw new ObjectDisposedException("DirectPngSource");
                }

                _reader.ReadNextRgba(destination);
            }

            public void Dispose()
            {
                if (_reader != null)
                {
                    PngRowReader reader = _reader;
                    _reader = null;
                    reader.Dispose();
                }
            }
        }

        private sealed class GdiSource : ISourceRowReader
        {
            private Bitmap _bitmap;
            private readonly byte[] _bgraRow;
            private int _nextRow;

            public GdiSource(StitchSource source)
            {
                Bitmap bitmap = null;

                try
                {
                    bitmap = ImageLoader.LoadNormalizedArgb(source.Path);
                    if (bitmap.Width != source.Width || bitmap.Height != source.Height)
                    {
                        throw new InvalidDataException("源图片显示尺寸已变化，请重新添加：" + source.Path);
                    }

                    _bgraRow = new byte[checked(source.Width * 4)];
                    _bitmap = bitmap;
                    bitmap = null;
                }
                finally
                {
                    if (bitmap != null)
                    {
                        bitmap.Dispose();
                    }
                }
            }

            public void ReadNextRgba(byte[] destination)
            {
                if (_bitmap == null)
                {
                    throw new ObjectDisposedException("GdiSource");
                }

                if (destination == null || destination.Length != _bgraRow.Length)
                {
                    throw new ArgumentException("目标行缓冲区长度不正确。", "destination");
                }

                if (_nextRow >= _bitmap.Height)
                {
                    throw new EndOfStreamException("读取的源图片行数超过图片高度。");
                }

                BitmapData data = null;
                try
                {
                    data = _bitmap.LockBits(
                        new Rectangle(0, _nextRow, _bitmap.Width, 1),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppArgb);
                    Marshal.Copy(data.Scan0, _bgraRow, 0, _bgraRow.Length);
                }
                finally
                {
                    if (data != null)
                    {
                        _bitmap.UnlockBits(data);
                    }
                }

                int i;
                for (i = 0; i < destination.Length; i += 4)
                {
                    destination[i] = _bgraRow[i + 2];
                    destination[i + 1] = _bgraRow[i + 1];
                    destination[i + 2] = _bgraRow[i];
                    destination[i + 3] = _bgraRow[i + 3];
                }

                _nextRow++;
            }

            public void Dispose()
            {
                if (_bitmap != null)
                {
                    Bitmap bitmap = _bitmap;
                    _bitmap = null;
                    bitmap.Dispose();
                }
            }
        }
    }
}
