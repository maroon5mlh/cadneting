using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Nesting.Core;

namespace Nesting.AutoCAD.Extraction
{
    /// <summary>
    /// 把 AutoCAD 各类曲线离散成折线点集（块定义坐标系下，未经块变换）。
    /// 直线段保留原始顶点；圆弧按弦高容差离散。
    /// </summary>
    internal static class CurveFlattener
    {
        /// <summary>
        /// 尝试离散一条曲线。
        /// isClosed 表示曲线自身首尾闭合（圆、闭合多段线）。
        /// 返回 false 表示该实体类型暂不支持（如三维多段线、面域）。
        /// </summary>
        public static bool TryFlatten(Curve curve, double tol, out List<Vec2> points, out bool isClosed)
        {
            points = null;
            isClosed = false;
            if (tol <= 0) tol = 0.1;

            var lw = curve as Autodesk.AutoCAD.DatabaseServices.Polyline;
            if (lw != null) return FlattenLwPolyline(lw, tol, out points, out isClosed);

            var line = curve as Autodesk.AutoCAD.DatabaseServices.Line;
            if (line != null)
            {
                points = new List<Vec2>(2)
                {
                    new Vec2(line.StartPoint.X, line.StartPoint.Y),
                    new Vec2(line.EndPoint.X,   line.EndPoint.Y)
                };
                isClosed = Math.Abs(line.StartPoint.X - line.EndPoint.X) < 1e-9
                        && Math.Abs(line.StartPoint.Y - line.EndPoint.Y) < 1e-9;
                return true;
            }

            // Circle / Arc / Ellipse / Spline 统一按弧长采样
            if (curve is Circle || curve is Arc ||
                curve is Autodesk.AutoCAD.DatabaseServices.Ellipse || curve is Spline)
            {
                points = SampleByDistance(curve, tol);
                isClosed = curve.Closed;
                return true;
            }

            return false;
        }

        private static bool FlattenLwPolyline(Autodesk.AutoCAD.DatabaseServices.Polyline pl,
                                              double tol, out List<Vec2> points, out bool isClosed)
        {
            points = null;
            isClosed = pl.Closed;
            int n = pl.NumberOfVertices;
            if (n < 2) return false;

            var pts = new List<Vec2>(n * 4);
            for (int i = 0; i < n; i++)
            {
                Point3d p3 = pl.GetPoint3dAt(i);   // 已含 OCS/Elevation 处理
                var p = new Vec2(p3.X, p3.Y);
                pts.Add(p);

                bool hasNext = i < n - 1 || pl.Closed;
                if (!hasNext) break;

                Point3d q3 = pl.GetPoint3dAt((i + 1) % n);
                var q = new Vec2(q3.X, q3.Y);
                double bulge = pl.GetBulgeAt(i);

                if (Math.Abs(bulge) < 1e-9) { pts.Add(q); continue; }

                // 优先让 AutoCAD 自己给出圆弧几何（GetArcSegment2dAt），
                // 彻底规避 bulge 正负方向的符号推断风险；
                // 取不到时退回 Core 里的纯数学离散。
                if (!AppendArcByAutoCad(pl, i, tol, pts))
                    GeometryUtil.AppendBulgeArc(pts, p, q, bulge, tol);
            }

            // 闭合时最后一点与首点重复，去掉
            if (pl.Closed && pts.Count > 1 &&
                pts[pts.Count - 1].DistanceTo(pts[0]) < 1e-7)
                pts.RemoveAt(pts.Count - 1);

            points = pts;
            return pts.Count >= 3 || !pl.Closed;
        }

        /// <summary>
        /// 直接向 AutoCAD 要第 i 段的圆弧几何对象，按角度采样。
        /// 这样圆弧的圆心、方向和正负完全由 AutoCAD 决定，不做任何符号推断。
        /// </summary>
        private static bool AppendArcByAutoCad(Autodesk.AutoCAD.DatabaseServices.Polyline pl,
                                               int i, double tol, List<Vec2> outPts)
        {
            try
            {
                CircularArc2d arc = pl.GetArcSegment2dAt(i);
                if (arc == null) return false;

                double sa = arc.StartAngle;
                double sweep = arc.EndAngle - sa;
                if (arc.IsClockWise) { while (sweep >= 0) sweep -= Math.PI * 2.0; }
                else { while (sweep <= 0) sweep += Math.PI * 2.0; }

                int seg = GeometryUtil.SegmentsForArc(arc.Radius, Math.Abs(sweep), tol);

                for (int k = 1; k <= seg; k++)
                {
                    Point2d pt = arc.EvaluatePoint(sa + sweep * k / seg);
                    outPts.Add(new Vec2(pt.X, pt.Y));
                }
                return true;
            }
            catch { return false; }
        }

        private static List<Vec2> SampleByDistance(Curve c, double tol)
        {
            var pts = new List<Vec2>();
            double len;
            try { len = c.GetDistanceAtParameter(c.EndParam); }
            catch { return pts; }
            if (len <= 0) return pts;

            // 通用曲线拿不到半径，用折线逼近经验式 n ≈ sqrt(L / (8·tol))
            int n = (int)Math.Ceiling(Math.Sqrt(len / (8.0 * tol)));
            if (n < 8) n = 8;
            if (n > 2000) n = 2000;

            for (int i = 0; i <= n; i++)
            {
                Point3d p = c.GetPointAtDist(len * i / n);
                pts.Add(new Vec2(p.X, p.Y));
            }
            return pts;
        }
    }
}
