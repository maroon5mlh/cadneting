using System;
using System.Collections.Generic;
using System.Text.Json;
using Nesting.Core;
using Nesting.Core.Solvers;

namespace SmokeTest
{
    internal static class DebugNest
    {
        public static void Run()
        {
            var job = new NestJob { BatchId = "STRESS", Unit = "mm" };
            job.Sheets.Add(new SheetSpec { Name = "1220x2440", Width = 1220, Height = 2440 });
            job.Sheets.Add(new SheetSpec { Name = "1220x3050", Width = 1220, Height = 3050 });
            job.Settings.SpacingMm = 0.2;

            var rnd = new Random(20261115);
            double[] widths = { 400, 450, 500, 564, 600, 645.4, 700, 800, 900, 1000 };
            double[] heights = { 300, 350, 400, 500, 600, 711, 776, 900 };

            int id = 0;
            for (int i = 0; i < 28; i++)
            {
                double w = widths[rnd.Next(widths.Length)];
                double h = heights[rnd.Next(heights.Length)];
                int qty = rnd.Next(8, 22);
                var p = new PartType
                {
                    Id = "P" + (++id).ToString("000"),
                    BlockName = "合成板" + id,
                    Width = w,
                    Height = h,
                    Quantity = qty,
                    NetArea = w * h * 0.995
                };
                p.Outer = new Contour(new[] {
                    new Vec2(0,0), new Vec2(w,0), new Vec2(w,h), new Vec2(0,h) });
                job.Parts.Add(p);
            }

            NestResult r = NestEngine.Solve(job);

            foreach (SheetResult s in r.Sheets)
            {
                var boxes = new List<double[]>();
                var names = new List<string>();
                foreach (Placement pl in s.Items)
                {
                    PartType pt = null;
                    foreach (PartType q in job.Parts) if (q.Id == pl.PartTypeId) pt = q;
                    double w = pl.RotationDeg == 90 ? pt.Height : pt.Width;
                    double h = pl.RotationDeg == 90 ? pt.Width : pt.Height;
                    boxes.Add(new[] { pl.X, pl.Y, pl.X + w, pl.Y + h });
                    names.Add(pl.PartTypeId + " rot" + pl.RotationDeg + " " + w + "x" + h);
                }

                for (int i = 0; i < boxes.Count; i++)
                    for (int j = i + 1; j < boxes.Count; j++)
                    {
                        double ox = Math.Min(boxes[i][2], boxes[j][2]) - Math.Max(boxes[i][0], boxes[j][0]);
                        double oy = Math.Min(boxes[i][3], boxes[j][3]) - Math.Max(boxes[i][1], boxes[j][1]);
                        if (ox > 1e-6 && oy > 1e-6)
                        {
                            Console.WriteLine("### 重叠 板#" + (s.Index + 1) + " idx " + i + "/" + j);
                            Console.WriteLine("  A " + names[i] + " box=[" + string.Join(", ", boxes[i]) + "]");
                            Console.WriteLine("  B " + names[j] + " box=[" + string.Join(", ", boxes[j]) + "]");
                            Console.WriteLine("  overlap " + ox.ToString("F3") + " x " + oy.ToString("F3"));
                        }
                    }
            }
            Console.WriteLine("done");
        }
    }
}
