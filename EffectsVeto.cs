using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Installer
{
    // ============================================================
    //  EffectsOnly 类型否决：C1.6 唯一获准接入正式分类的能力
    //  - Decide 是唯一判定实现，Shadow 与正式流程共用
    //  - 默认关闭；关闭时正式分类不进入任何额外扫描
    //  - 只读，不写缓存、不改用户数据
    // ============================================================
    internal static class EffectsVeto
    {
        private const int MaxTreeBytes = 32 * 1024 * 1024;
        private const int MaxVmtBytes = 16 * 1024;
        private const int PcfSlice = 8192;
        private const int MaxVmtRead = 300;
        private const int MaxPcfRead = 200;

        public static bool Enabled
        {
            get { try { return Core.Cfg != null && Core.Cfg.EffectsOnlyVeto; } catch { return false; } }
        }

        // 唯一判定：参数与 C1.6 Shadow 完全一致
        internal static bool Decide(int vmtRead, int model, bool hudIcon, bool subjectSound, int bsp, int pcfRef, int particleBase)
        {
            if (vmtRead <= 0) return false;
            if (model > 0 || hudIcon || subjectSound || bsp > 0) return false;
            return pcfRef >= 1 || (particleBase * 2 >= vmtRead);
        }

        public static bool ShouldVeto(string vpkFile, out string reason)
        {
            reason = "";
            if (!Enabled) return false;
            return ShouldVetoCore(vpkFile, out reason);
        }

        // 供 Shadow 做一致性对比：不走开关，只跑判定
        internal static bool ShouldVetoCore(string vpkFile, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(vpkFile) || !vpkFile.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                byte[] tree; long dataStart;
                if (!ReadTree(vpkFile, out tree, out dataStart)) { reason = "目录表读不出"; return false; }
                List<Entry> ents = ParseTree(tree);
                if (ents.Count == 0) { reason = "无条目"; return false; }

                int model = 0, vmt = 0, pcf = 0, bsp = 0;
                bool hud = false, snd = false;
                List<Entry> vmts = new List<Entry>();
                List<Entry> pcfs = new List<Entry>();
                foreach (Entry e in ents)
                {
                    string ext = ExtOf(e.Path);
                    if (ext == "mdl") model++;
                    else if (ext == "vmt") { vmt++; if (vmts.Count < MaxVmtRead) vmts.Add(e); }
                    else if (ext == "pcf") { pcf++; if (pcfs.Count < MaxPcfRead) pcfs.Add(e); }
                    else if (ext == "bsp") bsp++;
                    else if (ext == "wav" || ext == "mp3") { if (IsSubjectSound(e.Path)) snd = true; }
                    if (e.Path.StartsWith("materials/vgui/hud/icon_", StringComparison.Ordinal)) hud = true;
                }

                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Entry pe in pcfs) CollectNames(vpkFile, pe, dataStart, names);

                int vmtRead = 0, pcfRef = 0, particleBase = 0;
                foreach (Entry ve in vmts)
                {
                    byte[] raw = ReadEntry(vpkFile, ve, dataStart, MaxVmtBytes);
                    if (raw == null) continue;
                    vmtRead++;
                    string stem = StemOf(ve.Path);
                    if (names.Contains(stem)) pcfRef++;
                    if (IsParticlePath(VmtBaseTexture(raw))) particleBase++;
                }
                if (!Decide(vmtRead, model, hud, snd, bsp, pcfRef, particleBase)) return false;
                reason = "vmt=" + vmtRead + " pcfRef=" + pcfRef + " particleBase=" + particleBase + " model=" + model;
                return true;
            }
            catch { reason = "异常"; return false; }
        }

        private sealed class Entry
        {
            public string Path; public byte[] Preload; public ushort Arch; public uint Offset; public uint Length;
        }

        private static bool ReadTree(string file, out byte[] tree, out long dataStart)
        {
            tree = null; dataStart = 0;
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (fs.Length < 12) return false;
                    if (br.ReadUInt32() != 0x55AA1234u) return false;
                    uint ver = br.ReadUInt32();
                    int size = br.ReadInt32();
                    if (size <= 0 || size > MaxTreeBytes) return false;
                    if (ver == 2) { if (fs.Length < 28) return false; br.ReadBytes(16); }
                    if (fs.Length - fs.Position < size) return false;
                    tree = br.ReadBytes(size);
                    dataStart = fs.Position;
                    return tree != null && tree.Length == size;
                }
            }
            catch { return false; }
        }

        private static string ReadStr(byte[] t, ref int i)
        {
            int start = i;
            while (i < t.Length && t[i] != 0) i++;
            int len = i - start;
            if (i < t.Length) i++;
            if (len > 512) return "";
            return Encoding.UTF8.GetString(t, start, len);
        }

        private static List<Entry> ParseTree(byte[] t)
        {
            List<Entry> list = new List<Entry>();
            int i = 0;
            while (i < t.Length)
            {
                string ext = ReadStr(t, ref i);
                if (ext.Length == 0) break;
                while (true)
                {
                    string path = ReadStr(t, ref i);
                    if (path.Length == 0) break;
                    while (true)
                    {
                        string name = ReadStr(t, ref i);
                        if (name.Length == 0) break;
                        if (i + 18 > t.Length) return list;
                        ushort pre = BitConverter.ToUInt16(t, i + 4);
                        ushort ai = BitConverter.ToUInt16(t, i + 6);
                        uint off = BitConverter.ToUInt32(t, i + 8);
                        uint len = BitConverter.ToUInt32(t, i + 12);
                        i += 18;
                        Entry e = new Entry();
                        e.Arch = ai; e.Offset = off; e.Length = len;
                        e.Path = ((path.Length > 0 ? path + "/" : "") + name + "." + ext).ToLowerInvariant();
                        if (pre > 0)
                        {
                            int take = Math.Min(pre, t.Length - i);
                            e.Preload = new byte[take];
                            Array.Copy(t, i, e.Preload, 0, take);
                            i += pre;
                        }
                        list.Add(e);
                    }
                }
            }
            return list;
        }

        private static byte[] ReadEntry(string vpk, Entry e, long dataStart, int limit)
        {
            try
            {
                if (e.Preload != null) return e.Preload;
                string target = vpk;
                long at = dataStart + e.Offset;
                if (e.Arch != 0x7FFF)
                {
                    string baseName = vpk.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase) ? vpk.Substring(0, vpk.Length - 8) : vpk.Substring(0, vpk.Length - 4);
                    target = baseName + "_" + e.Arch.ToString("000") + ".vpk";
                    at = e.Offset;
                }
                using (FileStream fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (at >= fs.Length) return null;
                    int want = (int)Math.Min(Math.Min((long)limit, (long)e.Length), fs.Length - at);
                    if (want <= 0) return null;
                    fs.Seek(at, SeekOrigin.Begin);
                    byte[] b = new byte[want];
                    int got = 0;
                    while (got < want) { int r = fs.Read(b, got, want - got); if (r <= 0) break; got += r; }
                    return got == want ? b : null;
                }
            }
            catch { return null; }
        }

        private static byte[] ReadEntryAt(string vpk, Entry e, long dataStart, int off, int count)
        {
            try
            {
                if (e.Preload != null) return e.Preload;
                long at = dataStart + e.Offset + off;
                string target = vpk;
                if (e.Arch != 0x7FFF)
                {
                    string baseName = vpk.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase) ? vpk.Substring(0, vpk.Length - 8) : vpk.Substring(0, vpk.Length - 4);
                    target = baseName + "_" + e.Arch.ToString("000") + ".vpk";
                    at = e.Offset + off;
                }
                using (FileStream fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (at >= fs.Length) return null;
                    int want = (int)Math.Min((long)count, fs.Length - at);
                    fs.Seek(at, SeekOrigin.Begin);
                    byte[] b = new byte[want];
                    int got = 0;
                    while (got < want) { int r = fs.Read(b, got, want - got); if (r <= 0) break; got += r; }
                    return got == want ? b : null;
                }
            }
            catch { return null; }
        }

        private static void CollectNames(string vpk, Entry pe, long dataStart, HashSet<string> set)
        {
            byte[] head = ReadEntry(vpk, pe, dataStart, PcfSlice);
            if (head != null) ScanStrings(head, set);
            if (pe.Length > (uint)(PcfSlice * 2))
            {
                byte[] tail = ReadEntryAt(vpk, pe, dataStart, (int)pe.Length - PcfSlice, PcfSlice);
                if (tail != null) ScanStrings(tail, set);
            }
        }

        private static void ScanStrings(byte[] raw, HashSet<string> set)
        {
            int i = 0;
            while (i < raw.Length && set.Count < 20000)
            {
                if (raw[i] >= 32 && raw[i] < 127)
                {
                    int s = i;
                    while (i < raw.Length && raw[i] >= 32 && raw[i] < 127) i++;
                    int len = i - s;
                    if (len >= 4 && len <= 200)
                    {
                        string t = Encoding.ASCII.GetString(raw, s, len).ToLowerInvariant().Replace('\\', '/');
                        int slash = t.LastIndexOf('/');
                        string tail = slash >= 0 ? t.Substring(slash + 1) : t;
                        int dot = tail.LastIndexOf('.');
                        if (dot > 0) tail = tail.Substring(0, dot);
                        if (tail.Length >= 3) set.Add(tail);
                    }
                }
                else i++;
            }
        }

        private static string VmtBaseTexture(byte[] raw)
        {
            string s = Encoding.ASCII.GetString(raw);
            int i = s.IndexOf("$basetexture", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            i += 12;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '"' || s[i] == '\r' || s[i] == '\n')) i++;
            int st = i;
            while (i < s.Length && s[i] != '"' && s[i] != '\r' && s[i] != '\n' && s[i] != ' ' && s[i] != '\t') i++;
            return i > st ? s.Substring(st, i - st) : "";
        }

        private static bool IsParticlePath(string basetex)
        {
            if (string.IsNullOrEmpty(basetex)) return false;
            string s = basetex.Replace('\\', '/').ToLowerInvariant().TrimStart('/');
            if (s.StartsWith("materials/", StringComparison.Ordinal)) s = s.Substring(10);
            return s.StartsWith("particle/", StringComparison.Ordinal);
        }

        private static bool IsSubjectSound(string path)
        {
            return path.StartsWith("sound/weapons/", StringComparison.Ordinal)
                || path.StartsWith("sound/player/", StringComparison.Ordinal)
                || path.StartsWith("sound/npc/", StringComparison.Ordinal)
                || path.StartsWith("sound/zombie/", StringComparison.Ordinal)
                || path.StartsWith("sound/horde", StringComparison.Ordinal);
        }

        private static string ExtOf(string p) { int d = p.LastIndexOf('.'); return d < 0 ? "" : p.Substring(d + 1); }

        private static string StemOf(string p)
        {
            int s = p.LastIndexOf('/');
            string f = s >= 0 ? p.Substring(s + 1) : p;
            int d = f.LastIndexOf('.');
            return (d < 0 ? f : f.Substring(0, d)).ToLowerInvariant();
        }
    }
}