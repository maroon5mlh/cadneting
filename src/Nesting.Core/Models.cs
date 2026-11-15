using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nesting.Core
{
    /// <summary>平面点（单位：世界坐标系毫米）</summary>
    public struct Vec2
    {
        public double X;
        public double Y;

        public Vec2(double x, double y) { X = x; Y = y; }

        public double DistanceTo(Vec2 o)
        {
            double dx = X - o.X, dy = Y - o.Y;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }

        public override string ToString() { return X.ToString("F4") + "," + Y.ToString("F4"); }
    }

    /// <summary>
    /// 闭合轮廓。Points 为首尾不重复的点序列（最后一点隐含连回第一点）。
    /// 逆时针为正向，SignedArea > 0。
    /// </summary>
    public sealed class Contour
    {
        public Vec2[] Points;

        public Contour() { Points = new Vec2[0]; }
        public Contour(Vec2[] pts) { Points = pts ?? new Vec2[0]; }

        [JsonIgnore]
        public int Count { get { return Points == null ? 0 : Points.Length; } }

        /// <summary>有符号面积，逆时针为正（鞋带公式）</summary>
        [JsonIgnore]
        public double SignedArea
        {
            get
            {
                if (Count < 3) return 0;
                double a = 0;
                for (int i = 0; i < Count; i++)
                {
                    Vec2 p = Points[i];
                    Vec2 q = Points[(i + 1) % Count];
                    a += p.X * q.Y - q.X * p.Y;
                }
                return a * 0.5;
            }
        }

        [JsonIgnore]
        public double AbsArea { get { return System.Math.Abs(SignedArea); } }

        public void GetBounds(out Vec2 min, out Vec2 max)
        {
            if (Count == 0) { min = new Vec2(0, 0); max = new Vec2(0, 0); return; }
            double x0 = double.MaxValue, y0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i < Count; i++)
            {
                Vec2 p = Points[i];
                if (p.X < x0) x0 = p.X;
                if (p.Y < y0) y0 = p.Y;
                if (p.X > x1) x1 = p.X;
                if (p.Y > y1) y1 = p.Y;
            }
            min = new Vec2(x0, y0);
            max = new Vec2(x1, y1);
        }

        public Contour TranslatedBy(Vec2 d)
        {
            var pts = new Vec2[Count];
            for (int i = 0; i < Count; i++) pts[i] = new Vec2(Points[i].X + d.X, Points[i].Y + d.Y);
            return new Contour(pts);
        }

        /// <summary>射线法判断点是否在轮廓内部</summary>
        public bool Contains(Vec2 p)
        {
            bool inside = false;
            for (int i = 0, j = Count - 1; i < Count; j = i++)
            {
                Vec2 pi = Points[i], pj = Points[j];
                if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                    p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y + 1e-30) + pi.X)
                    inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>零件品种：几何完全一致的零件归并为一个品种，Quantity 为数量</summary>
    public sealed class PartType
    {
        /// <summary>品种编号，如 P001</summary>
        public string Id;

        /// <summary>来源块名，仅用于报告与排查</summary>
        public string BlockName;

        /// <summary>外轮廓（已归一到包围盒左下角为原点）</summary>
        public Contour Outer;

        /// <summary>内孔/内腔轮廓</summary>
        public List<Contour> Holes = new List<Contour>();

        /// <summary>外轮廓面积（扣孔后的净面积），用于计算利用率</summary>
        public double NetArea;

        /// <summary>外轮廓包围盒宽高</summary>
        public double Width;
        public double Height;

        /// <summary>是否允许 90 度旋转（有纹理方向/折弯方向要求的件设为 false）</summary>
        public bool AllowRotate90 = true;

        /// <summary>该品种数量</summary>
        public int Quantity = 1;

        /// <summary>该品种每个实例对应的 AutoCAD 实体句柄，与 Quantity 顺序一一对应</summary>
        public List<string> Handles = new List<string>();

        /// <summary>
        /// 与 Handles 一一对应的「跟随文字」句柄。
        /// 每个元素是一个实例上所有文字的句柄，多个用英文逗号分隔，空串表示该件没有文字。
        /// 用「平行列表 + 逗号拼接」而不是嵌套列表，是为了 JSON 可读、且顺序与 Handles 严格对齐
        /// —— 对齐一旦错位，文字就会跟错零件，而且很难一眼看出来。
        /// </summary>
        public List<string> TextHandles = new List<string>();

        /// <summary>
        /// 包围盒填充率 = 净面积 / (包围盒宽×高)。
        /// 接近 1 说明零件"基本是矩形"，包围盒排样几乎不浪费；
        /// 明显小于 1（L 形、U 形）说明需要 NFP 才能排紧。
        /// 由引擎计算，不参与序列化输入。
        /// </summary>
        [JsonIgnore]
        public double BboxFillRatio = 1.0;
    }

    /// <summary>板材规格</summary>
    public sealed class SheetSpec
    {
        public string Name;
        public double Width;
        public double Height;
        /// <summary>可用张数上限，默认不限</summary>
        public int MaxCount = int.MaxValue;
        /// <summary>单张价格，用于按成本择优；为 0 时按张数择优</summary>
        public double Price = 0;
    }

    public sealed class NestSettings
    {
        /// <summary>零件间距（mm）。0 表示严格共边；建议激光薄板取 0~0.2</summary>
        public double SpacingMm = 0.2;

        /// <summary>
        /// 留边（mm）：零件包围盒到板四边的距离，默认 5。
        /// 四边等距而不是只留左边和下边 —— 板边不整齐、夹具和吸附边缘都需要余量，
        /// 只留两个方向会让贴着上/右边的零件切不出来。
        /// </summary>
        public double MarginMm = 5;

        /// <summary>全局是否允许 90 度旋转（单件可通过 PartType.AllowRotate90 单独关闭）</summary>
        public bool AllowRotate90 = true;

        /// <summary>
        /// 块名命中这些关键字的零件强制允许 90° 旋转，**不受 AllowRotate90 全局开关影响**。
        /// 这些件没有纹向/折弯方向约束，能转就转，利用率提升明显。
        /// </summary>
        public List<string> RotateKeywords = new List<string>(RotationPolicy.DefaultKeywords);

        /// <summary>引擎计算时间上限（秒），到点取当前最优解</summary>
        public int TimeLimitSec = 60;

        /// <summary>优化目标：sheets=最少张数，cost=最低成本，area=最高利用率</summary>
        public string Objective = "sheets";

        /// <summary>轮廓离散为折线的精度（mm）</summary>
        public double FlattenTolerance = 0.1;
    }

    /// <summary>排样任务，序列化后作为引擎输入</summary>
    public sealed class NestJob
    {
        public string Unit = "mm";
        /// <summary>图纸标识 / 批次号</summary>
        public string BatchId;
        public List<SheetSpec> Sheets = new List<SheetSpec>();
        public List<PartType> Parts = new List<PartType>();
        public NestSettings Settings = new NestSettings();
    }

    /// <summary>单个零件的落位结果</summary>
    public sealed class Placement
    {
        public string PartTypeId;
        public string Handle;
        public int SheetIndex;
        /// <summary>落位后零件包围盒左下角的 X</summary>
        public double X;
        /// <summary>落位后零件包围盒左下角的 Y</summary>
        public double Y;
        /// <summary>额外旋转角，0 或 90</summary>
        public double RotationDeg;

        /// <summary>与这块零件一起移动的文字实体句柄（块旁的标注文字）</summary>
        public List<string> TextHandles = new List<string>();
    }

    public sealed class SheetResult
    {
        public int Index;
        public string SheetName;
        public double Width;
        public double Height;
        public double UsedArea;
        public double Utilization;
        public List<Placement> Items = new List<Placement>();
    }

    public sealed class NestResult
    {
        public List<SheetResult> Sheets = new List<SheetResult>();
        public double TotalUtilization;
        public long ElapsedMs;
        public string EngineVersion;
        public string Error;

        /// <summary>不影响出结果、但需要让用户知道的提示（异形件、近似处理等）</summary>
        public List<string> Warnings = new List<string>();
    }
}
