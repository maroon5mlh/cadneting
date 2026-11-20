using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nesting.Core.Solvers
{
    /// <summary>
    /// 方案对比：同一批零件在几种不同策略下各排一遍，返回可比、可选的候选方案。
    ///
    /// 为什么需要它：排样没有唯一解，而且"最优"取决于目的 ——
    /// 少换板（张数少）、省材料（用料面积小）、还是只用某一种库存板。
    /// 引擎内部已经会挑一个最优，但那是**按一个目标**挑的；
    /// 真实车间里"如果全部用 2440 会怎样"这种问题必须能回答，
    /// 所以把几个候选一起算出来交给用户看，而不是替他决定。
    ///
    /// 候选来源（都不违反工艺约束，除了明确标注的那一个）：
    ///   1. 基准        —— 完全按面板设置
    ///   2. 仅某规格    —— 每种勾选的板材单独跑一遍（库存约束）
    ///   3. 省料优先    —— 同样的板，目标换成用料面积最小
    ///   3b. 小板优先   —— 大板优先的对照组：逐件用能装下它的最小板
    ///   4. 全转 90°    —— 仅当面板禁止旋转时给出，标注需确认纹向
    /// </summary>
    public static class NestPlanner
    {
        public static List<NestPlan> Compare(NestJob job) { return Compare(job, new NestEngine.Options()); }

        public static List<NestPlan> Compare(NestJob job, NestEngine.Options baseOpt)
        {
            var plans = new List<NestPlan>();
            if (job == null || job.Parts == null || job.Parts.Count == 0 ||
                job.Sheets == null || job.Sheets.Count == 0)
            {
                return plans;
            }

            if (baseOpt == null) baseOpt = new NestEngine.Options();

            var sw = Stopwatch.StartNew();
            int seq = 0;

            // ---- 1. 基准：面板设置原样 ----
            bool preferArea = string.Equals(job.Settings.Objective, "area", StringComparison.OrdinalIgnoreCase);
            Add(plans, job, baseOpt, ref seq,
                "基准",
                string.Format("按面板设置：{0}，{1}，{2}",
                    preferArea ? "省料优先" : "张数最少",
                    job.Settings.SheetPreferLarge ? "大板只装非它不可的件" : "逐件用最小板",
                    job.Settings.AllowRotate90 ? "允许 90° 旋转" : "不旋转（关键字件除外）"),
                false);

            // ---- 2. 仅某规格：库存只有这一种板时的方案 ----
            if (job.Sheets.Count > 1)
            {
                foreach (SheetSpec s in job.Sheets)
                {
                    if (s == null || s.Width <= 0 || s.Height <= 0) continue;

                    var o = Clone(baseOpt);
                    o.RestrictSheets = new List<SheetSpec> { s };
                    Add(plans, job, o, ref seq,
                        "仅 " + s.Name,
                        string.Format("只能用 {0}（库存/采购约束），其余规格不参与", s.Name),
                        false);
                }
            }

            // ---- 3. 省料优先：同一批板，换成用料面积最小 ----
            {
                var o = Clone(baseOpt);
                o.ObjectiveOverride = "area";
                Add(plans, job, o, ref seq,
                    "省料优先",
                    "同样这批板，目标改为用料面积最小（张数可能多一两张）",
                    false);
            }

            // ---- 3b. 小板优先：每一件都用能装下它的最小板 ----
            // 只在"当前是大板优先 + 勾了多种规格"时才有意义，否则和基准同解，
            // 白占一行还让人以为漏看了什么
            bool preferLarge = baseOpt.SheetPreferLargeOverride ?? job.Settings.SheetPreferLarge;
            if (job.Sheets.Count > 1 && preferLarge)
            {
                var o = Clone(baseOpt);
                o.SheetPreferLargeOverride = false;
                Add(plans, job, o, ref seq,
                    "小板优先",
                    "逐件贪心：不为填满大板调整顺序，件能进小板就用小板",
                    false);
            }

            // ---- 4. 全转 90°：不是免费的，会动工艺前提 ----
            if (!job.Settings.AllowRotate90)
            {
                var o = Clone(baseOpt);
                o.AllowRotate90Override = true;
                Add(plans, job, o, ref seq,
                    "全转 90°",
                    "连有纹向/折弯方向要求的件也放开旋转，利用率通常更高，但落地前必须人工确认",
                    true);
            }

            sw.Stop();

            // ---- 去重：不同方案算出同一张版是常态（比如基准本来就选了"仅 3050"）----
            List<NestPlan> unique = Dedupe(plans);

            // ---- 推荐：只在"不改工艺前提"的方案里选，按 张数 → 用料 ----
            NestPlan best = null;
            foreach (NestPlan p in unique)
            {
                if (p.HasError || p.ChangesCraft) continue;
                if (best == null || p.SheetCount < best.SheetCount ||
                    (p.SheetCount == best.SheetCount && p.SheetArea < best.SheetArea - 1e-6))
                {
                    best = p;
                }
            }
            if (best != null) best.IsRecommended = true;

            // ---- 排序：不改工艺的在前（推荐置顶），改工艺的在后并保持醒目 ----
            unique.Sort((a, b) =>
            {
                if (a.ChangesCraft != b.ChangesCraft) return a.ChangesCraft ? 1 : -1;
                if (a.IsRecommended != b.IsRecommended) return a.IsRecommended ? -1 : 1;
                if (a.HasError != b.HasError) return a.HasError ? 1 : -1;
                if (a.SheetCount != b.SheetCount) return a.SheetCount.CompareTo(b.SheetCount);
                return a.SheetArea.CompareTo(b.SheetArea);
            });

            // Id 按最终顺序重编，避免界面上出现 A C B 这种跳跃
            char id = 'A';
            foreach (NestPlan p in unique) p.Id = (id++).ToString();

            return unique;
        }

        private static NestEngine.Options Clone(NestEngine.Options src)
        {
            return new NestEngine.Options
            {
                MaxAttempts = src.MaxAttempts,
                RestrictSheets = src.RestrictSheets == null ? null : new List<SheetSpec>(src.RestrictSheets),
                ObjectiveOverride = src.ObjectiveOverride,
                AllowRotate90Override = src.AllowRotate90Override,
                SheetPreferLargeOverride = src.SheetPreferLargeOverride
            };
        }

        private static void Add(List<NestPlan> plans, NestJob job, NestEngine.Options opt,
                                ref int seq, string name, string detail, bool changesCraft)
        {
            NestResult r;
            try
            {
                r = NestEngine.Solve(job, opt);
            }
            catch (Exception ex)
            {
                r = new NestResult { Error = ex.Message };
            }

            var plan = new NestPlan
            {
                Name = name,
                Detail = detail,
                Result = r,
                ChangesCraft = changesCraft
            };

            if (r != null && r.Error == null && r.Sheets != null)
            {
                plan.SheetCount = r.Sheets.Count;
                plan.Utilization = r.TotalUtilization;

                double area = 0;
                var mix = new Dictionary<string, int>();
                foreach (SheetResult s in r.Sheets)
                {
                    area += s.Width * s.Height;
                    if (mix.ContainsKey(s.SheetName)) mix[s.SheetName]++;
                    else mix[s.SheetName] = 1;
                }
                plan.SheetArea = area / 1e6;

                var parts = new List<string>();
                foreach (KeyValuePair<string, int> kv in mix)
                    parts.Add(kv.Key + "×" + kv.Value);
                parts.Sort(StringComparer.Ordinal);
                plan.SpecMix = string.Join(" + ", parts);
            }

            seq++;
            plans.Add(plan);
        }

        /// <summary>
        /// 同解去重。判据必须是"版型一致"，不能只比张数：
        /// 张数相同但规格构成不同，是两个真方案（料钱和换板次数都不一样）。
        /// 利用率也要比，否则"同样张数同样规格但摆法不同"会被错误合并。
        /// </summary>
        private static List<NestPlan> Dedupe(List<NestPlan> plans)
        {
            var unique = new List<NestPlan>();
            var seen = new Dictionary<string, NestPlan>();

            foreach (NestPlan p in plans)
            {
                if (p.HasError) { unique.Add(p); continue; }

                string sig = p.SheetCount + "|" + p.SpecMix + "|" +
                             Math.Round(p.Utilization * 10000).ToString();

                NestPlan hit;
                if (seen.TryGetValue(sig, out hit))
                {
                    // 保留先出现的（基准优先），后来者记为同解
                    if (hit.SameAs.Count < 3) hit.SameAs.Add(p.Name);
                    continue;
                }

                seen[sig] = p;
                unique.Add(p);
            }
            return unique;
        }
    }
}
