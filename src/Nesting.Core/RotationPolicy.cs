using System;
using System.Collections.Generic;

namespace Nesting.Core
{
    /// <summary>
    /// 旋转策略：一件零件到底允不允许转 90°。
    ///
    /// 判断分两层，顺序不能反：
    /// 1. 零件自身是否允许（有纹向、折弯方向的件为 false）—— 这是硬约束，谁也不能覆盖；
    /// 2. 全局开关（面板上的「允许旋转 90°」）—— 这是批量开关；
    /// 3. 关键字白名单 —— 块名含「顶包边 / 底框 / 中横框」的件**无视全局开关**，永远可转。
    ///
    /// 为什么第 3 条要凌驾于全局开关之上：用户关掉全局开关，通常是"个别件不能乱转"，
    /// 而不是"所有件都不许转"。为了这几件去关全局、再逐个放开，操作上做不到
    /// （面板没有逐件开关），结果就是白白损失 3~6 个点的利用率。
    /// 所以这三类件直接走白名单，见 RotateKeywords 可自行增删。
    /// </summary>
    public static class RotationPolicy
    {
        /// <summary>默认关键字。改这里等于改所有未显式配置的任务的默认行为。</summary>
        public static readonly string[] DefaultKeywords = { "顶包边", "底框", "中横框" };

        /// <summary>块名是否命中强制旋转关键字。关键字为空时退回 DefaultKeywords。</summary>
        public static bool IsForcedRotate(string blockName, IList<string> keywords)
        {
            if (string.IsNullOrEmpty(blockName)) return false;

            IList<string> list = (keywords == null || keywords.Count == 0)
                ? DefaultKeywords
                : keywords;

            for (int i = 0; i < list.Count; i++)
            {
                string k = list[i];
                if (k == null) continue;
                k = k.Trim();
                if (k.Length == 0) continue;

                if (blockName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }


        /// <summary>
        /// 综合判定：零件自身允许 && (全局开关 || 命中关键字)。
        /// partAllow 为 false 时直接否决，关键字也救不回来 —— 纹向是物理约束。
        /// </summary>
        public static bool CanRotate(bool globalAllow, bool partAllow, string blockName, IList<string> keywords)
        {
            if (!partAllow) return false;
            if (globalAllow) return true;
            return IsForcedRotate(blockName, keywords);
        }
    }
}
