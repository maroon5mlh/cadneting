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

            /// <summary>覆盖"大板优先"（null = 用 job.Settings.SheetPreferLarge）</summary>
            public bool? SheetPreferLargeOverride;
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

            if (items.Count == 0)
            {
                result.Error = "任务里没有可排的零件（数量都是 0）。";
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
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

            // ---- 板材顺序 ----
            // 规格集合始终按面积升序保存，具体开哪一张由「还需要多大的板」决定（见 Pack）。
            // 方案对比可限定规格集合（"库存只有 1220x2440"这种现实约束）
            var sheetSource = (opt.RestrictSheets != null && opt.RestrictSheets.Count > 0)
                              ? opt.RestrictSheets : job.Sheets;
            var specs = new List<SheetSpec>(sheetSource);
            specs.Sort((a, b) => (a.Width * a.Height).CompareTo(b.Width * b.Height));

            bool preferLarge = opt.SheetPreferLargeOverride ?? job.Settings.SheetPreferLarge;

            double spacing = job.Settings.SpacingMm;
            double margin = job.Settings.MarginMm > 0 ? job.Settings.MarginMm : 0;

            // 每件先算出「能装下它的最小规格板」。开新板时看的是剩余件里最难装的那个，
            // 所以必须提前算：3050 只在大件真的需要时才会被打开。
            foreach (PackItem it in items)
            {
                it.RequiredSpec = MinFittingSpec(it, specs, margin);
                if (it.RequiredSpec != null) continue;

                // 提前点名是哪件超幅：比"所有尝试都失败"的笼统报错有用得多，
                // 用户要的是"加个什么规格"或"少留多少边"，不是"排不出来"
                SheetSpec biggest = specs[specs.Count - 1];
                string name = string.IsNullOrEmpty(it.Part.BlockName) ? it.Part.Id : it.Part.BlockName;
                result.Error = string.Format(
                    "零件「{0}」{1:F0}×{2:F0} 放不进任何勾选的板材：最大的 {3} 扣掉留边 {4:F0}mm 后" +
                    "只剩 {5:F0}×{6:F0}。请加一个更大的规格，或把留边调小。",
                    name, it.W0, it.H0, biggest.Name, margin,
                    biggest.Width - 2 * margin, biggest.Height - 2 * margin);
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }

            static double ReqArea(PackItem x)
            {
                return x.RequiredSpec == null ? 0 : x.RequiredSpec.Width * x.RequiredSpec.Height;
            }

            var orderings = new List<Comparison<PackItem>>();

            if (preferLarge)
            {
                // 必须用大板的件排最前：先让它们占住大板，剩下的空隙由小件填满。
                // 如果没有这一条，大件可能排在最后，前面的小件会把大板开出来却填不满，
                // 最后大件再单独占一张 —— 正是"大板没排满就排下一张"的观感。
                orderings.Add((a, b) =>
                {
                    double ra = ReqArea(a), rb = ReqArea(b);
                    if (Math.Abs(rb - ra) > 1e-6) return rb.CompareTo(ra);
                    return (b.W0 * b.H0).CompareTo(a.W0 * a.H0);
                });
            }

            orderings.Add((a, b) => b.W0 * b.H0 != a.W0 * a.H0
                          ? (b.W0 * b.H0).CompareTo(a.W0 * a.H0)          // 面积降序
                          : b.Seq.CompareTo(a.Seq));
            orderings.Add((a, b) => MaxSide(b).CompareTo(MaxSide(a)));         // 长边降序
            orderings.Add((a, b) => (b.W0 + b.H0).CompareTo(a.W0 + a.H0));     // 周长降序
            orderings.Add((a, b) => b.H0.CompareTo(a.H0));                     // 高度降序
            orderings.Add((a, b) => b.W0.CompareTo(a.W0));                     // 宽度降序
            orderings.Add((a, b) => a.H0.CompareTo(b.H0));                     // 高度升序（矮件先占平层）
            var heuristics = new[]
            {
                PackHeuristic.BestShortSideFit,
                PackHeuristic.BestAreaFit,
                PackHeuristic.BottomLeft
            };

            // ---- 候选规格组合：只跑混排 ----
            //
            // 规格顺序就是策略本身：大板优先 = 从大到小试，小板优先 = 从小到大试。
            // 早先还要额外跑"每种单一规格"，是因为最小板优先会把大板饿死
            // （同时勾 2250 和 2440 时 2440 永远开不出来，实测 428 件从
            // 「69 张 2440」变成「75 张 2250」），只能靠单规格方案兜底。
            //
            // 现在顺序是用户显式选的策略，引擎不该再用"张数最少"把它覆盖回
            // 另一种板型 —— 那样用户勾了"大板优先"却拿到小板方案，表现就是开关没反应。
            // "库存只有某一种板会怎样"由 NestPlanner 用 RestrictSheets 单独算，
            // 那是业务问题，不是引擎该替用户猜的组合。
            var specSets = new List<List<SheetSpec>> { new List<SheetSpec>(specs) };

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
                        List<PackBin> bins = Pack(list, specSet, spacing, margin, h, preferLarge, result);

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
                        RotationDeg = it.Rotated ? 90 : 0,
                        BoxW = it.W,
                        BoxH = it.H
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

            // ---- 每张板的余料：这是"还能不能再塞东西"的口径 ----
            // 用户看到"这张板空了一大片，为什么还另开一张"时问的就是这个数，
            // 与其让人拿面积去反推，不如直接给。
            ReportFreeArea(result);

            result.TotalUtilization = totalSheet <= 0 ? 0 : totalUsed / totalSheet;
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// 算每张板的包围盒余料，并把"明显没排满"的板报出来。
        ///
        /// 报告的目的不是抱怨引擎：排不下就是排不下。但"某张板只用了三成、
        /// 后面还开了新板"这种结果必须让人看见，因为它的常见原因不在引擎，
        /// 而在选料：混进来几个又细又长的标记块，或者留边/间距改大了。
        /// </summary>
        private static void ReportFreeArea(NestResult result)
        {
            const double thinThreshold = 0.45;   // 低于这个占比视为"没排满"

            var thin = new List<string>();
            foreach (SheetResult s in result.Sheets)
            {
                double sheetArea = s.Width * s.Height;

                double box = 0;
                foreach (Placement pl in s.Items) box += pl.BoxW * pl.BoxH;

                s.FreeArea = sheetArea > box ? sheetArea - box : 0;
                s.BboxCoverage = sheetArea <= 0 ? 0 : box / sheetArea;

                if (s.BboxCoverage < thinThreshold)
                {
                    thin.Add(string.Format("第 {0} 张（{1}，{2} 件，余料 {3:F2} m²）",
                        s.Index + 1, s.SheetName, s.Items.Count, s.FreeArea / 1e6));
                }
            }

            if (thin.Count > 0)
            {
                // 措辞不能是"最后一张空着正常"：这批料恰恰是"最后一张只放 2 件"
                // 顶出来的一张板，说成正常就把问题盖掉了。
                // 也要说清"余料大 ≠ 还能塞"：剩余空间常是窄条，面积够而形状不匹配。
                result.Warnings.Add(string.Format(
                    "{0} 张板余料偏大：{1}。余料按包围盒算，实际多半是窄条 —— " +
                    "件塞不进去不是因为面积不够，而是空隙形状不匹配。" +
                    "想再压下一张，可以试：允许 90° 旋转（薄板无纹向限制时最有效）、" +
                    "把留边/间距调小、或在「方案对比」里换一个方案。",
                    thin.Count, string.Join("；", thin)));
            }
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
                                          double spacing, double margin, PackHeuristic h,
                                          bool preferLarge, NestResult result)
        {
            var packer = new RectPacker(spacing);
            var bins = new List<PackBin>();

            // 每次尝试都用全新副本，避免跨尝试互相污染坐标
            var list = new List<PackItem>(proto.Count);
            foreach (PackItem it in proto) list.Add(it.Clone());

            // 规格集合可能被 RestrictSheets 收窄，RequiredSpec 要按实际可用的板重算
            foreach (PackItem it in list)
            {
                it.RequiredSpec = MinFittingSpec(it, specs, margin);
                if (it.RequiredSpec == null) return null;   // 这件在这组规格里根本装不下
            }

            for (int idx = 0; idx < list.Count; idx++)
            {
                PackItem it = list[idx];

                // 首次适配（First Fit）：依次尝试已开的板，用第一张放得下的。
                // 新板只有"已开的全放不下"时才开，所以一张大板的空隙会先被小件填满，
                // 填到真的塞不进任何一件，才开下一张 —— 这就是"大板要排满"。
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

                if (target == null)
                {
                    SheetSpec pick = ChooseNewSheet(list, idx, it, specs, margin, preferLarge);
                    if (pick == null) return null;

                    // 兜底：极端规格组合下"面积够大"不等于"装得下"（宽高比例不同），
                    // 这时退回这件自己能用的小板，宁可多开一张也不让本次尝试失败
                    if (!CanHold(pick, it, margin))
                        pick = MinFittingSpec(it, specs, margin);
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

        /// <summary>
        /// 开新板时决定用哪种规格。
        ///
        /// 大板优先并不等于"每张都开最大的"。判断依据是**还没放下的件还需要多大的板**：
        /// 只要剩余件里有一个 500x2500（只有 3050 装得下），这张就必须开 3050；
        /// 等它被放掉、剩余件都能进 2440 了，后面就都开 2440。
        /// 这样 3050 只出现在"非它不可"的场合，而且一旦开出就被小件填满。
        ///
        /// 关掉大板优先则是逐件贪心：只保证当前这件用能装下它的最小板。
        /// </summary>
        private static SheetSpec ChooseNewSheet(List<PackItem> list, int idx, PackItem it,
                                                List<SheetSpec> specs, double margin, bool preferLarge)
        {
            if (!preferLarge) return MinFittingSpec(it, specs, margin);

            SheetSpec pick = null;
            double needArea = -1;
            for (int k = idx; k < list.Count; k++)
            {
                SheetSpec s = list[k].RequiredSpec;
                if (s == null) return null;

                double a = s.Width * s.Height;
                if (a > needArea + 1e-9) { needArea = a; pick = s; }
            }
            return pick;
        }

        /// <summary>在给定规格里找能容纳该件的最小规格（按面积）；都装不下返回 null</summary>
        private static SheetSpec MinFittingSpec(PackItem it, List<SheetSpec> specs, double margin)
        {
            SheetSpec best = null;
            double bestArea = double.MaxValue;
            foreach (SheetSpec s in specs)
            {
                if (!CanHold(s, it, margin)) continue;

                double a = s.Width * s.Height;
                if (a < bestArea - 1e-9) { bestArea = a; best = s; }
            }
            return best;
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
