using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nesting.Core;
using Nesting.Core.Solvers;

namespace SmokeTest
{
    /// <summary>
    /// 不依赖 AutoCAD 的冒烟测试：验证几何离散、品种归并、JSON 协议三块。
    /// 运行：dotnet run --project tests/SmokeTest
    /// </summary>
    internal static class Program
    {
        private static int _failed;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args != null && args.Length > 0 && args[0] == "debug")
            {
                DebugNest.Run();
                return 0;
            }

            Console.WriteLine("===== CAD 排样 Core 冒烟测试 =====\n");

            TestBulgeSemicircle();
            TestBulgeQuarter();
            TestBulgeNegative();
            TestBulgeStraight();
            TestGrouping();
            TestJsonRoundTrip();
            TestNestRealJob();
            TestNestStress();
            TestMargin();
            TestKeywordRotation();
            TestTextHandles();

            Console.WriteLine();
            if (_failed == 0)
            {
                Console.WriteLine("全部通过。");
                return 0;
            }
            Console.WriteLine("失败 " + _failed + " 项。");
            return 1;
        }

        // ------------------------------------------------------------------
        // 凸度圆弧：验证采样点确实落在同一个圆上
        // ------------------------------------------------------------------
        private static void TestBulgeSemicircle()
        {
            var p1 = new Vec2(0, 0);
            var p2 = new Vec2(100, 0);
            var pts = new List<Vec2> { p1 };

            // bulge = 1 → 圆心角 180°，半径应为弦长一半
            GeometryUtil.AppendBulgeArc(pts, p1, p2, 1.0, 0.1);

            double maxErr = 0, maxSagitta = 0;
            double minY = 0, maxY = 0;
            var center = new Vec2(50, 0);
            for (int i = 0; i < pts.Count; i++)
            {
                maxErr = Math.Max(maxErr, Math.Abs(pts[i].DistanceTo(center) - 50.0));
                minY = Math.Min(minY, pts[i].Y);
                maxY = Math.Max(maxY, pts[i].Y);

                if (i > 0)
                {
                    var mid = new Vec2((pts[i].X + pts[i - 1].X) / 2, (pts[i].Y + pts[i - 1].Y) / 2);
                    maxSagitta = Math.Max(maxSagitta, 50.0 - mid.DistanceTo(center));
                }
            }

            Check("半圆 bulge=1 采样点落在半径 50 的圆上", maxErr < 0.01, "最大偏差 " + maxErr.ToString("F6") + " mm");
            Check("半圆弦高误差 <= 容差 0.1mm", maxSagitta <= 0.1001, "最大弦高 " + maxSagitta.ToString("F4") + " mm");
            Check("半圆分段数经济（<= 40 段）", pts.Count - 1 <= 40, (pts.Count - 1) + " 段");
            Check("半圆端点落在 (100,0)", pts[pts.Count - 1].DistanceTo(p2) < 1e-9, "");

            Console.WriteLine("    圆弧朝向：y 范围 [" + minY.ToString("F2") + ", " + maxY.ToString("F2") +
                              "]（正 bulge 为逆时针，AutoCAD 定义下应偏 x 轴负侧）");
        }

        private static void TestBulgeQuarter()
        {
            var p1 = new Vec2(0, 0);
            var p2 = new Vec2(100, 0);
            var pts = new List<Vec2> { p1 };

            // bulge = tan(22.5°) → 圆心角 90°
            double bulge = Math.Tan(Math.PI / 8.0);
            GeometryUtil.AppendBulgeArc(pts, p1, p2, bulge, 0.05);

            // 90° 圆心角、弦长 100 → 半径 = 100 / (2·sin45°)
            double r = 100.0 / (2.0 * Math.Sin(Math.PI / 4.0));
            double maxErr = 0;
            foreach (Vec2 p in pts)
                maxErr = Math.Max(maxErr, DistanceToChordCircle(p, p1, p2, r));
            Check("四分之一圆 bulge=tan22.5° 半径一致", maxErr < 0.05, "最大偏差 " + maxErr.ToString("F6") + "mm, r=" + r.ToString("F3"));
        }

        /// <summary>点到「过 p1/p2 且半径 r 的圆」圆心的距离偏差</summary>
        private static double DistanceToChordCircle(Vec2 p, Vec2 p1, Vec2 p2, double r)
        {
            // 圆心在弦的垂直平分线上，距弦中点 h = sqrt(r^2 - (L/2)^2)
            double L = p1.DistanceTo(p2);
            double h = Math.Sqrt(Math.Max(0, r * r - L * L / 4.0));
            var mid = new Vec2((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            var n = new Vec2(-(p2.Y - p1.Y) / L, (p2.X - p1.X) / L);
            var c1 = new Vec2(mid.X + n.X * h, mid.Y + n.Y * h);
            var c2 = new Vec2(mid.X - n.X * h, mid.Y - n.Y * h);
            return Math.Min(Math.Abs(p.DistanceTo(c1) - r), Math.Abs(p.DistanceTo(c2) - r));
        }

        private static void TestBulgeNegative()
        {
            var p1 = new Vec2(0, 0);
            var p2 = new Vec2(100, 0);
            var pos = new List<Vec2> { p1 };
            var neg = new List<Vec2> { p1 };

            GeometryUtil.AppendBulgeArc(pos, p1, p2, 1.0, 0.1);
            GeometryUtil.AppendBulgeArc(neg, p1, p2, -1.0, 0.1);

            // 正负 bulge 应在弦的两侧
            double posMinY = 0, negMaxY = 0;
            for (int i = 0; i < pos.Count; i++) posMinY = Math.Min(posMinY, pos[i].Y);
            for (int i = 0; i < neg.Count; i++) negMaxY = Math.Max(negMaxY, neg[i].Y);

            bool opposite = (posMinY < -1 && negMaxY > 1) || (posMinY > 1 && negMaxY < -1);
            Check("正负 bulge 分居弦两侧", opposite,
                  "bulge=+1 最低 y=" + posMinY.ToString("F2") + "，bulge=-1 最高 y=" + negMaxY.ToString("F2"));
        }

        private static void TestBulgeStraight()
        {
            var pts = new List<Vec2> { new Vec2(0, 0) };
            GeometryUtil.AppendBulgeArc(pts, new Vec2(0, 0), new Vec2(100, 0), 0.0, 0.1);
            Check("bulge=0 只产生终点", pts.Count == 2, pts.Count + " 点");
        }

        // ------------------------------------------------------------------
        // 品种归并
        // ------------------------------------------------------------------
        private static void TestGrouping()
        {
            var raws = new List<RawPart>();

            // 矩形 400x200，4 个实例放在不同位置
            for (int i = 0; i < 4; i++)
                raws.Add(new RawPart
                {
                    BlockName = "PL-400X200",
                    Handle = "R" + i,
                    Outer = Rect(i * 1000 + 10, i * 500 + 20, 400, 200)
                });

            // L 形 2 个实例
            for (int i = 0; i < 2; i++)
                raws.Add(new RawPart
                {
                    BlockName = "L-BRACKET",
                    Handle = "L" + i,
                    Outer = LShape(3000 + i * 800, 4000)
                });

            // 同一个矩形但以 90° 插入（宽高互换）
            raws.Add(new RawPart
            {
                BlockName = "PL-400X200",
                Handle = "R90",
                Outer = Rect(6000, 6000, 200, 400)
            });

            List<string> warnings;
            List<PartType> types = PartGrouper.Group(raws, out warnings);

            Check("7 个块归并为 3 个品种（矩形/L形/旋转矩形）", types.Count == 3,
                  "实际 " + types.Count + " 个");

            int rectQty = 0, lQty = 0;
            foreach (PartType t in types)
            {
                if (t.BlockName == "PL-400X200" && t.Width > t.Height) rectQty = t.Quantity;
                if (t.BlockName == "L-BRACKET") lQty = t.Quantity;
                Console.WriteLine(string.Format("    {0,-5} {1,-12} {2,7:0.#}x{3,-7:0.#} 数量 {4}  净面积 {5:N0}",
                    t.Id, t.BlockName, t.Width, t.Height, t.Quantity, t.NetArea));
            }

            Check("同位置无关的矩形实例归并为 4 件", rectQty == 4, "实际 " + rectQty + " 件");
            Check("L 形归并为 2 件", lQty == 2, "实际 " + lQty + " 件");
        }

        // ------------------------------------------------------------------
        // JSON 协议
        // ------------------------------------------------------------------
        private static void TestJsonRoundTrip()
        {
            var job = new NestJob { BatchId = "TEST", Unit = "mm" };
            job.Sheets.Add(new SheetSpec { Name = "1220x2440", Width = 1220, Height = 2440 });
            job.Parts.Add(new PartType
            {
                Id = "P001",
                BlockName = "侧板",
                Outer = Rect(0, 0, 400, 200),
                NetArea = 80000,
                Width = 400,
                Height = 200,
                Quantity = 3,
                Handles = new List<string> { "A1", "A2", "A3" }
            });

            var opts = new JsonSerializerOptions
            {
                IncludeFields = true,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string json = JsonSerializer.Serialize(job, opts);
            NestJob back = JsonSerializer.Deserialize<NestJob>(json, opts);

            Check("JSON 往返后板材数一致", back.Sheets.Count == 1, "");
            Check("JSON 往返后零件数一致", back.Parts.Count == 1, "");
            Check("JSON 往返后轮廓顶点数一致", back.Parts[0].Outer.Count == 4,
                  "实际 " + back.Parts[0].Outer.Count);
            Check("JSON 往返后中文块名未乱码", back.Parts[0].BlockName == "侧板",
                  "实际 " + back.Parts[0].BlockName);
            Check("派生属性未被写出（SignedArea/Count 不应出现）",
                  json.IndexOf("SignedArea", StringComparison.Ordinal) < 0 &&
                  json.IndexOf("\"Count\"", StringComparison.Ordinal) < 0, "");

            string preview = json.Length > 320 ? json.Substring(0, 320) + "..." : json;
            Console.WriteLine("    JSON 预览：\n" + preview);
        }

        // ------------------------------------------------------------------
        // 排样：用你实际导出的任务文件跑一遍
        // ------------------------------------------------------------------
        private static void TestNestRealJob()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string[] files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "nestjob_*.json")
                : new string[0];

            if (files.Length == 0)
            {
                Console.WriteLine("  [SKIP] 桌面上没有 nestjob_*.json，跳过实图验证");
                return;
            }

            Array.Sort(files, (a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            string path = files[0];
            Console.WriteLine("  实图任务：" + Path.GetFileName(path));

            var opts = new JsonSerializerOptions
            {
                IncludeFields = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            NestJob job;
            try { job = JsonSerializer.Deserialize<NestJob>(File.ReadAllText(path), opts); }
            catch (Exception ex)
            {
                Check("实图任务文件可解析", false, ex.Message);
                return;
            }

            NestResult r = NestEngine.Solve(job);
            Check("实图排样无错误", r.Error == null, r.Error ?? "");

            int placed = 0;
            foreach (SheetResult s in r.Sheets) placed += s.Items.Count;
            int demand = 0;
            double netArea = 0;
            double sheetW = 0, sheetH = 0;
            foreach (PartType p in job.Parts)
            {
                demand += p.Quantity;
                netArea += p.NetArea * p.Quantity;
            }
            if (job.Sheets.Count > 0)
            {
                // 用最大那张板算理论下限：实际只会更多，不会更少
                foreach (SheetSpec s in job.Sheets)
                    if (s.Width * s.Height > sheetW * sheetH) { sheetW = s.Width; sheetH = s.Height; }
            }

            Check("实图零件全部落位", placed == demand, placed + "/" + demand + " 件");

            if (sheetW > 0)
            {
                double usable = (sheetW - 2 * job.Settings.MarginMm) * (sheetH - 2 * job.Settings.MarginMm);
                int lower = (int)Math.Ceiling(netArea / usable - 1e-9);
                // 面积下限是物理极限；超过下限 2 张说明算法或间距设置有问题
                Check("实图张数在合理区间（面积下限 ~ 下限+2）",
                      r.Sheets.Count >= lower && r.Sheets.Count <= lower + 2,
                      "净面积 " + (netArea / 1e6).ToString("F2") + " m²，下限 " + lower +
                      " 张，实际 " + r.Sheets.Count + " 张");
                Console.WriteLine("    理论面积下限 " + lower + " 张（" + sheetW.ToString("F0") + "x" +
                                  sheetH.ToString("F0") + "，留边 " + job.Settings.MarginMm + "mm）");
            }

            Console.WriteLine("    耗时 " + r.ElapsedMs + " ms，总利用率 " + r.TotalUtilization.ToString("P2"));
            foreach (SheetResult s in r.Sheets)
            {
                Console.WriteLine("    板#" + (s.Index + 1) + " " + s.SheetName +
                                  " 利用率 " + s.Utilization.ToString("P2"));
                foreach (Placement pl in s.Items)
                {
                    PartType pt = FindPart(job, pl.PartTypeId);
                    Console.WriteLine(string.Format("      {0,-5} 句柄 {1,-6} ({2,7:0.0}, {3,7:0.0})  旋{4,3:0}°  尺寸 {5:0.0}x{6:0.0}",
                        pl.PartTypeId, pl.Handle ?? "-", pl.X, pl.Y, pl.RotationDeg,
                        pl.RotationDeg == 90 ? pt.Height : pt.Width,
                        pl.RotationDeg == 90 ? pt.Width : pt.Height));
                }
            }

            VerifyGeometry(job, r, "实图");
        }

        // ------------------------------------------------------------------
        // 排样：合成压力测试（400 件，含 L 形异形件）
        // ------------------------------------------------------------------
        private static void TestNestStress()
        {
            var job = new NestJob { BatchId = "STRESS", Unit = "mm" };
            job.Sheets.Add(new SheetSpec { Name = "1220x2440", Width = 1220, Height = 2440 });
            job.Sheets.Add(new SheetSpec { Name = "1220x3050", Width = 1220, Height = 3050 });
            job.Settings.SpacingMm = 0.2;

            var rnd = new Random(20261115);
            double[] widths = { 400, 450, 500, 564, 600, 645.4, 700, 800, 900, 1000 };
            double[] heights = { 300, 350, 400, 500, 600, 711, 776, 900 };

            int id = 0, total = 0;
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
                    NetArea = w * h * 0.995   // 与实图一致：带角部台阶的板
                };
                p.Outer = Rect(0, 0, w, h);
                job.Parts.Add(p);
                total += qty;
            }

            NestResult r = NestEngine.Solve(job);
            Check("压测排样无错误", r.Error == null, r.Error ?? "");

            int placed = 0;
            foreach (SheetResult s in r.Sheets) placed += s.Items.Count;
            Check("压测 400 件全部落位", placed == total, placed + "/" + total + " 件");
            Check("压测利用率 > 80%", r.TotalUtilization > 0.80, r.TotalUtilization.ToString("P2"));
            Check("压测耗时 < 3s", r.ElapsedMs < 3000, r.ElapsedMs + " ms");

            Console.WriteLine("    " + total + " 件 → " + r.Sheets.Count + " 张板，利用率 " +
                              r.TotalUtilization.ToString("P2") + "，耗时 " + r.ElapsedMs + " ms");
            foreach (SheetResult s in r.Sheets)
                Console.WriteLine("    板#" + (s.Index + 1) + " " + s.SheetName + "  件数 " +
                                  s.Items.Count + "  利用率 " + s.Utilization.ToString("P2"));

            VerifyGeometry(job, r, "压测");
        }

        /// <summary>几何校验：这是排样引擎唯一真正重要的正确性证明。
        /// 越界或重叠任何一项不通过，说明结果不可用。</summary>
        private static void VerifyGeometry(NestJob job, NestResult r, string tag)
        {
            bool inBounds = true, noOverlap = true, respectsMargin = true;
            string firstBad = "";
            string firstMarginBad = "";
            double margin = job.Settings.MarginMm > 0 ? job.Settings.MarginMm : 0;

            foreach (SheetResult s in r.Sheets)
            {
                var boxes = new List<double[]>();
                foreach (Placement pl in s.Items)
                {
                    PartType pt = FindPart(job, pl.PartTypeId);
                    if (pt == null) { inBounds = false; firstBad = "找不到品种 " + pl.PartTypeId; continue; }

                    double w = pl.RotationDeg == 90 ? pt.Height : pt.Width;
                    double h = pl.RotationDeg == 90 ? pt.Width : pt.Height;

                    if (pl.X < -1e-6 || pl.Y < -1e-6 ||
                        pl.X + w > s.Width + 1e-6 || pl.Y + h > s.Height + 1e-6)
                    {
                        inBounds = false;
                        firstBad = pl.PartTypeId + " 越界 (" + pl.X.ToString("F1") + "," + pl.Y.ToString("F1") +
                                   ") " + w.ToString("F1") + "x" + h.ToString("F1");
                    }

                    // 留边：四边都要守住。只查左边会被"贴着右边切不出来"这种问题漏过去
                    if (pl.X < margin - 1e-6 || pl.Y < margin - 1e-6 ||
                        pl.X + w > s.Width - margin + 1e-6 ||
                        pl.Y + h > s.Height - margin + 1e-6)
                    {
                        respectsMargin = false;
                        firstMarginBad = pl.PartTypeId + " 侵入留边区 (" +
                                         pl.X.ToString("F2") + "," + pl.Y.ToString("F2") + ") " +
                                         w.ToString("F1") + "x" + h.ToString("F1") +
                                         "，留边 " + margin.ToString("F1");
                    }

                    boxes.Add(new[] { pl.X, pl.Y, pl.X + w, pl.Y + h });
                }

                for (int i = 0; i < boxes.Count; i++)
                    for (int j = i + 1; j < boxes.Count; j++)
                    {
                        double ox = Math.Min(boxes[i][2], boxes[j][2]) - Math.Max(boxes[i][0], boxes[j][0]);
                        double oy = Math.Min(boxes[i][3], boxes[j][3]) - Math.Max(boxes[i][1], boxes[j][1]);
                        if (ox > 1e-6 && oy > 1e-6)
                        {
                            noOverlap = false;
                            firstBad = "板#" + (s.Index + 1) + " 第 " + i + "/" + j + " 件重叠 " +
                                       ox.ToString("F3") + "x" + oy.ToString("F3") + " mm";
                        }
                    }
            }

            Check(tag + "：所有零件在板内", inBounds, inBounds ? "" : firstBad);
            Check(tag + "：零件之间无重叠", noOverlap, noOverlap ? "" : firstBad);
            Check(tag + "：零件避开 " + margin.ToString("0.#") + "mm 留边", respectsMargin,
                  respectsMargin ? "" : firstMarginBad);
        }

        // ------------------------------------------------------------------
        // 留边：零件必须落在 [margin, 板幅-margin] 区间内，四边都要守住
        // ------------------------------------------------------------------
        private static void TestMargin()
        {
            NestJob job = MakeGridJob(20);
            NestResult r = NestEngine.Solve(job);
            Check("留边 20mm 排样无错误", r.Error == null, r.Error ?? "");
            if (r.Error != null) return;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxRight = 0, maxTop = 0;

            foreach (SheetResult s in r.Sheets)
                foreach (Placement pl in s.Items)
                {
                    PartType pt = FindPart(job, pl.PartTypeId);
                    double w = pl.RotationDeg == 90 ? pt.Height : pt.Width;
                    double h = pl.RotationDeg == 90 ? pt.Width : pt.Height;

                    minX = Math.Min(minX, pl.X);
                    minY = Math.Min(minY, pl.Y);
                    maxRight = Math.Max(maxRight, pl.X + w);
                    maxTop = Math.Max(maxTop, pl.Y + h);
                }

            Check("留边：左侧最小 X >= 20mm", minX >= 20 - 1e-6, "实测 " + minX.ToString("F3") + " mm");
            Check("留边：下侧最小 Y >= 20mm", minY >= 20 - 1e-6, "实测 " + minY.ToString("F3") + " mm");
            Check("留边：右侧余量 >= 20mm", maxRight <= 1220 - 20 + 1e-6,
                  "最右 " + maxRight.ToString("F3") + " / 允许 " + (1220 - 20).ToString("F1"));
            Check("留边：上侧余量 >= 20mm", maxTop <= 2440 - 20 + 1e-6,
                  "最上 " + maxTop.ToString("F3") + " / 允许 " + (2440 - 20).ToString("F1"));

            // 留边吃掉可用面积，张数只能持平或变多 —— 变少说明坐标被重复偏移了
            NestResult r0 = NestEngine.Solve(MakeGridJob(0));
            Check("留边 20mm 的张数不少于无留边", r.Sheets.Count >= r0.Sheets.Count,
                  "留边 " + r.Sheets.Count + " 张 vs 无留边 " + r0.Sheets.Count + " 张");

            Console.WriteLine("    留边 20mm：" + r.Sheets.Count + " 张，利用率 " + r.TotalUtilization.ToString("P2"));
            VerifyGeometry(job, r, "留边");
        }

        private static NestJob MakeGridJob(double margin)
        {
            var job = new NestJob { BatchId = "MARGIN", Unit = "mm" };
            job.Sheets.Add(new SheetSpec { Name = "1220x2440", Width = 1220, Height = 2440 });
            job.Settings.SpacingMm = 0.2;
            job.Settings.MarginMm = margin;

            for (int i = 0; i < 6; i++)
            {
                var p = new PartType
                {
                    Id = "P00" + (i + 1),
                    BlockName = "件" + (i + 1),
                    Width = 500,
                    Height = 400,
                    NetArea = 200000,
                    Quantity = 4
                };
                p.Outer = Rect(0, 0, 500, 400);
                job.Parts.Add(p);
            }
            return job;
        }

        // ------------------------------------------------------------------
        // 关键字白名单：全局关掉旋转后，命中关键字的件仍然转
        // ------------------------------------------------------------------
        private static void TestKeywordRotation()
        {
            Check("关键字识别：顶包边命中", RotationPolicy.IsForcedRotate("顶包边-2611060", null), "");
            Check("关键字识别：中横框命中", RotationPolicy.IsForcedRotate("中横框A", null), "");
            Check("关键字识别：普通件不命中", !RotationPolicy.IsForcedRotate("普通侧板", null), "");

            // 板 600 宽：1100x400 不转放不下，转 90° 后 400x1100 刚好塞进去
            NestJob job = MakeRotateJob("顶包边-1200");
            NestResult r = NestEngine.Solve(job);

            bool rotated = r.Error == null && r.Sheets.Count == 1 &&
                           r.Sheets[0].Items.Count == 1 &&
                           Math.Abs(r.Sheets[0].Items[0].RotationDeg - 90) < 1e-6;

            Check("全局关闭旋转时，关键字段仍可转 90°", rotated,
                  r.Error ?? ("张数 " + r.Sheets.Count + "，旋转 " +
                              (r.Sheets.Count > 0 && r.Sheets[0].Items.Count > 0
                                  ? r.Sheets[0].Items[0].RotationDeg.ToString("F0") + "°" : "-")));

            // 同样的零件换个名字，全局关闭后就应该放不下
            NestJob job2 = MakeRotateJob("普通侧板");
            NestResult r2 = NestEngine.Solve(job2);
            Check("全局关闭旋转时，普通件不放倒（放不下应报错）", r2.Error != null,
                  r2.Error == null ? "意外排成功，可能悄悄转了 90°" : r2.Error);

            // 全局打开时，普通件也能转
            job2.Settings.AllowRotate90 = true;
            NestResult r3 = NestEngine.Solve(job2);
            Check("全局打开旋转时，普通件可转 90°",
                  r3.Error == null && r3.Sheets.Count == 1 &&
                  Math.Abs(r3.Sheets[0].Items[0].RotationDeg - 90) < 1e-6,
                  r3.Error ?? "");
        }

        private static NestJob MakeRotateJob(string blockName)
        {
            var job = new NestJob { BatchId = "KW", Unit = "mm" };
            job.Sheets.Add(new SheetSpec { Name = "600x1220", Width = 600, Height = 1220 });
            job.Settings.AllowRotate90 = false;
            job.Settings.MarginMm = 5;
            job.Settings.SpacingMm = 0;

            var p = new PartType
            {
                Id = "P001",
                BlockName = blockName,
                Width = 1100,
                Height = 400,
                NetArea = 440000,
                Quantity = 1
            };
            p.Outer = Rect(0, 0, 1100, 400);
            job.Parts.Add(p);
            return job;
        }

        // ------------------------------------------------------------------
        // 跟随文字句柄：按实例传递，且不能串位
        // ------------------------------------------------------------------
        private static void TestTextHandles()
        {
            var job = new NestJob { BatchId = "TEXT", Unit = "mm" };
            job.Sheets.Add(new SheetSpec { Name = "1220x2440", Width = 1220, Height = 2440 });
            job.Settings.MarginMm = 5;

            var p = new PartType
            {
                Id = "P001",
                BlockName = "顶包边",
                Width = 400,
                Height = 300,
                NetArea = 120000,
                Quantity = 3
            };
            p.Outer = Rect(0, 0, 400, 300);

            p.Handles.AddRange(new[] { "A1", "A2", "A3" });
            p.TextHandles.AddRange(new[] { "T1,T2", "", "T3" });   // 第二件没有文字
            job.Parts.Add(p);

            NestResult r = NestEngine.Solve(job);
            Check("文字句柄场景排样无错误", r.Error == null, r.Error ?? "");
            if (r.Error != null) return;

            var byHandle = new Dictionary<string, Placement>();
            foreach (SheetResult s in r.Sheets)
                foreach (Placement pl in s.Items)
                    if (pl.Handle != null) byHandle[pl.Handle] = pl;

            Check("三件都已落位", byHandle.Count == 3, byHandle.Count + " 件");

            int c1 = byHandle.ContainsKey("A1") ? byHandle["A1"].TextHandles.Count : -1;
            int c2 = byHandle.ContainsKey("A2") ? byHandle["A2"].TextHandles.Count : -1;
            int c3 = byHandle.ContainsKey("A3") ? byHandle["A3"].TextHandles.Count : -1;

            Check("第 1 件带 2 条文字", c1 == 2, "实际 " + c1);
            Check("第 2 件不带文字（空串不产生句柄）", c2 == 0, "实际 " + c2);
            Check("第 3 件带 1 条文字", c3 == 1, "实际 " + c3);
            Check("文字句柄未串位",
                  byHandle.ContainsKey("A3") && byHandle["A3"].TextHandles.Contains("T3"), "");

            VerifyGeometry(job, r, "文字");
        }

        private static PartType FindPart(NestJob job, string id)
        {
            foreach (PartType p in job.Parts) if (p.Id == id) return p;
            return null;
        }

        // ------------------------------------------------------------------
        private static Contour Rect(double x, double y, double w, double h)
        {
            return new Contour(new[]
            {
                new Vec2(x, y), new Vec2(x + w, y),
                new Vec2(x + w, y + h), new Vec2(x, y + h)
            });
        }

        private static Contour LShape(double x, double y)
        {
            return new Contour(new[]
            {
                new Vec2(x, y), new Vec2(x + 300, y), new Vec2(x + 300, y + 100),
                new Vec2(x + 120, y + 100), new Vec2(x + 120, y + 260), new Vec2(x, y + 260)
            });
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                Console.WriteLine("  [PASS] " + name + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
            }
            else
            {
                _failed++;
                Console.WriteLine("  [FAIL] " + name + "  " + detail);
            }
        }
    }
}
