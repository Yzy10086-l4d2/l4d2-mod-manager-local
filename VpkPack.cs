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

        private sealed class Ent
        {
            public string Ext = "", Path = "", Name = "";
            public uint Crc, Offset, Length;
            public int Preload;
            public int Archive;
        }

        static string ReadStr(byte[] b, ref int p)
        {
            int s = p;
            while (p < b.Length && b[p] != 0) p++;
            string v = Encoding.ASCII.GetString(b, s, Math.Max(0, p - s));
            if (p < b.Length) p++;
            return v;
        }

        /// <summary>
        /// 往现成的 vpk 里塞一张封面（addonimage.jpg），不额外留同名图片文件。
        /// 做法是把原 vpk 拆开重写：目录表照抄、数据段照搬，只多一条 jpg 记录。
        /// 只处理单文件 v1/v2（L4D2 的工坊包基本都是），多卷或带 preload 的直接放弃，交给调用方兜底。
        /// </summary>
        public static bool InjectImage(string vpkPath, byte[] jpg)
        {
            if (!File.Exists(vpkPath) || jpg == null || jpg.Length < 64) return false;
            try
            {
                byte[] all = File.ReadAllBytes(vpkPath);
                if (all.Length < 12) return false;
                uint sig = BitConverter.ToUInt32(all, 0);
                uint ver = BitConverter.ToUInt32(all, 4);
                uint tree = BitConverter.ToUInt32(all, 8);
                if (sig != 0x55AA1234 || tree == 0) return false;
                int hdr = 12 + (ver == 2 ? 16 : 0);
                int treeEnd = hdr + (int)tree;
                if (treeEnd > all.Length) return false;

                List<Ent> ents = new List<Ent>();
                int p = hdr;
                while (p + 1 < treeEnd && !(all[p] == 0xFF && all[p + 1] == 0xFF))
                {
                    string ext = ReadStr(all, ref p);
                    while (p + 1 < treeEnd && !(all[p] == 0xFF && all[p + 1] == 0xFF))
                    {
                        string dir = ReadStr(all, ref p);
                        while (p + 1 < treeEnd && !(all[p] == 0xFF && all[p + 1] == 0xFF))
                        {
                            Ent e = new Ent();
                            e.Ext = ext; e.Path = dir; e.Name = ReadStr(all, ref p);
                            if (p + 16 > all.Length) return false;
                            e.Crc = BitConverter.ToUInt32(all, p); p += 4;
                            e.Preload = BitConverter.ToUInt16(all, p); p += 2;
                            e.Archive = BitConverter.ToUInt16(all, p); p += 2;
                            e.Offset = BitConverter.ToUInt32(all, p); p += 4;
                            e.Length = BitConverter.ToUInt32(all, p); p += 4;
                            ents.Add(e);
                        }
                        p += 2;
                    }
                    p += 2;
                }

                if (ver != 1) return false;          // v2 头结构不同，不去动它
                // 实测大量现成 vpk 是多卷（数据在 _001.vpk 等分卷里）或带 preload，
                // 这种包重写极易写坏 —— 宁可退回旁边放图，也不冒险动用户 20MB 的包。
                foreach (Ent e in ents)
                    if (e.Archive != 0x7FFF || e.Preload != 0) return false;

                // preload 区紧跟在目录表后面，数据区起点 = treeEnd + 所有 preload 之和
                long totalPreload = 0;
                foreach (Ent e in ents) totalPreload += e.Preload;
                if (treeEnd + totalPreload > all.Length) return false;

                // 旧封面在目录表里就先摘掉，新封面追加到文件尾部（不动别的条目一个字节）
                ents.RemoveAll(delegate(Ent e) { return e.Ext == "jpg" && e.Path.Length == 0 && string.Equals(e.Name, "addonimage", StringComparison.OrdinalIgnoreCase); });

                Ent img = new Ent();
                img.Ext = "jpg"; img.Path = ""; img.Name = "addonimage";
                img.Crc = Crc(jpg);
                img.Archive = 0x7FFF;
                img.Preload = 0;
                img.Offset = (uint)Math.Max(0, (all.Length - treeEnd) - totalPreload);
                img.Length = (uint)jpg.Length;
                ents.Add(img);

                // 按扩展名 → 路径 → 名称排序后写树（原有条目的字段原样照抄）
                ents.Sort(delegate(Ent x, Ent y)
                {
                    int c = string.Compare(x.Ext, y.Ext, StringComparison.Ordinal); if (c != 0) return c;
                    c = string.Compare(x.Path, y.Path, StringComparison.Ordinal); if (c != 0) return c;
                    return string.Compare(x.Name, y.Name, StringComparison.Ordinal);
                });

                MemoryStream t = new MemoryStream();
                string curExt = null, curPath = null;
                foreach (Ent e in ents)
                {
                    if (e.Ext != curExt)
                    {
                        if (curExt != null) { WriteU16(t, 0xFFFF); }
                        WriteStr(t, e.Ext); curExt = e.Ext; curPath = null;
                    }
                    if (e.Path != curPath)
                    {
                        if (curPath != null) { WriteU16(t, 0xFFFF); }
                        WriteStr(t, e.Path); curPath = e.Path;
                    }
                    WriteStr(t, e.Name);
                    WriteU32(t, e.Crc); WriteU16(t, e.Preload); WriteU16(t, e.Archive);
                    WriteU32(t, e.Offset); WriteU32(t, e.Length);
                }
                WriteU16(t, 0xFFFF);
                WriteU16(t, 0xFFFF);
                WriteU16(t, 0xFFFF);

                string tmp = vpkPath + ".tmpimg";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    WriteU32(fs, 0x55AA1234);
                    WriteU32(fs, 1);
                    WriteU32(fs, (uint)t.Length);
                    t.WriteTo(fs);
                    fs.Write(all, treeEnd, all.Length - treeEnd);   // 原有 preload + 数据区，原样照搬
                    fs.Write(jpg, 0, jpg.Length);                   // 新封面追加在最后
                }
                File.Delete(vpkPath);
                File.Move(tmp, vpkPath);
                return true;
            }
            catch { return false; }
        }

        static uint Crc2(byte[] b, int start, int len)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < len; i++) c = Table[(c ^ b[start + i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
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
