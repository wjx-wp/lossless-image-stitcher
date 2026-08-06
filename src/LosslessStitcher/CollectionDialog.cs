using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace LosslessStitcher
{
    internal sealed class CollectionDialog : Form
    {
        private readonly List<CollectionItem> _workingItems;
        private readonly List<Bitmap> _thumbnails;
        private readonly DataGridView _nameGrid;
        private readonly CheckBox _automaticLayoutCheck;
        private readonly NumericUpDown _columnsInput;
        private readonly NumericUpDown _posterWidthInput;
        private readonly NumericUpDown _gapInput;
        private readonly NumericUpDown _marginInput;
        private readonly NumericUpDown _fontSizeInput;
        private readonly ComboBox _formatCombo;
        private readonly NumericUpDown _jpegQualityInput;
        private readonly ComboBox _backgroundCombo;
        private readonly CollectionPreviewPanel _preview;
        private readonly Label _previewInfo;
        private readonly Label _qualityHint;
        private readonly Button _nextButton;
        private readonly Button _cancelButton;

        private TextBoxBase _captionEditor;
        private bool _disposed;
        private bool _previewValid;
        private CollectionLayout _previewLayout;
        private int _previewWidth;
        private int _previewHeight;
        private int _previewRows;
        private int _previewColumns;

        public List<CollectionItem> ResultItems { get; private set; }

        public CollectionSettings ResultSettings { get; private set; }

        public CollectionDialog(IList<CollectionItem> items)
            : this(items, null)
        {
        }

        public CollectionDialog(IList<CollectionItem> items, IList<Bitmap> existingThumbnails)
        {
            if (items == null)
            {
                throw new ArgumentNullException("items");
            }

            if (items.Count == 0)
            {
                throw new ArgumentException("至少需要一张图片。", "items");
            }

            if (existingThumbnails != null && existingThumbnails.Count != items.Count)
            {
                throw new ArgumentException("缩略图数量必须和图片数量一致。", "existingThumbnails");
            }

            _workingItems = CloneItems(items);
            _thumbnails = new List<Bitmap>(_workingItems.Count);

            Text = "制作海报合集";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1050, 700);
            MinimumSize = new Size(900, 600);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(244, 247, 251);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;

            _nameGrid = CreateNameGrid();
            _automaticLayoutCheck = new CheckBox();
            _columnsInput = CreateNumericInput(1, 30, 4);
            _posterWidthInput = CreateNumericInput(300, 1600, 800);
            _gapInput = CreateNumericInput(0, 200, 32);
            _marginInput = CreateNumericInput(0, 300, 48);
            _fontSizeInput = CreateNumericInput(16, 100, 36);
            _formatCombo = CreateDropDown();
            _jpegQualityInput = CreateNumericInput(80, 100, 92);
            _backgroundCombo = CreateDropDown();
            _preview = new CollectionPreviewPanel(this);
            _previewInfo = CreateInfoLabel();
            _qualityHint = CreateInfoLabel();
            _nextButton = CreateButton("下一步：选择保存位置", 184, true);
            _cancelButton = CreateButton("取消", 82, false);

            Controls.Add(BuildInterface());
            PopulateGrid();
            ConfigureSettings();
            WireEvents();
            if (existingThumbnails == null)
            {
                LoadThumbnails();
            }
            else
            {
                CopyThumbnails(existingThumbnails);
            }
            UpdateSettingsState();
            RefreshPreview();

            AcceptButton = _nextButton;
            CancelButton = _cancelButton;
        }

        private void CopyThumbnails(IList<Bitmap> existingThumbnails)
        {
            try
            {
                int index;
                for (index = 0; index < existingThumbnails.Count; index++)
                {
                    Bitmap source = existingThumbnails[index];
                    _thumbnails.Add(source == null ? null : new Bitmap(source));
                }
            }
            catch
            {
                int index;
                for (index = 0; index < _thumbnails.Count; index++)
                {
                    if (_thumbnails[index] != null)
                    {
                        _thumbnails[index].Dispose();
                    }
                }

                _thumbnails.Clear();
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;

                if (_captionEditor != null)
                {
                    _captionEditor.TextChanged -= CaptionEditorTextChanged;
                    _captionEditor = null;
                }

                for (int index = 0; index < _thumbnails.Count; index++)
                {
                    Bitmap thumbnail = _thumbnails[index];
                    if (thumbnail != null)
                    {
                        thumbnail.Dispose();
                    }
                }

                _thumbnails.Clear();
            }

            base.Dispose(disposing);
        }

        private static List<CollectionItem> CloneItems(IList<CollectionItem> items)
        {
            List<CollectionItem> clones = new List<CollectionItem>(items.Count);
            for (int index = 0; index < items.Count; index++)
            {
                CollectionItem item = items[index];
                if (item == null)
                {
                    throw new ArgumentException("第 " + (index + 1) + " 项图片为空。", "items");
                }

                clones.Add(CloneItem(item));
            }

            return clones;
        }

        private static CollectionItem CloneItem(CollectionItem item)
        {
            return new CollectionItem
            {
                Path = item.Path,
                Caption = item.Caption,
                Width = item.Width,
                Height = item.Height
            };
        }

        private Control BuildInterface()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(12);
            root.Margin = Padding.Empty;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildWorkspace(), 0, 1);
            root.Controls.Add(BuildFooter(), 0, 2);
            return root;
        }

        private Control BuildHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Color.Transparent;

            Label title = new Label();
            title.Text = "制作海报合集";
            title.AutoSize = true;
            title.Font = new Font(Font.FontFamily, 16F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(24, 34, 48);
            title.Location = new Point(3, 4);

            Label subtitle = new Label();
            subtitle.Text = "核对国家名称，程序会自动选择协调的行列并生成清晰、适合分享的合集图。";
            subtitle.AutoSize = true;
            subtitle.ForeColor = Color.FromArgb(91, 103, 120);
            subtitle.Location = new Point(5, 38);

            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            return header;
        }

        private Control BuildWorkspace()
        {
            TableLayoutPanel workspace = new TableLayoutPanel();
            workspace.Dock = DockStyle.Fill;
            workspace.Margin = Padding.Empty;
            workspace.ColumnCount = 3;
            workspace.RowCount = 1;
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 254F));

            Control names = BuildNamePanel();
            names.Margin = new Padding(0, 0, 10, 0);
            Control preview = BuildPreviewPanel();
            preview.Margin = new Padding(0, 0, 10, 0);
            Control settings = BuildSettingsPanel();
            settings.Margin = Padding.Empty;

            workspace.Controls.Add(names, 0, 0);
            workspace.Controls.Add(preview, 1, 0);
            workspace.Controls.Add(settings, 2, 0);
            return workspace;
        }

        private Control BuildNamePanel()
        {
            Panel card = CreateCardPanel();

            Label heading = CreateHeading("图片与显示名称");
            heading.Dock = DockStyle.Top;
            heading.Height = 42;
            heading.Padding = new Padding(12, 12, 0, 0);

            Label hint = new Label();
            hint.Dock = DockStyle.Bottom;
            hint.Height = 42;
            hint.Padding = new Padding(10, 5, 10, 5);
            hint.Text = "显示名称可直接修改；图片会按当前列表顺序排入合集。";
            hint.ForeColor = Color.FromArgb(100, 111, 127);
            hint.BackColor = Color.FromArgb(248, 250, 253);
            hint.TextAlign = ContentAlignment.MiddleLeft;

            _nameGrid.Dock = DockStyle.Fill;
            card.Controls.Add(_nameGrid);
            card.Controls.Add(hint);
            card.Controls.Add(heading);
            return card;
        }

        private Control BuildPreviewPanel()
        {
            Panel card = CreateCardPanel();

            Label heading = CreateHeading("合集预览");
            heading.Dock = DockStyle.Top;
            heading.Height = 42;
            heading.Padding = new Padding(12, 12, 0, 0);

            Panel infoPanel = new Panel();
            infoPanel.Dock = DockStyle.Bottom;
            infoPanel.Height = 50;
            infoPanel.Padding = new Padding(10, 5, 10, 5);
            infoPanel.BackColor = Color.FromArgb(248, 250, 253);
            _previewInfo.Dock = DockStyle.Fill;
            _previewInfo.TextAlign = ContentAlignment.MiddleCenter;
            _previewInfo.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            infoPanel.Controls.Add(_previewInfo);

            _preview.Dock = DockStyle.Fill;
            card.Controls.Add(_preview);
            card.Controls.Add(infoPanel);
            card.Controls.Add(heading);
            return card;
        }

        private Control BuildSettingsPanel()
        {
            Panel card = CreateCardPanel();

            Label heading = CreateHeading("输出设置");
            heading.Dock = DockStyle.Top;
            heading.Height = 42;
            heading.Padding = new Padding(12, 12, 0, 0);

            TableLayoutPanel settings = new TableLayoutPanel();
            settings.Dock = DockStyle.Fill;
            settings.Padding = new Padding(12, 7, 12, 7);
            settings.ColumnCount = 2;
            settings.RowCount = 11;
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));

            _automaticLayoutCheck.Text = "自动排列（推荐）";
            _automaticLayoutCheck.Checked = true;
            _automaticLayoutCheck.AutoSize = true;
            _automaticLayoutCheck.Margin = new Padding(2, 5, 0, 5);
            settings.Controls.Add(_automaticLayoutCheck, 0, 0);
            settings.SetColumnSpan(_automaticLayoutCheck, 2);

            AddSettingRow(settings, 1, "手动列数", _columnsInput, null);
            AddSettingRow(settings, 2, "单张宽度", _posterWidthInput, "像素");
            AddSettingRow(settings, 3, "图片间距", _gapInput, "像素");
            AddSettingRow(settings, 4, "外边距", _marginInput, "像素");
            AddSettingRow(settings, 5, "国家名字号", _fontSizeInput, "像素");
            AddSettingRow(settings, 6, "输出格式", _formatCombo, null);
            AddSettingRow(settings, 7, "JPEG 质量", _jpegQualityInput, null);
            AddSettingRow(settings, 8, "背景颜色", _backgroundCombo, null);

            _qualityHint.Dock = DockStyle.Fill;
            _qualityHint.Margin = new Padding(2, 10, 2, 0);
            _qualityHint.Text = "JPEG 高质量适合分享，画面清晰且文件更小。";
            settings.Controls.Add(_qualityHint, 0, 9);
            settings.SetColumnSpan(_qualityHint, 2);

            Label autoHint = CreateInfoLabel();
            autoHint.Dock = DockStyle.Fill;
            autoHint.Margin = new Padding(2, 8, 2, 0);
            autoHint.Text = "自动排列会根据图片数量选择更协调的行列。";
            settings.Controls.Add(autoHint, 0, 10);
            settings.SetColumnSpan(autoHint, 2);

            for (int row = 0; row < 9; row++)
            {
                settings.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 0 ? 34F : 42F));
            }

            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 53F));
            settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            card.Controls.Add(settings);
            card.Controls.Add(heading);
            return card;
        }

        private Control BuildFooter()
        {
            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.Padding = new Padding(0, 12, 0, 0);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Right;
            actions.AutoSize = true;
            actions.WrapContents = false;
            actions.FlowDirection = FlowDirection.RightToLeft;
            _cancelButton.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(_nextButton);
            actions.Controls.Add(_cancelButton);

            Label note = new Label();
            note.AutoSize = true;
            note.Text = "下一步只选择保存位置，不会覆盖原始图片。";
            note.ForeColor = Color.FromArgb(91, 103, 120);
            note.Location = new Point(4, 25);

            footer.Controls.Add(actions);
            footer.Controls.Add(note);
            return footer;
        }

        private void ConfigureSettings()
        {
            _formatCombo.Items.Add("JPEG（高质量，推荐）");
            _formatCombo.Items.Add("PNG（无损，文件较大）");
            _formatCombo.SelectedIndex = 0;

            _backgroundCombo.Items.Add("白色");
            _backgroundCombo.Items.Add("浅灰色");
            _backgroundCombo.SelectedIndex = 0;
        }

        private void PopulateGrid()
        {
            _nameGrid.Rows.Clear();
            for (int index = 0; index < _workingItems.Count; index++)
            {
                CollectionItem item = _workingItems[index];
                string fileName;
                try
                {
                    fileName = Path.GetFileName(item.Path);
                }
                catch
                {
                    fileName = item.Path;
                }

                _nameGrid.Rows.Add(fileName, item.Caption);
            }

            _nameGrid.ClearSelection();
        }

        private void LoadThumbnails()
        {
            for (int index = 0; index < _workingItems.Count; index++)
            {
                Bitmap thumbnail = null;
                try
                {
                    thumbnail = ImageLoader.LoadThumbnail(
                        _workingItems[index].Path,
                        120,
                        160);
                }
                catch
                {
                    // A missing preview must not prevent the user from correcting names
                    // or continuing to the normal export validation.
                }

                _thumbnails.Add(thumbnail);
            }
        }

        private void WireEvents()
        {
            _automaticLayoutCheck.CheckedChanged += delegate
            {
                UpdateSettingsState();
                RefreshPreview();
            };

            _columnsInput.ValueChanged += SettingChanged;
            _posterWidthInput.ValueChanged += SettingChanged;
            _gapInput.ValueChanged += SettingChanged;
            _marginInput.ValueChanged += SettingChanged;
            _fontSizeInput.ValueChanged += SettingChanged;
            _jpegQualityInput.ValueChanged += SettingChanged;
            _formatCombo.SelectedIndexChanged += delegate
            {
                UpdateSettingsState();
                RefreshPreview();
            };
            _backgroundCombo.SelectedIndexChanged += SettingChanged;

            _nameGrid.CellEndEdit += delegate
            {
                SyncCaptionsFromGrid();
                RefreshPreview();
            };
            _nameGrid.EditingControlShowing += NameGridEditingControlShowing;
            _nameGrid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e)
            {
                e.ThrowException = false;
            };

            _nextButton.Click += NextButtonClick;
        }

        private void SettingChanged(object sender, EventArgs e)
        {
            RefreshPreview();
        }

        private void NameGridEditingControlShowing(
            object sender,
            DataGridViewEditingControlShowingEventArgs e)
        {
            if (_captionEditor != null)
            {
                _captionEditor.TextChanged -= CaptionEditorTextChanged;
            }

            _captionEditor = e.Control as TextBoxBase;
            if (_captionEditor != null)
            {
                _captionEditor.TextChanged += CaptionEditorTextChanged;
            }
        }

        private void CaptionEditorTextChanged(object sender, EventArgs e)
        {
            if (_nameGrid.CurrentCell == null || _nameGrid.CurrentCell.ColumnIndex != 1)
            {
                return;
            }

            int rowIndex = _nameGrid.CurrentCell.RowIndex;
            if (rowIndex < 0 || rowIndex >= _workingItems.Count)
            {
                return;
            }

            _workingItems[rowIndex].Caption = _captionEditor == null
                ? String.Empty
                : _captionEditor.Text;
            _preview.Invalidate();
        }

        private void UpdateSettingsState()
        {
            _columnsInput.Enabled = !_automaticLayoutCheck.Checked;
            bool isJpeg = _formatCombo.SelectedIndex != 1;
            _jpegQualityInput.Enabled = isJpeg;
            _qualityHint.Text = isJpeg
                ? "JPEG 高质量适合分享，画面清晰且文件更小。"
                : "PNG 完全无损，但合集文件通常会明显更大。";
        }

        private CollectionSettings ReadSettings()
        {
            return new CollectionSettings
            {
                Columns = _automaticLayoutCheck.Checked ? 0 : (int)_columnsInput.Value,
                PosterWidth = (int)_posterWidthInput.Value,
                Gap = (int)_gapInput.Value,
                Margin = (int)_marginInput.Value,
                FontSize = (int)_fontSizeInput.Value,
                OutputFormat = _formatCombo.SelectedIndex == 1
                    ? CollectionOutputFormat.Png
                    : CollectionOutputFormat.Jpeg,
                JpegQuality = (int)_jpegQualityInput.Value,
                Background = _backgroundCombo.SelectedIndex == 1
                    ? Color.FromArgb(242, 244, 247)
                    : Color.White
            };
        }

        private void RefreshPreview()
        {
            if (_disposed || _preview == null)
            {
                return;
            }

            try
            {
                CollectionSettings settings = ReadSettings();
                var layout = CollectionLayoutCalculator.Calculate(_workingItems, settings);
                _previewLayout = layout;
                _previewWidth = layout.Width;
                _previewHeight = layout.Height;
                _previewRows = layout.Rows;
                _previewColumns = layout.Columns > 0
                    ? layout.Columns
                    : GetEffectiveColumnCount(
                        _workingItems.Count,
                        settings.Columns,
                        layout.Rows);
                _previewValid = true;

                double megapixels = layout.PixelCount / 1000000D;
                _previewInfo.ForeColor = Color.FromArgb(53, 66, 84);
                _previewInfo.Text = String.Format(
                    "实际尺寸 {0:N0} × {1:N0} 像素  ·  {2} 行 × {3} 列  ·  {4:0.0} MP",
                    layout.Width,
                    layout.Height,
                    layout.Rows,
                    _previewColumns,
                    megapixels);
            }
            catch (Exception exception)
            {
                _previewValid = false;
                _previewLayout = null;
                _previewWidth = 0;
                _previewHeight = 0;
                _previewRows = 0;
                _previewColumns = 0;
                _previewInfo.ForeColor = Color.FromArgb(185, 54, 54);
                _previewInfo.Text = "当前设置无法生成预览：" + exception.Message;
            }

            _preview.Invalidate();
        }

        private static int GetEffectiveColumnCount(int itemCount, int requestedColumns, int rows)
        {
            if (itemCount <= 0)
            {
                return 0;
            }

            if (requestedColumns > 0)
            {
                return Math.Min(requestedColumns, itemCount);
            }

            if (rows <= 0)
            {
                return itemCount;
            }

            return Math.Max(1, (itemCount + rows - 1) / rows);
        }

        private void NextButtonClick(object sender, EventArgs e)
        {
            if (!_nameGrid.EndEdit())
            {
                MessageBox.Show(
                    this,
                    "显示名称仍在编辑中，请确认后重试。",
                    "无法继续",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SyncCaptionsFromGrid();
            for (int index = 0; index < _workingItems.Count; index++)
            {
                string caption = _workingItems[index].Caption;
                caption = caption == null ? String.Empty : caption.Trim();
                _workingItems[index].Caption = caption;
                _nameGrid.Rows[index].Cells[1].Value = caption;

                if (caption.Length == 0)
                {
                    _nameGrid.CurrentCell = _nameGrid.Rows[index].Cells[1];
                    _nameGrid.BeginEdit(true);
                    MessageBox.Show(
                        this,
                        "第 " + (index + 1) + " 张图片的显示名称不能为空。",
                        "请补充名称",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
            }

            CollectionSettings settings = ReadSettings();
            try
            {
                CollectionLayoutCalculator.Calculate(_workingItems, settings);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "当前设置无法生成合集：\r\n\r\n" + exception.Message,
                    "无法继续",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            ResultItems = CloneItems(_workingItems);
            ResultSettings = new CollectionSettings
            {
                Columns = settings.Columns,
                PosterWidth = settings.PosterWidth,
                Gap = settings.Gap,
                Margin = settings.Margin,
                FontSize = settings.FontSize,
                OutputFormat = settings.OutputFormat,
                JpegQuality = settings.JpegQuality,
                Background = settings.Background
            };

            DialogResult = DialogResult.OK;
            Close();
        }

        private void SyncCaptionsFromGrid()
        {
            int rowCount = Math.Min(_nameGrid.Rows.Count, _workingItems.Count);
            for (int index = 0; index < rowCount; index++)
            {
                object value = _nameGrid.Rows[index].Cells[1].Value;
                _workingItems[index].Caption = value == null
                    ? String.Empty
                    : Convert.ToString(value);
            }
        }

        private void DrawPreview(Graphics graphics, Rectangle clientRectangle)
        {
            graphics.Clear(Color.FromArgb(235, 240, 246));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            if (!_previewValid || _previewWidth <= 0 || _previewHeight <= 0)
            {
                DrawCenteredMessage(graphics, clientRectangle, "暂时无法显示预览");
                return;
            }

            Rectangle available = Rectangle.Inflate(clientRectangle, -18, -18);
            if (available.Width < 20 || available.Height < 20)
            {
                return;
            }

            RectangleF page = FitRectangle(
                available,
                _previewWidth,
                _previewHeight);

            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(45, 43, 54, 67)))
            {
                graphics.FillRectangle(
                    shadowBrush,
                    page.X + 5F,
                    page.Y + 6F,
                    page.Width,
                    page.Height);
            }

            CollectionSettings settings = ReadSettings();
            using (SolidBrush pageBrush = new SolidBrush(settings.Background))
            using (Pen pagePen = new Pen(Color.FromArgb(205, 213, 224)))
            {
                graphics.FillRectangle(pageBrush, page);
                graphics.DrawRectangle(
                    pagePen,
                    page.X,
                    page.Y,
                    Math.Max(1F, page.Width - 1F),
                    Math.Max(1F, page.Height - 1F));
            }

            DrawPreviewItems(graphics, page, settings);
        }

        private void DrawPreviewItems(
            Graphics graphics,
            RectangleF page,
            CollectionSettings settings)
        {
            if (_previewLayout == null || _previewLayout.Items == null ||
                _previewLayout.Items.Count == 0)
            {
                return;
            }

            float scaleX = page.Width / _previewWidth;
            float scaleY = page.Height / _previewHeight;
            float fontSize = Math.Max(5F, Math.Min(18F, settings.FontSize * scaleY));

            using (Font captionFont = new Font(
                Font.FontFamily,
                fontSize,
                FontStyle.Bold,
                GraphicsUnit.Pixel))
            using (SolidBrush captionBrush = new SolidBrush(Color.FromArgb(35, 42, 52)))
            using (Pen missingPen = new Pen(Color.FromArgb(183, 191, 202)))
            using (StringFormat captionFormat = new StringFormat())
            {
                captionFormat.Alignment = StringAlignment.Center;
                captionFormat.LineAlignment = StringAlignment.Center;
                captionFormat.Trimming = StringTrimming.EllipsisCharacter;
                captionFormat.FormatFlags = StringFormatFlags.NoWrap;

                for (int itemIndex = 0;
                     itemIndex < _previewLayout.Items.Count;
                     itemIndex++)
                {
                    CollectionPlacedItem placed = _previewLayout.Items[itemIndex];
                    Rectangle imageBox = placed.ImageBox;
                    Rectangle labelBox = placed.LabelBox;
                    RectangleF imageArea = ScaleLayoutRectangle(
                        page,
                        imageBox,
                        scaleX,
                        scaleY);
                    RectangleF captionArea = ScaleLayoutRectangle(
                        page,
                        labelBox,
                        scaleX,
                        scaleY);

                    DrawThumbnail(graphics, itemIndex, imageArea, missingPen);

                    string caption = placed.Item == null
                        ? _workingItems[itemIndex].Caption
                        : placed.Item.Caption;
                    graphics.DrawString(
                        String.IsNullOrEmpty(caption) ? "（未命名）" : caption,
                        captionFont,
                        captionBrush,
                        captionArea,
                        captionFormat);
                }
            }
        }

        private static RectangleF ScaleLayoutRectangle(
            RectangleF page,
            Rectangle rectangle,
            float scaleX,
            float scaleY)
        {
            return new RectangleF(
                page.X + (rectangle.X * scaleX),
                page.Y + (rectangle.Y * scaleY),
                rectangle.Width * scaleX,
                rectangle.Height * scaleY);
        }

        private void DrawThumbnail(
            Graphics graphics,
            int itemIndex,
            RectangleF imageArea,
            Pen missingPen)
        {
            Bitmap thumbnail = itemIndex >= 0 && itemIndex < _thumbnails.Count
                ? _thumbnails[itemIndex]
                : null;

            if (thumbnail == null)
            {
                RectangleF placeholder = RectangleF.Inflate(imageArea, -1F, -1F);
                using (SolidBrush placeholderBrush = new SolidBrush(Color.FromArgb(233, 237, 242)))
                {
                    graphics.FillRectangle(placeholderBrush, placeholder);
                }

                graphics.DrawRectangle(
                    missingPen,
                    placeholder.X,
                    placeholder.Y,
                    placeholder.Width,
                    placeholder.Height);
                return;
            }

            RectangleF target = FitRectangle(
                imageArea,
                thumbnail.Width,
                thumbnail.Height);
            Rectangle targetPixels = Rectangle.Round(target);
            graphics.DrawImage(
                thumbnail,
                targetPixels,
                0,
                0,
                thumbnail.Width,
                thumbnail.Height,
                GraphicsUnit.Pixel);
        }

        private void DrawCenteredMessage(
            Graphics graphics,
            Rectangle bounds,
            string message)
        {
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(103, 115, 132)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                graphics.DrawString(message, Font, brush, bounds, format);
            }
        }

        private static RectangleF FitRectangle(
            Rectangle bounds,
            int contentWidth,
            int contentHeight)
        {
            return FitRectangle(
                new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                contentWidth,
                contentHeight);
        }

        private static RectangleF FitRectangle(
            RectangleF bounds,
            int contentWidth,
            int contentHeight)
        {
            if (contentWidth <= 0 || contentHeight <= 0 ||
                bounds.Width <= 0F || bounds.Height <= 0F)
            {
                return RectangleF.Empty;
            }

            float scale = Math.Min(
                bounds.Width / contentWidth,
                bounds.Height / contentHeight);
            float width = contentWidth * scale;
            float height = contentHeight * scale;
            return new RectangleF(
                bounds.X + ((bounds.Width - width) / 2F),
                bounds.Y + ((bounds.Height - height) / 2F),
                width,
                height);
        }

        private static DataGridView CreateNameGrid()
        {
            DataGridView grid = new DataGridView();
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AutoGenerateColumns = false;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeight = 36;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.EnableHeadersVisualStyles = false;
            grid.GridColor = Color.FromArgb(229, 234, 241);
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.RowTemplate.Height = 34;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;

            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(55, 65, 81);
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(
                "Microsoft YaHei UI",
                9F,
                FontStyle.Bold);
            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = Color.FromArgb(42, 52, 66);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 237, 255);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(25, 48, 85);
            grid.DefaultCellStyle.Padding = new Padding(5, 2, 5, 2);

            DataGridViewTextBoxColumn fileColumn = new DataGridViewTextBoxColumn();
            fileColumn.HeaderText = "原文件";
            fileColumn.ReadOnly = true;
            fileColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            fileColumn.FillWeight = 54F;
            fileColumn.SortMode = DataGridViewColumnSortMode.NotSortable;

            DataGridViewTextBoxColumn captionColumn = new DataGridViewTextBoxColumn();
            captionColumn.HeaderText = "显示名称";
            captionColumn.ReadOnly = false;
            captionColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            captionColumn.FillWeight = 46F;
            captionColumn.SortMode = DataGridViewColumnSortMode.NotSortable;

            grid.Columns.Add(fileColumn);
            grid.Columns.Add(captionColumn);
            return grid;
        }

        private static NumericUpDown CreateNumericInput(int minimum, int maximum, int value)
        {
            NumericUpDown input = new NumericUpDown();
            input.Minimum = minimum;
            input.Maximum = maximum;
            input.Value = value;
            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(2, 7, 2, 7);
            input.TextAlign = HorizontalAlignment.Right;
            input.ThousandsSeparator = true;
            return input;
        }

        private static ComboBox CreateDropDown()
        {
            ComboBox combo = new ComboBox();
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Dock = DockStyle.Fill;
            combo.Margin = new Padding(2, 7, 2, 7);
            return combo;
        }

        private static void AddSettingRow(
            TableLayoutPanel table,
            int row,
            string labelText,
            Control input,
            string suffix)
        {
            Label label = new Label();
            label.Text = labelText;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = Color.FromArgb(65, 75, 91);
            label.Margin = new Padding(2, 0, 2, 0);
            table.Controls.Add(label, 0, row);

            if (String.IsNullOrEmpty(suffix))
            {
                table.Controls.Add(input, 1, row);
                return;
            }

            TableLayoutPanel wrapper = new TableLayoutPanel();
            wrapper.Dock = DockStyle.Fill;
            wrapper.Margin = Padding.Empty;
            wrapper.ColumnCount = 2;
            wrapper.RowCount = 1;
            wrapper.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            wrapper.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34F));
            input.Margin = new Padding(2, 7, 2, 7);

            Label suffixLabel = new Label();
            suffixLabel.Text = suffix;
            suffixLabel.Dock = DockStyle.Fill;
            suffixLabel.TextAlign = ContentAlignment.MiddleRight;
            suffixLabel.ForeColor = Color.FromArgb(115, 124, 138);
            suffixLabel.Margin = Padding.Empty;

            wrapper.Controls.Add(input, 0, 0);
            wrapper.Controls.Add(suffixLabel, 1, 0);
            table.Controls.Add(wrapper, 1, row);
        }

        private static Panel CreateCardPanel()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            return panel;
        }

        private static Label CreateHeading(string text)
        {
            Label heading = new Label();
            heading.Text = text;
            heading.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            heading.ForeColor = Color.FromArgb(35, 45, 61);
            heading.BackColor = Color.White;
            return heading;
        }

        private static Label CreateInfoLabel()
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.ForeColor = Color.FromArgb(91, 103, 120);
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static Button CreateButton(string text, int width, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 38;
            button.Margin = new Padding(8, 0, 0, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            if (primary)
            {
                button.BackColor = Color.FromArgb(39, 105, 218);
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = Color.FromArgb(39, 105, 218);
            }
            else
            {
                button.BackColor = Color.White;
                button.ForeColor = Color.FromArgb(55, 65, 81);
                button.FlatAppearance.BorderColor = Color.FromArgb(201, 209, 220);
            }

            return button;
        }

        private sealed class CollectionPreviewPanel : Panel
        {
            private readonly CollectionDialog _owner;

            public CollectionPreviewPanel(CollectionDialog owner)
            {
                _owner = owner;
                BackColor = Color.FromArgb(235, 240, 246);
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.UserPaint,
                    true);
                UpdateStyles();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                _owner.DrawPreview(e.Graphics, ClientRectangle);
            }
        }
    }
}
