using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Installer
{
    // ---------- addonlist.txt 读写（游戏用它记录“哪些 MOD 开着”） ----------
    public static class AddonList
    {
        private static Regex LineRe = new Regex("^\\s*\"([^\"]+)\"\\s+\"([01])\"\\s*$", RegexOptions.Compiled);

        // ---------- 写的时候游戏可能在跑 ----------
        // 游戏自己也会读写 addonlist（加载附加内容时、你在游戏内菜单里改开关时），
        // 所以我们不再硬拦：大厅里调整开关是正常操作，想改就让它写下去，
        // 写完回读一次，对不上就说清楚「可能是游戏把它盖回去了」，不装作成功。

        /// <summary>自检用：置 true / false 假装游戏在跑或没跑。null = 真去查进程（自检没法真起游戏）。</summary>
        internal static bool? ForceGameRunning;

        /// <summary>上一次写入时游戏是否正在运行。界面靠它决定要不要提醒「游戏开着，可能被它自己的状态覆盖」。</summary>
        public static bool LastWroteWhileRunning { get; private set; }

        /// <summary>
        /// 写完开关后插件那边要提醒的一句话（现在只有「缺 L4N」一种）。
        /// 开关的写入口有好几个，文案只在 Work.SyncFromSwitches 里生成一份，界面调 ShowPluginNote 弹。
        /// </summary>
        public static string LastPluginNote = "";

        /// <summary>缺 L4N 时弹一次。没有话就什么都不做。</summary>
        public static void ShowPluginNote()
        {
            if (string.IsNullOrEmpty(LastPluginNote)) return;
            MessageBox.Show(LastPluginNote, "插件不会被加载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LastPluginNote = "";
        }

        public static bool GameIsRunning()
        {
            if (ForceGameRunning.HasValue) return ForceGameRunning.Value;
            try
            {
                Process[] ps = Process.GetProcessesByName("left4dead2");
                bool any = ps.Length > 0;
                for (int i = 0; i < ps.Length; i++) { try { ps[i].Dispose(); } catch { } }
                return any;
            }
            catch { return false; }
        }

        // ---------- 外部改动：记住「我们写完是什么样」，下次一比就知道被动过 ----------
        // Steam 更新订阅会把 MOD 重新置为启用，游戏内菜单也能改开关 —— 这两件事都不经过本程序，
        // 状态一夜之间变样时，至少能给出「谁动了、动了几个」，而不是让用户以为工具坏了。

        private static string SnapshotPath()
        {
            string dir = Core.LogDir;
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, "addonlist.snapshot");
        }

        /// <summary>文件里所有登记（含 workshop 条目）。快照与比对用这个，别用 Read（它会跳过 workshop）。</summary>
        public static Dictionary<string, bool> ReadAll(string gameRoot)
        {
            Dictionary<string, bool> d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string p = FileOf(gameRoot);
            if (!File.Exists(p)) return d;
            try
            {
                foreach (string raw in File.ReadAllLines(p, Encoding.Default))
                {
                    Match m = LineRe.Match(raw);
                    if (m.Success) d[m.Groups[1].Value] = (m.Groups[2].Value == "1");
                }
            }
            catch (Exception ex) { Core.Log("WARN", "读 addonlist 失败: " + ex.Message); }
            return d;
        }

        /// <summary>把我们刚写进去的样子存下来。按 addonlist 的绝对路径认，换游戏目录不会串。</summary>
        private static void SaveSnapshot(string addonListPath)
        {
            string snap = SnapshotPath();
            if (snap == null) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 本程序上次写完 addonlist 的样子；用来发现 Steam 更新 / 游戏内改开关造成的外部改动");
                sb.AppendLine("File=" + addonListPath);
                foreach (KeyValuePair<string, bool> kv in ReadAllOfFile(addonListPath))
                    sb.AppendLine((kv.Value ? "1" : "0") + "\t" + kv.Key);
                Core.WriteAllTextAtomic(snap, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Core.Log("WARN", "写 addonlist 快照失败: " + ex.Message); }
        }

        private static Dictionary<string, bool> ReadAllOfFile(string file)
        {
            Dictionary<string, bool> d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(file)) return d;
            try
            {
                foreach (string raw in File.ReadAllLines(file, Encoding.Default))
                {
                    Match m = LineRe.Match(raw);
                    if (m.Success) d[m.Groups[1].Value] = (m.Groups[2].Value == "1");
                }
            }
            catch { }
            return d;
        }

        /// <summary>读快照；没有、或记的是别的 addonlist → null（当成第一次，不算外部改动）。</summary>
        private static Dictionary<string, bool> LoadSnapshot(string addonListPath)
        {
            string snap = SnapshotPath();
            if (snap == null || !File.Exists(snap)) return null;
            try
            {
                Dictionary<string, bool> d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                bool mine = false;
                foreach (string raw in File.ReadAllLines(snap, Encoding.UTF8))
                {
                    if (raw.StartsWith("#")) continue;
                    if (raw.StartsWith("File=", StringComparison.Ordinal))
                    {
                        mine = string.Equals(raw.Substring(5).Trim(), addonListPath, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    int tab = raw.IndexOf('\t');
                    if (tab < 0 || tab > 1) continue;      // 只认「0/1 + TAB + 文件名」，别的行跳过
                    d[raw.Substring(tab + 1)] = raw[0] == '1';
                }
                return mine ? d : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 拿上次写完的样子比一次现在的文件：有变化返回一句人话（顺手刷新快照，同一处改动只报一次），
        /// 没变化 / 没有快照返回 null。
        /// </summary>
        public static string CheckExternal(string gameRoot)
        {
            if (string.IsNullOrEmpty(gameRoot)) return null;
            string file = FileOf(gameRoot);
            Dictionary<string, bool> prev = LoadSnapshot(file);
            if (prev == null) return null;
            Dictionary<string, bool> cur = ReadAll(gameRoot);

            List<string> on = new List<string>(), off = new List<string>(), added = new List<string>(), gone = new List<string>();
            foreach (KeyValuePair<string, bool> kv in cur)
            {
                bool was;
                if (!prev.TryGetValue(kv.Key, out was)) added.Add(kv.Key);
                else if (kv.Value && !was) on.Add(kv.Key);
                else if (!kv.Value && was) off.Add(kv.Key);
            }
            foreach (KeyValuePair<string, bool> kv in prev) if (!cur.ContainsKey(kv.Key)) gone.Add(kv.Key);
            if (on.Count + off.Count + added.Count + gone.Count == 0) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("检测到 addonlist 被外部改过（Steam 更新订阅，或在游戏里动过开关），已按文件为准：");
            if (on.Count > 0) sb.Append(" 开启 " + on.Count + " 个");
            if (off.Count > 0) sb.Append(" 关闭 " + off.Count + " 个");
            if (added.Count > 0) sb.Append(" 新增 " + added.Count + " 个");
            if (gone.Count > 0) sb.Append(" 消失 " + gone.Count + " 个");
            sb.Append("（" + FirstFew(on, off, added, gone) + "）");
            Core.Log("INFO", sb.ToString());
            SaveSnapshot(file);                      // 认过就不重复报
            return sb.ToString();
        }

        private static string FirstFew(List<string> on, List<string> off, List<string> added, List<string> gone)
        {
            List<string> all = new List<string>();
            all.AddRange(on); all.AddRange(off); all.AddRange(added); all.AddRange(gone);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < all.Count && i < 3; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(all[i]);
            }
            if (all.Count > 3) sb.Append(" 等 " + all.Count + " 个");
            return sb.ToString();
        }

        public static string FileOf(string gameRoot)
        {
            return Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addonlist.txt");
        }

        public static bool WriteOrdered(string gameRoot, List<string> order, Dictionary<string, bool> states)
        {
            string p = FileOf(gameRoot);
            List<string> lines = new List<string>();
            if (File.Exists(p)) lines.AddRange(File.ReadAllLines(p, Encoding.Default));
            if (lines.Count == 0) { lines.Add("\"AddonList\""); lines.Add("{"); lines.Add("}"); }
            int open = lines.FindIndex(delegate(string s) { return s.Trim() == "{"; });
            int close = lines.FindLastIndex(delegate(string s) { return s.Trim() == "}"; });
            if (open < 0 || close < 0 || close <= open) { return Write(gameRoot, states); }
            List<string> head = lines.GetRange(0, open + 1);
            List<string> tail = lines.GetRange(close, lines.Count - close);
            HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> body = new List<string>();
            foreach (string name in order)
            {
                bool on = states.ContainsKey(name) ? states[name] : true;
                body.Add("\t\"" + name + "\"\t\t\"" + (on ? "1" : "0") + "\"");
                written.Add(name);
            }
            foreach (string raw in lines.GetRange(open + 1, close - open - 1))
            {
                Match mm = LineRe.Match(raw);
                if (mm.Success && written.Contains(mm.Groups[1].Value)) continue;
                body.Add(raw);
            }
            List<string> all = new List<string>();
            all.AddRange(head); all.AddRange(body); all.AddRange(tail);
            return WriteKeepEol(gameRoot, p, all);
        }

        /// <summary>
        /// 按原文件的行尾写回。游戏自己写出来的是 LF；以前一律用 File.WriteAllLines（Windows 下是 CRLF），
        /// 结果只是「点一下开关」，整份 addonlist 的每一行都被改了 —— 用对比工具看像是全文件都动过，
        /// 也说不清到底改的是哪几条。现在沿用原文的行尾和文件末尾是否有换行，只动该动的那几行。
        /// </summary>
        private static bool WriteKeepEol(string gameRoot, string path, List<string> lines)
        {
            LastWroteWhileRunning = GameIsRunning();
            if (LastWroteWhileRunning)
                Core.Log("INFO", "游戏正在运行，照写；写完回读确认，如果对不上多半是游戏用自己的内存状态盖回去了");
            string nl = "\r\n", tail = "";
            try
            {
                if (File.Exists(path))
                {
                    string raw = File.ReadAllText(path, Encoding.Default);
                    nl = raw.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
                    tail = raw.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
                         : raw.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
                }
            }
            catch { }
            // 原子写：写一半被打断（拔 U 盘 / 空间满）会把 addonlist 留成空文件，游戏里所有开关就全丢了
            Core.WriteAllTextAtomic(path, string.Join(nl, lines.ToArray()) + tail, Encoding.Default);
            SaveSnapshot(path);
            // 开关刚变：插件的进出跟着这份新状态收敛。全程序只在这一处收口（Write / WriteOrdered /
            // Remove 都走这儿），按钮里不再各调一遍；游戏在跑时 SyncPlugins 自己会跳过文件操作。
            Work.SyncFromSwitches(gameRoot);
            return true;
        }

        /// <summary>
        /// 文件里登记的名字，按它在文件里的先后顺序返回。
        /// 顺序 = 覆盖优先级（后写的压前面的），界面上要照这个显示；
        /// 以前界面按文件名排序显示、再整份写回去，一保存就把用户的优先级顺序打乱了。
        /// </summary>
        public static List<string> OrderOf(string gameRoot)
        {
            List<string> names = new List<string>();
            string p = FileOf(gameRoot);
            if (!File.Exists(p)) return names;
            try
            {
                foreach (string raw in File.ReadAllLines(p, Encoding.Default))
                {
                    Match m = LineRe.Match(raw);
                    if (m.Success) names.Add(m.Groups[1].Value);
                }
            }
            catch (Exception ex) { Core.Log("WARN", "读 addonlist 顺序失败: " + ex.Message); }
            return names;
        }

        public static Dictionary<string, bool> Read(string gameRoot)
        {
            Dictionary<string, bool> d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string p = FileOf(gameRoot);
            if (!File.Exists(p)) return d;
            foreach (string raw in File.ReadAllLines(p, Encoding.Default))
            {
                Match m = LineRe.Match(raw);
                if (!m.Success) continue;
                string nm = m.Groups[1].Value;
                // Steam 创意工坊订阅的条目（workshop\123.vpk）暂不归程序管：
                // 订阅/取消订阅由 Steam 决定，混进来会显示成一堆点不动的开关。
                if (nm.IndexOf("workshop", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                d[nm] = (m.Groups[2].Value == "1");
            }
            return d;
        }

        public static bool Write(string gameRoot, Dictionary<string, bool> changes)
        {
            string p = FileOf(gameRoot);
            List<string> lines = new List<string>();
            if (File.Exists(p)) lines.AddRange(File.ReadAllLines(p, Encoding.Default));
            if (lines.Count == 0)
            {
                lines.Add("\"AddonList\"");
                lines.Add("{");
                lines.Add("}");
            }
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Count; i++)
            {
                Match m = LineRe.Match(lines[i]);
                if (!m.Success) continue;
                string name = m.Groups[1].Value;
                if (changes.ContainsKey(name))
                {
                    lines[i] = "\t\"" + name + "\"\t\t\"" + (changes[name] ? "1" : "0") + "\"";
                    done.Add(name);
                }
            }
            List<string> add = new List<string>();
            foreach (KeyValuePair<string, bool> kv in changes)
            {
                if (!done.Contains(kv.Key)) add.Add("\t\"" + kv.Key + "\"\t\t\"" + (kv.Value ? "1" : "0") + "\"");
            }
            if (add.Count > 0)
            {
                int close = lines.FindLastIndex(delegate(string s) { return s.Trim() == "}"; });
                if (close < 0) close = lines.Count;
                lines.InsertRange(close, add);
            }
            return WriteKeepEol(gameRoot, p, lines);
        }

        /// <summary>从 addonlist 里删掉这些名字（文件被移走后，别留一条指向不存在文件的开关）。</summary>
        public static int Remove(string gameRoot, IEnumerable<string> names)
        {
            string p = FileOf(gameRoot);
            if (!File.Exists(p)) return 0;
            HashSet<string> drop = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (drop.Count == 0) return 0;
            List<string> lines = new List<string>(File.ReadAllLines(p, Encoding.Default));
            int n = 0;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                Match m = LineRe.Match(lines[i]);
                if (m.Success && drop.Contains(m.Groups[1].Value)) { lines.RemoveAt(i); n++; }
            }
            if (n > 0 && !WriteKeepEol(gameRoot, p, lines)) return 0;
            return n;
        }
    }

    // ---------- 方案预设 ----------
    public class Profile
    {
        public string Name = "";
        public DateTime Saved = DateTime.MinValue;
        public List<string> Mods = new List<string>();
        public string Plat = "";
        public bool HasOpt = false;      // 有 Opt= 行：才知道 换整套/覆盖/参数存档 这些选项
        public bool Swap = false;
        public bool Overwrite = false;
        public string Tweak = "";
        public string SelfTest = "";     // 空=没跑过；否则 PASS|120|时间 或 FAIL|24|时间

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Name);
            sb.Append(" · ");
            sb.Append(Mods.Count);
            sb.Append(" 个");
            if (Saved != DateTime.MinValue) { sb.Append(" · "); sb.Append(Saved.ToString("MM-dd HH:mm")); }
            if (!string.IsNullOrEmpty(SelfTest)) sb.Append(SelfTest.StartsWith("FAIL") ? " · ⚠崩过" : " · 自检过");
            return sb.ToString();
        }
    }

    public static class ProfileStore
    {
        public static string DirOf()
        {
            return Path.Combine(Core.LogDir, "方案");
        }

        public static List<string> ListProfiles()
        {
            List<string> names = new List<string>();
            try
            {
                string d = DirOf();
                if (!Directory.Exists(d)) return names;
                foreach (string f in Directory.GetFiles(d, "*.txt"))
                    names.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            names.Sort();
            return names;
        }

        public static List<Profile> LoadAll()
        {
            List<Profile> list = new List<Profile>();
            try
            {
                string d = DirOf();
                if (!Directory.Exists(d)) return list;
                foreach (string f in Directory.GetFiles(d, "*.txt"))
                {
                    Profile p = Parse(f);
                    if (p != null) list.Add(p);
                }
            }
            catch { }
            list.Sort(delegate(Profile a, Profile b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static Profile Load(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string f = Path.Combine(DirOf(), Core.SafeName(name) + ".txt");
            if (!File.Exists(f)) return null;
            return Parse(f);
        }

        private static Profile Parse(string file)
        {
            Profile p = new Profile();
            p.Name = Path.GetFileNameWithoutExtension(file);
            try { p.Saved = File.GetLastWriteTime(file); } catch { }
            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); } catch { return p; }
            foreach (string raw in lines)
            {
                string s = raw.Trim();
                if (s.StartsWith("Plat=")) p.Plat = s.Substring(5);
                else if (s.StartsWith("Mod=")) p.Mods.Add(s.Substring(4));
                else if (s.StartsWith("SelfTest=")) p.SelfTest = s.Substring(9);
                else if (s.StartsWith("Opt="))
                {
                    p.HasOpt = true;
                    foreach (string part in s.Substring(4).Split(';'))
                    {
                        string[] kv = part.Split('=');
                        if (kv.Length != 2) continue;
                        if (kv[0] == "Swap") p.Swap = (kv[1] == "1");
                        else if (kv[0] == "Over") p.Overwrite = (kv[1] == "1");
                        else if (kv[0] == "Tweak") p.Tweak = kv[1];
                    }
                }
            }
            return p;
        }

        // 写方案：同名直接覆盖，不留副本
        public static void Save(Profile p)
        {
            p.Name = Core.SafeName(p.Name);
            string d = DirOf();
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            string f = Path.Combine(d, p.Name + ".txt");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 方案 " + p.Name + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            if (!string.IsNullOrEmpty(p.Plat)) sb.AppendLine("Plat=" + p.Plat);
            if (p.HasOpt)
            {
                sb.AppendLine("Opt=Swap=" + (p.Swap ? "1" : "0") + ";Over=" + (p.Overwrite ? "1" : "0") + ";Tweak=" + (p.Tweak == null ? "" : p.Tweak));
            }
            foreach (string m in p.Mods) sb.AppendLine("Mod=" + m);
            if (!string.IsNullOrEmpty(p.SelfTest)) sb.AppendLine("SelfTest=" + p.SelfTest);
            Core.WriteAllTextAtomic(f, sb.ToString(), new UTF8Encoding(true));
        }

        // 把自检结果写回方案（kind = PASS / FAIL）
        public static void SetSelfTest(string name, string kind, int seconds)
        {
            Profile p = Load(name);
            if (p == null) return;
            p.SelfTest = kind + "|" + seconds + "|" + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Save(p);
        }

        public static void Delete(string name)
        {
            string f = Path.Combine(DirOf(), Core.SafeName(name) + ".txt");
            if (File.Exists(f)) File.Delete(f);
        }

        public static string Rename(string oldName, string newName)
        {
            newName = Core.SafeName(newName);
            if (string.IsNullOrEmpty(newName)) return "名字不能为空";
            string a = Path.Combine(DirOf(), Core.SafeName(oldName) + ".txt");
            string b = Path.Combine(DirOf(), newName + ".txt");
            if (!File.Exists(a)) return "原方案不存在";
            if (File.Exists(b)) return "已经有叫这个名字的方案了";
            File.Move(a, b);
            return null;
        }

        // 另存为：复制一份，不动原件
        public static string CopyAs(string from, string to)
        {
            to = Core.SafeName(to);
            if (string.IsNullOrEmpty(to)) return "名字不能为空";
            string a = Path.Combine(DirOf(), Core.SafeName(from) + ".txt");
            string b = Path.Combine(DirOf(), to + ".txt");
            if (!File.Exists(a)) return "原方案不存在";
            if (File.Exists(b)) return "已经有叫这个名字的方案了";
            File.Copy(a, b, false);
            return null;
        }
    }

    // 一组「体积完全相同」的 vpk。体积相同只是嫌疑，内容是否一样要算过才知道（DupVerify）。
    public class DupGroup
    {
        public long Size;
        public List<ModItem> Items = new List<ModItem>();
        /// <summary>核对结果：""=没查过，"same"=内容完全相同，"diff"=内容不同</summary>
        public string Verdict = "";
        public string VerdictNote = "";
    }

    // ---------- 冲突提示（只做便宜的三类） ----------
    public static class ConflictCheck
    {
        public static List<string> Run(List<ModItem> mods, string gameRoot)
        {
            return Run(mods, gameRoot, true);
        }

        // checkExisting=false 用于启动阶段：游戏目录常在网络共享上，逐个 File.Exists 很贵
        public static List<string> Run(List<ModItem> mods, string gameRoot, bool checkExisting)
        {
            List<string> msgs = new List<string>();
            Dictionary<string, List<ModItem>> byName = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
            int already = 0;
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            foreach (ModItem m in mods)
            {
                if (!byName.ContainsKey(m.Name)) byName[m.Name] = new List<ModItem>();
                byName[m.Name].Add(m);
                if (checkExisting)
                {
                    try { if (Directory.Exists(addons) && File.Exists(Path.Combine(addons, m.Name))) already++; } catch { }
                }
            }
            foreach (KeyValuePair<string, List<ModItem>> kv in byName)
            {
                if (DistinctGroups(kv.Value) < 2) continue;          // 同一个套装的部件同名不算冲突
                List<string> cats = new List<string>();
                foreach (ModItem m in kv.Value)
                {
                    string pc = m.Cat + " · " + m.Sub;
                    if (!cats.Contains(pc)) cats.Add(pc);
                }
                msgs.Add("同名文件 " + kv.Value.Count + " 份: " + kv.Key + "（" + string.Join("、", cats.ToArray()) + "），铺进去会互相覆盖");
            }
            foreach (DupGroup g in DupGroups(mods))
            {
                List<string> names = new List<string>();
                foreach (ModItem m in g.Items) names.Add(m.Name);
                msgs.Add("体积完全相同（可能重复）: " + string.Join("、", names.ToArray()));
            }
            if (already > 0) msgs.Add("游戏 addons 里已存在 " + already + " 个同名文件（会被这次的覆盖）");
            return msgs;
        }

        /// <summary>
        /// 体积完全相同（≥1 MB）但不是同一个套装的 vpk —— 只是「嫌疑」，
        /// 真重复要算过内容才知道。重复对话框和日志都用这一份数据。
        /// </summary>
        public static List<DupGroup> DupGroups(List<ModItem> mods)
        {
            List<DupGroup> res = new List<DupGroup>();
            Dictionary<long, List<ModItem>> bySize = new Dictionary<long, List<ModItem>>();
            foreach (ModItem m in mods)
            {
                if (m.Size < 1048576L) continue;                  // 小文件同大小太常见，报了也是噪音
                if (!bySize.ContainsKey(m.Size)) bySize[m.Size] = new List<ModItem>();
                bySize[m.Size].Add(m);
            }
            foreach (KeyValuePair<long, List<ModItem>> kv in bySize)
            {
                if (DistinctGroups(kv.Value) < 2) continue;        // 同一套装的部件不算
                DupGroup g = new DupGroup();
                g.Size = kv.Key;
                g.Items = kv.Value;
                res.Add(g);
            }
            // 浪费空间大的排前面：先处理值得处理的
            res.Sort(delegate(DupGroup a, DupGroup b)
            {
                long wa = a.Size * (a.Items.Count - 1), wb = b.Size * (b.Items.Count - 1);
                return wb.CompareTo(wa);
            });
            return res;
        }

        private static int DistinctGroups(List<ModItem> list)
        {
            List<string> g = new List<string>();
            foreach (ModItem m in list)
            {
                string k = m.GroupKey == null ? m.Key : m.GroupKey;
                if (!g.Contains(k)) g.Add(k);
            }
            return g.Count;
        }

        // 深度检查：只读 VPK 目录表，看这些 MOD 之间有没有同路径文件
        public static List<string> Deep(List<ModItem> sel)
        {
            return DeepAll(sel);
        }

        // 同一个路径被两个 MOD 提供，后果差很多：覆盖 .bsp/.nav/missions 是进图就崩，
        // 覆盖一张贴图只是「看谁生效」。分级表借自 LytVpk 的口径，按我们踩过的坑收窄。
        private static int ConflictSeverity(string p)
        {
            string s = (p ?? "").ToLowerInvariant();
            if (s == "particles/particles_manifest.txt" || s == "scripts/soundmixers.txt") return 3;
            if (s.EndsWith(".bsp") || s.EndsWith(".nav")) return 3;
            if (s.StartsWith("missions/") && s.EndsWith(".txt")) return 3;
            if (s.StartsWith("scripts/") && s.EndsWith(".txt") && !s.StartsWith("scripts/vscripts/")) return 3;
            if (s.EndsWith(".nut") || s.EndsWith(".vscript") || s.EndsWith(".nuc")) return 2;
            if (s.EndsWith(".phy") || s.EndsWith(".vtx") || s.EndsWith(".vvd") || s.EndsWith(".db")) return 2;
            if (s.StartsWith("resource/") && s.EndsWith(".res")) return 2;
            if (s.EndsWith(".ttf") || s.EndsWith(".otf")) return 2;
            return 1;
        }

        // 一次遍历同时给出「路径重叠」和「脚本库冲突」，同一批 vpk 不再解析两遍。
        // 只记“第一次出现是谁的”，出现第二次才建列表——否则每个路径都 new 一个 List，
        // 选一大套三方图就能把内存吃满。
        public static List<string> DeepAll(List<ModItem> sel)
        {
            List<string> msgs = new List<string>();
            if (sel == null || sel.Count < 2) return msgs;

            // 同一把枪被多个 MOD 替换 —— 最容易装错的一类，而且文件重叠检查抓不到：
            // 一个改 v_rifle、另一个改 w_rifle_m16a2，文件名完全不同，抢的却是同一把枪。
            // 换整套模型的、只换第一人称的分开记账：只换第一人称是叠在模型上的，两类之间不算冲突。
            Dictionary<string, List<ModItem>> bySubject = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in sel)
            {
                if (m.Subject.Length == 0) continue;
                if (m.SubjectLevel != "模型") continue;
                string k = (m.Only1P ? "1P" : "ALL") + "\u0001" + m.Subject;
                if (!bySubject.ContainsKey(k)) bySubject[k] = new List<ModItem>();
                bySubject[k].Add(m);
            }
            foreach (KeyValuePair<string, List<ModItem>> kv in bySubject)
            {
                if (kv.Value.Count < 2) continue;
                List<string> names = new List<string>();
                foreach (ModItem m in kv.Value)
                {
                    string nm = m.Name;
                    if (nm.Length > 20) nm = nm.Substring(0, 19) + "…";
                    names.Add(nm);
                }
                string[] kp = kv.Key.Split('\u0001');
                string act = kp[0] == "1P" ? "都在改第一人称模型" : "都在换";
                msgs.Add(kv.Value.Count + " 个 MOD " + act + " " + kp[1] + "："
                         + string.Join("、", names.ToArray()) + "（同时装只有一个会生效）");
            }

            const int Cap = 1500000;                  // 最多跟踪 150 万个不同路径
            Dictionary<string, int> firstOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<int>> dup = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            int[] tracked = new int[1];
            int[] capped = new int[1];
            Stopwatch sw = Stopwatch.StartNew();
            for (int idx = 0; idx < sel.Count; idx++)
            {
                if (sel[idx].IsArchive) continue;      // 压缩包里的 vpk 要先解压才能读目录表，这里不做
                int owner = idx;
                Vpk.Walk(sel[idx].FullPath, 300000, delegate(string p)
                {
                    // 每个 addon 都带的“身份证”文件，互相重叠属正常，不算冲突
                    if (p.EndsWith("/addoninfo.txt") || p == "addoninfo.txt") return;
                    if (p.EndsWith("/addonimage.jpg") || p == "addonimage.jpg") return;
                    if (p.EndsWith("/sound.cache") || p == "sound/sound.cache") return;

                    List<int> owners;
                    if (dup.TryGetValue(p, out owners))
                    {
                        if (!owners.Contains(owner)) owners.Add(owner);
                        return;
                    }
                    int first;
                    if (firstOwner.TryGetValue(p, out first))
                    {
                        firstOwner.Remove(p);
                        owners = new List<int>();
                        owners.Add(first);
                        owners.Add(owner);
                        dup[p] = owners;
                        return;
                    }
                    if (tracked[0] >= Cap) { capped[0]++; return; }
                    firstOwner[p] = owner;
                    tracked[0]++;
                });
            }
            sw.Stop();
            int overlap = 0, scriptHit = 0, critHit = 0;
            List<string> ex1 = new List<string>();
            List<string> ex2 = new List<string>();
            List<string> ex3 = new List<string>();
            HashSet<int> involved = new HashSet<int>();
            foreach (KeyValuePair<string, List<int>> kv in dup)
            {
                if (SameGroupOnly(sel, kv.Value)) continue;   // 一套里的几个分片共用文件属正常
                bool isScript = kv.Key.StartsWith("scripts/vscripts/", StringComparison.OrdinalIgnoreCase);
                if (isScript)
                {
                    // 官方允许每个 addon 各带一份的入口脚本，不算冲突
                    if (kv.Key.EndsWith("/director_base_addon.nut", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.EndsWith("/scriptedmode_addon.nut", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.EndsWith("/mapspawn_addon.nut", StringComparison.OrdinalIgnoreCase)) continue;
                    scriptHit++;
                    if (ex2.Count < 3) ex2.Add(kv.Key + "（" + NamesOf(sel, kv.Value) + "）");
                }
                else if (ConflictSeverity(kv.Key) >= 3)
                {
                    // 覆盖就出事的几类（地图 / 导航 / mission / 清单 / 配置）单独一桶，别混进普通重叠
                    critHit++;
                    if (ex3.Count < 3) ex3.Add(kv.Key + "（" + NamesOf(sel, kv.Value) + "）");
                }
                else
                {
                    overlap++;
                    if (ex1.Count < 3) ex1.Add(kv.Key + "（" + NamesOf(sel, kv.Value) + "）");
                }
                foreach (int i in kv.Value) involved.Add(i);
            }
            if (critHit > 0)
            {
                msgs.Add("有 " + critHit + " 个「覆盖就出事」的文件（地图/导航/mission/particles_manifest/soundmixers/scripts 配置），同时装大概率进图崩");
                foreach (string e in ex3) msgs.Add("例: " + e);
            }
            if (overlap > 0)
            {
                msgs.Add("这 " + sel.Count + " 个 MOD 之间有 " + overlap + " 个文件路径重叠，会互相覆盖");
                msgs.Add("涉及 " + involved.Count + " 个 MOD");
                foreach (string e in ex1) msgs.Add("例: " + e);
            }
            if (scriptHit > 0)
            {
                msgs.Add("另有 " + scriptHit + " 个 scripts/vscripts 脚本被多个 MOD 同时提供，这类冲突会让进图直接崩");
                foreach (string e in ex2) msgs.Add("例: " + e);
            }
            if (capped[0] > 0) msgs.Add("（路径太多，只比对了前 " + Cap + " 个，剩余 " + capped[0] + " 条未比对）");
            Core.Log("INFO", "深度冲突检查 " + Math.Round(sw.Elapsed.TotalSeconds, 1) + " 秒，唯一路径 " + tracked[0] + " 条");
            return msgs;
        }

        private static bool SameGroupOnly(List<ModItem> sel, List<int> idx)
        {
            string first = null;
            foreach (int i in idx)
            {
                string k = sel[i].GroupKey == null ? sel[i].Key : sel[i].GroupKey;
                if (first == null) first = k;
                else if (k != first) return false;
            }
            return true;
        }

        private static string NamesOf(List<ModItem> sel, List<int> idx)
        {
            List<string> names = new List<string>();
            foreach (int i in idx) if (i >= 0 && i < sel.Count) names.Add(sel[i].Name);
            return string.Join(" / ", names.ToArray());
        }
    }

    // ---------- 按顺序写 addonlist（顺序=覆盖优先级） ----------    // ---------- VPK 目录表解析（只读文件表，不读内容） ----------
    public static class Vpk
    {
        public static List<string> Paths(string file, int maxEntries)
        {
            List<string> list = new List<string>();
            Walk(file, maxEntries, delegate(string p) { list.Add(p); });
            return list;
        }

        // 流式版：目录树一次读进内存再扫（不再逐字节 ReadByte），也不把几万条路径全堆在 List 里
        // 从内存里的目录表走一遍（zip / rar 里解出的那一小段用得上）
        // 结构与 Vpk.Walk 完全一致：扩展名 → 目录 → 文件名 三层，每条后面跟 18 字节元数据
        public static void WalkBuffer(byte[] t, int maxEntries, Action<string> onPath)
        {
            int i = 0, count = 0;
            while (i < t.Length)
            {
                string ext = ReadStr(t, ref i);
                if (ext.Length == 0) break;
                while (i < t.Length)
                {
                    string path = ReadStr(t, ref i);
                    if (path.Length == 0) break;
                    while (i < t.Length)
                    {
                        string name = ReadStr(t, ref i);
                        if (name.Length == 0) break;
                        i += 18;
                        if (i > t.Length) return;
                        string full = ((path.Length > 0 ? path + "/" : "") + name + "." + ext).TrimStart('/');
                        onPath(full.ToLowerInvariant());
                        if (++count >= maxEntries) return;
                    }
                }
            }
        }

        /// <summary>
        /// 结构自检（下载完必跑）：目录表要能完整走完，且所有"数据在本文件里"的条目必须落在文件大小以内。
        /// 网络半截断开时文件大小可能看着正常，但数据段没下全 —— 这种包进游戏就是缺模型/缺贴图。
        /// </summary>
        public static bool VerifyComplete(string file, out string reason)
        {
            reason = "";
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (fs.Length < 12) { reason = "文件太小"; return false; }
                    if (br.ReadUInt32() != 0x55AA1234) { reason = "不是 vpk"; return false; }
                    uint ver = br.ReadUInt32();
                    int treeSize = (int)br.ReadUInt32();
                    uint dataSec = 0, md5Sec = 0, otherSec = 0, sigSec = 0;
                    if (ver == 2)
                    {
                        dataSec = br.ReadUInt32(); md5Sec = br.ReadUInt32();
                        otherSec = br.ReadUInt32(); sigSec = br.ReadUInt32();
                    }
                    if (treeSize <= 0 || treeSize > 64 * 1024 * 1024) { reason = "目录表大小异常"; return false; }
                    long avail = fs.Length - fs.Position;
                    if (treeSize > avail) { reason = "目录表超出文件末尾"; return false; }
                    byte[] t = br.ReadBytes(treeSize);
                    if (t == null || t.Length != treeSize) { reason = "目录表读不全"; return false; }
                    long dataStart = fs.Position + md5Sec + otherSec + sigSec;

                    long maxEnd = 0;
                    int entries = 0, external = 0;
                    int i = 0;
                    while (i < t.Length)
                    {
                        string ext = ReadStr(t, ref i);
                        if (ext.Length == 0) break;
                        while (i < t.Length)
                        {
                            string path = ReadStr(t, ref i);
                            if (path.Length == 0) break;
                            while (i < t.Length)
                            {
                                string name = ReadStr(t, ref i);
                                if (name.Length == 0) break;
                                if (i + 18 > t.Length) { reason = "目录表截断"; return false; }
                                ushort pre = BitConverter.ToUInt16(t, i + 4);
                                ushort ai = BitConverter.ToUInt16(t, i + 6);
                                uint off = BitConverter.ToUInt32(t, i + 8);
                                uint len = BitConverter.ToUInt32(t, i + 12);
                                i += 18;
                                if (pre > t.Length - i) { reason = "预载段截断"; return false; }
                                i += pre;
                                entries++;
                                if (ai != 0x7FFF) external++;
                                else if ((long)off + len > maxEnd) maxEnd = (long)off + len;
                            }
                        }
                    }
                    if (entries == 0) { reason = "目录表是空的"; return false; }
                    if (external > 0) { reason = "这是分卷 vpk：" + external + " 个条目在配套分卷里，单下这一个用不了"; return false; }
                    long need = dataStart + maxEnd;
                    if (need > fs.Length) { reason = "数据段不完整：需要 " + need + " 字节，实际只有 " + fs.Length; return false; }
                    if (ver == 2 && dataSec > 0 && dataStart + dataSec > fs.Length)
                    { reason = "数据段声明超出文件（少 " + (dataStart + dataSec - fs.Length) + " 字节）"; return false; }
                    return true;
                }
            }
            catch (Exception ex) { reason = ex.Message; return false; }
        }

        public static void Walk(string file, int maxEntries, Action<string> onPath)
        {
            int count = 0;
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (fs.Length < 12) return;
                    uint sig = br.ReadUInt32();
                    if (sig != 0x55AA1234) return;
                    uint ver = br.ReadUInt32();
                    int treeSize = (int)br.ReadUInt32();
                    if (ver == 2) br.ReadBytes(16);
                    if (treeSize <= 0) return;
                    long avail = fs.Length - fs.Position;
                    if (treeSize > avail) treeSize = (int)avail;
                    if (treeSize > 32 * 1024 * 1024) treeSize = 32 * 1024 * 1024;
                    byte[] t = br.ReadBytes(treeSize);
                    if (t == null || t.Length == 0) return;
                    int i = 0;
                    while (i < t.Length)
                    {
                        string ext = ReadStr(t, ref i);
                        if (ext.Length == 0) break;                  // 空扩展名 = 目录树结束
                        while (i < t.Length)
                        {
                            string path = ReadStr(t, ref i);
                            if (path.Length == 0) break;             // 空目录 = 这个扩展名结束
                            while (i < t.Length)
                            {
                                string name = ReadStr(t, ref i);
                                if (name.Length == 0) break;         // 空文件名 = 这个目录结束
                                i += 18;   // crc4 + 预载2 + 归档号2 + 偏移4 + 长度4 + 结尾2
                                if (i > t.Length) return;
                                string full = ((path.Length > 0 ? path + "/" : "") + name + "." + ext).TrimStart('/');
                                onPath(full.ToLowerInvariant());
                                if (++count >= maxEntries) return;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static string ReadStr(byte[] t, ref int i)
        {
            int start = i;
            while (i < t.Length && t[i] != 0) i++;
            int len = i - start;
            if (i < t.Length) i++;           // 吃掉结尾的 0
            if (len > 512) len = 512;        // 坏文件保护
            return Encoding.UTF8.GetString(t, start, len);
        }

        // ---------- 包内的「身份证」：addoninfo.txt ----------
        // 游戏 addons 菜单显示的是这里的 addonTitle，不是文件名。库里绝大多数包两者都不一样。
        public class AddonMeta
        {
            public bool Found;              // 包里确实有根目录的 addoninfo.txt
            public bool Readable;           // 内容读出来了（在分卷里 / 坏包时为 false）
            public string Title = "";
            public string Author = "";
            /// <summary>addoninfo.txt 全文（标题、描述、标签都在里面）。识别兜底用：作者常把枪名写进描述。</summary>
            public string Text = "";
        }

        /// <summary>
        /// 读 vpk 根目录的 addoninfo.txt，只取 addonTitle / addonauthor。
        /// 只读文件头 + 目录表 + 这一个条目，不碰正片，几十 KB 的 I/O。
        /// 读不到就返回空字段 —— 调用方一律按「没有标题」处理，不能因此判成坏包。
        /// </summary>
        public static AddonMeta ReadAddonInfo(string file)
        {
            AddonMeta meta = new AddonMeta();
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                           FileShare.Read | FileShare.Write, 64 * 1024, FileOptions.RandomAccess))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (fs.Length < 12) return meta;
                    uint sig = br.ReadUInt32();
                    if (sig != 0x55AA1234) return meta;
                    uint ver = br.ReadUInt32();
                    int treeSize = (int)br.ReadUInt32();
                    if (ver == 2) br.ReadBytes(16);
                    if (treeSize <= 0) return meta;
                    long avail = fs.Length - fs.Position;
                    if (treeSize > avail) treeSize = (int)avail;
                    if (treeSize > 32 * 1024 * 1024) treeSize = 32 * 1024 * 1024;
                    byte[] t = br.ReadBytes(treeSize);
                    if (t == null || t.Length == 0) return meta;
                    long dataBase = fs.Position;

                    int ps, pl;
                    uint dof, dln;
                    bool inFile;
                    if (!FindAddonInfo(t, out ps, out pl, out dof, out dln, out inFile)) return meta;
                    meta.Found = true;

                    byte[] raw = null;
                    if (pl > 0)
                    {
                        raw = new byte[pl];
                        Array.Copy(t, ps, raw, 0, pl);
                    }
                    else if (inFile && dln > 0 && dln < 1024 * 1024)
                    {
                        raw = new byte[dln];
                        fs.Seek(dataBase + dof, SeekOrigin.Begin);
                        if (!ReadFull(fs, raw, raw.Length)) raw = null;
                    }
                    if (raw == null) return meta;          // 数据在分卷里：不追，算没有标题
                    meta.Readable = true;
                    FillMeta(meta, raw);
                }
            }
            catch { }
            return meta;
        }

        /// <summary>
        /// 读 vpk 里打包的封面图（addonimage.jpg / .png / .gif）。
        /// 游戏自己的 addons 列表显示的就是这张图；作者打包时没放就没有，返回 null。
        /// 有 preload 的直接从目录表取，正文在数据段的按 offset/length 读包体。
        /// </summary>
        public static byte[] ReadAddonImage(string file)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                           FileShare.Read | FileShare.Write, 64 * 1024, FileOptions.RandomAccess))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (fs.Length < 12) return null;
                    if (br.ReadUInt32() != 0x55AA1234) return null;
                    uint ver = br.ReadUInt32();
                    int treeSize = (int)br.ReadUInt32();
                    if (ver == 2) br.ReadBytes(16);
                    if (treeSize <= 0) return null;
                    long avail = fs.Length - fs.Position;
                    if (treeSize > avail) treeSize = (int)avail;
                    if (treeSize > 32 * 1024 * 1024) treeSize = 32 * 1024 * 1024;
                    byte[] t = br.ReadBytes(treeSize);
                    if (t == null || t.Length == 0) return null;
                    long dataBase = fs.Position;

                    int ps, pl;
                    uint dof, dln;
                    bool inFile;
                    if (!FindAddonImage(t, out ps, out pl, out dof, out dln, out inFile)) return null;

                    byte[] raw = null;
                    if (pl > 0)
                    {
                        raw = new byte[pl];
                        Array.Copy(t, ps, raw, 0, pl);
                    }
                    else if (inFile && dln > 0 && dln < 8 * 1024 * 1024)
                    {
                        raw = new byte[dln];
                        fs.Seek(dataBase + dof, SeekOrigin.Begin);
                        if (!ReadFull(fs, raw, raw.Length)) raw = null;
                    }
                    if (raw == null || raw.Length < 32) return null;

                    // 认图片头：有作者会塞 0 字节占位或别的文件，直接交给 Image 会抛异常
                    bool jpeg = raw[0] == 0xFF && raw[1] == 0xD8;
                    bool png = raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E;
                    bool gif = raw[0] == 0x47 && raw[1] == 0x49 && raw[2] == 0x46;
                    if (!jpeg && !png && !gif) return null;
                    return raw;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 在目录表里找 addonimage。路径不卡死在根目录：实测有作者把图打在 " /addonimage.jpg"
        /// （目录名是一个空格），只认根目录会把这一批全漏掉。优先取根目录那份，没有才用按路径找到的。
        /// </summary>
        private static bool FindAddonImage(byte[] t, out int preStart, out int preLen,
                                           out uint dataOff, out uint dataLen, out bool inThisFile)
        {
            preStart = 0; preLen = 0; dataOff = 0; dataLen = 0; inThisFile = false;
            bool gotFallback = false;
            int i = 0;
            while (i < t.Length)
            {
                string ext = ReadStr(t, ref i);
                if (ext.Length == 0) break;
                while (i < t.Length)
                {
                    string path = ReadStr(t, ref i);
                    if (path.Length == 0) break;
                    while (i < t.Length)
                    {
                        string name = ReadStr(t, ref i);
                        if (name.Length == 0) break;
                        if (i + 18 > t.Length) return gotFallback;
                        ushort pre = BitConverter.ToUInt16(t, i + 4);
                        ushort ai = BitConverter.ToUInt16(t, i + 6);
                        uint off = BitConverter.ToUInt32(t, i + 8);
                        uint len = BitConverter.ToUInt32(t, i + 12);
                        i += 18;
                        if (pre > t.Length - i) return gotFallback;
                        int ps = i;
                        i += pre;

                        if (!string.Equals(name, "addonimage", StringComparison.OrdinalIgnoreCase)) continue;
                        bool isImg = string.Equals(ext, "jpg", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(ext, "jpeg", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(ext, "png", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(ext, "gif", StringComparison.OrdinalIgnoreCase);
                        if (!isImg) continue;
                        if (gotFallback && path.Trim().Length != 0) continue;

                        preStart = ps; preLen = pre;
                        dataOff = off; dataLen = len;
                        inThisFile = ai == 0x7FFF;
                        if (path.Trim().Length == 0) return true;   // 根目录那份最正，直接收工
                        gotFallback = true;
                    }
                }
            }
            return gotFallback;
        }

        /// <summary>
        /// 只有目录表（压缩包里的 vpk 是这样读的）时用这个。
        /// preload 里的正文能直接取；正文在数据段的话，手上没有包体，只能报「找到了但读不出」。
        /// </summary>
        public static AddonMeta InfoFromTreeBuffer(byte[] tree)
        {
            AddonMeta meta = new AddonMeta();
            if (tree == null || tree.Length == 0) return meta;
            try
            {
                int ps, pl;
                uint dof, dln;
                bool inFile;
                if (!FindAddonInfo(tree, out ps, out pl, out dof, out dln, out inFile)) return meta;
                meta.Found = true;
                if (pl <= 0) return meta;
                byte[] raw = new byte[pl];
                Array.Copy(tree, ps, raw, 0, pl);
                meta.Readable = true;
                FillMeta(meta, raw);
            }
            catch { }
            return meta;
        }

        private static void FillMeta(AddonMeta meta, byte[] raw)
        {
            string text = DecodeText(raw);
            meta.Title = CleanField(InfoField(text, "addontitle"));
            meta.Author = CleanField(InfoField(text, "addonauthor"));
            meta.Text = text != null && text.Length > 4000 ? text.Substring(0, 4000) : (text ?? "");
        }

        /// <summary>
        /// 在目录表里找根目录那份 addoninfo.txt，把数据位置告诉调用方。
        /// 只认根目录：有的包把 addoninfo.txt 塞在 for_modders/ 下当参考，那份游戏根本不读
        /// （标题写的还是别的包名，正是「不能轻信标题」的活例子）。
        /// </summary>
        private static bool FindAddonInfo(byte[] t, out int preStart, out int preLen, out uint dataOff, out uint dataLen, out bool inThisFile)
        {
            preStart = 0; preLen = 0; dataOff = 0; dataLen = 0; inThisFile = false;
            int i = 0;
            while (i < t.Length)
            {
                string ext = ReadStr(t, ref i);
                if (ext.Length == 0) break;
                while (i < t.Length)
                {
                    string path = ReadStr(t, ref i);
                    if (path.Length == 0) break;
                    while (i < t.Length)
                    {
                        string name = ReadStr(t, ref i);
                        if (name.Length == 0) break;
                        if (i + 18 > t.Length) return false;
                        ushort pre = BitConverter.ToUInt16(t, i + 4);
                        ushort ai = BitConverter.ToUInt16(t, i + 6);
                        uint off = BitConverter.ToUInt32(t, i + 8);
                        uint len = BitConverter.ToUInt32(t, i + 12);
                        i += 18;
                        if (pre > t.Length - i) return false;
                        int ps = i;
                        i += pre;

                        if (!string.Equals(ext, "txt", StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(name, "addoninfo", StringComparison.OrdinalIgnoreCase)
                            || path.Trim().Length != 0) continue;

                        preStart = ps; preLen = pre;
                        dataOff = off; dataLen = len;
                        inThisFile = ai == 0x7FFF;
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool ReadFull(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }

        /// <summary>顺序流（Rar.exe 的 stdout、zip 条目流）不能 seek，只能读掉。</summary>
        private static bool Skip(Stream s, long n)
        {
            if (n <= 0) return true;
            try { if (s.CanSeek) { s.Seek(n, SeekOrigin.Current); return true; } }
            catch { }
            byte[] buf = new byte[64 * 1024];
            long left = n;
            while (left > 0)
            {
                int want = (int)Math.Min(buf.Length, left);
                int r = s.Read(buf, 0, want);
                if (r <= 0) return false;
                left -= r;
            }
            return true;
        }

        /// <summary>
        /// 从「vpk 文件开头」的流里读 addoninfo。压缩包里的包只能这样读：
        /// 拿到整包不现实（几 GB），所以读到 addoninfo 那一小段就收工。
        /// </summary>
        public static AddonMeta ReadAddonInfoFromStream(Stream s)
        {
            AddonMeta meta = new AddonMeta();
            try
            {
                byte[] hdr = new byte[12];
                if (!ReadFull(s, hdr, 12)) return meta;
                uint sig = BitConverter.ToUInt32(hdr, 0);
                if (sig != 0x55AA1234) return meta;
                uint ver = BitConverter.ToUInt32(hdr, 4);
                int treeSize = (int)BitConverter.ToUInt32(hdr, 8);
                if (ver == 2 && !ReadFull(s, new byte[16], 16)) return meta;
                if (treeSize <= 0 || treeSize > 32 * 1024 * 1024) return meta;

                byte[] t = new byte[treeSize];
                if (!ReadFull(s, t, treeSize)) return meta;

                int ps, pl;
                uint dof, dln;
                bool inFile;
                if (!FindAddonInfo(t, out ps, out pl, out dof, out dln, out inFile)) return meta;
                meta.Found = true;

                byte[] raw = null;
                if (pl > 0)
                {
                    raw = new byte[pl];
                    Array.Copy(t, ps, raw, 0, pl);
                }
                else if (inFile && dln > 0 && dln < 1024 * 1024)
                {
                    // 目录表之后就是数据段，跳过前面 off 个字节
                    if (!Skip(s, dof)) return meta;
                    raw = new byte[dln];
                    if (!ReadFull(s, raw, raw.Length)) raw = null;
                }
                if (raw == null) return meta;
                meta.Readable = true;
                FillMeta(meta, raw);
            }
            catch { }
            return meta;
        }

        /// <summary>
        /// 压缩包里的 vpk 的 addoninfo。zip 直接开条目流；rar 从 Rar.exe 的 stdout 里读到就走，不等整个包解完。
        /// 7z 这里不碰：解到临时文件那条路（TreeOf7z）会连临时目录一起删，留给「点详情」时再读。
        /// </summary>
        public static AddonMeta ReadAddonInfoFromArchive(string container, string inner)
        {
            AddonMeta meta = new AddonMeta();
            try
            {
                if (string.IsNullOrEmpty(container) || string.IsNullOrEmpty(inner)) return meta;
                if (container.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.OpenRead(container))
                    {
                        System.IO.Compression.ZipArchiveEntry hit = null;
                        string want = inner.Replace('\\', '/');
                        foreach (System.IO.Compression.ZipArchiveEntry e in za.Entries)
                            if (string.Equals(e.FullName.Replace('\\', '/'), want, StringComparison.OrdinalIgnoreCase)) { hit = e; break; }
                        if (hit == null) return meta;
                        using (Stream st = hit.Open()) return ReadAddonInfoFromStream(st);
                    }
                }
                if (container.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) return meta;
                string rar = Catalog.RarExe();
                if (string.IsNullOrEmpty(rar)) return meta;
                ProcessStartInfo psi = new ProcessStartInfo(rar, "p -inul -y \"" + container + "\" \"" + inner + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    AddonMeta got = null;
                    System.Threading.Thread rd = new System.Threading.Thread(delegate()
                    { try { got = ReadAddonInfoFromStream(p.StandardOutput.BaseStream); } catch { } });
                    rd.IsBackground = true;
                    rd.Start();
                    // 杀软锁包 / 盘停顿：等不到就放弃，绝不能拖着后台线程不放
                    if (!rd.Join(20000)) { try { p.Kill(); } catch { } return meta; }
                    try { p.Kill(); } catch { }
                    return got ?? meta;
                }
            }
            catch { }
            return meta;
        }

        // 作者写 addoninfo 的手法五花八门：带引号、不带引号、大小写随意，甚至只写结尾一个引号。
        // 所以引号一律当可选的，值取到行尾或下一个引号为止。
        private static string InfoField(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return "";
            Match m = Regex.Match(text, "^\\s*\"?" + key + "\"?\\s+\"?([^\"\\r\\n]*)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : "";
        }

        // UTF-8 优先（V 社工具写的都是 UTF-8），解不动再按 GBK —— 国内作者手搓的包不少是 GBK。
        // Encoding.UTF8 默认不抛异常、会悄悄替换成 U+FFFD，所以这里用严格模式判一次。
        private static string DecodeText(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch
            {
                try { return Encoding.GetEncoding(936).GetString(b); }
                catch { return Encoding.UTF8.GetString(b); }
            }
        }

        // 标题里混进 tab/换行会把缓存行拆坏（tab 分隔），控制字符也不能留
        private static string CleanField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == '\t' || ch == '\r' || ch == '\n') sb.Append(' ');
                else if (!char.IsControl(ch)) sb.Append(ch);
            }
            string r = sb.ToString().Trim().Trim('"').Trim();
            if (r.Length > 120) r = r.Substring(0, 120);
            return r;
        }
    }

    // ---------- 「已装 MOD 开关」窗口 ----------
    public class AddonSwitchForm : Form
    {
        private readonly string gameRoot;
        private ListView lv;
        private Label lbl;
        private bool orderDirty;            // 用户在这个窗口里真的上移/下移过：只有这时才重排 addonlist
        private Dictionary<string, bool> state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public int Deleted;                 // 这个窗口里删掉（真删）的个数，关窗后主窗口据此决定要不要重扫

        public AddonSwitchForm(string root)
        {
            gameRoot = root;
            Text = Lang.S("已装 MOD 开关 —— 勾上=进游戏时启用");
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(560, 480);
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            state = AddonList.Read(gameRoot);
            lv = new ListView();
            lv.SetBounds(12, 12, 536, 380);
            lv.View = View.Details;
            lv.CheckBoxes = true;
            lv.FullRowSelect = true;
            lv.Columns.Add(Lang.S("MOD 文件"), 380);
            lv.Columns.Add(Lang.S("状态"), 120);
            lv.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (e.Item.SubItems.Count > 1)
                    e.Item.SubItems[1].Text = e.Item.Checked ? "启用" : "关闭";
            };
            Controls.Add(lv);
            ContextMenuStrip cms = new ContextMenuStrip();
            ToolStripMenuItem delItem = new ToolStripMenuItem("删除选中的", null, delegate { DeleteChecked(); });
            cms.Items.Add(delItem);
            // 这个列表里「勾选」= 启用，跟「选中」是两回事。菜单按选中行数报数，
            // 不在菜单文字里混用「勾」字，免得有人为了删除去取消勾选、结果把 MOD 打开了。
            cms.Opening += delegate
            {
                // U 盘模式下这一项直接灰掉并写清原因：以前它一直是亮的，
                // 点下去就真删游戏 addons 里的 vpk，把主界面那道模式铁律绕过去了。
                if (!Core.IsLocal)
                {
                    delItem.Text = "删除选中的（U 盘模式不动游戏目录的文件）";
                    delItem.Enabled = false;
                    return;
                }
                delItem.Text = lv.SelectedItems.Count > 0
                    ? "删除选中的 " + lv.SelectedItems.Count + " 个"
                    : "删除选中的（先在上面选行）";
                delItem.Enabled = lv.SelectedItems.Count > 0;
            };
            lv.ContextMenuStrip = cms;

            Button up = new Button(); up.Text = Lang.S("上移 ↑"); up.SetBounds(12, 360, 90, 28); up.Click += delegate { MoveItem(-1); }; Controls.Add(up);
            Button dn = new Button(); dn.Text = Lang.S("下移 ↓"); dn.SetBounds(108, 360, 90, 28); dn.Click += delegate { MoveItem(1); }; Controls.Add(dn);
            Label tip = new Label(); tip.Text = Lang.S("顺序 = 覆盖优先级（下面的覆盖上面的）"); tip.SetBounds(210, 364, 330, 20); Controls.Add(tip);
            Button all = new Button(); all.Text = Lang.S("全开"); all.SetBounds(12, 402, 80, 30);
            all.Click += delegate { SetAll(true); }; Controls.Add(all);
            Button none = new Button(); none.Text = Lang.S("全关"); none.SetBounds(98, 402, 80, 30);
            none.Click += delegate { SetAll(false); }; Controls.Add(none);
            Button collect = new Button(); collect.Text = Lang.S("收回新 MOD"); collect.SetBounds(184, 402, 110, 30);
            collect.Click += delegate { Collect(); }; Controls.Add(collect);
            Button park = new Button(); park.Text = Lang.S("清理关着的"); park.SetBounds(300, 402, 104, 30);
            park.Click += delegate { Park(); }; Controls.Add(park);
            // 这两个都是「往 U 盘库搬东西」：本地模式没有库，搬过去只会进一个不参与扫描的目录，
            // 同时把游戏 addons 里那份删掉 —— MOD 于是在本地模式列表里凭空消失。按模式锁死。
            if (Core.IsLocal)
            {
                collect.Enabled = false;
                park.Enabled = false;
                ToolTip tipLocal = new ToolTip();
                tipLocal.SetToolTip(collect, "本地模式没有 U 盘库，这个功能用不上");
                tipLocal.SetToolTip(park, "本地模式没有 U 盘库，这个功能用不上");
            }
            Button ok = new Button(); ok.Text = Lang.S("写入并关闭"); ok.SetBounds(410, 402, 138, 30);
            ok.Click += delegate { Apply(); }; Controls.Add(ok);
            lbl = new Label(); lbl.SetBounds(12, 436, 536, 20); lbl.Text = ""; Controls.Add(lbl);

            LoadItems();
        }

        /// <summary>批量删除：把选中的 vpk 从游戏 addons 直接删掉，并摘掉 addonlist 登记。</summary>
        private void DeleteChecked()
        {
            // 铁律：删游戏目录里的文件只许本地模式做，U 盘模式库里的那份才是母本。
            // 菜单项本身也会灰掉，这里再拦一次：键盘回车 / 将来的调用方都绕不过去。
            if (!Core.IsLocal)
            {
                MessageBox.Show("当前是 U 盘模式，不动游戏目录里的文件。\r\n\r\n"
                    + "库里的那份才是母本；真要卸载，把右上角切到「本地模式」再来。",
                    Lang.S("删除已装 MOD"));
                return;
            }
            if (AddonList.GameIsRunning())
            {
                MessageBox.Show("游戏正在运行，先完全退出再删。\r\n\r\n"
                    + "正在被游戏读着的 vpk 删到一半容易报错或留下残文件。\r\n"
                    + "（只改开关不受这个限制，大厅里照样能调）",
                    "游戏正在运行");
                return;
            }
            // 只认选中行。以前用 CheckedItems，可这个列表里勾选=启用：
            // 用户为了删除去「勾选」，反而先把 MOD 打开了；取消勾选再来删又选不中。
            List<string> names = new List<string>();
            foreach (ListViewItem li in lv.SelectedItems) names.Add(li.Tag.ToString());
            if (names.Count == 0)
            {
                MessageBox.Show("先在上面选中要删的行（可按住 Ctrl / Shift 多选）。\r\n\r\n"
                    + "注意：左侧的勾是「启用 / 关闭」，不是选中。", "删除已装 MOD");
                return;
            }
            if (MessageBox.Show("把这 " + names.Count + " 个 MOD 从游戏里删掉？\r\n\r\n"
                + "· vpk 直接从 left4dead2\\addons 删除，删了就是没了\r\n"
                + "· addonlist 里那条登记也会摘掉\r\n\r\n确定？",
                "删除已装 MOD", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            int n = 0;
            foreach (string name in names)
            {
                try
                {
                    ModItem m = new ModItem();
                    m.Name = name;
                    m.FullPath = Path.Combine(addons, name);
                    m.FromGame = true;
                    if (Work.DeleteGameMod(m, gameRoot) == null) n++;
                }
                catch (Exception ex) { Core.Log("WARN", "删除 " + name + " 失败: " + ex.Message); }
            }
            Deleted += n;
            Core.Log("OK", "已装 MOD 开关：从游戏 addons 删掉 " + n + " 个");
            state = AddonList.Read(gameRoot);
            LoadItems();
            lbl.Text = "已删除 " + n + " 个";
        }

        private void LoadItems()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            if (!Directory.Exists(addons)) { lv.EndUpdate(); lbl.Text = Lang.S("游戏里没有 addons 目录"); return; }
            string[] files = Directory.GetFiles(addons, "*.vpk");

            // 行的顺序就是 addonlist 里的顺序（= 覆盖优先级）。以前这里是按文件名排序，
            // 而「写入并关闭」又照这个顺序整份写回去 —— 打开一次、点一次保存，
            // 用户排好的优先级就被改成字母序了。现在先按文件里的顺序排，没登记的排在后面。
            HashSet<string> onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files) onDisk.Add(Path.GetFileName(f));
            List<string> order = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in AddonList.OrderOf(gameRoot))
                if (onDisk.Contains(n) && seen.Add(n)) order.Add(n);
            List<string> rest = new List<string>();
            foreach (string f in files)
            {
                string n = Path.GetFileName(f);
                if (seen.Add(n)) rest.Add(n);
            }
            rest.Sort(StringComparer.OrdinalIgnoreCase);
            order.AddRange(rest);

            foreach (string name in order)
            {
                // 没登记 = 游戏默认就是启用（和主列表的判定一致；见 ModItem.AddonOn 的注释）。
                // 以前这里默认 false，新丢进 addons 的 vpk 一打开这个窗口就成了「关闭」，
                // 点一下「写入并关闭」就写成 0，等于把用户没碰过的 MOD 静默停用。
                bool on;
                if (!state.TryGetValue(name, out on)) on = true;
                ListViewItem li = new ListViewItem(name);
                li.SubItems.Add(on ? "启用" : "关闭");
                li.Checked = on;
                li.Tag = name;
                lv.Items.Add(li);
            }
            lv.EndUpdate();
            lbl.Text = "共 " + files.Length + " 个已装 MOD，顺序 = 覆盖优先级（下面的压上面的）";
        }

        private void MoveItem(int dir)
        {
            if (lv.SelectedIndices.Count == 0) return;
            int i = lv.SelectedIndices[0];
            int j = i + dir;
            if (j < 0 || j >= lv.Items.Count) return;
            ListViewItem it = lv.Items[i];
            lv.Items.RemoveAt(i);
            lv.Items.Insert(j, it);
            it.Selected = true;
            orderDirty = true;
            lv.Focus();
        }

        private void SetAll(bool on)
        {
            lv.BeginUpdate();
            foreach (ListViewItem li in lv.Items)
            {
                li.Checked = on;
                li.SubItems[1].Text = on ? "启用" : "关闭";
            }
            lv.EndUpdate();
        }

        private void Collect()
        {
            if (Core.IsLocal) return;    // 按钮已按模式禁用；这里再挡一次程序化调用
            if (MessageBox.Show(
                "把游戏 addons 里「U 盘库没有」的 MOD 收进 U 盘 addons回收？\r\n\r\n"
                + "· 先复制到 U 盘并核对大小\r\n"
                + "· 复制成功后才删游戏里的那份（给游戏盘腾地方）\r\n"
                + "· 复制失败的一律保留原文件，不会删\r\n\r\n"
                + "注意：删除后游戏里就没有这个 MOD 了，要用时从 U 盘库装回来。",
                "收回新 MOD", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            List<string> kept = new List<string>();
            List<string> failed = new List<string>();
            List<string> got;
            Cursor = Cursors.WaitCursor;
            try
            {
                got = Work.CollectNewMods(gameRoot, true, delegate(int done, int total, string name)
                {
                    // 大文件单个就要几十秒，不报进度会像卡死
                    lbl.Text = "正在收回 " + done + "/" + total + "：" + name;
                    lbl.Refresh();
                    Application.DoEvents();
                }, kept, failed);
            }
            finally { Cursor = Cursors.Default; }

            if (got.Count == 0 && kept.Count == 0 && failed.Count == 0)
            {
                lbl.Text = "";
                MessageBox.Show("没有新 MOD：游戏里的都在 U 盘库里。");
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("收回 " + got.Count + " 个到 U 盘 addons回收，并从游戏里删除。");
            if (kept.Count > 0) sb.AppendLine("另有 " + kept.Count + " 个库里已有同一份，已直接删掉游戏里的副本。");
            if (failed.Count > 0)
                sb.AppendLine("有 " + failed.Count + " 个复制失败，原文件已保留（详见日志）：\r\n  "
                    + string.Join("\r\n  ", failed.ToArray()));
            lbl.Text = "已收回 " + got.Count + " 个";
            MessageBox.Show(sb.ToString(), "收回新 MOD");
        }

        /// <summary>把「关闭」的 MOD 挪回 U 盘，给游戏盘腾地方（开着的一概不碰）。</summary>
        private void Park()
        {
            if (Core.IsLocal) return;    // 按钮已按模式禁用；这里再挡一次程序化调用
            if (MessageBox.Show(
                "把「关闭」状态的 MOD 从游戏 addons 里挪回 U 盘？\r\n\r\n"
                + "· 只动已经关掉的\r\n"
                + "· 开着的、以及不是本工具装的，一概不碰\r\n"
                + "· 挪走的以后要用会自动重新复制\r\n\r\n"
                +                 "先把上面的开关写进去，再清理。", "清理关着的 MOD",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            if (!ApplyQuiet()) return;          // 写入失败就不继续清理，避免按旧状态挪文件
            int n = Work.ParkDisabled(gameRoot);
            state = AddonList.Read(gameRoot);
            LoadItems();
            MessageBox.Show(n > 0
                ? ("已挪回 U 盘 " + n + " 个。\r\n下次装到这套时会重新复制回来。")
                : "没有可清理的：关着的 MOD 本来就不在 addons 里。");
        }

        /// <summary>
        /// 把窗口里的开关写进 addonlist。
        /// reorder=false 时只改 1/0、不动任何一行的位置 —— 绝大多数人只是来开关一个 MOD，
        /// 不该顺带把整份优先级顺序重写一遍。真的上移/下移过（reorder=true）才按列表顺序重排。
        /// </summary>
        internal static bool WriteSwitches(string gameRoot, List<string> order, Dictionary<string, bool> states, bool reorder)
        {
            return reorder ? AddonList.WriteOrdered(gameRoot, order, states) : AddonList.Write(gameRoot, states);
        }

        private bool ApplyQuiet()
        {
            Dictionary<string, bool> changes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            foreach (ListViewItem li in lv.Items) { changes[(string)li.Tag] = li.Checked; order.Add((string)li.Tag); }
            try
            {
                bool reordered = orderDirty;
                if (!WriteSwitches(gameRoot, order, changes, reordered))
                {
                    Core.Log("WARN", "开关没写进去");
                    MessageBox.Show("开关没写进去（addonlist.txt 写不动，可能被占用或权限不足）。\r\n\r\n窗口不会关闭，避免看起来已经保存成功。", "没写进去");
                    return false;
                }
                orderDirty = false;
                int on = 0;
                foreach (KeyValuePair<string, bool> kv in changes) if (kv.Value) on++;
                Core.Log("OK", "addonlist 已更新：" + on + " 个启用 / " + (changes.Count - on) + " 个关闭"
                    + (reordered ? "（顺序按窗口里的排列重写）" : "（顺序没动）")
                    + (AddonList.LastWroteWhileRunning ? "（写入时游戏正在运行）" : ""));

                // 回读确认：游戏开着的时候写得进去，但它也可能用自己的内存状态盖回来。
                // 对不上就把话说明白，不给「保存成功」的错觉。
                int mism = 0;
                try
                {
                    Dictionary<string, bool> after = AddonList.Read(gameRoot);
                    foreach (KeyValuePair<string, bool> kv in changes)
                    {
                        bool v;
                        if (!after.TryGetValue(kv.Key, out v) || v != kv.Value) mism++;
                    }
                }
                catch { }
                if (mism > 0)
                {
                    Core.Log("WARN", "回读不符 " + mism + " 条" + (AddonList.LastWroteWhileRunning ? "（写入时游戏正在运行）" : ""));
                    MessageBox.Show("写进去了，但回读有 " + mism + " 条对不上。\r\n\r\n"
                        + (AddonList.LastWroteWhileRunning
                            ? "写入时游戏正在运行，它可能用自己的开关状态把文件盖了回去。\r\n退出游戏后再改一次就稳了。"
                            : "可以试试以管理员身份运行，或先退出占用这个文件的程序。"),
                        "回读不一致", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                AddonList.ShowPluginNote();
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "addonlist 写入失败: " + ex.Message);
                MessageBox.Show("写入失败: " + ex.Message + "\r\n\r\n窗口不会关闭，避免看起来已经保存成功。", "写入失败");
                return false;
            }
        }

        private void Apply()
        {
            if (!ApplyQuiet()) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}