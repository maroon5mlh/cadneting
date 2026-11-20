using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
        public CheckBox PreferLargeCheck;
        public TextBox KeywordBox;
        public Button RunButton;
        public DataGridView PlanGrid;
        public Button ApplyPlanButton;
        public DataGridView ResultGrid;
        public Label StatusLabel;

        private ToolTip _tip;

        /// <summary>最近一次比出的候选方案，下标与 PlanGrid 的行一一对应</summary>
        private List<NestPlan> _plans = new List<NestPlan>();

        /// <summary>最近一次排样的零件件数，用于状态栏文案</summary>
        private int _partCount;

        /// <summary>装载/恢复配置期间为 true，避免控件的 Changed 事件把半成品存盘</summary>
        private bool _loading;

        /// <summary>板材表格所在行的样式，行数变化时需要改它的高度</summary>
        private RowStyle _sheetRowStyle;

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
                RowCount = 11,
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

            // 常用规格先摆三行，实际内容在构造末尾由 PanelSettings 恢复
            // （用户改过的规格会被原样读回，包括增删的条目）
            foreach (PanelSheetRow r in PanelSettings.CreateDefault().Sheets)
                SheetGrid.Rows.Add(r.Use, r.Name, PanelSettings.Num(r.W), PanelSettings.Num(r.H));


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
                RowCount = 4,
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
                Value = 0M,          // 默认零：这批件走共边，间距会白白吃掉利用率
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
                Checked = false,     // 默认不转：有纹向/折弯方向要求时乱转是废料
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

            // ToolTip 必须先建：下面几个控件的说明都挂在它上面，
            // 而且必须留字段引用，否则被 GC 后提示就消失了
            _tip = new ToolTip();

            paramGrid.Controls.Add(spacingPanel, 0, 0);
            paramGrid.Controls.Add(marginPanel, 1, 0);
            paramGrid.Controls.Add(RotateCheck, 0, 1);
            paramGrid.Controls.Add(FollowText, 1, 1);

            // 大板优先：勾了多种规格时的用板策略。
            // 默认开：只有小板放不下的件才开大板，开出来立刻用小件把空隙填满。
            // 关掉换成"逐件用能装下它的最小板"（贪心，有时会多出几张板）。
            PreferLargeCheck = new CheckBox
            {
                Text = "大板优先",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };
            if (_tip != null)
            {
                _tip.SetToolTip(PreferLargeCheck,
                    "勾选多种规格时怎么用板。\n" +
                    "开：只有小板放不下的件才开大板，这张板剩下的空隙立刻用小件填满；\n" +
                    "     等剩余件都能进小板了，后面就改用小板。\n" +
                    "关：每一件都用能装下它的最小板（逐件贪心）。");
            }
            var preferHint = new Label
            {
                Text = "只装非它不可的件",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 8, 0, 0)
            };
            paramGrid.Controls.Add(PreferLargeCheck, 0, 2);
            paramGrid.Controls.Add(preferHint, 1, 2);

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
            _tip.SetToolTip(KeywordBox, "块名包含任一关键字即允许 90° 旋转，不受上面的全局开关影响。\n用顿号、逗号或空格分隔。");

            paramGrid.Controls.Add(kwLabel, 0, 3);
            paramGrid.Controls.Add(KeywordBox, 1, 3);

            // ---- 主按钮 ----
            RunButton = new Button
            {
                Text = "选择图形并对比方案",
                Dock = DockStyle.Fill,
                Height = 42,
                Margin = new Padding(0, 0, 0, 8),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily,
                                 SystemFonts.MessageBoxFont.Size + 1.5f, FontStyle.Bold)
            };

            if (_tip != null)
                _tip.SetToolTip(RunButton,
                    "选块 → 提取 → 多方案对比 → 按推荐方案摆放。\n摆放前会在图上要求点一个插入点（第一张板左下角）。");

            // ---- 方案对比 ----
            // 标题行右侧塞「应用选中」，比单独占一行省 30px 高度：
            // 面板最窄 380、默认高 560，多一行就会把下方结果表挤成一条缝
            var planTitleRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0)
            };
            planTitleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            planTitleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var planTitle = MakeSectionLabel("方案对比");
            planTitle.Margin = new Padding(0);

            ApplyPlanButton = MakeSmallButton("应用选中");
            ApplyPlanButton.Enabled = false;
            ApplyPlanButton.Margin = new Padding(0, 0, 0, 0);
            planTitleRow.Controls.Add(planTitle, 0, 0);
            planTitleRow.Controls.Add(ApplyPlanButton, 1, 0);

            // 关键交互提前说清楚：按下去之后不是立刻摆放，而是先要在图上点插入点
            if (_tip != null)
                _tip.SetToolTip(ApplyPlanButton,
                    "应用后到图上点一下插入点（第一张板的左下角）。\n拖动时能看到板框预览；回车沿用上次位置，ESC 取消。");

            PlanGrid = new DataGridView
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
                MultiSelect = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 26
            };
            PlanGrid.RowsDefaultCellStyle.Font = SystemFonts.MessageBoxFont;

            var planName = new DataGridViewTextBoxColumn { HeaderText = "方案", FillWeight = 40 };
            var planSheets = new DataGridViewTextBoxColumn { HeaderText = "张", FillWeight = 10 };
            var planUtil = new DataGridViewTextBoxColumn { HeaderText = "利用率", FillWeight = 16 };
            var planArea = new DataGridViewTextBoxColumn { HeaderText = "用料 m²", FillWeight = 16 };
            var planMix = new DataGridViewTextBoxColumn { HeaderText = "规格", FillWeight = 18 };
            planSheets.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            planUtil.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            planArea.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            planMix.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

            PlanGrid.Columns.AddRange(planName, planSheets, planUtil, planArea, planMix);
            foreach (DataGridViewColumn c in PlanGrid.Columns)
                c.SortMode = DataGridViewColumnSortMode.NotSortable;

            PlanGrid.SelectionChanged += OnPlanSelected;

            // ---- 结果 ----
            var resTitle = MakeSectionLabel("结果");

            ResultGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                // 这块是"每张板还剩多少"的唯一入口，被挤成一条缝等于没有：
                // 表头 + 3 行是下限，面板再矮也不再压缩它（宁可整体出现滚动条）
                MinimumSize = new Size(0, 96),
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
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "板号", FillWeight = 14 });
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "规格", FillWeight = 32 });
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "件数", FillWeight = 14 });
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "利用率", FillWeight = 18 });
            // 余料 = 还能不能再塞东西的面积。和「利用率」不是一回事：
            // 利用率按净面积算，异形件的凹陷不算浪费；余料只问"这块板还剩多大空地"
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "余料 m²", FillWeight = 22 });
            foreach (DataGridViewColumn c in ResultGrid.Columns)
            {
                c.SortMode = DataGridViewColumnSortMode.NotSortable;
                if (c.HeaderText == "件数" || c.HeaderText == "利用率" || c.HeaderText == "余料 m²")
                    c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }

            StatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 40,
                Text = "未排样",
                ForeColor = SystemColors.GrayText,
                TextAlign = ContentAlignment.MiddleLeft,
                // 状态行会换到两行（件数/张数/利用率 +「｜方案说明」），
                // 宁可截断也不要让第二行压到结果表上
                AutoEllipsis = true
            };

            root.Controls.Add(sheetTitle, 0, 0);
            root.Controls.Add(SheetGrid, 0, 1);
            root.Controls.Add(sheetBtnRow, 0, 2);
            root.Controls.Add(paramTitle, 0, 3);
            root.Controls.Add(paramGrid, 0, 4);
            root.Controls.Add(RunButton, 0, 5);
            root.Controls.Add(planTitleRow, 0, 6);
            root.Controls.Add(PlanGrid, 0, 7);
            root.Controls.Add(resTitle, 0, 8);
            // 状态行放在结果表**上面**（当表的小标题用），不放在最底下：
            // 结果表是 Percent 行，面板被压矮时它会溢出 —— 放在末尾只会被裁掉一截，
            // 而放在状态行上面就会把它压住，看起来像"表格没出来"。
            root.Controls.Add(StatusLabel, 0, 9);
            root.Controls.Add(ResultGrid, 0, 10);

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 0 title
            _sheetRowStyle = new RowStyle(SizeType.Absolute, 104);    // 1 sheet grid（行数变了要跟着调）
            root.RowStyles.Add(_sheetRowStyle);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 2 buttons
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 3 title
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 4 params
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 5 run
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 6 plan title
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112)); // 7 plan grid
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 8 title
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // 9 status
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 10 results

            Controls.Add(root);

            UpdateSheetGridHeight();
            HookPersistence();
            LoadSettings();

            ResumeLayout(true);
        }

        /// <summary>
        /// 板材表格高度跟随行数。第 1 行是 Absolute 定高，不跟着改的话
        /// 加了几行就会被裁掉，而面板看起来"就是少一行"，很容易误判成数据没存上。
        /// </summary>
        private void UpdateSheetGridHeight()
        {
            int rows = 0;
            foreach (DataGridViewRow r in SheetGrid.Rows) if (!r.IsNewRow) rows++;
            int h = 30 + rows * 23;                 // 表头 + 每行约 23px
            if (h < 70) h = 70;
            if (h > 240) h = 240;                   // 再高就挤掉结果区，改为滚动
            if (_sheetRowStyle != null) _sheetRowStyle.Height = h;
            SheetGrid.Height = h;
        }

        // ------------------------------------------------------------------
        // 参数持久化
        // ------------------------------------------------------------------

        private void HookPersistence()
        {
            SpacingBox.ValueChanged += OnAnySettingChanged;
            MarginBox.ValueChanged += OnAnySettingChanged;
            RotateCheck.CheckedChanged += OnAnySettingChanged;
            FollowText.CheckedChanged += OnAnySettingChanged;
            PreferLargeCheck.CheckedChanged += OnAnySettingChanged;
            KeywordBox.Leave += OnAnySettingChanged;

            // 勾选框必须显式提交，否则 CellValueChanged 要等离开单元格才触发，
            // 用户勾完直接关 CAD 就会丢
            SheetGrid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (SheetGrid.IsCurrentCellDirty)
                    SheetGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            SheetGrid.CellValueChanged += OnAnySettingChanged;
            SheetGrid.CellEndEdit += OnAnySettingChanged;
            SheetGrid.RowsAdded += (s, e) => { UpdateSheetGridHeight(); SaveSettings(); };
            SheetGrid.RowsRemoved += (s, e) => { UpdateSheetGridHeight(); SaveSettings(); };
        }

        private void OnAnySettingChanged(object sender, EventArgs e)
        {
            SaveSettings();
        }

        /// <summary>把面板当前状态写盘。失败静默（原因在 PanelSettings.LastError，NESTDIAG 可查）</summary>
        public void SaveSettings()
        {
            if (_loading) return;
            CollectSettings().Save();
        }

        /// <summary>面板 → 配置对象</summary>
        public PanelSettings CollectSettings()
        {
            var st = new PanelSettings
            {
                SpacingMm = (double)SpacingBox.Value,
                MarginMm = (double)MarginBox.Value,
                AllowRotate90 = RotateCheck.Checked,
                FollowText = FollowText.Checked,
                SheetPreferLarge = PreferLargeCheck.Checked,
                Keywords = KeywordBox.Text ?? ""
            };

            foreach (DataGridViewRow row in SheetGrid.Rows)
            {
                if (row.IsNewRow) continue;

                object use = row.Cells["Use"].Value;
                string name = Convert.ToString(row.Cells["Name"].Value) ?? "";
                double w, h;
                double.TryParse(Convert.ToString(row.Cells["W"].Value),
                                NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out w);
                double.TryParse(Convert.ToString(row.Cells["H"].Value),
                                NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out h);

                st.Sheets.Add(new PanelSheetRow
                {
                    Use = use != null && Convert.ToBoolean(use),
                    Name = name,
                    W = w,
                    H = h
                });
            }
            return st;
        }

        /// <summary>配置对象 → 面板。文件不存在或损坏时保持面板默认值</summary>
        private void LoadSettings()
        {
            PanelSettings saved = PanelSettings.TryLoad();
            if (saved == null) return;

            _loading = true;
            try
            {
                if (saved.Sheets != null && saved.Sheets.Count > 0)
                {
                    SheetGrid.Rows.Clear();
                    foreach (PanelSheetRow r in saved.Sheets)
                    {
                        if (r.W <= 0 || r.H <= 0) continue;   // 坏行直接丢，别让面板出现 0 宽板
                        SheetGrid.Rows.Add(r.Use, r.Name,
                                           PanelSettings.Num(r.W), PanelSettings.Num(r.H));
                    }
                    UpdateSheetGridHeight();
                }

                SpacingBox.Value = Clamp(SpacingBox, saved.SpacingMm);
                MarginBox.Value = Clamp(MarginBox, saved.MarginMm);
                RotateCheck.Checked = saved.AllowRotate90;
                FollowText.Checked = saved.FollowText;
                PreferLargeCheck.Checked = saved.SheetPreferLarge;

                if (!string.IsNullOrWhiteSpace(saved.Keywords)) KeywordBox.Text = saved.Keywords;
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>把配置值夹到 NumericUpDown 的合法区间，越界会抛 ArgumentOutOfRange</summary>
        private static decimal Clamp(NumericUpDown box, double v)
        {
            double x = double.IsNaN(v) || double.IsInfinity(v) ? (double)box.Minimum : v;
            if (x < (double)box.Minimum) x = (double)box.Minimum;
            if (x > (double)box.Maximum) x = (double)box.Maximum;
            return (decimal)x;
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
                SheetPreferLarge = PreferLargeCheck.Checked,
                Objective = "sheets"
            };

            string raw = KeywordBox.Text ?? "";
            var words = SplitWords(raw);

            // 面板清空 = 用回默认关键字。这里不做"空表示一个都不强制"：
            // 那样用户误删一行就会静默丢掉几个点的利用率，很难联想到原因。
            if (words.Count > 0) st.RotateKeywords = words;

            return st;
        }

        /// <summary>把「A、B，C D」这类输入拆成关键字列表</summary>
        private static List<string> SplitWords(string raw)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(raw)) return words;

            foreach (string s in raw.Split(new[] { '、', ',', '，', ';', '；', ' ', '\t' },
                                           StringSplitOptions.RemoveEmptyEntries))
            {
                string t = s.Trim();
                if (t.Length > 0) words.Add(t);
            }
            return words;
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
                // 固定用 InvariantCulture 解析：面板写入的是 "1220.5"，
                // 跟随区域设置解析时，逗号小数点区域会把 1220,5 解析成 12205
                if (!double.TryParse(Convert.ToString(row.Cells["W"].Value),
                                     NumberStyles.Any, CultureInfo.InvariantCulture, out w)) continue;
                if (!double.TryParse(Convert.ToString(row.Cells["H"].Value),
                                     NumberStyles.Any, CultureInfo.InvariantCulture, out h)) continue;
                if (w <= 0 || h <= 0) continue;

                list.Add(new SheetSpec { Name = name, Width = w, Height = h });
            }
            return list;
        }

        /// <param name="suffix">
        /// 附加在状态栏末尾的说明（方案的差异描述、同解提示）。
        /// 由调用方传进来而不是在这里回头查方案，是因为"应用方案"和"选中预览"
        /// 两条路径都会刷这块状态栏，谁刷谁负责说清楚自己在展示什么。
        /// </param>
        public void ShowResult(NestResult result, int partCount, string suffix = null)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => ShowResult(result, partCount, suffix))); return; }

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
                                    s.Items.Count.ToString(), s.Utilization.ToString("P1"),
                                    (s.FreeArea / 1e6).ToString("F2"));
            }

            StatusLabel.ForeColor = Color.SeaGreen;
            StatusLabel.Text = string.Format("{0} 件 → {1} 张板，利用率 {2:P1}，{3} ms",
                partCount, result.Sheets.Count, result.TotalUtilization, result.ElapsedMs);

            if (result.Warnings != null && result.Warnings.Count > 0)
                StatusLabel.Text += "（有提示）";

            if (!string.IsNullOrEmpty(suffix))
                StatusLabel.Text += "　｜　" + suffix;
        }

        public void SetBusy(bool busy)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetBusy(busy))); return; }
            RunButton.Enabled = !busy;
            RunButton.Text = busy ? "排样中…" : "选择图形并对比方案";
            ApplyPlanButton.Enabled = !busy && SelectedPlan != null;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        // ------------------------------------------------------------------
        // 方案对比
        // ------------------------------------------------------------------

        /// <summary>面板当前选中的方案；没有跑过对比时为 null</summary>
        public NestPlan SelectedPlan
        {
            get
            {
                if (_plans.Count == 0) return null;
                DataGridViewRow row = PlanGrid.CurrentRow;
                if (row == null) return null;

                int i = row.Index;
                // 行序与 _plans 一一对应，越界说明表格被外部清过，宁可返回 null 也不猜
                if (i < 0 || i >= _plans.Count) return null;
                return _plans[i];
            }
        }

        public int PartCount { get { return _partCount; } }

        public void ShowPlans(List<NestPlan> plans, int partCount)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => ShowPlans(plans, partCount))); return; }

            _plans = plans == null ? new List<NestPlan>() : plans;
            _partCount = partCount;

            PlanGrid.Rows.Clear();
            foreach (NestPlan p in _plans)
            {
                string name = (p.IsRecommended ? "★ " : "") + p.Name + (p.ChangesCraft ? " ⚠" : "");
                int i = PlanGrid.Rows.Add(
                    name,
                    p.HasError ? "-" : p.SheetCount.ToString(),
                    p.HasError ? "-" : p.Utilization.ToString("P1"),
                    p.HasError ? "-" : p.SheetArea.ToString("F2"),
                    p.HasError ? "失败" : p.SpecMix);

                DataGridViewRow row = PlanGrid.Rows[i];
                row.Tag = p;

                if (p.HasError)
                {
                    row.DefaultCellStyle.ForeColor = Color.Firebrick;
                }
                else                 if (p.ChangesCraft)
                {
                    // 改工艺前提的方案：橙色 + 提示。不能靠"用户自己会看说明"，
                    // 这个方案通常张数最少，不标出来一定有人直接拿去切
                    row.DefaultCellStyle.ForeColor = Color.FromArgb(200, 100, 0);
                }
            }

            // 默认选推荐项，没有就选第一行（用户按下按钮时应该已经看到最优解）
            int sel = 0;
            for (int i = 0; i < _plans.Count; i++)
                if (_plans[i].IsRecommended) { sel = i; break; }

            if (PlanGrid.Rows.Count > 0)
            {
                PlanGrid.Rows[sel].Selected = true;
                PlanGrid.CurrentCell = PlanGrid.Rows[sel].Cells[0];
            }

            ApplyPlanButton.Enabled = SelectedPlan != null && !SelectedPlan.HasError;
        }

        private void OnPlanSelected(object sender, EventArgs e)
        {
            NestPlan p = SelectedPlan;
            if (p == null) return;

            ApplyPlanButton.Enabled = !p.HasError;

            // 选中即预览：结果表跟着切到该方案，不用先应用再撤销
            if (!p.HasError)
            {
                string note = p.Detail;
                if (p.SameAs.Count > 0)
                    note += "（与「" + string.Join("、", p.SameAs) + "」结果相同）";
                ShowResult(p.Result, _partCount, note);
            }

            if (_tip != null)
                _tip.SetToolTip(PlanGrid, p.Detail ?? "");
        }
    }
}
