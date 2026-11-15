using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Nesting.Core;

namespace Nesting.AutoCAD.UI
{
    /// <summary>
    /// 排样面板。
    ///
    /// 刻意用 WinForms 而不是 WPF：PaletteSet.Add 直接接收 WinForms 控件，
    /// 不需要 ElementHost 包装，也不涉及 XAML 编译和 WPF 版本兼容，
    /// 在 AutoCAD 进程里加载最稳。面板只负责「收集参数 + 显示结果」，
    /// 所有 CAD 操作都由命令在文档上下文里完成。
    /// </summary>
    public sealed class NestPaletteControl : UserControl
    {
        public DataGridView SheetGrid;
        public NumericUpDown SpacingBox;
        public NumericUpDown MarginBox;
        public CheckBox RotateCheck;
        public CheckBox FollowText;
        public TextBox KeywordBox;
        public Button RunButton;
        public DataGridView ResultGrid;
        public Label StatusLabel;

        private ToolTip _tip;

        public NestPaletteControl()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            SuspendLayout();

            Font = SystemFonts.MessageBoxFont;
            BackColor = SystemColors.Control;
            Padding = new Padding(10, 8, 10, 8);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 9,
                BackColor = SystemColors.Control
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // ---- 板材规格 ----
            var sheetTitle = MakeSectionLabel("板材规格");

            SheetGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                EditMode = DataGridViewEditMode.EditOnEnter,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 26
            };
            SheetGrid.RowsDefaultCellStyle.Font = SystemFonts.MessageBoxFont;

            var colUse = new DataGridViewCheckBoxColumn
            {
                HeaderText = "",
                Name = "Use",
                FillWeight = 18,
                MinimumWidth = 26
            };
            var colName = new DataGridViewTextBoxColumn { HeaderText = "名称", Name = "Name", FillWeight = 42 };
            var colW = new DataGridViewTextBoxColumn { HeaderText = "宽", Name = "W", FillWeight = 20 };
            var colH = new DataGridViewTextBoxColumn { HeaderText = "高", Name = "H", FillWeight = 20 };
            colW.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            colH.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

            SheetGrid.Columns.AddRange(colUse, colName, colW, colH);
            SheetGrid.Rows.Add(true, "1220x2440", "1220", "2440");
            SheetGrid.Rows.Add(true, "1220x3050", "1220", "3050");
            SheetGrid.Height = 96;

            var sheetBtnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 4, 0, 6)
            };
            var addBtn = MakeSmallButton("添加规格");
            addBtn.Click += (s, e) => SheetGrid.Rows.Add(true, "自定义", "1220", "2440");
            var delBtn = MakeSmallButton("删除选中");
            delBtn.Click += (s, e) =>
            {
                if (SheetGrid.SelectedRows.Count > 0) SheetGrid.Rows.Remove(SheetGrid.SelectedRows[0]);
            };
            sheetBtnRow.Controls.Add(addBtn);
            sheetBtnRow.Controls.Add(delBtn);

            // ---- 参数 ----
            // 两列布局而不是单行 FlowLayout：面板最窄能到 380px，
            // 四个参数挤一行会被裁掉右边那个（表现为"留边框看不见"）
            var paramTitle = MakeSectionLabel("参数");

            var paramGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 2, 0, 8)
            };
            paramGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
            paramGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));

            // 间距
            var spacingPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0)
            };
            spacingPanel.Controls.Add(new Label
            {
                Text = "间距",
                AutoSize = true,
                Margin = new Padding(0, 8, 4, 0)
            });
            SpacingBox = new NumericUpDown
            {
                DecimalPlaces = 2,
                Minimum = 0,
                Maximum = 50,
                Increment = 0.1M,
                Value = 0.2M,
                Width = 58,
                Margin = new Padding(0, 5, 3, 0)
            };
            spacingPanel.Controls.Add(SpacingBox);
            spacingPanel.Controls.Add(new Label
            {
                Text = "mm",
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0)
            });

            // 留边
            var marginPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0)
            };
            marginPanel.Controls.Add(new Label
            {
                Text = "留边",
                AutoSize = true,
                Margin = new Padding(0, 8, 4, 0)
            });
            MarginBox = new NumericUpDown
            {
                DecimalPlaces = 1,
                Minimum = 0,
                Maximum = 200,
                Increment = 1M,
                Value = 5M,
                Width = 58,
                Margin = new Padding(0, 5, 3, 0)
            };
            marginPanel.Controls.Add(MarginBox);
            marginPanel.Controls.Add(new Label
            {
                Text = "mm",
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0)
            });

            // 旋转 / 文字跟随
            RotateCheck = new CheckBox
            {
                Text = "允许旋转 90°",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };

            FollowText = new CheckBox
            {
                Text = "文字跟随块",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };

            paramGrid.Controls.Add(spacingPanel, 0, 0);
            paramGrid.Controls.Add(marginPanel, 1, 0);
            paramGrid.Controls.Add(RotateCheck, 0, 1);
            paramGrid.Controls.Add(FollowText, 1, 1);

            // 关键字必须能改：图纸里的块名是「铰包边 / 中横板」，和口头说的
            // 「顶包边 / 中横框」经常差一个字，写死在代码里等于这个功能用不上
            var kwLabel = new Label
            {
                Text = "强制可转",
                AutoSize = true,
                Margin = new Padding(0, 8, 4, 0)
            };
            KeywordBox = new TextBox
            {
                Text = string.Join("、", RotationPolicy.DefaultKeywords),
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 5, 0, 0)
            };
            var kwTip = new ToolTip();
            kwTip.SetToolTip(KeywordBox, "块名包含任一关键字即允许 90° 旋转，不受上面的全局开关影响。\n用顿号、逗号或空格分隔。");
            _tip = kwTip;   // 保留引用，否则 ToolTip 被 GC 后提示就消失了

            paramGrid.Controls.Add(kwLabel, 0, 2);
            paramGrid.Controls.Add(KeywordBox, 1, 2);

            // ---- 主按钮 ----
            RunButton = new Button
            {
                Text = "选择图形并排样",
                Dock = DockStyle.Fill,
                Height = 42,
                Margin = new Padding(0, 0, 0, 8),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily,
                                 SystemFonts.MessageBoxFont.Size + 1.5f, FontStyle.Bold)
            };

            // ---- 结果 ----
            var resTitle = MakeSectionLabel("结果");

            ResultGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 26
            };
            ResultGrid.RowsDefaultCellStyle.Font = SystemFonts.MessageBoxFont;
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "板号", FillWeight = 18 });
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "规格", FillWeight = 40 });
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "件数", FillWeight = 18 });
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "利用率", FillWeight = 24 });
            foreach (DataGridViewColumn c in ResultGrid.Columns)
                c.SortMode = DataGridViewColumnSortMode.NotSortable;

            StatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 34,
                Text = "未排样",
                ForeColor = SystemColors.GrayText,
                TextAlign = ContentAlignment.MiddleLeft
            };

            root.Controls.Add(sheetTitle, 0, 0);
            root.Controls.Add(SheetGrid, 0, 1);
            root.Controls.Add(sheetBtnRow, 0, 2);
            root.Controls.Add(paramTitle, 0, 3);
            root.Controls.Add(paramGrid, 0, 4);
            root.Controls.Add(RunButton, 0, 5);
            root.Controls.Add(resTitle, 0, 6);
            root.Controls.Add(ResultGrid, 0, 7);
            root.Controls.Add(StatusLabel, 0, 8);

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 0 title
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104)); // 1 sheet grid
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 2 buttons
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 3 title
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 4 params
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 5 run
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 6 title
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 7 results
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // 8 status

            Controls.Add(root);
            ResumeLayout(true);
        }

        private static Label MakeSectionLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 22,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily,
                                 SystemFonts.MessageBoxFont.Size, FontStyle.Bold),
                Margin = new Padding(0, 2, 0, 2),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private static Button MakeSmallButton(string text)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                Height = 26,
                Margin = new Padding(0, 0, 6, 0),
                FlatStyle = FlatStyle.System
            };
        }

        /// <summary>读取面板上的参数，作废的行会被忽略</summary>
        public NestSettings ReadSettings()
        {
            var st = new NestSettings
            {
                SpacingMm = (double)SpacingBox.Value,
                MarginMm = (double)MarginBox.Value,
                AllowRotate90 = RotateCheck.Checked,
                Objective = "sheets"
            };

            string raw = KeywordBox.Text ?? "";
            var words = new List<string>();
            foreach (string s in raw.Split(new[] { '、', ',', '，', ';', '；', ' ', '\t' },
                                           StringSplitOptions.RemoveEmptyEntries))
            {
                string t = s.Trim();
                if (t.Length > 0) words.Add(t);
            }

            // 面板清空 = 用回默认关键字。这里不做"空表示一个都不强制"：
            // 那样用户误删一行就会静默丢掉几个点的利用率，很难联想到原因。
            if (words.Count > 0) st.RotateKeywords = words;

            return st;
        }

        /// <summary>读取面板上的板材规格</summary>
        public List<SheetSpec> ReadSheets()
        {
            var list = new List<SheetSpec>();
            foreach (DataGridViewRow row in SheetGrid.Rows)
            {
                if (row.IsNewRow) continue;

                object use = row.Cells["Use"].Value;
                if (use == null || !Convert.ToBoolean(use)) continue;

                string name = Convert.ToString(row.Cells["Name"].Value) ?? "板材";
                double w, h;
                if (!double.TryParse(Convert.ToString(row.Cells["W"].Value), out w)) continue;
                if (!double.TryParse(Convert.ToString(row.Cells["H"].Value), out h)) continue;
                if (w <= 0 || h <= 0) continue;

                list.Add(new SheetSpec { Name = name, Width = w, Height = h });
            }
            return list;
        }

        public void ShowResult(NestResult result, int partCount)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => ShowResult(result, partCount))); return; }

            ResultGrid.Rows.Clear();

            if (result == null)
            {
                StatusLabel.ForeColor = SystemColors.GrayText;
                StatusLabel.Text = "未排样";
                return;
            }

            if (result.Error != null)
            {
                StatusLabel.ForeColor = Color.Firebrick;
                StatusLabel.Text = "失败：" + result.Error;
                return;
            }

            foreach (SheetResult s in result.Sheets)
            {
                ResultGrid.Rows.Add((s.Index + 1).ToString(), s.SheetName,
                                    s.Items.Count.ToString(), s.Utilization.ToString("P1"));
            }

            StatusLabel.ForeColor = Color.SeaGreen;
            StatusLabel.Text = string.Format("{0} 件 → {1} 张板，利用率 {2:P1}，{3} ms",
                partCount, result.Sheets.Count, result.TotalUtilization, result.ElapsedMs);

            if (result.Warnings != null && result.Warnings.Count > 0)
                StatusLabel.Text += "（有提示）";
        }

        public void SetBusy(bool busy)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetBusy(busy))); return; }
            RunButton.Enabled = !busy;
            RunButton.Text = busy ? "排样中…" : "选择图形并排样";
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }
    }
}
