using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nesting.Core.Solvers
{
    /// <summary>
    /// 排样引擎入口。
    ///
    /// 策略说明：
    /// 激光切割无通切约束，因此采用 MaxRects 自由矩形装箱。
    /// 零件按包围盒参与碰撞判定 —— 轮廓 ⊆ 包围盒，所以包围盒排样**永远不会产生重叠**，
    /// 只是轮廓凹陷处会浪费一点空间。对填充率 97% 以上的板式家具件，
    /// 这个损失在 1% 以内，换来的是毫秒级速度和不依赖任何外部引擎。
    /// 填充率低于阈值的异形件会被单独标记，用于判断何时需要接入 NFP 引擎。
    /// </summary>
    public static class NestEngine
    {
        /// <summary>包围盒填充率低于此值视为异形件，包围盒排样会明显浪费</summary>
        public const double IrregularThreshold = 0.97;

        public sealed class Options
        {
            /// <summary>
            /// 排序 × 启发式 × 规格组合 的尝试次数上限。<br/>
            /// 默认 0 = 不限（全组合都跑一遍）。400 件的量级全跑也就几十毫秒，
            /// 限次数只会让"张数最少"这个主目标被砍掉的组合拖累。
            /// </summary>
            public int MaxAttempts = 0;

            /// <summary>
            /// 只用这些规格跑（null / 空 = 用 job.Sheets）。<br/>
            /// 方案对比里用来算"库存只有这一种板"的情形。
            /// </summary>
            public List<SheetSpec> RestrictSheets;

            /// <summary>覆盖优化目标（null = 用 job.Settings.Objective）。"sheets" / "area"</summary>
            public string ObjectiveOverride;

            /// <summary>覆盖全局 90° 旋转开关（null = 用 job.Settings.AllowRotate90）</summary>
            public bool? AllowRotate90Override;
        }

        public static NestResult Solve(NestJob job) { return Solve(job, new Options()); }

        public static NestResult Solve(NestJob job, Options opt)
        {
            var sw = Stopwatch.StartNew();
            var result = new NestResult { EngineVersion = "core-maxrects-1.0" };

            if (job == null || job.Parts == null || job.Parts.Count == 0)
            {
                result.Error = "任务中没有零件。";
                return result;
            }
            if (job.Sheets == null || job.Sheets.Count == 0)
            {
                result.Error = "任务中没有板材规格。";
                return result;
            }

            // ---- 展开零件实例 ----
            // 方案对比会覆盖全局旋转开关：这里统一取"本次生效"的口径，
            // 不要直接读 job.Settings.AllowRotate90，否则"全转 90°"那个方案会静默不生效
            bool globalRotate = opt.AllowRotate90Override.HasValue
                                ? opt.AllowRotate90Override.Value
                                : job.Settings.AllowRotate90;

            var items = new List<PackItem>();
            foreach (PartType p in job.Parts)
            {
                int qty = p.Quantity > 0 ? p.Quantity : 1;

                // 关键字白名单：块名含「顶包边/底框/中横框」的件即使全局关掉旋转也可转
                bool canRotate = RotationPolicy.CanRotate(
                    globalRotate, p.AllowRotate90, p.BlockName, job.Settings.RotateKeywords);

                for (int i = 0; i < qty; i++)
                {
                    items.Add(new PackItem
                    {
                        Seq = items.Count,
                        Part = p,
                        InstanceIndex = i,
                        W0 = p.Width,
                        H0 = p.Height,
                        W = p.Width,
                        H = p.Height,
                        CanRotate = canRotate
                    });
                }
            }

            ClassifyParts(job, result);

            // 全局关掉旋转时，关键字就是唯一的"放行通道"。
            // 如果一条都没命中（典型原因：关键字和实际块名不一致，比如「中横框」vs「中横板」），
            // 结果会是"所有件都不转、利用率掉几个点"，但表面上完全正常，必须主动报出来。
            if (!globalRotate)
            {
                int forced = 0;
                foreach (PartType p in job.Parts)
                {
                    if (RotationPolicy.IsForcedRotate(p.BlockName, job.Settings.RotateKeywords)) forced++;
                }

                if (forced == 0 && result != null)
                {
                    result.Warnings.Add("已关闭全局旋转，但没有任何块名命中强制旋转关键字（" +
                        string.Join("、", job.Settings.RotateKeywords ?? new List<string>()) +
                        "），本次所有零件都不会转，利用率会偏低。请检查面板里的关键字是否与块名一致。");
                }
            }

            // ---- 板材按面积升序：开新板时取「能装下的最小规格」，先省料再谈别的 ----
            // 方案对比可限定规格集合（"库存只有 1220x2440"这种现实约束）
            var sheetSource = (opt.RestrictSheets != null && opt.RestrictSheets.Count > 0)
                              ? opt.RestrictSheets : job.Sheets;
            var specs = new List<SheetSpec>(sheetSource);
            specs.Sort((a, b) => (a.Width * a.Height).CompareTo(b.Width * b.Height));

            double spacing = job.Settings.SpacingMm;
            double margin = job.Settings.MarginMm > 0 ? job.Settings.MarginMm : 0;

            var orderings = new List<Comparison<PackItem>>
            {
                (a, b) => b.W0 * b.H0 != a.W0 * a.H0
                          ? (b.W0 * b.H0).CompareTo(a.W0 * a.H0)          // 面积降序
                          : b.Seq.CompareTo(a.Seq),
                (a, b) => MaxSide(b).CompareTo(MaxSide(a)),                   // 长边降序
                (a, b) => (b.W0 + b.H0).CompareTo(a.W0 + a.H0),               // 周长降序
                (a, b) => b.H0.CompareTo(a.H0)                                // 高度降序
            };
            var heuristics = new[]
            {
                PackHeuristic.BestShortSideFit,
                PackHeuristic.BestAreaFit,
                PackHeuristic.BottomLeft
            };

            // ---- 候选规格组合：混排 + 每种单一规格 ----
            //
            // 为什么必须跑单一规格：开新板时是逐件贪心选「能装下的最小板」。
            // 一旦面板里同时勾了 2250 和 2440，凡是能进 2250 的件都会开 2250，
            // 2440 永远开不出来 —— 实测 428 件会从「69 张 2440」变成「75 张 2250」，
            // 总面积几乎一样，但主目标（张数最少）反而变差。
            // 所以要按整体结果择优，而不是逐件贪心。
            var specSets = new List<List<SheetSpec>>();
            specSets.Add(new List<SheetSpec>(specs));
            if (specs.Count > 1)
            {
                foreach (SheetSpec s in specs)
                    specSets.Add(new List<SheetSpec> { s });
            }

            List<PackBin> best = null;             // 按当前目标选出的最优解
            List<PackBin> bestByArea = null;       // 备选：板材总面积最小的解
            double bestSheetArea = double.MaxValue;
            double minSheetArea = double.MaxValue;
            int attempts = 0;
            bool overBudget = false;

            // 目标口径：sheets = 张数最少（默认，少换板少上下料）；area = 总用料面积最小
            bool preferArea = string.Equals(opt.ObjectiveOverride ?? job.Settings.Objective,
                                            "area", StringComparison.OrdinalIgnoreCase);

            foreach (List<SheetSpec> specSet in specSets)
            {
                if (overBudget) break;

                foreach (Comparison<PackItem> cmp in orderings)
                {
                    if (overBudget) break;

                    foreach (PackHeuristic h in heuristics)
                    {
                        if (opt.MaxAttempts > 0 && attempts >= opt.MaxAttempts) { overBudget = true; break; }
                        attempts++;

                        var list = new List<PackItem>(items);
                        list.Sort(cmp);
                        List<PackBin> bins = Pack(list, specSet, spacing, margin, h, result);

                        if (bins == null) continue;

                        double area = 0;
                        foreach (PackBin b in bins) area += b.W * b.H;

                        // 备选解：只看用料面积
                        if (bestByArea == null || area < minSheetArea - 1e-6)
                        {
                            bestByArea = bins;
                            minSheetArea = area;
                        }

                        bool better;
                        if (preferArea)
                            better = best == null || area < bestSheetArea - 1e-6;
                        else
                            better = best == null || bins.Count < best.Count ||
                                     (bins.Count == best.Count && area < bestSheetArea - 1e-6);

                        if (!better) continue;
                        best = bins;
                        bestSheetArea = area;
                    }
                }
            }

            // 张数最少的解往往用更大规格的板，材料面积反而略多。
            // 这个取舍不该静默发生：把另一个口径的数字也报出来，让人自己判断。
            if (!preferArea && bestByArea != null && bestByArea != best &&
                (bestByArea.Count != best.Count || Math.Abs(minSheetArea - bestSheetArea) > 1.0))
            {
                result.Warnings.Add(string.Format(
                    "按「张数最少」选定 {0} 张，用料 {1:N3} m²；若按「省料优先」可用 {2} 张，用料 {3:N3} m²。" +
                    "面板结果的利用率按选定方案计算。",
                    best.Count, bestSheetArea / 1e6, bestByArea.Count, minSheetArea / 1e6));
            }

            if (best == null)
            {
                result.Error = "没有任何板材能容纳全部零件（个别零件尺寸超过板幅，或留边过大）。";
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }

            // ---- 组织结果 ----
            double totalUsed = 0, totalSheet = 0;
            for (int bi = 0; bi < best.Count; bi++)
            {
                PackBin bin = best[bi];
                var sr = new SheetResult
                {
                    Index = bi,
                    SheetName = bin.SpecName,
                    Width = bin.W,
                    Height = bin.H,
                    UsedArea = bin.UsedArea,
                    Utilization = bin.Utilization
                };

                foreach (PackItem it in bin.Items)
                {
                    string handle = null;
                    if (it.Part.Handles != null && it.InstanceIndex < it.Part.Handles.Count)
                        handle = it.Part.Handles[it.InstanceIndex];

                    var pl = new Placement
                    {
                        PartTypeId = it.Part.Id,
                        Handle = handle,
                        SheetIndex = bi,
                        X = it.Px,
                        Y = it.Py,
                        RotationDeg = it.Rotated ? 90 : 0
                    };

                    // 把这一件的跟随文字句柄一并带出去，回写时和块一起变换
                    if (it.Part.TextHandles != null && it.InstanceIndex < it.Part.TextHandles.Count)
                        pl.TextHandles = SplitHandles(it.Part.TextHandles[it.InstanceIndex]);

                    sr.Items.Add(pl);
                }

                totalUsed += bin.UsedArea;
                totalSheet += bin.W * bin.H;
                result.Sheets.Add(sr);
            }

            result.TotalUtilization = totalSheet <= 0 ? 0 : totalUsed / totalSheet;
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        private static double MaxSide(PackItem it)
        {
            return Math.Max(it.W0, it.H0);
        }

        /// <summary>把逗号拼接的句柄串拆成列表；空串返回空列表</summary>
        private static List<string> SplitHandles(string joined)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(joined)) return list;

            foreach (string s in joined.Split(','))
            {
                string t = s.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>尝试把所有零件装进若干张板；有零件装不下则返回 null</summary>
        private static List<PackBin> Pack(List<PackItem> proto, List<SheetSpec> specs,
                                          double spacing, double margin, PackHeuristic h, NestResult result)
        {
            var packer = new RectPacker(spacing);
            var bins = new List<PackBin>();

            // 每次尝试都用全新副本，避免跨尝试互相污染坐标
            var list = new List<PackItem>(proto.Count);
            foreach (PackItem it in proto) list.Add(it.Clone());

            foreach (PackItem it in list)
            {
                // 首次适配（First Fit）：依次尝试已开的板，用第一张放得下的
                PackBin target = null;
                double bx = 0, by = 0;
                bool brot = false;

                for (int i = 0; i < bins.Count; i++)
                {
                    double x, y; bool rot;
                    if (packer.TryFindPosition(bins[i], it, h, out x, out y, out rot))
                    {
                        target = bins[i]; bx = x; by = y; brot = rot;
                        break;
                    }
                }

                // 放不下就开新板：选能容纳该件的最小规格
                if (target == null)
                {
                    SheetSpec pick = null;
                    foreach (SheetSpec s in specs)
                    {
                        if (!CanHold(s, it, margin)) continue;
                        pick = s;
                        break;
                    }
                    if (pick == null) return null;

                    var nb = new PackBin(pick.Width, pick.Height, pick.Name, margin);
                    if (!packer.TryFindPosition(nb, it, h, out bx, out by, out brot)) return null;
                    bins.Add(nb);
                    target = nb;
                }

                packer.Commit(target, it, bx, by, brot);
            }

            return bins;
        }

        /// <summary>板幅扣掉两侧留边后能否容纳该零件</summary>
        private static bool CanHold(SheetSpec s, PackItem it, double margin)
        {
            double uw = s.Width - 2 * margin;
            double uh = s.Height - 2 * margin;
            if (uw <= 0 || uh <= 0) return false;

            if (it.W0 <= uw + 1e-9 && it.H0 <= uh + 1e-9) return true;
            if (it.CanRotate && it.H0 <= uw + 1e-9 && it.W0 <= uh + 1e-9) return true;
            return false;
        }

        /// <summary>统计包围盒填充率，挑出需要 NFP 才能排紧的异形件</summary>
        public static void ClassifyParts(NestJob job, NestResult result)
        {
            int irregularTypes = 0, irregularQty = 0;
            double wasted = 0;

            foreach (PartType p in job.Parts)
            {
                double bbox = p.Width * p.Height;
                if (bbox <= 0) continue;
                double fill = p.NetArea / bbox;
                p.BboxFillRatio = fill;

                if (fill < IrregularThreshold)
                {
                    irregularTypes++;
                    irregularQty += p.Quantity;
                    wasted += (bbox - p.NetArea) * p.Quantity;
                }
            }

            if (irregularTypes > 0 && result != null)
            {
                result.Warnings.Add(string.Format(
                    "有 {0} 个品种（共 {1} 件）包围盒填充率低于 {2:P0}，按包围盒排样约浪费 {3:N0} mm²；" +
                    "这些件建议后续接入 NFP 引擎。",
                    irregularTypes, irregularQty, IrregularThreshold, wasted));
            }
        }
    }
}
