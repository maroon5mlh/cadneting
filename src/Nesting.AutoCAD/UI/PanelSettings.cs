using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Nesting.AutoCAD.UI
{
    /// <summary>面板里的一行板材规格（含"是否启用"勾选状态）</summary>
    public sealed class PanelSheetRow
    {
        public bool Use = true;
        public string Name = "";
        public double W = 1220;
        public double H = 2440;
    }

    /// <summary>
    /// 面板参数持久化。
    ///
    /// 存到 %AppData%\CadNesting\panel-settings.json，而不是插件目录：
    /// 插件目录在 Program Files 或 bundle 下，普通用户没有写权限，
    /// 而且升级/重装插件会把它冲掉。
    ///
    /// 所有读写都吞异常：参数丢了顶多是下次要重新填一遍，
    /// 但如果因为磁盘只读、文件被占用就抛到 UI 线程，面板会直接崩掉。
    /// </summary>
    public sealed class PanelSettings
    {
        public List<PanelSheetRow> Sheets = new List<PanelSheetRow>();
        public double SpacingMm = 0;
        public double MarginMm = 5;
        public bool AllowRotate90 = false;
        public bool FollowText = true;

        /// <summary>多规格时是否大板优先（默认是，与 NestSettings.SheetPreferLarge 同义）</summary>
        public bool SheetPreferLarge = true;

        public string Keywords = "";

        /// <summary>最近一次读/写失败的原因，供 NESTDIAG 显示</summary>
        public static string LastError;

        public static string DirPath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "CadNesting");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(DirPath, "panel-settings.json"); }
        }

        /// <summary>出厂默认：三种常用板 + 间距 0 + 不勾选旋转</summary>
        public static PanelSettings CreateDefault()
        {
            var s = new PanelSettings();
            s.Sheets.Add(new PanelSheetRow { Use = true, Name = "1220x2440", W = 1220, H = 2440 });
            s.Sheets.Add(new PanelSheetRow { Use = true, Name = "1220x2250", W = 1220, H = 2250 });
            s.Sheets.Add(new PanelSheetRow { Use = true, Name = "1220x3050", W = 1220, H = 3050 });
            return s;
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 本类用 public 字段（与 Nesting.Core 的模型风格一致），
            // 而 System.Text.Json 默认只序列化属性 —— 漏掉这行会存出一个 {}，
            // 表现为"参数设了但下次打开永远是默认值"
            IncludeFields = true,
            // 关键字是中文块名，不转义才看得到真实内容
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>读取配置；文件不存在或内容损坏时返回 null（由调用方用面板默认值）</summary>
        public static PanelSettings TryLoad()
        {
            return TryLoadFrom(FilePath);
        }

        /// <summary>指定路径读取，供测试使用</summary>
        public static PanelSettings TryLoadFrom(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;

                PanelSettings s = JsonSerializer.Deserialize<PanelSettings>(json, JsonOpts);
                if (s == null) return null;

                if (s.Sheets == null) s.Sheets = new List<PanelSheetRow>();
                return s;
            }
            catch (Exception ex)
            {
                LastError = "读取参数失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>保存；成功返回 true。失败不抛异常，原因记在 LastError</summary>
        public bool Save()
        {
            return SaveTo(FilePath);
        }

        /// <summary>指定路径保存，供测试使用</summary>
        public bool SaveTo(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(this, JsonOpts);
                string tmp = path + ".tmp";

                // 先写临时文件再替换：中途断电/被杀进程也不会留下半截 JSON
                File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));

                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = "保存参数失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>数字统一用固定格式，避免中文/欧式区域设置把小数点写成逗号</summary>
        public static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
