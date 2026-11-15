using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Nesting.Core;

namespace Nesting.AutoCAD.Extraction
{
    /// <summary>
    /// 找出「块旁边的标注文字」并把它和块绑定起来。
    ///
    /// 背景：XK 命令把文字写在块**外面**（模型空间里一个独立 DBText/MTEXT，
    /// 位置取块包围盒中心），没有分组、没有反应器、没有扩展数据。
    /// 也就是说 CAD 里根本不存在"文字属于这个块"的客观事实，只能靠几何关系推断。
    ///
    /// 推断规则（按可信度排序）：
    /// 1. 文字插入点在图块包围盒内 —— XK 放在正中心，这条几乎总成立；
    /// 2. 一个文字同时落在多个块的包围盒内时，归给"中心最近"的那个块。
    ///    排版前零件可能挨得很近，不按距离判定会串到邻件上；
    /// 3. 一个块可以有多条文字（名称 + 数量），全部跟随。
    ///
    /// 为什么不用"最近的文字"兜底：那会把隔壁零件漏关联的文字强行拉过来，
    /// 排完版表现为"某块上顶着别人的字"，比不跟随更糟。
    /// 所以只做保守的"包围盒内"判定，漏掉的会在命令行提示数量。
    /// </summary>
    internal static class TextCompanionFinder
    {
        /// <summary>一个有文字的块（世界坐标包围盒 + 归属空间）</summary>
        internal sealed class Target
        {
            public ObjectId BlockId;
            public ObjectId SpaceId;
            public RawPart Part;
            public Vec2 Min, Max, Center;
            public List<string> TextHandles = new List<string>();
        }

        private sealed class Candidate
        {
            public string Handle;
            public Vec2 Pos;
            public bool Claimed;
        }

        /// <summary>
        /// 为每个 target 填充 Part.TextHandles。返回成功关联的文字实体数。
        /// 必须在事务内调用（只读打开实体）。
        /// </summary>
        public static int Attach(List<Target> targets, Transaction tr, double tol,
                                 List<string> warnings, out int orphanTexts)
        {
            orphanTexts = 0;
            if (targets == null || targets.Count == 0) return 0;
            if (tol <= 0) tol = 1.0;

            // 按所属空间分组：块可能被放在图纸空间、也可能嵌在别的块里
            var bySpace = new Dictionary<ObjectId, List<Target>>();
            foreach (Target t in targets)
            {
                List<Target> list;
                if (!bySpace.TryGetValue(t.SpaceId, out list))
                {
                    list = new List<Target>();
                    bySpace[t.SpaceId] = list;
                }
                list.Add(t);
            }

            int linked = 0;

            foreach (KeyValuePair<ObjectId, List<Target>> kv in bySpace)
            {
                List<Candidate> cands = LoadTexts(kv.Key, tr);
                if (cands.Count == 0) continue;

                List<Target> group = kv.Value;

                foreach (Candidate c in cands)
                {
                    Target best = null;
                    double bestDist = double.MaxValue;

                    foreach (Target t in group)
                    {
                        // 包围盒内判定（带容差：文字略压边、字高误差都要容忍）
                        if (c.Pos.X < t.Min.X - tol || c.Pos.X > t.Max.X + tol) continue;
                        if (c.Pos.Y < t.Min.Y - tol || c.Pos.Y > t.Max.Y + tol) continue;

                        double d = c.Pos.DistanceTo(t.Center);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = t;
                        }
                    }

                    if (best == null)
                    {
                        orphanTexts++;
                        continue;
                    }

                    c.Claimed = true;
                    best.TextHandles.Add(c.Handle);
                    linked++;
                }
            }

            foreach (Target t in targets)
                t.Part.TextHandles = t.TextHandles;

            if (orphanTexts > 0)
                warnings.Add("有 " + orphanTexts + " 个文字没落在任何被选中的块里，不会跟随排样（可能属于未选中的块）");

            return linked;
        }

        /// <summary>遍历某个空间，取出所有 TEXT / MTEXT 的插入点</summary>
        private static List<Candidate> LoadTexts(ObjectId spaceId, Transaction tr)
        {
            var list = new List<Candidate>();

            BlockTableRecord space;
            try { space = tr.GetObject(spaceId, OpenMode.ForRead) as BlockTableRecord; }
            catch { return list; }
            if (space == null) return list;

            foreach (ObjectId id in space)
            {
                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                catch { continue; }
                if (ent == null) continue;

                Vec2 pos;
                if (!TryGetTextPosition(ent, out pos)) continue;

                list.Add(new Candidate { Handle = ent.Handle.ToString(), Pos = pos });
            }

            return list;
        }

        /// <summary>
        /// 取文字的定位点。
        /// DBText 的对齐方式不是「左/基线」时，真正起作用的是 AlignmentPoint，
        /// 而 Position 只是个残留值 —— XK 用的是居中对齐，只读 Position 会取到旧坐标。
        /// </summary>
        private static bool TryGetTextPosition(Entity ent, out Vec2 pos)
        {
            pos = new Vec2(0, 0);

            var txt = ent as DBText;
            if (txt != null)
            {
                bool aligned = txt.HorizontalMode != TextHorizontalMode.TextLeft
                            || txt.VerticalMode != TextVerticalMode.TextBase;

                Point3d p = txt.Position;
                if (aligned)
                {
                    Point3d ap = txt.AlignmentPoint;
                    if (!ap.IsEqualTo(Point3d.Origin)) p = ap;
                }

                pos = new Vec2(p.X, p.Y);
                return true;
            }

            var mtx = ent as MText;
            if (mtx != null)
            {
                pos = new Vec2(mtx.Location.X, mtx.Location.Y);
                return true;
            }

            return false;
        }
    }
}
