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

        static bool Wanted(string rel)
        {
            string low = rel.ToLowerInvariant();
            foreach (string r in Roots) if (low.StartsWith(r, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>把 srcDir 里的散文件打成 outPath。返回打进包的文件数；0 = 没有可用的游戏资源。</summary>
        public static int PackDir(string srcDir, string outPath)
        {
            if (!Directory.Exists(srcDir)) return 0;
            string root = srcDir.TrimEnd('\\') + "\\";

            List<string> files = new List<string>();
            foreach (string full in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = full.Substring(root.Length).Replace('\\', '/');
                if (Wanted(rel)) files.Add(rel);
            }
            if (files.Count == 0) return 0;
            files.Sort(StringComparer.OrdinalIgnoreCase);

            SortedDictionary<string, SortedDictionary<string, SortedDictionary<string, long[]>>> tree =
                new SortedDictionary<string, SortedDictionary<string, SortedDictionary<string, long[]>>>(StringComparer.Ordinal);
            MemoryStream data = new MemoryStream();
            foreach (string rel in files)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(root, rel));
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
