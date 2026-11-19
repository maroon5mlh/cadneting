using System;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadEditor = Autodesk.AutoCAD.EditorInput.Editor;

namespace Nesting.AutoCAD.UI
{
    /// <summary>
    /// 排样面板的容器管理。PaletteSet 是 AutoCAD 的停靠面板容器，
    /// 支持拖动停靠、自动隐藏，且不阻塞命令行。
    ///
    /// 兜底：若 PaletteSet 创建失败（极少见，通常是面板 Guid 已被占用），
    /// 退化为一个独立的非模态窗口，保证界面上一定能看到东西。
    /// </summary>
    public static class NestPalette
    {
        private static PaletteSet _palette;
        private static Form _form;
        private static NestPaletteControl _control;

        public static NestPaletteControl Control { get { return _control; } }

        private static void EnsureControl()
        {
            if (_control != null) return;
            _control = new NestPaletteControl { Dock = DockStyle.Fill };
            _control.RunButton.Click += OnRunClick;
            _control.ApplyPlanButton.Click += OnApplyPlanClick;
        }

        public static void Show()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            AcadEditor ed = doc == null ? null : doc.Editor;

            try
            {
                EnsureControl();

                if (_palette == null)
                {
                    _palette = new PaletteSet("板材排样",
                        new Guid("7B3F2C91-4A6E-4C1D-9F52-8E0A5D6B7C31"));
                    _palette.Style = PaletteSetStyles.ShowPropertiesMenu
                                   | PaletteSetStyles.ShowAutoHideButton
                                   | PaletteSetStyles.ShowCloseButton;
                    // 高度从 560 提到 680：多了「方案对比」一块（标题 + 4~5 行表格），
                    // 不放高就会把下方结果表挤成一条缝，看起来像"结果没出来"
                    _palette.MinimumSize = new Size(380, 680);
                    _palette.DockEnabled = DockSides.Left | DockSides.Right;
                    _palette.Add("排样", _control);
                }

                _palette.Visible = true;
                if (ed != null)
                    ed.WriteMessage("\n排样面板已打开。若没看到，检查是否被折叠成了侧边的竖排标签。");
                return;
            }
            catch (Exception ex)
            {
                if (ed != null) ed.WriteMessage("\n停靠面板创建失败：" + ex.GetType().Name + " - " + ex.Message);
            }

            // ---- 兜底：独立窗口 ----
            try
            {
                if (_control != null && _control.Parent != null)
                {
                    if (ed != null) ed.WriteMessage("\n面板已被停靠容器占用，请重启 AutoCAD 后重试 NESTPANEL。");
                    return;
                }

                EnsureControl();

                if (_form == null)
                {
                    _form = new Form
                    {
                        Text = "板材排样",
                        Width = 440,
                        Height = 800,
                        StartPosition = FormStartPosition.CenterScreen,
                        ShowInTaskbar = false,
                        MinimizeBox = false
                    };
                    _form.Controls.Add(_control);
                    _form.FormClosed += (s, e) => { _form = null; };
                }

                _form.Show();
                if (ed != null) ed.WriteMessage("\n已改用独立窗口显示排样面板。");
            }
            catch (Exception ex2)
            {
                if (ed != null)
                    ed.WriteMessage("\n独立窗口也失败：" + ex2.Message + "\n请把这两行错误发给开发者。");
            }
        }

        private static void OnRunClick(object sender, EventArgs e)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            if (_control != null) _control.SetBusy(true);

            // 交给文档上下文执行：面板是模态外的，
            // 在控件事件里直接操作 Editor / Database 会抛 eInvalidContext
            doc.SendStringToExecute("NESTAUTO ", true, false, false);
        }

        private static void OnApplyPlanClick(object sender, EventArgs e)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            // 不置 busy：这个动作不重新排样，只改块位置，几百毫秒内就结束，
            // 置 busy 反而会闪一下按钮文字
            doc.SendStringToExecute("NESTAPPLYPLAN ", true, false, false);
        }
    }
}
