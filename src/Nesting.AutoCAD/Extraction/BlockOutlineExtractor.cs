using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Nesting.Core;

namespace Nesting.AutoCAD.Extraction
{
    /// <summary>
    /// 从块参照提取零件外轮廓与内孔，全程只读，不炸开、不修改原图。
    ///
    /// 要点：
    /// 1. 用 BlockReference.BlockTransform 做完整变换，天然兼容镜像、非等比缩放、嵌套块；
    ///    不要手工用 Position/ScaleFactor 拼，会漏掉镜像。
    /// 2. 嵌套块需递归，变换矩阵按"子块→父块→世界"的顺序连乘。
    /// 3. 提取出来的是世界坐标轮廓，排样算法会用"包围盒左下角为原点"的局部坐标，
    ///    写回时用矩阵反算，全程不碰块基点。
    /// </summary>
    internal static class BlockOutlineExtractor
    {
        private const int MaxDepth = 8;

        public static RawPart Extract(BlockReference br, Transaction tr, double tol, List<string> warnings)
        {
            if (tol <= 0) tol = 0.1;

            var loops = new List<Contour>();
            var openSegments = new List<List<Vec2>>();

            Collect(br, tr, br.BlockTransform, loops, openSegments, tol, 0, warnings);

            if (openSegments.Count > 0)
                Stitch(openSegments, tol, loops, warnings);

            return BuildPart(br, tr, loops, warnings);
        }

        private static void Collect(BlockReference br, Transaction tr, Matrix3d xform,
                                    List<Contour> loops, List<List<Vec2>> openSegments,
                                    double tol, int depth, List<string> warnings)
        {
            if (depth > MaxDepth)
            {
                warnings.Add("块嵌套层数超限: " + br.Handle);
                return;
            }

            BlockTableRecord btr;
            try { btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead); }
            catch (Exception ex)
            {
                warnings.Add("无法打开块定义 " + br.Name + ": " + ex.Message);
                return;
            }

            if (btr.IsFromExternalReference || btr.IsFromOverlayReference)
                warnings.Add("块 " + br.Name + " 来自外部参照，几何可能不完整");

            foreach (ObjectId id in btr)
            {
                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                catch { continue; }
                if (ent == null) continue;

                var nested = ent as BlockReference;
                if (nested != null)
                {
                    // 子块空间 -> 父块空间 -> 世界：A*B 表示先 A 后 B
                    Matrix3d combined = nested.BlockTransform * xform;
                    Collect(nested, tr, combined, loops, openSegments, tol, depth + 1, warnings);
                    continue;
                }

                var curve = ent as Curve;
                if (curve == null) continue;

                List<Vec2> pts;
                bool closed;
                if (!CurveFlattener.TryFlatten(curve, tol, out pts, out closed)) continue;
                if (pts == null || pts.Count < 2) continue;

                // 变换到世界坐标
                var world = new Vec2[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                {
                    Point3d p = new Point3d(pts[i].X, pts[i].Y, 0).TransformBy(xform);
                    world[i] = new Vec2(p.X, p.Y);
                }

                if (closed)
                {
                    if (world.Length >= 3) loops.Add(new Contour(world));
                }
                else
                {
                    openSegments.Add(new List<Vec2>(world));
                }
            }
        }

        /// <summary>
        /// 把散开的线段/弧段按端点容差拼接成闭合环。
        /// 图纸里零件被炸成独立 LINE 时靠这里还原。
        /// </summary>
        private static void Stitch(List<List<Vec2>> segments, double tol,
                                   List<Contour> loops, List<string> warnings)
        {
            int n = segments.Count;
            var used = new bool[n];

            for (int i = 0; i < n; i++)
            {
                if (used[i]) continue;
                used[i] = true;

                var chain = new List<Vec2>(segments[i]);
                bool advanced = true;
                while (advanced)
                {
                    advanced = false;
                    Vec2 tail = chain[chain.Count - 1];

                    for (int j = 0; j < n; j++)
                    {
                        if (used[j]) continue;
                        List<Vec2> seg = segments[j];

                        if (tail.DistanceTo(seg[0]) <= tol)
                        {
                            for (int k = 1; k < seg.Count; k++) chain.Add(seg[k]);
                            used[j] = true; advanced = true; break;
                        }
                        if (tail.DistanceTo(seg[seg.Count - 1]) <= tol)
                        {
                            for (int k = seg.Count - 2; k >= 0; k--) chain.Add(seg[k]);
                            used[j] = true; advanced = true; break;
                        }
                    }

                    if (!advanced) break;
                    if (chain.Count > 3 && chain[chain.Count - 1].DistanceTo(chain[0]) <= tol)
                        break;   // 已闭合
                }

                if (chain.Count >= 4 && chain[chain.Count - 1].DistanceTo(chain[0]) <= tol)
                {
                    chain.RemoveAt(chain.Count - 1);
                    if (chain.Count >= 3) loops.Add(new Contour(chain.ToArray()));
                }
                else
                {
                    warnings.Add("有开口轮廓无法闭合（端点间距 > " + tol.ToString("0.###") + "mm），已忽略");
                }
            }
        }

        private static RawPart BuildPart(BlockReference br, Transaction tr,
                                         List<Contour> loops, List<string> warnings)
        {
            if (loops.Count == 0)
            {
                warnings.Add("块 " + br.Name + " 内未找到闭合轮廓，已跳过");
                return null;
            }

            // 面积最大者为外轮廓
            loops.Sort(delegate (Contour a, Contour b) { return b.AbsArea.CompareTo(a.AbsArea); });
            Contour outer = loops[0];

            var holes = new List<Contour>();
            int extraParts = 0;
            for (int i = 1; i < loops.Count; i++)
            {
                Contour c = loops[i];
                Vec2 probe = Centroid(c);
                if (outer.Contains(probe)) holes.Add(c);
                else extraParts++;
            }

            if (extraParts > 0)
                warnings.Add("块 " + br.Name + " 内含 " + extraParts +
                             " 个独立轮廓（可能是多个零件拼在一个块里），本次只取最大轮廓");

            return new RawPart
            {
                BlockName = br.Name,
                Handle = br.Handle.ToString(),
                Outer = outer,
                Holes = holes,
                AllowRotate90 = true
            };
        }

        private static Vec2 Centroid(Contour c)
        {
            Vec2 min, max;
            c.GetBounds(out min, out max);
            return new Vec2((min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5);
        }
    }
}
