using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Installer
{
    /// <summary>
    /// 一个 MOD 来源。kind=json 的先只有内置 GameBanana；kind=html 的是「给列表页地址 + 一条正则」的通用抓取。
    /// </summary>
    internal sealed class SourceDef
    {
        public string Id = "", Name = "", Kind = "html", Prefix = "";
        public bool Enabled = true;
        public string ListUrl = "", ItemPattern = "", Detail = "", BaseUrl = "";
    }

    /// <summary>
    /// 来源清单：配置文件 来源.ini，放配置目录。用户往里加一段就能多一个来源，不用改代码。
    /// </summary>
    internal static class Sources
    {
        internal static List<SourceDef> All = new List<SourceDef>();
        static DateTime loadedAt = DateTime.MinValue;

        internal static string FilePath { get { return Path.Combine(Core.CfgDir, "来源.ini"); } }

        /// <summary>第一次运行时写一份带注释的样板，用户照着改就行。</summary>
        internal static void EnsureDefault()
        {
            try
            {
                if (File.Exists(FilePath)) return;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# MOD 来源配置。每多一个 [名字] 段就是多一个来源，改完保存、重启程序生效。");
                sb.AppendLine("# 目前支持 kind=html：给一个列表页 + 一条正则，程序抓卡片；点开用浏览器打开详情页。");
                sb.AppendLine("# GameBanana 是内置的（不用配地址），[gb] 只负责开关。");
                sb.AppendLine();
                sb.AppendLine("[gb]");
                sb.AppendLine("name=GameBanana");
                sb.AppendLine("kind=json");
                sb.AppendLine("enabled=0");
                sb.AppendLine();
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>读配置。解析很土：一行一个 key=value，[段] 开头就是一个来源。</summary>
        internal static void Load(bool force)
        {
            lock (All)
            {
                if (!force && All.Count > 0 && (DateTime.Now - loadedAt).TotalSeconds < 30) return;
                All.Clear();
                EnsureDefault();
                try
                {
                    if (File.Exists(FilePath))
                    {
                        SourceDef cur = null;
                        foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                        {
                            string s = raw.Trim().TrimStart('\uFEFF');
                            if (s.Length == 0 || s.StartsWith("#")) continue;
                            if (s.StartsWith("[") && s.EndsWith("]"))
                            {
                                cur = new SourceDef();
                                cur.Id = s.Substring(1, s.Length - 2).Trim();
                                cur.Prefix = "s" + cur.Id + ":";
                                All.Add(cur);
                                continue;
                            }
                            int eq = s.IndexOf('=');
                            if (cur == null || eq <= 0) continue;
                            string k = s.Substring(0, eq).Trim().ToLowerInvariant();
                            string v = s.Substring(eq + 1).Trim();
                            switch (k)
                            {
                                case "name": cur.Name = v; break;
                                case "kind": cur.Kind = v.ToLowerInvariant(); break;
                                case "enabled": cur.Enabled = v != "0" && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase); break;
                                case "list": cur.ListUrl = v; break;
                                case "item": cur.ItemPattern = v; break;
                                case "detail": cur.Detail = v; break;
                                case "base": cur.BaseUrl = v; break;
                            }
                        }
                    }
                }
                catch { }
                if (All.Count == 0)
                {
                    SourceDef gb = new SourceDef();
                    gb.Id = "gb"; gb.Name = "GameBanana"; gb.Kind = "json"; gb.Prefix = "sgb:";
                    All.Add(gb);
                }
                loadedAt = DateTime.Now;
            }
        }

        /// <summary>某个来源现在开没开（读配置，现读现算）。</summary>
        internal static bool IsEnabled(string id)
        {
            try
            {
                Load(true);
                lock (All) { foreach (SourceDef d in All) if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d.Enabled; }
            }
            catch { }
            return false;
        }

        /// <summary>改某个来源的开关，直接写回来源.ini（只动 enabled 那一行，注释和正则原样留着）。</summary>
        internal static void SetEnabled(string id, bool on)
        {
            try
            {
                EnsureDefault();
                List<string> lines = new List<string>(File.ReadAllLines(FilePath, Encoding.UTF8));
                string section = "";
                for (int i = 0; i < lines.Count; i++)
                {
                    string s = lines[i].Trim();
                    if (s.StartsWith("[") && s.EndsWith("]")) { section = s.Substring(1, s.Length - 2).Trim(); continue; }
                    if (!string.Equals(section, id, StringComparison.OrdinalIgnoreCase)) continue;
                    if (s.StartsWith("enabled=", StringComparison.OrdinalIgnoreCase))
                        lines[i] = "enabled=" + (on ? "1" : "0");
                }
                File.WriteAllLines(FilePath, lines.ToArray(), Encoding.UTF8);
                Load(true);
            }
            catch { }
        }

        /// <summary>按配置顺序问一遍：第一个有结果的来源生效。返回 null 表示全都没结果/没成功。</summary>
        internal static List<WsItem> Take(string query, int page, int perPage, out string sourceName)
        {
            sourceName = "";
            Load(true);          // 每次现读：开关刚改完不该被 30 秒缓存挡住

            // 先看磁盘缓存（1 小时）：同一个位置反复点，不该每次都打网络
            string cachedName;
            List<WsItem> cached = LoadCache(query, page, out cachedName);
            if (cached != null && cached.Count > 0 && SourceEnabled(cachedName))
            {
                sourceName = cachedName;
                return cached;
            }
            List<SourceDef> defs;
            lock (All) { defs = new List<SourceDef>(All); }
            foreach (SourceDef d in defs)
            {
                if (!d.Enabled) continue;
                List<WsItem> got = null;
                try
                {
                    if (d.Kind == "json" && d.Id == "gb") got = Workshop.AltSearch(query, page, perPage);
                    else if (d.Kind == "html" && d.ListUrl.Length > 0 && d.ItemPattern.Length > 0) got = FetchHtml(d, perPage);
                }
                catch { got = null; }
                if (got != null && got.Count > 0)
                {
                    sourceName = d.Name.Length > 0 ? d.Name : d.Id;
                    SaveCache(query, page, sourceName, got);
                    return got;
                }
            }
            return null;
        }

        // ---------------- 磁盘缓存（1 小时） ----------------
        // 搜索结果落盘：重开程序、来回切位置都不再重复打网络。按「查询词 + 页码」分文件。

        static string CacheDir
        {
            get
            {
                string d = Path.Combine(Core.CfgDir, "_缓存");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        static string CachePath(string query, int page)
        {
            string k = (query ?? "") + "|" + page;
            uint h = 2166136261u;
            foreach (char c in k) { h ^= c; h *= 16777619u; }
            return Path.Combine(CacheDir, "src_" + h.ToString("x8") + ".txt");
        }

        static bool SourceEnabled(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (All)
            {
                foreach (SourceDef d in All)
                    if (d.Enabled && string.Equals(d.Name, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static List<WsItem> LoadCache(string query, int page, out string name)
        {
            name = "";
            try
            {
                string p = CachePath(query, page);
                if (!File.Exists(p)) return null;
                if ((DateTime.Now - File.GetLastWriteTime(p)).TotalHours >= 1) return null;
                string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                if (lines.Length < 2) return null;
                List<WsItem> outp = new List<WsItem>();
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split('\t');
                    if (f.Length < 4) continue;
                    WsItem it = new WsItem();
                    it.Id = f[0]; it.Title = f[1]; it.Preview = f[2]; it.Url = f[3];
                    if (f.Length > 4) it.Note = f[4];
                    if (f.Length > 5) { long v; if (long.TryParse(f[5], out v)) it.Updated = v; }
                    it.DetailsLoaded = true;
                    outp.Add(it);
                }
                if (outp.Count == 0) return null;
                name = lines[0].Trim();
                return outp;
            }
            catch { return null; }
        }

        static void SaveCache(string query, int page, string name, List<WsItem> items)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(name ?? "");
                foreach (WsItem it in items)
                {
                    sb.Append(Safe(it.Id)).Append('\t').Append(Safe(it.Title)).Append('\t')
                      .Append(Safe(it.Preview)).Append('\t').Append(Safe(it.Url)).Append('\t')
                      .Append(Safe(it.Note)).Append('\t').Append(it.Updated).AppendLine();
                }
                File.WriteAllText(CachePath(query, page), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static string Safe(string s)
        {
            return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        /// <summary>抓一个 html 来源的列表页。不翻页（很多站翻页要过验证，翻了也是 403）。</summary>
        static List<WsItem> FetchHtml(SourceDef d, int perPage)
        {
            string html = WsNet.GetString(d.ListUrl, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36", 15000);
            if (string.IsNullOrEmpty(html)) return null;
            Regex rx;
            try { rx = new Regex(d.ItemPattern, RegexOptions.IgnoreCase); }
            catch { return null; }
            List<WsItem> outp = new List<WsItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in rx.Matches(html))
            {
                if (outp.Count >= perPage) break;
                if (m.Groups.Count < 4) break;
                string id = m.Groups[1].Value.Trim();
                string img = m.Groups[2].Value.Trim();
                string title = m.Groups[3].Value.Trim();
                if (id.Length == 0 || title.Length == 0) continue;
                if (!seen.Add(id)) continue;
                WsItem it = new WsItem();
                it.Id = d.Prefix + id;
                it.Title = title;
                it.Preview = img.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? img
                    : d.BaseUrl.TrimEnd('/') + "/" + img.TrimStart('/');
                it.Url = d.Detail.Length > 0 ? d.Detail.Replace("{id}", id) : "";
                it.Note = d.Name;
                it.DetailsLoaded = true;       // 没有正文可读，详情页只当占位
                outp.Add(it);
            }
            return outp.Count > 0 ? outp : null;
        }
    }
}
