using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace LocalModMgr
{
    /// <summary>一个 vpk 在列表里的一行。</summary>
    internal sealed class ModRow
    {
        public string Name;          // 文件名（含 .vpk）
        public string FullPath;
        public long Size;
        public bool Workshop;        // 在 addons\workshop 下（创意工坊订阅的）
        public bool On = true;       // addonlist 里是 1；没登记按游戏默认算「开」
    }

    /// <summary>配置与文件操作。本地版只管游戏目录，没有别的库。</summary>
    internal static class Core
    {
        public static string Here;
        public static string GameRoot = "";
        public static string LogDir;

        public static string CfgPath { get { return Path.Combine(Here, "L4D2MOD管理工具.本地版.ini"); } }
        public static string LogFile { get { return Path.Combine(LogDir, "log-" + DateTime.Now.ToString("yyyyMMdd") + ".txt"); } }

        public static string AddonsDir
        {
            get
            {
                if (GameRoot.Length == 0) return "";
                return Path.Combine(Path.Combine(GameRoot, "left4dead2"), "addons");
            }
        }

        public static bool TestGame(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            try { return File.Exists(Path.Combine(root, "left4dead2.exe")); }
            catch { return false; }
        }

        public static void Load()
        {
            Here = Path.GetDirectoryName(Application.ExecutablePath);
            LogDir = Path.Combine(Here, "_本地版记录");
            try { Directory.CreateDirectory(LogDir); } catch { LogDir = Path.GetTempPath(); }
            try
            {
                if (File.Exists(CfgPath))
                {
                    foreach (string raw in File.ReadAllLines(CfgPath, Encoding.UTF8))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = raw.Substring(0, eq).Trim();
                        string v = raw.Substring(eq + 1).Trim();
                        if (k == "GameRoot") GameRoot = v;
                    }
                }
            }
            catch { }
            if (!TestGame(GameRoot)) GameRoot = FindGame();
        }

        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("GameRoot=" + GameRoot);
                File.WriteAllText(CfgPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("WARN", "保存配置失败: " + ex.Message); }
        }

        /// <summary>在常见位置找一遍 Left 4 Dead 2（各盘的 steamapps\common）。</summary>
        public static string FindGame()
        {
            List<string> cands = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                    if (!d.IsReady) continue;
                    string r = d.RootDirectory.FullName;
                    cands.Add(Path.Combine(Path.Combine(Path.Combine(r, "steamapps"), "common"), "Left 4 Dead 2"));
                    cands.Add(Path.Combine(Path.Combine(Path.Combine(Path.Combine(r, "SteamLibrary"), "steamapps"), "common"), "Left 4 Dead 2"));
                }
                catch { }
            }
            foreach (string c in cands)
                if (TestGame(c)) return c;
            return "";
        }

        // ---------------------------------------------------------- addonlist

        private static readonly Regex LineRe = new Regex("^\\s*\"([^\"]+)\"\\s*\"(\\d*)\"");

        public static string AddonListFile
        {
            get { return Path.Combine(Path.Combine(GameRoot, "left4dead2"), "addonlist.txt"); }
        }

        public static Dictionary<string, bool> ReadAddonList()
        {
            Dictionary<string, bool> d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string p = AddonListFile;
            if (!File.Exists(p)) return d;
            foreach (string raw in File.ReadAllLines(p, Encoding.Default))
            {
                Match m = LineRe.Match(raw);
                if (m.Success) d[m.Groups[1].Value] = m.Groups[2].Value == "1";
            }
            return d;
        }

        public static void WriteAddonList(Dictionary<string, bool> changes)
        {
            string p = AddonListFile;
            List<string> lines = new List<string>();
            if (File.Exists(p)) lines.AddRange(File.ReadAllLines(p, Encoding.Default));
            if (lines.Count == 0) { lines.Add("\"AddonList\""); lines.Add("{"); lines.Add("}"); }
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Count; i++)
            {
                Match m = LineRe.Match(lines[i]);
                if (!m.Success) continue;
                string name = m.Groups[1].Value;
                bool v;
                if (!changes.TryGetValue(name, out v)) continue;
                lines[i] = "\t\"" + name + "\"\t\t\"" + (v ? "1" : "0") + "\"";
                done.Add(name);
            }
            foreach (KeyValuePair<string, bool> kv in changes)
            {
                if (done.Contains(kv.Key)) continue;
                int close = -1;
                for (int i = lines.Count - 1; i >= 0; i--)
                    if (lines[i].Trim() == "}") { close = i; break; }
                if (close < 0) { lines.Add("}"); close = lines.Count - 1; }
                lines.Insert(close, "\t\"" + kv.Key + "\"\t\t\"" + (kv.Value ? "1" : "0") + "\"");
            }
            File.WriteAllLines(p, lines.ToArray(), Encoding.Default);
        }

        // ---------------------------------------------------------- 扫描

        public static List<ModRow> Scan()
        {
            List<ModRow> list = new List<ModRow>();
            string addons = AddonsDir;
            if (addons.Length == 0 || !Directory.Exists(addons)) return list;
            Dictionary<string, bool> st = ReadAddonList();
            foreach (string f in Directory.GetFiles(addons, "*.vpk")) list.Add(MakeRow(f, st, false));
            string ws = Path.Combine(addons, "workshop");
            if (Directory.Exists(ws))
                foreach (string f in Directory.GetFiles(ws, "*.vpk")) list.Add(MakeRow(f, st, true));
            list.Sort(delegate(ModRow a, ModRow b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        private static ModRow MakeRow(string path, Dictionary<string, bool> st, bool workshop)
        {
            ModRow r = new ModRow();
            r.Name = Path.GetFileName(path);
            r.FullPath = path;
            r.Workshop = workshop;
            try { r.Size = new FileInfo(path).Length; } catch { }
            bool v;
            r.On = st.TryGetValue(r.Name, out v) ? v : true;
            return r;
        }

        public static void Log(string level, string msg)
        {
            try { File.AppendAllText(LogFile, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + level + " " + msg + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        public static string Human(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.0") + " MB";
            return (bytes / 1073741824.0).ToString("0.00") + " GB";
        }
    }
}
