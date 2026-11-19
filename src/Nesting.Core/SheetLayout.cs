using System;

namespace Nesting.Core
{
    /// <summary>
    /// 排样结果落到图纸上时的坐标换算。
    ///
    /// 单独抽出来是因为"插入点"最容易被改错：板框、零件、跟随文字三者的基准
    /// 必须完全一致，任何一处漏掉 Y 偏移，表现都是"零件压到板框外面"。
    /// Commands 里只做 Point3d 包装，实际算术都在这里，可以脱离 AutoCAD 验证。
    /// </summary>
    public static class SheetLayout
    {
        /// <summary>板与板之间留的空隙（mm）。只为出图时分开看，与排样无关</summary>
        public const double GapBetweenSheets = 300;

        /// <summary>第 index 张板左下角的世界坐标（插入点 = 第一张板左下角）</summary>
        public static Vec2 SheetOrigin(NestResult result, int index, Vec2 insert)
        {
            double x = insert.X;

            if (result != null && result.Sheets != null)
            {
                int n = Math.Min(index, result.Sheets.Count);
                for (int i = 0; i < n; i++)
                    x += result.Sheets[i].Width + GapBetweenSheets;
            }

            return new Vec2(x, insert.Y);
        }

        /// <summary>零件在板内的局部坐标 → 世界坐标</summary>
        public static Vec2 ToWorld(NestResult result, int index, Vec2 insert,
                                   double localX, double localY)
        {
            Vec2 o = SheetOrigin(result, index, insert);
            return new Vec2(o.X + localX, o.Y + localY);
        }
    }
}
