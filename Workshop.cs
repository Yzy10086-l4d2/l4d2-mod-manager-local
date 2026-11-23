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
        // 只对 steamcommunity.com 用「IP + Host 头」绕污染：api.steampowered.com 与 cdn.steamusercontent.com
        // 都是 Akamai CDN，本身不被墙，把 URL 换成裸 IP 反而会丢掉 TLS SNI 导致握手被拒或落到死节点。
        static readonly string[] PinHosts = new string[]
        {
            "steamcommunity.com"
        };

        // 最近一次失败的原因 + 探针明细：界面要能说清「为什么连不上」，日志要能看出探了哪些 IP
        static string lastWhy;
        static string probeNote;
        static DateTime lastLogAt;

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
                // 启动体检：三条线路各测一次，选最快的那条，之后所有 Steam 请求先走它。
                // 以前每条请求都按「直连→代理→DNS」硬走，线路死了每次白等 7.5 秒，推荐区自然「点几下才出来」。
                ProbeRoutes();
            });
        }

        /// <summary>优选 IP 或 Akamai 域名前置（SNI Fronting）时，证书域名与请求 Host 头不一致 —— 只对 Steam 自己的域名放行这一种错误。</summary>
        static bool CertOk(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors err)
        {
            if (err == SslPolicyErrors.None) return true;
            if ((err & SslPolicyErrors.RemoteCertificateNameMismatch) == 0) return false;
            try
            {
                HttpWebRequest r = sender as HttpWebRequest;
                return r != null && IsSteamHost(r.Host ?? "");
            }
            catch { return false; }
        }

        /// <summary>是不是 Steam 的域名（离线冷却与 SNI 前置只对它们生效）。</summary>
        static bool IsSteamHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.ToLowerInvariant();
            return h.EndsWith("steamcommunity.com", StringComparison.Ordinal)
                || h.EndsWith("steampowered.com", StringComparison.Ordinal)
                || h.EndsWith("steamusercontent.com", StringComparison.Ordinal)
                || h.EndsWith("steamstatic.com", StringComparison.Ordinal)
                || h.EndsWith("akamaihd.net", StringComparison.Ordinal);
        }

        // Akamai 边缘同集群未阻断的 SNI 前置域名（实测关加速器时 api.steampowered.com 的 TLS SNI 被掐，换前置域名 + Host 头即 200 OK）。
        // 不写死一个：各省运营商封的域名不一样，第一次用到时挨个探一遍，谁能通就用谁并记住，之后不再重复探。
        static readonly string[] FrontDomains = new string[]
        {
            "steamuserimages-a.akamaihd.net",
            "steamusercontent-a.akamaihd.net",
            "steamcdn-a.akamaihd.net",
            "cloud-3.steamusercontent.com"
        };
        static string frontChosen;

        internal static string FrontDomain
        {
            get
            {
                lock (gate) { if (!string.IsNullOrEmpty(frontChosen)) return frontChosen; }
                foreach (string f in FrontDomains)
                {
                    try
                    {
                        HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://" + f + "/");
                        req.Method = "GET";
                        req.Timeout = 3000;
                        req.ReadWriteTimeout = 3000;
                        try { req.Proxy = null; } catch { }
                        using (HttpWebResponse r = (HttpWebResponse)req.GetResponse()) { }
                        lock (gate) { frontChosen = f; }
                        try { Core.Log("INFO", "工坊：Akamai 前置域名用 " + f); } catch { }
                        return f;
                    }
                    catch (WebException wex)
                    {
                        if (wex.Response is HttpWebResponse)
                        {
                            lock (gate) { frontChosen = f; }
                            try { Core.Log("INFO", "工坊：Akamai 前置域名用 " + f); } catch { }
                            return f;
                        }
                    }
                    catch { }
                }
                return FrontDomains[0];
            }
        }

        internal static bool CanFrontHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.ToLowerInvariant();
            // steamcommunity.com 也在 Akamai 边缘上：实测经前置域名 + Host 头能拿到工坊详情页 HTML（200 / 75KB），
            // 所以「配套 MOD」这种只能从网页里刮的信息，没有加速器也能取到。
            return h.EndsWith("steampowered.com", StringComparison.Ordinal)
                || h.EndsWith("steamusercontent.com", StringComparison.Ordinal)
                || h.EndsWith("steamcommunity.com", StringComparison.Ordinal);
        }

        /// <summary>线路自检：走完整路由阶梯（含优选 IP / 系统代理 / Akamai 前置），404 也算链路通（CDN 根路径本就是 404）。</summary>
        internal static string ProbeLine(string url)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string host = HostOf(url);
            string testUrl = url;
            if (host != null && host.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase))
                testUrl = "https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/";
            byte[] b = GetBytes(testUrl, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36", 6000);
            sw.Stop();
            if (b == null && host != null && host.EndsWith("steamusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                // cdn.steamusercontent.com 根路径返回 HTTP 404，只要能收到 404 响应就说明 TLS 握手和链路是通的
                sw.Restart();
                if (ProbeHttpReachable(url, 5000))
                {
                    sw.Stop();
                    return "✅ " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒（" + RouteName(Preferred(host)) + "）";
                }
            }
            if (b == null)
            {
                string why = LastWhy;
                return "❌ " + (why.Length > 0 ? why : "连不上");
            }
            return "✅ " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒（" + RouteName(Preferred(host)) + "）";
        }

        static bool ProbeHttpReachable(string url, int timeoutMs)
        {
            foreach (string targetHost in new string[] { null, FrontDomain })
            {
                try
                {
                    Uri u = new Uri(url);
                    string reqUrl = url;
                    if (targetHost != null) { UriBuilder ub = new UriBuilder(u); ub.Host = targetHost; reqUrl = ub.Uri.AbsoluteUri; }
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(reqUrl);
                    if (targetHost != null) req.Host = u.Host;
                    req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs;
                    req.Proxy = PreferredProxy(u.Host);
                    using (HttpWebResponse r = (HttpWebResponse)req.GetResponse()) return true;
                }
                catch (WebException wex)
                {
                    if (wex.Response is HttpWebResponse) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>系统代理长什么样（没有就返回「无」）。</summary>
        internal static string SystemProxyDesc()
        {
            try
            {
                IWebProxy p = SystemProxy();
                if (p == null) return "无";
                Uri probe = new Uri("https://steamcommunity.com/");
                Uri got = p.GetProxy(probe);
                return got == null ? "无" : got.ToString();
            }
            catch { return "未知"; }
        }

        static bool IsPinHost(string host)
        {
            foreach (string h in PinHosts) if (string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static readonly Dictionary<string, DateTime> directDead = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>这个域名最近直连确认走不通（被掐 SNI）：十分钟内别再浪费一轮超时。</summary>
        static bool DirectBlocked(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            lock (gate) { DateTime at; return directDead.TryGetValue(host, out at) && DateTime.Now < at; }
        }

        static void BlockDirect(string host)
        {
            if (!CanFrontHost(host)) return;
            lock (gate) { directDead[host] = DateTime.Now.AddMinutes(10); }
        }

        public static string GetString(string url, string ua, int timeoutMs)
        {
            byte[] b = GetBytes(url, ua, timeoutMs);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        /// <summary>
        /// 阶梯：① 直连（优选 IP）→ ② 系统代理 → ③ 换公共 DNS 重新解析再直连。
        /// 每档 4 秒封顶、总共约 12 秒 —— 让人等 30 秒再告诉他「连不上」是最差的体验。
        /// 阶梯里没有一处需要用户去填东西：装了 Clash / 加速器的，第二档自动吃上。
        /// </summary>
        public static byte[] GetBytes(string url, string ua, int timeoutMs)
        {
            Tune();
            string host = HostOf(url);
            // 离线冷却只管 steamcommunity.com 网页主站：API 与图片 CDN 绝不跟着陪葬
            if (IsOffline && IsCommunityHost(host)) return null;
            int cap = Math.Min(timeoutMs, TierMs);

            // 检查代理是否刚变、或者当前路线已经过期（>3 分钟），必要时后台重测
            SystemProxy();
            MaybeReprobe(host);

            // API 与 UGC CDN 的域名 SNI 在国内被掐，同集群的 Akamai 前置域名可用。
            // 直连一旦确认走不通就记住十分钟，后续请求直接走前置，不再每次都先陪直连白等一轮超时。
            if (CanFrontHost(host) && DirectBlocked(host) && SystemProxy() == null)
            {
                byte[] fb = TryGetFronted(url, ua, Math.Min(timeoutMs, 6000), FrontDomain, null);
                if (fb != null) { MarkOnline(host); return fb; }
            }

            NetRoute pref = Preferred(host);
            bool triedDirect = pref == NetRoute.Direct, triedProxy = pref == NetRoute.Proxy, triedDns = pref == NetRoute.Dns;
            byte[] b = null;
            if (pref != NetRoute.Unknown)
            {
                Stopwatch sw = Stopwatch.StartNew();
                b = TryTransport(url, ua, cap, pref);
                sw.Stop();
                if (b != null)
                {
                    MarkOnline(host);
                    NoteRouteTime(host, pref, (int)sw.ElapsedMilliseconds);
                    return b;
                }
                Invalidate(host);              // 体检结论这次不灵：回退常规阶梯，同时触发后台重测
            }

            if (b == null && !triedDirect && !DirectBlocked(host))
            {
                Stopwatch sw = Stopwatch.StartNew();
                b = TryGet(url, ua, cap, true, null);
                sw.Stop();
                if (b != null) { MarkOnline(host); LearnRoute(host, NetRoute.Direct, (int)sw.ElapsedMilliseconds); return b; }
                BlockDirect(host);             // 直连确认走不通：记下来，十分钟内别再试
            }

            IWebProxy px = SystemProxy();
            if (b == null && !triedProxy && px != null)
            {
                Stopwatch sw = Stopwatch.StartNew();
                b = TryGet(url, ua, cap, false, px);
                sw.Stop();
                if (b != null) { MarkOnline(host); LearnRoute(host, NetRoute.Proxy, (int)sw.ElapsedMilliseconds); try { Core.Log("INFO", "工坊：直连不通，改走系统代理连上了"); } catch { } return b; }
            }

            if (b == null && !triedDns && host != null)
            {
                Stopwatch sw = Stopwatch.StartNew();
                b = TryTransport(url, ua, cap, NetRoute.Dns);
                sw.Stop();
                if (b != null) { MarkOnline(host); LearnRoute(host, NetRoute.Dns, (int)sw.ElapsedMilliseconds); try { Core.Log("INFO", "工坊：走 Akamai 前置/公共 DNS 连上了 (" + host + ")"); } catch { } return b; }
            }

            // 只有工坊主站挂了才标主站离线（90 秒），不影响 API 和 CDN
            if (IsCommunityHost(host)) GoOffline();
            return null;
        }

        static bool IsCommunityHost(string host)
        {
            return !string.IsNullOrEmpty(host) && host.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- 线路体检与动态择优：直连 / 系统代理 / 公共DNS ----------------

        internal enum NetRoute { Unknown = 0, Direct = 1, Proxy = 2, Dns = 3 }

        static NetRoute steamRoute = NetRoute.Unknown;
        static int steamRouteMs = -1;
        static bool steamProbed;
        static NetRoute apiRoute = NetRoute.Unknown;
        static int apiRouteMs = -1;
        static bool apiProbed;
        static NetRoute cdnRoute = NetRoute.Unknown;
        static int cdnRouteMs = -1;
        static DateTime lastProbeAt = DateTime.MinValue;
        static int probing;

        /// <summary>体检跑完了没有（没跑完之前不要拿「不通」下结论）。</summary>
        internal static bool SteamProbed { get { lock (gate) { return steamProbed; } } }
        /// <summary>体检结果：网页主站这条线现在是活的。</summary>
        internal static bool SteamRouteAlive { get { lock (gate) { return steamProbed && steamRoute != NetRoute.Unknown; } } }
        internal static int SteamRouteMs { get { lock (gate) { return steamRouteMs; } } }
        /// <summary>体检结果：API 接口这条线现在是活的。</summary>
        internal static bool ApiRouteAlive { get { lock (gate) { return !apiProbed || apiRoute != NetRoute.Unknown; } } }
        internal static int ApiRouteMs { get { lock (gate) { return apiRouteMs; } } }

        static string RouteName(NetRoute r)
        {
            switch (r)
            {
                case NetRoute.Direct: return "直连";
                case NetRoute.Proxy: return "系统代理";
                case NetRoute.Dns: return "公共DNS";
            }
            return "未知";
        }

        static byte[] TryTransport(string url, string ua, int cap, NetRoute route)
        {
            switch (route)
            {
                case NetRoute.Direct: return TryGet(url, ua, cap, true, null);
                case NetRoute.Proxy:
                {
                    IWebProxy px = SystemProxy();
                    return px == null ? null : TryGet(url, ua, cap, false, px);
                }
                case NetRoute.Dns:
                {
                    string host = HostOf(url);
                    if (host == null) return null;
                    // API 与 CDN 的域名 SNI 在国内会被掐，走同集群未阻断的 Akamai 前置域名（SNI Fronting + 原 Host 头）
                    // 前置握手慢，工坊详情页是 75~130KB 的 HTML，实测要 7~10 秒传完：网页给 20 秒，接口给 8 秒。
                    // （以前一律 6 秒，于是「主站」永远判不通，连带配套 MOD 识别也失效）
                    if (CanFrontHost(host))
                    {
                        int fc = host.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                            ? Math.Max(cap, 20000)
                            : Math.Max(cap, 8000);
                        return TryGetFronted(url, ua, fc, FrontDomain, null);
                    }
                    if (!ResolveByPublicDns(host)) return null;
                    return TryGet(url, ua, cap, IsPinHost(host), null);
                }
            }
            return null;
        }

        /// <summary>根据上次探测出的最优路线，给当前请求选代理（直连/DNS 返回 null，代理返回系统代理）。</summary>
        internal static IWebProxy PreferredProxy(string host)
        {
            return Preferred(host) == NetRoute.Proxy ? SystemProxy() : null;
        }

        static NetRoute Preferred(string host)
        {
            if (string.IsNullOrEmpty(host)) return NetRoute.Unknown;
            lock (gate)
            {
                string h = host.ToLowerInvariant();
                if (h.EndsWith("steamcommunity.com", StringComparison.Ordinal)) return steamRoute;
                if (h.EndsWith("steampowered.com", StringComparison.Ordinal)) return apiRoute;
                if (h.EndsWith("steamusercontent.com", StringComparison.Ordinal)) return cdnRoute != NetRoute.Unknown ? cdnRoute : apiRoute;
            }
            return NetRoute.Unknown;
        }

        /// <summary>当前路线这次失败或超时：立刻清掉首选并在后台重测三条线。</summary>
        static void Invalidate(string host)
        {
            if (string.IsNullOrEmpty(host)) return;
            lock (gate)
            {
                string h = host.ToLowerInvariant();
                if (h.EndsWith("steamcommunity.com", StringComparison.Ordinal)) steamRoute = NetRoute.Unknown;
                else if (h.EndsWith("steampowered.com", StringComparison.Ordinal)) apiRoute = NetRoute.Unknown;
                else if (h.EndsWith("steamusercontent.com", StringComparison.Ordinal)) cdnRoute = NetRoute.Unknown;
            }
            KickReprobe();
        }

        /// <summary>记录当前路线的真实耗时；如果劣化超过 1800ms，后台触发一次重测看有没有更快的路。</summary>
        static void NoteRouteTime(string host, NetRoute route, int elapsedMs)
        {
            if (string.IsNullOrEmpty(host)) return;
            bool slow = elapsedMs > 1800;
            lock (gate)
            {
                string h = host.ToLowerInvariant();
                if (h.EndsWith("steamcommunity.com", StringComparison.Ordinal)) steamRouteMs = steamRouteMs <= 0 ? elapsedMs : (steamRouteMs * 2 + elapsedMs) / 3;
                else if (h.EndsWith("steampowered.com", StringComparison.Ordinal)) apiRouteMs = apiRouteMs <= 0 ? elapsedMs : (apiRouteMs * 2 + elapsedMs) / 3;
                else if (h.EndsWith("steamusercontent.com", StringComparison.Ordinal)) cdnRouteMs = cdnRouteMs <= 0 ? elapsedMs : (cdnRouteMs * 2 + elapsedMs) / 3;
            }
            if (slow) KickReprobe();
        }

        /// <summary>某条线走通了：记成首选，下次直接用。</summary>
        static void LearnRoute(string host, NetRoute route, int elapsedMs)
        {
            if (string.IsNullOrEmpty(host) || route == NetRoute.Unknown) return;
            lock (gate)
            {
                string h = host.ToLowerInvariant();
                if (h.EndsWith("steamcommunity.com", StringComparison.Ordinal)) { steamRoute = route; steamRouteMs = elapsedMs; }
                else if (h.EndsWith("steampowered.com", StringComparison.Ordinal)) { apiRoute = route; apiRouteMs = elapsedMs; }
                else if (h.EndsWith("steamusercontent.com", StringComparison.Ordinal)) { cdnRoute = route; cdnRouteMs = elapsedMs; }
            }
        }

        static void MaybeReprobe(string host)
        {
            if (!IsSteamHost(host)) return;
            bool stale;
            lock (gate) { stale = steamProbed && (DateTime.Now - lastProbeAt).TotalMinutes >= 3; }
            if (stale) KickReprobe();
        }

        static void KickReprobe()
        {
            if (System.Threading.Interlocked.CompareExchange(ref probing, 1, 0) != 0) return;
            System.Threading.Tasks.Task.Run(delegate
            {
                try { ProbeRoutes(); }
                finally { System.Threading.Interlocked.Exchange(ref probing, 0); }
            });
        }

        /// <summary>
        /// 线路体检：网页主站（steamcommunity.com）和 API 接口（api.steampowered.com）各测三条线，
        /// 彼此完全独立 —— 网页主站不通绝不阻止 API 测速，谁快就记谁。
        /// </summary>
        static void ProbeRoutes()
        {
            NetRoute[] routes = new NetRoute[] { NetRoute.Direct, NetRoute.Proxy, NetRoute.Dns };
            int[] ms = new int[routes.Length];
            int[] ams = new int[routes.Length];
            System.Threading.Tasks.Parallel.For(0, routes.Length * 2, delegate(int k)
            {
                int i = k % routes.Length;
                bool isApi = k >= routes.Length;
                Stopwatch sw = Stopwatch.StartNew();
                byte[] b = null;
                try
                {
                    string u = isApi
                        ? "https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/"
                        : "https://steamcommunity.com/";
                    b = TryTransport(u, "Mozilla/5.0 (Windows NT 10.0; Win64; x64)", 2500, routes[i]);
                }
                catch { }
                sw.Stop();
                if (isApi) ams[i] = b != null ? (int)sw.ElapsedMilliseconds : -1;
                else ms[i] = b != null ? (int)sw.ElapsedMilliseconds : -1;
            });
            int best = -1;
            for (int i = 0; i < ms.Length; i++) if (ms[i] >= 0 && (best < 0 || ms[i] < ms[best])) best = i;
            int abest = -1;
            for (int i = 0; i < ams.Length; i++) if (ams[i] >= 0 && (abest < 0 || ams[i] < ams[abest])) abest = i;
            bool anyWeb = best >= 0, anyApi = abest >= 0;
            lock (gate)
            {
                lastProbeAt = DateTime.Now;
                steamProbed = true;
                apiProbed = true;
                steamRoute = anyWeb ? routes[best] : NetRoute.Unknown;
                steamRouteMs = anyWeb ? ms[best] : -1;
                apiRoute = anyApi ? routes[abest] : NetRoute.Unknown;
                apiRouteMs = anyApi ? ams[abest] : -1;
                if (cdnRoute == NetRoute.Unknown && anyApi) { cdnRoute = routes[abest]; cdnRouteMs = ams[abest]; }
                if (anyWeb) offlineUntil = DateTime.MinValue;
                if (anyApi) apiDeadUntil = DateTime.MinValue;
            }
            try
            {
                string line = "线路体检：主站[直连=" + (ms[0] >= 0 ? ms[0] + "ms" : "✗")
                    + "/代理=" + (ms[1] >= 0 ? ms[1] + "ms" : "✗")
                    + "/DNS=" + (ms[2] >= 0 ? ms[2] + "ms" : "✗")
                    + (anyWeb ? "→" + RouteName(routes[best]) : "→不通") + "] · "
                    + "API[直连=" + (ams[0] >= 0 ? ams[0] + "ms" : "✗")
                    + "/代理=" + (ams[1] >= 0 ? ams[1] + "ms" : "✗")
                    + "/DNS=" + (ams[2] >= 0 ? ams[2] + "ms" : "✗")
                    + (anyApi ? "→" + RouteName(routes[abest]) : "→不通") + "]";
                Core.Log((anyWeb || anyApi) ? "INFO" : "WARN", line);
            }
            catch { }
            if (!anyWeb) GoOffline();
        }

        static byte[] TryGet(string url, string ua, int timeoutMs, bool allowPin, IWebProxy proxy)
        {
            Uri u = null;
            try { u = new Uri(url); } catch { }
            string pinnedUrl = null;
            try
            {
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
                try { req.Proxy = proxy; } catch { }
                try { req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[16 * 1024];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    byte[] data = ms.ToArray();
                    if (data.Length == 0 && pinnedUrl != null) { DropPin(u.Host); return null; }
                    lock (gate) { lastWhy = null; }              // 通了就别再记着上一次的失败
                    return data;
                }
            }
            catch (Exception ex)
            {
                NoteFail(ex);
                // 只记最终那次（退回域名）的失败：优选 IP 那趟失败是预期内的，记了反而误导
                if (!allowPin) LogOnce("工坊请求失败: " + (u != null ? u.Host : url) + " → " + ex.Message);
                if (pinnedUrl != null) DropPin(u.Host);
                return null;
            }
        }

        /// <summary>Akamai 域名前置请求：连 frontHost 的 TLS（避开对原域名的 SNI 阻断），HTTP Host 头仍发原域名。</summary>
        internal static byte[] TryGetFronted(string url, string ua, int timeoutMs, string frontHost, IWebProxy proxy)
        {
            try
            {
                Uri u = new Uri(url);
                UriBuilder ub = new UriBuilder(u);
                ub.Host = frontHost;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ub.Uri.AbsoluteUri);
                req.Host = u.Host;
                req.Method = "GET";
                req.UserAgent = ua;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.KeepAlive = true;
                try { req.Proxy = proxy; } catch { }
                try { req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[16 * 1024];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    byte[] data = ms.ToArray();
                    if (data.Length == 0) return null;
                    lock (gate) { lastWhy = null; }
                    return data;
                }
            }
            catch (Exception ex)
            {
                NoteFail(ex);
                // 前置域名这次不通：把选中的那个作废，下次重新探一轮（否则会一直抱着一个坏域名）
                if (!string.IsNullOrEmpty(frontHost))
                    lock (gate) { if (string.Equals(frontChosen, frontHost, StringComparison.OrdinalIgnoreCase)) frontChosen = null; }
                return null;
            }
        }

        // Steam UGC 同源的 Akamai 边缘节点池：每个连接单流限速约 100~180 KB/s，多段跨节点并发可叠加带宽并绕开 cdn.steamusercontent.com 的 TLS SNI 阻断
        static readonly string[] UgcMirrors = new string[]
        {
            "steamuserimages-a.akamaihd.net",
            "steamusercontent-a.akamaihd.net",
            "cloud-3.steamusercontent.com",
            "cdn.akamai.steamstatic.com",
            "steamcdn-a.akamaihd.net",
            "shared.akamai.steamstatic.com",
            "store.akamai.steamstatic.com",
            "community.akamai.steamstatic.com"
        };

        /// <summary>
        /// 一条下载的暂停 / 取消开关。下载线程在每个读循环里查它，点按钮不用等这一包下完；
        /// 放在这里而不是界面层：界面可能已经关了（下载还在跑），开关不能跟着窗口一起没。
        /// </summary>
        internal sealed class DlCtl
        {
            public readonly string Id;
            public volatile bool Pause;
            public volatile bool Cancel;
            public DlCtl(string id) { Id = id ?? ""; }
            public void Tick()
            {
                if (Cancel) throw new DlAbort(true);
                if (Pause) throw new DlAbort(false);
            }
        }

        /// <summary>暂停（Canceled=false）/ 取消（true）时从下载线程里抛出来，一路往上交给界面收尾。</summary>
        internal sealed class DlAbort : Exception
        {
            public readonly bool Canceled;
            public DlAbort(bool canceled) : base(canceled ? "已取消下载" : "已暂停下载") { Canceled = canceled; }
        }

        /// <summary>
        /// 分片临时目录名用稳定哈希：string.GetHashCode 换进程就变，
        /// 暂停后关掉程序再开就找不到上次的分片，只能从头下。
        /// </summary>
        static string TempPartDir(string partPath)
        {
            ulong h = 1469598103934665603UL;
            foreach (char c in partPath) { h ^= c; h *= 1099511628211UL; }
            return Path.Combine(Path.GetTempPath(), "l4d2dl-" + (h & 0xFFFFFFFFUL).ToString("x8"));
        }

        // ---------------- 下载赛马：同一段字节量几条线路，谁快走谁 ----------------

        /// <summary>一条候选线路：直连 / 系统代理 / Akamai 前置域名（https 或 HTTP 80）。</summary>
        internal sealed class DlRoute
        {
            public IWebProxy Proxy;
            public string Front;
            public bool UseHttp;
            public long Speed;              // 实测字节/秒；0 = 不通
            public string Name;
        }

        /// <summary>换线信号：能下但太慢（不是失败）。上层据此重新赛马，把 part 留着续传。</summary>
        internal const long SlowSignal = -2;

        static readonly Dictionary<string, List<DlRoute>> raceCache = new Dictionary<string, List<DlRoute>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, long> raceAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 下载赛马：拿一段真实字节（32KB）把候选线路各量一遍，按实测速度从高到低排。
        /// 结果按 host 缓存 90 秒 —— 一批 MOD 连着下时不必每件都重新量。
        /// </summary>
        internal static List<DlRoute> RaceRoutes(string url, string ua, string host, long totalHint)
        {
            if (string.IsNullOrEmpty(host)) return null;
            lock (gate)
            {
                List<DlRoute> hit;
                long at;
                if (raceCache.TryGetValue(host, out hit) && raceAt.TryGetValue(host, out at)
                    && Environment.TickCount - at < 90000 && hit != null && hit.Count > 0)
                    return hit;
            }
            List<DlRoute> cands = new List<DlRoute>();
            IWebProxy px = SystemProxy();
            if (px != null) cands.Add(new DlRoute { Proxy = px, Name = "系统代理" });
            cands.Add(new DlRoute { Name = "直连" });
            if (CanFrontHost(host))
            {
                // 前置域名只探前三个 + 对应 HTTP 80：太多条会把启动时间拖长（并行跑，总时长≈最慢那条）
                int n = Math.Min(FrontDomains.Length, 3);
                for (int i = 0; i < n; i++)
                    cands.Add(new DlRoute { Front = FrontDomains[i], Name = "前置 " + FrontDomains[i] });
                for (int i = 0; i < n; i++)
                    cands.Add(new DlRoute { Front = FrontDomains[i], UseHttp = true, Name = "前置80 " + FrontDomains[i] });
            }
            const long probeLen = 32 * 1024;
            System.Threading.Tasks.Parallel.For(0, cands.Count, delegate(int i)
            {
                cands[i].Speed = ProbeSpeed(url, ua, host, cands[i], probeLen);
            });
            cands.RemoveAll(delegate(DlRoute c) { return c.Speed <= 0; });
            cands.Sort(delegate(DlRoute a, DlRoute b) { return b.Speed.CompareTo(a.Speed); });
            if (cands.Count == 0) return null;
            lock (gate) { raceCache[host] = cands; raceAt[host] = Environment.TickCount; }
            try { Core.Log("INFO", "下载赛马 " + host + "：" + cands[0].Name + " " + (cands[0].Speed / 1024) + "KB/s（候选 " + cands.Count + " 条）"); } catch { }
            return cands;
        }

        /// <summary>量一条线路的实际吞吐：请求开头 32KB，读满或超时就按已读字节算速度。</summary>
        static long ProbeSpeed(string url, string ua, string host, DlRoute c, long probeLen)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                Uri u = new Uri(url);
                UriBuilder ub = new UriBuilder(u);
                if (c.Front != null) ub.Host = c.Front;
                if (c.UseHttp) { ub.Scheme = "http"; ub.Port = 80; }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ub.Uri.AbsoluteUri);
                if (c.Front != null) req.Host = host;
                req.UserAgent = ua;
                req.Timeout = 1800;
                req.ReadWriteTimeout = 1800;
                try { req.Proxy = c.Proxy; } catch { }
                req.AddRange(0, probeLen - 1);
                long got = 0;
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                {
                    if (r.StatusCode != HttpStatusCode.PartialContent && r.StatusCode != HttpStatusCode.OK) return 0;
                    using (Stream s = r.GetResponseStream())
                    {
                        byte[] buf = new byte[16384];
                        int n;
                        while (got < probeLen && (n = s.Read(buf, 0, buf.Length)) > 0) got += n;
                    }
                }
                long ms = Math.Max(1, sw.ElapsedMilliseconds);
                return got * 1000 / ms;
            }
            catch { return 0; }
        }

        /// <summary>分片用哪个域名：优先用赛马量出来较快的，没量过就用固定池。</summary>
        static string[] FrontOrder(string host)
        {
            List<DlRoute> rs = null;
            lock (gate) { List<DlRoute> hit; if (raceCache.TryGetValue(host ?? "", out hit)) rs = hit; }
            if (rs == null || rs.Count == 0) return UgcMirrors;
            List<string> fronts = new List<string>();
            foreach (DlRoute r in rs)
            {
                if (r.Front == null || r.UseHttp) continue;
                if (fronts.IndexOf(r.Front) < 0) fronts.Add(r.Front);
            }
            return fronts.Count > 0 ? fronts.ToArray() : UgcMirrors;
        }

        /// <summary>大文件下载：大于 2MB 的文件默认走 8 路跨节点并发分段下载（速度翻 6~8 倍且自动绕开 SNI 阻断）；小文件或断点续传走单连接阶梯。</summary>
        public static long DownloadTo(string url, string ua, int timeoutMs, string partPath, long totalHint, Action<long, long> progress)
        {
            return DownloadTo(url, ua, timeoutMs, partPath, totalHint, progress, null);
        }

        /// <summary>可中断版：ctl 非空时，界面点「暂停 / 取消」会从读循环里抛 DlAbort 退出，已下字节留在磁盘上。</summary>
        public static long DownloadTo(string url, string ua, int timeoutMs, string partPath, long totalHint, Action<long, long> progress, DlCtl ctl)
        {
            Tune();
            // 系统默认每站点 2 条连接：调到 40，否则上面 24 条并发会被这里悄悄排队（实测 32 条反而更慢就是这个原因）
            try { if (ServicePointManager.DefaultConnectionLimit < 40) ServicePointManager.DefaultConnectionLimit = 40; } catch { }
            if (ctl != null) ctl.Tick();

            // 默认开启多段跨节点并发（文件 ≥ 2MB 且无未完断点时）
            if (totalHint <= 0 || totalHint >= 2L * 1024 * 1024)
            {
                long partial;
                long mc = TryMultiParts(url, ua, timeoutMs, partPath, totalHint, progress, ctl, out partial);
                if (mc > 0)
                {
                    try { Core.Log("INFO", "下载：多段并发完成 " + (mc / 1048576) + " MB"); } catch { }
                    return mc;
                }
                // 分段下了一部分又换节点失败：不许退回单连接从头下（用户看到的就是「进度跳回起点重新下」）。
                // 分片留着，「继续 / 重试」从断点接着下。
                if (mc < 0)
                    throw new IOException("分段下载连续换节点仍未完成，已下 " + Core.Human(partial) + "；点「继续」接着下，不会从头重来");
            }
            Uri u = null;
            try { u = new Uri(url); } catch { }
            string host = u == null ? null : u.Host;

            // 下载也赛马：先拿 32KB 真实字节把几条候选线路各量一遍，谁快先走谁。
            // 跑到一半变慢（SlowSignal）就重开一次秒表，只有明显更快（≥1.3 倍）才换，免得来回跳。
            // 小文件（<2MB）不赛马：2 秒的探针比文件本身还久。
            List<DlRoute> raced = null;
            if (host != null && (totalHint <= 0 || totalHint >= 2L * 1024 * 1024 || File.Exists(partPath)))
            {
                try { raced = RaceRoutes(url, ua, host, totalHint); } catch { }
            }
            if (raced != null && raced.Count > 0)
            {
                int switches = 0, idx = 0;
                while (idx < raced.Count)
                {
                    if (ctl != null) ctl.Tick();
                    DlRoute r = raced[idx++];
                    long rg = TryDownload(url, ua, timeoutMs, partPath, totalHint, progress, false, r.Proxy, r.Front, r.UseHttp, ctl);
                    if (rg > 0)
                    {
                        LearnRoute(host, r.Proxy != null ? NetRoute.Proxy : (r.Front != null ? NetRoute.Dns : NetRoute.Direct), 0);
                        return rg;
                    }
                    if (rg != SlowSignal) continue;            // 普通失败：换下一条候选
                    switches++;
                    if (switches > 2) break;                   // 最多换两次
                    List<DlRoute> again = null;
                    try { again = RaceRoutes(url, ua, host, totalHint); } catch { }
                    if (again == null || again.Count == 0) break;
                    DlRoute best = null;
                    foreach (DlRoute cand in again)
                        if (cand.Speed > r.Speed * 13 / 10) { best = cand; break; }
                    if (best == null) break;                   // 没有明显更快的：别折腾
                    try { Core.Log("INFO", "下载：换到更快线路 " + best.Name + "（" + (best.Speed / 1024) + "KB/s）"); } catch { }
                    raced = again; idx = 0;
                }
                return -1;
            }

            // 候选一条都没量通：退回老的单连接阶梯（已知最快 → 前置 → 直连 → 代理）
            NetRoute pref = Preferred(host);
            IWebProxy px = SystemProxy();
            long got = -1;
            if (pref == NetRoute.Proxy && px != null)
            {
                got = TryDownload(url, ua, timeoutMs, partPath, totalHint, progress, false, px, null, false, ctl);
                if (got > 0) return got;
                Invalidate(host);
            }
            if (CanFrontHost(host))
            {
                got = TryDownload(url, ua, timeoutMs, partPath, totalHint, progress, false, null, FrontDomain, false, ctl);
                if (got > 0) { LearnRoute(host, NetRoute.Dns, 0); return got; }
            }
            got = TryDownload(url, ua, timeoutMs, partPath, totalHint, progress, false, null, null, false, ctl);
            if (got > 0) { LearnRoute(host, NetRoute.Direct, 0); return got; }
            if (px != null && pref != NetRoute.Proxy)
            {
                got = TryDownload(url, ua, timeoutMs, partPath, totalHint, progress, false, px, null, false, ctl);
                if (got > 0) { LearnRoute(host, NetRoute.Proxy, 0); try { Core.Log("INFO", "下载：直连不通，改走系统代理成功"); } catch { } return got; }
            }
            return -1;
        }

        /// <summary>
        /// 8 路跨节点并发下载：把大 VPK 切成 8 段，分散到多个未阻断的 Akamai 边缘节点并发拉取（单段失败自动换节点重试一次）。
        /// </summary>
        /// <summary>
        /// 多段跨节点并发下载：把大文件切成 4~16 段分散到不同 Akamai 边缘节点拉。
        /// 每段都能断点续传：某段被速度看门狗掐掉只重下这一段剩下的一截，不再整段清零。
        /// 返回 &gt;0 = 成功字节数；0 = 不适合并发（交给单连接）；-1 = 并发过且下了分片但最终没成（分片保留）。
        /// </summary>
        static long TryMultiParts(string url, string ua, int timeoutMs, string partPath, long totalHint, Action<long, long> progress, DlCtl ctl, out long partialBytes)
        {
            partialBytes = 0;
            Uri u = null;
            try { u = new Uri(url); } catch { return 0; }
            if (u == null || string.IsNullOrEmpty(partPath)) return 0;
            long total = totalHint;
            if (!ProbeRange(url, ua, u, out total) || total < 2L * 1024 * 1024) return 0;

            // 并发条数：4/8/16。但**分片切成两倍于并发条数的小块**，用队列轮流取：
            // 固定切 N 大段有个天然毛病 —— 快下完时只剩最后一段在跑，总速度掉回单连接水平，
            // 那一段哪怕正常速度也显得"蜗牛爬"（用户 2026-11-23 反馈「最后一段很慢」）。
            // 小块排队时，谁先下完谁接下一块，连接一直排到队尾，最后阶段还是多条线在跑。
            // 2026-11-23 离线基准：单连接被限速时总速度近似按连接数叠加（4/8/16 条 → 10.7/5.3/2.6 秒），
            // 正常网络下 8→24 条也快 3 倍；32 条受线程与系统连接上限影响反而变慢，甜点区 16~24。
            int parts = total >= 64L * 1024 * 1024 ? 24 : (total >= 16L * 1024 * 1024 ? 16 : 4);
            int units = total >= 16L * 1024 * 1024 ? parts * 2 : parts;
            long chunk = total / units;
            if (chunk < 512L * 1024 && units > 4)            // 别切得太碎：每块至少 512KB
            {
                units = Math.Max(4, (int)(total / (512L * 1024)));
                chunk = total / units;
            }
            string[] files = new string[units];
            long[] froms = new long[units];
            long[] tos = new long[units];
            // 分片放系统临时目录：以前直接写在 addons 旁边（xxx.vpk.p0…p7），
            // 下到一半强退就留在游戏目录里一堆文件（用户 2026-11-22 反馈）。
            string tmpDir = TempPartDir(partPath);
            // 上次留下的分片不删：点「继续 / 重试」要接着下（用户 2026-11-23 反馈「卡一下就从头重下」）
            try { Directory.CreateDirectory(tmpDir); } catch { return 0; }
            for (int i = 0; i < units; i++)
            {
                files[i] = Path.Combine(tmpDir, "p" + i);
                froms[i] = i * chunk;
                tos[i] = (i == units - 1) ? (total - 1) : (froms[i] + chunk - 1);
            }

            // 有单连接留下的 .part 就别浪费：把已经下下来的那段字节切进分片文件，接着并发下。
            // （以前这里直接交给单连接，结果「接着下」的大文件变成单线程 —— 用户 2026-11-23 要求改掉）
            try { SeedFromPart(partPath, files, froms, tos, units); } catch { }
            // 分片也走赛马：先量一遍线路，快的前置域名优先分到分片
            try { RaceRoutes(url, ua, u.Host, total); } catch { }

            long done = SumParts(files, froms, tos, units);
            partialBytes = done;
            Action<long> report = delegate(long delta)
            {
                long now = System.Threading.Interlocked.Add(ref done, delta);
                if (progress != null) { try { progress(now, total); } catch { } }
            };
            if (done > 0 && progress != null) { try { progress(done, total); } catch { } }   // 续传：进度条从断点起步

            // 每轮重新排队把没下完的块扫一遍；换节点重试最多三轮，还是不行就把分片留着等「继续」
            for (int round = 0; round < 3; round++)
            {
                if (ctl != null) ctl.Tick();
                string[] errs = new string[units];
                DlAbort aborted = null;
                int next = 0;
                System.Threading.Thread[] ths = new System.Threading.Thread[parts];
                for (int w = 0; w < parts; w++)
                {
                    ths[w] = new System.Threading.Thread(delegate()
                    {
                        while (true)
                        {
                            int idx = System.Threading.Interlocked.Increment(ref next) - 1;
                            if (idx >= units) return;
                            if (aborted != null) return;
                            long span = tos[idx] - froms[idx] + 1;
                            if (LenOf(files[idx]) == span) continue;      // 这块已经齐了
                            try
                            {
                                errs[idx] = GetRangeWithRetry(url, ua, timeoutMs, files[idx], froms[idx], tos[idx], u.Host, idx, report, ctl);
                                if (errs[idx] != null) { /* 这块失败：本轮先放着，下轮再排 */ }
                            }
                            catch (DlAbort ab) { if (aborted == null) aborted = ab; return; }
                        }
                    });
                    ths[w].IsBackground = true;
                    ths[w].Start();
                }
                for (int w = 0; w < parts; w++) ths[w].Join();
                if (aborted != null) throw aborted;                  // 线程都停了才往外抛，别让半截线程接着写

                bool ok = true;
                for (int i = 0; i < units; i++)
                {
                    if (LenOf(files[i]) != (tos[i] - froms[i] + 1)) { ok = false; break; }
                }
                if (ok)
                {
                    try
                    {
                        using (FileStream fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072))
                        {
                            for (int i = 0; i < units; i++)
                                using (FileStream si = File.OpenRead(files[i])) si.CopyTo(fs, 131072);
                        }
                        if (LenOf(partPath) != total) { DelIfExists(partPath); return -1; }
                        try { Directory.Delete(tmpDir, true); } catch { }
                        return total;
                    }
                    catch
                    {
                        DelIfExists(partPath);
                        return -1;
                    }
                }
                partialBytes = SumParts(files, froms, tos, units);
                if (partialBytes <= 0) return 0;                     // 一点没下来：交给单连接阶梯试别的路
                System.Threading.Thread.Sleep(400);
            }
            partialBytes = SumParts(files, froms, tos, units);
            return -1;
        }

        /// <summary>把单连接断点 .part 里已有的字节按段切进分片文件（只补空段，不覆盖已有进度）。</summary>
        static void SeedFromPart(string partPath, string[] files, long[] froms, long[] tos, int segs)
        {
            if (!File.Exists(partPath)) return;
            long have = LenOf(partPath);
            if (have <= 0) return;
            byte[] buf = new byte[131072];
            using (FileStream src = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                for (int i = 0; i < segs; i++)
                {
                    long start = froms[i];
                    if (start >= have) break;
                    long span = tos[i] - start + 1;
                    long want = Math.Min(span, have - start);
                    long already = LenOf(files[i]);
                    if (already < 0) already = 0;
                    if (already >= want) continue;               // 这段已经有更多进度，别覆盖
                    using (FileStream dst = new FileStream(files[i], FileMode.Append, FileAccess.Write, FileShare.None, 131072))
                    {
                        src.Position = start + already;
                        long left = want - already;
                        while (left > 0)
                        {
                            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) break;
                            dst.Write(buf, 0, n);
                            left -= n;
                        }
                    }
                }
            }
        }

        /// <summary>各分片已下字节合计（超过本段长度的按本段算，防半截垃圾撑大进度）。</summary>
        static long SumParts(string[] files, long[] froms, long[] tos, int segs)
        {
            long sum = 0;
            for (int i = 0; i < segs; i++)
            {
                long have = LenOf(files[i]);
                if (have <= 0) continue;
                long span = tos[i] - froms[i] + 1;
                sum += have > span ? span : have;
            }
            return sum;
        }

        /// <summary>Range 探针：优先用未阻断的 Akamai 前置域名探 1 字节，确认支持 206 + Content-Range 并拿到精确总字节数。</summary>
        static bool ProbeRange(string url, string ua, Uri u, out long total)
        {
            total = 0;
            IWebProxy px = PreferredProxy(u.Host);
            string[] fronts = CanFrontHost(u.Host) ? new string[] { FrontDomain, "steamusercontent-a.akamaihd.net", null } : new string[] { null };
            foreach (string front in fronts)
            {
                try
                {
                    string target = url;
                    if (front != null) { UriBuilder ub = new UriBuilder(u); ub.Host = front; target = ub.Uri.AbsoluteUri; }
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(target);
                    if (front != null) req.Host = u.Host;
                    req.UserAgent = ua;
                    req.Timeout = 10000; req.ReadWriteTimeout = 10000;
                    try { req.Proxy = px; } catch { }
                    req.AddRange(0, 0);
                    using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                    {
                        if (r.StatusCode != HttpStatusCode.PartialContent) continue;
                        string cr = r.Headers["Content-Range"];
                        int slash = cr == null ? -1 : cr.LastIndexOf('/');
                        if (slash < 0 || !long.TryParse(cr.Substring(slash + 1), out total) || total <= 0) continue;
                        using (Stream s = r.GetResponseStream()) { byte[] one = new byte[1]; if (s != null) s.Read(one, 0, 1); }
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        static string GetRangeWithRetry(string url, string ua, int timeoutMs, string path, long from, long to, string host, int slot, Action<long> report, DlCtl ctl)
        {
            bool canFront = CanFrontHost(host);
            long span = to - from + 1;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (ctl != null) ctl.Tick();
                long have = LenOf(path);
                if (have < 0) have = 0;
                if (have > span) { DelIfExists(path); have = 0; }    // 比本段还大：旧垃圾，重来
                if (have == span) return null;
                string[] order = FrontOrder(host);
                string front = canFront ? order[(slot + attempt) % order.Length] : null;
                // HTTP 80 在 Akamai 上免 TLS 握手且不被 SNI 掐，大文件分片优先走 HTTP + 跨节点前置，失败再退 HTTPS
                bool useHttp = canFront && attempt < 2;
                string err = GetRange(url, ua, timeoutMs, path, from + have, to, host, front, useHttp, report, ctl);
                if (err == null && LenOf(path) == span) return null;
                // 已经下下来的字节不删：下一次从断点接着要。
                // 以前这里 DelIfExists(path) + report(-已下) 把整段清零重下，用户看到的就是「进度跳回去重新开始」。
            }
            return "分段下载重试失败";
        }

        /// <summary>下某一段到独立文件（可续）：path 里已有的字节算已下，只请求 from..to 还没拿到的部分。</summary>
        static string GetRange(string url, string ua, int timeoutMs, string path, long from, long to, string host, string frontHost, bool useHttp, Action<long> report, DlCtl ctl)
        {
            try
            {
                if (to < from) return null;
                Uri u = new Uri(url);
                UriBuilder ub = new UriBuilder(u);
                if (frontHost != null) ub.Host = frontHost;
                if (useHttp) { ub.Scheme = "http"; ub.Port = 80; }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ub.Uri.AbsoluteUri);
                if (frontHost != null) req.Host = host;
                req.UserAgent = ua;
                req.Timeout = Math.Min(timeoutMs, 20000);
                req.ReadWriteTimeout = 15000;          // 单次读的等待上限缩短：真停摆时 15 秒就能发现并换节点
                try { req.Proxy = PreferredProxy(host); } catch { }
                req.AddRange(from, to);
                // 整段截止时间 + 停摆判定：Akamai 偶尔会滴流（每秒几字节），只靠读超时会永远下不完，
                // 表现就是进度条卡在一个百分比再也不动（用户 2026-11-22 反馈）。
                long bytesSpan = to - from + 1;
                // 单段预算：按 120KB/s 估，最少 90 秒。Akamai 某些节点跑到一半会限速到几 KB/s，
                // 那种「看着在动、永远下不完」比直接失败更难受，所以要有速度看门狗主动掐掉换节点。
                long budget = Math.Max(90000L, bytesSpan / 120000L * 1000L);
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                {
                    if (r.StatusCode != HttpStatusCode.PartialContent) return "服务器没按 Range 返回（" + r.StatusCode + "）";
                    using (Stream src = r.GetResponseStream())
                    using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 131072))
                    {
                        byte[] buf = new byte[131072];
                        int n;
                        Stopwatch clock = Stopwatch.StartNew();
                        long sampleAt = 0, sampleBytes = 0, totalGot = 0;
                        int slowSamples = 0;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                            report(n);
                            totalGot += n;
                            if (ctl != null) ctl.Tick();      // 暂停 / 取消：立刻停手，已经写进文件的字节留着续传
                            long now = clock.ElapsedMilliseconds;
                            if (now - sampleAt >= 3000)
                            {
                                long spd = (totalGot - sampleBytes) * 1000L / Math.Max(1L, now - sampleAt);
                                if (spd < 12000) slowSamples++; else slowSamples = 0;
                                sampleAt = now;
                                sampleBytes = totalGot;
                                if (slowSamples >= 4) throw new IOException("分段太慢（连续 12 秒低于 12KB/s），换节点重下");
                            }
                            if (now > budget) throw new IOException("分段超时（超过 " + (budget / 1000) + " 秒），换节点重下");
                        }
                    }
                }
                return null;
            }
            catch (DlAbort) { throw; }                    // 暂停 / 取消要往上传，不能当成一次普通失败吞掉
            catch (Exception ex) { return ex.Message; }
        }

        static void DelIfExists(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>取消下载：删掉这件没下完的临时东西（单连接的 .part 和分段用的 %TEMP% 分片目录）。</summary>
        internal static void CleanPartial(string dest)
        {
            if (string.IsNullOrEmpty(dest)) return;
            string part = dest + ".part";
            DelIfExists(part);
            try
            {
                string d = TempPartDir(part);
                if (Directory.Exists(d)) Directory.Delete(d, true);
            }
            catch { }
        }

        static long LenOf(string p)
        {
            try { return new FileInfo(p).Length; } catch { return -1; }
        }

        static long TryDownload(string url, string ua, int timeoutMs, string partPath, long totalHint, Action<long, long> progress, bool allowPin, IWebProxy proxy, string frontHost, bool useHttp, DlCtl ctl)
        {
            Uri u = null;
            try { u = new Uri(url); } catch { }
            if (ctl != null) ctl.Tick();
            try
            {
                string pinnedUrl = null;
                if (!string.IsNullOrEmpty(frontHost) && u != null)
                {
                    UriBuilder ub = new UriBuilder(u);
                    ub.Host = frontHost;
                    if (useHttp) { ub.Scheme = "http"; ub.Port = 80; }
                    pinnedUrl = ub.Uri.AbsoluteUri;
                }
                else if (allowPin && u != null)
                {
                    string ip = Pick(u.Host);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        UriBuilder ub = new UriBuilder(u);
                        ub.Host = ip;
                        pinnedUrl = ub.Uri.AbsoluteUri;
                    }
                }
                long already = 0;
                try { if (File.Exists(partPath)) already = new FileInfo(partPath).Length; } catch { already = 0; }
                if (totalHint > 0 && already >= totalHint) already = 0;   // 比目标还大：是旧垃圾，从头来

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(pinnedUrl ?? url);
                if (pinnedUrl != null) req.Host = u.Host;
                req.UserAgent = ua;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = 60000;
                req.AllowAutoRedirect = true;
                req.KeepAlive = true;
                try { req.Proxy = proxy; } catch { }
                if (already > 0) { try { req.AddRange(already); } catch { } }

                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                {
                    // 206 = 服务器按 Range 续上了；200 = 它不理 Range，只能从头写
                    bool append = r.StatusCode == HttpStatusCode.PartialContent && already > 0;
                    long baseLen = append ? already : 0;
                    long total = r.ContentLength > 0 ? r.ContentLength + baseLen : totalHint;
                    using (Stream src = r.GetResponseStream())
                    using (FileStream fs = new FileStream(partPath, append ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, 131072))
                    {
                        byte[] buf = new byte[131072];
                        long got = baseLen;
                        int n;
                        Stopwatch dlClock = Stopwatch.StartNew();
                        long lastByteAt = 0, capMs = Math.Max(180000L, (total > 0 ? total : 0) / 30000L * 1000L);
                        long sampleAt = 0, sampleBytes = got;
                        int slowSamples = 0;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                            got += n;
                            lastByteAt = dlClock.ElapsedMilliseconds;
                            if (progress != null) progress(got, total);
                            if (ctl != null) ctl.Tick();      // 暂停 / 取消：立刻停手，part 留着续传
                            // 慢速换线：能下但慢得离谱（连续 10 秒低于 50KB/s）就别干耗到总时限，
                            // 把 part 留着回上层重新赛马，换到更快的线路接着下（用户 2026-11-23 要求）。
                            long t = dlClock.ElapsedMilliseconds;
                            if (t - sampleAt >= 5000)
                            {
                                long spd = (got - sampleBytes) * 1000L / Math.Max(1L, t - sampleAt);
                                if (spd < 50000) slowSamples++; else slowSamples = 0;
                                sampleAt = t;
                                sampleBytes = got;
                                if (slowSamples >= (total > 0 ? 2 : 4))
                                {
                                    // 但别把"慢线但快下完了"的小文件硬掐掉：按当前速度估算还要多久，
                                    // 剩不到 150 秒就老实下完（换线重来反而更慢）。
                                    long remain = total > 0 ? Math.Max(0, total - got) : 0;
                                    long etaSec = total > 0 ? remain / Math.Max(1L, spd) : 999999;
                                    if (etaSec >= 150)
                                    {
                                        fs.Flush();
                                        return SlowSignal;
                                    }
                                    slowSamples = 0;
                                }
                            }
                            if (t > capMs) throw new IOException("下载超时（超过 " + (capMs / 1000) + " 秒）");
                        }
                        if (dlClock.ElapsedMilliseconds - lastByteAt > 25000)
                            throw new IOException("下载停摆（25 秒没有新数据）");
                        fs.Flush();
                        return got;
                    }
                }
            }
            catch (DlAbort) { throw; }                    // 暂停 / 取消要往上传，不能当成普通失败吞掉
            catch (WebException wex)
            {
                // 416 = 断点位置越界（文件被换过/服务器不认）：把 part 清掉，下次从头来
                HttpWebResponse bad = wex.Response as HttpWebResponse;
                if (bad != null && bad.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                }
                if (allowPin && u != null) DropPin(u.Host);
                return -1;      // 其他失败留着 part，下次续传
            }
            catch
            {
                if (allowPin && u != null) DropPin(u.Host);
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
            else
            {
                // 「优选 IP 失败」这句不带明细等于没说：用户反馈时唯一能看的就是这一行
                string d; lock (gate) { d = probeNote ?? ""; }
                try { Core.Log("WARN", "工坊优选 IP 全部失败，改用域名: " + host + (d.Length > 0 ? " ← " + d : "")); } catch { }
            }
            return best;
        }

        static void DropPin(string host)
        {
            lock (gate) { pinned.Remove(host); skip[host] = true; }
            try { Core.Log("WARN", "工坊优选 IP 用不上（请求失败），改用域名: " + host); } catch { }
        }

        /// <summary>候选 IP 各连一次 443，取毫秒数最小的（最多 4 个，单个上限 700ms）。</summary>
        static string Probe(string host)
        {
            System.Net.IPAddress[] ips;
            try { ips = Dns.GetHostAddresses(host); }
            catch (Exception ex) { lock (gate) { probeNote = "域名解析失败: " + ex.Message; } return null; }
            if (ips == null || ips.Length == 0) return null;
            int take = Math.Min(ips.Length, 4);
            int[] ms = new int[take];
            for (int i = 0; i < take; i++) ms[i] = -1;
            System.Threading.Tasks.Parallel.For(0, take, delegate(int i) { ms[i] = TcpProbe(ips[i].ToString(), 443, 700); });
            int best = -1;
            StringBuilder d = new StringBuilder();
            for (int i = 0; i < take; i++)
            {
                if (ms[i] >= 0 && (best < 0 || ms[i] < ms[best])) best = i;
                if (i > 0) d.Append(", ");
                d.Append(ips[i]).Append(ms[i] >= 0 ? "(" + ms[i] + "ms)" : "(700ms 无响应)");
            }
            lock (gate) { probeNote = d.ToString(); }
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

        /// <summary>
        /// 失败的「翻译」：把所有异常收敛成一句人话（连接被重置 / 请求超时 / 连不上服务器…）。
        /// 之前这里全被 catch 吞掉，界面只能写「连不上」，用户和自己的诊断都没抓手。
        /// </summary>
        static void NoteFail(Exception ex)
        {
            string why = "网络错误";
            WebException we = ex as WebException;
            if (we != null)
            {
                switch (we.Status)
                {
                    case WebExceptionStatus.Timeout: why = "请求超时"; break;
                    case WebExceptionStatus.NameResolutionFailure: why = "域名解析失败"; break;
                    case WebExceptionStatus.ConnectFailure: why = "连不上服务器"; break;
                    case WebExceptionStatus.TrustFailure: why = "证书校验失败"; break;
                    case WebExceptionStatus.SecureChannelFailure: why = "连接被重置"; break;
                    default:
                        // 被 RST 时 .NET 常报「基础连接已经关闭：接收时发生错误」，归到「连接被重置」
                        string m = we.Message ?? "";
                        if (m.IndexOf("关闭", StringComparison.Ordinal) >= 0
                            || m.IndexOf("closed", StringComparison.OrdinalIgnoreCase) >= 0) why = "连接被重置";
                        break;
                }
            }
            else if (ex is IOException) why = "连接被重置";
            lock (gate) { lastWhy = why; }
        }

        /// <summary>最近一次失败是什么（空串 = 没失败过，或者刚成功过）。</summary>
        public static string LastWhy { get { lock (gate) { return lastWhy ?? ""; } } }

        /// <summary>给界面的那句话：为什么连不上 + 一句怎么办。调用方外面再包 Lang.S。</summary>
        internal static string LastFailHint()
        {
            string why = LastWhy;
            if (why.Length == 0) return "工坊网页连不上";
            // hosts 指到本机、但本机没人监听：这是一条死配置，不说清楚用户永远查不出来
            if (TcpProbe("127.0.0.1", 443, 300) < 0
                && (HostsPointsToLocalhost("steamcommunity.com") || HostsPointsToLocalhost("api.steampowered.com")))
                return "本机 hosts 把 Steam 域名指到了 127.0.0.1，但没有工具在监听：打开对应加速工具，或删掉 hosts 里那一行";
            if (why == "连接被重置")
            {
                string acc = AcceleratorName();
                if (acc != null && SystemProxy() == null)
                    return "连不上 Steam：检测到" + acc + "在跑，但它多半只加速游戏本体 —— 打开它的「Steam 社区」加速再试";
                return "连接被重置（可能需要开 Steam 社区加速）";
            }
            if (why == "请求超时") return "请求超时（这条线路到 Steam 不通）";
            if (why == "连不上服务器") return "连不上服务器（DNS 给的 IP 都不通）";
            return "连不上（" + why + "）";
        }

        /// <summary>本机有没有装加速器（只认进程名，纯提示用，不参与连接）。</summary>
        static string AcceleratorName()
        {
            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                {
                    string n;
                    try { n = p.ProcessName.ToLowerInvariant(); } catch { continue; }
                    if (n.StartsWith("uu_")) return "UU";
                    if (n.StartsWith("leigod")) return "雷神";
                    if (n.StartsWith("xunyou") || n.StartsWith("qyclient")) return "迅游";
                }
            }
            catch { }
            return null;
        }

        /// <summary>hosts 里是否把这个域名指到了本机 —— 指了却没人监听，就是一条会误导人的死配置。</summary>
        static bool HostsPointsToLocalhost(string host)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
                if (!File.Exists(path)) return false;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash);
                    string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    bool hostHit = false, local = false;
                    foreach (string part in parts)
                    {
                        if (string.Equals(part, host, StringComparison.OrdinalIgnoreCase)) hostHit = true;
                        else if (part == "127.0.0.1" || part == "::1") local = true;
                    }
                    if (hostHit && local) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>失败日志：封面会并发抓好几张，全挂了别刷屏 —— 5 秒一条，够追问题。</summary>
        static void LogOnce(string msg)
        {
            lock (gate)
            {
                if ((DateTime.Now - lastLogAt).TotalSeconds < 5) return;
                lastLogAt = DateTime.Now;
            }
            try { Core.Log("WARN", msg); } catch { }
        }

        // ---------------- 阶梯重试 / 离线态 / 系统代理 / 公共 DNS ----------------

        const int TierMs = 2500;                    // 单档上限：三档合计 7.5 秒就要收手，别让人干等
        static DateTime offlineUntil = DateTime.MinValue;

        public static bool IsOffline { get { lock (gate) { return DateTime.Now < offlineUntil; } } }

        /// <summary>用户点「重试」：立刻解除冷却，下一轮从第一档重来。</summary>
        public static void Retry()
        {
            lock (gate)
            {
                offlineUntil = DateTime.MinValue;
                apiDeadUntil = DateTime.MinValue;      // 用户主动点重试：API 的冷却也一起解开
                steamFailAt = DateTime.MinValue;
            }
        }

        /// <summary>请求成功。只有 Steam 自己的域名成功才能解除离线态 ——
        /// GameBanana 的图能下，不代表工坊复活，以前这里一律清，导致每次点位置又去敲 Steam。</summary>
        static void MarkOnline(string host)
        {
            lock (gate)
            {
                lastWhy = null;
                if (IsSteamHost(host)) offlineUntil = DateTime.MinValue;
            }
        }

        // API 单独一份冷却：api.steampowered.com 和网页是两条线，可能一条死一条活
        static DateTime steamFailAt = DateTime.MinValue;
        /// <summary>最近 60 秒内 Steam 刚失败过：这一轮就别再等它了，直接上来源。</summary>
        internal static bool SteamJustFailed { get { lock (gate) { return (DateTime.Now - steamFailAt).TotalSeconds < 60; } } }

        static DateTime apiDeadUntil = DateTime.MinValue;
        internal static bool ApiOffline { get { lock (gate) { return DateTime.Now < apiDeadUntil; } } }
        internal static void MarkApiDead() { lock (gate) { apiDeadUntil = DateTime.Now.AddSeconds(90); } }   // 只歇 90 秒：这条线时通时断，锁太久反而像坏了
        internal static void MarkApiAlive() { lock (gate) { apiDeadUntil = DateTime.MinValue; } }

        static void GoOffline()
        {
            lock (gate)
            {
                if (DateTime.Now < offlineUntil) return;
                offlineUntil = DateTime.Now.AddSeconds(90);
            }
            lock (gate) { steamFailAt = DateTime.Now; }
            LogOnce("工坊网页主站暂不可达（90 秒后自动重探，有 API key 时不影响 API 搜索）");
        }

        static string HostOf(string url)
        {
            try { return new Uri(url).Host; } catch { return null; }
        }

        static IWebProxy systemProxy;
        static string systemProxyAddr = "";
        static bool systemProxyRead;
        static DateTime systemProxyAt;

        /// <summary>
        /// 本机系统代理（Clash / v2rayN / 加速器设的那种）。只读注册表，不触发 IE 自动探测那几秒卡顿。
        /// 缓存 15 秒：用户中途开/关加速器，15 秒内就能感知并自动重测最快路线。
        /// </summary>
        static IWebProxy SystemProxy()
        {
            lock (gate)
            {
                if (systemProxyRead && (DateTime.Now - systemProxyAt).TotalSeconds < 15) return systemProxy;
            }
            IWebProxy px = null;
            string addr = "";
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k != null && Convert.ToInt32(k.GetValue("ProxyEnable") ?? 0) != 0)
                    {
                        string srv = ((k.GetValue("ProxyServer") as string) ?? "").Trim();
                        addr = PickProxyAddress(srv);
                        if (addr.Length > 0)
                        {
                            if (addr.IndexOf("://", StringComparison.Ordinal) < 0) addr = "http://" + addr;
                            px = new WebProxy(addr);
                        }
                    }
                }
            }
            catch { }
            bool changed = false;
            lock (gate)
            {
                if (systemProxyRead && !string.Equals(systemProxyAddr, addr, StringComparison.OrdinalIgnoreCase))
                    changed = true;
                systemProxy = px;
                systemProxyAddr = addr;
                systemProxyRead = true;
                systemProxyAt = DateTime.Now;
            }
            if (changed) KickReprobe();
            return px;
        }

        /// <summary>注册表里可能是 "1.2.3.4:7890"，也可能是 "http=...;https=..." —— 优先取 https 那段。</summary>
        static string PickProxyAddress(string srv)
        {
            if (srv.Length == 0) return "";
            if (srv.IndexOf('=') < 0) return srv;
            string https = "", http = "";
            foreach (string part in srv.Split(';'))
            {
                int at = part.IndexOf('=');
                if (at <= 0) continue;
                string key = part.Substring(0, at).Trim().ToLowerInvariant();
                string val = part.Substring(at + 1).Trim();
                if (key == "https") https = val;
                else if (key == "http") http = val;
            }
            return https.Length > 0 ? https : http;
        }

        static readonly string[] PublicDns = new string[] { "223.5.5.5", "119.29.29.29" };

        /// <summary>
        /// 换公共 DNS 重新解析，把能连的那个 IP 写进 pinned —— 系统 DNS 被投毒时这是唯一干净的来源。
        /// 拿到 IP 还要证明它能连（探 443），否则等于把「优选 IP」自家人为拉黑。
        /// </summary>
        static bool ResolveByPublicDns(string host)
        {
            foreach (string dns in PublicDns)
            {
                List<IPAddress> ips = DnsLookupA(host, dns);
                if (ips == null || ips.Count == 0) continue;
                int bestMs = -1; string bestIp = null;
                foreach (IPAddress ip in ips)
                {
                    int ms = TcpProbe(ip.ToString(), 443, 1200);
                    if (ms >= 0 && (bestMs < 0 || ms < bestMs)) { bestMs = ms; bestIp = ip.ToString(); }
                }
                if (bestIp == null) continue;
                lock (gate) { pinned[host] = bestIp; skip.Remove(host); }
                return true;
            }
            return false;
        }

        static List<IPAddress> DnsLookupA(string host, string server)
        {
            int id = Environment.TickCount & 0xFFFF;
            byte[] q = DnsQuery(host, id);
            if (q == null) return null;
            byte[] resp = DnsExchangeUdp(server, q);
            // 截断位（TC）：换 TCP 重问一次
            if (resp != null && resp.Length > 2 && (resp[2] & 0x02) != 0) resp = DnsExchangeTcp(server, q);
            return DnsParseA(resp, id);
        }

        static byte[] DnsQuery(string host, int id)
        {
            try
            {
                List<byte> b = new List<byte>(64);
                b.Add((byte)(id >> 8)); b.Add((byte)(id & 0xFF));
                b.Add(0x01); b.Add(0x00);                   // 标准查询，要递归
                b.Add(0); b.Add(1);                         // QDCOUNT
                for (int i = 0; i < 6; i++) b.Add(0);       // AN / NS / AR
                foreach (string label in host.Split('.'))
                {
                    byte[] lb = Encoding.ASCII.GetBytes(label);
                    if (lb.Length == 0 || lb.Length > 63) return null;
                    b.Add((byte)lb.Length); b.AddRange(lb);
                }
                b.Add(0);
                b.Add(0); b.Add(1);                         // Type A
                b.Add(0); b.Add(1);                         // Class IN
                return b.ToArray();
            }
            catch { return null; }
        }

        static byte[] DnsExchangeUdp(string server, byte[] q)
        {
            try
            {
                using (System.Net.Sockets.UdpClient u = new System.Net.Sockets.UdpClient())
                {
                    u.Client.ReceiveTimeout = 1500;
                    IPEndPoint ep = new IPEndPoint(IPAddress.Parse(server), 53);
                    u.Send(q, q.Length, ep);
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    return u.Receive(ref from);
                }
            }
            catch { return null; }
        }

        static byte[] DnsExchangeTcp(string server, byte[] q)
        {
            try
            {
                using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                {
                    c.ReceiveTimeout = 1500;
                    if (!c.ConnectAsync(server, 53).Wait(1500)) return null;
                    Stream s = c.GetStream();
                    byte[] packet = new byte[q.Length + 2];
                    packet[0] = (byte)(q.Length >> 8); packet[1] = (byte)(q.Length & 0xFF);
                    Buffer.BlockCopy(q, 0, packet, 2, q.Length);
                    s.Write(packet, 0, packet.Length);
                    byte[] head = new byte[2];
                    if (!ReadFull(s, head, 2)) return null;
                    int len = (head[0] << 8) | head[1];
                    if (len <= 0 || len > 4096) return null;
                    byte[] body = new byte[len];
                    return ReadFull(s, body, len) ? body : null;
                }
            }
            catch { return null; }
        }

        static bool ReadFull(Stream s, byte[] buf, int n)
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

        static List<IPAddress> DnsParseA(byte[] r, int id)
        {
            if (r == null || r.Length < 12) return null;
            if (((r[0] << 8) | r[1]) != id) return null;
            int qd = (r[4] << 8) | r[5];
            int an = (r[6] << 8) | r[7];
            if (an <= 0) return null;
            int at = 12;
            for (int i = 0; i < qd; i++)
            {
                at = SkipName(r, at);
                if (at < 0 || at + 4 > r.Length) return null;
                at += 4;
            }
            List<IPAddress> outp = new List<IPAddress>();
            for (int i = 0; i < an; i++)
            {
                at = SkipName(r, at);
                if (at < 0 || at + 10 > r.Length) break;
                int type = (r[at] << 8) | r[at + 1];
                int rdlen = (r[at + 8] << 8) | r[at + 9];
                at += 10;
                if (at + rdlen > r.Length) break;
                if (type == 1 && rdlen == 4)
                    outp.Add(new IPAddress(new byte[] { r[at], r[at + 1], r[at + 2], r[at + 3] }));
                at += rdlen;
            }
            return outp;
        }

        static int SkipName(byte[] r, int at)
        {
            while (at >= 0 && at < r.Length)
            {
                int len = r[at];
                if (len == 0) return at + 1;
                if ((len & 0xC0) == 0xC0) return at + 2;     // 压缩指针：名字到此结束
                at += len + 1;
            }
            return -1;
        }

        /// <summary>「复制诊断」：傻瓜用户不会去 %APPDATA% 找日志，让他粘一段发过来就行。</summary>
        public static string Diagnostics()
        {
            StringBuilder b = new StringBuilder();
            b.Append("工坊连不上（").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append("）\r\n");
            b.Append("版本 ").Append(Version()).Append("\r\n");
            b.Append("原因：").Append(LastFailHint()).Append("\r\n");
            b.Append("系统代理：").Append(SystemProxy() != null ? "有" : "无").Append("\r\n");
            string rr;
            lock (gate)
            {
                rr = "主站=" + (steamRoute == NetRoute.Unknown ? "不通" : RouteName(steamRoute) + " " + steamRouteMs + "ms");
                if (apiRoute != NetRoute.Unknown) rr += "，API=" + RouteName(apiRoute) + " " + apiRouteMs + "ms";
            }
            b.Append("线路体检：").Append(rr).Append("\r\n");
            string d; lock (gate) { d = probeNote ?? ""; }
            if (d.Length > 0) b.Append("探针：").Append(d).Append("\r\n");
            string w; lock (gate) { w = lastWhy ?? ""; }
            if (w.Length > 0) b.Append("判定：").Append(w).Append("\r\n");
            b.Append("已试：直连 → 系统代理 → Akamai 前置（公共 DNS）");
            return b.ToString();
        }

        static string Version()
        {
            try { return typeof(WsNet).Assembly.GetName().Version.ToString(); } catch { return "?"; }
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
            // 带上失败原因：这句话会一路显示到推荐区/工坊窗口的空态上
            if (s == null) throw new WebException("工坊请求失败：" + WsNet.LastFailHint());
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
        // 搜索秒表：记录 API 与网页最近一次成功耗时（毫秒），谁快走谁；
        // 首次或超过 3 分钟没比过、或者上次领先方劣化（>1500ms）时，两条路并发赛马，谁先回来用谁。
        static int searchApiMs = -1;
        static int searchWebMs = -1;
        static DateTime searchRaceAt = DateTime.MinValue;

        internal static List<WsItem> SearchItems(string query, string sort, int page, string tag = null)
        {
            if (!Core.Cfg.SteamEnabled) throw new InvalidOperationException("Steam 来源已在「来源」里关闭");
            if (!HasApiKey) return SearchByWeb(query, sort, page, tag);

            bool webAlive = !WsNet.IsOffline && (!WsNet.SteamProbed || WsNet.SteamRouteAlive);
            bool apiAlive = !WsNet.ApiOffline;
            int aMs = searchApiMs, wMs = searchWebMs;
            bool needRace = webAlive && apiAlive
                && (aMs < 0 || wMs < 0
                    || (DateTime.Now - searchRaceAt).TotalMinutes >= 3
                    || Math.Min(aMs, wMs) > 1500);

            if (needRace)
            {
                List<WsItem> raced = RaceSearch(query, sort, page, tag);
                if (raced != null && raced.Count > 0) return raced;
            }

            // 已经有秒表结论：谁快先走谁，慢了（>1500ms）或失败立刻用另一条补救
            bool preferWeb = webAlive && (!apiAlive || (wMs >= 0 && (aMs < 0 || wMs + 150 < aMs)));
            if (preferWeb)
            {
                try
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    List<WsItem> viaWeb = SearchByWeb(query, sort, page, tag);
                    sw.Stop();
                    if (viaWeb != null && viaWeb.Count > 0)
                    {
                        searchWebMs = (int)sw.ElapsedMilliseconds;
                        return viaWeb;
                    }
                }
                catch { searchWebMs = 9999; }
            }

            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                List<WsItem> viaApi = SearchByApi(query, sort, page, tag);
                sw.Stop();
                if (viaApi != null && viaApi.Count > 0)
                {
                    searchApiMs = (int)sw.ElapsedMilliseconds;
                    WsNet.MarkApiAlive();
                    return viaApi;
                }
            }
            catch (Exception ex)
            {
                searchApiMs = 9999;
                WsNet.MarkApiDead();
                try { Core.Log("WARN", "工坊 API 失败，切回网页/备用源: " + ex.Message); } catch { }
            }

            if (!preferWeb && !WsNet.IsOffline)
            {
                Stopwatch sw = Stopwatch.StartNew();
                List<WsItem> viaWeb = SearchByWeb(query, sort, page, tag);
                sw.Stop();
                if (viaWeb != null && viaWeb.Count > 0) searchWebMs = (int)sw.ElapsedMilliseconds;
                return viaWeb;
            }
            throw new WebException("工坊请求失败：" + WsNet.LastFailHint());
        }

        /// <summary>API 与网页并发赛马：谁先拿到非空结果就立刻返回，后回来的顺手更新秒表。</summary>
        static List<WsItem> RaceSearch(string query, string sort, int page, string tag)
        {
            searchRaceAt = DateTime.Now;
            object box = new object();
            List<WsItem> winner = null;
            int done = 0;

            System.Threading.Tasks.Task.Run(delegate
            {
                Stopwatch sw = Stopwatch.StartNew();
                List<WsItem> r = null;
                try { r = SearchByApi(query, sort, page, tag); } catch { }
                sw.Stop();
                bool ok = r != null && r.Count > 0;
                if (ok) { searchApiMs = (int)sw.ElapsedMilliseconds; WsNet.MarkApiAlive(); }
                else searchApiMs = 9999;
                lock (box)
                {
                    if (ok && winner == null) winner = r;
                    done++;
                    System.Threading.Monitor.PulseAll(box);
                }
            });

            System.Threading.Tasks.Task.Run(delegate
            {
                Stopwatch sw = Stopwatch.StartNew();
                List<WsItem> r = null;
                try { r = SearchByWeb(query, sort, page, tag); } catch { }
                sw.Stop();
                bool ok = r != null && r.Count > 0;
                if (ok) searchWebMs = (int)sw.ElapsedMilliseconds;
                else searchWebMs = 9999;
                lock (box)
                {
                    if (ok && winner == null) winner = r;
                    done++;
                    System.Threading.Monitor.PulseAll(box);
                }
            });

            lock (box)
            {
                Stopwatch wait = Stopwatch.StartNew();
                while (winner == null && done < 2 && wait.ElapsedMilliseconds < 8000)
                    System.Threading.Monitor.Wait(box, 200);
                return winner;
            }
        }

        static List<WsItem> SearchByWeb(string query, string sort, int page, string tag)
        {
            StringBuilder u = new StringBuilder();
            u.Append("https://steamcommunity.com/workshop/browse/?appid=").Append(AppId);
            u.Append("&section=readytouseitems");
            if (!string.IsNullOrEmpty(sort)) u.Append("&browsesort=").Append(sort);
            if (!string.IsNullOrEmpty(query)) u.Append("&searchtext=").Append(Uri.EscapeDataString(query));
            // 标签过滤比文本准：地图只有 Campaigns 标签、特感只有 Special Infected 标签，光靠文本词搜不准
            if (!string.IsNullOrEmpty(tag)) u.Append("&requiredtags%5B%5D=").Append(Uri.EscapeDataString(tag));
            if (page > 1) u.Append("&p=").Append(page);
            string html = Get(u.ToString());
            List<WsItem> outp = ParseBrowseItems(html);
            if (outp.Count > 0) return outp;

            // 回退：老路径（抽 ID → 详情接口）
            List<string> ids = ParseBrowse(html);
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
                it.Preview = FixImageUrl(JStr(ob, "preview_url"));
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
            return PostUrl(ApiRoot + path, body, 20000);
        }

        static string PostUrl(string url, string body, int timeoutMs)
        {
            WsNet.Tune();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Uri u = null;
            try { u = new Uri(url); } catch { }
            string host = u == null ? null : u.Host;
            IWebProxy px = WsNet.PreferredProxy(host);
            bool frontFirst = WsNet.CanFrontHost(host) && px == null;
            // 依次尝试：HTTPS 前置 → 直连 → HTTP(80) 前置。
            // 实测这条线对 Akamai 的 TLS 握手时常被掐，但 80 端口的 POST 走前置域名能通（1.5 秒回 200）；
            // 这些 POST 的正文里只有 ID，不带 API key，所以走明文没有泄密风险。
            string[] targets = frontFirst
                ? new string[] { "steamuserimages-a.akamaihd.net", null, "http:steamusercontent-a.akamaihd.net" }
                : new string[] { null, "steamuserimages-a.akamaihd.net", "http:steamusercontent-a.akamaihd.net" };
            Exception last = null;
            foreach (string front in targets)
            {
                try
                {
                    string reqUrl = url;
                    bool plain = front != null && front.StartsWith("http:", StringComparison.Ordinal);
                    string realFront = plain ? front.Substring(5) : front;
                    if (realFront != null && u != null)
                    {
                        UriBuilder ub = new UriBuilder(u);
                        ub.Host = realFront;
                        if (plain) { ub.Scheme = "http"; ub.Port = 80; }
                        reqUrl = ub.Uri.AbsoluteUri;
                    }
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(reqUrl);
                    if (realFront != null && host != null) req.Host = host;
                    req.Method = "POST";
                    req.ContentType = "application/x-www-form-urlencoded";
                    req.UserAgent = Ua;
                    int cap = Math.Min(timeoutMs, 8000);
                    req.Timeout = cap;
                    req.ReadWriteTimeout = cap;
                    try { req.Proxy = realFront == null ? px : null; } catch { }
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    req.ContentLength = data.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                    using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch (Exception ex) { last = ex; }
            }
            if (last != null) throw last;
            return null;
        }

        // ---------------- 工坊 API 路线（填了 key 才走，绕开被墙的网页） ----------------

        const string PubRoot = "https://api.steampowered.com/IPublishedFileService/";

        /// <summary>用户自己填的 Steam Web API key；空 = 不用 API 路线。</summary>
        internal static string ApiKey { get { return (Core.Cfg.WsApiKey ?? "").Trim(); } }
        internal static bool HasApiKey { get { return ApiKey.Length >= 20; } }

        /// <summary>排序 → QueryFiles 的 query_type 枚举。有搜索词时按文本搜索，否则按热度/订阅。</summary>
        static int ApiQueryType(string sort, string query)
        {
            if (!string.IsNullOrEmpty(query)) return 11;                      // RankedByTextSearch
            if (string.Equals(sort, "trend", StringComparison.OrdinalIgnoreCase)) return 3;   // RankedByTrend
            if (string.Equals(sort, "mostsubscribed", StringComparison.OrdinalIgnoreCase)) return 12;
            if (string.Equals(sort, "mostrecent", StringComparison.OrdinalIgnoreCase)) return 1;
            return 3;
        }

        /// <summary>
        /// 用 QueryFiles 拿一页结果。不需要 steamcommunity，只要 key。
        /// 返回 null 表示这次 API 没成功（调用方退回网页路由）。
        /// </summary>
        // API 结果缓存（10 分钟）：同一个位置来回切不该每次都打一遍接口
        static readonly Dictionary<string, List<WsItem>> apiCache = new Dictionary<string, List<WsItem>>(StringComparer.Ordinal);
        static readonly Dictionary<string, DateTime> apiCacheAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        static readonly object apiGate = new object();

        static List<WsItem> SearchByApi(string query, string sort, int page, string tag)
        {
            string ck = (query ?? "") + "|" + (sort ?? "") + "|" + page + "|" + (tag ?? "");
            lock (apiGate)
            {
                DateTime at; List<WsItem> hit;
                if (apiCacheAt.TryGetValue(ck, out at) && (DateTime.Now - at).TotalMinutes < 10 && apiCache.TryGetValue(ck, out hit))
                    return hit;
            }
            // QueryFiles 只认 GET（POST 会 405）；参数一律塞进 query string
            StringBuilder u = new StringBuilder(PubRoot + "QueryFiles/v1/?format=json");
            u.Append("&key=").Append(ApiKey);
            u.Append("&appid=").Append(AppId);
            u.Append("&page=").Append(page);
            u.Append("&numperpage=").Append(Math.Min(100, Math.Max(1, 30)));
            u.Append("&return_short_description=true");
            u.Append("&language=schinese");                 // 作者填过中文的，直接拿中文
            u.Append("&cache_max_age_seconds=600");         // 同一分类反复刷不重复打接口
            u.Append("&query_type=").Append(ApiQueryType(sort, query));
            if (!string.IsNullOrEmpty(query)) u.Append("&search_text=").Append(Uri.EscapeDataString(query));
            if (!string.IsNullOrEmpty(tag)) u.Append("&requiredtags%5B0%5D=").Append(Uri.EscapeDataString(tag));

            string json = WsNet.GetString(u.ToString(), Ua, 8000);
            if (string.IsNullOrEmpty(json))
            {
                // 冷启动第一次经常空手而归（连接刚建、边缘还没热）：隔 300 毫秒再来一次
                try { System.Threading.Thread.Sleep(300); } catch { }
                json = WsNet.GetString(u.ToString(), Ua, 8000);
            }
            if (string.IsNullOrEmpty(json)) return null;
            List<string> objs = SplitObjects(json, "publishedfiledetails");
            List<WsItem> outp = new List<WsItem>();
            // 缓存命中判断用的 ck 在方法开头算好了
            string cacheKey = (query ?? "") + "|" + (sort ?? "") + "|" + page + "|" + (tag ?? "");
            foreach (string o in objs)
            {
                string id = JStr(o, "publishedfileid");
                if (id.Length == 0) continue;
                WsItem it = new WsItem();
                it.Id = id;
                it.Title = JStr(o, "title").Trim();
                it.Preview = FixImageUrl(JStr(o, "preview_url"));
                it.Size = JNum(o, "file_size");
                it.Subs = JNum(o, "subscriptions");
                it.Views = JNum(o, "views");
                if (it.Url.Length == 0) it.Url = JStr(o, "file_url");   // 列表接口也带直链，别让下载按钮灰着
                it.Created = JNum(o, "time_created");
                it.Updated = JNum(o, "time_updated");
                it.Desc = JStr(o, "short_description");
                if (it.Desc.Length == 0) it.Desc = JStr(o, "description");
                it.Tags = TagsOf(o);
                it.DetailsLoaded = true;
                outp.Add(it);
            }
            if (outp.Count > 0)
                lock (apiGate) { apiCache[cacheKey] = outp; apiCacheAt[cacheKey] = DateTime.Now; }
            return outp;
        }

        /// <summary>
        /// 给「怎么拿 key」向导做的实测：返回空串 = key 可用，否则是一句能看懂的原因。
        ///
        /// 分两次打接口：先打一个**不需要 key** 的端点确认线路通，再带 key 查一页。
        /// 只打一次的话，「key 不对」和「网不通」在调用方看来都是“没返回”，
        /// 会把用户往错的方向推（明明 key 是对的，却让人回去重新申请）。
        /// </summary>
        internal static string TestApiKey(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0) return "还没填 key";
            if (key.Length < 20) return "key 太短了：应该是 32 位，这次只有 " + key.Length + " 位";

            string probe = WsNet.GetString("https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/?format=json", Ua, 6000);
            if (string.IsNullOrEmpty(probe)) return "连不上 Steam 接口（线路不通或被挡）；key 本身还没能验证";

            StringBuilder u = new StringBuilder(PubRoot + "QueryFiles/v1/?format=json");
            u.Append("&key=").Append(Uri.EscapeDataString(key));
            u.Append("&appid=").Append(AppId);
            u.Append("&page=1&numperpage=1&query_type=3");
            string json = WsNet.GetString(u.ToString(), Ua, 8000);
            if (string.IsNullOrEmpty(json)) return "Steam 没收这个 key：可能没复制全，或这个 key 已被停用";
            if (json.IndexOf("publishedfiledetails", StringComparison.Ordinal) < 0)
                return "Steam 回了意料之外的内容：" + (json.Length > 100 ? json.Substring(0, 100) : json);
            return "";
        }

        // ---------------- GameBanana：Steam 整条不通时的备用内容源 ----------------

        const int GbGameL4D2 = 3456;   // GameBanana 上「Left 4 Dead 2」的游戏 id

        /// <summary>
        /// GameBanana 搜索/浏览。它家 API 不走 Steam，工坊被墙时这是唯一还能出东西的路。
        /// 返回 null = 没成功；返回空表 = 真没搜到。
        /// </summary>
        static readonly Dictionary<string, List<WsItem>> gbCache = new Dictionary<string, List<WsItem>>(StringComparer.Ordinal);
        static readonly Dictionary<string, DateTime> gbCacheAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        static readonly object gbGate = new object();

        internal static List<WsItem> AltSearch(string query, int page, int perPage)
        {
            // 10 分钟内同一个词直接给缓存：来回切位置不该每次都重新打网络
            string ck = (query ?? "") + "|" + page;
            lock (gbGate)
            {
                DateTime at;
                List<WsItem> hit;
                if (gbCacheAt.TryGetValue(ck, out at) && (DateTime.Now - at).TotalMinutes < 10 && gbCache.TryGetValue(ck, out hit))
                    return hit;
            }

            // 中文/别名先翻成英文关键词（GameBanana 只有英文库），一个词搜不到就接着试下一个
            string[] keys = GbKeywords(query);
            List<WsItem> outp = new List<WsItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            bool anyOk = false;
            if (keys.Length == 0)
            {
                List<WsItem> all = GbFetch("", page, perPage);
                if (all != null) { anyOk = true; outp = all; }
            }
            else
            {
                // 几个近义词并行问：串行时 4 个词要 8 秒，并行 1.9 秒（实测）
                List<WsItem>[] buckets = new List<WsItem>[keys.Length];
                System.Threading.Thread[] ths = new System.Threading.Thread[keys.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    int idx = i;
                    ths[i] = new System.Threading.Thread(delegate() { try { buckets[idx] = GbFetch(keys[idx], page, perPage); } catch { } });
                    ths[i].IsBackground = true;
                    ths[i].Start();
                }
                foreach (System.Threading.Thread th in ths) th.Join(6000);
                foreach (List<WsItem> part in buckets)
                {
                    if (part == null) continue;
                    anyOk = true;
                    foreach (WsItem it in part)
                    {
                        if (!seen.Add(it.Id)) continue;
                        outp.Add(it);
                        if (outp.Count >= perPage) break;
                    }
                    if (outp.Count >= perPage) break;
                }
            }
            if (!anyOk) return null;
            try { Core.Log("INFO", "GameBanana 兜底：" + outp.Count + " 条（" + query + "）"); } catch { }
            lock (gbGate) { gbCache[ck] = outp; gbCacheAt[ck] = DateTime.Now; }
            return outp;
        }

        /// <summary>问 GameBanana 一次。返回 null = 网络失败，「空表」= 真没搜到。</summary>
        static List<WsItem> GbFetch(string keyword, int page, int perPage)
        {
            StringBuilder u = new StringBuilder("https://gamebanana.com/apiv11/");
            if (!string.IsNullOrEmpty(keyword))
            {
                u.Append("Util/Search/Results?_sSearchString=").Append(Uri.EscapeDataString(keyword));
                u.Append("&_sModelName=Mod&_idGameRow=").Append(GbGameL4D2);
                u.Append("&_nPerpage=").Append(perPage).Append("&_nPage=").Append(page);
            }
            else
            {
                u.Append("Mod/Index?_nPerpage=").Append(perPage).Append("&_nPage=").Append(page);
                u.Append("&_aFilters%5BGeneric_Game%5D=").Append(GbGameL4D2);
            }
            string json = WsNet.GetString(u.ToString(), Ua, 12000);
            if (string.IsNullOrEmpty(json)) return null;

            List<WsItem> outp = new List<WsItem>();
            foreach (string o in SplitObjects(json, "_aRecords"))
            {
                long raw = JNum(o, "_idRow");
                if (raw <= 0) continue;
                WsItem it = new WsItem();
                it.Id = "gb" + raw;                       // 前缀区分来源：详情/点击都靠它分流
                it.Title = JStr(o, "_sName").Trim();
                it.Preview = FixImageUrl(GbImage(o));
                it.Created = JNum(o, "_tsDateAdded");
                it.Updated = JNum(o, "_tsDateModified");
                it.Note = "GameBanana";
                if (it.Title.Length == 0 || it.Preview.Length == 0) continue;
                outp.Add(it);
            }
            return outp;
        }

        /// <summary>
        /// 关键词表：GameBanana 是英文库，中文位置名原样发过去必然零结果。
        /// 一个中文词给 3~4 个英文近义词，多搜几趟合并去重 —— 比只翻一个词命中率高得多。
        /// </summary>
        static string[] GbKeywords(string q)
        {
            string k = (q ?? "").Trim().ToLowerInvariant();
            if (k.Length == 0) return new string[0];

            if (k.Contains("手枪")) return new string[] { "pistol", "glock", "deagle", "beretta" };
            if (k.Contains("霰弹") || k.Contains("连喷") || k.Contains("铁喷")) return new string[] { "shotgun", "remington", "spas" };
            if (k.Contains("狙击")) return new string[] { "sniper", "awp", "scout" };
            if (k.Contains("冲锋")) return new string[] { "smg", "uzi", "mp5" };
            if (k.Contains("步枪") || k.Contains("突击")) return new string[] { "rifle", "ak-47", "m16" };
            if (k.Contains("近战") || k.Contains("刀")) return new string[] { "melee", "katana", "axe" };
            if (k.Contains("投掷") || k.Contains("手雷") || k.Contains("燃烧")) return new string[] { "grenade", "molotov", "pipe bomb" };
            if (k.Contains("人物") || k.Contains("幸存者") || k.Contains("角色")) return new string[] { "survivor", "zoey", "bill", "coach" };
            if (k.Contains("特感") || k.Contains("感染")) return new string[] { "infected", "tank", "witch", "hunter" };
            if (k.Contains("地图") || k.Contains("战役")) return new string[] { "campaign", "map" };
            if (k.Contains("喷漆")) return new string[] { "spray", "spraypaint" };
            if (k.Contains("音效") || k.Contains("语音")) return new string[] { "sound", "voice", "audio" };
            if (k.Contains("医疗") || k.Contains("药")) return new string[] { "medkit", "pills" };

            // 英文：常见写法归一（ak47 / ak-47，deagle / desert eagle）
            List<string> outp = new List<string>();
            outp.Add(k);
            string squash = k.Replace("-", "").Replace(" ", "");
            if (squash.Length > 0 && squash != k) outp.Add(squash);
            else if (squash.Length > 0) outp.Add(squash.Replace("ak47", "ak-47"));
            if (squash == "deagle") outp.Add("desert eagle");
            if (squash == "awp") outp.Add("magnum sniper");
            return outp.ToArray();
        }

        /// <summary>GameBanana 的详情：它没有 Steam 那套接口，字段全在 ProfilePage 这一份 JSON 里。</summary>
        internal static WsItem GbDetail(string rawId)
        {
            if (string.IsNullOrEmpty(rawId)) return null;
            string json = WsNet.GetString("https://gamebanana.com/apiv11/Mod/" + rawId + "/ProfilePage", Ua, 12000);
            if (string.IsNullOrEmpty(json)) return null;

            WsItem it = new WsItem();
            it.Id = "gb" + rawId;
            it.Title = GbField(json, "_sName").Trim();
            it.Preview = GbImage(json);
            it.Desc = GbText(GbField(json, "_sDescription"));
            it.Creator = GbNested(json, "_aSubmitter", "_sName");
            it.Tags = GbText(GbNested(json, "_aCategory", "_sName"));
            it.Created = JNum(json, "_tsDateAdded");
            it.Updated = JNum(json, "_tsDateModified");
            it.Note = "GameBanana";
            it.DetailsLoaded = true;          // 有这一行，详情页就不会再去问 Steam 要它
            try
            {
                int at = json.IndexOf("\"_aFiles\"", StringComparison.Ordinal);
                if (at >= 0)
                {
                    string seg = json.Substring(at);
                    it.Url = GbField(seg.Length > 2000 ? seg.Substring(0, 2000) : seg, "_sDownloadUrl");
                }
            }
            catch { }
            return it;
        }

        /// <summary>取一个字段，并还原 JSON 转义。</summary>
        static string GbField(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? GbUnescape(m.Groups[1].Value) : "";
        }

        /// <summary>嵌套对象里的字段（作者名在 _aSubmitter 里，分类名在 _aCategory 里）。</summary>
        static string GbNested(string json, string objKey, string field)
        {
            int at = json.IndexOf("\"" + objKey + "\"", StringComparison.Ordinal);
            if (at < 0) return "";
            int end = json.IndexOf("\"_a", at + objKey.Length + 4, StringComparison.Ordinal);
            string seg = end > at ? json.Substring(at, end - at) : json.Substring(at, Math.Min(800, json.Length - at));
            return GbField(seg, field);
        }

        static string GbUnescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\/", "/").Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "").Replace("\\t", " ").Replace("\\\\", "\\");
        }

        /// <summary>把 HTML/BBCode 去掉，只留能塞进详情框的纯文字。</summary>
        static string GbText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, "<[^>]+>", " ");
            s = Regex.Replace(s, "\\[/?[a-zA-Z]+[^\\]]*\\]", " ");
            s = s.Replace("[*]", "· ");
            s = Regex.Replace(s, "[ \\t]+", " ");
            s = Regex.Replace(s, "\\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>
        /// 装完补两样：库里卡片的预览图，以及「这个 MOD 从哪来的」记录。
        /// 后者写成 vpk 旁边的 .url.txt，右键「打开工坊详情页」靠它开网页。
        /// </summary>
        static void SaveInstalledExtra(WsItem it, string addons, string fileName)
        {
            try
            {
                if (it == null || string.IsNullOrEmpty(fileName)) return;
                string installed = Path.Combine(addons, Path.GetFileName(fileName));
                SavePreview(it, installed);
                if (it.Id.StartsWith("gb", StringComparison.Ordinal))
                    File.WriteAllText(installed + ".url.txt", "https://gamebanana.com/mods/" + it.Id.Substring(2), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>zip 里有没有现成的 vpk（有就直接用，没有才需要打包）。</summary>
        static bool HasVpkInside(string zipPath)
        {
            try
            {
                using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    foreach (System.IO.Compression.ZipArchiveEntry e in za.Entries)
                        if (e.FullName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>很多包外面套了一层同名文件夹（addons/xxx 或 hitman_pistol/），往里剥到真正的根。</summary>
        static string UnwrapSingleRoot(string dir)
        {
            try
            {
                for (int guard = 0; guard < 3; guard++)
                {
                    string[] subs = Directory.GetDirectories(dir);
                    string[] files = Directory.GetFiles(dir);
                    if (files.Length == 0 && subs.Length == 1) { dir = subs[0]; continue; }
                    break;
                }
            }
            catch { }
            return dir;
        }

        /// <summary>按文件头认容器（不信扩展名：工坊和 GameBanana 都有把 zip 叫成别的的包）。</summary>
        static string DetectContainerExt(string path)
        {
            try
            {
                byte[] head = new byte[8];
                using (FileStream fs = File.OpenRead(path))
                {
                    if (fs.Read(head, 0, head.Length) < 4) return "";
                }
                if (head[0] == 0x50 && head[1] == 0x4B) return ".zip";
                if (head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF) return ".7z";
                if (head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21) return ".rar";
                if (head[0] == 0x34 && head[1] == 0x12 && head[2] == 0xAA && head[3] == 0x55) return ".vpk";
            }
            catch { }
            return "";
        }

        /// <summary>
        /// GameBanana 一键装：下载它的文件（zip/7z/rar）→ 解包出 vpk → 丢进 addons。
        /// 它的下载地址是重定向直链、内容不是 vpk，不能像工坊那样直接落盘到 addons 里。
        /// </summary>
        static void Say(Action<string> stage, string msg)
        {
            if (stage == null) return;
            try { stage(msg); } catch { }
        }

        internal static string GbInstall(WsItem it, out int added, Action<string> stage)
        {
            return GbInstall(it, out added, stage, null);
        }

        internal static string GbInstall(WsItem it, out int added, Action<string> stage, Action<long, long> onProgress)
        {
            added = 0;
            if (it == null) return "没有选中条目";
            string url = it.Url ?? "";
            if (string.IsNullOrEmpty(url) && it.Id.StartsWith("gb", StringComparison.Ordinal))
            {
                WsItem full = GbDetail(it.Id.Substring(2));
                if (full != null) url = full.Url;
            }
            if (string.IsNullOrEmpty(url)) return "这个条目没有可下载的文件";

            string tmpDir = Path.Combine(Path.GetTempPath(), "l4d2gb");
            string part = Path.Combine(tmpDir, it.Id + ".part");
            string file = Path.Combine(tmpDir, it.Id + ".pkg");
            try
            {
                Directory.CreateDirectory(tmpDir);
                if (File.Exists(part)) File.Delete(part);
            }
            catch { }

            Say(stage, "下载中…");
            long got = WsNet.DownloadTo(url, Ua, 120000, part, 0, delegate(long g, long t)
            {
                if (t > 0) Say(stage, "下载中 " + (int)Math.Min(100, g * 100 / t) + "%  ·  " + Core.Human(g) + " /  " + Core.Human(t));
                else Say(stage, "下载中  ·  " + Core.Human(g));
                if (onProgress != null) { try { onProgress(g, t); } catch { } }
            });
            if (got <= 0) { try { File.Delete(part); } catch { } return "下载失败：GameBanana 连不上"; }
            try
            {
                if (File.Exists(file)) File.Delete(file);
                File.Move(part, file);
                // 导入器只认 .vpk/.zip/.rar/.7z 这几个后缀：按文件头把后缀改成真的那种
                string ext = DetectContainerExt(file);
                if (ext.Length > 0)
                {
                    string renamed = Path.ChangeExtension(file, ext);
                    if (File.Exists(renamed)) File.Delete(renamed);
                    File.Move(file, renamed);
                    file = renamed;
                }
            }
            catch (Exception ex) { return "下载后保存失败：" + ex.Message; }

            string addons = Core.GameAddonsDir();
            string unpack = Path.Combine(tmpDir, "unpack");
            try
            {
                // 散文件包（materials/、models/ 这种）：addons 只认 vpk，先打成一个 vpk 再导入
                if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !HasVpkInside(file))
                {
                    try { if (Directory.Exists(unpack)) Directory.Delete(unpack, true); } catch { }
                    Directory.CreateDirectory(unpack);
                    try { System.IO.Compression.ZipFile.ExtractToDirectory(file, unpack); }
                    catch (Exception ex) { return "解压失败：" + ex.Message; }
                    string src = UnwrapSingleRoot(unpack);
                    string vpk = Path.Combine(tmpDir, SafeFileName(it.Title, it.Id) + ".vpk");
                    Say(stage, "打包中…（把散文件打成 vpk）");
                    byte[] shot = null;
                    if (!string.IsNullOrEmpty(it.Preview))
                    {
                        try { shot = WsNet.GetBytes(it.Preview, Ua, 15000); } catch { }
                    }
                    int packed = VpkPack.PackDir(src, vpk, shot);
                    if (packed == 0) return "这个包是散文件（模型/材质/语音），但没有能打进 vpk 的游戏资源";
                    Say(stage, "导入中…");
                    int sk; List<string> er;
                    string m2 = Catalog.ImportTo(new string[] { vpk }, addons, out added, out sk, out er);
                    SaveInstalledExtra(it, addons, Path.GetFileName(vpk));
                    try { File.Delete(vpk); } catch { }
                    return m2 + "（已把 " + packed + " 个散文件打包成 vpk）";
                }

                int skipped;
                List<string> errs;
                string msg = Catalog.ImportTo(new string[] { file }, addons, out added, out skipped, out errs);
                SaveInstalledExtra(it, addons, Path.GetFileName(file));
                return msg;
            }
            finally
            {
                try { File.Delete(file); } catch { }
                try { if (Directory.Exists(unpack)) Directory.Delete(unpack, true); } catch { }
            }
        }

        /// <summary>GameBanana 的图分两段给：_sBaseUrl + _sFile，拼起来才是完整地址。</summary>
        static string GbImage(string o)
        {
            Match b = Regex.Match(o, "\"_sBaseUrl\"\\s*:\\s*\"([^\"]+)\"");
            Match f = Regex.Match(o, "\"_sFile\"\\s*:\\s*\"([^\"]+)\"");
            if (!b.Success || !f.Success) return "";
            // JSON 里的斜杠是转义的（https:\/\/images...），不还原的话拼出来的地址根本请求不出去
            string u = b.Groups[1].Value.Replace("\\/", "/") + "/" + f.Groups[1].Value.Replace("\\/", "/");
            return u;
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
                    it.Preview = FixImageUrl(JStr(o, "preview_url"));
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
            string saved, note;
            Download(it, dest, progress, out saved, out note, null);
            LastSavedPath = saved;
            LastDownloadNote = note;
        }

        /// <summary>
        /// 下载的「可并发版」：结果走 out 参数，不碰静态字段。
        /// 合集/配套那批以前是一条一条下（每条都等上一条写完），两路并行能明显缩短整批时间。
        /// </summary>
        public static void Download(WsItem it, string dest, Action<long, long> progress, out string savedPath, out string note)
        {
            Download(it, dest, progress, out savedPath, out note, null);
        }

        /// <summary>取消下载时清掉没下完的临时文件（.part 与分段用的 %TEMP% 分片目录）；库里的成品不动。</summary>
        internal static void CleanPartial(string dest)
        {
            WsNet.CleanPartial(dest);
        }

        /// <summary>带暂停 / 取消开关的版本：ctl 非空时，界面点「暂停 / 取消」会从这个调用里抛 DlAbort。</summary>
        public static void Download(WsItem it, string dest, Action<long, long> progress, out string savedPath, out string note, WsNet.DlCtl ctl)
        {
            savedPath = "";
            note = "";
            string part = dest + ".part";
            // part 是断点续传的凭据，不能一来就删。只有它已经 >= 目标大小（旧垃圾/换过文件）才清掉。
            try
            {
                if (File.Exists(part) && it.Size > 0 && new FileInfo(part).Length >= it.Size) File.Delete(part);
            }
            catch { }

            // 走联网层：优选 IP（CDN 大多命中 cdn.steamusercontent），失败自动退回域名重下
            long gotBytes = WsNet.DownloadTo(it.Url, Ua, 30000, part, it.Size, progress, ctl);
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
            note = "";
            savedPath = "";
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
                    note = "这个 vpk 结构不完整（" + why + "），先留着，进游戏可能缺件";
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
                note = "这是 " + kind + " 压缩包（不是单个 vpk）：已按压缩包存好，解压/装配由库处理";
            }
            else
            {
                note = "下到的文件不是标准 vpk/压缩包（未知格式），先原样留着";
            }

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
            savedPath = dest;
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
            // 网页主站已知不通时，别为了抓侧栏依赖去硬等 20 秒超时
            if (WsNet.IsOffline || (WsNet.SteamProbed && !WsNet.SteamRouteAlive)) return list;
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

        /// <summary>删掉某个工坊物品的「已下载」标记（文件都删了，记录留着会一直显示已下载）。</summary>
        internal static void ForgetDownloaded(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                if (!File.Exists(IdMapPath)) return;
                List<string> keep = new List<string>();
                foreach (string line in File.ReadAllLines(IdMapPath, Encoding.UTF8))
                {
                    int bar = line.IndexOf('|');
                    if (bar > 0 && string.Equals(line.Substring(0, bar).Trim(), id, StringComparison.Ordinal)) continue;
                    keep.Add(line);
                }
                File.WriteAllLines(IdMapPath, keep.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// 这个工坊物品本机是不是已经有了：我们下过（idmap 里记着）、库里/游戏 addons 认到同一个工坊 ID、
        /// 或已有一个同名 MOD。配套 MOD 判断用它 —— 已经装了的不该再问「要不要一起下」（用户 2026-11-22 要求）。
        /// </summary>
        internal static bool IsInstalledLocally(string id, string title)
        {
            if (!string.IsNullOrEmpty(id))
            {
                try { if (LoadDownloadedIds().Contains(id)) return true; } catch { }
                try
                {
                    List<ModItem> mods = Core.ModList;
                    if (mods != null)
                        foreach (ModItem m in mods)
                        {
                            if (m == null) continue;
                            if (string.Equals(m.WorkshopId, id, StringComparison.Ordinal)) return true;
                        }
                }
                catch { }
            }
            string t = (title ?? "").Trim();
            if (t.Length > 3)
            {
                try
                {
                    List<ModItem> mods = Core.ModList;
                    if (mods != null)
                        foreach (ModItem m in mods)
                        {
                            if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                            if (string.Equals(m.Name.Trim(), t, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                }
                catch { }
            }
            return false;
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
        /// <summary>
        /// 本机已经订阅并下好：直接把里面的 vpk 复制进 addons，一个字节都不用下载。
        /// 返回 null 表示本机没有这份订阅，调用方照常走下载。
        /// </summary>
        internal static string InstallFromSubscription(WsItem it, string addons, out int added)
        {
            added = 0;
            try
            {
                string dir = Catalog.SubscribedDir(it == null ? null : it.Id);
                if (dir == null) return null;
                string[] vpks = Directory.GetFiles(dir, "*.vpk", SearchOption.TopDirectoryOnly);
                if (vpks.Length == 0) return null;
                int skipped;
                List<string> errs;
                string msg = Catalog.ImportTo(vpks, addons, out added, out skipped, out errs);
                return msg + "（本机已订阅，直接复制，没走网络）";
            }
            catch { return null; }
        }

        /// <summary>steamcmd 放哪：配置目录下的 steamcmd\steamcmd.exe。</summary>
        internal static string SteamCmdPath
        {
            get
            {
                try
                {
                    string p = Path.Combine(Core.CfgDir, "steamcmd", "steamcmd.exe");
                    if (File.Exists(p)) return p;
                    p = Path.Combine(Core.PkgDir, "steamcmd", "steamcmd.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
                return null;
            }
        }

        /// <summary>
        /// SteamCMD 兜底：走 Steam 自己的下载网络，不依赖 api / cdn。
        /// 匿名优先；下完从 steamcmd 自己的 steamapps 里取 vpk，导进 addons。
        /// </summary>
        internal static string DownloadViaSteamCmd(WsItem it, string addons, out int added)
        {
            added = 0;
            if (it == null || string.IsNullOrEmpty(it.Id)) return "没有物品 ID";
            string exe = SteamCmdPath;
            if (exe == null) return "没有 steamcmd：把 steamcmd.exe 放到 " + Path.Combine(Core.CfgDir, "steamcmd");
            string work = Path.Combine(Core.CfgDir, "steamcmd");
            try
            {
                // 首次下载不需要 validate（那是给「校验已有文件」用的，会多重扫一遍，白慢一截）
                ProcessStartInfo psi = new ProcessStartInfo(exe, "+login anonymous +workshop_download_item 550 " + it.Id + " +quit");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = work;
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(300000);
                    if (p.ExitCode != 0) return "steamcmd 退出码 " + p.ExitCode;
                    string dir = Path.Combine(work, "steamapps", "workshop", "content", "550", it.Id);
                    if (!Directory.Exists(dir)) dir = Catalog.SubscribedDir(it.Id);
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                        return "steamcmd 说下完了，但找不到文件（多半是这个物品不允许匿名下载）";
                    string[] vpks = Directory.GetFiles(dir, "*.vpk", SearchOption.AllDirectories);
                    if (vpks.Length == 0) return "下完了，但包里没有 vpk";
                    int skipped; List<string> errs;
                    string msg = Catalog.ImportTo(vpks, addons, out added, out skipped, out errs);
                    return msg + "（SteamCMD）";
                }
            }
            catch (Exception ex) { return "steamcmd 跑不起来：" + ex.Message; }
        }

        /// <summary>
        /// 封面图地址修正：images.steamusercontent.com 这条线实测被掐（12 秒超时），
        /// 换成同一套 UGC 路径的 cdn.steamusercontent.com 能拿到图（实测 200 / 1.1 MB）。
        /// </summary>
        internal static string FixImageUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.IndexOf("images.steamusercontent.com", StringComparison.OrdinalIgnoreCase) >= 0)
                url = url.Replace("images.steamusercontent.com", "steamuserimages-a.akamaihd.net");
            else if (url.IndexOf("cdn.steamusercontent.com", StringComparison.OrdinalIgnoreCase) >= 0)
                url = url.Replace("cdn.steamusercontent.com", "steamuserimages-a.akamaihd.net");
            return url;
        }

        /// <summary>给工坊预览图加上服务端缩放参数：3.4MB 的 4K 原图在服务端缩成 11KB 再下，卡片缩略图秒出。</summary>
        internal static string ThumbUrl(string url, int maxW, int maxH)
        {
            string u = FixImageUrl(url);
            if (string.IsNullOrEmpty(u)) return u;
            if (u.IndexOf("/ugc/", StringComparison.OrdinalIgnoreCase) < 0 || u.IndexOf('?') >= 0) return u;
            int w = Math.Max(256, Math.Min(1024, maxW * 2));
            int h = Math.Max(144, Math.Min(576, maxH * 2));
            return u + "?imw=" + w + "&imh=" + h + "&ima=fit&impolicy=Letterbox&imcolor=%23000000&letterbox=false";
        }

        /// <summary>这条东西从哪来的：详情页/预览面板统一用它，别写死 Steam。</summary>
        internal static string SourceLabel(WsItem it)
        {
            if (it == null) return "";
            if (it.Id.StartsWith("gb", StringComparison.Ordinal)) return "GameBanana";
            if (it.Id.StartsWith("s", StringComparison.Ordinal)) return string.IsNullOrEmpty(it.Note) ? "外部来源" : it.Note;
            return "Steam 创意工坊";
        }

        public static void SavePreview(WsItem it, string savedPath)
        {
            if (it == null || string.IsNullOrEmpty(savedPath)) return;
            try
            {
                string dir = Path.GetDirectoryName(savedPath);
                string stem = Path.GetFileNameWithoutExtension(savedPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return;
                // 先吃缓存：浏览时 WsThumb 已经把原图存进 workshop-img 了，这里别再下一遍。
                // 以前 15 次下载只有 1 次存到封面，就是因为这里硬走网络、失败还静默 return。
                byte[] b = WsThumb.RawOf(it.Id);
                if (b == null && !string.IsNullOrEmpty(it.Preview))
                    b = WsNet.GetBytes(FixImageUrl(it.Preview), Ua, 15000);
                if (b == null || b.Length < 64)
                {
                    Core.Log("WARN", "工坊：封面没拿到（" + it.Id + "）"
                        + (string.IsNullOrEmpty(it.Preview) ? "：条目没有预览图地址" : "：图下载失败"));
                    return;
                }
                WsThumb.PutRaw(it.Id, b);
                // 优先塞进 vpk（游戏和程序看到同一份，目录里也不多一个文件）
                if (savedPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) && VpkPack.InjectImage(savedPath, b))
                {
                    CleanSidecarImages(dir, stem);       // 旧版本在旁边留过同名图，顺手清掉
                    Cover.Forget(savedPath);             // 包刚被重写：旧的「没封面」结论作废
                    Core.Log("OK", "工坊：封面已写进 vpk " + Path.GetFileName(savedPath));
                    return;
                }
                // v2 / 多卷 / 带 preload 的包 InjectImage 会主动放弃（不动用户几十 MB 的包），
                // 这时退回「旁边放同名图」，不是失败。
                if (WriteSidecar(savedPath, b))
                {
                    Cover.Forget(savedPath);
                    Core.Log("OK", "工坊：预览图已保存 " + Path.GetFileName(savedPath) + "（放在 vpk 旁边）");
                }
                else Core.Log("WARN", "工坊：预览图写不进 vpk 也放不到旁边（" + Path.GetFileName(savedPath) + "）");
            }
            catch (Exception ex) { Core.Log("WARN", "工坊：预览图保存失败 " + ex.Message); }
        }

        /// <summary>把封面放在 vpk 旁边（vpk 同级同名不同扩展）。成功返回 true。</summary>
        static bool WriteSidecar(string vpkPath, byte[] b)
        {
            try
            {
                string dir = Path.GetDirectoryName(vpkPath);
                string stem = Path.GetFileNameWithoutExtension(vpkPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return false;
                string ext = ExtOfImage(b);
                if (ext == ".webp") return false;          // GDI+ 解不了 webp，放了也用不上
                string dst = Path.Combine(dir, stem + ext);
                if (File.Exists(dst) && new FileInfo(dst).Length > 1024) return true;
                File.WriteAllBytes(dst, b);
                return true;
            }
            catch { return false; }
        }

        /// <summary>按文件头判断图片真实格式（缓存命中时没有 URL 可参考）。</summary>
        static string ExtOfImage(byte[] b)
        {
            if (b != null && b.Length >= 4)
            {
                if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
                if (b[0] == 0x89 && b[1] == 0x50) return ".png";
                if (b[0] == 0x47 && b[1] == 0x49) return ".gif";
                if (b[0] == 0x52 && b[1] == 0x49) return ".webp";   // RIFF/WEBP
            }
            return ".jpg";
        }

        /// <summary>删掉旧版本留在 vpk 旁边的同名封面，保持目录干净。</summary>
        static void CleanSidecarImages(string dir, string stem)
        {
            foreach (string e in new string[] { ".jpg", ".jpeg", ".png", ".gif" })
            {
                try { string p = Path.Combine(dir, stem + e); if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        /// <summary>
        /// 补封面：库里带工坊 ID 的 vpk，逐个取 preview_url 塞进包（addonimage.jpg）。
        /// 返回补成功几个，failed 返回失败几个。
        /// </summary>
        internal static int BackfillCovers(Action<string> stage, out int failed)
        {
            failed = 0;
            int ok = 0;
            try
            {
                List<ModItem> mods = Core.ModList;
                List<string> ids = new List<string>();
                foreach (ModItem m in mods)
                {
                    if (m == null || string.IsNullOrEmpty(m.WorkshopId) || string.IsNullOrEmpty(m.FullPath)) continue;
                    if (!m.FullPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ids.Contains(m.WorkshopId)) ids.Add(m.WorkshopId);
                }
                if (ids.Count == 0) { if (stage != null) stage("库里没有带工坊 ID 的 vpk，没什么可补的"); return 0; }
                if (stage != null) stage("要补 " + ids.Count + " 个物品的封面…");
                List<WsItem> items = null;
                try { items = Fetch(ids); } catch { }
                Dictionary<string, string> prev = new Dictionary<string, string>(StringComparer.Ordinal);
                if (items != null)
                    foreach (WsItem it in items) if (it != null && !string.IsNullOrEmpty(it.Preview)) prev[it.Id] = it.Preview;
                int idx = 0;
                foreach (ModItem m in mods)
                {
                    if (m == null || string.IsNullOrEmpty(m.WorkshopId) || string.IsNullOrEmpty(m.FullPath)) continue;
                    if (!m.FullPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) continue;
                    idx++;
                    // 浏览时缓存过的原图直接用，不必再走网络（断网也能补）
                    byte[] b = WsThumb.RawOf(m.WorkshopId);
                    string url;
                    if (b == null && prev.TryGetValue(m.WorkshopId, out url))
                        try { b = WsNet.GetBytes(FixImageUrl(url), Ua, 15000); } catch { }
                    if (b == null || b.Length < 64) { failed++; continue; }
                    WsThumb.PutRaw(m.WorkshopId, b);
                    bool done = VpkPack.InjectImage(m.FullPath, b) || WriteSidecar(m.FullPath, b);
                    if (done) { ok++; Cover.Forget(m.FullPath); } else failed++;
                    if (stage != null) stage("补封面 " + idx + "/" + ids.Count + (ok > 0 ? "（成功 " + ok + "）" : ""));
                }
            }
            catch { }
            return ok;
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

            // 封面原图缓存：下载后直接复用浏览时缓存的那张，不再重复走网络（2026-11-22 查封面问题）
            {
                string tid = "selftest-cover-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                byte[] probe = new byte[128];
                for (int i = 0; i < probe.Length; i++) probe[i] = (byte)(i & 0xFF);
                WsThumb.PutRaw(tid, probe);
                byte[] back = WsThumb.RawOf(tid);
                bool okc = back != null && back.Length == probe.Length;
                try { File.Delete(Path.Combine(WsThumb.CacheDir, "img-" + WsText.SafeId(tid) + ".bin")); } catch { }
                if (okc) Core.Log("INFO", "封面缓存往返正常（下载后直接复用，不再重复下载）");
                else { bad++; Core.Log("FAIL", "封面缓存往返失败：RawOf 取不到 PutRaw 写进去的字节"); }
            }

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
