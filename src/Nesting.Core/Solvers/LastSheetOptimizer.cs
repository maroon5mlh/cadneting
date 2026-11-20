using System;
using System.Collections.Generic;

namespace Nesting.Core.Solvers
{
    /// <summary>
    /// 最后一张板的「靠左压实」：零件全部堆到左侧，右边留出一条贯穿全高的整条余料。
    ///
    /// 为什么单做这一步：MaxRects 只关心"放得下"，不管余料长什么样。
    /// 排到最后一两张板时剩下的件很少，启发式会把它们散着放（下面一件、上面一件、
    /// 右边一件），余料被切成几块短缝，每块都短到没法再用。
    /// 同样这些件如果按「从左下往右上」堆成几列，右侧就是一条 1220×板高 的长料，
    /// 车间可以直接留作下一个活的料 —— 这正是操作工说的"给右边留空"。
    ///
    /// 只改 X/Y，不改件的朝向、不改板上的件集合，所以张数和用料面积都不受影响；
    /// 判定标准只有一条：最右件的右边界（= 右侧整条余料的宽度）有没有变小。
    /// </summary>
    public static class LastSheetOptimizer
    {
        /// <summary>占用宽度至少缩小这么多才判定为改进，避免 0.001mm 级的抖动改图</summary>
        private const double ImproveMm = 1.0;

        public sealed class Compacted
        {
            /// <summary>重排后的零件（Improved=false 时是原样副本）</summary>
            public List<PackItem> Items;
            /// <summary>最右件的右边界（相对板左下角）</summary>
            public double UsedWidth;
            /// <summary>最高件的上边界</summary>
            public double UsedHeight;
            /// <summary>是否真的比原来更靠左（true 才应该替换原布局）</summary>
            public bool Improved;
        }

        /// <summary>
        /// 尝试压实一张板。返回 null 表示不值得尝试（板太小 / 右侧本来就没空）。
        /// 调用方应只在结果 Improved 时替换布局。
        /// </summary>
        public static Compacted TryCompact(PackBin bin, double spacing, double margin)
        {
            if (bin == null || bin.Items == null || bin.Items.Count == 0) return null;

            double usableW = bin.W - 2 * margin;
            double usableH = bin.H - 2 * margin;
            if (usableW <= 1e-9 || usableH <= 1e-9) return null;

            double origMaxX = 0, origMaxY = 0;
            foreach (PackItem it in bin.Items)
            {
                double rx = it.Px + it.W, ry = it.Py + it.H;
                if (rx > origMaxX) origMaxX = rx;
                if (ry > origMaxY) origMaxY = ry;
            }

            // 这里不能"看右侧还剩多少"来决定要不要试：常见情形恰恰是右侧只剩十几毫米
            // （例如末板 4 件 600x600 铺成 2x2，宽度铺满 1200、高度只用一半），
            // 而改成 1 列 4 行就能腾出 600mm 的整条。所以一律试，改不动就不采用。

            List<Comparison<PackItem>> orders = Orders(bin.Items.Count);

            // 主启发式只有 LeftBottom；件少时再多跑两种，反正成本可以忽略。
            // 件多时不跑：末板几百件时每次重装都是实打实的 O(n²)，
            // 而件多到几百件的板本来也留不出整条余料。
            var heuristics = new List<PackHeuristic> { PackHeuristic.LeftBottom };
            if (bin.Items.Count <= 120)
            {
                heuristics.Add(PackHeuristic.BottomLeft);
                heuristics.Add(PackHeuristic.BestShortSideFit);
            }

            List<PackItem> bestItems = null;
            double bestX = double.MaxValue, bestY = double.MaxValue;

            foreach (Comparison<PackItem> cmp in orders)
            {
                var ordered = new List<PackItem>(bin.Items);
                ordered.Sort(cmp);

                foreach (PackHeuristic h in heuristics)
                {
                    double usedX, usedY;
                    List<PackItem> packed = TryPack(ordered, bin, spacing, margin, h,
                                                    out usedX, out usedY);
                    if (packed == null) continue;

                    // 主看右边界（余料带宽度），同宽再看顶边（顶部也能留一条）
                    bool better = usedX < bestX - 1e-6
                               || (Math.Abs(usedX - bestX) <= 1e-6 && usedY < bestY - 1e-6);
                    if (!better) continue;

                    bestX = usedX; bestY = usedY; bestItems = packed;
                }
            }

            if (bestItems == null) return null;

            bool improved = bestX < origMaxX - ImproveMm
                         || (Math.Abs(bestX - origMaxX) <= ImproveMm && bestY < origMaxY - ImproveMm);

            return new Compacted
            {
                Items = improved ? bestItems : new List<PackItem>(bin.Items),
                UsedWidth = improved ? bestX : origMaxX,
                UsedHeight = improved ? bestY : origMaxY,
                Improved = improved
            };
        }

        /// <summary>在一张空板上按给定顺序和启发式重装这批件；装不下返回 null</summary>
        private static List<PackItem> TryPack(List<PackItem> ordered, PackBin src, double spacing,
                                              double margin, PackHeuristic h,
                                              out double usedX, out double usedY)
        {
            usedX = 0; usedY = 0;

            var packer = new RectPacker(spacing);
            var bin = new PackBin(src.W, src.H, src.SpecName, margin);
            var placed = new List<PackItem>(ordered.Count);

            foreach (PackItem it in ordered)
            {
                PackItem copy = FixedOrientation(it);

                double x, y; bool rot;
                if (!packer.TryFindPosition(bin, copy, h, out x, out y, out rot)) return null;
                packer.Commit(bin, copy, x, y, rot);
                placed.Add(copy);
            }

            foreach (PackItem it in placed)
            {
                double rx = it.Px + it.W, ry = it.Py + it.H;
                if (rx > usedX) usedX = rx;
                if (ry > usedY) usedY = ry;
            }
            return placed;
        }

        /// <summary>
        /// 复制一件并**锁死当前朝向**：压实只解决"摆在哪"，不重新决定"要不要转"。
        /// 让它在这一步翻案，图面上的朝向会莫名其妙地跳，操作工没法对照上一版看。
        /// </summary>
        private static PackItem FixedOrientation(PackItem it)
        {
            return new PackItem
            {
                Seq = it.Seq,
                Part = it.Part,
                InstanceIndex = it.InstanceIndex,
                W0 = it.W,
                H0 = it.H,
                W = it.W,
                H = it.H,
                CanRotate = false,
                RequiredSpec = it.RequiredSpec
            };
        }

        /// <summary>
        /// 排序候选。件越多候选越少 —— 末板几百件时重装一次的代价不小，
        /// 而那种板本来也压不出整条余料。
        /// </summary>
        private static List<Comparison<PackItem>> Orders(int count)
        {
            var orders = new List<Comparison<PackItem>>();

            // 面积降序：通用起点
            orders.Add((a, b) =>
            {
                double aa = a.W * a.H, bb = b.W * b.H;
                if (Math.Abs(aa - bb) > 1e-6) return bb.CompareTo(aa);
                return b.Seq.CompareTo(a.Seq);
            });

            if (count > 400) return orders;

            // 高度降序、同高按宽降序：长条件堆成列最整齐，列宽也不会忽宽忽窄
            orders.Add((a, b) =>
            {
                if (Math.Abs(a.H - b.H) > 1e-6) return b.H.CompareTo(a.H);
                if (Math.Abs(a.W - b.W) > 1e-6) return b.W.CompareTo(a.W);
                return a.Seq.CompareTo(b.Seq);
            });

            // 宽度降序：让左边第一列就定下最宽的列，右边不会再为宽件破列
            orders.Add((a, b) =>
            {
                if (Math.Abs(a.W - b.W) > 1e-6) return b.W.CompareTo(a.W);
                if (Math.Abs(a.H - b.H) > 1e-6) return b.H.CompareTo(a.H);
                return a.Seq.CompareTo(b.Seq);
            });

            if (count > 120) return orders;

            // 长边降序
            orders.Add((a, b) =>
            {
                double ma = Math.Max(a.W, a.H), mb = Math.Max(b.W, b.H);
                if (Math.Abs(ma - mb) > 1e-6) return mb.CompareTo(ma);
                return b.Seq.CompareTo(a.Seq);
            });

            return orders;
        }
    }
}
