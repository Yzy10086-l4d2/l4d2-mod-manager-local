using System;
using System.IO;

namespace Installer
{
    /// <summary>
    /// 受保护根目录下的路径校验（SEC-03）。
    ///
    /// 为什么不能只用 Core.IsUnder：它只把两个字符串规范化再比前缀。
    /// 一是认不出 root2 这种"前缀一样的兄弟目录"（少了分隔符边界），
    /// 二是认不出目录里的链接（Junction / Symlink）—— 链接指向根目录外时，
    /// 前缀判断照样通过，写下去就落到根目录外面了。
    ///
    /// 这里只做三件事：规范化、逐级查链接、按分隔符边界比对。
    /// 调用方看 Ok 决定能不能动盘，Full 是唯一认可的完整路径。
    /// </summary>
    public static class SafePath
    {
        /// <summary>一次校验的结果。Ok=false 时 Why 是给人看的原因，Full 为空。</summary>
        public struct Check
        {
            public bool Ok;
            public string Full;
            public string Why;
        }

        static Check Fail(string why)
        {
            Check c = new Check();
            c.Ok = false;
            c.Full = null;
            c.Why = why;
            return c;
        }

        static Check Pass(string full)
        {
            Check c = new Check();
            c.Ok = true;
            c.Full = full;
            c.Why = null;
            return c;
        }

        /// <summary>
        /// 根目录 + 相对路径 → 完整路径。目标还不存在时用这个（安装、解包的落点）。
        /// 相对路径里不许出现绝对路径、盘符、UNC、. 或 ..、非法字符、保留名。
        /// </summary>
        public static Check Resolve(string root, string relative)
        {
            Check c;
            string rootFull;
            if (!TryRoot(root, out rootFull, out c)) return c;
            if (string.IsNullOrEmpty(relative)) return Fail("路径为空");
            if (relative.IndexOf('\0') >= 0) return Fail("路径里有空字符");
            if (Path.IsPathRooted(relative) || relative.StartsWith("\\", StringComparison.Ordinal))
                return Fail("不接受绝对路径或网络路径：" + relative);

            string[] segs = relative.Split(new char[] { '\\', '/' }, StringSplitOptions.None);
            foreach (string s in segs)
            {
                string bad = BadName(s);
                if (bad != null) return Fail(bad + "：" + s);
            }

            string full;
            try { full = Path.GetFullPath(Path.Combine(rootFull, relative)); }
            catch (Exception ex) { return Fail("路径不合法：" + ex.Message); }
            if (!Under(rootFull, full)) return Fail("落到根目录外：" + full);

            string link = FirstLink(rootFull, full);
            if (link != null) return Fail("路径里穿过了链接/联接点：" + link);
            return Pass(full);
        }

        /// <summary>
        /// 已经存在的目标（或者调用方自己拼好的完整路径）→ 校验它确实在 root 内，且中间没有链接。
        /// 删除、移动、覆盖写这种"目标已经在盘上"的操作走这个。
        /// </summary>
        public static Check Validate(string root, string target)
        {
            Check c;
            string rootFull;
            if (!TryRoot(root, out rootFull, out c)) return c;
            if (string.IsNullOrEmpty(target)) return Fail("路径为空");
            if (target.IndexOf('\0') >= 0) return Fail("路径里有空字符");

            string full;
            try { full = Path.GetFullPath(target); }
            catch (Exception ex) { return Fail("路径不合法：" + ex.Message); }
            if (!Under(rootFull, full)) return Fail("不在根目录内：" + full);

            string link = FirstLink(rootFull, full);
            if (link != null) return Fail("路径里穿过了链接/联接点：" + link);
            return Pass(full);
        }

        /// <summary>这个路径本身是不是链接/联接点。读不到属性一律当"不是"，由调用方的边界判断兜底。</summary>
        public static bool IsLink(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string p = path.TrimEnd('\\');
                if (p.Length == 0) return false;
                if (!File.Exists(p) && !Directory.Exists(p)) return false;
                FileAttributes a = File.GetAttributes(p);
                return (a & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch { return false; }
        }

        /// <summary>单个路径段不合法时返回原因，合法返回 null。</summary>
        public static string BadName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "空路径段";
            if (name == "." || name == "..") return "不接受 . 或 ..";
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (ch < 32) return "名字里有控制字符";
                if ("<>:\"|?*".IndexOf(ch) >= 0) return "名字里有非法字符（" + ch + "）";
            }
            if (name[name.Length - 1] == '.' || name[name.Length - 1] == ' ') return "名字以点或空格结尾";
            if (IsReservedName(name)) return "是 Windows 保留名";
            return null;
        }

        static bool IsReservedName(string name)
        {
            string stem = name;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            stem = stem.Trim().ToUpperInvariant();
            if (stem.Length == 0) return false;
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return true;
            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] >= '1' && stem[3] <= '9') return true;
            return false;
        }

        static bool TryRoot(string root, out string rootFull, out Check fail)
        {
            rootFull = null;
            fail = Fail("根目录为空");
            if (string.IsNullOrEmpty(root)) return false;
            try { rootFull = Path.GetFullPath(root).TrimEnd('\\'); }
            catch (Exception ex) { fail = Fail("根目录不合法：" + ex.Message); return false; }
            if (!Directory.Exists(rootFull)) { fail = Fail("根目录不存在：" + rootFull); return false; }
            fail = default(Check);
            return true;
        }

        /// <summary>完整路径是否在根目录下面。必须按分隔符边界比，root2 不算在 root 里。</summary>
        static bool Under(string rootFull, string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            string r = rootFull.EndsWith("\\", StringComparison.Ordinal) ? rootFull : rootFull + "\\";
            if (full.Length <= r.Length) return false;
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>根目录到目标之间，第一个存在的链接（没有返回 null）。</summary>
        static string FirstLink(string rootFull, string full)
        {
            string rest = full.Substring(rootFull.Length).TrimStart('\\');
            if (rest.Length == 0) return null;
            string cur = rootFull;
            foreach (string seg in rest.Split('\\'))
            {
                if (seg.Length == 0) continue;
                cur = Path.Combine(cur, seg);
                if (IsLink(cur)) return cur;
            }
            return null;
        }
    }
}
