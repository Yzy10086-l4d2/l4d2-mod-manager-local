using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>创意工坊物品的元数据（GetPublishedFileDetails 的一行）。</summary>
    internal sealed class WsItem
    {
        public string Id = "";
        public string Title = "";
        public long Size;
        public string Url = "";
        public string FileName = "";        // 接口给的内部存储路径，不是文件名
        public bool IsCollection;
        public string Note = "";            // 「没有直链」这类说明

        /// <summary>详情页要用的字段：同一个响应里本来就有，以前一个都没解析。</summary>
        public string Desc = "";            // file_description，原文是 BBCode
        public string Preview = "";         // preview_url
        public long Created, Updated;       // time_created / time_updated（Unix 秒）
        public long Subs, Views;            // subscriptions / views
        public string Creator = "";         // 作者 steamid
        public string Tags = "";            // "枪械 · 模型" 这种拼好的标签串
        public string Where = "";           // 下载后识别出来的「替换 AK-47 · 模型」

        public string State = "待下载";
        public bool Ok;                     // 已经落盘
        public bool DetailsLoaded;          // 详情接口是否已补全（浏览页只有短介绍）
    }

    /// <summary>
    /// 创意工坊：查元数据 + 直链下载。
    ///
    /// L4D2 的物品都是单个 vpk，GetPublishedFileDetails 会给出 file_url，可以直接下，
    /// 不需要 steamcmd、也不需要登录。两点必须记住：
    ///   1) 接口本身不要 API key，但一次最多 100 个 ID；
    ///   2) 下载那一步必须带浏览器 UA —— 裸请求（curl 默认 UA）会被 CDN 回 404，
    ///      地址明明是对的，看起来像「物品不存在」，很容易被误导。
    /// 文件夹式的物品（别的游戏居多）file_url 是空的，这类只能走 Steam 客户端订阅，
    /// 所以这里留着 Note 字段把它说清楚，而不是报一个看不懂的错。
    /// </summary>
    /// <summary>
    /// 工坊联网层（2026-11-21）：
    ///   1. 一次性调好 .NET 的全局设置 —— 默认连接数只有 2，封面并发下载会被串成两条线；
    ///      再把 IE 代理探测关掉（Proxy=null），那一步在有些机器上能卡好几秒。
    ///   2. Steam 的几个域名先测一遍候选 IP（TCP 443 连一下看谁快），固定用最快的那个发请求，
    ///      并用 Host 头保留原域名 —— 这就是 LytVpk「优选 IP」的做法，绕开解析到慢/被墙的地址。
    ///   3. 优选 IP 那次失败就把它拉黑，退回正常域名重试一次，不让「加速」变成「连不上」。
    /// </summary>
    internal static class WsNet
    {
        static bool tuned;
        static readonly object gate = new object();
        static readonly Dictionary<string, string> pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, bool> skip = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static readonly string[] PinHosts = new string[]
        {
            "steamcommunity.com", "cdn.steamusercontent.com",
            "images.steamusercontent.com", "api.steampowered.com"
        };

        public static void Tune()
        {
            lock (gate) { if (tuned) return; tuned = true; }
            try { ServicePointManager.DefaultConnectionLimit = 12; } catch { }
            try { ServicePointManager.Expect100Continue = false; } catch { }
            // .NET Framework 没有 ServicePointManager.Proxy；关掉的是默认代理（那一步会去问 IE 设置）
            try { WebRequest.DefaultWebProxy = null; } catch { }
            try
            {
                SecurityProtocolType t = SecurityProtocolType.Tls12;
                try { t = t | (SecurityProtocolType)12288; } catch { }
                ServicePointManager.SecurityProtocol = t;
            }
            catch { }
            try { ServicePointManager.ServerCertificateValidationCallback = CertOk; } catch { }
            System.Threading.Tasks.Task.Run(delegate
            {
                foreach (string h in PinHosts) { try { Dns.GetHostAddresses(h); } catch { } }
                foreach (string h in PinHosts) { try { Pick(h); } catch { } }
            });
        }

        /// <summary>优选 IP 时 URL 换成了 IP，证书名字必然对不上 —— 只对 Steam 那几个域名放行这一种错误。</summary>
        static bool CertOk(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors err)
        {
            if (err == SslPolicyErrors.None) return true;
            if ((err & SslPolicyErrors.RemoteCertificateNameMismatch) == 0) return false;
            try
            {
                HttpWebRequest r = sender as HttpWebRequest;
                return r != null && IsPinHost(r.Host ?? "");
            }
            catch { return false; }
        }

        static bool IsPinHost(string host)
        {
            foreach (string h in PinHosts) if (string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string GetString(string url, string ua, int timeoutMs)
        {
            byte[] b = GetBytes(url, ua, timeoutMs);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        public static byte[] GetBytes(string url, string ua, int timeoutMs)
        {
            Tune();
            byte[] first = TryGet(url, ua, timeoutMs, true);
            if (first != null) return first;
            return TryGet(url, ua, timeoutMs, false);
        }

        static byte[] TryGet(string url, string ua, int timeoutMs, bool allowPin)
        {
            Uri u = null;
            try { u = new Uri(url); } catch { }
            try
            {
                string pinnedUrl = null;
                if (allowPin && u != null)
                {
                    string ip = Pick(u.Host);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        UriBuilder ub = new UriBuilder(u);
                        ub.Host = ip;
                        pinnedUrl = ub.Uri.AbsoluteUri;
                    }
                }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(pinnedUrl ?? url);
                if (pinnedUrl != null) req.Host = u.Host;
                req.Method = "GET";
                req.UserAgent = ua;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.KeepAlive = true;
                try { req.Proxy = null; } catch { }
                try { req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[16 * 1024];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    byte[] data = ms.ToArray();
                    if (data.Length == 0 && pinnedUrl != null) { if (u != null) DropPin(u.Host); return null; }
                    return data;
                }
            }
            catch
            {
                if (allowPin && u != null) DropPin(u.Host);
                return null;
            }
        }

        /// <summary>大文件下载：同样走优选 IP，失败退回域名重来一次；写到 part 路径，边下边报进度。</summary>
        public static long DownloadTo(string url, string ua, int timeoutMs, string partPath, long totalHint, Action<long, long> progress)
        {
            Tune();
            long got = TryDownload(url, ua, timeoutMs, partPath, totalHint, progress, true);
            if (got > 0) return got;
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            return TryDownload(url, ua, timeoutMs, partPath, totalHint, progress, false);
        }

        static long TryDownload(string url, string ua, int timeoutMs, string partPath, long totalHint, Action<long, long> progress, bool allowPin)
        {
            Uri u = null;
            try { u = new Uri(url); } catch { }
            try
            {
                string pinnedUrl = null;
                if (allowPin && u != null)
                {
                    string ip = Pick(u.Host);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        UriBuilder ub = new UriBuilder(u);
                        ub.Host = ip;
                        pinnedUrl = ub.Uri.AbsoluteUri;
                    }
                }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(pinnedUrl ?? url);
                if (pinnedUrl != null) req.Host = u.Host;
                req.UserAgent = ua;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = 60000;
                req.AllowAutoRedirect = true;
                req.KeepAlive = true;
                try { req.Proxy = null; } catch { }
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                {
                    long total = r.ContentLength > 0 ? r.ContentLength : totalHint;
                    using (Stream src = r.GetResponseStream())
                    using (FileStream fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    {
                        byte[] buf = new byte[65536];
                        long got = 0;
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                            got += n;
                            if (progress != null) progress(got, total);
                        }
                        fs.Flush();
                        return got;
                    }
                }
            }
            catch
            {
                if (allowPin && u != null) DropPin(u.Host);
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                return -1;
            }
        }

        static string Pick(string host)
        {
            if (!IsPinHost(host)) return null;
            lock (gate) { string ip; if (pinned.TryGetValue(host, out ip)) return ip; if (skip.ContainsKey(host)) return null; }
            string best = Probe(host);
            lock (gate) { if (best != null) pinned[host] = best; else skip[host] = true; }
            if (best != null) { try { Core.Log("INFO", "工坊优选 IP: " + host + " → " + best); } catch { } }
            return best;
        }

        static void DropPin(string host)
        {
            lock (gate) { pinned.Remove(host); skip[host] = true; }
            try { Core.Log("WARN", "工坊优选 IP 失败，改用域名: " + host); } catch { }
        }

        /// <summary>候选 IP 各连一次 443，取毫秒数最小的（最多 4 个，单个上限 700ms）。</summary>
        static string Probe(string host)
        {
            System.Net.IPAddress[] ips;
            try { ips = Dns.GetHostAddresses(host); } catch { return null; }
            if (ips == null || ips.Length == 0) return null;
            int take = Math.Min(ips.Length, 4);
            int[] ms = new int[take];
            for (int i = 0; i < take; i++) ms[i] = -1;
            System.Threading.Tasks.Parallel.For(0, take, delegate(int i) { ms[i] = TcpProbe(ips[i].ToString(), 443, 700); });
            int best = -1;
            for (int i = 0; i < take; i++)
                if (ms[i] >= 0 && (best < 0 || ms[i] < ms[best])) best = i;
            return best >= 0 ? ips[best].ToString() : null;
        }

        static int TcpProbe(string ip, int port, int timeoutMs)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                using (TcpClient c = new TcpClient())
                {
                    System.Threading.Tasks.Task t = c.ConnectAsync(ip, port);
                    if (!t.Wait(timeoutMs)) return -1;
                    sw.Stop();
                    return (int)sw.ElapsedMilliseconds;
                }
            }
            catch { return -1; }
        }
    }

    internal static class Workshop
    {
        public const string AppId = "550";
        const string ApiRoot = "https://api.steampowered.com/ISteamRemoteStorage/";
        const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) L4D2Setup/2.51";

        static readonly Regex RxLink = new Regex(@"sharedfiles/filedetails/?\?id=(\d{4,})", RegexOptions.IgnoreCase);
        static readonly Regex RxNum = new Regex(@"^\d{4,}$");

        // ---------------------------------------------------------- 解析输入

        /// <summary>
        /// 从一段文本里抠出物品 ID：完整链接、纯数字、逗号/空格/换行分隔都能认。
        /// 先按分隔符切开逐个判断，再对整段兜一次「链接连着贴」的情况，最后按出现顺序去重。
        /// </summary>
        public static List<string> ParseIds(string text)
        {
            List<string> ids = new List<string>();
            if (text == null) return ids;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            char[] sep = new char[] { '\r', '\n', ',', '，', ';', '；', ' ', '\t', '"', '\'', '|' };
            foreach (string raw in text.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = raw.Trim();
                if (s.Length == 0) continue;
                Match m = RxLink.Match(s);
                if (m.Success) s = m.Groups[1].Value;
                else if (!RxNum.IsMatch(s)) continue;
                if (seen.Add(s)) ids.Add(s);
            }

            foreach (Match m in RxLink.Matches(text))
            {
                string id = m.Groups[1].Value;
                if (seen.Add(id)) ids.Add(id);
            }
            return ids;
        }

        // ---------------------------------------------------------- 浏览 / 搜索

        static readonly Regex RxAnchor = new Regex(@"<a\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex RxId = new Regex(@"[?&]id=(\d+)");
        // 新版工坊页是 JS 渲染：结果在一段转义 JSON 里，形如 \"publishedfileid\":\"3832210318\"
        static readonly Regex RxJsonId = new Regex(@"publishedfileid[\\""]*:[\\""]*(\d+)");

        static string Get(string url)
        {
            // 走联网层：连接数/代理/协议调好，Steam 域名优选 IP，失败自动退回域名
            string s = WsNet.GetString(url, Ua, 25000);
            if (s == null) throw new WebException("工坊请求失败: " + url);
            return s;
        }

        /// <summary>
        /// 按关键词浏览工坊。走的是公开网页（workshop/browse），不要 API key，
        /// 因为 QueryFiles 那个接口要 key —— 为了看个列表不值得让用户去申请。
        /// 一页 30 个，id 从结果卡片里取，别把整个页面的 ?id= 都抓来（侧栏推荐会混进去）。
        /// </summary>
        public static List<string> Search(string query, string sort, int page)
        {
            StringBuilder u = new StringBuilder();
            u.Append("https://steamcommunity.com/workshop/browse/?appid=").Append(AppId);
            u.Append("&section=readytouseitems");
            if (!string.IsNullOrEmpty(sort)) u.Append("&browsesort=").Append(sort);
            if (!string.IsNullOrEmpty(query)) u.Append("&searchtext=").Append(Uri.EscapeDataString(query));
            if (page > 1) u.Append("&p=").Append(page);
            return ParseBrowse(Get(u.ToString()));
        }

        /// <summary>
        /// 搜索并直接解析出完整条目 —— 浏览页内嵌的 JSON 本来就带
        /// title / subscriptions / file_size / preview_url / tags / time_updated / file_url，
        /// 以前只抽 ID 再去详情接口批量换元数据，白白多一趟网络（每页 1~2 秒）。
        /// 解析失败时回退老路径（Search + Fetch），保证功能不退化。
        /// </summary>
        internal static List<WsItem> SearchItems(string query, string sort, int page, string tag = null)
        {
            StringBuilder u = new StringBuilder();
            u.Append("https://steamcommunity.com/workshop/browse/?appid=").Append(AppId);
            u.Append("&section=readytouseitems");
            if (!string.IsNullOrEmpty(sort)) u.Append("&browsesort=").Append(sort);
            if (!string.IsNullOrEmpty(query)) u.Append("&searchtext=").Append(Uri.EscapeDataString(query));
            // 标签过滤比文本准：地图只有 Campaigns 标签、特感只有 Special Infected 标签，光靠文本词搜不准
            if (!string.IsNullOrEmpty(tag)) u.Append("&requiredtags%5B%5D=").Append(Uri.EscapeDataString(tag));
            if (page > 1) u.Append("&p=").Append(page);
            List<WsItem> outp = ParseBrowseItems(Get(u.ToString()));
            if (outp.Count > 0) return outp;

            // 回退：老路径（抽 ID → 详情接口）
            List<string> ids = ParseBrowse(Get(u.ToString()));
            return ids.Count > 0 ? Fetch(ids) : outp;
        }

        /// <summary>从浏览页 HTML 提取转义 JSON 数组，反转义一层后按条目拆开。</summary>
        internal static List<WsItem> ParseBrowseItems(string html)
        {
            List<WsItem> outp = new List<WsItem>();
            if (string.IsNullOrEmpty(html)) return outp;

            // 条目数据是「JSON 套 JSON」——必须先整体反转义一层，再按普通 JSON 扫描。
            // 在转义文本上直接扫会被两层转义骗（实测 30 个条目一个都提不出来）。
            string json = html.Replace("\\\\", "\u0001").Replace("\\\"", "\"").Replace("\u0001", "\\\\");
            int at = json.IndexOf("\"results\"", StringComparison.Ordinal);
            if (at < 0) return outp;
            int i = json.IndexOf('[', at);
            if (i < 0) return outp;
            int depth = 0, end = -1;
            bool inStr = false, esc = false;
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end < 0) return outp;

            // results 数组本体不含 "publishedfiledetails" 键，包一层借 SplitObjects 拆条目
            string arr = json.Substring(i, end - i + 1);
            List<string> objs = SplitObjects("{\"x\":" + arr + "}", "x");
            foreach (string ob in objs)
            {
                WsItem it = new WsItem();
                it.Id = JStr(ob, "publishedfileid");
                if (it.Id.Length == 0) continue;
                it.Title = JStr(ob, "title").Trim();
                it.Size = JNum(ob, "file_size");
                it.Url = JStr(ob, "file_url");
                it.Preview = JStr(ob, "preview_url");
                it.Created = JNum(ob, "time_created");
                it.Updated = JNum(ob, "time_updated");
                it.Subs = JNum(ob, "subscriptions");
                it.Views = JNum(ob, "views");
                it.Tags = TagsOf(ob);
                it.Desc = JStr(ob, "short_description");     // 短介绍兜底，全文在点开时按需补
                outp.Add(it);
            }
            return outp;
        }

        internal static List<string> ParseBrowse(string html)
        {
            List<string> ids = new List<string>();
            if (string.IsNullOrEmpty(html)) return ids;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            // 新版页面是 JS 渲染，<a> 里根本没有链接；结果在一整段转义 JSON 里，
            // publishedfileid 键值对按文档顺序出现，顺序就是页面上的排序。
            foreach (Match m in RxJsonId.Matches(html))
                if (seen.Add(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
            if (ids.Count > 0) return ids;

            // 老版页面兜底：结果卡片 <a class="workshopItem..."> 的链接在标签里。
            foreach (Match a in RxAnchor.Matches(html))
            {
                string tag = a.Value;
                if (tag.IndexOf("workshopItem", StringComparison.Ordinal) < 0) continue;
                Match m = RxId.Match(tag);
                if (!m.Success) continue;
                if (seen.Add(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
            }
            return ids;
        }

        /// <summary>
        /// 槽位 → 默认搜索词。槽位名本身带拉丁字母（AK-47 / UZI / AWP）就直接用；
        /// 中文名（木喷 / 佐伊）退回模型键的尾段（pump / zoey），不准用户自己改。
        /// </summary>
        internal static string SearchWord(string slotName, string slotKey)
        {
            string n = (slotName ?? "").Trim();
            bool latin = false;
            foreach (char c in n)
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) { latin = true; break; }
            if (latin) return n;

            string k = slotKey ?? "";
            int bar = k.IndexOf('\u0001');
            if (bar >= 0) k = k.Substring(bar + 1);
            foreach (string p in new string[] { "slot:", "#empty:", "c:", "sub:" })
                if (k.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { k = k.Substring(p.Length); break; }
            if (k.StartsWith("w_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("v_", StringComparison.OrdinalIgnoreCase)) k = k.Substring(2);
            int us = k.LastIndexOf('_');
            if (us >= 0 && us < k.Length - 1) k = k.Substring(us + 1);
            return k;
        }

        /// <summary>大类 → 搜索词。太泛的大类给空串（那就是浏览全部）。</summary>
        internal static string CatWord(string cat)
        {
            switch (cat ?? "")
            {
                case "枪械": return "";
                case "近战": return "melee";
                case "投掷物": return "grenade";
                case "弹药升级": return "ammo";
                case "人物": return "survivor";
                case "药品": return "medkit";
                case "地图": return "";              // 靠 Campaigns 标签，文本搜 "map" 全是杂物
                case "喷漆": return "spray";
                default: return "";
            }
        }

        /// <summary>小分类 → 搜索词（大类/位置没覆盖时用）。</summary>
        internal static string SubWord(string sub)
        {
            switch (sub ?? "")
            {
                case "步枪": return "rifle";
                case "冲锋枪": return "smg";
                case "霰弹枪": return "shotgun";
                case "狙击枪": return "sniper";
                case "机枪": return "machine gun";
                case "手枪": return "pistols";
                case "近战": return "melee weapon";
                case "投掷物": return "grenade";
                case "弹药升级": return "ammo";
                case "药品": return "medkit";
                case "特感": return "special infected";
                case "小僵尸": return "common infected";
                case "人物": return "survivor";
                case "地图": return "campaign";
                case "喷漆": return "spray";
                default: return "";
            }
        }

        /// <summary>
        /// 分类 → 工坊标签过滤。只给"标签能准确指代这一大类"的：
        /// 枪械=Weapons、地图=Campaigns、人物=Survivors、特感=Special Infected；
        /// 其它大类（近战/投掷物/药品…）混在 Weapons 里会带进一堆枪，就不加标签、只靠文本。
        /// </summary>
        internal static string TagFor(string cat, string sub)
        {
            switch (cat ?? "")
            {
                case "枪械":
                case "近战":
                case "投掷物":
                case "弹药升级":
                case "药品": return "Weapons";
                case "地图": return "Campaigns";
                case "人物": return "Survivors";
                case "僵尸": return sub == "特感" ? "Special Infected" : "";
                default: return "";
            }
        }

        // ---------------------------------------------------------- 网络

        static string Post(string path, string body)
        {
            WsNet.Tune();                                   // 全局连接/代理设置先调好（POST 不走优选 IP）
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ApiRoot + path);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.UserAgent = Ua;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>批量查详情。接口一次最多 100 个，这里按 100 分组。</summary>
        public static List<WsItem> Fetch(IList<string> ids)
        {
            List<WsItem> outp = new List<WsItem>();
            if (ids == null || ids.Count == 0) return outp;

            for (int from = 0; from < ids.Count; from += 100)
            {
                int n = Math.Min(100, ids.Count - from);
                StringBuilder sb = new StringBuilder();
                sb.Append("itemcount=").Append(n);
                for (int k = 0; k < n; k++)
                    sb.Append("&publishedfileids[").Append(k).Append("]=").Append(ids[from + k]);

                string json = Post("GetPublishedFileDetails/v1/", sb.ToString());
                List<string> objs = SplitObjects(json, "publishedfiledetails");

                Dictionary<string, string> byId = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string o in objs)
                {
                    string id = JStr(o, "publishedfileid");
                    if (id.Length > 0) byId[id] = o;
                }

                for (int k = 0; k < n; k++)
                {
                    string id = ids[from + k];
                    WsItem it = new WsItem();
                    it.Id = id;
                    string o;
                    if (!byId.TryGetValue(id, out o))
                    {
                        it.Note = "接口没有返回这个 ID";
                        outp.Add(it);
                        continue;
                    }
                    long res = JNum(o, "result");
                    it.Title = JStr(o, "title").Trim();
                    it.Size = JNum(o, "file_size");
                    it.Url = JStr(o, "file_url");
                    it.FileName = JStr(o, "filename");
                    // 详情字段：同一个响应里本来就有，顺手全取下来（详情面板 + 中文翻译要用）
                    // 注意字段名：Steam 现在返回的是 description，早期文档里的 file_description 已经不再下发。
                    // 只认后者的话，真实数据里所有物品的正文都会是空的（详情页统一显示「没有写介绍」）。
                    it.Desc = JStr(o, "description");
                    if (it.Desc.Length == 0) it.Desc = JStr(o, "file_description");
                    it.Preview = JStr(o, "preview_url");
                    it.Created = JNum(o, "time_created");
                    it.Updated = JNum(o, "time_updated");
                    it.Subs = JNum(o, "subscriptions");
                    it.Views = JNum(o, "views");
                    it.Creator = JStr(o, "creator");
                    it.Tags = TagsOf(o);
                    if (res != 1) it.Note = "物品不存在或已删除";
                    it.DetailsLoaded = true;
                    outp.Add(it);
                }
            }
            return outp;
        }

        static readonly Regex RxTag = new Regex("\"tag\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

        /// <summary>
        /// 标签：接口给的是 [{"tag":"Action"},…] 这种对象数组，手写解析器没有数组支持，
        /// 这里用一条正则把 tag 值抠出来拼成一句。没有标签返回空串。
        /// </summary>
        internal static string TagsOf(string obj)
        {
            List<string> tags = new List<string>();
            if (string.IsNullOrEmpty(obj)) return "";
            foreach (Match m in RxTag.Matches(obj))
            {
                string t = m.Groups[1].Value.Trim();
                if (t.Length == 0 || tags.Contains(t)) continue;
                tags.Add(t);
                if (tags.Count >= 8) break;
            }
            return string.Join(" · ", tags.ToArray());
        }

        /// <summary>合集 → 子项 ID。不是合集时返回空表。</summary>
        public static List<string> CollectionChildren(string collectionId, out string title)
        {
            title = "";
            List<string> kids = new List<string>();
            string json = Post("GetCollectionDetails/v1/", "collectioncount=1&publishedfileids[0]=" + collectionId);
            List<string> objs = SplitObjects(json, "collectiondetails");
            if (objs.Count == 0) return kids;
            title = JStr(objs[0], "title").Trim();
            foreach (string ko in SplitObjects(objs[0], "children"))
            {
                string id = JStr(ko, "publishedfileid");
                if (id.Length > 0 && !kids.Contains(id)) kids.Add(id);
            }
            return kids;
        }

        /// <summary>
        /// 判断是不是合集。**不能靠「有没有直链」**：合集的 file_url 也非空，
        /// 但那指向的是合集的缩略图（filename 形如 xxx_new_preview_标题 Thumbnail.jpg，
        /// 域名是 images.steamusercontent.com），当成正常物品下下来就是一张 JPG。
        /// 普通物品的 filename 一定以 .vpk 结尾，拿这个当主判据最稳。
        /// </summary>
        internal static bool LooksLikeCollection(WsItem it)
        {
            string fn = it.FileName ?? "";
            if (fn.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) return false;
            if (fn.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return true;
            if (fn.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return true;
            if (fn.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return true;
            if (it.Url.IndexOf("images.steamusercontent.com", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return it.Url.Length == 0 && it.Size == 0;
        }

        /// <summary>查询 + 展开合集，返回最终要下载的清单（合集会先占一行，再跟上它的子项）。</summary>
        public static List<WsItem> Resolve(IList<string> ids)
        {
            const int MaxChildren = 200;
            List<WsItem> first = Fetch(ids);
            List<WsItem> final = new List<WsItem>();
            List<string> more = new List<string>();

            foreach (WsItem it in first)
            {
                if (it.Note.Length > 0) { final.Add(it); continue; }

                if (LooksLikeCollection(it))
                {
                    string title = "";
                    List<string> kids;
                    try { kids = CollectionChildren(it.Id, out title); }
                    catch { kids = new List<string>(); }

                    if (kids.Count > 0)
                    {
                        it.IsCollection = true;
                        it.Title = title.Length > 0 ? title : ("合集 " + it.Id);
                        it.State = kids.Count + " 个子项" + (kids.Count > MaxChildren ? "（只取前 " + MaxChildren + "）" : "");
                        if (kids.Count > MaxChildren) kids = kids.GetRange(0, MaxChildren);
                        final.Add(it);
                        foreach (string k in kids) if (!more.Contains(k)) more.Add(k);
                        continue;
                    }
                    // 不像合集又没内容：大概率是文件夹式物品
                    if (it.Url.Length == 0 || it.Size == 0)
                        it.Note = "没有直链（文件夹式物品），要用 Steam 客户端订阅下载";
                    else
                        it.Note = "拿不到可用的文件（可能是合集封面）";
                }
                final.Add(it);
            }

            if (more.Count > 0)
            {
                // 子项自己也可能是个合集，但实际嵌套很少见，这里只展开一层，避免死循环
                List<WsItem> kids = Fetch(more);
                foreach (WsItem k in kids) if (k.Note.Length == 0 && k.Url.Length == 0 && k.Size == 0)
                    k.Note = "没有直链（文件夹式物品），要用 Steam 客户端订阅下载";
                final.AddRange(kids);
            }
            return final;
        }

        /// <summary>下载完成后要告诉用户的话（压缩包 / 结构不完整 / 未知格式），没话说就是空串。</summary>
        public static string LastDownloadNote = "";
        /// <summary>实际落盘路径：容器不是 vpk 时扩展名会被改成 .zip/.rar/.7z。</summary>
        public static string LastSavedPath = "";

        /// <summary>
        /// 下一个物品。先写 .part，校验大小后再改名 —— 半截文件绝不能出现在库里，
        /// 否则扫描会把一个坏 vpk 当成正常 MOD 收进去。
        /// </summary>
        public static void Download(WsItem it, string dest, Action<long, long> progress)
        {
            string part = dest + ".part";
            try
            {
                if (File.Exists(part)) File.Delete(part);
            }
            catch { }

            // 走联网层：优选 IP（CDN 大多命中 cdn.steamusercontent），失败自动退回域名重下
            long gotBytes = WsNet.DownloadTo(it.Url, Ua, 30000, part, it.Size, progress);
            if (gotBytes <= 0) throw new IOException("下载失败（连不上 steamusercontent，已重试一次）");

            long real = 0;
            try { real = new FileInfo(part).Length; } catch { }
            if (it.Size > 0 && real != it.Size)
            {
                try { File.Delete(part); } catch { }
                throw new IOException("大小不一致：收到 " + real + " 字节，应为 " + it.Size);
            }
            if (real == 0)
            {
                try { File.Delete(part); } catch { }
                throw new IOException("下载到 0 字节");
            }
            // 认真实容器（不看扩展名）：作者可能用 zip/rar/7z 传播，甚至把压缩包命名成 .vpk。
            // 只有"网页/图片"才是真的下错了；其余一律保存下来并提示，不能替作者判"失败"（用户 2026-11-21 要求）。
            LastDownloadNote = "";
            LastSavedPath = "";
            string kind = ContainerKind(part);
            if (kind == "html" || kind == "image")
            {
                try { File.Delete(part); } catch { }
                throw new IOException("下到的是网页/图片，不是 MOD（多半是合集封面或链接失效）");
            }
            if (kind == "vpk")
            {
                string why;
                if (!Vpk.VerifyComplete(part, out why))
                    LastDownloadNote = "这个 vpk 结构不完整（" + why + "），先留着，进游戏可能缺件";
            }
            else if (kind == "zip" || kind == "rar" || kind == "7z")
            {
                // 按真实容器改扩展名：库和游戏都按扩展名认压缩包，名字叫 .vpk 会被当坏包
                string ext = "." + kind;
                if (!string.Equals(Path.GetExtension(dest), ext, StringComparison.OrdinalIgnoreCase))
                {
                    string dir0 = Path.GetDirectoryName(dest) ?? "";
                    string stem = Path.Combine(dir0, Path.GetFileNameWithoutExtension(dest));
                    string cand = stem + ext;
                    int n = 2;
                    while (File.Exists(cand)) { cand = stem + " (" + n + ")" + ext; n++; }
                    dest = cand;
                }
                LastDownloadNote = "这是 " + kind + " 压缩包（不是单个 vpk）：已按压缩包存好，解压/装配由库处理";
            }
            else
            {
                LastDownloadNote = "下到的文件不是标准 vpk/压缩包（未知格式），先原样留着";
            }

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
            LastSavedPath = dest;
        }

        /// <summary>是不是 vpk（看头 4 字节）。</summary>
        static bool IsVpkFile(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = new byte[4];
                    if (fs.Read(h, 0, 4) < 4) return false;
                    return h[0] == 0x34 && h[1] == 0x12 && h[2] == 0xAA && h[3] == 0x55;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 看头几个字节认容器：vpk / zip / rar / 7z / 图片 / 网页 / 其它。
        /// 不信扩展名 —— 工坊上把 zip 命名成 .vpk 的包真实存在。
        /// </summary>
        static readonly Regex RxReqId = new Regex(@"filedetails/?\?id=(\d+)", RegexOptions.IgnoreCase);
        static readonly Regex RxReqTitle = new Regex(@"class=""requiredItem""[^>]*>([\s\S]*?)</div>", RegexOptions.IgnoreCase);

        /// <summary>
        /// 读工坊页面右侧的「Required items」区块 —— 这是 Steam 自己的配套物品声明，不是作者写的正文
        /// （2026-11-21 用户指出：正文提取不对）。页面上有 author 设过的依赖才会出现这个区块。
        /// 只用于提示和可选批量下载，抓不到就返回空。
        /// </summary>
        static readonly Dictionary<string, List<WsItem>> reqCache = new Dictionary<string, List<WsItem>>(StringComparer.Ordinal);

        /// <summary>抓详情页：WsNet 的优选 IP 偶尔连不上，这里再退一次最朴素的请求。</summary>
        static string FetchItemPage(string id)
        {
            string url = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id;
            string h = null;
            try { h = WsNet.GetString(url, Ua, 20000); } catch { }
            if (!string.IsNullOrEmpty(h) && h.Length > 200) return h;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = Ua;
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        public static List<WsItem> RequiredItems(string id)
        {
            List<WsItem> list = new List<WsItem>();
            if (string.IsNullOrEmpty(id)) return list;
            lock (reqCache) { List<WsItem> c; if (reqCache.TryGetValue(id, out c)) return c; }
            string html = null;
            for (int attempt = 0; attempt < 2 && string.IsNullOrEmpty(html); attempt++)
            {
                if (attempt > 0) { try { System.Threading.Thread.Sleep(250); } catch { } }
                try { html = FetchItemPage(id); } catch { }
            }
            if (string.IsNullOrEmpty(html)) return list;
            int at = html.IndexOf("requiredItemsContainer", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return list;
            int end = html.IndexOf("rightSectionTopTitle", at + 30, StringComparison.OrdinalIgnoreCase);
            string seg = end > at ? html.Substring(at, end - at) : html.Substring(at, Math.Min(20000, html.Length - at));
            MatchCollection ids = RxReqId.Matches(seg);
            MatchCollection titles = RxReqTitle.Matches(seg);
            for (int i = 0; i < ids.Count && list.Count < 8; i++)
            {
                string rid = ids[i].Groups[1].Value;
                if (rid.Length == 0 || rid == id) continue;
                string t = i < titles.Count ? CleanHtml(titles[i].Groups[1].Value) : "";
                WsItem one = new WsItem();
                one.Id = rid;
                one.Title = t;
                list.Add(one);
            }
            lock (reqCache) reqCache[id] = list;
            return list;
        }

        static string CleanHtml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = Regex.Replace(s, "<[^>]+>", " ");
            t = System.Net.WebUtility.HtmlDecode(t);
            t = Regex.Replace(t, "\\s+", " ").Trim();
            return t;
        }

        static string ContainerKind(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = new byte[8];
                    int n = fs.Read(h, 0, 8);
                    if (n >= 4 && h[0] == 0x34 && h[1] == 0x12 && h[2] == 0xAA && h[3] == 0x55) return "vpk";
                    if (n >= 4 && h[0] == 0x50 && h[1] == 0x4B) return "zip";
                    if (n >= 4 && h[0] == 0x52 && h[1] == 0x61 && h[2] == 0x72 && h[3] == 0x21) return "rar";
                    if (n >= 6 && h[0] == 0x37 && h[1] == 0x7A && h[2] == 0xBC && h[3] == 0xAF
                        && h[4] == 0x27 && h[5] == 0x1C) return "7z";
                    if (n >= 3 && h[0] == 0xFF && h[1] == 0xD8) return "image";
                    if (n >= 4 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return "image";
                    if (n >= 3 && h[0] == 0x47 && h[1] == 0x49 && h[2] == 0x46) return "image";
                    int i = 0;
                    while (i < n && (h[i] == 0x20 || h[i] == 0x0A || h[i] == 0x0D || h[i] == 0x09)) i++;
                    if (i < n && h[i] == 0x3C) return "html";
                    return "other";
                }
            }
            catch { return "other"; }
        }

        // ---------------------------------------------------------- JSON（只够用）

        /// <summary>
        /// 从 JSON 里切出某个数组下的所有对象。没有引入 JSON 库：接口返回的结构固定，
        /// 按括号深度扫描比引一个依赖更省事，也不会被版本差异影响。
        /// </summary>
        internal static List<string> SplitObjects(string json, string arrayKey)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(json)) return list;

            int at = json.IndexOf("\"" + arrayKey + "\"", StringComparison.Ordinal);
            if (at < 0) return list;
            int i = json.IndexOf('[', at);
            if (i < 0) return list;

            int depth = 0, start = -1;
            bool inStr = false, esc = false;
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(json.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
                else if (c == ']' && depth == 0) break;
            }
            return list;
        }

        internal static string JStr(string obj, string key)
        {
            string pat = "\"" + key + "\":";
            int i = obj.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return "";
            i += pat.Length;
            while (i < obj.Length && (obj[i] == ' ' || obj[i] == '\t')) i++;
            if (i >= obj.Length || obj[i] != '"') return "";      // null 或数字

            i++;
            StringBuilder sb = new StringBuilder();
            while (i < obj.Length)
            {
                char c = obj[i];
                if (c == '\\' && i + 1 < obj.Length)
                {
                    char n = obj[i + 1];
                    if (n == 'u' && i + 5 < obj.Length)
                    {
                        int code;
                        if (int.TryParse(obj.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber,
                                         System.Globalization.CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                        i += 6;
                        continue;
                    }
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') sb.Append('\r');
                    else sb.Append(n);
                    i += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        internal static long JNum(string obj, string key)
        {
            string pat = "\"" + key + "\":";
            int i = obj.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += pat.Length;
            while (i < obj.Length && (obj[i] == ' ' || obj[i] == '\t')) i++;
            // Steam 的数字字段一半是数字、一半是字符串：file_size 是 "12345"（带引号），
            // result 是 1（不带）。两种都得认。
            if (i < obj.Length && obj[i] == '"') i++;
            int j = i;
            if (j < obj.Length && obj[j] == '-') j++;
            while (j < obj.Length && char.IsDigit(obj[j])) j++;
            long v;
            long.TryParse(obj.Substring(i, j - i), out v);
            return v;
        }

        // ---------------------------------------------------------- 落盘

        /// <summary>下载目录。U 盘模式放主库，本地模式直接放游戏 addons（本地模式就是直接管游戏目录）。</summary>
        /// <summary>
        /// 下载记录：工坊 ID → 落盘文件名。画廊打开时预扫它给卡片标「已下载」。
        /// 只增不改；旧的按标题改名的历史下载没有 ID，标不出来（诚实胜过猜）。
        /// </summary>
        internal static string IdMapPath { get { return Path.Combine(Core.LogDir, "workshop-idmap.txt"); } }

        internal sealed class DownloadRec
        {
            public string Id = "";
            public string File = "";
            public long Updated;          // 下载时接口给的 time_updated（0 = 老记录没存）
            public long Size;             // 下载时接口给的 file_size
        }

        internal sealed class UpdateInfo
        {
            public WsItem Item;
            public DownloadRec Rec;
            public string Why = "";
        }

        internal static void RememberDownload(string id, string fileName)
        {
            RememberDownload(id, fileName, 0, 0);
        }

        /// <summary>记一条下载记录：id|文件名|接口更新时间|接口大小（后两项 0 = 老格式没有）。</summary>
        internal static void RememberDownload(string id, string fileName, long updated, long size)
        {
            try
            {
                Directory.CreateDirectory(Core.LogDir);
                File.AppendAllText(IdMapPath, (id ?? "").Trim() + "|" + (fileName ?? "").Trim()
                    + "|" + updated + "|" + size + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>读下载记录：同一个 id 以最后一行为准；旧格式（只有两列）时间/大小按 0 处理。</summary>
        public static List<DownloadRec> LoadIndex()
        {
            Dictionary<string, DownloadRec> map = new Dictionary<string, DownloadRec>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(IdMapPath)) return new List<DownloadRec>();
                foreach (string line in File.ReadAllLines(IdMapPath, Encoding.UTF8))
                {
                    string[] p = line.Split('|');
                    if (p.Length < 2) continue;
                    string id = p[0].Trim(), file = p[1].Trim();
                    if (id.Length == 0 || file.Length == 0) continue;
                    DownloadRec r = new DownloadRec();
                    r.Id = id;
                    r.File = file;
                    long v;
                    if (p.Length > 2 && long.TryParse(p[2].Trim(), out v)) r.Updated = v;
                    if (p.Length > 3 && long.TryParse(p[3].Trim(), out v)) r.Size = v;
                    map[id] = r;
                }
            }
            catch { }
            return new List<DownloadRec>(map.Values);
        }

        /// <summary>
        /// 检查已下载的工坊 MOD 有没有新版本：拉接口比对 time_updated / file_size。
        /// 老记录没存时间/大小（=0）时只按大小差异判，查不出来就算未知，不冒充"有更新"。
        /// </summary>
        public static List<UpdateInfo> CheckUpdates(Action<int, int> progress)
        {
            List<UpdateInfo> list = new List<UpdateInfo>();
            List<DownloadRec> alive = new List<DownloadRec>();
            string dir;
            try { dir = DestDir(); } catch { dir = ""; }
            foreach (DownloadRec r in LoadIndex())
            {
                try
                {
                    if (dir.Length == 0) continue;
                    string p = Path.Combine(dir, r.File);
                    if (!File.Exists(p)) continue;                 // 文件已删：不算更新
                    if (r.Size <= 0) r.Size = new FileInfo(p).Length;
                    alive.Add(r);
                }
                catch { }
            }
            List<string> ids = alive.ConvertAll(delegate(DownloadRec r) { return r.Id; });
            int done = 0;
            for (int from = 0; from < ids.Count; from += 100)
            {
                List<string> part = ids.GetRange(from, Math.Min(100, ids.Count - from));
                List<WsItem> items = Fetch(part);
                foreach (WsItem it in items)
                {
                    if (it == null || it.Id.Length == 0) continue;
                    int ix = alive.FindIndex(delegate(DownloadRec r) { return r.Id == it.Id; });
                    if (ix < 0) continue;
                    DownloadRec rec = alive[ix];
                    string why = "";
                    if (rec.Updated > 0 && it.Updated > rec.Updated)
                        why = Lang.F("更新于 {0}", WsText.Ago(it.Updated));
                    else if (it.Size > 0 && rec.Size > 0 && it.Size != rec.Size)
                        why = Lang.F("大小变了：{0} → {1}", Core.Human(rec.Size), Core.Human(it.Size));
                    if (why.Length == 0) continue;
                    UpdateInfo u = new UpdateInfo();
                    u.Item = it;
                    u.Rec = rec;
                    u.Why = why;
                    list.Add(u);
                }
                done += part.Count;
                if (progress != null) progress(done, ids.Count);
            }
            return list;
        }

        internal static HashSet<string> LoadDownloadedIds()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(IdMapPath)) return set;
                foreach (string line in File.ReadAllLines(IdMapPath, Encoding.UTF8))
                {
                    int bar = line.IndexOf('|');
                    if (bar > 0) set.Add(line.Substring(0, bar).Trim());
                }
            }
            catch { }
            return set;
        }

        /// <summary>
        /// 下载保存到哪。用户在工坊「下载列表」里指定过就用那个目录，
        /// 没指定才跟随主库：U 盘模式 = 库根\_创意工坊（库扫描是递归的，子目录照样看得见），
        /// 本地模式 = 游戏 addons 根目录（游戏和工具都只认这一层 vpk，不另建子目录）。
        /// </summary>
        public static string DestDir()
        {
            string custom = (Core.Cfg.WorkshopDir ?? "").Trim();
            if (custom.Length > 0)
            {
                try { Directory.CreateDirectory(custom); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("工坊下载位置不可用：" + custom + "（" + ex.Message + "）");
                }
                return custom;
            }

            string root;
            if (Core.IsLocal)
            {
                root = Core.GameAddonsDir();
                if (string.IsNullOrEmpty(root))
                    throw new InvalidOperationException("本地模式下没找到游戏 addons 目录，先在设置里指定游戏目录");
                // 以前这里会再拼一层 _创意工坊 并建出来：工具扫游戏目录只认 addons 根
                // （Catalog.ScanGameAddons 是 TopDirectoryOnly），下载完在界面里 0 个 MOD，
                // 游戏本身也不读子目录的 vpk。addons 根就是唯一该放的地方，别建子目录。
                return root;
            }
            else
            {
                if (string.IsNullOrEmpty(Core.ModRoot))
                    throw new InvalidOperationException("还没选主库，先在顶部选一下 U 盘");
                root = Core.ModRoot;
            }
            string dir = Path.Combine(root, "_创意工坊");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// 把工坊的预览图存到 MOD 旁边（同目录同名的 .jpg/.png）。
        /// 库里的封面优先读包内 addonimage，包里没有就找同名图片 —— 下载时存一份，没打包封面图的作者也能有预览。
        /// </summary>
        public static void SavePreview(WsItem it, string savedPath)
        {
            if (it == null || string.IsNullOrEmpty(it.Preview) || string.IsNullOrEmpty(savedPath)) return;
            try
            {
                string dir = Path.GetDirectoryName(savedPath);
                string stem = Path.GetFileNameWithoutExtension(savedPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return;
                string low = it.Preview.ToLowerInvariant();
                string ext = low.Contains(".png") ? ".png" : (low.Contains(".gif") ? ".gif" : ".jpg");
                string dst = Path.Combine(dir, stem + ext);
                if (File.Exists(dst) && new FileInfo(dst).Length > 1024) return;     // 已经在（重下同一份）就不重复拉
                byte[] b = WsNet.GetBytes(it.Preview, Ua, 15000);
                if (b == null || b.Length < 64) return;
                File.WriteAllBytes(dst, b);
                Core.Log("OK", "工坊：预览图已保存 " + Path.GetFileName(dst));
            }
            catch (Exception ex) { Core.Log("WARN", "工坊：预览图保存失败 " + ex.Message); }
        }

        /// <summary>
        /// 给下载下来的 vpk 起名。用标题而不是 ID：库里按名字浏览，一串数字没法看。
        /// 非法字符换成空格，过长截断，全空时退回 ID。
        /// </summary>
        public static string SafeFileName(string title, string id)
        {
            string t = (title ?? "").Trim();
            if (t.Length == 0) t = "workshop_" + id;

            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in t)
            {
                if (Array.IndexOf(bad, c) >= 0 || c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                else sb.Append(c);
                if (sb.Length >= 70) break;
            }
            string s = sb.ToString().Trim().TrimEnd('.');
            while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
            if (s.Length == 0) s = "workshop_" + id;
            return s + ".vpk";
        }

        /// <summary>
        /// 下载完自动改名：读 vpk 自己的 addoninfo.txt，用**游戏里显示的那个名字**当文件名。
        /// 工坊标题常带一堆【】标签、作者前缀和表情，游戏内名才是这个 MOD 真正的身份
        /// （库里显示、方案树归类、搜索别名全都跟着它走）。
        /// 读不出 addoninfo（没有 / 分卷包 / 坏包）就保留工坊标题名 —— 改名只是锦上添花，不许失败。
        /// 撞名加 (2) (3)…序号，谁也不覆盖谁。
        /// </summary>
        /// <returns>改完的文件名；没改（读不到 / 名字没变 / 出错）返回 null。</returns>
        public static string RenameToAddonTitle(string dest)
        {
            try
            {
                if (!string.Equals(Path.GetExtension(dest), ".vpk", StringComparison.OrdinalIgnoreCase)) return null;
                Vpk.AddonMeta m = Vpk.ReadAddonInfo(dest);
                string t = (m == null || m.Title == null) ? "" : m.Title.Trim();
                if (t.Length == 0) return null;

                string want = SafeFileName(t, Path.GetFileNameWithoutExtension(dest));
                string cur = Path.GetFileName(dest);
                if (string.Equals(want, cur, StringComparison.OrdinalIgnoreCase)) return null;

                string dir = Path.GetDirectoryName(dest);
                string cand = Path.Combine(dir, want);
                // 改名时把同名预览图（.jpg/.png/.gif）一起带走，不然封面会挂在旧文件名上
                string oldStem = Path.GetFileNameWithoutExtension(dest);
                if (File.Exists(cand))
                {
                    string stem = Path.GetFileNameWithoutExtension(want);
                    for (int n = 2; n < 100; n++)
                    {
                        cand = Path.Combine(dir, stem + " (" + n + ").vpk");
                        if (!File.Exists(cand)) break;
                    }
                }
                File.Move(dest, cand);
                // 同名预览图一起改名（.jpg/.jpeg/.png/.gif），不然封面还挂在旧文件名上
                string newStem = Path.GetFileNameWithoutExtension(cand);
                foreach (string e0 in new string[] { ".jpg", ".jpeg", ".png", ".gif" })
                {
                    string a = Path.Combine(dir, oldStem + e0);
                    if (!File.Exists(a)) continue;
                    string b2 = Path.Combine(dir, newStem + e0);
                    try { if (File.Exists(b2)) File.Delete(b2); File.Move(a, b2); } catch { }
                }
                return Path.GetFileName(cand);
            }
            catch { return null; }
        }

        // ---------------------------------------------------------- 离线自检

        /// <summary>
        /// 只测不联网的部分：链接解析、文件名清理、JSON 提取。
        /// 网络波动不该让自检变红，所以这里一个字节都不发出去。
        /// </summary>
        internal static int SelfTest()
        {
            int bad = 0;

            List<string> a = ParseIds("https://steamcommunity.com/sharedfiles/filedetails/?id=3833236101");
            if (a.Count != 1 || a[0] != "3833236101")
            { bad++; Core.Log("FAIL", "WS 解析单个链接得到 " + string.Join(",", a.ToArray())); }
            else Core.Log("INFO", "WS 解析单个链接 = " + a[0]);

            List<string> b = ParseIds("3832308538, https://steamcommunity.com/sharedfiles/filedetails/?id=3834851144\r\n3832308538");
            string bj = string.Join("/", b.ToArray());
            if (bj != "3832308538/3834851144")
            { bad++; Core.Log("FAIL", "WS 解析混合输入得到 " + bj + "（应去重且保序）"); }
            else Core.Log("INFO", "WS 解析混合输入 = " + bj);

            List<string> c = ParseIds("这不是链接 abc 12");
            if (c.Count != 0)
            { bad++; Core.Log("FAIL", "WS 把垃圾文本认成了 ID：" + string.Join(",", c.ToArray())); }
            else Core.Log("INFO", "WS 垃圾文本被正确忽略");

            CheckName(ref bad, "Hello/World: test?", "Hello World test.vpk");
            CheckName(ref bad, "", "workshop_123.vpk");
            CheckName(ref bad, "  ..  ", "workshop_123.vpk");
            CheckName(ref bad, new string('x', 90), new string('x', 70) + ".vpk");

            string json = "{\"response\":{\"publishedfiledetails\":["
                + "{\"publishedfileid\":\"111\",\"result\":1,\"title\":\"first\",\"file_size\":\"12345\",\"file_url\":\"https://x/y/\",\"filename\":\"a/b.vpk\"},"
                + "{\"publishedfileid\":\"222\",\"result\":9,\"title\":\"\\u4e2d\\u6587\\u6807\\u9898\",\"file_size\":\"0\"}]}}";

            List<string> objs = SplitObjects(json, "publishedfiledetails");
            if (objs.Count != 2)
            { bad++; Core.Log("FAIL", "WS JSON 切出 " + objs.Count + " 个对象，应为 2"); }
            else
            {
                string t1 = JStr(objs[0], "title");
                long s1 = JNum(objs[0], "file_size");
                string u1 = JStr(objs[0], "file_url");
                string t2 = JStr(objs[1], "title");
                long r2 = JNum(objs[1], "result");
                if (t1 != "first" || s1 != 12345 || u1 != "https://x/y/")
                { bad++; Core.Log("FAIL", "WS JSON 字段解析错：" + t1 + " / " + s1 + " / " + u1); }
                if (t2 != "中文标题" || r2 != 9)
                { bad++; Core.Log("FAIL", "WS JSON 中文或结果码解析错：" + t2 + " / " + r2); }
                Core.Log("INFO", "WS JSON：" + t1 + " / " + s1 + " / " + t2 + " / result=" + r2);
            }

            // file_url 为空 = 文件夹式物品，这个判断决定了列表里显示「需客户端下载」而不是下载失败
            string emptyUrl = "{\"response\":{\"publishedfiledetails\":[{\"publishedfileid\":\"333\",\"result\":1,\"title\":\"folder item\",\"file_size\":\"4840892\",\"file_url\":\"\"}]}}";
            List<string> eo = SplitObjects(emptyUrl, "publishedfiledetails");
            if (eo.Count == 1 && JStr(eo[0], "file_url").Length == 0 && JNum(eo[0], "file_size") == 4840892)
                Core.Log("INFO", "WS 无直链物品识别正常（file_url 空、大小仍有值）");
            else
            { bad++; Core.Log("FAIL", "WS 无直链物品没被正确识别"); }

            // 详情字段：同一个响应里就带着 file_description / preview_url / tags / time_updated，
            // 以前一个都没取 —— 现在右侧详情面板和中文翻译全靠它们，断了就是一片空白。
            string detJson = "{\"response\":{\"publishedfiledetails\":[{\"publishedfileid\":\"444\",\"result\":1,"
                           + "\"title\":\"Tank\",\"file_size\":\"1024\",\"file_url\":\"https://x/z.vpk\","
                           + "\"file_description\":\"[b]Bold[/b] text\",\"preview_url\":\"https://img/p.jpg\","
                           + "\"time_created\":\"1700000000\",\"time_updated\":\"1710000000\","
                           + "\"subscriptions\":\"12400\",\"views\":\"99\","
                           + "\"tags\":[{\"tag\":\"Tank\"},{\"tag\":\"角色\"}]}]}}";
            List<string> dd = SplitObjects(detJson, "publishedfiledetails");
            if (dd.Count == 1)
            {
                string desc = JStr(dd[0], "file_description");
                string prev = JStr(dd[0], "preview_url");
                string tags = TagsOf(dd[0]);
                long upd = JNum(dd[0], "time_updated");
                long subs = JNum(dd[0], "subscriptions");
                if (desc != "[b]Bold[/b] text" || prev != "https://img/p.jpg" || tags != "Tank · 角色"
                    || upd != 1710000000 || subs != 12400)
                {
                    bad++;
                    Core.Log("FAIL", "WS 详情字段解析错：desc=" + desc + " prev=" + prev
                        + " tags=" + tags + " upd=" + upd + " subs=" + subs);
                }
                else Core.Log("INFO", "WS 详情字段：描述 / 预览图 / 标签 / 更新时间 / 订阅数 全部解析正常");
            }
            else { bad++; Core.Log("FAIL", "WS 详情字段：夹具没解析出来"); }

            // 回归：Steam 现在下发的字段名是 description，不是 file_description。
            // 上面那个夹具写的是老名字，所以「只认 file_description」的 bug 离线全绿、真实数据全空
            // （详情页统一显示「这个物品没有写介绍」）。这里按真实字段名再验一遍，并且要求优先用它。
            string realJson = "{\"response\":{\"publishedfiledetails\":[{\"publishedfileid\":\"445\",\"result\":1,"
                            + "\"title\":\"AK\",\"description\":\"[b]正文[/b] 带换行\\n第二行\","
                            + "\"preview_url\":\"\",\"tags\":[]}]}}";
            List<string> rl = SplitObjects(realJson, "publishedfiledetails");
            if (rl.Count == 1)
            {
                string d1 = JStr(rl[0], "description");
                if (d1.Length == 0) d1 = JStr(rl[0], "file_description");
                if (d1.IndexOf("正文") < 0 || d1.IndexOf("\n") < 0)
                {
                    bad++;
                    Core.Log("FAIL", "WS 真实字段名 description 解析不对：" + d1.Replace("\n", "\\n"));
                }
                else Core.Log("INFO", "WS 真实字段名 description 解析正常（含 \\n 转义）");
            }
            else { bad++; Core.Log("FAIL", "WS description 夹具没解析出来"); }

            // BBCode 清洗：标签清干净、小标题与列表符号保留、链接只留文字
            string cleaned = WsText.Clean("[h1]Title[/h1][b]Bold[/b][list][*]one\n[*]two[/list][url=http://x]link[/url]");
            if (cleaned.IndexOf('[') >= 0 || cleaned.IndexOf("■ Title") < 0
                || cleaned.IndexOf("· one") < 0 || cleaned.IndexOf("link") < 0)
            {
                bad++;
                Core.Log("FAIL", "WS 正文清洗不对：" + cleaned.Replace("\n", " / "));
            }
            else Core.Log("INFO", "WS 正文清洗：标签清空、小标题 / 列表 / 链接文字保留");

            // 中文正文不该再白跑一趟翻译
            if (!WsText.MostlyChinese("这是一个中文介绍") || WsText.MostlyChinese("This is an English description"))
            { bad++; Core.Log("FAIL", "WS 中英判定不对"); }
            else Core.Log("INFO", "WS 中英判定：中文不翻、英文要翻");

            // 译文缓存：键里带 time_updated —— 作者改过介绍，旧译文必须失效
            {
                string cid = "999001";
                bool hitOld, missNew;
                try
                {
                    WsTranslate.WriteCacheForTest(cid, 111, "旧译文");
                    hitOld = WsTranslate.ReadCache(cid, 111) == "旧译文";
                    missNew = WsTranslate.ReadCache(cid, 222) == null;
                    string cp = WsTranslate.CachePathForTest(cid);
                    if (File.Exists(cp)) File.Delete(cp);
                }
                catch (Exception ex) { hitOld = false; missNew = false; Core.Log("WARN", "WS 缓存测试异常: " + ex.Message); }
                if (hitOld && missNew) Core.Log("INFO", "WS 译文缓存：命中同版本、介绍更新后失效");
                else { bad++; Core.Log("FAIL", "WS 译文缓存行为不对 (hit=" + hitOld + " miss=" + missNew + ")"); }
            }

            // 合集识别：合集本身也有 file_url，指向缩略图 JPG。曾经按「没有直链才算合集」判断，
            // 结果粘贴一个合集链接，下下来的是一张封面图被存成 .vpk。这条断言就是防这个回归。
            WsItem col = new WsItem();
            col.FileName = "1791607851_new_preview_Outland Necrotics Thumbnail.jpg";
            col.Url = "https://images.steamusercontent.com/ugc/13962854076826563678/FEA2A020/";
            col.Size = 116242;
            WsItem modItem = new WsItem();
            modItem.FileName = "myl4d2addons/gordon as louis.vpk";
            modItem.Url = "https://cdn.steamusercontent.com/ugc/1812/B48E/";
            modItem.Size = 35557542;

            if (!LooksLikeCollection(col))
            { bad++; Core.Log("FAIL", "WS 没把「缩略图 jpg + images 域名」认成合集"); }
            else Core.Log("INFO", "WS 合集识别：缩略图 jpg → 是合集");
            if (LooksLikeCollection(modItem))
            { bad++; Core.Log("FAIL", "WS 把正常的 .vpk 物品误判成合集了"); }
            else Core.Log("INFO", "WS 合集识别：.vpk 物品 → 不是合集");

            // 浏览页解析：新版是 JS 渲染，结果在转义 JSON 里（\"publishedfileid\":\"123\"）；
            // 老版才是 <a class="workshopItem">。两条路都得能出 id，且按文档顺序、去重。
            string newPage = "\\\"publishedfileid\\\":\\\"111\\\",\\\"title\\\":\\\"a\\\"}"
                           + " \\\"publishedfileid\\\":\\\"222\\\" ... \\\"publishedfileid\\\":\\\"111\\\"";
            List<string> nb = ParseBrowse(newPage);
            if (nb.Count != 2 || nb[0] != "111" || nb[1] != "222")
            { bad++; Core.Log("FAIL", "WS 新版浏览页解析得到 " + nb.Count + " 个: " + string.Join(",", nb.ToArray())); }
            else Core.Log("INFO", "WS 新版浏览页（转义 JSON）解析 = 111,222（去重保序）");

            string oldPage = "<a class=\"workshopItem\" href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=333\">"
                           + "<a class=\"workshopItemTitle\" href=\"...?id=444&searchtext=x\">";
            List<string> ob = ParseBrowse(oldPage);
            if (ob.Count != 2 || ob[0] != "333" || ob[1] != "444")
            { bad++; Core.Log("FAIL", "WS 老版浏览页解析得到 " + string.Join(",", ob.ToArray())); }
            else Core.Log("INFO", "WS 老版浏览页（workshopItem 链接）解析 = 333,444");

            // 槽位 → 搜索词：拉丁名直接用，中文名退回模型键尾段
            if (SearchWord("AK-47", "c:AK-47") != "AK-47" || SearchWord("木喷", "枪械\u0001w_shotgun_pump") != "pump")
            { bad++; Core.Log("FAIL", "WS 槽位搜索词推错: " + SearchWord("AK-47", "c:AK-47") + " / " + SearchWord("木喷", "枪械\u0001w_shotgun_pump")); }
            else Core.Log("INFO", "WS 槽位搜索词：AK-47 → AK-47，木喷 → pump");

            // 下载完自动改名：addoninfo 的游戏内名 > 工坊标题。读不出保留原名，撞名加序号不覆盖。
            string rnDir = Path.Combine(Path.GetTempPath(), "ws-rename-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                Directory.CreateDirectory(rnDir);
                string v1 = Path.Combine(rnDir, "【工坊标题】一长串.vpk");
                File.WriteAllBytes(v1, Work.BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"黑色AK103\"\r\n}\r\n", true));
                string r1 = RenameToAddonTitle(v1);
                if (r1 == "黑色AK103.vpk" && File.Exists(Path.Combine(rnDir, r1)) && !File.Exists(v1))
                    Core.Log("INFO", "WS 改名：addoninfo 标题生效 → " + r1);
                else { bad++; Core.Log("FAIL", "WS 改名：应为「黑色AK103.vpk」，实际「" + r1 + "」"); }

                string r2 = RenameToAddonTitle(Path.Combine(rnDir, "黑色AK103.vpk"));
                if (r2 == null) Core.Log("INFO", "WS 改名：名字已一致，不动");
                else { bad++; Core.Log("FAIL", "WS 改名：名字一致却返回了「" + r2 + "」"); }

                string v3 = Path.Combine(rnDir, "没有addoninfo.vpk");
                File.WriteAllBytes(v3, Work.BuildTinyVpk(null, false));
                string r3 = RenameToAddonTitle(v3);
                if (r3 == null && File.Exists(v3)) Core.Log("INFO", "WS 改名：没有 addoninfo，保留原文件名");
                else { bad++; Core.Log("FAIL", "WS 改名：没有 addoninfo 却动了文件（r3=" + r3 + "）"); }

                string v4 = Path.Combine(rnDir, "另一个工坊名.vpk");
                File.WriteAllBytes(v4, Work.BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"黑色AK103\"\r\n}\r\n", true));
                string r4 = RenameToAddonTitle(v4);
                if (r4 == "黑色AK103 (2).vpk" && File.Exists(Path.Combine(rnDir, "黑色AK103.vpk")))
                    Core.Log("INFO", "WS 改名：撞名加序号 " + r4 + "，没覆盖别人");
                else { bad++; Core.Log("FAIL", "WS 改名：撞名应得「黑色AK103 (2).vpk」，实际「" + r4 + "」"); }
            }
            catch (Exception ex) { bad++; Core.Log("FAIL", "WS 改名自检抛异常：" + ex.Message); }
            finally { try { Directory.Delete(rnDir, true); } catch { } }

            // 本地模式的下载落点必须是 addons 根目录本身，不许在下面再建 _创意工坊。
            // 以前拼了子目录，工具扫不到（ScanGameAddons 只 TopDirectoryOnly）、游戏也不读，
            // 新人下完第一个 MOD 界面里还是 0 个。用临时假游戏根验，不碰用户真实配置。
            {
                string savedGame = Core.Cfg.GameRoot;
                string savedMode = Core.Cfg.Mode;
                string savedWs = Core.Cfg.WorkshopDir;
                string fake = Path.Combine(Path.GetTempPath(), "ws-dest-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    string addons = Path.Combine(Path.Combine(fake, "left4dead2"), "addons");
                    Directory.CreateDirectory(addons);
                    File.WriteAllText(Path.Combine(fake, "left4dead2.exe"), "x");
                    Core.Cfg.GameRoot = fake;
                    Core.Cfg.Mode = "local";
                    Core.Cfg.WorkshopDir = "";
                    string got = DestDir();
                    bool same = string.Equals(got, addons, StringComparison.OrdinalIgnoreCase);
                    bool noSub = !Directory.Exists(Path.Combine(addons, "_创意工坊"));
                    if (same && noSub) Core.Log("INFO", "WS 下载落点：本地模式 = addons 根，未建子目录");
                    else
                    {
                        bad++;
                        Core.Log("FAIL", "WS 下载落点不对：got=" + got + " want=" + addons
                            + " 子目录存在=" + (!noSub));
                    }
                }
                catch (Exception ex) { bad++; Core.Log("FAIL", "WS 下载落点自检抛异常：" + ex.Message); }
                finally
                {
                    Core.Cfg.GameRoot = savedGame;
                    Core.Cfg.Mode = savedMode;
                    Core.Cfg.WorkshopDir = savedWs;
                    try { Directory.Delete(fake, true); } catch { }
                }
            }

            return bad;
        }

        static void CheckName(ref int bad, string title, string want)
        {
            string got = SafeFileName(title, "123");
            if (got != want)
            {
                string t = title == null ? "" : (title.Length > 20 ? title.Substring(0, 20) + "…" : title);
                bad++;
                Core.Log("FAIL", "WS 文件名「" + t + "」得到「" + got + "」，应为「" + want + "」");
            }
            else Core.Log("INFO", "WS 文件名 = " + got);
        }

        /// <summary>
        /// 联网端到端自检（--workshopnet=ID）：真的查一次、下一次，落到临时目录校验魔数和大小后删掉。
        /// 不落库、不改配置 —— 只用来确认「UA、流读取、大小校验」这条链路是通的。
        /// </summary>
        internal static int NetTest(string id)
        {
            int bad = 0;
            List<WsItem> items = Resolve(new List<string> { id });
            WsItem it = null;
            foreach (WsItem x in items)
                if (!x.IsCollection && x.Url.Length > 0) { it = x; break; }
            if (it == null)
            {
                Core.Log("FAIL", "WSNET 解析不到可下载的物品 " + id);
                return 1;
            }
            Core.Log("INFO", "WSNET " + it.Id + "「" + it.Title + "」" + Core.Human(it.Size));

            // 正文是详情页的主体，必须拿真实数据验一次：上一版字段名写错（file_description），
            // 离线自检全绿，但真实数据里全都是空串，详情页统一显示「这个物品没有写介绍」。
            string rawDesc = it.Desc == null ? "" : it.Desc;
            string cleanDesc = WsText.Clean(rawDesc);
            Core.Log("INFO", "WSNET 正文 原文 " + rawDesc.Length + " 字 / 清洗后 " + cleanDesc.Length + " 字");
            if (rawDesc.Length == 0)
            {
                bad++;
                Core.Log("FAIL", "WSNET 正文是空的 —— 接口字段名可能又变了");
            }
            else if (cleanDesc.Length == 0)
            {
                bad++;
                Core.Log("FAIL", "WSNET 正文清洗后变空 —— BBCode 清洗器把内容吃掉了");
            }
            else Core.Log("INFO", "WSNET 正文前 40 字：" + cleanDesc.Substring(0, Math.Min(40, cleanDesc.Length)).Replace("\n", " "));

            // 翻译通道：只记录不判失败（翻译是锦上添花，挂了不该让下载自检变红），
            // 但必须能看见这次走的是哪条路 —— 上一条路（微软鉴权）已经 404 了。
            if (cleanDesc.Length > 0)
            {
                try
                {
                    string terr;
                    string zh = WsTranslate.Translate(it.Id, it.Updated, cleanDesc, out terr);
                    Core.Log("INFO", "WSNET 翻译 通道=" + (WsTranslate.LastProvider == null ? "" : WsTranslate.LastProvider)
                        + " 译文 " + zh.Length + " 字" + (terr != null ? "（" + terr + "）" : ""));
                    if (zh.Length > 0)
                        Core.Log("INFO", "WSNET 译文前 40 字：" + zh.Substring(0, Math.Min(40, zh.Length)).Replace("\n", " "));
                }
                catch (Exception tex) { Core.Log("WARN", "WSNET 翻译异常: " + tex.Message); }
            }

            string tmp = Path.Combine(Path.GetTempPath(), "ws_" + id + ".vpk");
            string rn = null;
            try
            {
                long last = -1;
                Download(it, tmp, delegate(long got, long total)
                {
                    if (total > 0 && got == total && last != got) last = got;
                });

                long real = new FileInfo(tmp).Length;
                Core.Log("INFO", "WSNET 落盘 " + real + " 字节（接口报 " + it.Size + "）");
                if (it.Size > 0 && real != it.Size)
                {
                    bad++;
                    Core.Log("FAIL", "WSNET 大小对不上");
                }

                using (FileStream fs = File.OpenRead(tmp))
                {
                    byte[] h = new byte[4];
                    int n = fs.Read(h, 0, 4);
                    bool vpk = n == 4 && h[0] == 0x34 && h[1] == 0x12 && h[2] == 0xAA && h[3] == 0x55;
                    if (!vpk) { bad++; Core.Log("FAIL", "WSNET 下到的不是 vpk（魔数不是 55AA1234）"); }
                    else Core.Log("INFO", "WSNET vpk 魔数正确");
                }

                // 下载完自动改名（与对话框同一条路径）：addoninfo 游戏内名 > 工坊标题
                rn = RenameToAddonTitle(tmp);
                if (rn != null) Core.Log("INFO", "WSNET 已按游戏内名改名 → " + rn);
                else Core.Log("INFO", "WSNET 没改名（addoninfo 没有标题或名字一致），保留「" + Path.GetFileName(tmp) + "」");
                string gone = File.Exists(tmp) ? Path.GetFileName(tmp) : rn;
                if (gone != null) Core.Log("INFO", "WSNET 最终文件名「" + gone + "」");
                // 顺手验一遍预览图落盘（库里没打包 addonimage 时，封面就靠这张同名图）
                string finalPath = rn != null ? Path.Combine(Path.GetDirectoryName(tmp), rn) : tmp;
                SavePreview(it, finalPath);
                string fs0 = Path.GetFileNameWithoutExtension(finalPath);
                string side = null;
                foreach (string e0 in new string[] { ".jpg", ".jpeg", ".png", ".gif" })
                { string cand = Path.Combine(Path.GetDirectoryName(finalPath), fs0 + e0); if (File.Exists(cand)) { side = cand; break; } }
                if (side != null) Core.Log("INFO", "WSNET 预览图已保存 " + new FileInfo(side).Length + " 字节");
                else Core.Log("WARN", "WSNET 预览图没存下来");
            }
            catch (Exception ex)
            {
                bad++;
                Core.Log("FAIL", "WSNET 下载失败: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                try { if (File.Exists(tmp + ".part")) File.Delete(tmp + ".part"); } catch { }
                try { if (rn != null) File.Delete(Path.Combine(Path.GetDirectoryName(tmp), rn)); } catch { }
                try { string fs1 = Path.GetFileNameWithoutExtension(rn != null ? Path.Combine(Path.GetDirectoryName(tmp), rn) : tmp);
                      foreach (string e0 in new string[] { ".jpg", ".jpeg", ".png", ".gif" })
                      { string cand = Path.Combine(Path.GetDirectoryName(tmp), fs1 + e0); if (File.Exists(cand)) File.Delete(cand); } } catch { }
            }
            return bad;
        }

        /// <summary>
        /// 联网搜索自检（--workshopsearch=词）：走真实的 browse 页。
        /// 页面是 JS 渲染、结果在转义 JSON 里 —— 解析器改版后必须拿真页面验一遍才作数。
        /// </summary>
        internal static int SearchTest(string query)
        {
            int bad = 0;
            // 用 SearchItems（浏览页直解）测 —— 这是画廊/列表的真实路径
            List<WsItem> items = SearchItems(query, "mostsubscribed", 1);
            Core.Log("INFO", "WSS 搜「" + query + "」第 1 页 = " + items.Count + " 个");
            if (items.Count < 5)
            {
                Core.Log("FAIL", "WSS 结果少于 5 个，浏览页解析多半断了");
                return 1;
            }
            if (items.Count > 30)
            {
                bad++;
                Core.Log("FAIL", "WSS 一页不该超过 30 个（侧栏推荐混进来了？），实际 " + items.Count);
            }

            // 每个条目的关键字段：ID / 标题 / 订阅数（浏览页直解，不再走详情接口）
            foreach (WsItem it in items.GetRange(0, Math.Min(3, items.Count)))
            {
                if (it.Id.Length == 0 || string.IsNullOrEmpty(it.Title))
                { bad++; Core.Log("FAIL", "WSS 条目字段缺失: id=" + it.Id + " title=" + (it.Title ?? "")); }
                else Core.Log("INFO", "WSS " + it.Id + "「" + (it.Title.Length <= 46 ? it.Title : it.Title.Substring(0, 46) + "…") + "」订阅 " + it.Subs);
            }

            // 翻页：画廊现在是「往下滑就往后追加」，全靠这两点 —— 第 2 页取得到、且和第 1 页不重复。
            List<WsItem> p2 = SearchItems(query, "mostsubscribed", 2);
            Core.Log("INFO", "WSS 第 2 页 = " + p2.Count + " 个");
            if (p2.Count == 0)
            {
                bad++;
                Core.Log("FAIL", "WSS 第 2 页是空的 —— 翻页没生效，滑动加载会卡在第一页");
            }
            else
            {
                HashSet<string> s1 = new HashSet<string>(StringComparer.Ordinal);
                foreach (WsItem x in items) s1.Add(x.Id);
                int dup = 0;
                foreach (WsItem x in p2) if (s1.Contains(x.Id)) dup++;
                if (dup > 0)
                {
                    bad++;
                    Core.Log("FAIL", "WSS 第 1/2 页重复 " + dup + " 个 —— 追加后会出现重复卡片");
                }
                else Core.Log("INFO", "WSS 第 1/2 页无重复，追加安全");
            }
            if (items.Count < 30)
                Core.Log("WARN", "WSS 第 1 页只有 " + items.Count + " 个（不足 30），界面会判成「已到底」—— 这个词本身结果就少");
            return bad;
        }
    }

}
