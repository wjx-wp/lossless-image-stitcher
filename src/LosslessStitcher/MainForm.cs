using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LosslessStitcher
{
    internal sealed class MainForm : Form
    {
        private static readonly string[] SupportedExtensions =
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
        };

        private readonly ListView _imageList;
        private readonly ImageList _thumbnailList;
        private readonly PreviewCanvas _preview;
        private readonly RadioButton _verticalRadio;
        private readonly RadioButton _horizontalRadio;
        private readonly ComboBox _alignmentCombo;
        private readonly NumericUpDown _spacingInput;
        private readonly NumericUpDown _marginInput;
        private readonly RadioButton _transparentRadio;
        private readonly RadioButton _whiteRadio;
        private readonly RadioButton _customRadio;
        private readonly Button _customColorButton;
        private readonly Label _selectionLabel;
        private readonly Label _dimensionLabel;
        private readonly Label _estimateLabel;
        private readonly Label _warningLabel;
        private readonly Label _progressLabel;
        private readonly ProgressBar _progressBar;
        private readonly Button _exportButton;
        private readonly Button _cancelButton;

        private Color _customColor = Color.FromArgb(255, 36, 42, 54);
        private ListViewItem _draggedItem;
        private bool _suppressItemChecked;
        private volatile bool _cancelExport;
        private bool _exporting;

        [Flags]
        private enum MoveFileFlags
        {
            ReplaceExisting = 0x1,
            WriteThrough = 0x8
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            MoveFileFlags flags);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string first, string second);

        public MainForm()
        {
            using (Icon executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            {
                if (executableIcon != null)
                {
                    Icon = (Icon)executableIcon.Clone();
                }
            }

            Text = "无损拼图 · 原始像素拼接";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1040, 650);
            ClientSize = new Size(1280, 760);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(245, 247, 250);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            AllowDrop = true;

            _thumbnailList = new ImageList();
            _thumbnailList.ColorDepth = ColorDepth.Depth32Bit;
            _thumbnailList.ImageSize = new Size(64, 64);
            _thumbnailList.TransparentColor = Color.Transparent;

            _imageList = CreateImageList();
            _preview = new PreviewCanvas(this);

            _verticalRadio = new RadioButton();
            _horizontalRadio = new RadioButton();
            _alignmentCombo = new ComboBox();
            _spacingInput = CreatePixelInput();
            _marginInput = CreatePixelInput();
            _transparentRadio = new RadioButton();
            _whiteRadio = new RadioButton();
            _customRadio = new RadioButton();
            _customColorButton = new Button();
            _selectionLabel = CreateStatusLabel(FontStyle.Bold);
            _dimensionLabel = CreateStatusLabel(FontStyle.Bold);
            _estimateLabel = CreateStatusLabel(FontStyle.Regular);
            _warningLabel = CreateStatusLabel(FontStyle.Regular);
            _progressLabel = CreateStatusLabel(FontStyle.Regular);
            _progressBar = new ProgressBar();
            _exportButton = CreateButton("导出无损 PNG…", 150);
            _cancelButton = CreateButton("取消导出", 94);

            Controls.Add(BuildInterface());
            WireEvents();
            UpdateDirectionControls();
            UpdateState();
        }

        private Control BuildInterface()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));

            root.Controls.Add(BuildToolbar(), 0, 0);
            root.Controls.Add(BuildWorkspace(), 0, 1);
            root.Controls.Add(BuildFooter(), 0, 2);
            return root;
        }

        private Control BuildToolbar()
        {
            Panel bar = new Panel();
            bar.Dock = DockStyle.Fill;
            bar.BackColor = Color.White;
            bar.Padding = new Padding(14, 9, 14, 8);

            Label title = new Label();
            title.Text = "无损拼图";
            title.AutoSize = true;
            title.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(31, 41, 55);
            title.Location = new Point(16, 15);
            bar.Controls.Add(title);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Right;
            actions.AutoSize = true;
            actions.WrapContents = false;
            actions.FlowDirection = FlowDirection.LeftToRight;

            Button addImages = CreateButton("添加图片", 92);
            Button addFolder = CreateButton("添加文件夹", 104);
            Button collection = CreateButton("带名称合集", 112);
            Button remove = CreateButton("移除选中", 96);
            Button clear = CreateButton("清空", 72);
            addImages.Click += delegate { ChooseImages(); };
            addFolder.Click += delegate { ChooseFolder(); };
            collection.Click += delegate { BeginCollectionSetup(); };
            remove.Click += delegate { RemoveSelectedItems(); };
            clear.Click += delegate { ClearItems(); };
            actions.Controls.Add(addImages);
            actions.Controls.Add(addFolder);
            actions.Controls.Add(collection);
            actions.Controls.Add(remove);
            actions.Controls.Add(clear);
            bar.Controls.Add(actions);
            return bar;
        }

        private Control BuildWorkspace()
        {
            TableLayoutPanel workspace = new TableLayoutPanel();
            workspace.Dock = DockStyle.Fill;
            workspace.Padding = new Padding(12, 12, 12, 6);
            workspace.ColumnCount = 3;
            workspace.RowCount = 1;
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 392F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 278F));

            workspace.Controls.Add(BuildImagePanel(), 0, 0);
            workspace.Controls.Add(BuildPreviewPanel(), 1, 0);
            workspace.Controls.Add(BuildSettingsPanel(), 2, 0);
            return workspace;
        }

        private Control BuildImagePanel()
        {
            Panel panel = CreateCardPanel();
            panel.Margin = new Padding(0, 0, 10, 0);

            Label heading = CreateHeading("图片与顺序");
            heading.Dock = DockStyle.Top;
            heading.Height = 40;
            heading.Padding = new Padding(12, 11, 0, 0);

            FlowLayoutPanel listActions = new FlowLayoutPanel();
            listActions.Dock = DockStyle.Bottom;
            listActions.Height = 42;
            listActions.Padding = new Padding(7, 6, 4, 4);
            listActions.WrapContents = false;
            listActions.BackColor = Color.FromArgb(248, 250, 252);

            Button all = CreateSmallButton("全选");
            Button none = CreateSmallButton("全不选");
            Button invert = CreateSmallButton("反选");
            Button up = CreateSmallButton("上移");
            Button down = CreateSmallButton("下移");
            all.Click += delegate { SetAllChecked(true); };
            none.Click += delegate { SetAllChecked(false); };
            invert.Click += delegate { InvertChecked(); };
            up.Click += delegate { MoveSelected(-1); };
            down.Click += delegate { MoveSelected(1); };
            listActions.Controls.Add(all);
            listActions.Controls.Add(none);
            listActions.Controls.Add(invert);
            listActions.Controls.Add(up);
            listActions.Controls.Add(down);

            _imageList.Dock = DockStyle.Fill;
            _imageList.Margin = new Padding(8);
            panel.Controls.Add(_imageList);
            panel.Controls.Add(listActions);
            panel.Controls.Add(heading);
            return panel;
        }

        private Control BuildPreviewPanel()
        {
            Panel panel = CreateCardPanel();
            panel.Margin = new Padding(0, 0, 10, 0);

            Label heading = CreateHeading("成品预览");
            heading.Dock = DockStyle.Top;
            heading.Height = 40;
            heading.Padding = new Padding(12, 11, 0, 0);

            _preview.Dock = DockStyle.Fill;
            _preview.BackColor = Color.FromArgb(237, 241, 246);
            _preview.AllowDrop = true;
            panel.Controls.Add(_preview);
            panel.Controls.Add(heading);
            return panel;
        }

        private Control BuildSettingsPanel()
        {
            Panel card = CreateCardPanel();
            card.Margin = Padding.Empty;
            card.Padding = new Padding(10, 8, 10, 8);

            FlowLayoutPanel stack = new FlowLayoutPanel();
            stack.Dock = DockStyle.Fill;
            stack.FlowDirection = FlowDirection.TopDown;
            stack.WrapContents = false;
            stack.AutoScroll = true;
            stack.Padding = new Padding(0, 0, 4, 0);

            stack.Controls.Add(BuildDirectionGroup());
            stack.Controls.Add(BuildGeometryGroup());
            stack.Controls.Add(BuildBackgroundGroup());
            stack.Controls.Add(BuildLosslessNotice());
            card.Controls.Add(stack);
            return card;
        }

        private Control BuildDirectionGroup()
        {
            GroupBox group = CreateGroup("拼接方向", 236, 82);
            _verticalRadio.Text = "竖向拼接";
            _verticalRadio.AutoSize = true;
            _verticalRadio.Location = new Point(15, 34);
            _verticalRadio.Checked = true;
            _horizontalRadio.Text = "横向拼接";
            _horizontalRadio.AutoSize = true;
            _horizontalRadio.Location = new Point(122, 34);
            group.Controls.Add(_verticalRadio);
            group.Controls.Add(_horizontalRadio);
            return group;
        }

        private Control BuildGeometryGroup()
        {
            GroupBox group = CreateGroup("位置与留白", 236, 166);

            Label alignLabel = CreateFieldLabel("横向对齐", 15, 31);
            _alignmentCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _alignmentCombo.Location = new Point(102, 27);
            _alignmentCombo.Size = new Size(116, 25);
            _alignmentCombo.Items.AddRange(new object[] { "左对齐", "居中", "右对齐" });
            _alignmentCombo.SelectedIndex = 1;

            Label spacingLabel = CreateFieldLabel("图片间距", 15, 73);
            _spacingInput.Location = new Point(102, 68);
            Label spacingUnit = CreateUnitLabel("px", 196, 73);

            Label marginLabel = CreateFieldLabel("画布边距", 15, 115);
            _marginInput.Location = new Point(102, 110);
            Label marginUnit = CreateUnitLabel("px", 196, 115);

            group.Controls.Add(alignLabel);
            group.Controls.Add(_alignmentCombo);
            group.Controls.Add(spacingLabel);
            group.Controls.Add(_spacingInput);
            group.Controls.Add(spacingUnit);
            group.Controls.Add(marginLabel);
            group.Controls.Add(_marginInput);
            group.Controls.Add(marginUnit);
            return group;
        }

        private Control BuildBackgroundGroup()
        {
            GroupBox group = CreateGroup("空白区域", 236, 126);
            _transparentRadio.Text = "透明";
            _transparentRadio.AutoSize = true;
            _transparentRadio.Location = new Point(15, 31);
            _transparentRadio.Checked = true;
            _whiteRadio.Text = "白色";
            _whiteRadio.AutoSize = true;
            _whiteRadio.Location = new Point(88, 31);
            _customRadio.Text = "自定义";
            _customRadio.AutoSize = true;
            _customRadio.Location = new Point(154, 31);

            _customColorButton.Text = "选择颜色";
            _customColorButton.FlatStyle = FlatStyle.Flat;
            _customColorButton.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            _customColorButton.Location = new Point(15, 69);
            _customColorButton.Size = new Size(203, 32);
            _customColorButton.BackColor = _customColor;
            _customColorButton.ForeColor = Color.White;
            group.Controls.Add(_transparentRadio);
            group.Controls.Add(_whiteRadio);
            group.Controls.Add(_customRadio);
            group.Controls.Add(_customColorButton);
            return group;
        }

        private Control BuildLosslessNotice()
        {
            Panel notice = new Panel();
            notice.Size = new Size(236, 118);
            notice.Margin = new Padding(3, 8, 3, 3);
            notice.BackColor = Color.FromArgb(236, 253, 245);

            Label title = new Label();
            title.Text = "✓ 保持原始像素，不缩放";
            title.Font = new Font(Font, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(4, 120, 87);
            title.AutoSize = true;
            title.Location = new Point(12, 12);

            Label body = new Label();
            body.Text = "导出为 RGBA PNG；压缩只影响文件大小，不影响画质。JPEG 输入不会再次产生 JPEG 损失。";
            body.ForeColor = Color.FromArgb(55, 65, 81);
            body.Location = new Point(12, 40);
            body.Size = new Size(210, 66);
            notice.Controls.Add(title);
            notice.Controls.Add(body);
            return notice;
        }

        private Control BuildFooter()
        {
            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Color.White;
            footer.Padding = new Padding(14, 8, 14, 8);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270F));

            Panel information = new Panel();
            _selectionLabel.Location = new Point(2, 2);
            _dimensionLabel.Location = new Point(190, 2);
            _estimateLabel.Location = new Point(410, 2);
            _warningLabel.Location = new Point(2, 27);
            _warningLabel.ForeColor = Color.FromArgb(180, 83, 9);
            _warningLabel.MaximumSize = new Size(760, 0);
            _progressLabel.Location = new Point(2, 49);
            _progressLabel.ForeColor = Color.FromArgb(71, 85, 105);
            _progressBar.Location = new Point(190, 50);
            _progressBar.Size = new Size(310, 13);
            _progressBar.Visible = false;
            information.Controls.Add(_selectionLabel);
            information.Controls.Add(_dimensionLabel);
            information.Controls.Add(_estimateLabel);
            information.Controls.Add(_warningLabel);
            information.Controls.Add(_progressLabel);
            information.Controls.Add(_progressBar);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 17, 0, 0);
            _exportButton.Height = 38;
            _exportButton.BackColor = Color.FromArgb(37, 99, 235);
            _exportButton.ForeColor = Color.White;
            _exportButton.FlatAppearance.BorderColor = Color.FromArgb(37, 99, 235);
            _cancelButton.Height = 38;
            _cancelButton.Visible = false;
            buttons.Controls.Add(_exportButton);
            buttons.Controls.Add(_cancelButton);

            layout.Controls.Add(information, 0, 0);
            layout.Controls.Add(buttons, 1, 0);
            footer.Controls.Add(layout);
            return footer;
        }

        private ListView CreateImageList()
        {
            ListView list = new ListView();
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.MultiSelect = true;
            list.HideSelection = false;
            list.BorderStyle = BorderStyle.None;
            list.SmallImageList = _thumbnailList;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.ShowItemToolTips = true;
            list.AllowDrop = true;
            list.Columns.Add("图片", 190, HorizontalAlignment.Left);
            list.Columns.Add("尺寸", 92, HorizontalAlignment.Left);
            list.Columns.Add("大小", 78, HorizontalAlignment.Right);
            return list;
        }

        private void WireEvents()
        {
            _verticalRadio.CheckedChanged += delegate
            {
                if (_verticalRadio.Checked)
                {
                    UpdateDirectionControls();
                    UpdateState();
                }
            };
            _horizontalRadio.CheckedChanged += delegate
            {
                if (_horizontalRadio.Checked)
                {
                    UpdateDirectionControls();
                    UpdateState();
                }
            };
            _alignmentCombo.SelectedIndexChanged += delegate { UpdateState(); };
            _spacingInput.ValueChanged += delegate { UpdateState(); };
            _marginInput.ValueChanged += delegate { UpdateState(); };
            _transparentRadio.CheckedChanged += delegate { UpdateState(); };
            _whiteRadio.CheckedChanged += delegate { UpdateState(); };
            _customRadio.CheckedChanged += delegate { UpdateState(); };
            _customColorButton.Click += delegate { ChooseCustomColor(); };
            _exportButton.Click += delegate { BeginExport(); };
            _cancelButton.Click += delegate { RequestCancel(); };

            _imageList.ItemChecked += OnItemChecked;
            _imageList.ItemDrag += OnItemDrag;
            _imageList.DragEnter += OnDragEnter;
            _imageList.DragOver += OnImageListDragOver;
            _imageList.DragLeave += delegate { _imageList.InsertionMark.Index = -1; };
            _imageList.DragDrop += OnImageListDragDrop;
            _imageList.KeyDown += OnImageListKeyDown;

            DragEnter += OnDragEnter;
            DragDrop += OnExternalDrop;
            _preview.DragEnter += OnDragEnter;
            _preview.DragDrop += OnExternalDrop;
            FormClosing += OnMainFormClosing;
            FormClosed += OnMainFormClosed;
        }

        private void ChooseImages()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要拼接的图片";
                dialog.Multiselect = true;
                dialog.Filter = "支持的图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|PNG 图片|*.png|所有文件|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    AddPaths(dialog.FileNames);
                }
            }
        }

        private void ChooseFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择图片所在文件夹（读取当前层）";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    AddPaths(new string[] { dialog.SelectedPath });
                }
            }
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            List<string> candidates = new List<string>();
            foreach (string rawPath in paths)
            {
                if (String.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                try
                {
                    string path = Path.GetFullPath(rawPath);
                    if (Directory.Exists(path))
                    {
                        string[] files = Directory.GetFiles(path);
                        Array.Sort(files, new NaturalPathComparer());
                        int fileIndex;
                        for (fileIndex = 0; fileIndex < files.Length; fileIndex++)
                        {
                            if (IsSupportedImage(files[fileIndex]))
                            {
                                candidates.Add(files[fileIndex]);
                            }
                        }
                    }
                    else if (File.Exists(path) && IsSupportedImage(path))
                    {
                        candidates.Add(path);
                    }
                }
                catch
                {
                    // An individual bad drop path must not prevent the remaining files.
                }
            }

            if (candidates.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "没有找到支持的图片。\r\n支持 PNG、JPEG、BMP、GIF 和 TIFF。",
                    "无损拼图",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem item in _imageList.Items)
            {
                ImageEntry entry = item.Tag as ImageEntry;
                if (entry != null)
                {
                    existing.Add(entry.Path);
                }
            }

            List<string> errors = new List<string>();
            int added = 0;
            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            _imageList.BeginUpdate();
            try
            {
                int index;
                for (index = 0; index < candidates.Count; index++)
                {
                    string path = Path.GetFullPath(candidates[index]);
                    if (!existing.Add(path))
                    {
                        continue;
                    }

                    try
                    {
                        Size size = ImageLoader.ReadDisplaySize(path);
                        Bitmap thumbnail = ImageLoader.LoadThumbnail(path, 60, 60);
                        ImageEntry entry = new ImageEntry(path, size.Width, size.Height, thumbnail);
                        int imageIndex = _thumbnailList.Images.Count;
                        _thumbnailList.Images.Add(thumbnail);

                        FileInfo info = new FileInfo(path);
                        ListViewItem item = new ListViewItem(Path.GetFileName(path), imageIndex);
                        item.SubItems.Add(size.Width + " × " + size.Height);
                        item.SubItems.Add(FormatBytes(info.Length));
                        item.ToolTipText = path;
                        item.Tag = entry;
                        item.Checked = true;
                        entry.Item = item;
                        _imageList.Items.Add(item);
                        added++;
                    }
                    catch (Exception exception)
                    {
                        errors.Add(Path.GetFileName(path) + "：" + exception.Message);
                    }
                }
            }
            finally
            {
                _imageList.EndUpdate();
                Cursor = previousCursor;
            }

            UpdateState();
            if (errors.Count > 0)
            {
                int shown = Math.Min(5, errors.Count);
                string message = "有 " + errors.Count + " 张图片无法读取：\r\n\r\n";
                int errorIndex;
                for (errorIndex = 0; errorIndex < shown; errorIndex++)
                {
                    message += "• " + errors[errorIndex] + "\r\n";
                }

                if (errors.Count > shown)
                {
                    message += "…以及另外 " + (errors.Count - shown) + " 张。";
                }

                MessageBox.Show(this, message, "部分图片未添加", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (added == 0)
            {
                _progressLabel.Text = "这些图片已经在列表中。";
            }
        }

        private void RemoveSelectedItems()
        {
            if (_exporting)
            {
                return;
            }

            List<ListViewItem> selected = GetSelectedItems();
            if (selected.Count == 0)
            {
                return;
            }

            _imageList.BeginUpdate();
            try
            {
                int index;
                for (index = 0; index < selected.Count; index++)
                {
                    ImageEntry entry = selected[index].Tag as ImageEntry;
                    _imageList.Items.Remove(selected[index]);
                    if (entry != null)
                    {
                        entry.Dispose();
                    }
                }
            }
            finally
            {
                _imageList.EndUpdate();
            }

            UpdateState();
        }

        private void ClearItems()
        {
            if (_exporting || _imageList.Items.Count == 0)
            {
                return;
            }

            if (MessageBox.Show(
                this,
                "清空当前图片列表？原始文件不会被删除。",
                "无损拼图",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            foreach (ListViewItem item in _imageList.Items)
            {
                ImageEntry entry = item.Tag as ImageEntry;
                if (entry != null)
                {
                    entry.Dispose();
                }
            }

            _imageList.Items.Clear();
            _thumbnailList.Images.Clear();
            UpdateState();
        }

        private void SetAllChecked(bool value)
        {
            _suppressItemChecked = true;
            try
            {
                foreach (ListViewItem item in _imageList.Items)
                {
                    item.Checked = value;
                }
            }
            finally
            {
                _suppressItemChecked = false;
            }

            UpdateState();
        }

        private void InvertChecked()
        {
            _suppressItemChecked = true;
            try
            {
                foreach (ListViewItem item in _imageList.Items)
                {
                    item.Checked = !item.Checked;
                }
            }
            finally
            {
                _suppressItemChecked = false;
            }

            UpdateState();
        }

        private void MoveSelected(int direction)
        {
            if (_exporting || _imageList.SelectedItems.Count == 0)
            {
                return;
            }

            _imageList.BeginUpdate();
            try
            {
                if (direction < 0)
                {
                    int index;
                    for (index = 1; index < _imageList.Items.Count; index++)
                    {
                        ListViewItem item = _imageList.Items[index];
                        if (item.Selected && !_imageList.Items[index - 1].Selected)
                        {
                            _imageList.Items.RemoveAt(index);
                            _imageList.Items.Insert(index - 1, item);
                        }
                    }
                }
                else
                {
                    int index;
                    for (index = _imageList.Items.Count - 2; index >= 0; index--)
                    {
                        ListViewItem item = _imageList.Items[index];
                        if (item.Selected && !_imageList.Items[index + 1].Selected)
                        {
                            _imageList.Items.RemoveAt(index);
                            _imageList.Items.Insert(index + 1, item);
                        }
                    }
                }
            }
            finally
            {
                _imageList.EndUpdate();
            }

            UpdateState();
        }

        private void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_suppressItemChecked || IsDisposed)
            {
                return;
            }

            BeginInvoke((MethodInvoker)delegate { UpdateState(); });
        }

        private void OnItemDrag(object sender, ItemDragEventArgs e)
        {
            if (_exporting)
            {
                return;
            }

            _draggedItem = e.Item as ListViewItem;
            if (_draggedItem != null)
            {
                _imageList.DoDragDrop(_draggedItem, DragDropEffects.Move);
            }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (_exporting)
            {
                e.Effect = DragDropEffects.None;
            }
            else if (e.Data.GetDataPresent(typeof(ListViewItem)))
            {
                e.Effect = DragDropEffects.Move;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnImageListDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(ListViewItem)))
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
                return;
            }

            Point client = _imageList.PointToClient(new Point(e.X, e.Y));
            ListViewItem nearest = _imageList.GetItemAt(client.X, client.Y);
            if (nearest == null)
            {
                _imageList.InsertionMark.Index = _imageList.Items.Count - 1;
                _imageList.InsertionMark.AppearsAfterItem = true;
            }
            else
            {
                Rectangle bounds = nearest.GetBounds(ItemBoundsPortion.Entire);
                _imageList.InsertionMark.Index = nearest.Index;
                _imageList.InsertionMark.AppearsAfterItem = client.Y > bounds.Top + (bounds.Height / 2);
            }

            e.Effect = DragDropEffects.Move;
        }

        private void OnImageListDragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    OnExternalDrop(sender, e);
                    return;
                }

                ListViewItem dragged = e.Data.GetData(typeof(ListViewItem)) as ListViewItem;
                if (dragged == null || dragged.ListView != _imageList)
                {
                    return;
                }

                int insertionIndex = _imageList.InsertionMark.Index;
                if (insertionIndex < 0)
                {
                    insertionIndex = _imageList.Items.Count - 1;
                }

                if (_imageList.InsertionMark.AppearsAfterItem)
                {
                    insertionIndex++;
                }

                int oldIndex = dragged.Index;
                _imageList.Items.Remove(dragged);
                if (oldIndex < insertionIndex)
                {
                    insertionIndex--;
                }

                insertionIndex = Math.Max(0, Math.Min(insertionIndex, _imageList.Items.Count));
                _imageList.Items.Insert(insertionIndex, dragged);
                dragged.Selected = true;
                dragged.Focused = true;
                dragged.EnsureVisible();
                UpdateState();
            }
            finally
            {
                _draggedItem = null;
                _imageList.InsertionMark.Index = -1;
            }
        }

        private void OnExternalDrop(object sender, DragEventArgs e)
        {
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && !_exporting)
            {
                AddPaths(paths);
            }
        }

        private void OnImageListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelectedItems();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in _imageList.Items)
                {
                    item.Selected = true;
                }

                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Up)
            {
                MoveSelected(-1);
                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Down)
            {
                MoveSelected(1);
                e.Handled = true;
            }
        }

        private void ChooseCustomColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = _customColor;
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _customColor = Color.FromArgb(255, dialog.Color.R, dialog.Color.G, dialog.Color.B);
                    _customColorButton.BackColor = _customColor;
                    _customColorButton.ForeColor = GetContrastingTextColor(_customColor);
                    _customRadio.Checked = true;
                    UpdateState();
                }
            }
        }

        private void UpdateDirectionControls()
        {
            int selected = _alignmentCombo.SelectedIndex;
            _alignmentCombo.Items.Clear();
            if (_verticalRadio.Checked)
            {
                _alignmentCombo.Items.AddRange(new object[] { "左对齐", "居中", "右对齐" });
            }
            else
            {
                _alignmentCombo.Items.AddRange(new object[] { "顶部", "居中", "底部" });
            }

            _alignmentCombo.SelectedIndex = selected >= 0 && selected < 3 ? selected : 1;
        }

        private void UpdateState()
        {
            if (IsDisposed)
            {
                return;
            }

            List<ImageEntry> checkedEntries = GetCheckedEntries();
            _selectionLabel.Text = "已勾选 " + checkedEntries.Count + " / 共 " + _imageList.Items.Count + " 张";

            StitchLayout layout;
            string layoutError;
            if (TryBuildLayout(checkedEntries, out layout, out layoutError))
            {
                _dimensionLabel.Text = "成品 " + FormatNumber(layout.Width) + " × " + FormatNumber(layout.Height) + " px";
                double rawMiB = layout.PixelCount * 4D / 1024D / 1024D;
                _estimateLabel.Text = FormatNumber(layout.PixelCount) + " 像素 · RGBA 展开约 " + rawMiB.ToString("0.0", CultureInfo.InvariantCulture) + " MiB";

                if (layout.Width > 16384 || layout.Height > 16384)
                {
                    _warningLabel.Text = "提示：成品长边超过 16384 px；本工具可导出，但部分旧看图软件可能打不开。";
                }
                else
                {
                    _warningLabel.Text = String.Empty;
                }
            }
            else
            {
                _dimensionLabel.Text = checkedEntries.Count == 0 ? "成品 —" : "尺寸计算失败";
                _estimateLabel.Text = String.Empty;
                _warningLabel.Text = checkedEntries.Count == 0 ? "把图片或文件夹拖入窗口即可开始。" : layoutError;
            }

            _exportButton.Enabled = !_exporting && checkedEntries.Count > 0 && layout != null;
            SetEditingEnabled(!_exporting);
            _preview.Invalidate();
        }

        private bool TryBuildLayout(
            List<ImageEntry> entries,
            out StitchLayout layout,
            out string error)
        {
            layout = null;
            error = null;
            if (entries == null || entries.Count == 0)
            {
                return false;
            }

            try
            {
                List<StitchSource> sources = new List<StitchSource>(entries.Count);
                int index;
                for (index = 0; index < entries.Count; index++)
                {
                    ImageEntry entry = entries[index];
                    sources.Add(new StitchSource
                    {
                        Path = entry.Path,
                        Width = entry.Width,
                        Height = entry.Height
                    });
                }

                layout = LayoutCalculator.Calculate(sources, GetSettings());
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private StitchSettings GetSettings()
        {
            StitchAlignment alignment = StitchAlignment.Center;
            if (_alignmentCombo.SelectedIndex == 0)
            {
                alignment = StitchAlignment.Start;
            }
            else if (_alignmentCombo.SelectedIndex == 2)
            {
                alignment = StitchAlignment.End;
            }

            Color background = Color.Transparent;
            bool transparent = _transparentRadio.Checked;
            if (_whiteRadio.Checked)
            {
                background = Color.White;
            }
            else if (_customRadio.Checked)
            {
                background = _customColor;
            }

            return new StitchSettings
            {
                Direction = _verticalRadio.Checked ? StitchDirection.Vertical : StitchDirection.Horizontal,
                Alignment = alignment,
                Spacing = Decimal.ToInt32(_spacingInput.Value),
                Margin = Decimal.ToInt32(_marginInput.Value),
                Background = background,
                TransparentBackground = transparent
            };
        }

        private void BeginCollectionSetup()
        {
            if (_exporting)
            {
                return;
            }

            List<ImageEntry> entries = GetCheckedEntries();
            if (entries.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "请先勾选要放入合集的图片。",
                    "带名称合集",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            List<CollectionItem> collectionItems = new List<CollectionItem>(entries.Count);
            List<Bitmap> collectionThumbnails = new List<Bitmap>(entries.Count);
            int index;
            for (index = 0; index < entries.Count; index++)
            {
                ImageEntry entry = entries[index];
                collectionItems.Add(new CollectionItem
                {
                    Path = entry.Path,
                    Caption = CountryNameParser.FromFileName(entry.Path),
                    Width = entry.Width,
                    Height = entry.Height
                });
                collectionThumbnails.Add(entry.Thumbnail);
            }

            CollectionSettings settings;
            using (CollectionDialog dialog = new CollectionDialog(collectionItems, collectionThumbnails))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                collectionItems = dialog.ResultItems;
                settings = dialog.ResultSettings;
            }

            CollectionLayout layout;
            try
            {
                layout = CollectionLayoutCalculator.Calculate(collectionItems, settings);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "无法生成合集布局：\r\n\r\n" + exception.Message,
                    "带名称合集",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            string extension = settings.OutputFormat == CollectionOutputFormat.Jpeg ? ".jpg" : ".png";
            string destination;
            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Title = "导出带名称合集";
                saveDialog.Filter = settings.OutputFormat == CollectionOutputFormat.Jpeg
                    ? "JPEG 高质量图片|*.jpg;*.jpeg"
                    : "PNG 无损图片|*.png";
                saveDialog.DefaultExt = extension.Substring(1);
                saveDialog.AddExtension = true;
                saveDialog.OverwritePrompt = true;
                saveDialog.FileName = "海报合集_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + extension;
                saveDialog.InitialDirectory = Path.GetDirectoryName(collectionItems[0].Path);
                if (saveDialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                destination = Path.GetFullPath(saveDialog.FileName);
            }

            string chosenExtension = Path.GetExtension(destination);
            bool extensionMatches = settings.OutputFormat == CollectionOutputFormat.Jpeg
                ? String.Equals(chosenExtension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                  String.Equals(chosenExtension, ".jpeg", StringComparison.OrdinalIgnoreCase)
                : String.Equals(chosenExtension, ".png", StringComparison.OrdinalIgnoreCase);
            if (!extensionMatches)
            {
                destination = Path.ChangeExtension(destination, extension);
                if (File.Exists(destination) && MessageBox.Show(
                    this,
                    "目标文件已经存在：\r\n" + destination + "\r\n\r\n是否覆盖？",
                    "确认覆盖",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    return;
                }
            }

            for (index = 0; index < collectionItems.Count; index++)
            {
                if (String.Equals(destination, collectionItems[index].Path, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        this,
                        "不能覆盖参与合集的源图片。请换一个文件名。",
                        "保护原图",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
            }

            string temporaryPath = destination + ".partial-" + Guid.NewGuid().ToString("N") + extension;
            _cancelExport = false;
            _exporting = true;
            _progressBar.Value = 0;
            _progressBar.Visible = true;
            _cancelButton.Visible = true;
            _progressLabel.Text = "正在准备带名称合集…";
            UpdateState();

            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception failure = null;
                bool cancelled = false;
                try
                {
                    CollectionExporter.Export(
                        layout,
                        settings,
                        temporaryPath,
                        delegate { return _cancelExport; },
                        delegate(int percent, string message) { PostProgress(percent, message); });

                    if (_cancelExport)
                    {
                        throw new OperationCanceledException();
                    }

                    ReplaceOutputAtomically(temporaryPath, destination);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        try
                        {
                            File.Delete(temporaryPath);
                        }
                        catch
                        {
                            // Preserve the useful export result or error.
                        }
                    }
                }

                PostCollectionFinished(destination, layout, cancelled, failure);
            });
        }

        private void BeginExport()
        {
            if (_exporting)
            {
                return;
            }

            List<ImageEntry> entries = GetCheckedEntries();
            StitchLayout layout;
            string error;
            if (!TryBuildLayout(entries, out layout, out error))
            {
                MessageBox.Show(this, error ?? "请至少勾选一张图片。", "无法导出", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if ((layout.Width > 16384 || layout.Height > 16384 || layout.PixelCount > 500000000L) &&
                MessageBox.Show(
                    this,
                    "成品尺寸为 " + FormatNumber(layout.Width) + " × " + FormatNumber(layout.Height) + " px。\r\n\r\n" +
                    "生成可能需要一些时间，部分旧软件无法打开这么大的图片。仍然继续吗？",
                    "成品尺寸较大",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            string destination;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出无损 PNG";
                dialog.Filter = "PNG 无损图片|*.png";
                dialog.DefaultExt = "png";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = "拼图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png";
                if (entries.Count > 0)
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(entries[0].Path);
                }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                destination = Path.GetFullPath(dialog.FileName);
            }

            int sourceIndex;
            for (sourceIndex = 0; sourceIndex < entries.Count; sourceIndex++)
            {
                if (String.Equals(destination, entries[sourceIndex].Path, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        this,
                        "不能覆盖参与拼接的源图片。请换一个文件名。",
                        "保护原图",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
            }

            StitchSettings settings = GetSettings();
            string temporaryPath = destination + ".partial-" + Guid.NewGuid().ToString("N") + ".png";
            _cancelExport = false;
            _exporting = true;
            _progressBar.Value = 0;
            _progressBar.Visible = true;
            _cancelButton.Visible = true;
            _progressLabel.Text = "正在准备导出…";
            UpdateState();

            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception failure = null;
                bool cancelled = false;
                try
                {
                    PngStitcher.Export(
                        layout,
                        settings,
                        temporaryPath,
                        delegate { return _cancelExport; },
                        delegate(int percent, string message) { PostProgress(percent, message); });

                    if (_cancelExport)
                    {
                        throw new OperationCanceledException();
                    }

                    ReplaceOutputAtomically(temporaryPath, destination);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        try
                        {
                            File.Delete(temporaryPath);
                        }
                        catch
                        {
                            // The primary result or error is more useful than cleanup noise.
                        }
                    }
                }

                PostExportFinished(destination, layout, cancelled, failure);
            });
        }

        private void RequestCancel()
        {
            if (!_exporting)
            {
                return;
            }

            _cancelExport = true;
            _cancelButton.Enabled = false;
            _progressLabel.Text = "正在取消，请稍候…";
        }

        private void PostProgress(int percent, string message)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!_exporting)
                    {
                        return;
                    }

                    _progressBar.Value = Math.Max(0, Math.Min(100, percent));
                    _progressLabel.Text = message + "  " + percent + "%";
                });
            }
            catch (InvalidOperationException)
            {
                // The window is closing.
            }
        }

        private void PostExportFinished(
            string destination,
            StitchLayout layout,
            bool cancelled,
            Exception failure)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _exporting = false;
                    _cancelExport = false;
                    _progressBar.Visible = false;
                    _cancelButton.Visible = false;
                    _cancelButton.Enabled = true;

                    if (cancelled)
                    {
                        _progressLabel.Text = "导出已取消，未生成成品。";
                    }
                    else if (failure != null)
                    {
                        _progressLabel.Text = "导出失败。";
                        MessageBox.Show(
                            this,
                            "导出失败：\r\n\r\n" + failure.Message,
                            "无损拼图",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    else
                    {
                        FileInfo info = new FileInfo(destination);
                        _progressLabel.Text = "导出完成：" + destination;
                        DialogResult result = MessageBox.Show(
                            this,
                            "导出完成\r\n\r\n" +
                            FormatNumber(layout.Width) + " × " + FormatNumber(layout.Height) + " px · " +
                            FormatBytes(info.Length) + "\r\n\r\n打开文件所在位置？",
                            "无损拼图",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information);
                        if (result == DialogResult.Yes)
                        {
                            OpenInExplorer(destination);
                        }
                    }

                    UpdateState();
                });
            }
            catch (InvalidOperationException)
            {
                // The window is closing.
            }
        }

        private void PostCollectionFinished(
            string destination,
            CollectionLayout layout,
            bool cancelled,
            Exception failure)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _exporting = false;
                    _cancelExport = false;
                    _progressBar.Visible = false;
                    _cancelButton.Visible = false;
                    _cancelButton.Enabled = true;

                    if (cancelled)
                    {
                        _progressLabel.Text = "合集导出已取消，未生成成品。";
                    }
                    else if (failure != null)
                    {
                        _progressLabel.Text = "合集导出失败。";
                        MessageBox.Show(
                            this,
                            "合集导出失败：\r\n\r\n" + failure.Message,
                            "带名称合集",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    else
                    {
                        FileInfo info = new FileInfo(destination);
                        _progressLabel.Text = "合集导出完成：" + destination;
                        DialogResult result = MessageBox.Show(
                            this,
                            "合集导出完成\r\n\r\n" +
                            layout.Items.Count + " 张 · " + layout.Rows + " 行\r\n" +
                            FormatNumber(layout.Width) + " × " + FormatNumber(layout.Height) + " px · " +
                            FormatBytes(info.Length) + "\r\n\r\n打开文件所在位置？",
                            "带名称合集",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information);
                        if (result == DialogResult.Yes)
                        {
                            OpenInExplorer(destination);
                        }
                    }

                    UpdateState();
                });
            }
            catch (InvalidOperationException)
            {
                // The window is closing.
            }
        }

        private void DrawPreview(Graphics graphics, Rectangle client)
        {
            graphics.Clear(Color.FromArgb(237, 241, 246));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            List<ImageEntry> entries = GetCheckedEntries();
            StitchLayout layout;
            string error;
            if (!TryBuildLayout(entries, out layout, out error))
            {
                DrawCenteredText(
                    graphics,
                    client,
                    _imageList.Items.Count == 0
                        ? "把图片或文件夹拖到这里\n或点击右上角「添加图片」"
                        : "请至少勾选一张图片",
                    Color.FromArgb(100, 116, 139));
                return;
            }

            Rectangle available = new Rectangle(
                client.Left + 26,
                client.Top + 24,
                Math.Max(1, client.Width - 52),
                Math.Max(1, client.Height - 66));
            double scale = Math.Min(
                available.Width / (double)layout.Width,
                available.Height / (double)layout.Height);
            if (scale <= 0D || Double.IsInfinity(scale) || Double.IsNaN(scale))
            {
                return;
            }

            int canvasWidth = Math.Max(1, (int)Math.Round(layout.Width * scale));
            int canvasHeight = Math.Max(1, (int)Math.Round(layout.Height * scale));
            int originX = available.Left + ((available.Width - canvasWidth) / 2);
            int originY = available.Top + ((available.Height - canvasHeight) / 2);
            Rectangle canvas = new Rectangle(originX, originY, canvasWidth, canvasHeight);
            StitchSettings settings = GetSettings();

            if (settings.TransparentBackground)
            {
                DrawCheckerboard(graphics, canvas);
            }
            else
            {
                using (Brush brush = new SolidBrush(settings.Background))
                {
                    graphics.FillRectangle(brush, canvas);
                }
            }

            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int index;
            for (index = 0; index < layout.Images.Count && index < entries.Count; index++)
            {
                PlacedImage placed = layout.Images[index];
                ImageEntry entry = entries[index];
                int x = originX + (int)Math.Round(placed.X * scale);
                int y = originY + (int)Math.Round(placed.Y * scale);
                int width = Math.Max(1, (int)Math.Round(placed.Source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(placed.Source.Height * scale));
                graphics.DrawImage(entry.Thumbnail, new Rectangle(x, y, width, height));
            }

            using (Pen border = new Pen(Color.FromArgb(148, 163, 184)))
            {
                graphics.DrawRectangle(border, canvas.X, canvas.Y, canvas.Width - 1, canvas.Height - 1);
            }

            string note = "预览已缩放 · 导出仍使用原始像素";
            SizeF noteSize = graphics.MeasureString(note, Font);
            using (Brush backdrop = new SolidBrush(Color.FromArgb(210, 255, 255, 255)))
            using (Brush textBrush = new SolidBrush(Color.FromArgb(71, 85, 105)))
            {
                RectangleF noteBox = new RectangleF(
                    client.Left + ((client.Width - noteSize.Width) / 2F) - 8F,
                    client.Bottom - 31F,
                    noteSize.Width + 16F,
                    23F);
                graphics.FillRectangle(backdrop, noteBox);
                graphics.DrawString(note, Font, textBrush, noteBox.Left + 8F, noteBox.Top + 3F);
            }
        }

        private List<ImageEntry> GetCheckedEntries()
        {
            List<ImageEntry> result = new List<ImageEntry>();
            foreach (ListViewItem item in _imageList.Items)
            {
                if (!item.Checked)
                {
                    continue;
                }

                ImageEntry entry = item.Tag as ImageEntry;
                if (entry != null)
                {
                    result.Add(entry);
                }
            }

            return result;
        }

        private List<ListViewItem> GetSelectedItems()
        {
            List<ListViewItem> result = new List<ListViewItem>();
            foreach (ListViewItem item in _imageList.SelectedItems)
            {
                result.Add(item);
            }

            return result;
        }

        private void SetEditingEnabled(bool enabled)
        {
            _imageList.Enabled = enabled;
            _verticalRadio.Enabled = enabled;
            _horizontalRadio.Enabled = enabled;
            _alignmentCombo.Enabled = enabled;
            _spacingInput.Enabled = enabled;
            _marginInput.Enabled = enabled;
            _transparentRadio.Enabled = enabled;
            _whiteRadio.Enabled = enabled;
            _customRadio.Enabled = enabled;
            _customColorButton.Enabled = enabled;
        }

        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_exporting)
            {
                return;
            }

            MessageBox.Show(
                this,
                "正在导出图片。请先点击「取消导出」，等待取消完成后再关闭窗口。",
                "无损拼图",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            e.Cancel = true;
        }

        private void OnMainFormClosed(object sender, FormClosedEventArgs e)
        {
            foreach (ListViewItem item in _imageList.Items)
            {
                ImageEntry entry = item.Tag as ImageEntry;
                if (entry != null)
                {
                    entry.Dispose();
                }
            }

            _thumbnailList.Dispose();
        }

        private static void ReplaceOutputAtomically(string temporaryPath, string destination)
        {
            if (!MoveFileEx(
                temporaryPath,
                destination,
                MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法把完整成品移到目标位置。");
            }
        }

        private static void OpenInExplorer(string path)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "explorer.exe";
                startInfo.Arguments = "/select,\"" + path.Replace("\"", "") + "\"";
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch
            {
                // Export succeeded; failing to open Explorer should not turn it into an error.
            }
        }

        private static bool IsSupportedImage(string path)
        {
            string extension = Path.GetExtension(path);
            int index;
            for (index = 0; index < SupportedExtensions.Length; index++)
            {
                if (String.Equals(extension, SupportedExtensions[index], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
            {
                return (bytes / 1024D / 1024D / 1024D).ToString("0.00", CultureInfo.InvariantCulture) + " GiB";
            }

            if (bytes >= 1024L * 1024L)
            {
                return (bytes / 1024D / 1024D).ToString("0.00", CultureInfo.InvariantCulture) + " MiB";
            }

            return (bytes / 1024D).ToString("0.0", CultureInfo.InvariantCulture) + " KiB";
        }

        private static string FormatNumber(long value)
        {
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private static Color GetContrastingTextColor(Color background)
        {
            double luminance = (0.299D * background.R) + (0.587D * background.G) + (0.114D * background.B);
            return luminance > 150D ? Color.Black : Color.White;
        }

        private static void DrawCheckerboard(Graphics graphics, Rectangle bounds)
        {
            const int cell = 12;
            using (Brush light = new SolidBrush(Color.FromArgb(247, 247, 247)))
            using (Brush dark = new SolidBrush(Color.FromArgb(218, 223, 229)))
            {
                graphics.FillRectangle(light, bounds);
                int y;
                for (y = bounds.Top; y < bounds.Bottom; y += cell)
                {
                    int x;
                    for (x = bounds.Left; x < bounds.Right; x += cell)
                    {
                        int column = (x - bounds.Left) / cell;
                        int row = (y - bounds.Top) / cell;
                        if (((column + row) & 1) != 0)
                        {
                            graphics.FillRectangle(
                                dark,
                                x,
                                y,
                                Math.Min(cell, bounds.Right - x),
                                Math.Min(cell, bounds.Bottom - y));
                        }
                    }
                }
            }
        }

        private void DrawCenteredText(Graphics graphics, Rectangle bounds, string text, Color color)
        {
            using (StringFormat format = new StringFormat())
            using (Brush brush = new SolidBrush(color))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                graphics.DrawString(text, Font, brush, bounds, format);
            }
        }

        private static Panel CreateCardPanel()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            return panel;
        }

        private Label CreateHeading(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font(Font, FontStyle.Bold);
            label.ForeColor = Color.FromArgb(31, 41, 55);
            return label;
        }

        private static Label CreateStatusLabel(FontStyle style)
        {
            Label label = new Label();
            label.AutoSize = true;
            label.Font = new Font("Microsoft YaHei UI", 9F, style, GraphicsUnit.Point);
            label.ForeColor = Color.FromArgb(31, 41, 55);
            return label;
        }

        private static Label CreateFieldLabel(string text, int x, int y)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Location = new Point(x, y);
            return label;
        }

        private static Label CreateUnitLabel(string text, int x, int y)
        {
            Label label = CreateFieldLabel(text, x, y);
            label.ForeColor = Color.FromArgb(100, 116, 139);
            return label;
        }

        private static GroupBox CreateGroup(string title, int width, int height)
        {
            GroupBox group = new GroupBox();
            group.Text = title;
            group.Size = new Size(width, height);
            group.Margin = new Padding(3, 3, 3, 8);
            return group;
        }

        private static NumericUpDown CreatePixelInput()
        {
            NumericUpDown input = new NumericUpDown();
            input.Minimum = 0;
            input.Maximum = 100000;
            input.DecimalPlaces = 0;
            input.ThousandsSeparator = true;
            input.Size = new Size(88, 25);
            return input;
        }

        private static Button CreateButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 32;
            button.Margin = new Padding(4, 2, 4, 2);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.BackColor = Color.White;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static Button CreateSmallButton(string text)
        {
            Button button = CreateButton(text, 63);
            button.Height = 27;
            button.Margin = new Padding(2, 1, 2, 1);
            button.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
            return button;
        }

        private sealed class PreviewCanvas : Panel
        {
            private readonly MainForm _owner;

            internal PreviewCanvas(MainForm owner)
            {
                _owner = owner;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                _owner.DrawPreview(e.Graphics, ClientRectangle);
            }
        }

        private sealed class ImageEntry : IDisposable
        {
            internal readonly string Path;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly Bitmap Thumbnail;
            internal ListViewItem Item;

            internal ImageEntry(string path, int width, int height, Bitmap thumbnail)
            {
                Path = path;
                Width = width;
                Height = height;
                Thumbnail = thumbnail;
            }

            public void Dispose()
            {
                Thumbnail.Dispose();
            }
        }

        private sealed class NaturalPathComparer : IComparer<string>
        {
            public int Compare(string first, string second)
            {
                try
                {
                    return StrCmpLogicalW(Path.GetFileName(first), Path.GetFileName(second));
                }
                catch
                {
                    return StringComparer.CurrentCultureIgnoreCase.Compare(first, second);
                }
            }
        }
    }
}
