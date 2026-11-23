using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Installer
{
    /// <summary>
    /// 工坊下载的一条记录。运行中的那条 Status = downloading，Got/Total 会实时更新，
    /// 所以下载列表窗口不用自己记账，订阅 WorkshopDownloads.Changed 刷新就行。
    /// </summary>
    internal sealed class WsDownload
    {
        public string Id = "";
        public string Title = "";
        public string Url = "";          // 直链，重下时直接用，不必再查一次接口
        public string Path = "";         // 落盘后的完整路径（改过名就是改后的）
        public string Dir = "";          // 下载时的保存位置，换过位置后还知道它原来落在哪
        public long Size;                // 成功记录 = 文件字节数；运行中 = 总字节（未知时 0）
        public long Got;                 // 运行中：已下载字节
        public string Status = "ok";     // downloading / ok / fail / cancel
        public string Error = "";
        public DateTime Time = DateTime.Now;

        public bool Running { get { return Status == "downloading"; } }
        public bool Paused { get { return Status == "paused"; } }

        public bool ExistsOnDisk
        {
            get
            {
                try { return Path.Length > 0 && File.Exists(Path); }
                catch { return false; }
            }
        }

        public string SizeText
        {
            get
            {
                if (Running)
                    return Total() > 0 ? Core.Human(Got) + " / " + Core.Human(Total()) : Core.Human(Got);
                return Size > 0 ? Core.Human(Size) : "—";
            }
        }

        private long Total() { return Size; }

        public string StateText
        {
            get
            {
                if (Running)
                {
                    int pct = Size > 0 ? (int)Math.Min(100, Got * 100 / Size) : 0;
                    return Size > 0 ? "下载中 " + pct + "%" : "下载中…";
                }
                if (Status == "paused")
                {
                    int pct = Size > 0 ? (int)Math.Min(100, Got * 100 / Size) : 0;
                    return Size > 0 ? "已暂停 " + pct + "%（点「继续」接着下）" : "已暂停（点「继续」接着下）";
                }
                if (Status == "ok") return ExistsOnDisk ? "已下载" : "文件已删除";
                if (Status == "cancel") return "已取消";
                return "失败" + (Error.Length > 0 ? "：" + Error : "");
            }
        }
    }

    /// <summary>
    /// 工坊下载记录：成功 / 失败 / 取消 / 进行中，都记在这一处。
    /// 数据落在 _装机记录\workshop-downloads.tsv（纯文本，出问题能直接看）。
    ///
    /// 为什么不复用 workshop-idmap.txt：那个只有 id→文件名，是给卡片标「已下载」用的；
    /// 列表要显示时间、大小、状态、完整路径，还要支持重下，字段不够。
    /// </summary>
    internal static class WorkshopDownloads
    {
        private const int MaxRecords = 300;

        private static readonly object gate = new object();
        private static readonly List<WsDownload> items = new List<WsDownload>();   // 最新的在前
        private static bool loaded;

        /// <summary>记录有变化（新增 / 进度 / 结束 / 删除）。列表窗口订阅它刷新。</summary>
        public static event Action Changed;

        /// <summary>
        /// 每条下载的暂停 / 取消开关：下载线程在每个读循环里查它。
        /// 这里只负责把开关递到线程手上，状态什么时候变由线程自己停下来后调 Pause / Cancel —— 那样记下的进度才是真的。
        /// </summary>
        private static readonly Dictionary<string, WsNet.DlCtl> ctls = new Dictionary<string, WsNet.DlCtl>(StringComparer.Ordinal);

        public static WsNet.DlCtl Ctl(string id)
        {
            Ensure();
            string k = id ?? "";
            lock (gate)
            {
                WsNet.DlCtl c;
                if (ctls.TryGetValue(k, out c)) return c;
                c = new WsNet.DlCtl(k);
                ctls[k] = c;
                return c;
            }
        }

        public static void RequestPause(string id) { WsNet.DlCtl c = FindCtl(id); if (c != null) c.Pause = true; }
        public static void RequestCancel(string id) { WsNet.DlCtl c = FindCtl(id); if (c != null) c.Cancel = true; }
        public static bool PauseRequested(string id) { WsNet.DlCtl c = FindCtl(id); return c != null && c.Pause; }

        private static WsNet.DlCtl FindCtl(string id)
        {
            lock (gate) { WsNet.DlCtl c; return ctls.TryGetValue(id ?? "", out c) ? c : null; }
        }

        private static void DropCtl(string id) { lock (gate) { ctls.Remove(id ?? ""); } }

        public static string StorePath { get { return Path.Combine(Core.LogDir, "workshop-downloads.tsv"); } }

        private static void Ensure()
        {
            if (loaded) return;
            loaded = true;
            try { Read(); } catch { }
        }

        /// <summary>全部记录（最新的在前）。运行中的排最前，方便一眼看到在下的那个。</summary>
        public static List<WsDownload> All()
        {
            Ensure();
            lock (gate)
            {
                List<WsDownload> running = new List<WsDownload>();
                List<WsDownload> done = new List<WsDownload>();
                foreach (WsDownload d in items)
                    (d.Running ? running : done).Add(d);
                running.AddRange(done);
                return running;
            }
        }

        public static int Count { get { Ensure(); lock (gate) { return items.Count; } } }

        public static int RunningCount
        {
            get
            {
                Ensure();
                lock (gate)
                {
                    int n = 0;
                    foreach (WsDownload d in items) if (d.Running) n++;
                    return n;
                }
            }
        }

        /// <summary>开始下载：先插一条运行中的记录，列表里立刻能看到它在动。</summary>
        public static void Begin(string id, string title, string url, string dir)
        {
            Ensure();
            WsDownload d = new WsDownload();
            d.Id = id ?? "";
            d.Title = title ?? "";
            d.Url = url ?? "";
            d.Dir = dir ?? "";
            d.Status = "downloading";
            d.Time = DateTime.Now;
            lock (gate)
            {
                // 同一个 ID 又在下了：把旧的运行中 / 暂停中的记录顶掉，别同时挂两条
                items.RemoveAll(delegate(WsDownload x) { return (x.Running || x.Status == "paused") && x.Id == d.Id; });
                items.Insert(0, d);
            }
            Fire();
        }

        public static void Progress(string id, long got, long total)
        {
            Ensure();
            bool hit = false;
            lock (gate)
            {
                foreach (WsDownload d in items)
                {
                    if (!d.Running || d.Id != id) continue;
                    d.Got = got;
                    if (total > 0) d.Size = total;
                    hit = true;
                    break;
                }
            }
            if (hit) Fire();
        }

        public static void Finish(string id, bool ok, string path, long size, string error)
        {
            Ensure();
            lock (gate)
            {
                foreach (WsDownload d in items)
                {
                    if (!d.Running || d.Id != id) continue;
                    d.Status = ok ? "ok" : "fail";
                    d.Path = path ?? "";
                    if (size > 0) d.Size = size;
                    d.Error = ok ? "" : Clean(error);
                    d.Time = DateTime.Now;
                    break;
                }
                Trim();
            }
            Save();
            Fire();
        }

        public static void Cancel(string id)
        {
            Ensure();
            lock (gate)
            {
                foreach (WsDownload d in items)
                {
                    if (d.Id != id || d.Status == "cancel") continue;
                    if (!d.Running && d.Status != "paused") continue;
                    d.Status = "cancel";
                    d.Time = DateTime.Now;
                    break;
                }
            }
            DropCtl(id);
            Save();
            Fire();
        }

        /// <summary>下载线程收到暂停后调这个：状态改「已暂停」，进度和分片都留着，等「继续」接着下。</summary>
        public static void Pause(string id)
        {
            Ensure();
            lock (gate)
            {
                foreach (WsDownload d in items)
                {
                    if (!d.Running || d.Id != id) continue;
                    d.Status = "paused";
                    d.Time = DateTime.Now;
                    break;
                }
            }
            DropCtl(id);
            Save();
            Fire();
        }

        public static void Remove(WsDownload d)
        {
            if (d == null) return;
            Ensure();
            lock (gate) { items.Remove(d); }
            Save();
            Fire();
        }

        private static void Trim()
        {
            if (items.Count <= MaxRecords) return;
            // 从尾部丢最旧的；运行中的绝不丢
            for (int i = items.Count - 1; i >= 0 && items.Count > MaxRecords; i--)
                if (!items[i].Running) items.RemoveAt(i);
        }

        private static void Fire()
        {
            Action h = Changed;
            if (h == null) return;
            try { h(); } catch { }
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static void Read()
        {
            string p = StorePath;
            if (!File.Exists(p)) return;
            string[] lines = File.ReadAllLines(p, Encoding.UTF8);
            lock (gate)
            {
                items.Clear();
                foreach (string line in lines)
                {
                    if (line.Length == 0) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 6) continue;
                    WsDownload d = new WsDownload();
                    DateTime t;
                    if (DateTime.TryParse(f[0], out t)) d.Time = t;
                    d.Id = f[1];
                    d.Title = f[2];
                    d.Path = f[3];
                    long sz;
                    if (long.TryParse(f[4], out sz)) d.Size = sz;
                    d.Status = f[5];
                    // 上次没写完就退出了：不该继续显示"下载中"，改成中断
                    if (d.Status == "downloading") d.Status = "cancel";
                    d.Url = f.Length > 6 ? f[6] : "";
                    d.Error = f.Length > 7 ? f[7] : "";
                    d.Dir = d.Path.Length > 0 ? SafeDir(d.Path) : "";
                    items.Add(d);
                }
            }
        }

        private static string SafeDir(string file)
        {
            try { return Path.GetDirectoryName(file) ?? ""; } catch { return ""; }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Core.LogDir);
                StringBuilder sb = new StringBuilder();
                lock (gate)
                {
                    foreach (WsDownload d in items)
                    {
                        if (d.Running) continue;      // 运行中的不落盘，重启后不该是"在下载"
                        sb.AppendLine(string.Join("\t", new string[] {
                            d.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                            Clean(d.Id), Clean(d.Title), Clean(d.Path),
                            d.Size.ToString(), Clean(d.Status), Clean(d.Url), Clean(d.Error)
                        }));
                    }
                }
                Core.WriteAllTextAtomic(StorePath, sb.ToString(), new UTF8Encoding(false));   // 断电也不会写坏成半截
            }
            catch (Exception ex) { Core.Log("WARN", "写下载记录失败: " + ex.Message); }
        }
    }
}
