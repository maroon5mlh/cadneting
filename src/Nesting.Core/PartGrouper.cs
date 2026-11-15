using System.Collections.Generic;

namespace Nesting.Core
{
    /// <summary>
    /// 从图纸中提取出来的一个零件实例（尚未归并）。外轮廓为世界坐标。
    /// </summary>
    public sealed class RawPart
    {
        public string BlockName;
        public string Handle;
        /// <summary>与这块零件关联的标注文字句柄（块外独立 TEXT/MTEXT）</summary>
        public List<string> TextHandles = new List<string>();
        public Contour Outer;
        public List<Contour> Holes = new List<Contour>();
        public bool AllowRotate90 = true;
    }

    /// <summary>
    /// 把「一堆图形」归并成「品种 x 数量」。
    /// 这是排样输入的关键一步：引擎吃的是品种清单，不是图形列表。
    /// 做错了会表现为"排出来的版看起来对但件数不对"，极难排查。
    /// </summary>
    public static class PartGrouper
    {
        public static List<PartType> Group(IList<RawPart> rawParts, out List<string> warnings)
        {
            warnings = new List<string>();
            var map = new Dictionary<string, PartType>();
            var order = new List<string>();

            foreach (RawPart rp in rawParts)
            {
                if (rp == null || rp.Outer == null || rp.Outer.Count < 3)
                {
                    warnings.Add("跳过无效轮廓，句柄=" + (rp == null ? "null" : rp.Handle));
                    continue;
                }

                Vec2 originalMin;
                Contour normalized = GeometryUtil.NormalizeToOrigin(rp.Outer, out originalMin);

                var normHoles = new List<Contour>();
                foreach (Contour h in rp.Holes)
                {
                    Vec2 hmin, hmax;
                    h.GetBounds(out hmin, out hmax);
                    normHoles.Add(h.TranslatedBy(new Vec2(-originalMin.X, -originalMin.Y)));
                }

                string key = GeometryUtil.Fingerprint(normalized, normHoles.Count);

                PartType pt;
                if (!map.TryGetValue(key, out pt))
                {
                    Vec2 nmin, nmax;
                    normalized.GetBounds(out nmin, out nmax);

                    pt = new PartType
                    {
                        Id = "P" + (map.Count + 1).ToString("D3"),
                        BlockName = rp.BlockName,
                        Outer = normalized,
                        Holes = normHoles,
                        Width = nmax.X - nmin.X,
                        Height = nmax.Y - nmin.Y,
                        NetArea = GeometryUtil.NetArea(normalized, normHoles),
                        AllowRotate90 = rp.AllowRotate90,
                        Quantity = 0
                    };
                    map[key] = pt;
                    order.Add(key);
                }
                else
                {
                    // 同品种中出现"禁止旋转"的实例，则整个品种禁止旋转（保守策略）
                    if (!rp.AllowRotate90) pt.AllowRotate90 = false;
                }

                pt.Quantity++;
                pt.Handles.Add(rp.Handle);

                // 与 Handles 严格同序：多个文字句柄用逗号拼接，无文字则占位空串。
                // 这里如果少 Add 一次，后面所有实例的文字就会整体错位一位。
                pt.TextHandles.Add(rp.TextHandles == null || rp.TextHandles.Count == 0
                    ? ""
                    : string.Join(",", rp.TextHandles));
            }

            var result = new List<PartType>(order.Count);
            foreach (string k in order) result.Add(map[k]);
            return result;
        }
    }
}
