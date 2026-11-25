using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Installer
{
    /// <summary>包级资源类型（第 3 刀：类型层独立）。只是标签，不参与分类。</summary>
    internal enum PackageKind
    {
        Unknown = 0,
        ModelBearing,   // 有模型（.mdl）
        EffectsOnly,    // C1.6 口径的纯特效：无模型/无主体音效/无 HUD 图标/无 bsp，且有粒子引用证据
        SoundOnly,      // 没有模型与粒子证据，资源主体是音效
        TextureOnly,    // 没有模型与音效，资源主体是材质/贴图
        ScriptOnly      // 只有脚本/配置类资源
    }

    // ============================================================
    //  第 3 刀：类型层独立
    //  - 输入 vpk 路径，输出 PackageKind；结果只写 ModItem.AssetKind，分类流程不读
    //  - 依据只有两样：VPK 目录表 + 已有只读内容能力（VMT 的 $basetexture、PCF 可打印串里的材质名）
    //  - EffectsOnly 的最终判定委托 EffectsVeto.Decide（C1.6 唯一判定实现），这里不复制规则
    //  - 全程只读：不写缓存、不改配置、不动用户数据；任何异常都回 Unknown
    // ============================================================
    internal static class AssetType
    {
        // 与 EffectsVeto 同一组读取上限：判定同源，读到的证据也必须同源
        private const int MaxTreeBytes = 32 * 1024 * 1024;
        private const int MaxVmtBytes = 16 * 1024;
        private const int PcfSlice = 8192;
        private const int MaxVmtRead = 300;
        private const int MaxPcfRead = 200;

        private sealed class Entry
        {
            public string Path; public byte[] Preload; public ushort Arch; public uint Offset; public uint Length;
        }

        /// <summary>判定一个 vpk 的包级资源类型；读不出、坏包、格式不认识的都回 Unknown，不抛异常。</summary>
        public static PackageKind Detect(string vpkFile)
        {
            try { return DetectCore(vpkFile); }
            catch { return PackageKind.Unknown; }
        }

        /// <summary>写进 ModItem.AssetKind 的字符串形式（枚举名）。</summary>
        public static string KindName(string vpkFile)
        {
            return Detect(vpkFile).ToString();
        }

        private static PackageKind DetectCore(string vpkFile)
        {
            if (string.IsNullOrEmpty(vpkFile) || !vpkFile.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) return PackageKind.Unknown;

            byte[] tree; long dataStart;
            if (!ReadTree(vpkFile, out tree, out dataStart)) return PackageKind.Unknown;
            List<Entry> ents = ParseTree(tree);
            if (ents.Count == 0) return PackageKind.Unknown;

            int model = 0, vmt = 0, bsp = 0, vtf = 0, sound = 0, script = 0;
            bool hud = false, snd = false;
            List<Entry> vmts = new List<Entry>();
            List<Entry> pcfs = new List<Entry>();
            foreach (Entry e in ents)
            {
                string ext = ExtOf(e.Path);
                if (ext == "mdl") model++;
                else if (ext == "vmt") { vmt++; if (vmts.Count < MaxVmtRead) vmts.Add(e); }
                else if (ext == "pcf") { if (pcfs.Count < MaxPcfRead) pcfs.Add(e); }
                else if (ext == "bsp") bsp++;
                else if (ext == "vtf") vtf++;
                else if (ext == "wav" || ext == "mp3" || ext == "ogg") { sound++; if (IsSubjectSound(e.Path)) snd = true; }
                else if (IsScriptPath(e.Path, ext)) script++;
                if (e.Path.StartsWith("materials/vgui/hud/icon_", StringComparison.Ordinal)) hud = true;
            }

            // 纯特效只可能在「没有主体证据、且有 VMT」时成立；其余组合连内容都不用读。
            // 这里的短路条件与 EffectsVeto.Decide 的 return false 分支一一对应，判定结果同源。
            if (model == 0 && !hud && !snd && bsp == 0 && vmt > 0)
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Entry pe in pcfs) CollectNames(vpkFile, pe, dataStart, names);

                int vmtRead = 0, pcfRef = 0, particleBase = 0;
                foreach (Entry ve in vmts)
                {
                    byte[] raw = ReadEntry(vpkFile, ve, dataStart, MaxVmtBytes);
                    if (raw == null) continue;
                    vmtRead++;
                    if (names.Contains(StemOf(ve.Path))) pcfRef++;
                    if (IsParticlePath(VmtBaseTexture(raw))) particleBase++;
                }
                if (EffectsVeto.Decide(vmtRead, model, hud, snd, bsp, pcfRef, particleBase)) return PackageKind.EffectsOnly;
            }

            // 单一标签：按 模型 → 音效 → 材质 → 脚本 取第一个命中的类型。
            // 模型包同时带音效/材质时仍算 ModelBearing（模型是主体）。
            if (model > 0) return PackageKind.ModelBearing;
            if (sound > 0) return PackageKind.SoundOnly;
            if (vtf > 0 || vmt > 0) return PackageKind.TextureOnly;
            if (script > 0) return PackageKind.ScriptOnly;
            return PackageKind.Unknown;
        }

        // ---- 目录表 / 内容读取（只读；解析逻辑与 C1.6 的 Shadow / 正式扫描一致）----

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

        /// <summary>脚本/配置类资源：扩展名，或 scripts/ / resource/ / missions/ / cfg/ 目录。addoninfo.txt 只是元数据，不算。</summary>
        private static bool IsScriptPath(string p, string ext)
        {
            if (p == "addoninfo.txt") return false;
            if (ext == "nut" || ext == "cfg" || ext == "res" || ext == "vts" || ext == "vdf") return true;
            return p.StartsWith("scripts/", StringComparison.Ordinal)
                || p.StartsWith("resource/", StringComparison.Ordinal)
                || p.StartsWith("missions/", StringComparison.Ordinal)
                || p.StartsWith("cfg/", StringComparison.Ordinal);
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
