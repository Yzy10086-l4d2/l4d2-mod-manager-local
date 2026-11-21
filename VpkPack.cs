using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Installer
{
    /// <summary>
    /// 把一堆散文件打成 VPK（v1、单文件、数据段同文件）。
    /// GameBanana 上很多条目是 materials/、models/ 这种散文件包，addons 只认 vpk，
    /// 不打这一层，下下来游戏也不认。
    /// </summary>
    internal static class VpkPack
    {
        /// <summary>只收这些顶层目录里的文件；readme、截图之类不进包。</summary>
        static readonly string[] Roots = new string[]
        {
            "materials/", "models/", "sound/", "scripts/", "particles/", "resource/", "scenes/", "missions/", "cfg/"
        };

        static readonly uint[] Table = BuildTable();

        static uint[] BuildTable()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }

        static uint Crc(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++) c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        /// <summary>
        /// 把「外面套了一层」的路径剥掉：left4dead2/materials/... → materials/...，addons/xxx/models/... → models/...。
        /// 非 ASCII 名字直接放弃：vpk 目录表是 ASCII，写进去游戏也认不出。
        /// </summary>
        static string NormalizeRel(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            foreach (char c in rel) if (c > 127) return null;
            string low = rel.ToLowerInvariant();
            foreach (string r in Roots)
            {
                int at = low.IndexOf("/" + r, StringComparison.Ordinal);
                if (at >= 0) return rel.Substring(at + 1);
                if (low.StartsWith(r, StringComparison.Ordinal)) return rel;
            }
            return null;
        }

        static bool Wanted(string rel)
        {
            string low = rel.ToLowerInvariant();
            foreach (string r in Roots) if (low.StartsWith(r, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>把 srcDir 里的散文件打成 outPath。返回打进包的文件数；0 = 没有可用的游戏资源。</summary>
        public static int PackDir(string srcDir, string outPath)
        {
            return PackDir(srcDir, outPath, null);
        }

        /// <summary>
        /// previewBytes 不为空时会作为根目录的 addonimage.jpg 打进包 —— 游戏自己的 addons 列表和我们的卡片都用它。
        /// </summary>
        public static int PackDir(string srcDir, string outPath, byte[] previewBytes)
        {
            if (!Directory.Exists(srcDir)) return 0;
            string root = srcDir.TrimEnd('\\') + "\\";

            List<string> fulls = new List<string>();
            List<string> files = new List<string>();
            foreach (string full in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = full.Substring(root.Length).Replace('\\', '/');
                rel = NormalizeRel(rel);          // 包内路径：剥掉 left4dead2/ 这类外壳
                if (rel == null) continue;
                if (!Wanted(rel)) continue;
                fulls.Add(full);                   // 读文件还得用磁盘上的真实路径
                files.Add(rel);
            }
            if (files.Count == 0) return 0;

            int[] order = new int[files.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, delegate(int x, int y) { return string.Compare(files[x], files[y], StringComparison.OrdinalIgnoreCase); });
            List<string> f2 = new List<string>(); List<string> u2 = new List<string>();
            foreach (int i in order) { f2.Add(files[i]); u2.Add(fulls[i]); }
            files = f2; fulls = u2;

            SortedDictionary<string, SortedDictionary<string, SortedDictionary<string, long[]>>> tree =
                new SortedDictionary<string, SortedDictionary<string, SortedDictionary<string, long[]>>>(StringComparer.Ordinal);
            MemoryStream data = new MemoryStream();
            if (previewBytes != null && previewBytes.Length > 64)
            {
                long[] pe = new long[] { Crc(previewBytes), data.Position, previewBytes.Length };
                data.Write(previewBytes, 0, previewBytes.Length);
                SortedDictionary<string, SortedDictionary<string, long[]>> jp;
                if (!tree.TryGetValue("jpg", out jp)) { jp = new SortedDictionary<string, SortedDictionary<string, long[]>>(StringComparer.Ordinal); tree["jpg"] = jp; }
                SortedDictionary<string, long[]> pn;
                if (!jp.TryGetValue("", out pn)) { pn = new SortedDictionary<string, long[]>(StringComparer.Ordinal); jp[""] = pn; }
                pn["addonimage"] = pe;
            }
            for (int fi = 0; fi < files.Count; fi++)
            {
                string rel = files[fi];
                byte[] bytes = File.ReadAllBytes(fulls[fi]);
                long[] e = new long[] { Crc(bytes), data.Position, bytes.Length };
                data.Write(bytes, 0, bytes.Length);

                int slash = rel.LastIndexOf('/');
                string dirPart = slash > 0 ? rel.Substring(0, slash) : "";
                string name = slash >= 0 ? rel.Substring(slash + 1) : rel;
                string ext = Path.GetExtension(name);
                if (ext.Length > 0) { name = name.Substring(0, name.Length - ext.Length); ext = ext.Substring(1); }
                else ext = " ";

                SortedDictionary<string, SortedDictionary<string, long[]>> byPath;
                if (!tree.TryGetValue(ext, out byPath)) { byPath = new SortedDictionary<string, SortedDictionary<string, long[]>>(StringComparer.Ordinal); tree[ext] = byPath; }
                SortedDictionary<string, long[]> byName;
                if (!byPath.TryGetValue(dirPart, out byName)) { byName = new SortedDictionary<string, long[]>(StringComparer.Ordinal); byPath[dirPart] = byName; }
                byName[name] = e;
            }

            MemoryStream t = new MemoryStream();
            foreach (KeyValuePair<string, SortedDictionary<string, SortedDictionary<string, long[]>>> ex in tree)
            {
                WriteStr(t, ex.Key);
                foreach (KeyValuePair<string, SortedDictionary<string, long[]>> p in ex.Value)
                {
                    WriteStr(t, p.Key);
                    foreach (KeyValuePair<string, long[]> n in p.Value)
                    {
                        WriteStr(t, n.Key);
                        WriteU32(t, (uint)n.Value[0]);
                        WriteU16(t, 0);
                        WriteU16(t, 0x7FFF);
                        WriteU32(t, (uint)n.Value[1]);
                        WriteU32(t, (uint)n.Value[2]);
                    }
                    WriteU16(t, 0xFFFF);
                }
                WriteU16(t, 0xFFFF);
            }
            WriteU16(t, 0xFFFF);

            using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            {
                WriteU32(fs, 0x55AA1234);
                WriteU32(fs, 1);
                WriteU32(fs, (uint)t.Length);
                t.WriteTo(fs);
                data.WriteTo(fs);
            }
            return files.Count;
        }

        static void WriteStr(Stream s, string v)
        {
            byte[] b = Encoding.ASCII.GetBytes(v ?? "");
            s.Write(b, 0, b.Length);
            s.WriteByte(0);
        }

        static void WriteU16(Stream s, int v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
        }

        static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 24) & 0xFF));
        }
    }
}
