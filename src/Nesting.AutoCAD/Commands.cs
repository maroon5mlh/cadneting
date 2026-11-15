using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System.Text.Json;
using System.Text.Encodings.Web;
using Nesting.AutoCAD.Extraction;
using Nesting.AutoCAD.UI;
using Nesting.Core;
using Nesting.Core.Solvers;

namespace Nesting.AutoCAD
{
    /// <summary>
    /// 插件入口命令。
    /// NESTEXTRACT : 选块 → 提取轮廓 → 归并计数 → 导出排样任务 JSON
    /// NESTAPPLY   : 读排样结果 JSON → 按板分组摆放块 + 画板材框
    /// </summary>
    public class Commands
    {
        private const double FlattenTolerance = 0.1;   // mm
        private const string SheetLayer = "NEST_SHEET";

        /// <summary>最近一次导出的任务文件，方便紧接着排样</summary>
        private static string _lastJobPath;

        /// <summary>
        /// 模型用的是 public 字段而非属性，必须开 IncludeFields。
        /// UnsafeRelaxedJsonEscaping 保证中文块名不被转义成 \uXXXX。
        /// </summary>
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // ------------------------------------------------------------------
        // 提取
        // ------------------------------------------------------------------
        [CommandMethod("NESTEXTRACT")]
        public static void NestExtract()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "INSERT")
            });

            PromptSelectionResult psr = ed.GetSelection(filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n未选择任何块参照。");
                return;
            }

            var warnings = new List<string>();
            int blockCount, textLinked;
            List<PartType> parts = ExtractAndGroup(ed, db, psr.Value, warnings, true,
                                                   out blockCount, out textLinked);

            if (parts == null)
            {
                DumpWarnings(ed, warnings);
                return;
            }

            var job = new NestJob
            {
                BatchId = "B" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                Unit = "mm"
            };
            job.Sheets.Add(new SheetSpec { Name = "1220x2440", Width = 1220, Height = 2440 });
            job.Sheets.Add(new SheetSpec { Name = "1220x3050", Width = 1220, Height = 3050 });
            job.Parts = parts;

            string path = AskSavePath(ed, job.BatchId);
            if (path == null) return;

            File.WriteAllText(path, JsonSerializer.Serialize(job, JsonOpts),
                              new UTF8Encoding(false));
            _lastJobPath = path;

            PrintSummary(ed, blockCount, parts, path, textLinked);
            DumpWarnings(ed, warnings);
        }

        /// <summary>
        /// 选中的块参照 → 提取轮廓 → 归并为品种清单。
        /// 提取不到任何闭合轮廓时返回 null，警告写进 warnings 供调用方输出。
        /// attachText 为 true 时顺带把块旁的标注文字关联进来（排样时一起移动）。
        /// </summary>
        private static List<PartType> ExtractAndGroup(Editor ed, Database db, SelectionSet ss,
                                                     List<string> warnings, bool attachText,
                                                     out int blockCount, out int textLinked)
        {
            blockCount = 0;
            textLinked = 0;
            var raws = new List<RawPart>();
            var targets = new List<TextCompanionFinder.Target>();

            int sizeMismatch = 0;
            double maxGrow = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in ss)
                {
                    if (so == null) continue;
                    var br = tr.GetObject(so.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;

                    blockCount++;
                    try
                    {
                        RawPart rp = BlockOutlineExtractor.Extract(br, tr, FlattenTolerance, warnings);
                        if (rp == null) continue;

                        raws.Add(rp);
                        if (attachText) targets.Add(BuildTextTarget(br, rp));

                        // 排样按轮廓算、回写按包围盒锚定。两者不一致（块里还有轮廓之外的
                        // 图元：引线、标注线、杂散线）时，块实际占位会大于排样占位，
                        // 表现为"算法算得对，图纸上却压在一起"。
                        double grow = ExtentGrowth(br, rp);
                        if (grow > 1.0)
                        {
                            sizeMismatch++;
                            if (grow > maxGrow) maxGrow = grow;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        warnings.Add("提取失败 句柄=" + br.Handle + " : " + ex.Message);
                    }
                }

                if (attachText && targets.Count > 0)
                {
                    int orphan;
                    textLinked = TextCompanionFinder.Attach(targets, tr, 1.0, warnings, out orphan);
                }

                tr.Commit();
            }

            if (sizeMismatch > 0)
            {
                warnings.Add(string.Format(
                    "有 {0} 个块的几何范围比零件轮廓大（最多 {1:0.#} mm），块内可能含轮廓以外的图元；" +
                    "排样按轮廓计算、回写按包围盒定位，这类块实际占位会略大于排样占位",
                    sizeMismatch, maxGrow));
            }

            if (raws.Count == 0)
            {
                ed.WriteMessage("\n没有可用的闭合轮廓，请确认选中的块内部是由闭合多段线/圆构成的。");
                return null;
            }

            List<string> groupWarnings;
            List<PartType> parts = PartGrouper.Group(raws, out groupWarnings);
            warnings.AddRange(groupWarnings);
            return parts;
        }

        /// <summary>块包围盒比零件轮廓大多少（mm），取宽/高差值的较大者</summary>
        private static double ExtentGrowth(BlockReference br, RawPart part)
        {
            try
            {
                Extents3d e = br.GeometricExtents;
                Vec2 min, max;
                part.Outer.GetBounds(out min, out max);

                double dw = (e.MaxPoint.X - e.MinPoint.X) - (max.X - min.X);
                double dh = (e.MaxPoint.Y - e.MinPoint.Y) - (max.Y - min.Y);
                return Math.Max(dw, dh);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 构造文字关联用的目标范围。
        /// 包围盒优先取块的实际范围（GeometricExtents）而不是零件轮廓范围 ——
        /// XK 用块范围中心放文字，两个中心不一致时（块里有杂散图元）
        /// 用轮廓范围会把文字判到框外，表现是"有的件跟、有的件不跟"。
        /// </summary>
        private static TextCompanionFinder.Target BuildTextTarget(BlockReference br, RawPart part)
        {
            Vec2 min, max;
            bool ok = false;

            try
            {
                Extents3d ext = br.GeometricExtents;
                min = new Vec2(ext.MinPoint.X, ext.MinPoint.Y);
                max = new Vec2(ext.MaxPoint.X, ext.MaxPoint.Y);
                ok = max.X > min.X && max.Y > min.Y;
            }
            catch
            {
                min = new Vec2(0, 0);
                max = new Vec2(0, 0);
            }

            if (!ok) part.Outer.GetBounds(out min, out max);

            return new TextCompanionFinder.Target
            {
                BlockId = br.ObjectId,
                SpaceId = br.OwnerId,
                Part = part,
                Min = min,
                Max = max,
                Center = new Vec2((min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5)
            };
        }

        private static void PrintSummary(Editor ed, int blockCount, List<PartType> parts,
                                         string path, int textLinked)
        {
            int totalQty = 0, withText = 0;
            double totalArea = 0;
            foreach (PartType p in parts)
            {
                totalQty += p.Quantity;
                totalArea += p.NetArea * p.Quantity;
                if (p.TextHandles != null)
                {
                    foreach (string s in p.TextHandles) if (!string.IsNullOrEmpty(s)) withText++;
                }
            }

            ed.WriteMessage("\n========== 提取结果 ==========");
            ed.WriteMessage(string.Format("\n块参照 {0} 个 → 归并为 {1} 个品种，共 {2} 件", blockCount, parts.Count, totalQty));
            ed.WriteMessage(string.Format("\n零件总面积 {0:N0} mm² ({1:N3} m²)", totalArea, totalArea / 1e6));
            if (textLinked > 0 || withText > 0)
                ed.WriteMessage(string.Format("\n跟随文字：关联 {0} 条，覆盖 {1} 件（排样时随块一起移动）",
                    textLinked, withText));

            ed.WriteMessage("\n品种  块名                尺寸(mm)          数量  可旋90  文字");
            ed.WriteMessage("\n---------------------------------------------------------------------");

            foreach (PartType p in parts)
            {
                bool forced = RotationPolicy.IsForcedRotate(p.BlockName, null);
                string rot = !p.AllowRotate90 ? "否" : (forced ? "强制" : "是");

                int tcount = 0;
                if (p.TextHandles != null)
                    foreach (string s in p.TextHandles) if (!string.IsNullOrEmpty(s)) tcount++;

                ed.WriteMessage(string.Format("\n{0,-5} {1,-18} {2,7:0.#} x {3,-7:0.#} {4,5}   {5,-5} {6,4}",
                    p.Id,
                    Truncate(p.BlockName, 18),
                    p.Width, p.Height,
                    p.Quantity,
                    rot,
                    tcount));
            }

            if (parts.Count > 0)
            {
                ed.WriteMessage("\n\n「强制」= 块名命中旋转关键字，即使关掉全局旋转也会转 90°：");
                ed.WriteMessage("\n  " + string.Join("、", RotationPolicy.DefaultKeywords));
            }

            // 参考：一张 1220x2440 的理论张数
            double sheetArea = 1220 * 2440;
            ed.WriteMessage(string.Format("\n\n理论最少板材张数（按面积，实际会更多）：{0:N2} 张 1220x2440",
                totalArea / sheetArea));
            ed.WriteMessage("\n已导出排样任务：" + path);
            ed.WriteMessage("\n==============================");
        }

        // ------------------------------------------------------------------
        // 应用结果
        // ------------------------------------------------------------------
        [CommandMethod("NESTAPPLY")]
        public static void NestApply()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            string defaultPath = _lastJobPath == null
                ? Path.Combine(Path.GetTempPath(), "nestresult.json")
                : Path.ChangeExtension(_lastJobPath, ".result.json");

            var pso = new PromptStringOptions("\n排样结果 JSON 路径")
            {
                DefaultValue = defaultPath,
                AllowSpaces = true,
                UseDefaultValue = true
            };
            PromptResult pr = ed.GetString(pso);
            if (pr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pr.StringResult)) return;

            string path = pr.StringResult.Trim().Trim('"');
            if (!File.Exists(path))
            {
                ed.WriteMessage("\n文件不存在：" + path);
                return;
            }

            NestResult result;
            try
            {
                result = JsonSerializer.Deserialize<NestResult>(File.ReadAllText(path), JsonOpts);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n解析失败：" + ex.Message);
                return;
            }

            if (result == null || result.Sheets == null || result.Sheets.Count == 0)
            {
                ed.WriteMessage("\n结果为空。" + (result != null && result.Error != null ? result.Error : ""));
                return;
            }

            ApplyResult(ed, db, result);
            PrintResult(ed, result);
        }

        // ------------------------------------------------------------------
        // 一键排样：读任务 → 引擎求解 → 写结果 → 摆放
        // ------------------------------------------------------------------
        [CommandMethod("NESTRUN")]
        public static void NestRun()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            string defaultPath = _lastJobPath;
            if (defaultPath == null)
            {
                ed.WriteMessage("\n请先运行 NESTEXTRACT 生成任务文件。");
                return;
            }

            var pso = new PromptStringOptions("\n排样任务 JSON 路径")
            {
                DefaultValue = defaultPath,
                AllowSpaces = true,
                UseDefaultValue = true
            };
            PromptResult pr = ed.GetString(pso);
            if (pr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pr.StringResult)) return;

            string path = pr.StringResult.Trim().Trim('"');
            if (!File.Exists(path))
            {
                ed.WriteMessage("\n文件不存在：" + path);
                return;
            }

            NestJob job;
            try
            {
                job = JsonSerializer.Deserialize<NestJob>(File.ReadAllText(path), JsonOpts);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n任务文件解析失败：" + ex.Message);
                return;
            }

            if (job == null || job.Parts == null || job.Parts.Count == 0)
            {
                ed.WriteMessage("\n任务文件里没有零件。");
                return;
            }

            ed.WriteMessage("\n排样中…");
            NestResult result = NestEngine.Solve(job);

            if (result.Error != null)
            {
                ed.WriteMessage("\n排样失败：" + result.Error);
                return;
            }

            // 结果文件与任务文件放一起，便于复现和排查
            string outPath = Path.ChangeExtension(path, ".result.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(result, JsonOpts),
                              new UTF8Encoding(false));

            ApplyResult(ed, db, result);
            PrintResult(ed, result);

            ed.WriteMessage("\n结果文件：" + outPath);
            ed.WriteMessage("\n提示：块位置已被修改，如不满意可撤销（U）。");
        }

        // ------------------------------------------------------------------
        // 面板
        // ------------------------------------------------------------------
        [CommandMethod("NESTPANEL")]
        public static void ShowPanel()
        {
            NestPalette.Show();
        }

        /// <summary>自检：确认加载的是哪个 dll、依赖是否齐全、面板是否创建成功</summary>
        [CommandMethod("NESTDIAG")]
        public static void NestDiag()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var asm = typeof(Commands).Assembly;

            ed.WriteMessage("\n########## 排样插件诊断 ##########");
            ed.WriteMessage("\n程序集路径：" + asm.Location);
            ed.WriteMessage("\n程序集版本：" + asm.GetName().Version);
            ed.WriteMessage("\n.NET 运行时：" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            ed.WriteMessage("\nAutoCAD 版本：" + Application.Version);

            string dir = Path.GetDirectoryName(asm.Location);
            bool core = dir != null && File.Exists(Path.Combine(dir, "Nesting.Core.dll"));
            ed.WriteMessage("\n同目录 Nesting.Core.dll：" + (core ? "存在" : "缺失（必须与主 dll 放在同一目录）"));

            ed.WriteMessage("\n已加载的 Nesting 程序集：");
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n == null || !n.StartsWith("Nesting", StringComparison.Ordinal)) continue;
                ed.WriteMessage("\n  " + n + " " + a.GetName().Version + "  ← " +
                                (string.IsNullOrEmpty(a.Location) ? "(内存)" : a.Location));
            }

            ed.WriteMessage("\n面板实例：" + (NestPalette.Control == null ? "未创建" : "已创建"));
            ed.WriteMessage("\n##################################");
        }

        /// <summary>
        /// 面板按钮真正执行的命令：选块 → 提取 → 排样 → 摆放 → 回填面板。
        ///
        /// 为什么绕一层命令：面板是模态外的，从面板控件的事件里直接调用
        /// Editor / Database 会抛 eInvalidContext 或随机崩溃。
        /// SendStringToExecute 把执行权交回文档上下文，这是唯一稳的做法。
        /// </summary>
        [CommandMethod("NESTAUTO")]
        public static void NestAuto()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            NestPaletteControl ctl = NestPalette.Control;
            if (ctl == null)
            {
                ed.WriteMessage("\n请先运行 NESTPANEL 打开排样面板。");
                return;
            }

            try
            {
                List<SheetSpec> sheets = ctl.ReadSheets();
                if (sheets.Count == 0)
                {
                    ed.WriteMessage("\n面板里没有勾选任何板材规格。");
                    return;
                }

                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
                PromptSelectionResult psr = ed.GetSelection(filter);
                if (psr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n未选择任何块参照。");
                    return;
                }

                var warnings = new List<string>();
                int blockCount, textLinked;
                bool followText = ctl.FollowText.Checked;

                List<PartType> parts = ExtractAndGroup(ed, db, psr.Value, warnings, followText,
                                                       out blockCount, out textLinked);
                if (parts == null)
                {
                    DumpWarnings(ed, warnings);
                    return;
                }

                int qty = 0;
                foreach (PartType p in parts) qty += p.Quantity;

                var job = new NestJob
                {
                    BatchId = "B" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                    Unit = "mm",
                    Sheets = sheets,
                    Parts = parts,
                    Settings = ctl.ReadSettings()
                };

                ed.WriteMessage(string.Format(
                    "\n{0} 个块 → {1} 个品种，共 {2} 件；间距 {3} mm，留边 {4} mm，跟随文字 {5} 条",
                    blockCount, parts.Count, qty,
                    job.Settings.SpacingMm, job.Settings.MarginMm,
                    followText ? textLinked : 0));

                NestResult result = NestEngine.Solve(job);

                if (result.Error != null)
                {
                    ed.WriteMessage("\n排样失败：" + result.Error);
                    ctl.ShowResult(result, qty);
                    return;
                }

                ApplyResult(ed, db, result);
                ed.WriteMessage(string.Format("\n完成：{0} 张板，利用率 {1:P2}，耗时 {2} ms",
                    result.Sheets.Count, result.TotalUtilization, result.ElapsedMs));

                ctl.ShowResult(result, qty);
                DumpWarnings(ed, warnings);
                if (result.Warnings != null) DumpWarnings(ed, result.Warnings);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n排样中断：" + ex.GetType().Name + " - " + ex.Message);
                ed.WriteMessage("\n已回滚，图纸未被修改。");
            }
            finally
            {
                ctl.SetBusy(false);
            }
        }

        private static void PrintResult(Editor ed, NestResult result)
        {
            int total = 0, rotated = 0;
            foreach (SheetResult s in result.Sheets)
            {
                total += s.Items.Count;
                foreach (Placement p in s.Items) if (Math.Abs(p.RotationDeg) > 1e-6) rotated++;
            }

            ed.WriteMessage("\n\n========== 排样结果 ==========");
            ed.WriteMessage(string.Format("\n共 {0} 张板，{1} 件零件，其中 {2} 件旋转了 90°",
                result.Sheets.Count, total, rotated));
            ed.WriteMessage(string.Format("\n总利用率 {0:P2}，耗时 {1} ms（引擎 {2}）",
                result.TotalUtilization, result.ElapsedMs, result.EngineVersion));

            ed.WriteMessage("\n\n板号  规格           件数   利用率");
            ed.WriteMessage("\n-------------------------------------");
            foreach (SheetResult s in result.Sheets)
            {
                ed.WriteMessage(string.Format("\n{0,3}   {1,-13} {2,5}   {3,7:P1}",
                    s.Index + 1, s.SheetName, s.Items.Count, s.Utilization));
            }

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                ed.WriteMessage("\n");
                foreach (string w in result.Warnings) ed.WriteMessage("\n注意：" + w);
            }
            ed.WriteMessage("\n==============================");
        }

        /// <summary>把排样结果写到模型空间：画板框 + 摆放块 + 带动跟随文字</summary>
        private static void ApplyResult(Editor ed, Database db, NestResult result)
        {
            int moved = 0, missing = 0, failed = 0;
            int textMoved = 0, textMissing = 0, textFailed = 0;
            string lastError = null;
            double originX = 0;
            const double gapBetweenSheets = 300;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // 板框图层按需创建：直接 pl.Layer = "NEST_SHEET" 在图层不存在时
                // 会抛 eKeyNotFound，而且会让整个事务回滚，块一件都不会动
                ObjectId sheetLayerId = EnsureLayer(tr, db, SheetLayer);

                foreach (SheetResult sheet in result.Sheets)
                {
                    DrawSheetBorder(ms, tr, sheetLayerId, originX, 0, sheet.Width, sheet.Height);

                    foreach (Placement pl in sheet.Items)
                    {
                        ObjectId id = GetIdByHandle(db, pl.Handle);
                        if (id.IsNull) { missing++; continue; }

                        // 单个块失败（图层被锁、块被约束等）不能拖垮整批
                        try
                        {
                            var br = tr.GetObject(id, OpenMode.ForWrite) as BlockReference;
                            if (br == null) { missing++; continue; }

                            var companions = CollectTexts(tr, db, pl, ref textMissing);

                            int ok, bad;
                            ApplyPlacement(br, companions, originX + pl.X, pl.Y, pl.RotationDeg,
                                           out ok, out bad);

                            moved++;
                            textMoved += ok;
                            textFailed += bad;
                        }
                        catch (System.Exception ex)
                        {
                            failed++;
                            lastError = pl.Handle + " : " + ex.Message;
                        }
                    }

                    originX += sheet.Width + gapBetweenSheets;
                }

                tr.Commit();
            }

            ed.WriteMessage(string.Format("\n已摆放 {0} 件，未找到 {1} 件，失败 {2} 件。", moved, missing, failed));
            if (textMoved + textFailed + textMissing > 0)
            {
                ed.WriteMessage(string.Format("\n跟随文字：移动 {0} 条，失败 {1} 条，找不到 {2} 条。",
                    textMoved, textFailed, textMissing));
            }
            if (failed > 0 && lastError != null)
            {
                ed.WriteMessage("\n  首个失败原因：" + lastError);
                ed.WriteMessage("\n  提示：若提示图层已锁定，请先解锁块所在图层再重试。");
            }
        }

        /// <summary>按句柄取出这一件的跟随文字，逐个以写权限打开</summary>
        private static List<Entity> CollectTexts(Transaction tr, Database db, Placement pl,
                                                 ref int notFound)
        {
            var list = new List<Entity>();
            if (pl.TextHandles == null) return list;

            foreach (string hex in pl.TextHandles)
            {
                ObjectId tid = GetIdByHandle(db, hex);
                if (tid.IsNull) { notFound++; continue; }

                try
                {
                    var ent = tr.GetObject(tid, OpenMode.ForWrite) as Entity;
                    if (ent == null) { notFound++; continue; }
                    list.Add(ent);
                }
                catch
                {
                    notFound++;
                }
            }
            return list;
        }

        /// <summary>
        /// 取图层 Id，不存在则创建。
        /// 必须在事务内调用，且拿写权限前先升级 LayerTable。
        /// </summary>
        private static ObjectId EnsureLayer(Transaction tr, Database db, string name)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];

            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = name };
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        /// <summary>
        /// 把块摆到目标位置，并让跟随文字一起走。
        ///
        /// 两个关键点：
        /// 1. 全程用包围盒锚定 + 矩阵变换，不依赖块基点（基点可能在任意位置）；
        /// 2. 旋转后新的左下角由「四角点变换后取 min」算出，而不是把宽高互换 ——
        ///    互换只对 0/90 成立，一旦将来放开任意角度就会静默错位。
        /// 文字用同一个矩阵变换，相对位置和朝向自动保持，不需要单独算偏移。
        /// </summary>
        private static void ApplyPlacement(BlockReference br, List<Entity> companions,
                                           double targetX, double targetY, double rotationDeg,
                                           out int textOk, out int textFailed)
        {
            int ok = 0, bad = 0;

            Extents3d ext = br.GeometricExtents;
            var center = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * 0.5,
                                     (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5, 0);

            Point3d[] corners = CornersOf(ext);

            if (Math.Abs(rotationDeg % 360.0) > 1e-6)
            {
                Matrix3d rot = Matrix3d.Rotation(rotationDeg * Math.PI / 180.0, Vector3d.ZAxis, center);
                br.TransformBy(rot);
                TransformEntities(companions, rot, ref ok, ref bad);

                for (int i = 0; i < corners.Length; i++) corners[i] = corners[i].TransformBy(rot);
            }

            double newMinX = double.MaxValue, newMinY = double.MaxValue;
            for (int i = 0; i < corners.Length; i++)
            {
                if (corners[i].X < newMinX) newMinX = corners[i].X;
                if (corners[i].Y < newMinY) newMinY = corners[i].Y;
            }

            Matrix3d disp = Matrix3d.Displacement(
                new Vector3d(targetX - newMinX, targetY - newMinY, 0));

            br.TransformBy(disp);
            TransformEntities(companions, disp, ref ok, ref bad);

            textOk = ok;
            textFailed = bad;
        }

        private static Point3d[] CornersOf(Extents3d e)
        {
            return new[]
            {
                new Point3d(e.MinPoint.X, e.MinPoint.Y, 0),
                new Point3d(e.MaxPoint.X, e.MinPoint.Y, 0),
                new Point3d(e.MaxPoint.X, e.MaxPoint.Y, 0),
                new Point3d(e.MinPoint.X, e.MaxPoint.Y, 0)
            };
        }

        private static void TransformEntities(List<Entity> list, Matrix3d m, ref int ok, ref int failed)
        {
            if (list == null) return;

            foreach (Entity e in list)
            {
                try
                {
                    e.TransformBy(m);
                    KeepTextUpright(e);
                    ok++;
                }
                catch
                {
                    // 文字在锁定图层上：跳过，但不能连累块和其它文字
                    failed++;
                }
            }
        }

        /// <summary>
        /// 文字跟着零件转完后，把倒置的角纠正回来。
        /// 块转 90° 时原本 90° 的文字会变成 180°，打印出来是倒字。
        /// 居中锚定的文字改自身角度不会跑位，所以只需在 (90°, 270°) 区间减 180°。
        /// </summary>
        private static void KeepTextUpright(Entity e)
        {
            var txt = e as DBText;
            if (txt != null)
            {
                double r = KeepUpright(txt.Rotation);
                if (Math.Abs(r - txt.Rotation) > 1e-9) txt.Rotation = r;
                return;
            }

            var mtx = e as MText;
            if (mtx != null)
            {
                double r = KeepUpright(mtx.Rotation);
                if (Math.Abs(r - mtx.Rotation) > 1e-9) mtx.Rotation = r;
            }
        }

        private static double KeepUpright(double rad)
        {
            const double twoPi = Math.PI * 2.0;
            rad = rad % twoPi;
            if (rad < 0) rad += twoPi;
            if (rad > Math.PI * 0.5 + 1e-6 && rad < Math.PI * 1.5 - 1e-6) rad -= Math.PI;
            return rad;
        }

        private static void DrawSheetBorder(BlockTableRecord ms, Transaction tr, ObjectId layerId,
                                            double x, double y, double w, double h)
        {
            var pl = new Autodesk.AutoCAD.DatabaseServices.Polyline(4);
            pl.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(x + w, y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(x + w, y + h), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(x, y + h), 0, 0, 0);
            pl.Closed = true;
            pl.LayerId = layerId;
            pl.ColorIndex = 8;

            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        private static ObjectId GetIdByHandle(Database db, string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return ObjectId.Null;
            try
            {
                var h = new Handle(Convert.ToInt64(hex, 16));
                ObjectId id;
                return db.TryGetObjectId(h, out id) ? id : ObjectId.Null;
            }
            catch { return ObjectId.Null; }
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------
        private static string AskSavePath(Editor ed, string batchId)
        {
            string def = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "nestjob_" + batchId + ".json");

            var pso = new PromptStringOptions("\n任务文件保存路径")
            {
                DefaultValue = def,
                AllowSpaces = true,
                UseDefaultValue = true
            };
            PromptResult pr = ed.GetString(pso);
            if (pr.Status != PromptStatus.OK) return null;

            string path = pr.StringResult.Trim().Trim('"');
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return path;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n路径无效：" + ex.Message);
                return null;
            }
        }

        private static void DumpWarnings(Editor ed, List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0) return;

            ed.WriteMessage("\n\n---------- 警告 " + warnings.Count + " 条 ----------");
            int limit = Math.Min(warnings.Count, 15);
            for (int i = 0; i < limit; i++) ed.WriteMessage("\n  · " + warnings[i]);
            if (warnings.Count > limit)
                ed.WriteMessage("\n  ... 其余 " + (warnings.Count - limit) + " 条已略过");
        }

        private static string Truncate(string s, int len)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= len ? s : s.Substring(0, len - 1) + "…";
        }
    }
}
