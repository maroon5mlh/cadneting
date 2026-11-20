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

        /// <summary>板框的 XData 应用名：用来标记"这个框是哪一批零件排出来的"</summary>
        private const string SheetAppName = "CADNESTING_SHEET";

        /// <summary>最近一次导出的任务文件，方便紧接着排样</summary>
        private static string _lastJobPath;

        /// <summary>
        /// 上一次用的插入点（WCS，第一张板左下角）。
        /// 默认原点：老图纸都在原点附近作业，首次用不着点也能落回原处；
        /// 之后每次拾取都会记住，换方案重排时回车即可原地覆盖。
        /// </summary>
        private static Point3d _insertOrigin = Point3d.Origin;

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
            job.Sheets.Add(new SheetSpec { Name = "1220x2250", Width = 1220, Height = 2250 });
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

            Point3d origin;
            if (!AskInsertPoint(ed, result, out origin)) return;

            ApplyResult(ed, db, result, origin);
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

            Point3d origin;
            if (!AskInsertPoint(ed, result, out origin))
            {
                ed.WriteMessage("\n结果文件已保存：" + outPath);
                return;
            }

            ApplyResult(ed, db, result, origin);
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
            ed.WriteMessage("\n参数配置文件：" + UI.PanelSettings.FilePath +
                            (System.IO.File.Exists(UI.PanelSettings.FilePath) ? "（已存在）" : "（尚未生成）"));
            if (UI.PanelSettings.LastError != null)
                ed.WriteMessage("\n参数读写异常：" + UI.PanelSettings.LastError);
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

                // 同一批零件按几种策略各排一遍：张数最少的未必最省料，
                // 而"只用 2440 会怎样"这类问题必须能回答，所以给候选而不是替用户决定
                List<NestPlan> plans = NestPlanner.Compare(job);

                if (plans.Count == 0)
                {
                    ed.WriteMessage("\n排样失败：没有生成任何方案。");
                    return;
                }

                ctl.ShowPlans(plans, qty);
                PrintPlanTable(ed, plans);

                NestPlan chosen = null;
                foreach (NestPlan p in plans)
                    if (p.IsRecommended) { chosen = p; break; }
                if (chosen == null)
                    foreach (NestPlan p in plans)
                        if (!p.HasError) { chosen = p; break; }

                if (chosen == null)
                {
                    ed.WriteMessage("\n所有方案都排不下，请检查板材规格或留边设置。");
                    if (plans[0].Result != null && plans[0].Result.Error != null)
                        ed.WriteMessage("\n原因：" + plans[0].Result.Error);
                    return;
                }

                // 先亮方案表再问插入点：用户是看着张数/利用率决定放不放的，
                // 点了 ESC 也不该丢掉已经算好的方案
                ctl.ShowResult(chosen.Result, qty, chosen.Detail);

                Point3d origin;
                if (!AskInsertPoint(ed, chosen.Result, out origin))
                {
                    ed.WriteMessage("\n方案已保留在面板里，稍后可点「应用选中」再摆。");
                    return;
                }

                ApplyResult(ed, db, chosen.Result, origin);

                ed.WriteMessage(string.Format("\n已按「{0}」摆放：{1} 张板，利用率 {2:P2}，耗时 {3} ms",
                    chosen.Name, chosen.Result.Sheets.Count, chosen.Result.TotalUtilization,
                    chosen.Result.ElapsedMs));
                ed.WriteMessage("\n换方案：在面板的「方案对比」里选中另一行 → 点「应用选中」。");

                DumpWarnings(ed, warnings);
                if (chosen.Result.Warnings != null) DumpWarnings(ed, chosen.Result.Warnings);
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

        /// <summary>把候选方案打到命令行：面板窄，张数/用料这些要能一次看全</summary>
        private static void PrintPlanTable(Editor ed, List<NestPlan> plans)
        {
            ed.WriteMessage("\n========== 方案对比 ==========");
            ed.WriteMessage("\n  方案              张数   利用率     用料m²  规格");
            ed.WriteMessage("\n--------------------------------------------------------");

            foreach (NestPlan p in plans)
            {
                if (p.HasError)
                {
                    ed.WriteMessage(string.Format("\n{0} {1,-14}  失败：{2}",
                        p.Id, p.Name, p.Result == null ? "" : p.Result.Error));
                    continue;
                }

                ed.WriteMessage(string.Format("\n{0} {1}{2,-14} {3,4}  {4,7:P1}  {5,9:F2}  {6}{7}",
                    p.Id,
                    p.IsRecommended ? "★" : " ",
                    p.Name,
                    p.SheetCount,
                    p.Utilization,
                    p.SheetArea,
                    p.SpecMix,
                    p.ChangesCraft ? "  ← 改工艺前提，需确认纹向" : ""));
            }

            ed.WriteMessage("\n==============================");
            ed.WriteMessage("\n★ = 推荐（不改工艺前提下 张数最少 → 用料最省）");
        }

        /// <summary>把面板上选中的方案落到图上（不改参数、不重新排样）</summary>
        [CommandMethod("NESTAPPLYPLAN")]
        public static void NestApplyPlan()
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

            NestPlan plan = ctl.SelectedPlan;
            if (plan == null)
            {
                ed.WriteMessage("\n面板里还没有选中方案，请先点「选择图形并对比方案」。");
                return;
            }
            if (plan.HasError)
            {
                ed.WriteMessage("\n这个方案没有排成功：" + (plan.Result == null ? "" : plan.Result.Error));
                return;
            }

            try
            {
                // 换方案时也重新问插入点：板数变了，原来的位置可能已经压到别的东西。
                // 回车沿用上次的点即可原地重排，所以这一步并不打断"连续比几个方案"
                ctl.ShowResult(plan.Result, ctl.PartCount, plan.Detail);

                Point3d origin;
                if (!AskInsertPoint(ed, plan.Result, out origin)) return;

                // 块是按包围盒重新锚定的绝对定位，重复应用不会累积偏移，
                // 所以"选错了换个方案"可以随便点，不需要先撤销
                ApplyResult(ed, db, plan.Result, origin);

                ed.WriteMessage(string.Format("\n已切换到「{0}」：{1} 张板，利用率 {2:P1}，用料 {3:F2} m²",
                    plan.Name, plan.SheetCount, plan.Utilization, plan.SheetArea));
                if (plan.ChangesCraft)
                    ed.WriteMessage("\n注意：该方案放开了 90° 旋转，请确认零件纹向/折弯方向允许。");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n应用方案失败：" + ex.GetType().Name + " - " + ex.Message);
            }
        }

        /// <summary>
        /// 让用户用鼠标点出排样整体的插入点（第一张板左下角）。
        /// 返回 false 表示用户按 ESC 放弃摆放。
        /// </summary>
        private static bool AskInsertPoint(Editor ed, NestResult result, out Point3d origin)
        {
            origin = _insertOrigin;
            if (result == null || result.Sheets == null || result.Sheets.Count == 0) return true;

            ed.WriteMessage(string.Format(
                "\n上次插入点 ({0:0.##}, {1:0.##})，回车沿用；ESC 取消摆放。",
                _insertOrigin.X, _insertOrigin.Y));

            SheetResult first = result.Sheets[0];
            int sheetCount = result.Sheets.Count;

            try
            {
                var jig = new InsertPointJig(first.Width, first.Height, _insertOrigin,
                                             ed.CurrentUserCoordinateSystem);
                PromptResult pr = ed.Drag(jig);

                if (pr.Status == PromptStatus.OK)
                {
                    _insertOrigin = jig.Position;      // 点了新位置
                }
                else if (pr.Status == PromptStatus.Cancel && !jig.KeepPrevious)
                {
                    ed.WriteMessage("\n已取消摆放。");
                    return false;                      // ESC
                }
                // 其余（Cancel + KeepPrevious = 空回车）→ 沿用上次的点
                origin = _insertOrigin;
            }
            catch (System.Exception ex)
            {
                // jig 在极端情况下（视口被别的窗口抢占、图纸正在重生成）会抛，
                // 退回普通取点，功能不能因此完全不可用
                ed.WriteMessage("\n预览拾取不可用（" + ex.Message + "），改用普通取点。");
                var ppo = new PromptPointOptions("\n指定插入点（第一张板的左下角）") { AllowNone = true };
                PromptPointResult r = ed.GetPoint(ppo);
                if (r.Status == PromptStatus.Cancel)
                {
                    ed.WriteMessage("\n已取消摆放。");
                    return false;
                }
                if (r.Status == PromptStatus.OK)
                {
                    Point3d p = r.Value;
                    // GetPoint 同样返回 UCS 点，板框是按 WCS 画的，先转换
                    try { p = p.TransformBy(ed.CurrentUserCoordinateSystem); } catch { }
                    _insertOrigin = new Point3d(p.X, p.Y, 0);
                }
                origin = _insertOrigin;
            }

            ed.WriteMessage(string.Format(
                "\n插入点：({0:0.##}, {1:0.##})，板框向右排开，共 {2} 张板。",
                origin.X, origin.Y, sheetCount));
            return true;
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

            ed.WriteMessage("\n\n板号  规格           件数   利用率   条料");
            ed.WriteMessage("\n-------------------------------------------");
            foreach (SheetResult s in result.Sheets)
            {
                // 条料 = 右侧贯穿全高的余料宽度。末板会被特意压实成这个形状，
                // 打出来才知道"剩下这条料到底有多宽、能不能留着用"
                string strip = s.RightStripWidth >= 0.5
                               ? s.RightStripWidth.ToString("F0") + "mm"
                               : "-";
                ed.WriteMessage(string.Format("\n{0,3}   {1,-13} {2,5}   {3,7:P1}   {4,6}",
                    s.Index + 1, s.SheetName, s.Items.Count, s.Utilization, strip));
            }

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                ed.WriteMessage("\n");
                foreach (string w in result.Warnings) ed.WriteMessage("\n注意：" + w);
            }
            ed.WriteMessage("\n==============================");
        }

        /// <summary>把排样结果写到模型空间：画板框 + 摆放块 + 带动跟随文字</summary>
        /// <param name="origin">插入点：第一张板的左下角（WCS）</param>
        private static void ApplyResult(Editor ed, Database db, NestResult result, Point3d origin)
        {
            int moved = 0, missing = 0, failed = 0;
            int textMoved = 0, textMissing = 0, textFailed = 0;
            int clearedBorders = 0;
            string lastError = null;
            var insert = new Vec2(origin.X, origin.Y);
            int sheetIndex = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // 板框图层按需创建：直接 pl.Layer = "NEST_SHEET" 在图层不存在时
                // 会抛 eKeyNotFound，而且会让整个事务回滚，块一件都不会动
                ObjectId sheetLayerId = EnsureLayer(tr, db, SheetLayer);

                // 本批的指纹（零件句柄集合），写进板框的 XData。
                // 同一批换方案/重排时指纹不变，框才能被认出并刷新；
                // 换成另一批零件时指纹不同，上一批的框就绝不会被碰
                string signature = BatchSignature(result);
                bool canTag = EnsureRegApp(tr, db);

                // 换方案时会重画板框。不清掉旧的，图上就会一层层叠出十几个板框，
                // 看起来像是"方案没换成功"，实际是框叠在一起了。
                // 但绝不能整层清空：用户是分批排的，第二批一来就把第一批的框删掉，
                // 上一批的料会只剩零件、没有板框（真实报障）。
                int cleared = ClearSheetBorders(tr, sheetLayerId, db, result, signature);
                clearedBorders = cleared;

                foreach (SheetResult sheet in result.Sheets)
                {
                    // 插入点在这里生效：板框与零件的坐标都从同一个板原点算出来，
                    // 不会出现"框搬过去了、零件还在老地方"这种半截偏移
                    Vec2 sheetOrigin = SheetLayout.SheetOrigin(result, sheetIndex, insert);

                    DrawSheetBorder(ms, tr, sheetLayerId, sheetOrigin.X, sheetOrigin.Y,
                                    sheet.Width, sheet.Height, canTag ? signature : null);

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
                            ApplyPlacement(br, companions,
                                           sheetOrigin.X + pl.X, sheetOrigin.Y + pl.Y, pl.RotationDeg,
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

                    sheetIndex++;
                }

                tr.Commit();
            }

            ed.WriteMessage(string.Format("\n已摆放 {0} 件，未找到 {1} 件，失败 {2} 件。", moved, missing, failed));
            if (clearedBorders > 0)
                ed.WriteMessage(string.Format("\n已清除上一版板框 {0} 个。", clearedBorders));
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
                                            double x, double y, double w, double h, string signature)
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

            if (!string.IsNullOrEmpty(signature)) TagSheet(pl, signature);
        }

        /// <summary>
        /// 本批零件的指纹：把件句柄排序后做 FNV-1a。
        ///
        /// 用句柄而不是块名/数量：同一品种的两批料块名完全一样，
        /// 只有句柄能区分"还是这批"和"又来一批"。
        /// 不依赖块当前位置，所以零件被挪动过、重排时照样认得出。
        /// </summary>
        private static string BatchSignature(NestResult result)
        {
            var handles = new List<string>();
            if (result != null && result.Sheets != null)
            {
                foreach (SheetResult s in result.Sheets)
                {
                    if (s.Items == null) continue;
                    foreach (Placement p in s.Items)
                        if (!string.IsNullOrEmpty(p.Handle)) handles.Add(p.Handle);
                }
            }
            handles.Sort(StringComparer.OrdinalIgnoreCase);

            ulong h = 14695981039346656037UL;
            foreach (string s in handles)
                foreach (char c in s) { h ^= c; h *= 1099511628211UL; }

            return handles.Count.ToString("X") + "-" + h.ToString("X16");
        }

        /// <summary>注册 XData 应用名；失败返回 false（图纸只读等），框就不带批次标记</summary>
        private static bool EnsureRegApp(Transaction tr, Database db)
        {
            try
            {
                RegAppTable rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                if (rat.Has(SheetAppName)) return true;

                rat.UpgradeOpen();
                var rec = new RegAppTableRecord { Name = SheetAppName };
                rat.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TagSheet(Entity e, string signature)
        {
            try
            {
                e.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, SheetAppName),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, signature));
            }
            catch { /* 写不进标记不影响画框，只是下次只能靠几何判断归属 */ }
        }

        /// <summary>读板框上的批次标记；没有/不是本插件的返回 null</summary>
        private static string ReadSheetTag(Entity e)
        {
            try
            {
                using (ResultBuffer rb = e.XData)
                {
                    if (rb == null) return null;

                    TypedValue[] vals = rb.AsArray();
                    if (vals.Length < 2) return null;
                    if (vals[0].TypeCode != (int)DxfCode.ExtendedDataRegAppName) return null;
                    if (!string.Equals(Convert.ToString(vals[0].Value), SheetAppName,
                                       StringComparison.Ordinal)) return null;

                    return Convert.ToString(vals[1].Value);
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 清掉本插件上一版画的板框 —— 但只清"属于这批零件"的那些。
        ///
        /// 两级判据：
        ///   1. 框上带批次标记（XData）→ 标记等于本批指纹才清。
        ///      这条不受块当前位置影响，所以"换了方案""同一批重排"都认得出，
        ///      而"另一批零件恰好堆在这个框里"也不会误删。
        ///   2. 没有标记（旧版本插件画的）→ 退化成几何判断：框里装着本批的块就清。
        ///
        /// 为什么不能整层清空：用户是分批排的，第二批一来就把第一批的框删掉，
        /// 上一批的料会只剩零件没有板框（真实报障）。板框长得都一样、图层也一样，
        /// 能区分批次的只有"框上记的批次"和"框里装的是谁"。
        /// </summary>
        private static int ClearSheetBorders(Transaction tr, ObjectId layerId, Database db,
                                             NestResult result, string signature)
        {
            if (result == null || result.Sheets == null) return 0;

            // 本批零件在图纸上的当前位置（包围盒中心），移动之前取。
            // 只用于没有标记的旧框，新框一律走批次标记
            var centers = new List<Point2d>();
            foreach (SheetResult s in result.Sheets)
            {
                if (s.Items == null) continue;

                foreach (Placement p in s.Items)
                {
                    ObjectId bid = GetIdByHandle(db, p.Handle);
                    if (bid.IsNull) continue;

                    try
                    {
                        var br = tr.GetObject(bid, OpenMode.ForRead, false) as BlockReference;
                        if (br == null) continue;

                        Extents3d e = br.GeometricExtents;
                        centers.Add(new Point2d((e.MinPoint.X + e.MaxPoint.X) * 0.5,
                                                (e.MinPoint.Y + e.MaxPoint.Y) * 0.5));
                    }
                    catch { /* 取不到范围的件不参与归属判断，不影响别的 */ }
                }
            }

            Database dbase = layerId.Database ?? db;
            if (dbase == null) return 0;

            var doomed = new List<ObjectId>();

            BlockTable bt = (BlockTable)tr.GetObject(dbase.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var pl = tr.GetObject(id, OpenMode.ForRead, false) as Autodesk.AutoCAD.DatabaseServices.Polyline;
                if (pl == null) continue;
                if (pl.LayerId != layerId) continue;
                if (!pl.Closed || pl.NumberOfVertices != 4) continue;

                string tag = ReadSheetTag(pl);

                bool mine;
                if (!string.IsNullOrEmpty(tag))
                    mine = string.Equals(tag, signature, StringComparison.Ordinal);
                else
                    mine = centers.Count > 0 && HoldsAnyPoint(pl, centers);

                if (!mine) continue;   // 别的批次的框：留着
                doomed.Add(id);
            }

            int n = 0;
            foreach (ObjectId id in doomed)
            {
                try
                {
                    var e = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (e == null) continue;
                    e.Erase();
                    n++;
                }
                catch { /* 单个删不掉不影响整批 */ }
            }
            return n;
        }

        /// <summary>这个矩形框里有没有落进给定的任一中心点</summary>
        private static bool HoldsAnyPoint(Autodesk.AutoCAD.DatabaseServices.Polyline box,
                                          List<Point2d> points)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            for (int i = 0; i < box.NumberOfVertices; i++)
            {
                Point2d v = box.GetPoint2dAt(i);
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
            }

            const double tol = 1e-6;
            for (int i = 0; i < points.Count; i++)
            {
                Point2d p = points[i];
                if (p.X >= minX - tol && p.X <= maxX + tol &&
                    p.Y >= minY - tol && p.Y <= maxY + tol) return true;
            }
            return false;
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
