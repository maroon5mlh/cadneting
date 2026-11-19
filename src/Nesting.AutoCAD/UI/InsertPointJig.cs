using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Nesting.AutoCAD.UI
{
    /// <summary>
    /// 排样插入点拾取：拖动鼠标时实时预览第一张板框，点下即定。
    ///
    /// 为什么用 Jig 而不是 GetPoint：图纸上零件本身就很密，光看十字光标
    /// 落点根本判断不出整排板会盖住哪些已有图元，点完才发现压住了东西。
    /// 带一个真实尺寸的板框跟着走，位置对不对一眼就能看出来。
    ///
    /// 只预览第一张板：插入点定义的是「第一张板左下角」，其余板按板材宽度
    /// 向右排开，第一张板的位置定了，整组的落位也就定了。
    /// </summary>
    internal sealed class InsertPointJig : EntityJig
    {
        private readonly double _w;
        private readonly double _h;

        /// <summary>UCS → WCS。jig 采到的点是当前 UCS 坐标，板框必须落在世界坐标里画</summary>
        private readonly Matrix3d _ucs;

        private Point3d _pos;

        /// <summary>用户是按回车（沿用上次）而不是 ESC —— 两者在 jig 里都表现为 Cancel</summary>
        private bool _keepPrevious;

        public InsertPointJig(double width, double height, Point3d start, Matrix3d ucs)
            : base(NewBox(0, 0, width, height))
        {
            _w = width > 1 ? width : 1;
            _h = height > 1 ? height : 1;
            _ucs = ucs;
            _pos = new Point3d(start.X, start.Y, 0);
            UpdateGeometry();
        }

        /// <summary>用户最终确认的插入点（WCS）</summary>
        public Point3d Position { get { return _pos; } }

        /// <summary>true = 空回车，调用方应沿用上次的插入点而不是当作取消</summary>
        public bool KeepPrevious { get { return _keepPrevious; } }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var opt = new JigPromptPointOptions("\n指定插入点：第一张板的左下角（回车沿用上次，ESC 取消）")
            {
                UseBasePoint = false,
                AppendKeywordsToMessage = false
            };
            // 不用 NoZeroResponseAccepted：原点本身是合法插入点，禁掉反而让人点不上。
            // NullResponseAccepted 让空回车先返回 None，再由我们转成"沿用上次"。
            opt.UserInputControls = UserInputControls.Accept3dCoordinates
                                  | UserInputControls.NullResponseAccepted;

            PromptPointResult r = prompts.AcquirePoint(opt);

            // ESC 与空回车在 jig 里都是 Cancel，只能靠状态区分：
            // 返回 Cancel 才能可靠地结束拖动（返回 NoChange 会继续等下一个点，用户会卡住）
            if (r.Status == PromptStatus.None)
            {
                _keepPrevious = true;
                return SamplerStatus.Cancel;
            }
            if (r.Status != PromptStatus.OK) return SamplerStatus.Cancel;

            Point3d p = r.Value;
            try { p = p.TransformBy(_ucs); } catch { /* UCS 取不到就按世界坐标用 */ }

            if (Math.Abs(p.X - _pos.X) < 1e-9 && Math.Abs(p.Y - _pos.Y) < 1e-9)
                return SamplerStatus.NoChange;   // 点没动就不刷预览，避免整屏重画

            _pos = new Point3d(p.X, p.Y, 0);
            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            UpdateGeometry();
            return true;
        }

        private void UpdateGeometry()
        {
            var pl = Entity as Polyline;
            if (pl == null || pl.NumberOfVertices < 4) return;

            pl.SetPointAt(0, new Point2d(_pos.X, _pos.Y));
            pl.SetPointAt(1, new Point2d(_pos.X + _w, _pos.Y));
            pl.SetPointAt(2, new Point2d(_pos.X + _w, _pos.Y + _h));
            pl.SetPointAt(3, new Point2d(_pos.X, _pos.Y + _h));
        }

        private static Polyline NewBox(double x, double y, double w, double h)
        {
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;

            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(x + w, y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(x + w, y + h), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(x, y + h), 0, 0, 0);
            pl.Closed = true;
            pl.ColorIndex = 8;          // 和正式板框同色，预览与结果一致
            return pl;
        }
    }
}
