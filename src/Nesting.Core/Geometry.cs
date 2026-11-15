using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nesting.Core
{
    /// <summary>
    /// 几何工具：归一化、几何指纹、面积修正等。
    /// 这里的算法不依赖 AutoCAD，可单元测试。
    /// </summary>
    public static class GeometryUtil
    {
        /// <summary>
        /// 把轮廓平移到以包围盒左下角为原点，返回平移后的轮廓。
        /// 排样算法统一在“零件局部坐标”下工作，避免基点问题。
        /// </summary>
        public static Contour NormalizeToOrigin(Contour c, out Vec2 originalMin)
        {
            Vec2 min, max;
            c.GetBounds(out min, out max);
            originalMin = min;
            return c.TranslatedBy(new Vec2(-min.X, -min.Y));
        }

        /// <summary>
        /// 几何指纹：用于识别“同一个零件”。
        /// 键 = 顶点数 + 归一化后按 0.01mm 取整的点序列 + 面积。
        /// 注意：镜像件会产生不同的指纹，这是有意为之——镜像件在钣金里是不同零件。
        /// </summary>
        public static string Fingerprint(Contour normalizedOuter, int holeCount)
        {
            var sb = new StringBuilder(256);
            sb.Append(normalizedOuter.Count).Append('|');
            sb.Append(Math.Round(normalizedOuter.AbsArea, 2).ToString("F2", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(holeCount).Append('|');
            for (int i = 0; i < normalizedOuter.Count; i++)
            {
                Vec2 p = normalizedOuter.Points[i];
                // 取整到 0.01mm，抹掉 CAD 里的浮点噪声
                long x = (long)Math.Round(p.X * 100.0, MidpointRounding.AwayFromZero);
                long y = (long)Math.Round(p.Y * 100.0, MidpointRounding.AwayFromZero);
                sb.Append(x).Append(',').Append(y).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>净面积 = 外轮廓面积 - 所有孔面积</summary>
        public static double NetArea(Contour outer, System.Collections.Generic.List<Contour> holes)
        {
            double a = outer.AbsArea;
            if (holes != null)
                for (int i = 0; i < holes.Count; i++) a -= holes[i].AbsArea;
            return a < 0 ? 0 : a;
        }

        /// <summary>把角度归一化到 [0, 360)</summary>
        public static double NormalizeDeg(double deg)
        {
            double d = deg % 360.0;
            if (d < 0) d += 360.0;
            return d;
        }

        /// <summary>把弧度归一化到 [0, 2π)</summary>
        public static double Norm2Pi(double rad)
        {
            double r = rad % (Math.PI * 2.0);
            if (r < 0) r += Math.PI * 2.0;
            return r;
        }

        /// <summary>绕点旋转（角度制）</summary>
        public static Vec2 Rotate(Vec2 p, Vec2 center, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            double dx = p.X - center.X, dy = p.Y - center.Y;
            return new Vec2(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
        }

        /// <summary>轮廓绕点旋转</summary>
        public static Contour RotateContour(Contour c, Vec2 center, double deg)
        {
            var pts = new Vec2[c.Count];
            for (int i = 0; i < c.Count; i++) pts[i] = Rotate(c.Points[i], center, deg);
            return new Contour(pts);
        }

        // ==================================================================
        // LWPOLYLINE 凸度（bulge）圆弧离散
        // 放在 Core 里是为了能脱离 AutoCAD 单元测试——这里的符号约定最易出错。
        // ==================================================================

        /// <summary>
        /// 处理一段凸度圆弧：把 p1→p2 的圆弧采样点追加到 outPts（不含 p1，含 p2）。
        /// bulge = tan(圆心角/4)，正值表示逆时针。
        /// </summary>
        public static void AppendBulgeArc(List<Vec2> outPts, Vec2 p1, Vec2 p2, double bulge, double tol)
        {
            if (Math.Abs(bulge) < 1e-9) { outPts.Add(p2); return; }
            if (tol <= 0) tol = 0.1;

            double theta = 4.0 * Math.Atan(bulge);
            double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            double sinHalf = Math.Sin(theta * 0.5);
            if (chord < 1e-9 || Math.Abs(sinHalf) < 1e-9) { outPts.Add(p2); return; }

            double r = Math.Abs(chord / (2.0 * sinHalf));
            double mx = (p1.X + p2.X) * 0.5, my = (p1.Y + p2.Y) * 0.5;
            double nx = -dy / chord, ny = dx / chord;
            double t = Math.Sqrt(Math.Max(0.0, r * r - chord * chord * 0.25));

            var cA = new Vec2(mx + nx * t, my + ny * t);
            var cB = new Vec2(mx - nx * t, my - ny * t);
            Vec2 center = PickCenter(cA, cB, p1, p2, theta);

            double a1 = Math.Atan2(p1.Y - center.Y, p1.X - center.X);
            double a2 = Math.Atan2(p2.Y - center.Y, p2.X - center.X);
            double sweep = (theta > 0)
                ? Norm2Pi(a2 - a1)
                : -Norm2Pi(a1 - a2);

            double arcLen = Math.Abs(sweep) * r;
            int seg = SegmentsForArc(r, Math.Abs(sweep), tol);

            for (int i = 1; i <= seg; i++)
            {
                double a = a1 + sweep * i / seg;
                outPts.Add(new Vec2(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a)));
            }
        }

        /// <summary>
        /// 按「弦高容差」计算圆弧需要的分段数。
        /// 弦高 h = r(1 - cos(Δθ/2))，令 h = tol 得 Δθ = 2·acos(1 - tol/r)。
        /// 注意：不能用「弧长/tol」，那会把一条 157mm 的弧切成 1571 段（密 60 倍）。
        /// </summary>
        public static int SegmentsForArc(double radius, double sweepAbs, double tol)
        {
            if (tol <= 0) tol = 0.1;
            if (radius <= 1e-9 || sweepAbs <= 1e-9) return 1;

            double ratio = 1.0 - tol / radius;
            int seg;
            if (ratio <= -1.0)
            {
                seg = 4;                       // 容差比半径还大，最少给 4 段
            }
            else
            {
                double da = 2.0 * Math.Acos(ratio);
                seg = (int)Math.Ceiling(sweepAbs / da);
            }

            if (seg < 2) seg = 2;
            if (seg > 1000) seg = 1000;        // 保险丝，防止极端参数炸掉
            return seg;
        }

        /// <summary>
        /// 垂直平分线上两个候选圆心，挑扫角与理论圆心角最接近的那个。
        /// 用校验代替手推符号，避免 bulge 正负方向搞反。
        /// </summary>
        private static Vec2 PickCenter(Vec2 cA, Vec2 cB, Vec2 p1, Vec2 p2, double theta)
        {
            double target = Math.Abs(theta);
            return SweepError(cA, p1, p2, theta, target) <= SweepError(cB, p1, p2, theta, target)
                ? cA : cB;
        }

        private static double SweepError(Vec2 c, Vec2 p1, Vec2 p2, double theta, double target)
        {
            double a1 = Math.Atan2(p1.Y - c.Y, p1.X - c.X);
            double a2 = Math.Atan2(p2.Y - c.Y, p2.X - c.X);
            double sweep = (theta > 0) ? Norm2Pi(a2 - a1) : Norm2Pi(a1 - a2);
            return Math.Abs(sweep - target);
        }
    }
}
