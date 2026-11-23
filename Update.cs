using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>一次「发现新版本」的结果。字段都来自 GitHub Release 的 latest。</summary>
    internal sealed class UpdateInfo
    {
        public string Tag = "";        // v0.6.28
        public string Version = "";    // 0.6.28
        public string Url = "";        // exe 资产直链（github.com，国内常被掐）
        public string ApiUrl = "";     // 同一个资产的 API 地址（api.github.com，通常能通）
        public string PageUrl = "";    // 发布页（下载页兜底）
        public long Size;              // 资产字节数（0 = 发布没给）
        public string Sha = "";        // sha256（小写；空 = 发布没给）
        public string Notes = "";      // 更新说明正文
    }

    /// <summary>
    /// 程序自更新：查 GitHub 的 latest → 下载到临时目录 → 校验 → 改名旧 exe → 换上新 exe → 重启自己。
    ///
    /// 为什么是「改名」不是「覆盖」：Windows 不许删/覆盖正在运行的 exe，但允许改名。
    /// 顺序必须是 先改名、再落新文件、失败就改回来 —— 任何一步失败都要留下一个能跑的 exe。
    ///
    /// 只换 exe，不碰配置和 MOD 库：配置在 %APPDATA%\L4D2Local，库在游戏 addons，
    /// 和 exe 放哪无关。所以「更新」对用户就是换一个文件。
    /// </summary>
    internal static class AppUpdate
    {
        /// <summary>本地版仓库。便携版（U 盘导入工具）是另一个仓库，别串。</summary>
        private const string Repo = "Yzy10086-l4d2/l4d2-mod-manager-local";
        private const string ApiLatest = "https://api.github.com/repos/" + Repo + "/releases/latest";

        internal static string Current
        {
            get
            {
                Version v = typeof(AppUpdate).Assembly.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        // ---------------------------------------------------------------- 查

        /// <summary>查最新版。返回 null = 查不到（网络不通 / 还没发过 Release / 解析不出 exe）。</summary>
        internal static UpdateInfo Check(int timeoutMs)
        {
            string json = Get(ApiLatest, timeoutMs, true);
            if (string.IsNullOrEmpty(json)) return null;
            return Parse(json);
        }

        /// <summary>把 GitHub 的 latest 响应解析成 UpdateInfo。单独拿出来是为了能拿固定 JSON 做回归。</summary>
        internal static UpdateInfo Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            UpdateInfo u = new UpdateInfo();
            u.Tag = Rx(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            u.PageUrl = Rx(json, "\"html_url\"\\s*:\\s*\"([^\"]+)\"");
            u.Notes = Unescape(Rx(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""));
            if (u.Tag.Length == 0) return null;
            u.Version = Ver(u.Tag);

            // assets 的每个对象里还嵌着 uploader 子对象，按「大括号配对」切块是切不出来的（实测踩到）。
            // 改成：认每个 browser_download_url，回头看它前面 2KB 里的 name / size / digest ——
            // GitHub 的字段顺序固定是 name → size → digest → browser_download_url。
            MatchCollection links = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"");
            foreach (Match m in links)
            {
                string url = m.Groups[1].Value;
                int start = Math.Max(0, m.Index - 2000);
                string win = json.Substring(start, m.Index - start);
                string name = Last(win, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                if (name.Length == 0 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                u.Url = url;
                // 同一窗口里最后一个 "url" 才是这个资产的 API 地址
                u.ApiUrl = Last(win, "\"url\"\\s*:\\s*\"(https://api\\.github\\.com/[^\"]+/assets/\\d+)\"");
                long n;
                if (long.TryParse(Last(win, "\"size\"\\s*:\\s*(\\d+)"), out n)) u.Size = n;
                u.Sha = Last(win, "\"digest\"\\s*:\\s*\"sha256:([0-9a-fA-F]+)\"").ToLowerInvariant();
                break;
            }
            if (u.Url.Length == 0) return null;      // 有 Release 但没传 exe：当作没有可更新的
            return u;
        }

        /// <summary>tag 里的版本号（v0.6.28 / 0.6.28 / v0.6.28-beta 都能取到 0.6.28）。</summary>
        internal static string Ver(string tag)
        {
            if (tag == null) return "";
            Match m = Regex.Match(tag, "(\\d+)\\.(\\d+)(?:\\.(\\d+))?");
            if (!m.Success) return "";
            string third = m.Groups[3].Success ? m.Groups[3].Value : "0";
            return m.Groups[1].Value + "." + m.Groups[2].Value + "." + third;
        }

        /// <summary>b 比 a 新？（按 点分数字逐段比，缺的段当 0）</summary>
        internal static bool Newer(string a, string b)
        {
            if (string.IsNullOrEmpty(b)) return false;
            string[] x = (a ?? "").Split('.'), y = b.Split('.');
            int n = Math.Max(x.Length, y.Length);
            for (int i = 0; i < n; i++)
            {
                int xi = 0, yi = 0;
                if (i < x.Length) int.TryParse(x[i], out xi);
                if (i < y.Length) int.TryParse(y[i], out yi);
                if (yi != xi) return yi > xi;
            }
            return false;
        }

        // ---------------------------------------------------------------- 装

        /// <summary>
        /// 下载并替换当前 exe。返回 null = 成功（调用方负责 Process.Start + 退出）；
        /// 返回字符串 = 失败原因（已经回滚，当前程序照常能跑）。
        /// </summary>
        /// <summary>只下载并校验，不替换（诊断用：--updget）。</summary>
        internal static string ApplyDownloadOnly(UpdateInfo u, string to)
        {
            string err = Download(u.ApiUrl, u.Url, to, null);
            if (err != null) return err;
            long len = new FileInfo(to).Length;
            if (u.Size > 0 && len != u.Size) return "尺寸不符：" + len + " / " + u.Size;
            if (u.Sha.Length > 0 && !string.Equals(Sha256(to), u.Sha, StringComparison.OrdinalIgnoreCase)) return "sha256 不符";
            return null;
        }

        internal static string Apply(UpdateInfo u, Action<int> pct)
        {
            string self = Application.ExecutablePath;
            string part = Path.Combine(Path.GetTempPath(), "l4d2mm-" + San(u.Version) + ".part");
            TryDelete(part);

            // 先走 API 资产地址（api.github.com 能通），失败再退回 github.com 直链
            string err = Download(u.ApiUrl, u.Url, part, pct);
            if (err != null) { TryDelete(part); return err; }

            try
            {
                long len = new FileInfo(part).Length;
                if (u.Size > 0 && len != u.Size) { TryDelete(part); return "下载不完整：" + len + " / " + u.Size + " 字节"; }
                if (u.Sha.Length > 0)
                {
                    string got = Sha256(part);
                    if (!string.Equals(got, u.Sha, StringComparison.OrdinalIgnoreCase))
                    { TryDelete(part); return "校验不过：文件的 sha256 和发布信息对不上。"; }
                }

                string old = self + ".old";
                TryDelete(old);
                try { File.Move(self, old); }
                catch (Exception ex)
                {
                    return "没有权限替换正在运行的程序（" + ex.Message + "）。\r\n\r\n"
                         + "新版已下到：" + part + "\r\n可以手动覆盖到：" + self;
                }
                try { File.Move(part, self); }
                catch (Exception ex)
                {
                    try { File.Move(old, self); } catch { }
                    return "替换失败，已还原旧版：" + ex.Message;
                }
                return null;
            }
            catch (Exception ex) { return "更新失败：" + ex.Message; }
        }

        /// <summary>重启到新版本（换完 exe 才调）。</summary>
        internal static bool RestartSelf()
        {
            try { Process.Start(Application.ExecutablePath, "--updated"); return true; }
            catch (Exception ex) { Core.Log("FAIL", "更新后重启失败: " + ex.Message); return false; }
        }

        /// <summary>上一个版本留下的 exe.old：启动时顺手删，删不掉（还被占用）就等下次。</summary>
        internal static void CleanupOld()
        {
            try
            {
                string old = Application.ExecutablePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
        }

        // ---------------------------------------------------------------- 底层

        private static string Get(string url, int timeoutMs, bool api)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "L4D2ModManager/" + Current;
                req.Accept = api ? "application/vnd.github+json" : "*/*";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (Exception ex) { Core.Log("INFO", "更新检查失败（" + url + "）：" + ex.Message); return null; }
        }

        private static string Download(string apiUrl, string url, string to, Action<int> pct)
        {
            if (!string.IsNullOrEmpty(apiUrl))
            {
                string e0 = DownloadOne(apiUrl, to, pct, true);
                if (e0 == null) return null;
                TryDelete(to);
                Core.Log("INFO", "更新：API 资产地址下载失败，改用直链重试（" + e0 + "）");
            }
            return DownloadOne(url, to, pct, false);
        }

        private static string DownloadOne(string url, string to, Action<int> pct, bool api)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                WebRequest req = WebRequest.Create(url);
                req.Timeout = 30000;
                HttpWebRequest hr = req as HttpWebRequest;
                if (hr != null)
                {
                    hr.ReadWriteTimeout = 60000;
                    hr.UserAgent = "L4D2ModManager/" + Current;
                    // API 资产地址必须要求二进制，不然返回的是 JSON 元数据
                    if (api) hr.Accept = "application/octet-stream";
                    hr.KeepAlive = false;
                }
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (FileStream f = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    long total = resp.ContentLength;
                    byte[] buf = new byte[65536];
                    long got = 0;
                    int lastPct = -1;
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        f.Write(buf, 0, n);
                        got += n;
                        if (pct != null && total > 0)
                        {
                            int p = (int)Math.Min(100, got * 100 / total);
                            if (p != lastPct) { lastPct = p; pct(p); }
                        }
                    }
                }
                return null;
            }
            catch (Exception ex) { return "下载失败：" + ex.Message; }
        }

        private static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream f = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(f);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string Rx(string s, string pat)
        {
            Match m = Regex.Match(s ?? "", pat);
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>同一段文本里取**最后一个**命中（窗口里最后的 name 才是这个资产的）。</summary>
        private static string Last(string s, string pat)
        {
            Match found = null;
            foreach (Match m in Regex.Matches(s ?? "", pat)) found = m;
            return found == null ? "" : found.Groups[1].Value;
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "  ").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static string San(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c) || c == '.') sb.Append(c);
            return sb.Length == 0 ? "new" : sb.ToString();
        }

        private static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>测试用：把一个本地 exe 当成「新版本」，走同一套替换流程（不联网）。</summary>
        internal static UpdateInfo FromFile(string path, string version)
        {
            FileInfo fi = new FileInfo(path);
            UpdateInfo u = new UpdateInfo();
            u.Tag = "v" + version;
            u.Version = version;
            u.Url = path;
            u.PageUrl = "";
            u.Size = fi.Length;
            u.Sha = Sha256(path);
            return u;
        }
    }
}
