using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Installer
{
    public static class Work
    {
        /// <summary>
        /// 已装的东西怎么办。false（默认）= 一样就跳过，快；true = 全部重写一遍，修被改坏/被删一半的情况。
        /// 每个安装函数都要照顾这个开关，日志里也要说清楚"跳过了"还是"覆盖了"。
        /// </summary>
        public static bool Overwrite;

        /// <summary>
        /// 用户请求停止后续安装。复制在文件边界安全停下，已经写完的文件保留；
        /// 不在这里做回滚，界面必须明确告诉用户“停止不等于还原”。
        /// </summary>
        public static volatile bool CancelRequested;

        public static int SelfTest(string gameRootHint)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== L4D2 MOD 管理工具 自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
            sb.AppendLine("程序位置 : " + Core.Here);
            sb.AppendLine("U 盘根   : " + Core.UsbRoot);
            sb.AppendLine("工作模式 : " + (Core.IsLocal ? "本地模式（只扫游戏目录 addons）" : "U 盘模式（主库 + 附加目录）"));
            sb.AppendLine("MOD 主库 : " + Core.ModRoot);
            bool okPack;
            sb.AppendLine("--- 包检查 ---");
            foreach (string l in Core.CheckPackages(out okPack)) sb.AppendLine("  " + l);
            sb.AppendLine("包齐全   : " + (okPack ? "是" : "否"));
            sb.AppendLine("--- 找游戏 ---");
            List<string> games = Core.FindGames();
            if (games.Count == 0) sb.AppendLine("  没找到");
            foreach (string g in games) sb.AppendLine("  " + g);
            sb.AppendLine("--- MOD 库 ---");
            Core.ScanMods();
            long total = 0;
            foreach (ModItem it in Core.ModList) total += it.Size;
            sb.AppendLine("  合计 " + Core.ModList.Count + " 个 vpk, " + Core.Human(total));
            File.WriteAllText(Path.Combine(Core.LogDir, "selftest.txt"), sb.ToString(), new UTF8Encoding(true));
            return 0;
        }

        // ---------- 通用 ----------
        // 系统解压兜底：老系统 / 缺 .NET 4.5 时用资源管理器那套
        // 解压出来的第一层如果只是个壳目录（里面才是 bin），就往下钻一层
        private static string FindPayload(string dir, int depth)
        {
            if (depth > 3) return null;
            if (File.Exists(Path.Combine(dir, "bin\\left4neko.dll"))) return dir;
            try
            {
                foreach (string s in Directory.GetDirectories(dir))
                {
                    string r = FindPayload(s, depth + 1);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        // ---------- L4N ----------
        // ---------- dxvk ----------
        // ---------- Skeeto ----------
        /// <summary>
        /// skeeto.dll 这次是不是真的到位：本来没有 → 在就行；
        /// 本来就有（覆盖重装）→ 要么大小/时间变了，要么内容已经和包里那份一致。
        /// 只判「文件在不在」的话，安装器一次没写成也会被算成功（v2.51 就是这么误报的）。
        /// </summary>
        static bool SkeetoWritten(string dll, bool already, long baseLen, DateTime baseTime)
        {
            try
            {
                FileInfo f = new FileInfo(dll);
                if (!f.Exists) return false;
                if (!already) return true;
                if (f.Length != baseLen || f.LastWriteTimeUtc != baseTime) return true;
                return SkeetoPayloadMatches(dll);
            }
            catch { return false; }
        }

        static bool SkeetoPayloadMatches(string dll)
        {
            try
            {
                string payload = Path.Combine(Path.Combine(Core.PkgRoot, "Skeeto\\Skeeto"), "skeeto.dll");
                if (!File.Exists(payload)) return false;
                string a = Core.HashOf(dll), b = Core.HashOf(payload);
                return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---------- 素材包 ----------
        // ---------- autoexec.cfg ----------
        // ---------- MQCam ----------
        /// <summary>
        /// 按 payload.sha256 核对装到游戏里的文件（清单格式：SHA256 + TAB + 相对路径，路径用 /）。
        /// 配置文件（.ini）只要求存在：里面的值游戏/工具自己会改，按哈希比对必然误报。
        /// 返回 false 表示没有清单可读。
        /// </summary>
        private static bool VerifyPayload(string manifest, string gameRoot, out int total, out int bad, out string detail)
        {
            total = 0; bad = 0; detail = "";
            if (string.IsNullOrEmpty(manifest) || !File.Exists(manifest)) return false;
            List<string> missing = new List<string>();
            List<string> wrong = new List<string>();
            try
            {
                foreach (string raw in File.ReadAllLines(manifest))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] p = line.Split(new char[] { '\t', ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 2) continue;
                    string hash = p[0].Trim();
                    string rel = p[1].Trim().Replace('/', '\\');
                    if (hash.Length != 64 || rel.Length == 0) continue;
                    total++;
                    string dst = Path.Combine(gameRoot, rel);
                    if (!File.Exists(dst)) { bad++; missing.Add(rel); continue; }
                    if (rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string got = Core.HashOf(dst);
                    if (!string.Equals(got, hash, StringComparison.OrdinalIgnoreCase)) { bad++; wrong.Add(rel); }
                }
            }
            catch (Exception ex) { detail = "清单读不了：" + ex.Message; return false; }
            List<string> bits = new List<string>();
            if (missing.Count > 0) bits.Add("缺 " + string.Join("、", missing.ToArray()));
            if (wrong.Count > 0) bits.Add("内容不符 " + string.Join("、", wrong.ToArray()));
            detail = string.Join("；", bits.ToArray());
            return true;
        }

        // ---------- 校验 ----------
        /// <summary>
        /// 装机后的逐项核对。只核对「这次真的装了 / 勾了」的组件 —— 用户没勾 dxvk，
        /// 却在日志里打一行「[缺] d3d9.dll」，看起来像装坏了，实际什么都没丢。
        /// </summary>
        public static List<string> Verify(string gameRoot, List<string> components)
        {
            List<string> list = new List<string>();
            List<string> items = new List<string>();
            items.Add("left4dead2.exe");                     // 这个必须有，跟勾选无关
            HashSet<string> comps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (components != null) foreach (string c in components) if (!string.IsNullOrEmpty(c)) comps.Add(c.Trim());

            if (comps.Contains("L4N")) { items.Add("bin\\left4neko.dll"); items.Add("bin\\wininet.dll"); }
            if (comps.Contains("Skeeto")) items.Add("skeeto.dll");
            if (comps.Contains("Mqcam")) items.Add("bin\\neko\\mqcore\\mqcam.dll");
            if (comps.Contains("Dxvk")) { items.Add("d3d9.dll"); items.Add("dxvk.conf"); }

            foreach (string it in items)
            {
                bool ok = File.Exists(Path.Combine(gameRoot, it));
                list.Add((ok ? "[OK] " : "[缺] ") + it);
                Core.Log(ok ? "OK" : "WARN", (ok ? "[OK] " : "[缺] ") + it);
            }
            return list;
        }

        /// <summary>老调用方式：不知道装了什么，就按全套核对（只在排查时用）。</summary>
        public static List<string> Verify(string gameRoot)
        {
            return Verify(gameRoot, new List<string> { "L4N", "Skeeto", "Mqcam", "Dxvk" });
        }

        // ---------- 铺 MOD ----------
        public static long CopyMods(string gameRoot, List<ModItem> sel, Action<long, long> progress, out int copied, out int skipped, out List<string> added, out List<string> failed)
        {
            List<string> backups;
            return CopyMods(gameRoot, sel, progress, out copied, out skipped, out added, out failed, out backups);
        }

        /// <summary>
        /// 铺 MOD。目标位置已有同名文件时，先把它整份快照到 _装机记录\装机快照\&lt;事务&gt;\ 再覆盖，
        /// 这样「取消安装 / 一键还原」能回到用户原来那一版 —— 以前覆盖完只记了文件名，
        /// 还原时把文件删掉，用户自己的旧版本就永远没了。
        /// </summary>
        public static long CopyMods(string gameRoot, List<ModItem> sel, Action<long, long> progress,
            out int copied, out int skipped, out List<string> added, out List<string> failed, out List<string> backups)
        {
            int c = 0, s = 0;
            List<string> okList = new List<string>();
            List<string> badList = new List<string>();
            List<string> bakList = new List<string>();          // "文件名|装机快照\事务\文件名"
            string txn = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            if (!Directory.Exists(addons)) Directory.CreateDirectory(addons);

            long total = 0;
            foreach (ModItem it in sel) total += it.Size;
            long done = 0;
            List<ModItem> todo = new List<ModItem>();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem it in sel)
            {
                // 游戏目录里的本来就装着：不用复制，翻开关就行
                if (it.FromGame) { s++; done += it.Size; continue; }
                // 不同分类下可能有同名 vpk：只铺第一个，否则并行时两个线程会同时写同一个目标文件
                if (!used.Add(it.Name))
                {
                    s++; done += it.Size;
                    Core.Log("WARN", "同名跳过（本次已装第一个）: " + it.Name);
                    continue;
                }
                string d = Path.Combine(addons, it.Name);
                // 判「已装」只看同名 + 同字节大小，和列表「已装」列同一套判定。
                // 以前用 Core.SameFile（还比 mtime），从别处拷来的同名同大小文件会被当成没装、白拷一遍；
                // 已装的 MOD 只该动 addonlist 的开关，不该把文件重新搬一次。
                // 勾了「覆盖重装」才强制重写，那是修坏文件的路径。
                bool same = false;
                try { same = File.Exists(d) && new FileInfo(d).Length == it.Size; }
                catch { same = false; }
                if (same && !Overwrite) { s++; done += it.Size; }
                else todo.Add(it);
            }
            if (progress != null) progress(done, total);

            object gate = new object();
            int degree = DegreeFor(todo);
            long todoSize = 0;
            foreach (ModItem it in todo) todoSize += it.Size;
            Stopwatch sw = Stopwatch.StartNew();
            long lastReport = 0;
            if (todo.Count > 0)
            {
                System.Threading.Tasks.Parallel.ForEach(todo,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = degree },
                    it =>
                    {
                        if (CancelRequested)
                        {
                            bool reportCancel = false;
                            lock (gate)
                            {
                                s++;
                                done += it.Size;
                                long tnow = sw.ElapsedMilliseconds;
                                if (tnow - lastReport >= 120) { lastReport = tnow; reportCancel = true; }
                            }
                            if (reportCancel && progress != null) progress(done, total);
                            return;
                        }
                        string d = Path.Combine(addons, it.Name);
                        // 覆盖前先留一份旧版本；快照失败只是「这条回不去」，不拦着装
                        string bak = SnapshotAddon(addons, txn, it.Name);
                        string why;
                        bool good = it.IsArchive
                            ? ExtractOne(it, d, out why)
                            : Core.CopyOneVerified(it.FullPath, d, out why);
                        bool report = false;
                        lock (gate)
                        {
                            if (good) { c++; okList.Add(it.Name); }
                            else { badList.Add(it.Name); Core.Log("FAIL", "复制/校验失败 " + it.Name + "：" + why); }
                            // 只要动过这个目标（含写坏的情况）就记下来：还原时按快照回填
                            if (bak != null) bakList.Add(it.Name + "|" + bak);
                            done += it.Size;
                            long now = sw.ElapsedMilliseconds;
                            if (now - lastReport >= 120) { lastReport = now; report = true; }
                        }
                        if (report && progress != null) progress(done, total);   // 别每个文件都跨线程刷一次进度条
                    });
            }
            sw.Stop();
            if (progress != null) progress(done, total);
            double sec = sw.Elapsed.TotalSeconds;
            if (sec > 0.5 && todoSize > 0)
            {
                double rate = todoSize / 1048576.0 / sec;
                if (rate > 0.2) Core.LastRateMBps = Math.Round(rate, 1);
            }
            copied = c; skipped = s; added = okList; failed = badList; backups = bakList;
            if (bakList.Count > 0) Core.Log("INFO", "覆盖前已快照 " + bakList.Count + " 个旧文件到 _装机记录\\装机快照\\" + txn);
            string tail = "共 " + Core.Human(total) + "，" + Math.Round(sec, 1) + " 秒（" + Core.LastRateMBps + " MB/s）";
            Core.Log("OK", Overwrite
                ? ("MOD: 覆盖重写 " + c + " 个（并行 " + degree + " 路 + 大小校验），同名去重跳过 " + s + " 个，" + tail)
                : ("MOD: 新装 " + c + " 个（并行 " + degree + " 路 + 大小校验），已装跳过 " + s + " 个，" + tail));
            return total;
        }

        // ---------- 插件组件：bin\neko\plugins ----------
        // 有些 MOD 除了 vpk 还带一套 DLL（CFX 渲染框架 + 这把枪的插件），要落到
        // <游戏>\bin\neko\plugins 才显示得出来。缺一个 DLL 的结果是「手上空的」——不崩、不报错，
        // 所以这一段必须和 vpk 一样：写入前快照、写完记录、能回滚。

        /// <summary>一次插件安装的账：写进去哪些、跳过哪些、失败哪些、覆盖了哪些旧文件。</summary>
        public sealed class PluginResult
        {
            /// <summary>本次真正写进去的「独占」文件，相对游戏根的路径。还原时按它删。</summary>
            public List<string> Written = new List<string>();
            /// <summary>本次写进去的「共用框架」文件。还原时保留（别的插件还在用）。</summary>
            public List<string> SharedWritten = new List<string>();
            /// <summary>没动过的（已装的 / 框架版本不同的），只进日志。</summary>
            public List<string> Skipped = new List<string>();
            public List<string> Failed = new List<string>();
            /// <summary>覆盖前的旧版本快照，每项 "相对路径|装机快照\事务\plugins\相对路径"。</summary>
            public List<string> Backups = new List<string>();
        }

        /// <summary>
        /// 铺插件。落点 bin\neko\plugins，源目录里的相对结构原样保留。
        ///
        /// 三种情况的处理刻意不一样：
        ///   · 同名同大小       → 已装，跳过（和 vpk 那套判定一致，不重复搬）
        ///   · 同名不同大小     → 独占的覆盖（先快照，能回滚）；共用的保留现有版本并警告
        ///                        共用框架是多个 CFX 包一起用的，后装的静默降级别人就哑了
        ///   · 不存在           → 写入
        /// 覆盖重装（Overwrite=true）时，共用框架也照包里那份覆盖。
        /// </summary>
        public static long CopyPlugins(string gameRoot, List<PluginPack> packs, Action<long, long> progress, PluginResult r)
        {
            if (r == null) r = new PluginResult();
            if (packs == null || packs.Count == 0) return 0;

            string txn = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            long total = 0, done = 0;
            // 同一个插件目录可能被两个套装同时认出来（模型 vpk 和音效 vpk 被拆成两组），这里先去重
            List<PluginPack> uniq = new List<PluginPack>();
            HashSet<string> seenPack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginPack p in packs)
            {
                if (p == null || !seenPack.Add(p.Key)) continue;
                uniq.Add(p);
                total += p.Size;
            }
            if (progress != null) progress(0, total);

            int wrote = 0, skipped = 0, failed = 0, replug = 0;
            foreach (PluginPack p in uniq)
            {
                Core.Log("STEP", "铺插件「" + p.Name + "」" + PluginKit.Describe(p));
                foreach (PluginFile f in p.Files)
                {
                    if (CancelRequested) { done += f.Size; continue; }
                    string dst = Path.Combine(gameRoot, f.Rel);
                    if (!Core.IsUnder(gameRoot, dst))
                    {
                        failed++;
                        Core.Log("FAIL", "插件路径越界，已跳过: " + f.Rel);
                        done += f.Size;
                        continue;
                    }
                    long have = -1;
                    try { if (File.Exists(dst)) have = new FileInfo(dst).Length; }
                    catch { have = -1; }

                    if (have == f.Size) { skipped++; done += f.Size; continue; }   // 同名同大小 = 已装

                    if (have >= 0 && f.Shared && !Overwrite)
                    {
                        skipped++;
                        Core.Log("WARN", "共用框架已存在且版本不同，保留现有版本: " + f.Rel
                            + "（库里 " + Core.Human(f.Size) + "，现有 " + Core.Human(have)
                            + "；要按包里这份覆盖，勾「覆盖重装」）");
                        done += f.Size;
                        continue;
                    }

                    string bak = have >= 0 ? SnapshotPlugin(gameRoot, f.Rel, txn) : null;
                    string why;
                    // 源在压缩包里：按条目取出来写，不整包解压
                    bool ok;
                    if (f.Inner != null)
                    {
                        string pd = Path.GetDirectoryName(dst);
                        if (!string.IsNullOrEmpty(pd) && !Directory.Exists(pd)) Directory.CreateDirectory(pd);
                        ok = Catalog.ExtractEntryTo(f.Src, f.Inner, dst, out why);
                    }
                    else ok = Core.CopyOneVerified(f.Src, dst, out why);
                    if (ok)
                    {
                        wrote++;
                        if (f.Shared) r.SharedWritten.Add(f.Rel); else r.Written.Add(f.Rel);
                        if (bak != null) { replug++; r.Backups.Add(f.Rel + "|" + bak); }
                    }
                    else
                    {
                        failed++;
                        r.Failed.Add(f.Rel);
                        Core.Log("FAIL", "插件写入失败 " + f.Rel + "：" + why);
                    }
                    done += f.Size;
                    if (progress != null) progress(done, total);
                }
            }

            Core.Log(failed > 0 ? "WARN" : "OK",
                "插件: 写入 " + wrote + " 个文件（其中共用框架 " + r.SharedWritten.Count
                + " 个、覆盖旧版本 " + replug + " 个），已装跳过 " + skipped + " 个"
                + (failed > 0 ? "，失败 " + failed + " 个" : ""));
            return total;
        }

        // ---------- 插件对齐：按磁盘上的现状收敛，不记账 ----------
        // 「开哪几套」只有 addonlist 一份真相，bin 下的插件文件是它的影子。每次写完开关（或删完 vpk）
        // 就按当前开着的包把影子对齐一遍：该有的补回来、不该有的挪进停用区。
        // 不维护「哪些插件开着」的表 —— 表迟早和磁盘打架，而磁盘永远是对的；对齐是幂等的，跑两遍一样。

        /// <summary>停用区：关掉的 MOD 的独占插件挪到这儿，保持 bin\neko\plugins\... 的相对路径。</summary>
        public static string DisabledPluginDir()
        {
            return Path.Combine(Core.LogDir, "插件停用");
        }

        /// <summary>记录区里收下的插件：一个 vpk 一个文件夹，进去是 bin\neko\plugins\...。</summary>
        public static string PluginPackDir()
        {
            return Path.Combine(Core.LogDir, "插件包");
        }

        /// <summary>一次对齐的账。</summary>
        public sealed class SyncResult
        {
            public List<string> Added = new List<string>();      // 补进游戏目录的
            public List<string> Parked = new List<string>();     // 挪进停用区的
            public List<string> Missing = new List<string>();    // 该在、可包源和停用区都找不到的
            public bool GameRunning;                             // 游戏在跑，一个文件都没碰

            public int Changes { get { return Added.Count + Parked.Count; } }
        }

        /// <summary>
        /// 把 bin\neko\plugins 对齐到「这几套包开着」的状态。插件文件的唯一写入入口。
        ///
        /// 规则刻意不对称：
        ///   · 独占文件、目标内   → 缺就补（先找停用区，再找包源）
        ///   · 独占文件、目标外   → 游戏目录里有就挪进停用区（不删：再开回来要原位放回）
        ///   · 共用框架           → 只补缺的，已经在了就不动（版本不同也不覆盖，别的包还在用它）
        /// 游戏在跑时一个文件都不碰：游戏正读着这些 DLL，挪一半比不动更糟，开关本身已经写好了。
        /// </summary>
        public static int SyncPlugins(string gameRoot, List<PluginPack> packs, SyncResult r)
        {
            if (r == null) r = new SyncResult();
            if (string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(Core.LogDir)) return 0;
            try { gameRoot = Path.GetFullPath(gameRoot).TrimEnd('\\'); }
            catch { return 0; }

            if (AddonList.GameIsRunning())
            {
                r.GameRunning = true;
                Core.Log("INFO", "游戏正在运行，插件文件这次一个都不动（开关已写好，退出游戏后再开关一次就对齐）");
                return 0;
            }

            // 目标 = 开着的包里的独占文件
            HashSet<string> target = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<PluginPack> live = new List<PluginPack>();
            HashSet<string> seenPack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (packs != null)
                foreach (PluginPack p in packs)
                {
                    if (p == null || !seenPack.Add(p.Key)) continue;
                    live.Add(p);
                    foreach (PluginFile f in p.Files) if (!f.Shared) target.Add(f.Rel);
                }

            // 已知 = 记录区收下的全部包 + 这次传进来的包（装机时还没有记录）。
            // 只动认得的文件：用户自己塞进 bin 的别的插件不认识，一律不碰。
            Dictionary<string, PluginFile> known = new Dictionary<string, PluginFile>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginPack p in RecordedPluginPacks()) AddPluginFiles(known, p);
            foreach (PluginPack p in live) AddPluginFiles(known, p);
            if (known.Count == 0) return 0;

            string parkRoot = DisabledPluginDir();
            int added = 0, parked = 0, missing = 0;
            foreach (KeyValuePair<string, PluginFile> kv in known)
            {
                string rel = kv.Key;
                PluginFile f = kv.Value;
                string dst = Path.Combine(gameRoot, rel);
                bool onDisk = File.Exists(dst);

                if (f.Shared)
                {
                    if (onDisk) continue;
                    string w0;
                    if (CopyPluginFile(f, dst, out w0)) { added++; r.Added.Add(rel); }
                    else { missing++; r.Missing.Add(rel); Core.Log("WARN", "共用插件补不回来 " + rel + "：" + w0); }
                    continue;
                }

                if (target.Contains(rel))
                {
                    if (onDisk) continue;
                    string fromPark = Path.Combine(parkRoot, rel);
                    if (File.Exists(fromPark))
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dst));
                            File.Move(fromPark, dst);
                            added++;
                            r.Added.Add(rel);
                            continue;
                        }
                        catch (Exception ex) { Core.Log("WARN", "停用区挪回来失败 " + rel + "：" + ex.Message); }
                    }
                    string w1;
                    if (CopyPluginFile(f, dst, out w1)) { added++; r.Added.Add(rel); }
                    else { missing++; r.Missing.Add(rel); Core.Log("WARN", "独占插件补不回来 " + rel + "：" + w1); }
                }
                else if (onDisk)
                {
                    string to = Path.Combine(parkRoot, rel);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.Copy(dst, to, true);
                        File.Delete(dst);
                        parked++;
                        r.Parked.Add(rel);
                    }
                    catch (Exception ex) { Core.Log("WARN", "插件挪进停用区失败 " + rel + "：" + ex.Message); }
                }
            }

            if (parked > 0) PrunePluginDirs(gameRoot, r.Parked);
            if (added > 0) PruneParkDirs(parkRoot, r.Added);

            if (added + parked + missing > 0)
                Core.Log(missing > 0 ? "WARN" : "OK",
                    "插件对齐：就位 +" + added + "、停用 " + parked
                    + (missing > 0 ? "、找不到 " + missing : "")
                    + "（认得 " + known.Count + " 个插件文件）");
            return r.Changes;
        }

        /// <summary>
        /// 单个插件文件落盘：源在压缩包里就按条目抽出来，否则整份复制（和 CopyPlugins 同一套口径）。
        /// 记录区的源一定是磁盘文件，但 SyncPlugins 是公开入口，压缩包形态的包也可能传进来。
        /// </summary>
        private static bool CopyPluginFile(PluginFile f, string dst, out string why)
        {
            if (f.Inner == null) return Core.CopyOneVerified(f.Src, dst, out why);
            string pd = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(pd) && !Directory.Exists(pd)) Directory.CreateDirectory(pd);
            return Catalog.ExtractEntryTo(f.Src, f.Inner, dst, out why);
        }

        /// <summary>记录区里收下的所有插件包（一个 vpk 一个文件夹）。</summary>
        private static List<PluginPack> RecordedPluginPacks()
        {
            List<PluginPack> list = new List<PluginPack>();
            string root = PluginPackDir();
            if (!Directory.Exists(root)) return list;
            try
            {
                foreach (string d in Directory.GetDirectories(root))
                {
                    string dir = Path.Combine(Path.Combine(Path.Combine(d, "bin"), "neko"), "plugins");
                    if (!Directory.Exists(dir)) continue;
                    PluginPack p = PluginKit.Load(dir);
                    if (p != null) list.Add(p);
                }
            }
            catch (Exception ex) { Core.Log("WARN", "读记录区的插件包失败: " + ex.Message); }
            return list;
        }

        private static void AddPluginFiles(Dictionary<string, PluginFile> known, PluginPack p)
        {
            if (p == null) return;
            foreach (PluginFile f in p.Files)
                if (!known.ContainsKey(f.Rel)) known[f.Rel] = f;
        }

        /// <summary>从停用区把文件挪回去之后，把空掉的目录收掉（停用区自己留着）。</summary>
        private static void PruneParkDirs(string parkRoot, List<string> rels)
        {
            if (rels == null || rels.Count == 0) return;
            string root;
            try { root = Path.GetFullPath(parkRoot).TrimEnd('\\'); }
            catch { return; }
            foreach (string rel in rels)
            {
                try
                {
                    string d = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, rel)));
                    while (!string.IsNullOrEmpty(d) && d.Length > root.Length
                           && d.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Directory.Exists(d) || Directory.GetFileSystemEntries(d).Length > 0) break;
                        Directory.Delete(d);
                        d = Path.GetDirectoryName(d);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 挂载点：addonlist 刚写完（开关变了）、或刚删掉一个 vpk 时调一次。全程序只有这里算「现在开着哪些包」。
        /// 缺 L4N 时顺手留一句话给界面：插件写了却不会被加载，进游戏是空手，不弹出来没人查得出来。
        /// </summary>
        public static void SyncFromSwitches(string gameRoot)
        {
            AddonList.LastPluginNote = "";
            if (Core.SelfTesting) return;      // 自检跑的都是假目录，别把真记录区的插件往里铺
            if (string.IsNullOrEmpty(gameRoot)) return;
            try
            {
                SyncResult r = new SyncResult();
                SyncPlugins(gameRoot, Catalog.EnabledPluginPacks(gameRoot), r);
                if (r.GameRunning || r.Added.Count == 0) return;
                if (File.Exists(Path.Combine(gameRoot, "bin\\left4neko.dll"))) return;
                AddonList.LastPluginNote = "这次往 bin\\neko\\plugins 写了 " + r.Added.Count + " 个插件文件，"
                    + "但游戏目录里没有 bin\\left4neko.dll（L4N 加载器）。\r\n\r\n"
                    + "插件不会被加载，进游戏是空手 —— 不崩也不报错，所以先说清楚。\r\n"
                    + "把 L4N 的 bin\\left4neko.dll 补进游戏目录后，再开关一次这个 MOD 就会生效。";
            }
            catch (Exception ex)
            {
                // 插件对齐失败不能连累开关：addonlist 已经写成功了，这里只留日志
                Core.Log("WARN", "插件对齐没做完（开关已写好）: " + ex.Message);
            }
        }

        /// <summary>覆盖前把 bin 下的旧文件存进快照。返回相对 LogDir 的路径（失败返回 null）。</summary>
        private static string SnapshotPlugin(string gameRoot, string rel, string txn)
        {
            try
            {
                string src = Path.Combine(gameRoot, rel);
                if (!File.Exists(src)) return null;
                string root = Path.Combine(Path.Combine(Core.LogDir, "装机快照"), txn);
                string dst = Path.Combine(root, Path.Combine("plugins", rel));
                string dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(dst)) Core.CopyOne(src, dst);
                return Path.Combine(Path.Combine("装机快照", txn), Path.Combine("plugins", rel));
            }
            catch (Exception ex)
            {
                Core.Log("WARN", "插件覆盖前备份失败，这个文件回不去: " + rel + "，" + ex.Message);
                return null;
            }
        }

        /// <summary>把插件快照放回游戏目录（取消安装 / 一键还原时用）。清单可能被手改，只认装机快照下面的。</summary>
        public static int RestorePluginBackups(string gameRoot, List<string> backups)
        {
            return RestorePluginBackups(gameRoot, backups, null);
        }

        public static int RestorePluginBackups(string gameRoot, List<string> backups, List<string> report)
        {
            if (backups == null || backups.Count == 0) return 0;
            string baseDir = Path.GetFullPath(Path.Combine(Core.LogDir, "装机快照")).TrimEnd('\\') + "\\";
            int n = 0, miss = 0, fail = 0;
            foreach (string raw in backups)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                int bar = line.IndexOf('|');
                if (bar <= 0) continue;
                string rel = line.Substring(0, bar).Trim();
                string snap = line.Substring(bar + 1).Trim();
                if (rel.Length == 0 || rel.IndexOf("..", StringComparison.Ordinal) >= 0) continue;
                try
                {
                    string src = Path.GetFullPath(Path.Combine(Core.LogDir, snap));
                    if (!src.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) continue;
                    string dst = Path.Combine(gameRoot, rel);
                    if (!Core.IsUnder(gameRoot, dst)) continue;
                    if (!File.Exists(src)) { miss++; Core.Log("WARN", "插件旧版本快照不在了: " + snap); continue; }
                    Core.CopyOne(src, dst);
                    n++;
                    try { File.Delete(src); } catch { }
                }
                catch (Exception ex) { fail++; Core.Log("WARN", "插件回填失败 " + rel + ": " + ex.Message); }
            }
            if (n > 0) Core.Log("OK", "已把 " + n + " 个插件文件放回原版本");
            if (report != null)
            {
                if (miss > 0) report.Add("未完成：有 " + miss + " 个插件旧版本快照已被清理，没能回填");
                if (fail > 0) report.Add("未完成：有 " + fail + " 个插件旧版本回填失败（详见日志）");
            }
            return n;
        }

        /// <summary>取消安装时用：删掉本次写进去的插件文件，再把被覆盖的旧版本放回去。</summary>
        public static int RemovePluginFiles(string gameRoot, List<string> rels)
        {
            if (rels == null || rels.Count == 0) return 0;
            int n = 0;
            foreach (string rel in rels)
            {
                if (string.IsNullOrEmpty(rel) || rel.IndexOf("..", StringComparison.Ordinal) >= 0) continue;
                string p = Path.Combine(gameRoot, rel);
                if (!Core.IsUnder(gameRoot, p)) continue;
                try
                {
                    if (File.Exists(p)) { File.Delete(p); n++; }
                }
                catch (Exception ex) { Core.Log("WARN", "删除插件文件失败 " + rel + ": " + ex.Message); }
            }
            PrunePluginDirs(gameRoot, rels);
            return n;
        }

        /// <summary>
        /// 删完插件文件，把 bin\neko\plugins 下面空掉的目录收掉。
        /// 只删「比 plugins 更深」的空目录 —— plugins 本身和 CFX 这一层要留着，
        /// 那是框架的地盘，删了别的插件就没地方落了。
        /// </summary>
        public static void PrunePluginDirs(string gameRoot, List<string> rels)
        {
            if (rels == null || rels.Count == 0) return;
            string anchor = Path.GetFullPath(Path.Combine(gameRoot, PluginKit.Anchor)).TrimEnd('\\');
            HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rel in rels)
            {
                if (string.IsNullOrEmpty(rel)) continue;
                try
                {
                    string full = Path.GetFullPath(Path.Combine(gameRoot, rel));
                    string d = Path.GetDirectoryName(full);
                    while (!string.IsNullOrEmpty(d) && d.Length > anchor.Length
                           && d.StartsWith(anchor + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        dirs.Add(d);
                        d = Path.GetDirectoryName(d);
                    }
                }
                catch { }
            }
            // 深的先删，删完上一层才可能变空
            List<string> list = new List<string>(dirs);
            list.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (string d in list)
            {
                try
                {
                    if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d);
                }
                catch { }
            }
        }

        /// <summary>覆盖前把旧文件复制到 _装机记录\装机快照\&lt;事务&gt;\；返回相对 LogDir 的路径（失败返回 null）。</summary>
        private static string SnapshotAddon(string addons, string txn, string name)
        {
            try
            {
                string src = Path.Combine(addons, name);
                if (!File.Exists(src)) return null;
                string dir = Path.Combine(Path.Combine(Core.LogDir, "装机快照"), txn);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, name);
                if (!File.Exists(dst)) Core.CopyOne(src, dst);
                return Path.Combine(Path.Combine("装机快照", txn), name);
            }
            catch (Exception ex)
            {
                Core.Log("WARN", "覆盖前备份失败，这个文件将无法回滚: " + name + "，" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 把装机快照里的旧版本放回 addons（取消安装 / 一键还原时用）。
        /// 清单是纯文本，可能被手改：只接受落在 _装机记录\装机快照 下面的备份路径。
        /// </summary>
        public static int RestoreBackups(string gameRoot, List<string> backups, List<string> report)
        {
            if (backups == null || backups.Count == 0) return 0;
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            string baseDir = Path.GetFullPath(Path.Combine(Core.LogDir, "装机快照")).TrimEnd('\\') + "\\";
            int n = 0, miss = 0, fail = 0;
            foreach (string raw in backups)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                int bar = line.IndexOf('|');
                if (bar <= 0) continue;
                string name = line.Substring(0, bar).Trim();
                string rel = line.Substring(bar + 1).Trim();
                if (name.Length == 0 || name.IndexOfAny(new char[] { '\\', '/', ':' }) >= 0
                    || name.IndexOf("..", StringComparison.Ordinal) >= 0) { fail++; continue; }
                try
                {
                    string src = Path.GetFullPath(Path.Combine(Core.LogDir, rel));
                    if (!src.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) { fail++; continue; }
                    if (!File.Exists(src)) { miss++; Core.Log("WARN", "旧版本快照不在了: " + rel); continue; }
                    if (!Directory.Exists(addons)) Directory.CreateDirectory(addons);
                    Core.CopyOne(src, Path.Combine(addons, name));
                    n++;
                    try { File.Delete(src); } catch { }          // 回填成功就把快照清掉，别占着 U 盘
                }
                catch (Exception ex) { fail++; Core.Log("WARN", "回填旧版本 " + name + " 失败: " + ex.Message); }
            }
            SweepEmptySnapshotDirs();
            if (report != null)
            {
                if (n > 0) report.Add("已把 " + n + " 个被覆盖的旧版本放回 addons");
                if (miss > 0) report.Add("未完成：有 " + miss + " 个旧版本快照已被清理，没能回填");
                if (fail > 0) report.Add("未完成：有 " + fail + " 个旧版本回填失败（详见日志）");
            }
            return n;
        }

        /// <summary>这条 "文件名|快照相对路径" 记录对应的快照文件还在不在。</summary>
        private static bool SnapshotExists(string line)
        {
            try
            {
                string t = (line ?? "").Trim();
                int bar = t.IndexOf('|');
                if (bar <= 0) return false;
                return File.Exists(Path.Combine(Core.LogDir, t.Substring(bar + 1).Trim()));
            }
            catch { return false; }
        }

        /// <summary>回填完把空的 装机快照\&lt;事务&gt;\ 目录删掉，别留一堆空壳。</summary>
        private static void SweepEmptySnapshotDirs()
        {
            try
            {
                string root = Path.Combine(Core.LogDir, "装机快照");
                if (!Directory.Exists(root)) return;
                foreach (string d in Directory.GetDirectories(root))
                    try { if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d); } catch { }
            }
            catch { }
        }

        /// <summary>从压缩包里只取出这一个 vpk 放到 addons：不整包解压，也不动 U 盘上的包。</summary>
        public static bool ExtractOne(ModItem it, string dst, out string why)
        {
            // 解包实现只有一份，在 Catalog 里 —— 识别层和判重层也要用，不能再各写各的
            return Catalog.ExtractEntryTo(it.FullPath, it.Inner, dst, out why);
        }

        // U 盘上并行读会互相打断顺序读，反而更慢；网络共享才需要多路掩盖延迟
        private static int DegreeFor(List<ModItem> todo)
        {
            int cpu = Environment.ProcessorCount;
            DriveType t = DriveType.Fixed;
            try { if (todo.Count > 0) t = new DriveInfo(Path.GetPathRoot(todo[0].FullPath)).DriveType; }
            catch { }
            if (t == DriveType.Removable || t == DriveType.CDRom) return Math.Min(2, Math.Max(1, cpu));
            if (t == DriveType.Network) return Math.Min(4, Math.Max(2, cpu));
            return Math.Min(3, Math.Max(2, cpu));
        }

        // ---------- MOD 库维护 ----------
        /// <summary>从库里删掉一个 vpk（真删，不进回收目录）。返回 null = 成功，否则是失败原因。</summary>
        public static string DeleteMod(ModItem m)
        {
            // U 盘模式：游戏 addons 里的文件不归库管（那是已装的，不是库里的）。
            // 本地模式：addons 就是库，删它 = 从游戏里卸掉，允许。
            if (m.FromGame && !Core.IsLocal) return "这个 MOD 在游戏目录里，U 盘模式不动它";
            return DeleteModFile(m, m.FromGame ? Core.Cfg.GameRoot : null);
        }

        /// <summary>
        /// 把游戏 addons 里的一个 vpk 直接删掉（只有本地模式允许）。
        /// 「已装 MOD 开关」窗口用它做批量删除；addonlist 里那条登记一并摘掉。
        /// </summary>
        public static string DeleteGameMod(ModItem m, string gameRoot)
        {
            // 铁律：U 盘模式不动游戏目录里的文件（库里的那份才是母本）。
            // 这道校验以前只写在主界面的删除条目那一段，「已装 MOD 开关」窗口走的
            // 是本函数，绕过去了 —— U 盘模式下照样能真删游戏 addons 里的东西。
            if (!Core.IsLocal) return "U 盘模式不动游戏目录里的文件；要删请先切到本地模式";
            return DeleteModFile(m, gameRoot);
        }

        /// <summary>
        /// 同名伴随文件：vpk 旁边的 <名字>.jpg/.png 这类配图（工坊订阅就是图和包分开放的），
        /// 还有工具自己可能留的 .meta。删包、改名时一起带走，别留一地孤儿图。
        /// </summary>
        internal static List<string> Sides(string vpkPath)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(vpkPath)) return list;
            string dir = Path.GetDirectoryName(vpkPath);
            string stem = Path.GetFileNameWithoutExtension(vpkPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return list;
            string[] exts = new string[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".meta" };
            foreach (string e in exts)
            {
                string p = Path.Combine(dir, stem + e);
                try { if (File.Exists(p)) list.Add(p); } catch { }
            }
            return list;
        }

        static string DeleteModFile(ModItem m, string addonListRoot)
        {
            // 最后一道防线：压缩包里的条目 FullPath 指向的是那个 rar/zip 本身，
            // 删「一条」等于删整包（包里其它 MOD 一起没了）。界面上都拦过，这里再拦一次。
            // 工坊订阅的文件属于 Steam：不删、不搬、不改名。要清掉请在工坊里取消订阅。
            if (m.FromWorkshop) return "「" + m.Name + "」是 Steam 创意工坊订阅的内容，程序不删也不搬；去掉它请在创意工坊取消订阅";
            if (m.IsArchive) return "「" + m.Name + "」在压缩包里（" + m.Container + "），本工具不删压缩包";
            // 本地版唯一的库就是游戏 addons（调用方给了游戏根就以它为准）。
            // 路径不在这棵树里 —— 配置写坏、库被链接指到外面 —— 一律不删：
            // 删错就是删用户的文件，这里没有回收站可退。
            string gameForLib = addonListRoot;
            if (string.IsNullOrEmpty(gameForLib) && Core.Cfg != null) gameForLib = Core.Cfg.GameRoot;
            string libRoot = null;
            if (!string.IsNullOrEmpty(gameForLib))
            {
                try { libRoot = Path.Combine(Path.Combine(gameForLib, "left4dead2"), "addons"); }
                catch { }
            }
            if (string.IsNullOrEmpty(libRoot) || !Directory.Exists(libRoot))
                return "找不到游戏 addons 目录，删除已取消";
            Core.PathCheck safe = Core.ValidateUnder(libRoot, m.FullPath);
            if (!safe.Ok)
            {
                Core.Log("WARN", "拒绝删除：" + safe.Why);
                return "拒绝删除：" + safe.Why;
            }
            try { File.Delete(safe.Full); }
            catch (Exception ex)
            {
                Core.Log("WARN", "删除 " + m.FullPath + " 失败: " + ex.Message);
                return ex.Message;
            }
            Core.Log("OK", "已删除 " + m.FullPath);
            foreach (string side in Sides(m.FullPath))
            {
                try { File.Delete(side); Core.Log("OK", "已删除伴随文件 " + side); }
                catch (Exception ex) { Core.Log("WARN", "伴随文件删不掉 " + side + ": " + ex.Message); }
            }
            string registrationError = null;
            if (addonListRoot != null && addonListRoot.Length > 0)
            {
                try { AddonList.Remove(addonListRoot, new string[] { m.Name }); }
                catch (Exception ex)
                {
                    registrationError = "文件已删除，但 addonlist 清理失败：" + ex.Message;
                    Core.Log("WARN", "从 addonlist 摘掉 " + m.Name + " 失败: " + ex.Message);
                }
                // vpk 没了 = 这套不开了，插件跟着收敛。Remove 写过时已经对齐过一次，这里是幂等的
                // （没登记的手放 vpk 走不到上面那次写入）。
                try { SyncFromSwitches(addonListRoot); }
                catch (Exception ex)
                {
                    if (registrationError == null) registrationError = "文件已删除，但插件状态同步失败：" + ex.Message;
                    Core.Log("WARN", "删除后同步插件状态失败: " + ex.Message);
                }
            }
            return registrationError;
        }

        public static string RenameMod(ModItem m, string newName)
        {
            if (m.FromWorkshop) return "工坊订阅的内容由 Steam 管，程序不改名（要改名请先退订）";
            if (m.FromGame) return "这个 MOD 在游戏目录里，不能在这里改名";
            newName = Core.SafeName(newName);
            if (string.IsNullOrEmpty(newName)) return "名字不能为空";
            if (!newName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) newName = newName + ".vpk";
            string dst = Path.Combine(Path.GetDirectoryName(m.FullPath), newName);
            if (File.Exists(dst)) return "同目录已经有 " + newName;
            List<string> sides = Sides(m.FullPath);
            File.Move(m.FullPath, dst);
            string dstDir = Path.GetDirectoryName(dst);
            foreach (string side in sides)
            {
                try
                {
                    string target = Path.Combine(dstDir, Path.GetFileNameWithoutExtension(dst) + Path.GetExtension(side));
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(side, target);
                }
                catch (Exception ex) { Core.Log("WARN", "伴随文件跟着改名失败 " + side + ": " + ex.Message); }
            }
            return null;
        }

        /// <summary>清重计划：先算清楚要删哪些、留哪些，再让界面照着清单确认。</summary>
        public class DupPlan
        {
            /// <summary>内容与另一份完全一致，可以删。</summary>
            public List<ModItem> Drop = new List<ModItem>();
            /// <summary>每个内容组留下的那一份。</summary>
            public List<ModItem> Keep = new List<ModItem>();
            /// <summary>同名但内容不一样（或读不出来）：一律不动，只在清单里列出来。</summary>
            public List<ModItem> Differ = new List<ModItem>();
            public int ArchiveSkipped;
            public bool NothingToDo { get { return Drop.Count == 0 && Differ.Count == 0; } }
        }

        /// <summary>扫库并算出清重清单（不删任何东西）。</summary>
        public static DupPlan FindDuplicateMods()
        {
            Core.ScanMods();
            return PlanDuplicates(Core.ModList);
        }

        /// <summary>
        /// 按「同名 + 同大小 + 同内容」分组。
        /// 原来只按文件名分组、留第一个删其余 —— 两个来源的同名 vpk 内容根本不一样，
        /// 照样被当重复删掉一份，而且留哪份只看扫描顺序。
        ///
        /// 现在的内容判定和「重复嫌疑」窗口共用同一个指纹函数（DupVerify.OfPath，CRC32），
        /// 不再是一套 SHA256、一套 CRC32 —— 两边结论不一样，用户会以为哪个在骗他。
        /// 顺序：同名 → 体积必须一致（不一致一定不是同一个文件，连读都不用读）→ CRC32 → 分桶 → 删前 SHA256 复核。
        /// </summary>
        public static DupPlan PlanDuplicates(List<ModItem> items)
        {
            DupPlan plan = new DupPlan();
            Dictionary<string, List<ModItem>> byName = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in items)
            {
                if (m.FromGame) continue;   // 游戏目录里的不参与清重：它和库里同名的那份是「已装的一份」
                // 压缩包里的条目共用同一个容器文件：删掉一条 = 删掉整个包，包里其它 MOD 全陪葬。
                if (m.IsArchive) { plan.ArchiveSkipped++; continue; }
                if (!byName.ContainsKey(m.Name)) byName[m.Name] = new List<ModItem>();
                byName[m.Name].Add(m);
            }

            foreach (KeyValuePair<string, List<ModItem>> kv in byName)
            {
                if (kv.Value.Count < 2) continue;

                // 体积不同的同名文件：不可能同内容，直接列为「内容不同，自己看着办」
                Dictionary<long, List<ModItem>> bySize = new Dictionary<long, List<ModItem>>();
                foreach (ModItem m in kv.Value)
                {
                    if (!bySize.ContainsKey(m.Size)) bySize[m.Size] = new List<ModItem>();
                    bySize[m.Size].Add(m);
                }

                List<ModItem> unknown = new List<ModItem>();
                List<List<ModItem>> kinds = new List<List<ModItem>>();
                foreach (List<ModItem> sameSize in bySize.Values)
                {
                    if (sameSize.Count < 2) { kinds.Add(sameSize); plan.Keep.Add(sameSize[0]); continue; }   // 只有一份这种体积，单独算一种
                    Dictionary<string, List<ModItem>> byCrc = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
                    foreach (ModItem m in sameSize)
                    {
                        string why;
                        string fp = DupVerify.OfPath(m.FullPath, out why);
                        if (string.IsNullOrEmpty(fp)) { unknown.Add(m); continue; }   // 读不出来就不碰
                        if (!byCrc.ContainsKey(fp)) byCrc[fp] = new List<ModItem>();
                        byCrc[fp].Add(m);
                    }
                    foreach (List<ModItem> g in byCrc.Values)
                    {
                        kinds.Add(g);
                        plan.Keep.Add(g[0]);          // 每种内容留一份，Keep 就是这份
                        if (g.Count < 2) continue;
                        // CRC32 只有 32 位。分组用它（快），真要删之前用 SHA256 复核一遍：
                        // 万一撞上碰撞，宁可留着让用户自己看，也不能删错文件。
                        string keepSha = Core.HashOf(g[0].FullPath);
                        for (int i = 1; i < g.Count; i++)
                        {
                            string sha = keepSha == null ? null : Core.HashOf(g[i].FullPath);
                            if (sha == null || !string.Equals(sha, keepSha, StringComparison.OrdinalIgnoreCase))
                            {
                                unknown.Add(g[i]);
                                Core.Log("WARN", "清重：CRC 相同但 SHA256 不符（或复核读不出），未删除 —— " + g[i].Name);
                                continue;
                            }
                            plan.Drop.Add(g[i]);
                        }
                    }
                }

                if (kinds.Count > 1)
                {
                    // 同名的几种内容都留着，让人自己决定删哪个
                    foreach (List<ModItem> g in kinds) plan.Differ.Add(g[0]);
                    Core.Log("WARN", "清重：同名但内容不同，未删除 —— " + kv.Key + "（" + kinds.Count + " 种内容）");
                }
                if (unknown.Count > 0)
                {
                    plan.Differ.AddRange(unknown);
                    Core.Log("WARN", "清重：同名文件读不出来（可能被占用），未删除 —— " + kv.Key);
                }
            }
            return plan;
        }

        /// <summary>按清单真删。返回删掉的条目（给人的一句话说明）。</summary>
        public static List<string> DeleteDuplicates(DupPlan plan, List<string> failed)
        {
            List<string> done = new List<string>();
            List<ModItem> gone = new List<ModItem>();
            foreach (ModItem m in plan.Drop)
            {
                try
                {
                    string err = DeleteMod(m);
                    if (err != null) throw new Exception(err);
                    done.Add(m.Name + "  (" + m.Cat + " · " + m.Sub + ")");
                    gone.Add(m);
                }
                catch (Exception ex)
                {
                    if (failed != null) failed.Add(m.Name + "：" + ex.Message);
                    Core.Log("WARN", "清理 " + m.Name + " 失败: " + ex.Message);
                }
            }
            // 文件已经删掉，直接把它从内存列表里摘掉，不再花几秒重扫全库
            foreach (ModItem g in gone) { Core.ModList.Remove(g); Core.ByRel.Remove(g.Key); }
            return done;
        }

        // ---------- 换整套：只翻 addonlist 的开关，文件一个都不动 ----------
        /// <summary>
        /// 按方案把 addonlist 翻一遍：这一套打开、上一套关上。
        /// 不动 addons 里的文件 —— 切回来只需把开关翻回去，不用重新复制。
        /// 只认 U 盘库里存在的 vpk；用户自己丢进 addons 的不碰。
        /// </summary>
        public static void SwitchAddons(string gameRoot, List<ModItem> sel, string assetName,
                                        out int on, out int off, out int untouched)
        {
            on = 0; off = 0; untouched = 0;
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sel != null)
                foreach (ModItem m in sel)
                {
                    if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                    if (!want.Add(m.Name)) continue;
                    st[m.Name] = true;
                }
            if (!string.IsNullOrEmpty(assetName) && want.Add(assetName)) st[assetName] = true;

            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Core.ModList != null)
                foreach (ModItem m in Core.ModList) if (!string.IsNullOrEmpty(m.Name)) known.Add(m.Name);

            // 平台包是别人的前置：换方案时一律不许关 —— 关掉它，挂在上面的 mod 会静默失效，
            // 玩家只会看到「表情没了 / 挂件没了」，查不出是换方案干的。
            HashSet<string> platform = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Core.ModList != null)
                foreach (ModItem m in Core.ModList)
                    if (m != null && !string.IsNullOrEmpty(m.Name) && m.Cat == Catalog.CAT_PLATFORM) platform.Add(m.Name);

            if (Directory.Exists(addons))
                foreach (string f in Directory.GetFiles(addons, "*.vpk"))
                {
                    string n = Path.GetFileName(f);
                    if (want.Contains(n)) continue;
                    if (!known.Contains(n)) { untouched++; continue; }   // 不认识的：不是我们装的，别管
                    if (platform.Contains(n)) { untouched++; continue; }  // 平台：永远不关
                    st[n] = false;
                }

            if (st.Count == 0) return;
            AddonList.Write(gameRoot, st);
            foreach (KeyValuePair<string, bool> kv in st) { if (kv.Value) on++; else off++; }
            Core.Log("OK", "整套开关：打开 " + on + " 个、关闭 " + off + " 个"
                + (untouched > 0 ? "、不认识的 " + untouched + " 个没动" : "")
                + "（addons 里的文件一个都没动，切回来直接开）");
        }

        // ---------- 回收新 MOD ----------
        /// <summary>
        /// 把游戏 addons 里「U 盘库没有」的 vpk 收进 U 盘 addons回收。
        /// deleteSource=true 时，复制并核对大小之后再把游戏里那份删掉 —— 只复制不删的旧行为
        /// 叫「收回」名不副实，用户以为腾出了空间，其实游戏盘一个字节没少。
        /// </summary>
        public static List<string> CollectNewMods(string gameRoot, bool deleteSource,
            Action<int, int, string> progress, List<string> kept, List<string> failed)
        {
            List<string> got = new List<string>();
            // 本地模式没有 U 盘库：ModRoot 只是程序所在盘算出来的默认库路径，
            // 它不参与本地模式扫描。往那儿「收」等于把文件搬进一个没人看得见的目录。
            if (Core.IsLocal) return got;
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            if (!Directory.Exists(addons)) return got;
            Core.ScanMods();
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in Core.ModList) known.Add(m.Name);
            string dst = Path.Combine(Core.ModRoot, "addons回收");

            List<string> todo = new List<string>();
            foreach (string f in Directory.GetFiles(addons, "*.vpk"))
            {
                string name = Path.GetFileName(f);
                if (known.Contains(name)) continue;
                todo.Add(f);
            }

            int i = 0;
            foreach (string f in todo)
            {
                string name = Path.GetFileName(f);
                i++;
                if (progress != null) progress(i, todo.Count, name);
                try
                {
                    if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);
                    string target = Path.Combine(dst, name);
                    if (Core.SameFile(f, target))
                    {
                        // 库里已经有同一份：不重复复制，但「删源」仍然要按用户的选择做
                        if (deleteSource) { File.Delete(f); }
                        kept.Add(name);
                        continue;
                    }
                    Core.CopyOne(f, target);
                    // CopyOne 已经校验过（默认比大小，严格模式比 SHA256）；这里再确认目标真的在
                    if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(f).Length)
                        throw new IOException("复制后大小不一致");
                    got.Add(name);
                    if (deleteSource)
                    {
                        File.Delete(f);
                        Core.Log("OK", "已收回并删除游戏里的: " + name);
                    }
                    else Core.Log("OK", "已复制到 U 盘（游戏里的保留）: " + name);
                }
                catch (Exception ex)
                {
                    failed.Add(name);
                    Core.Log("WARN", "回收 " + name + " 失败: " + ex.Message);
                }
            }
            return got;
        }

        // ---------- 彻底还原 ----------
        public static List<string> FullRestore(string gameRoot)
        {
            List<string> report = new List<string>();

            string mf = Path.Combine(Core.LogDir, "最近一次.ini");
            List<ManifestSection> sections = ReadManifest(mf);
            ManifestSection mine = SectionFor(sections, gameRoot, false);

            // 只删「装机记录里确实装过的」平台组件。以前是照着安装包清单无条件删，
            // 用户自己放的 d3d9.dll / vpk 也会被带走；现在先看记录，老记录没有组件信息才退回包清单（有备份兜底）。
            List<string> targets = new List<string>();
            // 本版本写的记录一定带 Schema；没有 Schema 行的就是 ≤2.58.1 留下的老记录。
            // 光看「Platform 为空」分不出这两种，会新记录当老记录处理、照包清单乱删。
            // 注意这里比的是 SchemaPlatform（2），不是当前的 ManifestSchema（3）：
            // 2.6x 写的记录已经带组件明细，升到 3 加了插件明细，但组件那条语义没变。
            bool precise = mine != null && mine.Schema >= SchemaPlatform;
            bool recorded = precise && mine.Platform.Count > 0;
            if (recorded)
            {
                foreach (string comp in mine.Platform)
                {
                    string c = (comp ?? "").Trim();
                    if (string.Equals(c, "L4N", StringComparison.OrdinalIgnoreCase))
                        targets.AddRange(L4NFiles());
                    else if (string.Equals(c, "Dxvk", StringComparison.OrdinalIgnoreCase))
                    {
                        targets.Add("d3d9.dll");
                        targets.Add("dxvk.conf");
                    }
                    else if (string.Equals(c, "Skeeto", StringComparison.OrdinalIgnoreCase))
                        targets.AddRange(PackageFiles(Path.Combine(Core.PkgRoot, "Skeeto\\Skeeto")));
                    else if (string.Equals(c, "Asset", StringComparison.OrdinalIgnoreCase))
                    {
                        string vpk = Core.FirstFile(Path.Combine(Core.PkgRoot, "Skeeto\\素材包"), "*.vpk");
                        if (vpk != null) targets.Add("left4dead2\\addons\\" + Path.GetFileName(vpk));
                    }
                    else if (string.Equals(c, "Mqcam", StringComparison.OrdinalIgnoreCase))
                        targets.Add("bin\\neko\\mqcore\\mqcam.dll");
                }
            }
            else if (mine != null && !precise)
            {
                // 老记录：有本目录的清单，但没有 Schema/Platform 字段（升级前装的）。
                // 分不清哪些组件是本工具装的，只能按包清单清理；删前逐个备份，备份失败就保留。
                targets.AddRange(L4NFiles());
                targets.AddRange(PackageFiles(Path.Combine(Core.PkgRoot, "Skeeto\\Skeeto")));
                string vpk = Core.FirstFile(Path.Combine(Core.PkgRoot, "Skeeto\\素材包"), "*.vpk");
                if (vpk != null) targets.Add("left4dead2\\addons\\" + Path.GetFileName(vpk));
                targets.Add("d3d9.dll");
                targets.Add("dxvk.conf");
                targets.Add("bin\\neko\\mqcore\\mqcam.dll");
            }
            else if (mine == null && File.Exists(mf))
                Core.Log("WARN", "装机记录属于另一个游戏目录，跳过平台文件删除: " + mf);
            // 新记录且 Platform 为空 = 本次装机确实没勾任何平台组件，平台文件一个都不动。
            // 注意：mqcam.ini / mqcam_state.ini 是用户自己调的相机参数，不在还原删除范围里。

            // 插件文件：只有 Schema>=3 才有这条明细。2 及以下的记录压根没装过插件，
            // bin 里的东西都是用户自己放的，一个都不许碰。
            List<string> plugDeleted = new List<string>();
            if (mine != null && mine.Schema >= ManifestSchema)
            {
                // 共用框架不在这里（当初就没记），所以删的都是「这次装的这个包独占的」
                foreach (string rel in mine.Plugin) targets.Add(rel);
            }

            HashSet<string> uniq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 删掉的那些 vpk 名：还要从 addonlist 里摘掉登记。
            // 不摘的话下次装回同名 MOD，会继承上次那条「0 = 关闭」，装上却玩不到。
            List<string> goneAddons = new List<string>();
            // 平台文件是按包清单删的，分不清“工具装的”和“用户自己放的”。
            // 删之前先整份备份；备份失败就保留不删，宁可还原不彻底，也不能把用户文件弄丢。
            string backupRoot = Path.Combine(Core.LogDir, "还原备份", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            int del = 0, backed = 0, backupFail = 0, delFail = 0;
            bool stopped = false;
            List<string> targetFailed = new List<string>();
            foreach (string rel in targets)
            {
                if (CancelRequested) { stopped = true; break; }
                if (string.IsNullOrEmpty(rel)) continue;
                string key = rel.TrimStart('\\');
                if (!uniq.Add(key)) continue;
                // 清单来自安装包/压缩包，可能带 ../ ：越界的路径一律不删
                string full = Path.Combine(gameRoot, key);
                if (!Core.IsUnder(gameRoot, full)) { Core.Log("WARN", "包清单里有越界路径，已跳过: " + key); continue; }
                try
                {
                    if (File.Exists(full))
                    {
                        if (!BackupBeforeDelete(full, key, backupRoot))
                        {
                            backupFail++;
                            targetFailed.Add(key + "（备份失败，保留未删）");
                            Core.Log("WARN", "备份失败，保留未删: " + key);
                            continue;
                        }
                        backed++;
                        File.Delete(full);
                        del++;
                        if (key.StartsWith(PluginKit.Anchor + "\\", StringComparison.OrdinalIgnoreCase))
                            plugDeleted.Add(key);
                    }
                    if (key.StartsWith("left4dead2\\addons\\", StringComparison.OrdinalIgnoreCase))
                        goneAddons.Add(Path.GetFileName(key));
                }
                catch (Exception ex) { delFail++; targetFailed.Add(key + "（" + ex.Message + "）"); Core.Log("WARN", "删除 " + key + " 失败: " + ex.Message); }
            }
            // 插件是跟平台文件同一个删除循环里走的，报数时得把它从「平台文件」里扣掉，
            // 否则一行「删掉平台文件 2 个」会让用户以为删了两个平台组件。
            report.Add("删掉平台文件 " + (del - plugDeleted.Count) + " 个"
                + (recorded ? "（按装机记录里的组件）"
                   : (mine == null ? "（没有本目录的装机记录，平台文件未动）"
                      : (precise ? "（记录里本次没有装平台组件）" : "（老记录没有组件信息，按安装包清单）")))
                + (delFail > 0 ? "，失败 " + delFail + " 个（详见日志）" : ""));
            if (backed > 0) report.Add("删除前已备份 " + backed + " 个文件到 " + backupRoot);
            if (backupFail > 0) report.Add("有 " + backupFail + " 个文件备份失败，已保留未删");
            if (plugDeleted.Count > 0)
            {
                PrunePluginDirs(gameRoot, plugDeleted);
                report.Add("删掉插件文件 " + plugDeleted.Count + " 个（bin\\neko\\plugins 下）"
                    + "；共用框架按设计保留（别的插件还在用）");
            }

            foreach (string d in new string[] { "bin\\neko\\mqcore", "bin\\neko", "Skeeto", "skeeto_kbact", "skeeto_update" })
            {
                try
                {
                    string full = Path.Combine(gameRoot, d);
                    if (Directory.Exists(full) && Directory.GetFileSystemEntries(full).Length == 0) Directory.Delete(full);
                }
                catch { }
            }

            List<string> added = new List<string>();
            if (mine != null) added.AddRange(mine.Added);
            else if (File.Exists(mf))
                Core.Log("WARN", "装机记录属于另一个游戏目录，跳过 MOD 删除: " + mf);

            string l4d2 = Path.Combine(gameRoot, "left4dead2");
            string addons = Path.Combine(l4d2, "addons");
            int moved = 0, bad = 0, failed = 0, addonIndex = 0;
            List<string> kept = new List<string>();
            foreach (string a in added)
            {
                if (CancelRequested) { stopped = true; break; }
                addonIndex++;
                // 装机记录是纯文本，可能被手改过：只认「直接躺在 addons 下的单个文件名」。
                // 带目录分隔符 / 盘符 / .. 的一律不删 —— 否则记录里写一行 ..\..\ 就能删到游戏目录外。
                string name = (a ?? "").Trim();
                if (name.Length == 0) continue;
                if (name.IndexOfAny(new char[] { '\\', '/', ':' }) >= 0 || name.IndexOf("..", StringComparison.Ordinal) >= 0)
                {
                    bad++;
                    Core.Log("WARN", "装机记录里有可疑路径，已忽略: " + name);
                    continue;
                }
                string p = Path.Combine(addons, name);
                if (!Core.IsUnder(addons, p)) { bad++; Core.Log("WARN", "装机记录里的路径越界，已忽略: " + name); continue; }
                try
                {
                    if (File.Exists(p)) { File.Delete(p); moved++; }
                    goneAddons.Add(name);
                }
                catch (Exception ex) { failed++; kept.Add(name); Core.Log("WARN", "删除 " + name + " 失败: " + ex.Message); }
            }
            if (stopped)
                for (int i = addonIndex; i < added.Count; i++) kept.Add(added[i]);

            // 删完本次装的，再把「安装前被覆盖的旧版本」放回去 —— 这是覆盖安装真正的回滚。
            // 不放回的话，一键还原会把用户自己原来那一版一起带走。
            // 快照分两种落点：addons 下按文件名回填，bin\neko\plugins 下按相对路径回填，
            // 混在一起走 RestoreBackups 会把插件那几条当成「可疑路径」丢掉。
            List<string> addonBak = new List<string>();
            List<string> plugBak = new List<string>();
            if (mine != null)
            {
                foreach (string line in mine.Backups)
                {
                    int bar = (line ?? "").IndexOf('|');
                    string nm = bar > 0 ? line.Substring(0, bar).Trim() : (line ?? "").Trim();
                    if (nm.IndexOfAny(new char[] { '\\', '/' }) >= 0) plugBak.Add(line); else addonBak.Add(line);
                }
            }
            int restored = mine == null ? 0 : RestoreBackups(gameRoot, addonBak, report);
            int plugRestored = mine == null ? 0 : RestorePluginBackups(gameRoot, plugBak, report);
            if (plugRestored > 0) report.Add("已把 " + plugRestored + " 个被覆盖的插件文件放回原版本");

            // 记录里已经删掉/本来就不存在的条目也一并摘掉，免得下次手放同名文件被误删。
            bool recordSaveFailed = false;
            if (mine != null)
            {
                mine.Added = kept;
                // 已经删掉的插件文件从记录里摘掉；没删掉的（被占用 / 权限不足）留着，下次还原还能再试。
                mine.Plugin.RemoveAll(delegate(string rel) { return !File.Exists(Path.Combine(gameRoot, rel)); });
                // 回填成功的那几条快照文件已经被删掉了，顺手从记录里摘掉；
                // 没回填成功的留着，下次还原还能再试一次。
                mine.Backups.RemoveAll(delegate(string line) { return !SnapshotExists(line); });
                // 平台文件都处理干净了才清组件记录；有删不掉/备份失败的留着，下次还原还能重试。
                if (!stopped && backupFail == 0 && delFail == 0)
                {
                    mine.Platform.Clear();
                    // 老记录这次已经按包清单清过一遍了，升级成明记录：下次还原不再「不知道装了什么就照单全删」。
                    mine.Schema = ManifestSchema;
                }
                try { SaveManifest(mf, sections); }
                catch (Exception ex) { recordSaveFailed = true; Core.Log("WARN", "更新装机记录失败: " + ex.Message); }
            }
            // 删掉的文件在 addonlist 里留了一条指向空气的开关：游戏读不到文件不会报错，
            // 但同一名字下次装回来时，这条旧的 0 会把它默默关掉。既然文件是我们删的，登记一起摘。
            bool addonListFailed = false;
            if (goneAddons.Count > 0)
            {
                try
                {
                    int cleared = AddonList.Remove(gameRoot, goneAddons);
                    if (cleared > 0) report.Add("addonlist 里指向这些文件的登记一并摘掉 " + cleared + " 条");
                }
                catch (Exception ex) { addonListFailed = true; Core.Log("WARN", "还原时清理 addonlist 失败: " + ex.Message); }
            }
            if (added.Count > 0)
                report.Add("装机记录里的 MOD 已删除 " + moved + " 个"
                    + (failed > 0 ? "，失败 " + failed + " 个（详见日志）" : "")
                    + (bad > 0 ? "，跳过可疑记录 " + bad + " 条" : ""));
            else
                report.Add(mine != null
                    ? "装机记录里没有 MOD 文件（这次只装了插件/组件）"
                    : "没有找到属于当前游戏目录的 MOD 装机记录，MOD 文件未动");
            report.Add("原版 exe / addonlist / autoexec 没有备份可还原，需要原版请用 Steam 校验文件");
            List<string> unfinished = new List<string>();
            if (stopped) unfinished.Add("已按用户请求停止，未处理项目已保留在装机记录中");
            if (targetFailed.Count > 0) unfinished.Add("平台文件未处理 " + targetFailed.Count + " 个：" + string.Join("、", targetFailed.ToArray()));
            if (kept.Count > 0) unfinished.Add("MOD 未删除 " + kept.Count + " 个：" + string.Join("、", kept.ToArray()));
            if (recordSaveFailed) unfinished.Add("装机记录更新失败，下次启动可能无法准确识别剩余项目");
            if (addonListFailed) unfinished.Add("addonlist 清理失败，可能残留已删除文件的开关");
            int plugLeft = 0;
            if (mine != null) foreach (string line in mine.Backups) if (SnapshotExists(line)) plugLeft++;
            if (plugLeft > 0) unfinished.Add("旧版本快照仍有 " + plugLeft + " 个未回填");
            if (bad > 0) unfinished.Add("跳过可疑记录 " + bad + " 条");
            if (unfinished.Count > 0) report.Add("未完成：" + string.Join("；", unfinished.ToArray()));
            return report;
        }

        /// <summary>
        /// 还原前把要删的文件复制到 _装机记录\还原备份\时间戳\ 下。
        /// 只做大小校验：还原是用户明确点的，备份失败就保留原文件，不拿“哈希算不出”当理由硬删。
        /// </summary>
        private static bool BackupBeforeDelete(string src, string rel, string backupRoot)
        {
            try
            {
                string dst = Path.Combine(backupRoot, (rel ?? "").TrimStart('\\'));
                string dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(src, dst, true);
                return new FileInfo(dst).Length == new FileInfo(src).Length;
            }
            catch (Exception ex)
            {
                Core.Log("WARN", "还原备份失败 " + rel + ": " + ex.Message);
                return false;
            }
        }

        private static List<string> PackageFiles(string pkgDir)
        {
            List<string> rels = new List<string>();
            try
            {
                if (!Directory.Exists(pkgDir)) return rels;
                foreach (string f in Directory.GetFiles(pkgDir, "*", SearchOption.AllDirectories))
                    rels.Add(f.Substring(pkgDir.Length).TrimStart('\\'));
            }
            catch { }
            return rels;
        }

        private static List<string> L4NFiles()
        {
            List<string> rels = new List<string>();
            string zip = Core.FirstFile(Core.PkgRoot, "L4N_v*.zip");
            try
            {
                if (zip != null)
                {
                    using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.OpenRead(zip))
                    {
                        Dictionary<string, bool> names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        foreach (System.IO.Compression.ZipArchiveEntry e in za.Entries) names[e.FullName] = true;
                        string prefix = null;
                        foreach (string n in names.Keys)
                        {
                            if (n.EndsWith("/bin/left4neko.dll", StringComparison.OrdinalIgnoreCase)) { prefix = n.Substring(0, n.Length - "bin/left4neko.dll".Length); break; }
                            if (string.Equals(n, "bin/left4neko.dll", StringComparison.OrdinalIgnoreCase)) { prefix = ""; break; }
                        }
                        if (prefix == null) return rels;
                        foreach (string n in names.Keys)
                        {
                            if (n.EndsWith("/")) continue;
                            if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                            rels.Add(n.Substring(prefix.Length).Replace('/', '\\'));
                        }
                    }
                }
                else rels.AddRange(PackageFiles(Path.Combine(Core.PkgRoot, "L4N平台\\L4N")));
            }
            catch (Exception ex) { Core.Log("WARN", "读 L4N 包清单失败: " + ex.Message); }
            return rels;
        }
        // ---------- 批量导入：把散落的 vpk / 压缩包收进库并按名字归类 --------
        // move=true 时，复制校验通过后删掉源文件（"收进库"，不占双份体积）
        public static List<string> ImportVpks(string sourceDir, bool move, out int imported, out int skipped)
        {
            imported = 0; skipped = 0;
            List<string> log = new List<string>();
            if (!Directory.Exists(sourceDir)) return log;
            Core.ScanMods();
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in Core.ModList) known.Add(m.Name);
            // 库里已有的压缩包（按文件名去重）
            HashSet<string> knownArc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string a in Directory.GetFiles(Core.ModRoot, "*", SearchOption.AllDirectories))
                {
                    string e = Path.GetExtension(a).ToLowerInvariant();
                    if (e == ".rar" || e == ".zip" || e == ".7z") knownArc.Add(Path.GetFileName(a));
                }
            }
            catch { }

            foreach (string f in Directory.GetFiles(sourceDir, "*.vpk", SearchOption.AllDirectories))
            {
                if (CancelRequested) { log.Add("已停止导入，后续文件未处理"); return log; }
                string name = Path.GetFileName(f);
                // 插件先收：move=true 会把源 vpk 删掉，删完就再也找不到旁边的 bin 了。
                // 「库里已有、跳过」的那条也要收 —— vpk 在库、插件没记录，是最常见的一次性漏。
                CollectPlugins(f, log);
                if (CancelRequested) { log.Add("已停止导入，后续文件未处理"); return log; }
                if (known.Contains(name)) { skipped++; continue; }
                string relDir = ClassifyByName(name);
                if (PutIntoLib(f, name, relDir, move, log)) { imported++; known.Add(name); }
            }

            // 7z 也要收：识别、装机、冲突检查都支持 7z 了，只有这里还只扫 rar/zip，
            // 结果「批量导入」对着一个 7z 文件夹一个字都不收，也不说为什么。
            foreach (string pat in new string[] { "*.rar", "*.zip", "*.7z" })
            {
                foreach (string f in Directory.GetFiles(sourceDir, pat, SearchOption.AllDirectories))
                {
                    if (CancelRequested) { log.Add("已停止导入，后续文件未处理"); return log; }
                    string name = Path.GetFileName(f);
                    if (knownArc.Contains(name)) { skipped++; continue; }
                    string relDir = ClassifyArchive(f, name);
                    if (PutIntoLib(f, name, relDir, move, log)) { imported++; knownArc.Add(name); }
                }
            }
            return log;
        }

        /// <summary>
        /// 把 vpk 旁边那套插件收进记录区：&lt;记录&gt;\插件包\&lt;vpk 名&gt;\bin\neko\plugins\...。
        /// 本地版的 vpk 进了 addons 之后就只剩一个文件（旁边的 bin 不会跟着进），
        /// 插件只能趁导入这一下收下来，否则以后开关这个 MOD 没处找插件。
        /// 带校验、按大小跳过已收过的 —— 重复导入同一批不会越积越多。
        /// </summary>
        private static void CollectPlugins(string vpkPath, List<string> log)
        {
            PluginPack p = PluginKit.Find(vpkPath);
            if (p == null) return;
            string name = Path.GetFileNameWithoutExtension(vpkPath);
            string dstRoot = Path.Combine(PluginPackDir(), name);
            int n = 0, failed = 0;
            foreach (PluginFile f in p.Files)
            {
                if (CancelRequested) { log.Add("已停止导入，插件 " + name + " 的后续文件未处理"); return; }
                if (f.Inner != null) continue;      // Find 找出来的源一定在磁盘上
                string dst = Path.Combine(dstRoot, f.Rel);
                try
                {
                    if (File.Exists(dst) && new FileInfo(dst).Length == f.Size) continue;   // 已经收过
                }
                catch { }
                string why;
                if (Core.CopyOneVerified(f.Src, dst, out why)) n++;
                else { failed++; log.Add("插件没复制进去 " + f.Rel + "：" + why); }
            }
            if (n > 0) log.Add("收插件 " + n + " 个 → 插件包\\" + name);
            if (failed > 0) Core.Log("WARN", "收插件有 " + failed + " 个文件失败（" + Path.GetFileName(vpkPath) + "）");
        }

        // 复制进库（带校验），move=true 时删源文件
        private static bool PutIntoLib(string srcPath, string name, string relDir, bool move, List<string> log)
        {
            try
            {
                string dstDir = Path.Combine(Core.ModRoot, relDir);
                Directory.CreateDirectory(dstDir);
                string dst = Path.Combine(dstDir, name);
                if (File.Exists(dst)) { log.Add("已存在，跳过 " + relDir + "\\" + name); return false; }
                string why;
                if (!Core.CopyOneVerified(srcPath, dst, out why)) { log.Add("失败 " + name + "：" + why); return false; }
                if (move)
                {
                    try { File.Delete(srcPath); }
                    catch (Exception ex) { log.Add("已复制但源文件删不掉（" + name + "）：" + ex.Message); }
                }
                bool isArc = IsArchiveName(name);
                if (!isArc) AddToMem(dst);
                log.Add((isArc ? "入库（压缩包）" : "入库") + relDir + "\\" + name);
                return true;
            }
            catch (Exception ex) { log.Add("失败 " + name + "：" + ex.Message); return false; }
        }

        private static bool IsArchiveName(string name)
        {
            string e = Path.GetExtension(name).ToLowerInvariant();
            return e == ".rar" || e == ".zip" || e == ".7z";
        }

        // 压缩包先按包名分类；分类落进「杂项」且包里只有一个 vpk 时，改用包里那个 vpk 的名字再猜一次
        public static string ClassifyArchive(string path, string name)
        {
            string rel = ClassifyByName(name);
            if (rel.IndexOf("杂项功能") < 0) return rel;
            try
            {
                string err;
                List<ArcEntry> list = Catalog.ListArchive(path, out err);
                if (list != null && list.Count > 0 && !string.IsNullOrEmpty(list[0].Inner))
                {
                    string inner = ClassifyByName(Path.GetFileName(list[0].Inner));
                    if (inner.IndexOf("杂项功能") < 0) return inner;
                }
            }
            catch { }
            return rel;
        }

        // 新入库的文件直接补进内存列表，省掉一次全库重扫
        private static void AddToMem(string fullPath)
        {
            try
            {
                ModItem it = new ModItem();
                it.Name = Path.GetFileName(fullPath);
                it.FullPath = fullPath;
                it.RelPath = fullPath.Substring(Core.ModRoot.Length).TrimStart('\\');
                it.Key = it.RelPath;
                string rd = Core.RelDir(it.RelPath);
                it.Size = new FileInfo(fullPath).Length;
                Catalog.Classify(it);
                Core.ModList.Add(it);
                Core.ByRel[it.Key] = it;
                if (rd.Length > 0)
                {
                    Core.BumpDir(rd);
                    int bs = rd.IndexOf('\\');
                    if (bs > 0) Core.BumpDir(rd.Substring(0, bs));
                }
            }
            catch { }
        }

        // 按文件名猜它该放哪个文件夹（只猜常见枪械，猜不到进「杂项功能」）
        public static string ClassifyByName(string name)
        {
            string n = name.ToLowerInvariant();
            string[][] map = new string[][]
            {
                new string[] { "ak47", "AK47" },
                new string[] { "mp5", "MP5" },
                new string[] { "m16", "M16" },
                new string[] { "scar", "scar" },
                new string[] { "awp", "AWP" },
                new string[] { "鸟狙", "鸟狙" }, new string[] { "scout", "鸟狙" },
                new string[] { "木喷", "木喷" },
                new string[] { "铁喷", "铁喷" },
                new string[] { "一代连喷", "一代连喷" }, new string[] { "二代连喷", "二代连喷" },
                new string[] { "15连", "15发连狙" }, new string[] { "30连", "30发连狙" },
                new string[] { "sg552", "sg552" }, new string[] { "uzi", "uzi" }, new string[] { "mac-10", "MAC-10" },
                new string[] { "m60", "M60" },
                new string[] { "改视角", "改视角" },
                new string[] { "火光", "火光消除" },
                new string[] { "药", "医疗物品\\药" }, new string[] { "针", "医疗物品\\针" }, new string[] { "医疗", "医疗物品\\包" },
                new string[] { "喷漆", "addons喷漆扩展" }, new string[] { "喷图", "addons喷漆扩展" },
                new string[] { "地图", "addons三方地图" }, new string[] { "战役", "addons三方地图" }
            };
            foreach (string[] kv in map) if (n.Contains(kv[0])) return kv[1].StartsWith("addons") ? kv[1] : ("addons各类物品储藏库\\" + kv[1]);
            return "addons各类物品储藏库\\杂项功能";
        }

        // 脚本库冲突已并入 ConflictCheck.DeepAll：同一批 vpk 只解析一遍

        // ---------- 装机记录 ----------
        // 一份记录里可以有多段：每段对应一个游戏目录。以前只写最后一段，第二次装机就把
        // 第一次的记录覆盖了，「一键还原」只能删最后一批。现在按 GameRoot 分段合并。
        /// <summary>
        /// 装机记录格式版本。0 = 老记录（没有 Schema 行，也分不清平台组件装没装），
        /// 2 = 带 Platform= 明细的新记录。靠它把「一个新记录，本次没勾平台组件」和
        /// 「一条老记录，压根不知道装了什么」分清楚 —— 否则前者会被当成后者，还原时
        /// 照着安装包清单把用户自己放的 d3d9.dll / dxvk.conf 也带走。
        /// 3 = 再带 Plugin= 明细（bin\neko\plugins 下的文件）。2 及以下绝不能碰 bin 下的东西：
        /// 那会儿还不支持插件，bin 里的东西一律是用户自己放的。
        /// </summary>
        private const int ManifestSchema = 3;

        /// <summary>组件明细（Platform=）从这一版开始有。别跟着 ManifestSchema 一起改。</summary>
        private const int SchemaPlatform = 2;

        private sealed class ManifestSection
        {
            public string Root = "";
            public string Time = "";
            public int Schema;                     // 0 = 老记录；ManifestSchema = 新记录
            public List<string> Added = new List<string>();
            /// <summary>
            /// 这次实际装成功的平台组件（L4N / Dxvk / Skeeto / Asset / Mqcam）。
            /// 还原时只按这里删，不再无条件照包清单删一遍 —— 免得把用户自己放的 d3d9.dll / vpk 也删了。
            /// Schema &gt;= 2 时「列表为空」就是「一个都没装」；Schema == 0 时才退回按包清单删（有备份兜底）。
            /// </summary>
            public List<string> Platform = new List<string>();
            /// <summary>
            /// 覆盖安装前留下的旧版本快照，每项 "文件名|装机快照\事务\文件名"。
            /// 还原 / 取消安装时按这里把用户原来的那一版放回 addons。
            /// </summary>
            public List<string> Backups = new List<string>();
            /// <summary>
            /// 本次写进 bin\neko\plugins 的「独占」插件文件，相对游戏根的路径。
            /// 共用框架（CFX\Framework 这些）不记 —— 还原时本来就要留着。
            /// Schema &lt; 3 的段落这个列表恒为空，还原时不许碰 bin。
            /// </summary>
            public List<string> Plugin = new List<string>();
        }

        private static string NormRoot(string root)
        {
            string s = (root ?? "").Trim().Trim('"');
            if (s.Length == 0) return "";
            try { s = Path.GetFullPath(s); } catch { }
            return s.TrimEnd('\\');
        }

        private static bool SameRoot(string a, string b)
        {
            return string.Equals(NormRoot(a), NormRoot(b), StringComparison.OrdinalIgnoreCase);
        }

        private static List<ManifestSection> ReadManifest(string path)
        {
            List<ManifestSection> list = new List<ManifestSection>();
            try
            {
                if (!File.Exists(path)) return list;
                ManifestSection cur = null;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string t = (raw ?? "").Trim();
                    if (t.Length == 0) continue;
                    if (t.StartsWith("GameRoot=", StringComparison.OrdinalIgnoreCase))
                    {
                        cur = new ManifestSection();
                        cur.Root = t.Substring("GameRoot=".Length).Trim();
                        if (NormRoot(cur.Root).Length > 0) list.Add(cur);
                        continue;
                    }
                    if (cur == null) continue;
                    if (t.StartsWith("Schema=", StringComparison.OrdinalIgnoreCase))
                    {
                        int sc;
                        if (int.TryParse(t.Substring("Schema=".Length).Trim(), out sc)) cur.Schema = sc;
                    }
                    else if (t.StartsWith("Time=", StringComparison.OrdinalIgnoreCase))
                        cur.Time = t.Substring("Time=".Length).Trim();
                    else if (t.StartsWith("Platform=", StringComparison.OrdinalIgnoreCase))
                    {
                        string comp = t.Substring("Platform=".Length).Trim();
                        if (comp.Length > 0) cur.Platform.Add(comp);
                    }
                    else if (t.StartsWith("Added=", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = Path.GetFileName(t.Substring("Added=".Length).Trim());
                        if (name.Length > 0) cur.Added.Add(name);
                    }
                    else if (t.StartsWith("Backup=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = t.Substring("Backup=".Length).Trim();
                        if (v.Length > 0) cur.Backups.Add(v);
                    }
                    else if (t.StartsWith("Plugin=", StringComparison.OrdinalIgnoreCase))
                    {
                        // 插件落点是相对游戏根的路径（bin\neko\plugins\...），不能再按文件名截断
                        string v = t.Substring("Plugin=".Length).Trim().TrimStart('\\');
                        if (v.Length > 0 && v.IndexOf("..", StringComparison.Ordinal) < 0) cur.Plugin.Add(v);
                    }
                }
            }
            catch (Exception ex) { Core.Log("WARN", "读装机记录失败: " + ex.Message); }
            return list;
        }

        private static ManifestSection SectionFor(List<ManifestSection> sections, string root, bool create)
        {
            ManifestSection hit = null;
            for (int i = sections.Count - 1; i >= 0; i--)
                if (SameRoot(sections[i].Root, root)) { hit = sections[i]; break; }
            if (hit == null && create)
            {
                hit = new ManifestSection();
                hit.Root = NormRoot(root);
                hit.Schema = ManifestSchema;    // 本版本新建的段落自带组件明细语义
                sections.Add(hit);
            }
            return hit;
        }

        private static void SaveManifest(string path, List<ManifestSection> sections)
        {
            // 旧文件可能有重复段（手改/并发写），写回前按根目录合并一遍。
            List<ManifestSection> merged = new List<ManifestSection>();
            foreach (ManifestSection s in sections)
            {
                if (s == null || NormRoot(s.Root).Length == 0) continue;
                // 这里不能用 SectionFor(create:true)：它会顺手把新段标成 Schema=2，
                // 合并老记录时就等于偷偷升了级，还原会以为「平台组件一个都没装」。
                ManifestSection m = null;
                for (int i = merged.Count - 1; i >= 0; i--)
                    if (SameRoot(merged[i].Root, s.Root)) { m = merged[i]; break; }
                if (m == null)
                {
                    m = new ManifestSection();
                    m.Root = NormRoot(s.Root);
                    merged.Add(m);
                }
                if (s.Schema > m.Schema) m.Schema = s.Schema;
                if (!string.IsNullOrEmpty(s.Time)) m.Time = s.Time;
                HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string a in m.Added) have.Add(a);
                foreach (string a in s.Added)
                    if (!string.IsNullOrEmpty(a) && have.Add(a)) m.Added.Add(a);
                HashSet<string> haveP = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string c in m.Platform) haveP.Add(c);
                foreach (string c in s.Platform)
                    if (!string.IsNullOrEmpty(c) && haveP.Add(c)) m.Platform.Add(c);
                HashSet<string> haveB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string b in m.Backups) haveB.Add(b);
                foreach (string b in s.Backups)
                    if (!string.IsNullOrEmpty(b) && haveB.Add(b)) m.Backups.Add(b);
                HashSet<string> haveG = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string g in m.Plugin) haveG.Add(g);
                foreach (string g in s.Plugin)
                    if (!string.IsNullOrEmpty(g) && haveG.Add(g)) m.Plugin.Add(g);
            }

            StringBuilder sb = new StringBuilder();
            foreach (ManifestSection s in merged)
            {
                sb.AppendLine("GameRoot=" + s.Root);
                // Schema 显式写出来：老记录保持 0，还原时才敢退回「按包清单」这条老路。
                sb.AppendLine("Schema=" + s.Schema);
                sb.AppendLine("Time=" + (string.IsNullOrEmpty(s.Time)
                    ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : s.Time));
                foreach (string c in s.Platform) sb.AppendLine("Platform=" + c);
                foreach (string b in s.Backups) sb.AppendLine("Backup=" + b);
                foreach (string g in s.Plugin) sb.AppendLine("Plugin=" + g);
                foreach (string a in s.Added) sb.AppendLine("Added=" + a);
                sb.AppendLine();
            }
            Core.WriteAllTextAtomic(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static void WriteManifest(string gameRoot, List<string> added)
        {
            WriteManifest(gameRoot, added, null);
        }

        public static void WriteManifest(string gameRoot, List<string> added, List<string> platform)
        {
            WriteManifest(gameRoot, added, platform, null);
        }

        /// <summary>
        /// 写装机记录。backups 是「覆盖前快照」，每项 "文件名|装机快照\事务\文件名"；
        /// 传 null 表示本次没有覆盖任何已有文件（不是「清空历史」）。
        /// </summary>
        public static void WriteManifest(string gameRoot, List<string> added, List<string> platform, List<string> backups)
        {
            WriteManifest(gameRoot, added, platform, backups, null);
        }

        /// <summary>
        /// 写装机记录。plugins 是本次写进 bin\neko\plugins 的独占文件（相对游戏根的路径）；
        /// 传 null 表示本次没动插件，不是「清空历史」。
        /// </summary>
        public static void WriteManifest(string gameRoot, List<string> added, List<string> platform, List<string> backups, List<string> plugins)
        {
            try
            {
                string path = Path.Combine(Core.LogDir, "最近一次.ini");
                List<ManifestSection> sections = ReadManifest(path);
                ManifestSection sec = SectionFor(sections, gameRoot, true);
                sec.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                // 这次真的记了插件，就得把这一段升到能表达插件的版本；
                // 否则还原时会按老记录处理，插件文件永远删不掉。
                if (plugins != null && plugins.Count > 0 && sec.Schema < ManifestSchema) sec.Schema = ManifestSchema;

                HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string n in sec.Added) have.Add(n);
                if (added != null)
                {
                    foreach (string a in added)
                    {
                        string name = Path.GetFileName((a ?? "").Trim());
                        // 只记文件名：这份记录后面会被「一键还原」逐行拿来删文件，不能写进任何带路径的东西
                        if (name.Length > 0 && have.Add(name)) sec.Added.Add(name);
                    }
                }
                HashSet<string> haveP = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string c in sec.Platform) haveP.Add(c);
                if (platform != null)
                {
                    foreach (string c in platform)
                    {
                        string comp = (c ?? "").Trim();
                        if (comp.Length > 0 && haveP.Add(comp)) sec.Platform.Add(comp);
                    }
                }
                HashSet<string> haveB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string b in sec.Backups) haveB.Add(b);
                if (backups != null)
                {
                    foreach (string b in backups)
                    {
                        string line = (b ?? "").Trim();
                        if (line.Length > 0 && haveB.Add(line)) sec.Backups.Add(line);
                    }
                }
                HashSet<string> haveG = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string g in sec.Plugin) haveG.Add(g);
                if (plugins != null)
                {
                    foreach (string g in plugins)
                    {
                        string rel = (g ?? "").Trim().TrimStart('\\');
                        // 记录会被逐行拿来删文件：带 .. 的一律不收
                        if (rel.Length > 0 && rel.IndexOf("..", StringComparison.Ordinal) < 0 && haveG.Add(rel))
                            sec.Plugin.Add(rel);
                    }
                }
                SaveManifest(path, sections);
            }
            catch (Exception ex) { Core.Log("WARN", "装机记录写失败: " + ex.Message); }
        }

        /// <summary>从当前游戏目录的记录里摘掉这些文件名（取消安装后删掉了本次复制的文件时用）。</summary>
        public static void ForgetManifest(string gameRoot, List<string> names)
        {
            try
            {
                if (names == null || names.Count == 0) return;
                string path = Path.Combine(Core.LogDir, "最近一次.ini");
                if (!File.Exists(path)) return;
                List<ManifestSection> sections = ReadManifest(path);
                ManifestSection sec = SectionFor(sections, gameRoot, false);
                if (sec == null) return;
                HashSet<string> drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string n in names)
                {
                    string name = Path.GetFileName((n ?? "").Trim());
                    if (name.Length > 0) drop.Add(name);
                }
                sec.Added.RemoveAll(delegate(string s) { return drop.Contains(s); });
                // 回填成功的快照文件已经删掉了，对应记录跟着清；没回填成功的留着，下次还能再试
                if (sec.Backups.Count > 0)
                    sec.Backups.RemoveAll(delegate(string line) { return !SnapshotExists(line); });
                SaveManifest(path, sections);
            }
            catch (Exception ex) { Core.Log("WARN", "更新装机记录失败: " + ex.Message); }
        }

        /// <summary>
        /// 取消安装时用：删掉本次已经完整复制到 addons 的文件，返回真正清掉（或本来就不在）的名字。
        /// 只认直接躺在 addons 下的单个文件名，路径越界/带分隔符的一律不碰。
        /// </summary>
        public static List<string> RemoveAddedFiles(string gameRoot, List<string> names)
        {
            List<string> cleared = new List<string>();
            if (names == null || names.Count == 0) return cleared;
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            List<string> gone = new List<string>();
            foreach (string a in names)
            {
                string name = (a ?? "").Trim();
                if (name.Length == 0) continue;
                if (name.IndexOfAny(new char[] { '\\', '/', ':' }) >= 0 || name.IndexOf("..", StringComparison.Ordinal) >= 0) continue;
                string p = Path.Combine(addons, name);
                if (!Core.IsUnder(addons, p)) continue;
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                    cleared.Add(name);
                    gone.Add(name);
                }
                catch (Exception ex) { Core.Log("WARN", "删除本次复制文件失败 " + name + ": " + ex.Message); }
            }
            if (gone.Count > 0)
            {
                try { AddonList.Remove(gameRoot, gone); }
                catch (Exception ex) { Core.Log("WARN", "取消安装后清理 addonlist 失败: " + ex.Message); }
            }
            return cleared;
        }

        /// <summary>
        /// --plugtry=&lt;目录&gt;：拿真实库里的插件套装，往一个（假的）游戏目录里完整走一遍
        /// 「识别 → 落盘 → 写装机记录 → 一键还原」，最后核对独占文件删干净、共用框架还在。
        ///
        /// 只在自检里用。写成一条命令而不是散在 UI 里，是因为这条链最容易在
        /// 「记录格式 / 还原分支」上出错，而 UI 那层只是把它们串起来。
        /// </summary>
        public static int PluginRoundTrip(string gameRoot)
        {
            int bad = 0;
            if (string.IsNullOrEmpty(gameRoot)) { Console.WriteLine("PLUGTRY 缺少目标目录"); return 1; }
            gameRoot = Path.GetFullPath(gameRoot);
            Console.WriteLine("PLUGTRY 目标: " + gameRoot);

            string savedGame = Core.Cfg.GameRoot;
            string savedLog = Core.LogDir;
            try
            {
                Directory.CreateDirectory(Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons"));
                Directory.CreateDirectory(Path.Combine(gameRoot, "bin"));
                File.WriteAllText(Path.Combine(gameRoot, "bin\\left4neko.dll"), "fake loader");
                Core.Cfg.GameRoot = gameRoot;
                Core.LogDir = Path.Combine(gameRoot, "_装机记录");
                Directory.CreateDirectory(Core.LogDir);

                Core.ScanMods();
                List<PluginPack> packs = new List<PluginPack>();
                foreach (ModGroup g in Catalog.Groups)
                {
                    if (g.Plugin == null) continue;
                    packs.Add(g.Plugin);
                    Console.WriteLine("PLUGTRY 命中「" + g.Name + "」" + PluginKit.Describe(g.Plugin)
                        + " 独占: " + g.Plugin.OwnedList(6));
                }
                if (packs.Count == 0)
                {
                    Console.WriteLine("PLUGTRY 库里没有带插件的套装 —— 先把带 bin\\neko\\plugins 的包放进库");
                    return 1;
                }

                PluginResult pr = new PluginResult();
                CopyPlugins(gameRoot, packs, null, pr);
                Console.WriteLine("PLUGTRY 写入 独占=" + pr.Written.Count + " 共用=" + pr.SharedWritten.Count
                    + " 跳过=" + pr.Skipped.Count + " 失败=" + pr.Failed.Count);

                bool allThere = true;
                foreach (string rel in pr.Written)
                    if (!File.Exists(Path.Combine(gameRoot, rel))) { allThere = false; Console.WriteLine("PLUGTRY 缺文件: " + rel); }
                if (!allThere) bad++;
                if (pr.Written.Count == 0) { bad++; Console.WriteLine("PLUGTRY 一个独占文件都没写进去"); }

                WriteManifest(gameRoot, null, null, null, pr.Written);
                string mf = Path.Combine(Core.LogDir, "最近一次.ini");
                string text = File.Exists(mf) ? File.ReadAllText(mf, Encoding.UTF8) : "";
                bool hasSchema = text.Contains("Schema=" + ManifestSchema);
                bool hasPlugin = text.Contains("Plugin=");
                Console.WriteLine("PLUGTRY 装机记录: Schema=" + hasSchema + "，Plugin 行=" + hasPlugin);
                if (!hasSchema || !hasPlugin) bad++;

                Console.WriteLine("PLUGTRY --- 一键还原 ---");
                foreach (string l in FullRestore(gameRoot)) Console.WriteLine("PLUGTRY " + l);

                bool ownedGone = true;
                foreach (string rel in pr.Written)
                    if (File.Exists(Path.Combine(gameRoot, rel))) { ownedGone = false; Console.WriteLine("PLUGTRY 还原后还在: " + rel); }
                bool sharedKept = true;
                foreach (string rel in pr.SharedWritten)
                    if (!File.Exists(Path.Combine(gameRoot, rel))) { sharedKept = false; Console.WriteLine("PLUGTRY 共用框架被删了: " + rel); }
                if (!ownedGone) bad++;
                if (!sharedKept) bad++;
                Console.WriteLine("PLUGTRY 结果: 独占已删=" + ownedGone + "，共用保留=" + sharedKept);
            }
            catch (Exception ex)
            {
                Console.WriteLine("PLUGTRY 异常: " + ex.Message);
                bad++;
            }
            finally
            {
                Core.Cfg.GameRoot = savedGame;
                Core.LogDir = savedLog;
                PluginKit.ClearCache();
            }
            Console.WriteLine("plugtry failures = " + bad);
            return bad;
        }

        // ---------- 自测：写/删的底线（在真正放数据的那个卷上跑） ----------        /// <summary>
        /// --safeself：验证「写要整份落地、删不能删整包、路径不能越界」这三条。
        /// 特意建在 _装机记录 下面 —— 那是装 MOD 的同一个卷（U 盘多半是 exFAT），
        /// 换名/覆盖的行为跟系统盘 NTFS 不一样，只有在真盘上跑才算数。
        /// </summary>
        public static int SelfTestSafe()
        {
            int bad = 0;
            string dir = Path.Combine(Core.LogDir, "safeself-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Console.WriteLine("SAFE 测试目录: " + dir);
            try
            {
                Directory.CreateDirectory(dir);
                byte[] data = new byte[512 * 1024];
                for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);
                string src = Path.Combine(dir, "src.bin");
                File.WriteAllBytes(src, data);

                // 0) 写盘互斥：同一时间只允许一件事在跑，释放必须按名字
                SafeCheck(ref bad, OpGuard.Enter("selftest-A"), "互斥：第一个操作能拿到写盘权");
                SafeCheck(ref bad, !OpGuard.Enter("selftest-B"), "互斥：已有操作在跑时，第二个必须被拒");
                OpGuard.Exit("selftest-B");                 // 名字不对：不能把 A 的锁放掉
                SafeCheck(ref bad, !OpGuard.Enter("selftest-B"), "互斥：别人的 Exit 不能误放行");
                OpGuard.Exit("selftest-A");
                SafeCheck(ref bad, OpGuard.Enter("selftest-B"), "互斥：正确释放后能再进入");
                OpGuard.Exit("selftest-B");

                // 1) 覆盖写：目标已存在也要整份换掉，且不留 .part
                string dst = Path.Combine(dir, "a.vpk");
                File.WriteAllBytes(dst, new byte[] { 1, 2, 3 });
                string h;
                Core.CopyOneHash(src, dst, out h);
                SafeCheck(ref bad, new FileInfo(dst).Length == data.Length, "覆盖写后大小正确");
                SafeCheck(ref bad, string.Equals(Core.HashOf(dst), h, StringComparison.OrdinalIgnoreCase), "覆盖写后内容哈希一致");

                // 2) 目标被占用：必须报错（不能假装成功），旧文件一个字节都不许动
                File.WriteAllBytes(dst, new byte[] { 9, 9 });
                bool threw = false;
                using (FileStream hold = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { Core.CopyOneHash(src, dst, out h); }
                    catch { threw = true; }
                }
                SafeCheck(ref bad, threw, "目标被占用时复制会失败");
                SafeCheck(ref bad, new FileInfo(dst).Length == 2, "复制失败后旧文件原样保留");

                // 3) 从 zip 里取一个条目
                string arc = Path.Combine(dir, "pack.zip");
                using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.Open(arc, System.IO.Compression.ZipArchiveMode.Create))
                {
                    System.IO.Compression.ZipArchiveEntry e = za.CreateEntry("ak47.vpk");
                    using (Stream s = e.Open()) s.Write(data, 0, 4096);
                }
                string ex = Path.Combine(dir, "ex.vpk");
                string why;
                bool got = Catalog.ExtractEntryTo(arc, "ak47.vpk", ex, out why);
                SafeCheck(ref bad, got && new FileInfo(ex).Length == 4096, "zip 条目解出成功且大小正确" + (got ? "" : "：" + why));

                // 4) 压缩包里的条目：绝不能删整包
                ModItem it = new ModItem();
                it.Name = "ak47.vpk";
                it.FullPath = arc;
                it.Container = "pack.zip";
                it.Inner = "ak47.vpk";
                string err = DeleteMod(it);
                SafeCheck(ref bad, err != null, "压缩包内条目删除被拒");
                SafeCheck(ref bad, File.Exists(arc), "压缩包本体没被删");

                // 5) 清重只删「同名且内容一样」的：同名不同内容必须留着
                string dA = Path.Combine(dir, "dupA"), dB = Path.Combine(dir, "dupB"), dC = Path.Combine(dir, "dupC");
                Directory.CreateDirectory(dA); Directory.CreateDirectory(dB); Directory.CreateDirectory(dC);
                File.WriteAllBytes(Path.Combine(dA, "ak47.vpk"), data);
                File.WriteAllBytes(Path.Combine(dB, "ak47.vpk"), data);
                byte[] other = new byte[data.Length];
                Array.Copy(data, other, data.Length);
                other[0] ^= 0xFF;                                   // 同名，内容差一个字节
                File.WriteAllBytes(Path.Combine(dC, "ak47.vpk"), other);
                List<ModItem> fake = new List<ModItem>();
                foreach (string p in new string[] { Path.Combine(dA, "ak47.vpk"), Path.Combine(dB, "ak47.vpk"), Path.Combine(dC, "ak47.vpk") })
                {
                    ModItem mi = new ModItem();
                    mi.Name = "ak47.vpk";
                    mi.FullPath = p;
                    mi.RelPath = p;
                    fake.Add(mi);
                }
                DupPlan plan = PlanDuplicates(fake);
                SafeCheck(ref bad, plan.Drop.Count == 1, "清重：3 个同名文件里只挑出 1 个真重复（实际 " + plan.Drop.Count + "）");
                SafeCheck(ref bad, plan.Drop.Count == 1 && plan.Drop[0].FullPath.IndexOf("dupB", StringComparison.OrdinalIgnoreCase) >= 0,
                    "清重：留第一份（dupA），删第二份（dupB）");
                SafeCheck(ref bad, plan.Differ.Count == 2, "清重：同名但内容不同的两个都留下（实际 " + plan.Differ.Count + "）");
                SafeCheck(ref bad, File.Exists(Path.Combine(dC, "ak47.vpk")) && File.Exists(Path.Combine(dA, "ak47.vpk")),
                    "清重清单只算不删，文件一个没动");

                // 5b) 指纹缓存不能把「换过内容的同名文件」当成同一份
                string crcA1, crcA2;
                string whyCrc;
                crcA1 = DupVerify.OfPath(Path.Combine(dC, "ak47.vpk"), out whyCrc);
                File.WriteAllBytes(Path.Combine(dC, "ak47.vpk"), data);        // 内容换成和 A 一样
                crcA2 = DupVerify.OfPath(Path.Combine(dC, "ak47.vpk"), out whyCrc);
                SafeCheck(ref bad, crcA1 != null && crcA2 != null && crcA1 != crcA2,
                    "清重：文件被换掉后指纹跟着变（不能拿旧结论当新结论）");

                // 5c) 同名但体积不同：连读都不用读，直接判「不是同一个文件」
                string dD = Path.Combine(dir, "dupD"), dE = Path.Combine(dir, "dupE");
                Directory.CreateDirectory(dD); Directory.CreateDirectory(dE);
                File.WriteAllBytes(Path.Combine(dD, "ak47.vpk"), new byte[data.Length + 4096]);
                File.WriteAllBytes(Path.Combine(dE, "ak47.vpk"), new byte[data.Length + 8192]);
                List<ModItem> fake2 = new List<ModItem>();
                foreach (string p in new string[] { Path.Combine(dA, "ak47.vpk"), Path.Combine(dD, "ak47.vpk"), Path.Combine(dE, "ak47.vpk") })
                {
                    ModItem mi = new ModItem();
                    mi.Name = "ak47.vpk";
                    mi.FullPath = p;
                    mi.RelPath = p;
                    mi.Size = new FileInfo(p).Length;
                    fake2.Add(mi);
                }
                DupPlan plan2 = PlanDuplicates(fake2);
                SafeCheck(ref bad, plan2.Drop.Count == 0, "清重：同名但体积不同的一个都不删（实际删了 " + plan2.Drop.Count + "）");
                SafeCheck(ref bad, plan2.Differ.Count == 3, "清重：三种体积各留一份报给人（实际 " + plan2.Differ.Count + "）");

                // 6) MQCam 那种「按清单核内容」：dll 必须哈希一致，.ini 只看在不在（里面的值会被游戏改）
                string gt = Path.Combine(dir, "fakeroot");
                string mqDir = Path.Combine(gt, "bin\\neko\\mqcore");
                Directory.CreateDirectory(mqDir);
                string mqDll = Path.Combine(mqDir, "mqcam.dll");
                string mqIni = Path.Combine(mqDir, "mqcam.ini");
                File.WriteAllBytes(mqDll, data);
                File.WriteAllText(mqIni, "fov=90");
                string mf = Path.Combine(dir, "payload.sha256");
                File.WriteAllText(mf,
                    Core.HashOf(mqDll) + "\tbin/neko/mqcore/mqcam.dll\r\n"
                    + new string('A', 64) + "\tbin/neko/mqcore/mqcam.ini\r\n"
                    + new string('B', 64) + "\tbin/neko/plugins/mqcore.dll\r\n",
                    new UTF8Encoding(false));
                int mt, mb; string mdetail;
                bool gotMf = VerifyPayload(mf, gt, out mt, out mb, out mdetail);
                SafeCheck(ref bad, gotMf && mt == 3, "清单核对：解析出 3 条（实际 " + mt + "）");
                SafeCheck(ref bad, gotMf && mb == 1, "清单核对：缺 1 个文件只算 1 个不对（实际 " + mb + "）");
                SafeCheck(ref bad, mdetail.IndexOf("mqcore.dll", StringComparison.OrdinalIgnoreCase) >= 0, "清单核对：报出缺的是哪个文件");
                File.WriteAllBytes(mqDll, other);          // 内容被换掉
                VerifyPayload(mf, gt, out mt, out mb, out mdetail);
                SafeCheck(ref bad, mb == 2 && mdetail.IndexOf("mqcam.dll", StringComparison.OrdinalIgnoreCase) >= 0,
                    "清单核对：内容不符也算不对（实际 " + mb + "）");
                File.WriteAllBytes(mqDll, data);
                File.WriteAllText(mqIni, "fov=120");       // 用户改过 ini
                VerifyPayload(mf, gt, out mt, out mb, out mdetail);
                SafeCheck(ref bad, mb == 1, "清单核对：ini 被改过不算错（实际 " + mb + "）");

                // 6) 并发扫库：两条线程同时扫，不能把自己的列表清成半截
                Core.ScanMods();
                int baseline = Core.ModList.Count;
                int raceErrors = 0;
                int[] seen = new int[3];
                System.Threading.Thread[] ths = new System.Threading.Thread[3];
                for (int ti = 0; ti < ths.Length; ti++)
                {
                    int idx = ti;
                    ths[ti] = new System.Threading.Thread(delegate()
                    {
                        try
                        {
                            Core.ScanMods();
                            seen[idx] = Core.ModList.Count;
                            // 空库时扫出 0 条是正确结果，不是「扫成半截」；只有本来有库却扫空了才算问题。
                            // （CI 的干净 runner 就是空库，以前这条会把它判成失败，只能挂 continue-on-error。）
                            if (seen[idx] <= 0 && baseline > 0) System.Threading.Interlocked.Increment(ref raceErrors);
                        }
                        catch (Exception exs)
                        {
                            System.Threading.Interlocked.Increment(ref raceErrors);
                            Console.WriteLine("RACE 扫描异常: " + exs.Message);
                        }
                    });
                    ths[ti].IsBackground = true;
                    ths[ti].Start();
                }
                bool allDone = true;
                foreach (System.Threading.Thread t in ths) if (!t.Join(180000)) allDone = false;
                SafeCheck(ref bad, allDone, "并发扫描都在 3 分钟内结束（没有互相等死）");
                SafeCheck(ref bad, raceErrors == 0, "并发扫描没有异常、没有空表");
                Core.ScanMods();
                if (baseline > 0)
                    SafeCheck(ref bad, Core.ModList.Count == baseline,
                        "并发扫描后条目数不变（" + Core.ModList.Count + " / " + baseline + "）");
                else Console.WriteLine("SKIP  库是空的，并发扫描只验证了不炸");

                // 7) 中断残留清理：addons 下超过 1 小时的 .part / ~l4d2-* 才清，刚写的不许动
                string sw = Path.Combine(Path.GetTempPath(), "l4d2-sweep-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    string swAddons = Path.Combine(Path.Combine(sw, "left4dead2"), "addons");
                    Directory.CreateDirectory(swAddons);
                    string oldPart = Path.Combine(swAddons, "中断.vpk.deadbeef.part");
                    string newPart = Path.Combine(swAddons, "正在写.vpk.cafebabe.part");
                    string stage = Path.Combine(swAddons, "~l4d2-abcd1234");
                    File.WriteAllText(oldPart, "x");
                    File.WriteAllText(newPart, "x");
                    Directory.CreateDirectory(stage);
                    File.WriteAllText(Path.Combine(stage, "half.vpk"), "x");
                    DateTime past = DateTime.UtcNow.AddHours(-3);
                    File.SetLastWriteTimeUtc(oldPart, past);
                    Directory.SetLastWriteTimeUtc(stage, past);

                    Core.SweepGameTemps(sw);

                    bool oldGone = !File.Exists(oldPart);
                    bool newKept = File.Exists(newPart);
                    bool stageGone = !Directory.Exists(stage);
                    SafeCheck(ref bad, oldGone, "中断超过 1 小时的 *.part 被清掉");
                    SafeCheck(ref bad, newKept, "正在写的 *.part 没被误删");
                    SafeCheck(ref bad, stageGone, "中断留下的 ~l4d2-* 暂存目录被清掉");
                }
                finally { try { Directory.Delete(sw, true); } catch { } }

                // 只报数字不断言：压缩包里的包要等包变过或用户点详情才补上标题，
                // 这里断言「必须 N 个」等于把库的内容写死在自测里。
                int titled = 0;
                foreach (ModItem mi in Core.ModList) if (!string.IsNullOrEmpty(mi.AddonTitle)) titled++;
                Console.WriteLine("INFO  addoninfo：库内 " + Core.ModList.Count + " 个条目，其中 "
                    + titled + " 个带包内标题");

                // 7) 中途失败不留垃圾
                SafeCheck(ref bad, Directory.GetFiles(dir, "*.part", SearchOption.TopDirectoryOnly).Length == 0, "没有残留 .part");
                SafeCheck(ref bad, Directory.GetDirectories(dir, "~l4d2-*").Length == 0, "没有残留解包暂存目录");

                // 8) 路径校验
                SafeCheck(ref bad, Core.IsUnder(dir, Path.Combine(dir, "left4dead2\\addons\\x.vpk")), "子路径判定在目录内");
                SafeCheck(ref bad, !Core.IsUnder(dir, Path.Combine(dir, "..\\x.vpk")), ".. 往上跳判定在目录外");

                // 8b) SEC-03：受保护路径 —— 越界、非法名、前缀相同的兄弟目录，一律拒绝
                string sr = Path.Combine(dir, "saferoot");
                Directory.CreateDirectory(Path.Combine(sr, "left4dead2\\addons"));
                Directory.CreateDirectory(Path.Combine(dir, "saferoot2"));
                File.WriteAllBytes(Path.Combine(dir, "saferoot2", "x.vpk"), data);

                Core.PathCheck sc = Core.ResolveUnder(sr, "left4dead2\\addons\\a.vpk");
                SafeCheck(ref bad, sc.Ok && sc.Full.EndsWith("a.vpk", StringComparison.OrdinalIgnoreCase),
                    "路径校验：根内正常子路径放行" + (sc.Ok ? "" : "（" + sc.Why + "）"));

                string[] badRel = new string[] { "..\\escape.vpk", "..\\..\\x.vpk", "C:\\Windows\\x.vpk",
                                                 "\\\\srv\\share\\x.vpk", "\\\\.\\C:\\x.vpk", "",
                                                 "left4dead2\\..\\..\\x.vpk", "left4dead2\\NUL.vpk",
                                                 "left4dead2\\a:b.vpk", "left4dead2\\trail.",
                                                 "left4dead2\\\\double.vpk" };
                foreach (string rel in badRel)
                {
                    Core.PathCheck bc = Core.ResolveUnder(sr, rel);
                    SafeCheck(ref bad, !bc.Ok, "路径校验：拒绝「" + rel + "」" + (bc.Ok ? "（却放行了）" : ""));
                }
                SafeCheck(ref bad, !Core.ResolveUnder(sr, "..\\saferoot2\\x.vpk").Ok, "路径校验：saferoot2 不算在 saferoot 里");
                SafeCheck(ref bad, Core.ValidateUnder(sr, Path.Combine(sr, "left4dead2\\addons\\a.vpk")).Ok,
                    "路径校验：已存在的根内目标放行");
                SafeCheck(ref bad, !Core.ValidateUnder(sr, Path.Combine(dir, "saferoot2", "x.vpk")).Ok,
                    "路径校验：已存在的根外目标拒绝");

                // 8c) SEC-03：删除入口只认游戏 addons —— 库里照删，库外的必须拒且文件还在
                string fakeGame = Path.Combine(dir, "fakegame");
                string fakeAddons = Path.Combine(Path.Combine(fakeGame, "left4dead2"), "addons");
                Directory.CreateDirectory(fakeAddons);
                File.WriteAllBytes(Path.Combine(fakeGame, "left4dead2.exe"), new byte[] { 0x4D, 0x5A });
                string inVpk = Path.Combine(fakeAddons, "in.vpk");
                string outVpk = Path.Combine(dir, "out.vpk");
                File.WriteAllBytes(inVpk, data);
                File.WriteAllBytes(outVpk, data);
                string savedRoot = Core.Cfg.GameRoot;
                try
                {
                    Core.Cfg.GameRoot = fakeGame;
                    ModItem inItem = new ModItem();
                    inItem.Name = "in.vpk";
                    inItem.FullPath = inVpk;
                    string e1 = DeleteMod(inItem);
                    SafeCheck(ref bad, e1 == null && !File.Exists(inVpk), "删除：库内的 vpk 正常删掉" + (e1 == null ? "" : "（" + e1 + "）"));

                    ModItem outItem = new ModItem();
                    outItem.Name = "out.vpk";
                    outItem.FullPath = outVpk;
                    string e2 = DeleteMod(outItem);
                    SafeCheck(ref bad, e2 != null, "删除：库外的 vpk 被拒" + (e2 == null ? "（却删掉了）" : ""));
                    SafeCheck(ref bad, File.Exists(outVpk), "删除：被拒的库外文件还在");
                }
                finally { Core.Cfg.GameRoot = savedRoot; }

                // 8d) SEC-03：联接点 —— 根内的链接指向根外时，写路径必须拒绝，外部文件一个字节不许动。
                //     联接点只有 NTFS 支持，所以这组放在系统盘临时目录（D 盘是 exFAT，造不出来就 SKIP）。
                string lt = Path.Combine(Path.GetTempPath(), "safepath-link-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    string lroot = Path.Combine(lt, "root");
                    string lout = Path.Combine(lt, "outside");
                    Directory.CreateDirectory(Path.Combine(lroot, "left4dead2\\addons"));
                    Directory.CreateDirectory(lout);
                    File.WriteAllBytes(Path.Combine(lout, "keep.vpk"), data);
                    string keep = Path.Combine(lout, "keep.vpk");
                    string keepHash = Core.HashOf(keep);
                    string juncOut = Path.Combine(lroot, "left4dead2\\linkout");
                    string juncIn = Path.Combine(lroot, "left4dead2\\addons\\linkin");
                    bool madeOut = MakeJunction(juncOut, lout);
                    bool madeIn = MakeJunction(juncIn, Path.Combine(lroot, "left4dead2\\addons"));
                    if (madeOut)
                    {
                        Core.PathCheck lo = Core.ResolveUnder(lroot, "left4dead2\\linkout\\x.vpk");
                        SafeCheck(ref bad, !lo.Ok, "路径校验：根内联接点指向根外，写入被拒");
                        Core.PathCheck lv = Core.ValidateUnder(lroot, keep);
                        SafeCheck(ref bad, !lv.Ok, "路径校验：根外文件给了完整路径也拒");
                        SafeCheck(ref bad, string.Equals(Core.HashOf(keep), keepHash, StringComparison.OrdinalIgnoreCase),
                            "路径校验：外部文件没被碰过");
                    }
                    else Console.WriteLine("SKIP  这卷造不出联接点（不是 NTFS？），链接那两条没验");
                    if (madeIn)
                    {
                        Core.PathCheck li = Core.ResolveUnder(lroot, "left4dead2\\addons\\linkin\\x.vpk");
                        SafeCheck(ref bad, !li.Ok, "路径校验：链接指向根内也算穿过链接，拒绝");
                    }
                }
                finally { try { Directory.Delete(lt, true); } catch { } }

                // 9) 包内 addoninfo：游戏 addons 菜单显示的是这里的 addonTitle，和文件名常常不是一回事。
                //    读取器要是认错了，详情里写的就是别的 MOD 的名字，比没有更糟。
                string vp1 = Path.Combine(dir, "info-preload.vpk");
                File.WriteAllBytes(vp1, BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"MWII RPK Macabre(AK47)\"\r\n\tAddonAuthor \"芬吸员\"\r\n}\r\n", true));
                Vpk.AddonMeta i1 = Vpk.ReadAddonInfo(vp1);
                SafeCheck(ref bad, i1.Found && i1.Readable && i1.Title == "MWII RPK Macabre(AK47)",
                    "addoninfo：目录表预载里的标题读对了（实际「" + i1.Title + "」）");
                SafeCheck(ref bad, i1.Author == "芬吸员", "addoninfo：作者读对了（实际「" + i1.Author + "」）");

                string vp2 = Path.Combine(dir, "info-data.vpk");
                File.WriteAllBytes(vp2, BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\t\"addonTitle\"\t\t\"CSO 白虎  mp5 \"\r\n}\r\n", false));
                SafeCheck(ref bad, Vpk.ReadAddonInfo(vp2).Title == "CSO 白虎  mp5",
                    "addoninfo：数据段里带引号、带多余空格的标题读对了");

                string vp3 = Path.Combine(dir, "info-odd.vpk");
                File.WriteAllBytes(vp3, BuildTinyVpk("addontitle CODOL自由霰弹(pumpshot)\"\r\n", true));
                SafeCheck(ref bad, Vpk.ReadAddonInfo(vp3).Title == "CODOL自由霰弹(pumpshot)",
                    "addoninfo：漏写前引号的野格式也能读");

                string vp4 = Path.Combine(dir, "info-none.vpk");
                File.WriteAllBytes(vp4, BuildTinyVpk(null, false));
                Vpk.AddonMeta i4 = Vpk.ReadAddonInfo(vp4);
                SafeCheck(ref bad, !i4.Found && i4.Title.Length == 0, "addoninfo：包里没有就留空，不瞎编");

                string vp5 = Path.Combine(dir, "info-broken.vpk");
                File.WriteAllBytes(vp5, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
                SafeCheck(ref bad, Vpk.ReadAddonInfo(vp5).Title.Length == 0, "addoninfo：怪文件不崩、返回空");

                SafeCheck(ref bad, ModIdentify.Analyze(vp1).Title == "MWII RPK Macabre(AK47)",
                    "addoninfo：识别结果里带上了标题（详情和搜索都读它）");

                // 压缩包里的 vpk 扫库时只拿得到目录表，走的是另一条路
                byte[] tiny = BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"压缩包里的标题\"\r\n}\r\n", true);
                int treeLen = (int)BitConverter.ToUInt32(tiny, 8);
                byte[] onlyTree = new byte[treeLen];
                Array.Copy(tiny, 12, onlyTree, 0, treeLen);
                SafeCheck(ref bad, Vpk.InfoFromTreeBuffer(onlyTree).Title == "压缩包里的标题",
                    "addoninfo：只有目录表也能读预载里的标题（压缩包条目走这条路）");

                byte[] dataOne = BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"在数据段\"\r\n}\r\n", false);
                int tl2 = (int)BitConverter.ToUInt32(dataOne, 8);
                byte[] onlyTree2 = new byte[tl2];
                Array.Copy(dataOne, 12, onlyTree2, 0, tl2);
                Vpk.AddonMeta i6 = Vpk.InfoFromTreeBuffer(onlyTree2);
                SafeCheck(ref bad, i6.Found && i6.Title.Length == 0,
                    "addoninfo：正文在数据段时只有目录表读不到，但不瞎编（Found=" + i6.Found + "）");

                // 压缩包里的 vpk：实测 addoninfo 大多在数据段，得顺着流读到那个位置
                string zp = Path.Combine(dir, "arc.zip");
                using (System.IO.Compression.ZipArchive za =
                       System.IO.Compression.ZipFile.Open(zp, System.IO.Compression.ZipArchiveMode.Create))
                {
                    System.IO.Compression.ZipArchiveEntry ze = za.CreateEntry("inner.vpk");
                    using (Stream zs = ze.Open())
                    {
                        byte[] zb = BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"压缩包数据段标题\"\r\n}\r\n", false);
                        zs.Write(zb, 0, zb.Length);
                    }
                }
                SafeCheck(ref bad, Vpk.ReadAddonInfoFromArchive(zp, "inner.vpk").Title == "压缩包数据段标题",
                    "addoninfo：zip 里数据段的标题也能读（压缩包条目靠这条补）");

                // 10) 搜索别名：标题/作者只在 addoninfo 里时，按那个词也得搜得到
                ModGroup fg = new ModGroup();
                fg.Key = "test|search";
                fg.Name = "科技之光SD 替换M16";
                ModItem fp = new ModItem();
                fp.Key = "test|search|file";
                fp.Name = "科技之光SD 替换M16.vpk";
                fp.AddonTitle = "M4A1Tech科技之光";
                fp.AddonAuthor = "安妮某栓栓";
                fg.Parts.Add(fp);
                SafeCheck(ref bad, MainForm.MatchGroupCore(fg, "M4A1"), "搜索：只在包内标题里的词也能搜到");
                SafeCheck(ref bad, MainForm.MatchGroupCore(fg, "安妮"), "搜索：作者名也能搜");
                SafeCheck(ref bad, MainForm.MatchGroupCore(fg, "科技之光"), "搜索：文件名照旧能搜");
                SafeCheck(ref bad, !MainForm.MatchGroupCore(fg, "完全不相干的词"), "搜索：不相干的词不会误命中");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL  自检本身出错: " + ex.Message);
                bad++;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            Console.WriteLine("safeself failures = " + bad);
            return bad;
        }

        /// <summary>自检用：造一个目录联接点（mklink /J 不需要管理员）。卷不支持（exFAT）或造不出来返回 false。</summary>
        static bool MakeJunction(string link, string target)
        {
            try
            {
                if (Directory.Exists(link)) Directory.Delete(link, false);
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    "/c mklink /J \"" + link + "\" \"" + target + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return false; }
                    if (p.ExitCode != 0) return false;
                }
                return Directory.Exists(link) && Core.IsLink(link);
            }
            catch { return false; }
        }

        static void SafeCheck(ref int bad, bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) bad++;
        }

        /// <summary>
        /// 造一个最小可读的 vpk：目录表里只放一条 addoninfo.txt（info 为 null 时什么都不放）。
        /// preload=true 时内容紧跟在目录表条目后面（V 社工具的常规写法），false 时放进数据段 ——
        /// 库里两种包都有，读标题这两条路都得走通。
        /// </summary>
        internal static byte[] BuildTinyVpk(string info, bool preload)
        {
            byte[] body = info == null ? new byte[0] : Encoding.UTF8.GetBytes(info);
            List<byte> tree = new List<byte>();
            if (info != null)
            {
                tree.AddRange(Encoding.UTF8.GetBytes("txt")); tree.Add(0);
                tree.AddRange(Encoding.UTF8.GetBytes(" ")); tree.Add(0);      // 根目录：V 社工具写的是一个空格，不是空串
                tree.AddRange(Encoding.UTF8.GetBytes("addoninfo")); tree.Add(0);
                byte[] e = new byte[18];
                BitConverter.GetBytes(0u).CopyTo(e, 0);                        // crc
                BitConverter.GetBytes((ushort)(preload ? body.Length : 0)).CopyTo(e, 4);
                BitConverter.GetBytes((ushort)0x7FFF).CopyTo(e, 6);            // 数据就在本文件
                BitConverter.GetBytes(0u).CopyTo(e, 8);                        // 偏移：数据段从 0 起
                BitConverter.GetBytes((uint)(preload ? 0 : body.Length)).CopyTo(e, 12);
                BitConverter.GetBytes((ushort)0xFFFF).CopyTo(e, 16);
                tree.AddRange(e);
                if (preload) tree.AddRange(body);
                tree.Add(0);                                                   // 文件名层结束
                tree.Add(0);                                                   // 路径层结束
            }
            tree.Add(0);                                                       // 扩展名层结束（整棵树到头）

            List<byte> all = new List<byte>();
            all.AddRange(BitConverter.GetBytes(0x55AA1234u));
            all.AddRange(BitConverter.GetBytes(1u));
            all.AddRange(BitConverter.GetBytes((uint)tree.Count));
            all.AddRange(tree);
            if (info != null && !preload) all.AddRange(body);
            return all.ToArray();
        }

        // ---------- 自测：addonlist 写入策略 + 外部改动（不依赖库） ----------
        /// <summary>
        /// 干净机器上也要真跑，所以不放在 SelfTestAddons 的库检查后面：
        ///   1) 游戏在跑时照写（大厅里改开关是正常操作），写完读得回来、并打上 LastWroteWhileRunning
        ///   2) 写完留快照；外部（Steam 更新 / 游戏内改开关）改了能报出来，且同一处只报一次
        /// </summary>
        public static int SelfTestAddonGate()
        {
            int bad = 0;
            string savedLogDir = Core.LogDir;
            string root = Path.Combine(Path.GetTempPath(), "l4d2-gate-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "left4dead2"));
                Core.LogDir = Path.Combine(root, "_装机记录");
                Directory.CreateDirectory(Core.LogDir);

                string file = AddonList.FileOf(root);
                Dictionary<string, bool> a = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                a["alpha.vpk"] = true;
                a["beta.vpk"] = false;
                if (!AddonList.Write(root, a)) { bad++; Console.WriteLine("[FAIL] 正常写入被拒了"); }

                // 1) 假装游戏在跑 → 照写，写得进去，且打上「写入时游戏在跑」的标记
                string before = File.Exists(file) ? File.ReadAllText(file, Encoding.Default) : "";
                Dictionary<string, bool> b = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                b["alpha.vpk"] = false;
                b["beta.vpk"] = true;
                AddonList.ForceGameRunning = true;
                bool wrote = AddonList.Write(root, b);
                bool flagged = AddonList.LastWroteWhileRunning;
                AddonList.ForceGameRunning = false;
                string after = File.Exists(file) ? File.ReadAllText(file, Encoding.Default) : "";
                Dictionary<string, bool> now = AddonList.Read(root);
                bool alphaOff = now.ContainsKey("alpha.vpk") && !now["alpha.vpk"];
                bool betaOn = now.ContainsKey("beta.vpk") && now["beta.vpk"];

                if (!wrote) { bad++; Console.WriteLine("[FAIL] 游戏在跑时写不进去了（应该照写）"); }
                if (!flagged) { bad++; Console.WriteLine("[FAIL] 游戏在跑时没打标记，界面就不知道该提醒"); }
                if (before == after) { bad++; Console.WriteLine("[FAIL] 说要写，文件却没变"); }
                if (!alphaOff || !betaOn) { bad++; Console.WriteLine("[FAIL] 写进去的值不对：alpha关=" + alphaOff + "，beta开=" + betaOn); }
                Console.WriteLine("ADDON 写入策略：游戏在跑 → 照写=" + wrote + "、打过标记=" + flagged
                    + "、文件已变=" + (before != after) + "、回读一致=" + (alphaOff && betaOn));

                // 游戏不在跑时，标记应当归位
                AddonList.Write(root, b);
                if (AddonList.LastWroteWhileRunning) { bad++; Console.WriteLine("[FAIL] 游戏没跑却打上了「正在运行」的标记"); }

                // 2) 外部改动：直接改文件（模拟 Steam 更新 / 游戏内开关）
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("\"AddonList\"");
                sb.AppendLine("{");
                sb.AppendLine("\t\"alpha.vpk\"\t\t\"1\"");
                sb.AppendLine("\t\"beta.vpk\"\t\t\"1\"");        // 被外部打开了
                sb.AppendLine("\t\"gamma.vpk\"\t\t\"0\"");       // 外部新增
                sb.AppendLine("}");
                File.WriteAllText(file, sb.ToString(), Encoding.Default);

                string ext = AddonList.CheckExternal(root);
                // 只断言「报得出来、举的例子里有 gamma」：
                // 上一组结束时快照里已经是 alpha=0/beta=1，所以外部这一改到底算
                // 「alpha 被开启」还是「beta 被开启」取决于前面怎么写的，不写死名字。
                bool reported = ext != null && ext.IndexOf("gamma.vpk", StringComparison.Ordinal) >= 0;
                string again = AddonList.CheckExternal(root);
                if (!reported) { bad++; Console.WriteLine("[FAIL] 外部改动没报出来: " + (ext == null ? "null" : ext)); }
                if (again != null) { bad++; Console.WriteLine("[FAIL] 同一处外部改动报了两遍: " + again); }
                Console.WriteLine("ADDON 外部改动：报出=" + reported + "，二次不重复=" + (again == null)
                    + (ext == null ? "" : "；" + ext));
            }
            catch (Exception ex) { bad++; Console.WriteLine("[FAIL] addonlist 守门自检异常: " + ex.Message); }
            finally
            {
                Core.LogDir = savedLogDir;
                try { Directory.Delete(root, true); } catch { }
            }
            return bad;
        }

        // ---------- 自测：换整套只翻开关，文件不动 ----------
        /// <summary>
        /// 拿个假游戏目录跑一遍 SwitchAddons，检查：
        ///   这次选的 → 1；上一套（库里有的）→ 0；用户自己放的 → 一个字节都没被改；文件数量不变。
        /// </summary>
        public static int SelfTestAddons()
        {
            int bad = 0;
            string tmp = Path.Combine(Path.GetTempPath(), "l4d2-addonlist-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string l4d2 = Path.Combine(tmp, "left4dead2");
            string addons = Path.Combine(l4d2, "addons");
            string savedGameRoot = Core.Cfg.GameRoot, savedLogDir = Core.LogDir, savedMode = Core.Cfg.Mode;
            string savedLog = savedLogDir, savedGame = savedGameRoot;
            bool savedSelfTesting = Core.SelfTesting;
            List<ModItem> savedItems = Core.ModList;
            Dictionary<string, ModItem> savedByRel = Core.ByRel;
            Dictionary<string, int> savedDirCount = Core.DirCount;
            List<ModGroup> savedGroups = Catalog.Groups;
            Dictionary<string, ModGroup> savedGroupByKey = Catalog.GroupByKey;
            try
            {
                bad += SelfTestAddonGate();
                Directory.CreateDirectory(addons);
                File.WriteAllText(Path.Combine(tmp, "left4dead2.exe"), "selftest");
                Core.Cfg.GameRoot = tmp;
                Core.Cfg.Mode = "local";
                Core.LogDir = Path.Combine(tmp, "_装机记录");
                Directory.CreateDirectory(Core.LogDir);
                Core.SelfTesting = true;
                Core.ModList = new List<ModItem>();
                Core.ByRel = new Dictionary<string, ModItem>(StringComparer.OrdinalIgnoreCase);
                Core.DirCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Catalog.Groups = new List<ModGroup>();
                Catalog.GroupByKey = new Dictionary<string, ModGroup>(StringComparer.OrdinalIgnoreCase);

                string[] names = new string[] { "selftest_ak47.vpk", "selftest_mp5.vpk", "selftest_pistol.vpk" };
                string[] models = new string[] { "v_rifle_ak47.mdl", "v_smg_mp5.mdl", "v_pistol.mdl" };
                string[] subjects = new string[] { "AK-47", "MP5", "手枪" };
                for (int i = 0; i < names.Length; i++)
                {
                    string src = Path.Combine(tmp, "src" + i);
                    Directory.CreateDirectory(Path.Combine(src, "models", "v_models"));
                    File.WriteAllText(Path.Combine(src, "models", "v_models", models[i]), "selftest");
                    if (VpkPack.PackDir(src, Path.Combine(addons, names[i])) != 1)
                        throw new IOException("合成 VPK 失败: " + names[i]);
                }
                Core.ScanMods();
                if (Core.ModList.Count != 3) throw new IOException("合成库扫描数不对: " + Core.ModList.Count);
                List<ModItem> pool = new List<ModItem>();
                foreach (string name in names)
                {
                    ModItem found = null;
                    foreach (ModItem m in Core.ModList) if (m.Name == name) { found = m; break; }
                    if (found == null || found.Entries == 0) throw new IOException("扫描没读到合成 VPK: " + name);
                    int expected = Array.IndexOf(names, name);
                    if (found.Subject != subjects[expected]) throw new IOException("合成 VPK 识别错: " + name + " → " + found.Subject);
                    pool.Add(found);
                }
                string mine = "我自己放的.vpk";
                File.WriteAllText(Path.Combine(addons, mine), "x");
                Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (ModItem m in pool) hashes[m.Name] = Core.HashOf(m.FullPath);
                hashes[mine] = Core.HashOf(Path.Combine(addons, mine));

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("\"AddonList\"");
                sb.AppendLine("{");
                foreach (ModItem m in pool) sb.AppendLine("\t\"" + m.Name + "\"\t\t\"1\"");
                sb.AppendLine("\t\"" + mine + "\"\t\t\"1\"");
                sb.AppendLine("}");
                File.WriteAllText(AddonList.FileOf(tmp), sb.ToString(), Encoding.Default);

                int on, off, keep;
                SwitchAddons(tmp, new List<ModItem> { pool[0] }, null, out on, out off, out keep);
                Dictionary<string, bool> st = AddonList.Read(tmp);

                bool a = st.ContainsKey(pool[0].Name) && st[pool[0].Name];
                bool b = st.ContainsKey(pool[1].Name) && !st[pool[1].Name];
                bool c = st.ContainsKey(pool[2].Name) && !st[pool[2].Name];
                bool d = st.ContainsKey(mine) && st[mine];           // 用户自己放的：没被关
                int files = Directory.GetFiles(addons, "*.vpk").Length;
                bool e = files == pool.Count + 1;                    // 文件一个没少
                bool hashesSame = true;
                foreach (KeyValuePair<string, string> kv in hashes)
                    if (Core.HashOf(Path.Combine(addons, kv.Key)) != kv.Value) { hashesSame = false; break; }

                if (!a) { bad++; Console.WriteLine("[FAIL] 这次选的没被打开: " + pool[0].Name); }
                if (!b) { bad++; Console.WriteLine("[FAIL] 上一套的没被关掉: " + pool[1].Name); }
                if (!c) { bad++; Console.WriteLine("[FAIL] 上一套的没被关掉: " + pool[2].Name); }
                if (!d) { bad++; Console.WriteLine("[FAIL] 用户自己放的被改动了: " + mine); }
                if (!e) { bad++; Console.WriteLine("[FAIL] addons 里文件数变了: " + files + " 个"); }
                if (!hashesSame) { bad++; Console.WriteLine("[FAIL] 开关改动了一个 VPK 文件的内容"); }
                Console.WriteLine("ADDON 挑一个装：打开 " + on + " / 关闭 " + off + " / 不认识的 " + keep
                    + "，文件 " + files + " 个（原 " + (pool.Count + 1) + "），内容未变=" + hashesSame);

                // 一个都不选 + 换整套 = 全关（用户自己的仍然不碰）
                SwitchAddons(tmp, new List<ModItem>(), null, out on, out off, out keep);
                st = AddonList.Read(tmp);
                bool g1, g2, g3;
                st.TryGetValue(pool[0].Name, out g1);
                st.TryGetValue(pool[1].Name, out g2);
                st.TryGetValue(pool[2].Name, out g3);
                bool f1 = !g1 && !g2 && !g3;
                bool f2;
                st.TryGetValue(mine, out f2);
                if (!f1) { bad++; Console.WriteLine("[FAIL] 一个都不选时，库里的没全关"); }
                if (!f2) { bad++; Console.WriteLine("[FAIL] 一个都不选时，用户自己放的被关了"); }
                int files2 = Directory.GetFiles(addons, "*.vpk").Length;
                if (files2 != files) { bad++; Console.WriteLine("[FAIL] 全关时文件数变了"); }
                foreach (KeyValuePair<string, string> kv in hashes)
                    if (Core.HashOf(Path.Combine(addons, kv.Key)) != kv.Value) { hashesSame = false; break; }
                if (!hashesSame) { bad++; Console.WriteLine("[FAIL] 全关时改了一个 VPK 文件的内容"); }
                Console.WriteLine("ADDON 一个都不选：全关=" + f1 + "，用户自己的还开着=" + f2 + "，文件 " + files2 + " 个");

                // 「已装 MOD 开关」窗口：只改开关不能重排顺序（顺序 = 覆盖优先级），真拖过才重排
                string tmp2 = Path.Combine(Path.GetTempPath(), "l4d2-order-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    string a2 = Path.Combine(Path.Combine(tmp2, "left4dead2"), "addons");
                    Directory.CreateDirectory(a2);
                    foreach (string n in new string[] { "c.vpk", "a.vpk", "b.vpk" })
                        File.WriteAllText(Path.Combine(a2, n), "x");
                    File.WriteAllText(AddonList.FileOf(tmp2),
                        "\"AddonList\"\n{\n\t\"b.vpk\"\t\t\"1\"\n\t\"c.vpk\"\t\t\"1\"\n\t\"a.vpk\"\t\t\"1\"\n"
                        + "\t\"workshop\\999.vpk\"\t\t\"1\"\n}\n", Encoding.Default);

                    List<string> got = AddonList.OrderOf(tmp2);
                    bool ord = got.Count == 4 && got[0] == "b.vpk" && got[1] == "c.vpk" && got[2] == "a.vpk";
                    if (!ord) { bad++; Console.WriteLine("[FAIL] 读不出 addonlist 的顺序: " + string.Join(",", got.ToArray())); }

                    Dictionary<string, bool> ch = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    ch["b.vpk"] = true; ch["c.vpk"] = false; ch["a.vpk"] = true;
                    AddonSwitchForm.WriteSwitches(tmp2, new List<string> { "b.vpk", "c.vpk", "a.vpk" }, ch, false);
                    List<string> after = AddonList.OrderOf(tmp2);
                    Dictionary<string, bool> st3 = AddonList.Read(tmp2);
                    bool keepOrder = after.Count >= 3 && after[0] == "b.vpk" && after[1] == "c.vpk" && after[2] == "a.vpk";
                    bool changed = st3.ContainsKey("c.vpk") && !st3["c.vpk"];
                    if (!keepOrder) { bad++; Console.WriteLine("[FAIL] 只改开关却把顺序改了: " + string.Join(",", after.ToArray())); }
                    if (!changed) { bad++; Console.WriteLine("[FAIL] 开关没写进 addonlist"); }

                    AddonSwitchForm.WriteSwitches(tmp2, new List<string> { "a.vpk", "c.vpk", "b.vpk" }, ch, true);
                    List<string> moved2 = AddonList.OrderOf(tmp2);
                    bool reordered = moved2.Count >= 3 && moved2[0] == "a.vpk" && moved2[1] == "c.vpk" && moved2[2] == "b.vpk";
                    if (!reordered) { bad++; Console.WriteLine("[FAIL] 拖过之后没按新顺序写: " + string.Join(",", moved2.ToArray())); }
                    Console.WriteLine("ADDON 开关窗口：读顺序=" + ord + "，只改开关不动顺序=" + keepOrder
                        + "，真拖过才重排=" + reordered);
                }
                finally { try { Directory.Delete(tmp2, true); } catch { } }

                // 导入取消：在第一个文件边界停下，源文件不能移动或复制。
                string savedImportRoot = Core.ModRoot;
                try
                {
                    string importSrc = Path.Combine(tmp, "待导入");
                    Directory.CreateDirectory(importSrc);
                    string importFile = Path.Combine(importSrc, "停止导入.vpk");
                    File.WriteAllText(importFile, "x");
                    Core.ModRoot = Path.Combine(tmp, "导入库");
                    CancelRequested = true;
                    int stopImported = -1, stopSkipped = -1;
                    List<string> stopLog = ImportVpks(importSrc, true, out stopImported, out stopSkipped);
                    bool importKept = stopImported == 0 && File.Exists(importFile);
                    bool importStopped = stopLog.Exists(delegate(string l) { return l != null && l.Contains("已停止导入"); });
                    if (!importKept) { bad++; Console.WriteLine("[FAIL] 取消导入仍然处理了源文件"); }
                    if (!importStopped) { bad++; Console.WriteLine("[FAIL] 取消导入没有明确报告停止"); }
                    Console.WriteLine("ADDON 取消导入：源文件保留=" + importKept + "，停止反馈=" + importStopped);
                }
                finally { CancelRequested = false; Core.ModRoot = savedImportRoot; }

                // 本地模式：从游戏 addons 删一项 → 文件真删掉、addonlist 里那条登记一起摘掉
                try
                {
                    Core.Cfg.Mode = "local";
                    Core.Cfg.GameRoot = tmp;
                    Core.LogDir = Path.Combine(tmp, "_装机记录");
                    Directory.CreateDirectory(Core.LogDir);
                    ModItem victim = pool[0];
                    victim.FullPath = Path.Combine(addons, victim.Name);
                    victim.FromGame = true;
                    string err = DeleteMod(victim);
                    bool moved = err == null && !File.Exists(victim.FullPath);
                    Dictionary<string, bool> st2 = AddonList.Read(tmp);
                    bool dropped = !st2.ContainsKey(victim.Name);
                    if (!moved) { bad++; Console.WriteLine("[FAIL] 本地模式删除没删掉文件: " + victim.Name + " " + err); }
                    if (!dropped) { bad++; Console.WriteLine("[FAIL] 删除后 addonlist 里还留着: " + victim.Name); }
                    Console.WriteLine("ADDON 本地模式删除：文件已删=" + moved + "，addonlist 摘掉=" + dropped);
                }
                finally
                {
                    Core.Cfg.Mode = savedMode;
                    Core.Cfg.GameRoot = savedGame;
                    Core.LogDir = savedLog;
                }

                // 还原取消必须停在文件边界，并把未处理项目留在装机记录里供下次重试。
                try
                {
                    Core.Cfg.Mode = "local";
                    Core.Cfg.GameRoot = tmp;
                    Core.LogDir = Path.Combine(tmp, "_装机记录");
                    string keepA = "取消还原A.vpk", keepB = "取消还原B.vpk";
                    File.WriteAllText(Path.Combine(addons, keepA), "x");
                    File.WriteAllText(Path.Combine(addons, keepB), "x");
                    File.WriteAllText(Path.Combine(tmp, "d3d9.dll"), "x");
                    WriteManifest(tmp, new List<string> { keepA, keepB }, new List<string> { "Dxvk" });
                    CancelRequested = true;
                    List<string> stoppedReport = FullRestore(tmp);
                    bool filesKept = File.Exists(Path.Combine(addons, keepA)) && File.Exists(Path.Combine(addons, keepB))
                        && File.Exists(Path.Combine(tmp, "d3d9.dll"));
                    string stoppedManifest = File.ReadAllText(Path.Combine(Core.LogDir, "最近一次.ini"), Encoding.UTF8);
                    bool manifestKept = stoppedManifest.Contains(keepA) && stoppedManifest.Contains(keepB) && stoppedManifest.Contains("Dxvk");
                    bool stopReported = stoppedReport.Exists(delegate(string l) { return l != null && l.StartsWith("未完成："); });
                    if (!filesKept) { bad++; Console.WriteLine("[FAIL] 取消还原仍然删掉了未处理的 MOD"); }
                    if (!manifestKept) { bad++; Console.WriteLine("[FAIL] 取消还原把未处理项目从装机记录里弄丢了"); }
                    if (!stopReported) { bad++; Console.WriteLine("[FAIL] 取消还原没有报告未完成状态"); }
                    Console.WriteLine("ADDON 取消还原：文件保留=" + filesKept + "，记录保留=" + manifestKept + "，未完成反馈=" + stopReported);
                }
                finally { CancelRequested = false; }

                // 彻底还原：删掉的 vpk 不能把 addonlist 里的登记留成「幽灵开关」
                // （留着的 0 会在下次装回同名 MOD 时把它默默关掉，用户以为装了却没生效）
                try
                {
                    Core.Cfg.Mode = "local";
                    Core.Cfg.GameRoot = tmp;
                    Core.LogDir = Path.Combine(tmp, "_装机记录");
                    Directory.CreateDirectory(Core.LogDir);

                    string ghost = "还原测试.vpk";
                    File.WriteAllText(Path.Combine(addons, ghost), "x");
                    Dictionary<string, bool> swOff = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    swOff[ghost] = false;                            // 先登记成「关闭」
                    AddonList.Write(tmp, swOff);
                    File.WriteAllText(Path.Combine(Core.LogDir, "最近一次.ini"),
                        "GameRoot=" + tmp + "\r\nTime=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        + "\r\nAdded=" + ghost + "\r\n", new UTF8Encoding(true));

                    FullRestore(tmp);

                    bool fileGone = !File.Exists(Path.Combine(addons, ghost));
                    bool regGone = !AddonList.Read(tmp).ContainsKey(ghost);
                    if (!fileGone) { bad++; Console.WriteLine("[FAIL] 彻底还原没删掉铺进去的 vpk: " + ghost); }
                    if (!regGone) { bad++; Console.WriteLine("[FAIL] 彻底还原后 addonlist 里还留着幽灵登记: " + ghost); }
                    Console.WriteLine("ADDON 彻底还原：文件已删=" + fileGone + "，addonlist 幽灵登记已清=" + regGone);
                }
                finally
                {
                    Core.Cfg.Mode = savedMode;
                    Core.Cfg.GameRoot = savedGame;
                    Core.LogDir = savedLog;
                }

                // 装机记录按游戏目录分段：第二次装不能覆盖第一次的清单；
                // 还原只删本目录的记录，平台文件只删记录过的组件（mqcam.ini 是用户相机设置，永远不删）。
                string tmpB = Path.Combine(Path.GetTempPath(), "l4d2-manifest-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    string addonsB = Path.Combine(Path.Combine(tmpB, "left4dead2"), "addons");
                    Directory.CreateDirectory(addonsB);
                    Core.Cfg.Mode = "local";
                    Core.Cfg.GameRoot = tmp;
                    Core.LogDir = Path.Combine(tmp, "_装机记录");
                    Directory.CreateDirectory(Core.LogDir);

                    File.WriteAllText(Path.Combine(addons, "第一段.vpk"), "x");
                    File.WriteAllText(Path.Combine(addons, "第二段.vpk"), "x");
                    File.WriteAllText(Path.Combine(addonsB, "别的目录.vpk"), "x");
                    // 记录里只有 Dxvk：d3d9.dll 该删，没记录的 mqcam.dll / mqcam.ini 都不该动
                    File.WriteAllText(Path.Combine(tmp, "d3d9.dll"), "x");
                    string mqDir = Path.Combine(tmp, "bin\\neko\\mqcore");
                    Directory.CreateDirectory(mqDir);
                    File.WriteAllText(Path.Combine(mqDir, "mqcam.dll"), "x");
                    File.WriteAllText(Path.Combine(mqDir, "mqcam.ini"), "x");

                    WriteManifest(tmp, new List<string> { "第一段.vpk" }, new List<string> { "Dxvk" });
                    WriteManifest(tmpB, new List<string> { "别的目录.vpk" }, new List<string> { "Mqcam" });
                    WriteManifest(tmp, new List<string> { "第二段.vpk" }, null);

                    string mf = Path.Combine(Core.LogDir, "最近一次.ini");
                    string text = File.ReadAllText(mf, Encoding.UTF8);
                    bool twoA = text.Contains("第一段.vpk") && text.Contains("第二段.vpk");
                    bool hasB = text.Contains("别的目录.vpk");
                    if (!twoA) { bad++; Console.WriteLine("[FAIL] 同一目录两次装的清单没合并"); }
                    if (!hasB) { bad++; Console.WriteLine("[FAIL] 第二次装把另一个目录的记录覆盖了"); }

                    FullRestore(tmp);
                    bool aGone = !File.Exists(Path.Combine(addons, "第一段.vpk"))
                              && !File.Exists(Path.Combine(addons, "第二段.vpk"));
                    bool bAlive = File.Exists(Path.Combine(addonsB, "别的目录.vpk"));
                    bool dllGone = !File.Exists(Path.Combine(tmp, "d3d9.dll"));
                    bool camKept = File.Exists(Path.Combine(mqDir, "mqcam.dll"))
                                && File.Exists(Path.Combine(mqDir, "mqcam.ini"));
                    if (!aGone) { bad++; Console.WriteLine("[FAIL] 本目录记录里的 MOD 没删干净"); }
                    if (!bAlive) { bad++; Console.WriteLine("[FAIL] 还原动了别的游戏目录"); }
                    if (!dllGone) { bad++; Console.WriteLine("[FAIL] 记录过的 dxvk 文件没删"); }
                    if (!camKept) { bad++; Console.WriteLine("[FAIL] 没记录的 mqcam 文件或用户相机设置被删了"); }
                    bool bStillRecorded = File.ReadAllText(mf, Encoding.UTF8).Contains("别的目录.vpk");
                    if (!bStillRecorded) { bad++; Console.WriteLine("[FAIL] 还原后把另一个目录的记录弄丢了"); }
                    bool schemaWritten = text.Contains("Schema=" + ManifestSchema);
                    if (!schemaWritten) { bad++; Console.WriteLine("[FAIL] 新记录没写 Schema=" + ManifestSchema + "，还原时会被当成老记录"); }
                    Console.WriteLine("ADDON 装机记录分段：同目录合并=" + twoA + "，另一目录保留=" + hasB
                        + "，还原只删本目录=" + (aGone && bAlive) + "，只删记录过的组件=" + (dllGone && camKept)
                        + "，Schema=" + schemaWritten);

                    // 只装 MOD、没勾平台组件的新记录：还原绝不能按包清单去删用户自己放的 d3d9/dxvk/mqcam。
                    // 这是「Platform 为空」与「老记录不知道装了什么」必须分开的原因。
                    string tmpC = Path.Combine(Path.GetTempPath(), "l4d2-manifest-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                    try
                    {
                        string addonsC = Path.Combine(Path.Combine(tmpC, "left4dead2"), "addons");
                        Directory.CreateDirectory(addonsC);
                        Core.Cfg.GameRoot = tmpC;
                        Core.LogDir = Path.Combine(tmpC, "_装机记录");
                        Directory.CreateDirectory(Core.LogDir);
                        File.WriteAllText(Path.Combine(addonsC, "只装MOD.vpk"), "x");
                        File.WriteAllText(Path.Combine(tmpC, "d3d9.dll"), "x");
                        File.WriteAllText(Path.Combine(tmpC, "dxvk.conf"), "x");
                        string mqC = Path.Combine(tmpC, "bin\\neko\\mqcore");
                        Directory.CreateDirectory(mqC);
                        File.WriteAllText(Path.Combine(mqC, "mqcam.dll"), "x");

                        WriteManifest(tmpC, new List<string> { "只装MOD.vpk" }, null);   // 2 参重载 = 本次没有平台组件
                        FullRestore(tmpC);

                        bool modGone = !File.Exists(Path.Combine(addonsC, "只装MOD.vpk"));
                        bool userKept = File.Exists(Path.Combine(tmpC, "d3d9.dll"))
                                     && File.Exists(Path.Combine(tmpC, "dxvk.conf"))
                                     && File.Exists(Path.Combine(mqC, "mqcam.dll"));
                        if (!modGone) { bad++; Console.WriteLine("[FAIL] 只装 MOD 的记录：还原没删掉那个 vpk"); }
                        if (!userKept) { bad++; Console.WriteLine("[FAIL] 只装 MOD 的记录：还原把用户自己的 d3d9/dxvk/mqcam 删了"); }
                        Console.WriteLine("ADDON 只装MOD的还原：MOD已删=" + modGone + "，用户平台文件未被碰=" + userKept);
                    }
                    finally
                    {
                        Core.Cfg.GameRoot = savedGame;
                        Core.LogDir = savedLog;
                        try { Directory.Delete(tmpC, true); } catch { }
                    }

                    // 插件组件：装机记录里的 Plugin= 只删「这次装的独占文件」。
                    // 共用框架（CFX\Framework）不进记录，还原时必须留着 —— 删了别的 CFX 枪一起哑。
                    string tmpE = Path.Combine(Path.GetTempPath(), "l4d2-plug-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                    try
                    {
                        Core.Cfg.GameRoot = tmpE;
                        Core.LogDir = Path.Combine(tmpE, "_装机记录");
                        Directory.CreateDirectory(Core.LogDir);
                        string livePlug = Path.Combine(tmpE, "bin\\neko\\plugins");
                        Directory.CreateDirectory(Path.Combine(livePlug, "CFX\\Framework"));

                        string srcPlug = Path.Combine(tmpE, "src", "bin\\neko\\plugins");
                        Directory.CreateDirectory(Path.Combine(srcPlug, "CFX\\Framework"));
                        Directory.CreateDirectory(Path.Combine(srcPlug, "CFX\\Weapons\\Gun1"));
                        File.WriteAllText(Path.Combine(srcPlug, "loader.dll"), "loader");
                        File.WriteAllText(Path.Combine(srcPlug, "CFX\\Framework\\core.dll"), "framework");
                        File.WriteAllText(Path.Combine(srcPlug, "CFX\\Weapons\\Gun1\\gun.dll"), "gun");

                        PluginKit.ClearCache();
                        PluginPack pk = PluginKit.Load(srcPlug);
                        PluginResult pr = new PluginResult();
                        CopyPlugins(tmpE, new List<PluginPack> { pk }, null, pr);

                        bool gunIn = File.Exists(Path.Combine(livePlug, "CFX\\Weapons\\Gun1\\gun.dll"))
                                  && File.Exists(Path.Combine(livePlug, "loader.dll"));
                        bool fwIn = File.Exists(Path.Combine(livePlug, "CFX\\Framework\\core.dll"));
                        bool recOk = pr.Written.Count == 2 && pr.SharedWritten.Count == 1;
                        if (!gunIn) { bad++; Console.WriteLine("[FAIL] 插件：独占文件没写进 bin\\neko\\plugins"); }
                        if (!fwIn) { bad++; Console.WriteLine("[FAIL] 插件：共用框架没写进去"); }
                        if (!recOk) { bad++; Console.WriteLine("[FAIL] 插件：独占/共用分类记错了（独占 "
                            + pr.Written.Count + "、共用 " + pr.SharedWritten.Count + "）"); }

                        WriteManifest(tmpE, null, null, null, pr.Written);
                        FullRestore(tmpE);

                        bool gunOut = !File.Exists(Path.Combine(livePlug, "CFX\\Weapons\\Gun1\\gun.dll"))
                                   && !File.Exists(Path.Combine(livePlug, "loader.dll"));
                        bool fwKept = File.Exists(Path.Combine(livePlug, "CFX\\Framework\\core.dll"));
                        bool gunDirPruned = !Directory.Exists(Path.Combine(livePlug, "CFX\\Weapons\\Gun1"));
                        bool cfxKept = Directory.Exists(Path.Combine(livePlug, "CFX"));
                        if (!gunOut) { bad++; Console.WriteLine("[FAIL] 插件：还原没删掉记录里的独占文件"); }
                        if (!fwKept) { bad++; Console.WriteLine("[FAIL] 插件：还原把共用框架删了（别的 CFX 枪会一起哑）"); }
                        if (!gunDirPruned) { bad++; Console.WriteLine("[FAIL] 插件：删空的枪目录没清掉"); }
                        if (!cfxKept) { bad++; Console.WriteLine("[FAIL] 插件：把 CFX 框架目录整个删了"); }
                        Console.WriteLine("ADDON 插件组件：独占写入=" + gunIn + "，共用写入=" + fwIn
                            + "，分类正确=" + recOk + "，还原删独占=" + gunOut + "，共用保留=" + fwKept
                            + "，空目录已收=" + gunDirPruned);
                    }
                    finally
                    {
                        Core.Cfg.GameRoot = savedGame;
                        Core.LogDir = savedLog;
                        PluginKit.ClearCache();
                        try { Directory.Delete(tmpE, true); } catch { }
                    }

                    // 覆盖安装：旧版本先快照，还原时放回去。
                    // 以前覆盖完只记文件名，还原把文件删掉，用户原来那一版就永远没了。
                    string tmpD = Path.Combine(Path.GetTempPath(), "l4d2-snap-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                    bool savedOw = Overwrite;
                    try
                    {
                        string addonsD = Path.Combine(Path.Combine(tmpD, "left4dead2"), "addons");
                        Directory.CreateDirectory(addonsD);
                        string srcDir = Path.Combine(tmpD, "src");
                        Directory.CreateDirectory(srcDir);
                        Core.Cfg.GameRoot = tmpD;
                        Core.LogDir = Path.Combine(tmpD, "_装机记录");
                        Directory.CreateDirectory(Core.LogDir);

                        string live = Path.Combine(addonsD, "覆盖测试.vpk");
                        File.WriteAllText(live, "旧版内容-123");            // 用户原来那一版
                        string src = Path.Combine(srcDir, "覆盖测试.vpk");
                        File.WriteAllText(src, "新版内容-456789");          // 要装的版本（大小不同）

                        ModItem it = new ModItem();
                        it.Name = "覆盖测试.vpk";
                        it.FullPath = src;
                        it.Size = new FileInfo(src).Length;

                        int cD, sD;
                        List<string> aD, fD, bD;
                        Overwrite = true;
                        CopyMods(tmpD, new List<ModItem> { it }, null, out cD, out sD, out aD, out fD, out bD);
                        bool wrote = File.ReadAllText(live) == "新版内容-456789";
                        bool snap = bD.Count == 1;
                        WriteManifest(tmpD, aD, null, bD);
                        FullRestore(tmpD);
                        bool backOld = File.ReadAllText(live) == "旧版内容-123";
                        if (!wrote) { bad++; Console.WriteLine("[FAIL] 覆盖安装没写进新版本"); }
                        if (!snap) { bad++; Console.WriteLine("[FAIL] 覆盖前没有留旧版本快照"); }
                        if (!backOld) { bad++; Console.WriteLine("[FAIL] 还原后没把被覆盖的旧版本放回去"); }
                        Console.WriteLine("ADDON 覆盖安装回滚：写入新版本=" + wrote + "，覆盖前已快照=" + snap + "，还原后回到旧版=" + backOld);
                    }
                    finally
                    {
                        Overwrite = savedOw;
                        Core.Cfg.GameRoot = savedGame;
                        Core.LogDir = savedLog;
                        try { Directory.Delete(tmpD, true); } catch { }
                    }
                }
                finally
                {
                    Core.Cfg.Mode = savedMode;
                    Core.Cfg.GameRoot = savedGame;
                    Core.LogDir = savedLog;
                    try { Directory.Delete(tmpB, true); } catch { }
                }

                // 批量导入：*.7z 也要收 —— 识别、装机、冲突检查都支持 7z，只有「导入」原来只扫 rar/zip，
                // 对着一个装着 7z 的文件夹点导入会一个都不收，还不说为什么。
                string savedModRoot = Core.ModRoot;
                try
                {
                    string lib = Path.Combine(tmp, "lib");
                    string srcDir = Path.Combine(tmp, "待导入");
                    Directory.CreateDirectory(lib);
                    Directory.CreateDirectory(srcDir);
                    // 内容不重要：导入只负责搬运，7z 读不读得了是别处的事
                    File.WriteAllText(Path.Combine(srcDir, "ak47测试.7z"), "x");
                    File.WriteAllText(Path.Combine(srcDir, "ak47测试.zip"), "x");
                    Core.ModRoot = lib;
                    int imp, skp;
                    ImportVpks(srcDir, false, out imp, out skp);
                    string box = Path.Combine(Path.Combine(lib, "addons各类物品储藏库"), "AK47");
                    bool gotZip = File.Exists(Path.Combine(box, "ak47测试.zip"));
                    bool got7z = File.Exists(Path.Combine(box, "ak47测试.7z"));
                    if (!gotZip) { bad++; Console.WriteLine("[FAIL] 批量导入：zip 没被收进库"); }
                    if (!got7z) { bad++; Console.WriteLine("[FAIL] 批量导入：7z 没被收进库（导入只扫 rar/zip）"); }
                    Console.WriteLine("ADDON 批量导入：zip=" + gotZip + "，7z=" + got7z
                        + "（入库 " + imp + " 个，跳过 " + skp + " 个）");
                }
                finally
                {
                    Core.ModRoot = savedModRoot;
                    try { Core.ScanMods(); } catch { }   // 库根换回来了，把内存里的表重建回去
                }
            }
            catch (Exception ex)
            {
                bad++;
                Console.WriteLine("[FAIL] ADDON 自测抛异常: " + ex.Message);
            }
            finally
            {
                Core.Cfg.GameRoot = savedGameRoot;
                Core.Cfg.Mode = savedMode;
                Core.LogDir = savedLogDir;
                Core.SelfTesting = savedSelfTesting;
                Core.ModList = savedItems;
                Core.ByRel = savedByRel;
                Core.DirCount = savedDirCount;
                Catalog.Groups = savedGroups;
                Catalog.GroupByKey = savedGroupByKey;
                ModIdentify.ResetCacheForSelfTest();
                try { Directory.Delete(tmp, true); } catch { }
            }
            return bad;
        }

        /// <summary>
        /// 手动腾地方：把 addonlist 里明确「关闭」的 MOD 挪回 U 盘（addons回收），再从 addons 删掉。
        /// 只动「关着 + 库里认识」的；开着的一概不碰。复制校验通过才删源文件。
        /// </summary>
        public static int ParkDisabled(string gameRoot)
        {
            // 本地模式没有 U 盘库。以前这里照样把关掉的 vpk 复制到
            // <程序所在盘>\游戏内容\...\addons回收，再从游戏 addons 删掉 ——
            // 结果文件搬进一个用户从没见过的目录（那个目录本地模式根本不扫），
            // MOD 在列表里直接消失，提示却写着「已挪回 U 盘 N 个」。
            if (Core.IsLocal) return 0;
            Dictionary<string, bool> st = AddonList.Read(gameRoot);
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            if (!Directory.Exists(addons)) return 0;
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Core.ModList != null)
                foreach (ModItem m in Core.ModList) if (!string.IsNullOrEmpty(m.Name)) known.Add(m.Name);
            string dst = Path.Combine(Core.ModRoot, "addons回收");
            int n = 0;
            foreach (string f in Directory.GetFiles(addons, "*.vpk"))
            {
                string name = Path.GetFileName(f);
                bool on;
                if (!st.TryGetValue(name, out on) || on) continue;      // 只动明确关掉的
                if (!known.Contains(name)) continue;                     // 不认识的别碰
                try
                {
                    if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);
                    string target = Path.Combine(dst, name);
                    if (!Core.SameFile(f, target)) Core.CopyOne(f, target);
                    if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(f).Length)
                        throw new Exception("复制后大小对不上");
                    File.Delete(f);
                    n++;
                }
                catch (Exception ex) { Core.Log("WARN", "腾地方失败 " + name + ": " + ex.Message); }
            }
            Core.Log(n > 0 ? "OK" : "INFO", "清理关着的 MOD：" + (n > 0 ? "挪回 U 盘 " + n + " 个" : "没有可清理的"));
            return n;
        }

        // ---------- 跑外部程序并抓输出 ----------
        // ---------- 自检：插件对齐（--plugself 的后半段，临时假目录，不碰真游戏） ----------

        private static int SyncExpect(bool ok, string what)
        {
            if (ok) return 0;
            Console.WriteLine("SYNC FAIL " + what);
            return 1;
        }

        private static int SyncCount(int got, int want, string what)
        {
            if (got == want) return 0;
            Console.WriteLine("SYNC FAIL " + what + "：期望 " + want + "，实际 " + got);
            return 1;
        }

        /// <summary>
        /// 开关 → bin\neko\plugins 这条链：首次补齐、跑两次不变（幂等）、关掉只收独占、
        /// 开回来原位放回、游戏在跑一律不动；最后拿真实素材（RAM7 CFX 插件版）走一遍
        /// 「导入收包 → 对齐 → 关掉」。全程建在 %TEMP%，真游戏目录和真记录区都不碰。
        /// </summary>
        public static int PluginSyncSelfTest()
        {
            int bad = 0;
            string tmp = Path.Combine(Path.GetTempPath(), "syncself-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string savedLog = Core.LogDir, savedGame = Core.Cfg.GameRoot, savedMods = Core.ModRoot;
            bool? savedRunning = AddonList.ForceGameRunning;
            // 自检没法真起/真停游戏，直接按「没跑」验（第四步再手动按「在跑」）；本机开着游戏时也不影响自检
            AddonList.ForceGameRunning = false;
            try
            {
                string game = Path.Combine(tmp, "假游戏");
                Core.LogDir = Path.Combine(tmp, "记录");
                Directory.CreateDirectory(Core.LogDir);
                Directory.CreateDirectory(Path.Combine(Path.Combine(game, "left4dead2"), "addons"));
                Directory.CreateDirectory(Path.Combine(game, "bin"));
                File.WriteAllText(Path.Combine(game, "bin", "left4neko.dll"), "fake loader");

                // 假包：独占 3（plugins 根 2 + 枪目录 1）、共用 2（CFX 根配置 + Framework）。
                // 直接摆在记录区里，跟「导入收下之后」一模一样（导入那一步在后面真实素材那组验）。
                string plug = Path.Combine(PluginPackDir(), "a", "bin\\neko\\plugins");   // 记录按 vpk 名（无扩展名）存
                Directory.CreateDirectory(Path.Combine(plug, "CFX", "Framework"));
                Directory.CreateDirectory(Path.Combine(plug, "CFX", "Weapons", "Gun1"));
                File.WriteAllText(Path.Combine(plug, "loader.dll"), "loader");
                File.WriteAllText(Path.Combine(plug, "shu_weapon.dll"), "weapon");
                File.WriteAllText(Path.Combine(plug, "CFX", "cfx_host.ini"), "ini");
                File.WriteAllText(Path.Combine(plug, "CFX", "Framework", "core.dll"), "core");
                File.WriteAllText(Path.Combine(plug, "CFX", "Weapons", "Gun1", "gun.dll"), "gun");
                PluginKit.ClearCache();
                PluginPack pack = PluginKit.LoadCollected(Core.LogDir, "a.vpk");
                bad += SyncExpect(pack != null && pack.Files.Count == 5, "记录区里认出 5 个插件文件（3 独占 / 2 共用）");
                if (pack == null) return bad;

                string inGame = Path.Combine(game, "bin\\neko\\plugins");
                // 1) 首次对齐：缺的从包源补齐
                SyncResult r1 = new SyncResult();
                SyncPlugins(game, new List<PluginPack> { pack }, r1);
                bad += SyncCount(r1.Added.Count, 5, "首次对齐补了 5 个文件");
                bad += SyncExpect(File.Exists(Path.Combine(inGame, "loader.dll")), "首次对齐：独占 loader.dll 就位");
                bad += SyncExpect(File.Exists(Path.Combine(inGame, "CFX\\Framework\\core.dll")), "首次对齐：共用框架也补了");

                // 2) 再跑一次：一个文件都不该动（幂等）
                SyncResult r2 = new SyncResult();
                SyncPlugins(game, new List<PluginPack> { pack }, r2);
                bad += SyncCount(r2.Changes, 0, "二次对齐 0 变化（幂等）");

                // 3) 关掉这一套：独占进停用区，共用框架原地不动
                string park = DisabledPluginDir();
                SyncResult r3 = new SyncResult();
                SyncPlugins(game, new List<PluginPack>(), r3);
                bad += SyncCount(r3.Parked.Count, 3, "关掉后 3 个独占进停用区");
                bad += SyncExpect(!File.Exists(Path.Combine(inGame, "loader.dll")), "关掉后 loader.dll 离开游戏目录");
                bad += SyncExpect(File.Exists(Path.Combine(park, "bin\\neko\\plugins\\loader.dll")), "loader.dll 在停用区里（没删）");
                bad += SyncExpect(File.Exists(Path.Combine(park, "bin\\neko\\plugins\\CFX\\Weapons\\Gun1\\gun.dll")), "枪目录的插件也在停用区");
                bad += SyncExpect(File.Exists(Path.Combine(inGame, "CFX\\Framework\\core.dll")), "关掉后共用框架还在（别的包在用）");

                // 4) 再开回来：独占从停用区原位放回，共用不动
                SyncResult r4 = new SyncResult();
                SyncPlugins(game, new List<PluginPack> { pack }, r4);
                bad += SyncCount(r4.Added.Count, 3, "开回来补 3 个独占");
                bad += SyncCount(r4.Parked.Count, 0, "开回来没有再往停用区挪");
                bad += SyncExpect(File.Exists(Path.Combine(inGame, "loader.dll")), "loader.dll 回到原位");
                bad += SyncExpect(!File.Exists(Path.Combine(park, "bin\\neko\\plugins\\loader.dll")), "停用区里那份已经挪走");

                // 5) 游戏在跑：缺的也不补，一个文件都不碰
                File.Delete(Path.Combine(inGame, "shu_weapon.dll"));
                AddonList.ForceGameRunning = true;
                SyncResult r5 = new SyncResult();
                SyncPlugins(game, new List<PluginPack> { pack }, r5);
                AddonList.ForceGameRunning = false;
                bad += SyncExpect(r5.GameRunning, "游戏在跑：报「跳过（游戏在跑）」");
                bad += SyncCount(r5.Changes, 0, "游戏在跑：0 变化");
                bad += SyncExpect(!File.Exists(Path.Combine(inGame, "shu_weapon.dll")), "游戏在跑：缺的文件也没补");

                // 6) 真实素材：完整链路（这台机器上没有素材就跳过，不算失败）
                bad += RealPluginChain(Path.Combine(tmp, "真链"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("SYNC 自检异常: " + ex.Message);
                bad++;
            }
            finally
            {
                Core.LogDir = savedLog;
                Core.Cfg.GameRoot = savedGame;
                Core.ModRoot = savedMods;
                AddonList.ForceGameRunning = savedRunning;
                PluginKit.ClearCache();
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
            Console.WriteLine("SYNC 自检失败数 = " + bad);
            return bad;
        }

        // 真实素材（只读）：RAM7 的 CFX 插件版。换台机器没有就跳过，不判失败。
        private const string RealPluginSample =
            @"D:\游戏内容\求生之路2各项MOD\addons各类物品储藏库\MAC-10\RAM7 星月夜（CFX 插件版）";

        /// <summary>
        /// 真实素材的完整链路：导入两个 vpk 时把旁边那套 44 个插件收进记录区 → 打开时铺进假游戏目录
        /// → 关掉后只收 2 个独占、42 个共用框架留下 → 再开回来 44 个齐。假游戏目录，素材只读。
        /// </summary>
        private static int RealPluginChain(string tmp)
        {
            int bad = 0;
            if (!Directory.Exists(RealPluginSample))
            {
                Console.WriteLine("SYNC 跳过真实素材链路（这台机器没有 RAM7 素材）");
                return 0;
            }
            string savedLog = Core.LogDir, savedGame = Core.Cfg.GameRoot, savedMods = Core.ModRoot;
            try
            {
                string game = Path.Combine(tmp, "游戏");
                Directory.CreateDirectory(Path.Combine(Path.Combine(game, "left4dead2"), "addons"));
                Directory.CreateDirectory(Path.Combine(game, "bin"));
                File.WriteAllText(Path.Combine(game, "bin", "left4neko.dll"), "fake loader");
                Core.LogDir = Path.Combine(tmp, "记录");
                Directory.CreateDirectory(Core.LogDir);
                Core.Cfg.GameRoot = game;
                Core.ModRoot = Path.Combine(tmp, "库");
                Directory.CreateDirectory(Core.ModRoot);
                PluginKit.ClearCache();

                // 导入：两个真 vpk + 旁边那套插件
                int imported = 0, skipped = 0;
                List<string> lines = ImportVpks(RealPluginSample, false, out imported, out skipped);
                Console.WriteLine("SYNC 真实素材：导入 " + imported + " 个 vpk，跳过 " + skipped);
                bad += SyncCount(imported, 2, "真实素材两个 vpk 都收进库");
                if (lines != null)
                    for (int i = 0; i < lines.Count && i < 8; i++) Console.WriteLine("SYNC   · " + lines[i]);

                string[] records = Directory.GetDirectories(PluginPackDir());
                bad += SyncCount(records.Length, 2, "两个 vpk 各收一份插件（插件包\\<vpk 名>\\）");
                int per = 0;
                if (records.Length > 0)
                    per = Directory.GetFiles(Path.Combine(records[0], "bin\\neko\\plugins"), "*", SearchOption.AllDirectories).Length;
                Console.WriteLine("SYNC 真实素材：每份记录 " + per + " 个插件文件");
                bad += SyncCount(per, 44, "记录区每份 44 个插件文件");

                // 再导一次：vpk 已在库会被跳过 —— 插件那条也得走（最容易漏的一支）。
                // 先删掉记录里一个文件，好验证「跳过」时插件确实被补回来，而不是整支被跳过
                string probe = Path.Combine(records[0], "bin\\neko\\plugins\\shu_weapon.dll");
                File.Delete(probe);
                int imported2 = 0, skipped2 = 0;
                List<string> lines2 = ImportVpks(RealPluginSample, false, out imported2, out skipped2);
                bool sawExist = false;
                foreach (string l in lines2) if (l.IndexOf("已存在", StringComparison.Ordinal) >= 0) sawExist = true;
                bad += SyncCount(imported2, 0, "二次导入 0 个 vpk 新入库");
                bad += SyncExpect(sawExist, "二次导入走了「已存在，跳过」那一支");
                bad += SyncExpect(File.Exists(probe), "vpk 被跳过时插件照样补收回来");
                bad += SyncCount(Directory.GetDirectories(PluginPackDir()).Length, 2, "二次导入后记录区仍是 2 份（没重复堆）");
                bad += SyncCount(Directory.GetFiles(Path.Combine(records[0], "bin\\neko\\plugins"), "*", SearchOption.AllDirectories).Length, 44, "二次导入后每份记录仍是 44 个文件");

                // 打开：addonlist 写 1，vpk 在 addons 里（判据只看名字在不在，内容不重要）
                Dictionary<string, bool> on = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (string v in Directory.GetFiles(RealPluginSample, "*.vpk"))
                {
                    string name = Path.GetFileName(v);
                    File.WriteAllText(Path.Combine(Path.Combine(Path.Combine(game, "left4dead2"), "addons"), name), "");
                    on[name] = true;
                }
                AddonList.Write(game, on);
                List<PluginPack> packs = Catalog.EnabledPluginPacks(game);
                bad += SyncCount(packs.Count, 2, "开着的插件包认出 2 份记录");

                string inGame = Path.Combine(game, "bin\\neko\\plugins");
                SyncPlugins(game, packs, new SyncResult());
                int got = Directory.GetFiles(inGame, "*", SearchOption.AllDirectories).Length;
                Console.WriteLine("SYNC 真实素材：铺进游戏 " + got + " 个文件");
                bad += SyncCount(got, 44, "44 个插件文件全在游戏目录");

                // 关掉：2 个独占进停用区，42 个共用框架留
                SyncPlugins(game, new List<PluginPack>(), new SyncResult());
                int left = Directory.GetFiles(inGame, "*", SearchOption.AllDirectories).Length;
                int parked = Directory.GetFiles(DisabledPluginDir(), "*", SearchOption.AllDirectories).Length;
                Console.WriteLine("SYNC 真实素材：关掉后游戏里留 " + left + " 个（共用），停用区 " + parked + " 个（独占）");
                bad += SyncCount(left, 42, "关掉后 42 个共用框架原地不动");
                bad += SyncCount(parked, 2, "关掉后 2 个独占进停用区");
                bad += SyncExpect(File.Exists(Path.Combine(DisabledPluginDir(), "bin\\neko\\plugins\\shu_weapon.dll")), "shu_weapon.dll 在停用区");
                bad += SyncExpect(File.Exists(Path.Combine(DisabledPluginDir(), "bin\\neko\\plugins\\CFX\\Weapons\\RAM7_Starlit\\CfxRam7Starlit.dll")), "CfxRam7Starlit.dll 在停用区");

                // 再开回来：44 个齐
                SyncPlugins(game, Catalog.EnabledPluginPacks(game), new SyncResult());
                int again = Directory.GetFiles(inGame, "*", SearchOption.AllDirectories).Length;
                bad += SyncCount(again, 44, "再开回来 44 个插件文件都到位");
            }
            catch (Exception ex)
            {
                Console.WriteLine("SYNC 真实素材链路异常: " + ex.Message);
                bad++;
            }
            finally
            {
                Core.LogDir = savedLog;
                Core.Cfg.GameRoot = savedGame;
                Core.ModRoot = savedMods;
                PluginKit.ClearCache();
            }
            return bad;
        }
    }
}