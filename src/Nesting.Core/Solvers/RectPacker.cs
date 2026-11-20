using System;
using System.Collections.Generic;

namespace Nesting.Core.Solvers
{
    /// <summary>自由矩形（MaxRects 的基本单元）</summary>
    public struct FRect
    {
        public double X, Y, W, H;

        public FRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }

        public bool Contains(FRect o)
        {
            return o.X >= X - 1e-9 && o.Y >= Y - 1e-9
                && o.X + o.W <= X + W + 1e-9 && o.Y + o.H <= Y + H + 1e-9;
        }
    }

    /// <summary>插入启发式</summary>
    public enum PackHeuristic
    {
        /// <summary>最贴短边：优先选不浪费短边的位置，板材利用率最高</summary>
        BestShortSideFit,
        /// <summary>最贴面积：优先选剩余面积最小的自由矩形</summary>
        BestAreaFit,
        /// <summary>左下优先：靠近板材左下角，出图好看、余料集中在右上</summary>
        BottomLeft
    }

    /// <summary>待排零件实例</summary>
    public sealed class PackItem
    {
        public int Seq;
        public PartType Part;
        public int InstanceIndex;

        /// <summary>当前朝向（放置时可能被旋转 90 度改写）</summary>
        public double W, H;
        /// <summary>原始朝向尺寸</summary>
        public double W0, H0;
        public bool CanRotate;

        /// <summary>落位结果：包围盒左下角</summary>
        public double Px, Py;
        public bool Rotated;
        public int BinIndex = -1;

        /// <summary>
        /// 能容纳该件的最小规格板（按面积），null = 任何规格都装不下。
        /// 开新板时由「剩余件里 RequiredSpec 最大的那一个」决定开哪种板：
        /// 这样 3050 只在大件真的需要时才开，开了之后空隙由小件填满，
        /// 而不是每件都各自开一张 3050。
        /// </summary>
        public SheetSpec RequiredSpec;

        /// <summary>
        /// 深拷贝。每次排样尝试必须使用独立副本：
        /// 尝试之间会覆写 Px/Py/Rotated，共享对象会让「上一次的最优解」
        /// 在后续尝试中被悄悄改写，表现为不同零件落在同一坐标上。
        /// </summary>
        public PackItem Clone()
        {
            return new PackItem
            {
                Seq = Seq,
                Part = Part,
                InstanceIndex = InstanceIndex,
                W = W0,
                H = H0,
                W0 = W0,
                H0 = H0,
                CanRotate = CanRotate,
                RequiredSpec = RequiredSpec
            };
        }
    }

    /// <summary>一张板材</summary>
    public sealed class PackBin
    {
        public double W, H;
        public string SpecName;
        public List<FRect> Free = new List<FRect>();
        public List<PackItem> Items = new List<PackItem>();
        public double UsedArea;

        /// <summary>零件到板边的留边</summary>
        public double Margin;

        public PackBin(double w, double h, string name) : this(w, h, name, 0) { }

        /// <summary>
        /// 留边通过「自由矩形的起点下移」实现，而不是在结果坐标上事后加偏移。
        /// 事后加偏移会在板材尺寸、利用率、越界判断三处各留一个坑；
        /// 让可用区域本身就是 (margin, margin) 起、尺寸减 2*margin，
        /// 之后所有坐标天然落在板内，逻辑只有一处。
        /// </summary>
        public PackBin(double w, double h, string name, double margin)
        {
            W = w; H = h; SpecName = name;
            Margin = Math.Max(0, margin);

            double uw = w - 2 * Margin;
            double uh = h - 2 * Margin;
            if (uw < 0) uw = 0;
            if (uh < 0) uh = 0;

            // 可用区为 0 时找不到任何位置，等价于"这块板放不下任何东西"
            Free.Add(new FRect(Margin, Margin, uw, uh));
        }

        public double Utilization { get { return W * H <= 0 ? 0 : UsedArea / (W * H); } }
    }

    /// <summary>
    /// MaxRects 装箱器。
    /// 无通切约束（激光切割）场景下，MaxRects 比 Guillotine 排得更紧，
    /// 因为它允许自由矩形任意分裂，不像 Guillotine 要求整刀贯穿。
    /// </summary>
    public sealed class RectPacker
    {
        private readonly double _spacing;

        public RectPacker(double spacing)
        {
            _spacing = Math.Max(0, spacing);
        }

        /// <summary>
        /// 在 bin 中寻找一个能放下 item 的位置。成功返回 true 并给出落位。
        /// 用「尺寸 + 间距」作为占位尺寸：这样相邻件的净间距恒等于 spacing，
        /// 而零件本身仍从自由矩形左下角起画，不会整体偏移。
        /// </summary>
        public bool TryFindPosition(PackBin bin, PackItem it, PackHeuristic h,
                                    out double x, out double y, out bool rotated)
        {
            x = 0; y = 0; rotated = false;

            double best1 = double.MaxValue, best2 = double.MaxValue;
            bool found = false;

            // 用原始尺寸计算占位，避免多次尝试之间 it.W/ it.H 被上一次结果污染
            double needW = it.W0 + _spacing;
            double needH = it.H0 + _spacing;
            double needWr = it.H0 + _spacing;
            double needHr = it.W0 + _spacing;

            for (int i = 0; i < bin.Free.Count; i++)
            {
                FRect fr = bin.Free[i];

                // 不旋转
                if (fr.W + 1e-9 >= needW && fr.H + 1e-9 >= needH)
                {
                    double s1, s2;
                    Score(h, fr, needW, needH, out s1, out s2);
                    if (s1 < best1 - 1e-9 || (Math.Abs(s1 - best1) <= 1e-9 && s2 < best2))
                    {
                        best1 = s1; best2 = s2;
                        x = fr.X; y = fr.Y; rotated = false; found = true;
                    }
                }

                // 旋转 90 度
                if (it.CanRotate && fr.W + 1e-9 >= needWr && fr.H + 1e-9 >= needHr)
                {
                    double s1, s2;
                    Score(h, fr, needWr, needHr, out s1, out s2);
                    // 同分时优先不旋转，减少图纸上的方向种类
                    if (s1 < best1 - 1e-9 || (Math.Abs(s1 - best1) <= 1e-9 && s2 < best2))
                    {
                        best1 = s1; best2 = s2;
                        x = fr.X; y = fr.Y; rotated = true; found = true;
                    }
                }
            }

            return found;
        }

        private static void Score(PackHeuristic h, FRect fr, double w, double hh,
                                  out double s1, out double s2)
        {
            switch (h)
            {
                case PackHeuristic.BestAreaFit:
                    s1 = fr.W * fr.H - w * hh;
                    s2 = Math.Min(fr.W - w, fr.H - hh);
                    break;

                case PackHeuristic.BottomLeft:
                    s1 = fr.Y + hh;
                    s2 = fr.X;
                    break;

                default: // BestShortSideFit
                    s1 = Math.Min(fr.W - w, fr.H - hh);
                    s2 = Math.Max(fr.W - w, fr.H - hh);
                    break;
            }
        }

        /// <summary>把 item 占位区域写入 bin，并分裂自由矩形</summary>
        public void Commit(PackBin bin, PackItem it, double x, double y, bool rotated)
        {
            double w = (rotated ? it.H0 : it.W0) + _spacing;
            double h = (rotated ? it.W0 : it.H0) + _spacing;
            var used = new FRect(x, y, w, h);

            var next = new List<FRect>(bin.Free.Count + 4);
            for (int i = 0; i < bin.Free.Count; i++)
            {
                FRect fr = bin.Free[i];
                if (!Intersects(fr, used)) { next.Add(fr); continue; }

                // 上 / 下
                if (used.X < fr.X + fr.W && used.X + used.W > fr.X)
                {
                    if (used.Y > fr.Y + 1e-9)
                        next.Add(new FRect(fr.X, fr.Y, fr.W, used.Y - fr.Y));
                    if (used.Y + used.H < fr.Y + fr.H - 1e-9)
                        next.Add(new FRect(fr.X, used.Y + used.H, fr.W,
                                           fr.Y + fr.H - (used.Y + used.H)));
                }
                // 左 / 右
                if (used.Y < fr.Y + fr.H && used.Y + used.H > fr.Y)
                {
                    if (used.X > fr.X + 1e-9)
                        next.Add(new FRect(fr.X, fr.Y, used.X - fr.X, fr.H));
                    if (used.X + used.W < fr.X + fr.W - 1e-9)
                        next.Add(new FRect(used.X + used.W, fr.Y,
                                           fr.X + fr.W - (used.X + used.W), fr.H));
                }
            }

            bin.Free = Prune(next);

            it.Px = x;
            it.Py = y;
            it.Rotated = rotated;
            it.W = rotated ? it.H0 : it.W0;
            it.H = rotated ? it.W0 : it.H0;

            bin.Items.Add(it);
            bin.UsedArea += it.Part.NetArea;
        }

        private static bool Intersects(FRect a, FRect b)
        {
            return a.X < b.X + b.W - 1e-9 && a.X + a.W > b.X + 1e-9 &&
                   a.Y < b.Y + b.H - 1e-9 && a.Y + a.H > b.Y + 1e-9;
        }

        /// <summary>剔除被其它自由矩形完全包含的项（MaxRects 标准剪枝）</summary>
        private static List<FRect> Prune(List<FRect> list)
        {
            var res = new List<FRect>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                FRect a = list[i];
                if (a.W <= 1e-9 || a.H <= 1e-9) continue;

                bool keep = true;
                for (int j = 0; j < list.Count; j++)
                {
                    if (i == j) continue;
                    FRect b = list[j];
                    if (b.W <= 1e-9 || b.H <= 1e-9) continue;
                    if (!b.Contains(a)) continue;

                    bool identical = Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9
                                  && Math.Abs(a.W - b.W) < 1e-9 && Math.Abs(a.H - b.H) < 1e-9;
                    if (!identical || j < i) { keep = false; break; }
                }
                if (keep) res.Add(a);
            }
            return res;
        }
    }
}
