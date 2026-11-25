using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Installer
{
    // ============================================================
    //  C1.4：结构化 Shadow 结果
    //  五块分离：资源类型 / 主体 / 影响面 / 归属 / 追溯
    //  只读、只写 Shadow 输出；不碰正式分类、缓存、配置。
    // ============================================================
    internal static class ShadowClassify
    {
        private const int MaxTreeBytes = 32 * 1024 * 1024;
        private const int MaxVmtBytes = 16 * 1024;
        private const int PcfSlice = 8192;
        private const int MaxVmtRead = 300;
        private const int MaxPcfRead = 200;
        private const int MaxEntries = 20000;
        private const int MaxDepth = 4;
        private const int MaxAssets = 600;

        private sealed class Entry
        {
            public string Path; public byte[] Preload; public ushort Arch; public uint Offset; public uint Length;
        }

        private sealed class Pkg
        {
            public string Path, Name, Dir;
            public int Model, Material, Texture, Particle, Sound, Map, Script, Other;
            public bool HudIcon, SubjectSound;
            public int VmtRead, PcfRead, PcfRef, ParticleBase, CustomBase;
            public readonly List<string> Affected = new List<string>();
            public readonly List<string> VmtStems = new List<string>();
            public readonly List<string> ModelStems = new List<string>();
            public readonly HashSet<string> PcfNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> AssetPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> OfficialNotes = new List<string>();
            public string Title = "", Author = "";
            public string Error = "";
        }

        private static Dictionary<string, string> sPos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private sealed class Rel
        {
            public string To, Type, Strength, Detail;
        }

        public static int Run(string root, string baseline, string outJson, string outTsv, string officialVpk, string posFile)
        {
            List<string> vpks = new List<string>();
            Collect(root, vpks, 0);
            vpks.Sort(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string[]> formal = LoadBaseline(baseline);
            HashSet<string> official = LoadOfficial(officialVpk);
            sPos = LoadPositions(posFile);

            int filteredLinks = 0;
            List<Pkg> pkgs = new List<Pkg>();
            for (int i = 0; i < vpks.Count; i++)
            {
                Pkg p = Scan(vpks[i]);
                if (p != null) pkgs.Add(p);
            }

            List<Rel>[] rels = BuildRelations(pkgs, official, out filteredLinks);

            string[] status = new string[pkgs.Count];
            for (int i = 0; i < pkgs.Count; i++) status[i] = Decide(pkgs, rels, i);

            // 与正式接入实现做一致性对比（只读）
            int vetoCore = 0, parityBad = 0;
            for (int i = 0; i < pkgs.Count; i++)
            {
                string rs;
                bool core = EffectsVeto.ShouldVetoCore(pkgs[i].Path, out rs);
                if (core) vetoCore++;
                if (core != IsEffectsOnly(pkgs[i])) parityBad++;
            }
            // JSONL
            StringBuilder js = new StringBuilder(1 << 20);
            for (int i = 0; i < pkgs.Count; i++)
                js.AppendLine(ToJson(pkgs[i], rels[i], status[i], formal, official));
            SafeWrite(outJson, js.ToString());

            // TSV 汇总
            StringBuilder sb = new StringBuilder(1 << 16);
            sb.AppendLine("包\t正式大类\t正式小类\t正式主体\t主体状态\t影响面\t归属状态\t关系数\t类型构成\t说明");
            int nComp = 0, nPoss = 0, nSame = 0, nDep = 0, nUnknown = 0, nMulti = 0;
            for (int i = 0; i < pkgs.Count; i++)
            {
                Pkg p = pkgs[i];
                string[] f = formal.ContainsKey(p.Name.ToLowerInvariant()) ? formal[p.Name.ToLowerInvariant()] : new string[] { "", "", "", "" };
                string st = status[i];
                if (st == "component") nComp++;
                else if (st == "possible-component") nPoss++;
                else if (st == "same-origin") nSame++;
                else if (st == "dependency") nDep++;
                else nUnknown++;
                if (Subjects(p, f, official).Count >= 2) nMulti++;
                string subjStatus = string.IsNullOrEmpty(f[2]) ? "unknown" : "identified";
                sb.Append(p.Name).Append('\t').Append(f[0]).Append('\t').Append(f[1]).Append('\t').Append(f[2]).Append('\t')
                  .Append(IsEffectsOnly(p) ? "effects-only" : subjStatus).Append('\t')
                  .Append(string.Join(",", p.Affected.ToArray())).Append('\t')
                  .Append(st).Append('\t').Append(rels[i].Count).Append('\t')
                  .Append("mdl" + p.Model + " vmt" + p.Material + " vtf" + p.Texture + " pcf" + p.Particle + " wav" + p.Sound).Append('\t')
                  .Append(p.Error).AppendLine();
            }
            sb.AppendLine();
            sb.AppendLine("# 汇总\t包=" + pkgs.Count + "\tcomponent=" + nComp + "\tpossible-component=" + nPoss + "\tsame-origin=" + nSame + "\tdependency=" + nDep + "\tunknown=" + nUnknown + "\t多主体=" + nMulti + "\t官方名被过滤=" + filteredLinks + "\t否决判定=" + vetoCore + "\t与正式实现不一致=" + parityBad);
            SafeWrite(outTsv, sb.ToString());
            return 0;
        }

        // ---------------------------------------------------------------- 关系

        private static List<Rel>[] BuildRelations(List<Pkg> ps, HashSet<string> official, out int filteredNames)
        {
            filteredNames = 0;
            List<Rel>[] rels = new List<Rel>[ps.Count];
            for (int i = 0; i < ps.Count; i++) rels[i] = new List<Rel>();

            // 名字频次：官方常用名 vs 少量出现的自定义名
            Dictionary<string, int> freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> dirs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Pkg p in ps)
                foreach (string n in p.Assets)
                {
                    int c; freq.TryGetValue(n, out c); freq[n] = c + 1;
                    HashSet<string> d;
                    if (!dirs.TryGetValue(n, out d)) { d = new HashSet<string>(StringComparer.OrdinalIgnoreCase); dirs[n] = d; }
                    d.Add(p.Dir);
                }

            // 材质名 → 包，供跨包直接引用
            Dictionary<string, List<int>> stemIndex = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ps.Count; i++)
                foreach (string s in ps[i].VmtStems)
                {
                    List<int> l;
                    if (!stemIndex.TryGetValue(s, out l)) { l = new List<int>(); stemIndex[s] = l; }
                    if (!l.Contains(i)) l.Add(i);
                }

            // 自定义名倒排，减少 O(n^2)
            Dictionary<string, List<int>> nameIndex = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ps.Count; i++)
                foreach (string n in ps[i].Assets)
                {
                    HashSet<string> dd; dirs.TryGetValue(n, out dd);
                    if (IsSlotShape(n)) continue;
                    if (dd != null && dd.Count > 1) continue;   // 跨目录出现的名字是通用名字，不做同源证据
                    List<int> l;
                    if (!nameIndex.TryGetValue(n, out l)) { l = new List<int>(); nameIndex[n] = l; }
                    if (!l.Contains(i)) l.Add(i);
                }
            HashSet<string> pairs = new HashSet<string>();
            foreach (KeyValuePair<string, List<int>> kv in nameIndex)
            {
                List<int> l = kv.Value;
                for (int a = 0; a < l.Count; a++)
                    for (int b = a + 1; b < l.Count; b++)
                        pairs.Add(Math.Min(l[a], l[b]) + ":" + Math.Max(l[a], l[b]));
            }

            // A：跨包直接引用（A 的 PCF 引用了 B 的材质名）
            for (int i = 0; i < ps.Count; i++)
            {
                Dictionary<int, int> hits = new Dictionary<int, int>();
                foreach (string nm in ps[i].PcfNames)
                {
                    List<int> owners;
                    if (!stemIndex.TryGetValue(nm, out owners)) continue;
                    int nf; freq.TryGetValue(nm, out nf);
                    if (official.Contains(nm)) { filteredNames++; continue; }   // 官方资源名：只做技术依赖之外的事实，不参与归属
                    if (nf != 1 || nm.Length < 8) continue;  // 只认“只由对方一个包提供”的长名；两包都带的通用材质名不算
                    foreach (int o in owners) { if (o == i) continue; int c; hits.TryGetValue(o, out c); hits[o] = c + 1; }
                }
                foreach (KeyValuePair<int, int> kv in hits)
                {
                    rels[i].Add(new Rel { To = ps[kv.Key].Name, Type = "dependency", Strength = "A", Detail = "本包 PCF 引用了对方 " + kv.Value + " 个材质名" });
                    bool dup = false;
                    foreach (Rel rr in rels[kv.Key]) if (rr.Strength == "A" && rr.To == ps[i].Name) { dup = true; break; }
                    if (!dup) rels[kv.Key].Add(new Rel { To = ps[i].Name, Type = "dependent", Strength = "A", Detail = "对方 PCF 引用本包 " + kv.Value + " 个材质名" });
                }
            }

            // B：共享自定义资源名（过滤官方常用名与槽位名）
            foreach (string key in pairs)
            {
                string[] ab = key.Split(':');
                int i = int.Parse(ab[0]), j = int.Parse(ab[1]);
                List<string> shared = new List<string>();
                foreach (string n in ps[i].Assets)
                    if (ps[j].Assets.Contains(n))
                    {
                        HashSet<string> d; dirs.TryGetValue(n, out d);
                        int nd = d == null ? 99 : d.Count;
                        string ap;
                        ps[i].AssetPath.TryGetValue(n, out ap);
                        bool pak = official.Contains(n) || official.Contains(n + ".vmt") || official.Contains(n + ".vtf");
                        bool dom = IsOfficialDomain(ap);
                        if (pak || dom)
                        {
                            filteredNames++;
                            if (ps[i].OfficialNotes.Count < 20)
                                ps[i].OfficialNotes.Add(n + "|pak01=" + (pak ? "1" : "0") + "|domain=" + (dom ? "1" : "0") + "|status=" + (pak && dom ? "official" : (pak ? "official-name" : "candidate-domain")));
                            continue;
                        }
                        if (nd <= 1 && !IsSlotShape(n)) shared.Add(n);   // 只认“只出现在同一目录”的名字：官方名会散落多处
                    }
                if (shared.Count == 0) continue;
                bool complementary = (ps[i].Model > 0) != (ps[j].Model > 0);
                bool authI = ps[i].VmtRead > 0 && ps[i].CustomBase * 2 >= ps[i].VmtRead;
                bool authJ = ps[j].VmtRead > 0 && ps[j].CustomBase * 2 >= ps[j].VmtRead;
                if (!(authI || authJ)) continue;   // 只是碰巧都覆盖同名官方材质：不算同源证据
                string type = complementary ? "possible-component" : "same-origin";
                rels[i].Add(new Rel { To = ps[j].Name, Type = type, Strength = "B", Detail = "共享自定义名 " + shared.Count + " 个，如 " + shared[0] });
                rels[j].Add(new Rel { To = ps[i].Name, Type = type, Strength = "B", Detail = "共享自定义名 " + shared.Count + " 个，如 " + shared[0] });
            }

            // C：同目录 + 名前缀；C-：同目录
            for (int i = 0; i < ps.Count; i++)
                for (int j = i + 1; j < ps.Count; j++)
                {
                    if (!string.Equals(ps[i].Dir, ps[j].Dir, StringComparison.OrdinalIgnoreCase)) continue;
                    int cp = CommonPrefix(ps[i].Name, ps[j].Name);
                    bool complementary = (ps[i].Model > 0) != (ps[j].Model > 0);
                    if (cp >= 8 && complementary)
                    {
                        // 同目录 + 长前缀 + 资源互补（一边带模型、一边只有音效/材质/脚本）→ 可能组件
                        rels[i].Add(new Rel { To = ps[j].Name, Type = "possible-component", Strength = "B", Detail = "同目录 + 前缀 " + cp + " 字 + 资源互补" });
                        rels[j].Add(new Rel { To = ps[i].Name, Type = "possible-component", Strength = "B", Detail = "同目录 + 前缀 " + cp + " 字 + 资源互补" });
                        continue;
                    }
                    string type = cp >= 4 ? "name-similar" : "folder";
                    string strength = cp >= 4 ? "C" : "C-";
                    string detail = cp >= 4 ? "同目录 + 名前缀 " + cp + " 字" : "同目录";
                    rels[i].Add(new Rel { To = ps[j].Name, Type = type, Strength = strength, Detail = detail });
                    rels[j].Add(new Rel { To = ps[i].Name, Type = type, Strength = strength, Detail = detail });
                }
            return rels;
        }

        private static string Decide(List<Pkg> ps, List<Rel>[] rels, int i)
        {
            bool a = false, aSameDir = false, bComp = false, bSame = false, c = false;
            foreach (Rel r in rels[i])
            {
                if (r.Strength == "A")
                {
                    a = true;
                    if (SameDirOf(ps, ps[i].Name, r.To)) aSameDir = true;
                }
                else if (r.Strength == "B" && r.Type == "possible-component") bComp = true;
                else if (r.Strength == "B") bSame = true;
                else c = true;
            }
            if (!string.IsNullOrEmpty(ps[i].Error)) return "unknown";
            if (a && aSameDir) return "component";
            if (bComp) return "possible-component";
            if (a) return "dependency";
            if (bSame) return "same-origin";
            return "unknown";
        }

        private static List<string[]> Subjects(Pkg p, string[] f, HashSet<string> official)
        {
            List<string[]> list = new List<string[]>();
            if (IsEffectsOnly(p)) return list;
            if (!string.IsNullOrEmpty(f[2]))
                list.Add(new string[] { f[2], "identified_from_formal", "来自正式结果投影" });
            // 多主体候选：包里出现 >=3 个不同的“官方模型名”，说明它同时改了多个对象
            List<string> cand = new List<string>();
            foreach (string m in p.ModelStems)
            {
                if (official.Contains(m + ".mdl") && !cand.Contains(m)) cand.Add(m);
            }
            if (cand.Count >= 3)
            {
                for (int i = 0; i < cand.Count && i < 12; i++)
                    list.Add(new string[] { cand[i], "candidate_from_model", "包内存在模型 " + cand[i] + ".mdl（官方槽位）" });
            }
            return list;
        }

        private static Dictionary<string, string> LoadPositions(string path)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return map;
            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int si = line.IndexOf("Sub = \"", StringComparison.Ordinal);
                    int ni = line.IndexOf("Name = \"", StringComparison.Ordinal);
                    if (si < 0 || ni < 0) continue;
                    string sub = Between(line, si + 6, '"');
                    string nm = Between(line, ni + 6, '"');
                    if (sub.Length == 0 || nm.Length == 0) continue;
                    map[nm] = sub + " / " + nm;
                    int ai = line.IndexOf("Aliases", StringComparison.Ordinal);
                    if (ai > 0)
                    {
                        int b1 = line.IndexOf('{', ai), b2 = line.IndexOf('}', ai);
                        if (b1 > 0 && b2 > b1)
                        {
                            string inner = line.Substring(b1 + 1, b2 - b1 - 1);
                            foreach (string part in inner.Split(','))
                            {
                                string a = part.Trim().Trim('"');
                                if (a.Length > 0 && !map.ContainsKey(a)) map[a] = sub + " / " + nm;
                            }
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        private static string Between(string s, int start, char quote)
        {
            int a = s.IndexOf(quote, start);
            if (a < 0) return "";
            int b = s.IndexOf(quote, a + 1);
            return b > a ? s.Substring(a + 1, b - a - 1) : "";
        }

        private static void MapPosition(string name, out string cand, out string status)
        {
            cand = ""; status = "no-candidate";
            if (string.IsNullOrEmpty(name) || sPos == null) return;
            string hit;
            if (sPos.TryGetValue(name, out hit)) { cand = hit; status = "candidate"; return; }
            // 候选模型名（英文小写）按位置表的英文名匹配：位置表里 Name 是中文，En 不参与本映射
            status = "unknown-subject";
        }

        private static HashSet<string> LoadOfficial(string path)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return set;
            byte[] tree; long ds;
            if (!ReadTree(path, out tree, out ds)) return set;
            List<Entry> ents = ParseTree(tree);
            foreach (Entry e in ents)
            {
                string bn = e.Path.Substring(e.Path.LastIndexOf('/') + 1);
                if (bn.Length > 0) set.Add(bn);
                int d = bn.LastIndexOf('.');
                if (d > 0) set.Add(bn.Substring(0, d));
            }
            return set;
        }

        private static bool SameDirOf(List<Pkg> ps, string a, string b)
        {
            string da = null, db = null;
            foreach (Pkg p in ps)
            {
                if (p.Name == a) da = p.Dir;
                if (p.Name == b) db = p.Dir;
            }
            return da != null && db != null && string.Equals(da, db, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOfficialDomain(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Replace('\\', '/').TrimStart('/');
            return p.StartsWith("models/", StringComparison.Ordinal)
                || p.StartsWith("materials/models/", StringComparison.Ordinal)
                || p.StartsWith("sound/weapons/", StringComparison.Ordinal)
                || p.StartsWith("sound/player/", StringComparison.Ordinal)
                || p.StartsWith("sound/npc/", StringComparison.Ordinal)
                || p.StartsWith("materials/particle/", StringComparison.Ordinal)
                || p.StartsWith("particles/", StringComparison.Ordinal);
        }

        private static bool IsSlotShape(string name)
        {
            string n = name.ToLowerInvariant();
            return n.StartsWith("v_") || n.StartsWith("w_");
        }

        private static int CommonPrefix(string a, string b)
        {
            int n = 0;
            while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
            return n;
        }

        // ---------------------------------------------------------------- 输出

        private static string ToJson(Pkg p, List<Rel> rels, string ownership, Dictionary<string, string[]> formal, HashSet<string> official)
        {
            string[] f = formal.ContainsKey(p.Name.ToLowerInvariant()) ? formal[p.Name.ToLowerInvariant()] : new string[] { "", "", "", "" };
            bool eff = IsEffectsOnly(p);
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("{");
            sb.Append("\"pkg\":").Append(Q(p.Name)).Append(',');
            sb.Append("\"formal\":{").Append("\"cat\":").Append(Q(f[0])).Append(",\"sub\":").Append(Q(f[1]))
              .Append(",\"subject\":").Append(Q(f[2])).Append(",\"key\":").Append(Q(f[3])).Append("},");

            sb.Append("\"resourceTypes\":{").Append("\"counts\":{")
              .Append("\"mdl\":").Append(p.Model).Append(",\"vmt\":").Append(p.Material)
              .Append(",\"vtf\":").Append(p.Texture).Append(",\"pcf\":").Append(p.Particle)
              .Append(",\"wav\":").Append(p.Sound).Append(",\"bsp\":").Append(p.Map).Append("},")
              .Append("\"parse\":{\"vmtRead\":").Append(p.VmtRead).Append(",\"pcfRead\":").Append(p.PcfRead)
              .Append(",\"error\":").Append(Q(p.Error)).Append("}},");

            List<string[]> subs = Subjects(p, f, official);
            sb.Append("\"subject\":{");
            sb.Append("\"status\":").Append(Q(eff ? "effects_only" : (subs.Count == 0 ? "unknown" : (subs.Count > 1 ? "multi" : "identified")))).Append(',');
            sb.Append("\"objects\":[");
            for (int k = 0; k < subs.Count; k++)
            {
                if (k > 0) sb.Append(',');
                string cand, st2;
                MapPosition(subs[k][0], out cand, out st2);
                sb.Append("{\"object\":").Append(Q(subs[k][0]))
                  .Append(",\"status\":").Append(Q(subs[k][1]))
                  .Append(",\"evidence\":").Append(Q(subs[k][2]))
                  .Append(",\"location\":{\"candidate\":").Append(Q(cand))
                  .Append(",\"status\":").Append(Q(st2)).Append("}}");
            }
            sb.Append("],\"multi\":").Append(subs.Count > 1 ? "true" : "false").Append("},");

            sb.Append("\"impact\":{").Append("\"objects\":[");
            for (int k = 0; k < p.Affected.Count; k++) { if (k > 0) sb.Append(','); sb.Append(Q(p.Affected[k])); }
            sb.Append("],\"note\":").Append(Q(eff ? "受影响的材质名（非主体）" : "")).Append("},");

            string layer = ownership == "component" || ownership == "possible-component" ? "组件关系"
                         : ownership == "same-origin" ? "来源关系"
                         : ownership == "dependency" ? "技术依赖" : "未知关系";
            sb.Append("\"ownership\":{").Append("\"status\":").Append(Q(ownership)).Append(',');
            sb.Append("\"layer\":").Append(Q(layer)).Append(',');
            sb.Append("\"relations\":[");
            for (int k = 0; k < rels.Count; k++)
            {
                if (k > 0) sb.Append(',');
                Rel r = rels[k];
                sb.Append("{\"to\":").Append(Q(r.To)).Append(",\"type\":").Append(Q(r.Type))
                  .Append(",\"strength\":").Append(Q(r.Strength)).Append(",\"detail\":").Append(Q(r.Detail)).Append("}");
            }
            sb.Append("]},");

            sb.Append("\"officialEvidence\":[");
            for (int k = 0; k < p.OfficialNotes.Count; k++) { if (k > 0) sb.Append(','); sb.Append(Q(p.OfficialNotes[k])); }
            sb.Append("],");
            sb.Append("\"trace\":{");
            sb.Append("\"facts\":[").Append(Q("条目：mdl" + p.Model + " vmt" + p.Material + " vtf" + p.Texture + " pcf" + p.Particle + " wav" + p.Sound)).Append("],");
            sb.Append("\"inference\":[").Append(Q(eff ? "类型判定为 EffectsOnly" : "未触发类型否决")).Append(",").Append(Q("归属判定：" + ownership)).Append("],");
            sb.Append("\"unresolved\":[").Append(Q(ownership == "standalone" ? "" : (ownership == "component_confirmed" ? "" : "套装归属未证实"))).Append("]");
            sb.Append("}}");
            return sb.ToString();
        }

        private static string Ratio(Pkg p) { return p.VmtRead == 0 ? "0.00" : ((double)p.ParticleBase / p.VmtRead).ToString("0.00"); }

        private static string Q(string s)
        {
            if (s == null) s = "";
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < ' ') b.Append(' ');
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        private static void SafeWrite(string path, string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                bool bom = path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(path, text, new UTF8Encoding(bom));
            }
            catch (Exception ex) { Console.WriteLine("write failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------- 扫描

        private static void Collect(string dir, List<string> list, int depth)
        {
            if (depth > MaxDepth || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.vpk", SearchOption.TopDirectoryOnly))
                    if (!IsPart(f)) list.Add(f);
                foreach (string d in Directory.GetDirectories(dir)) Collect(d, list, depth + 1);
            }
            catch { }
        }

        private static bool IsPart(string f)
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (n.StartsWith("pak01", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Length < 4) return false;
            int u = n.LastIndexOf('_');
            if (u < 0 || n.Length - u - 1 != 3) return false;
            for (int i = u + 1; i < n.Length; i++) if (n[i] < '0' || n[i] > '9') return false;
            return true;
        }

        private static Pkg Scan(string vpk)
        {
            Pkg p = new Pkg();
            p.Path = vpk; p.Name = Path.GetFileName(vpk); p.Dir = Path.GetDirectoryName(vpk);
            byte[] tree; long dataStart;
            if (!ReadTree(vpk, out tree, out dataStart)) { p.Error = "目录表读不出"; return p; }
            List<Entry> ents = ParseTree(tree);
            if (ents.Count == 0) { p.Error = "无条目"; return p; }

            List<Entry> vmts = new List<Entry>();
            List<Entry> pcfs = new List<Entry>();
            for (int i = 0; i < ents.Count; i++)
            {
                string path = ents[i].Path; string ext = ExtOf(path);
                if (ext == "mdl")
                {
                    p.Model++;
                    string ms = path.Substring(path.LastIndexOf('/') + 1);
                    int md = ms.LastIndexOf('.');
                    if (md > 0) ms = ms.Substring(0, md);
                    if (!p.ModelStems.Contains(ms)) p.ModelStems.Add(ms);
                }
                else if (ext == "vmt") { p.Material++; vmts.Add(ents[i]); }
                else if (ext == "vtf") p.Texture++;
                else if (ext == "pcf") { p.Particle++; pcfs.Add(ents[i]); }
                else if (ext == "wav" || ext == "mp3") { p.Sound++; if (IsSubjectSound(path)) p.SubjectSound = true; }
                else if (ext == "bsp") p.Map++;
                else if (ext == "nut" || ext == "cfg" || ext == "txt" || ext == "res") p.Script++;
                else p.Other++;
                if (path.StartsWith("materials/vgui/hud/icon_", StringComparison.Ordinal)) p.HudIcon = true;
                if (p.Assets.Count < MaxAssets && (ext == "vmt" || ext == "mdl" || ext == "pcf" || ext == "vtf"))
                {
                    string bn = path.Substring(path.LastIndexOf('/') + 1);
                    int bd = bn.LastIndexOf('.');
                    if (bd > 0) bn = bn.Substring(0, bd);
                    if (bn.Length >= 6) { p.Assets.Add(bn); if (!p.AssetPath.ContainsKey(bn)) p.AssetPath[bn] = path; }
                }
                if (ext == "txt" && path.IndexOf("addoninfo", StringComparison.Ordinal) >= 0)
                {
                    byte[] raw = ReadEntryAt(vpk, ents[i], dataStart, 0, 65536);
                    if (raw != null) ParseAddonInfo(raw, p);
                }
            }

            int pcfN = Math.Min(pcfs.Count, MaxPcfRead);
            for (int k = 0; k < pcfN; k++)
            {
                Entry pe = pcfs[k];
                byte[] head = ReadEntryAt(vpk, pe, dataStart, 0, PcfSlice);
                if (head != null) CollectNames(head, p.PcfNames);
                if (pe.Length > (uint)(PcfSlice * 2))
                {
                    byte[] tail = ReadEntryAt(vpk, pe, dataStart, (int)pe.Length - PcfSlice, PcfSlice);
                    if (tail != null) CollectNames(tail, p.PcfNames);
                }
            }
            p.PcfRead = pcfN;

            int vmtN = Math.Min(vmts.Count, MaxVmtRead);
            for (int i = 0; i < vmtN; i++)
            {
                byte[] raw = ReadEntryAt(vpk, vmts[i], dataStart, 0, MaxVmtBytes);
                if (raw == null) continue;
                p.VmtRead++;
                string stem = StemOf(vmts[i].Path);
                p.VmtStems.Add(stem);
                if (p.PcfNames.Contains(stem)) { p.PcfRef++; if (p.Affected.Count < 12 && !p.Affected.Contains(stem)) p.Affected.Add(stem); }
                string bt = VmtBaseTexture(raw);
                if (IsParticlePath(bt)) p.ParticleBase++;
                if (IsCustomBase(bt)) p.CustomBase++;
            }
            return p;
        }

        private static void ParseAddonInfo(byte[] raw, Pkg p)
        {
            string s = Encoding.UTF8.GetString(raw);
            p.Title = Field(s, "addontitle");
            p.Author = Field(s, "addonauthor");
        }

        private static string Field(string s, string key)
        {
            int i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int q1 = s.IndexOf('"', i + key.Length);
            if (q1 < 0) return "";
            int q2 = s.IndexOf('"', q1 + 1);
            if (q2 <= q1) return "";
            int q3 = s.IndexOf('"', q2 + 1);
            if (q3 < 0) return "";
            int q4 = s.IndexOf('"', q3 + 1);
            if (q4 <= q3) return "";
            string mid = s.Substring(q3 + 1, q4 - q3 - 1).Trim();
            return mid.Length > 200 ? mid.Substring(0, 200) : mid;
        }

        private static bool IsEffectsOnly(Pkg p)
        {
            // 唯一判定源：与正式接入共用 EffectsVeto.Decide
            return EffectsVeto.Decide(p.VmtRead, p.Model, p.HudIcon, p.SubjectSound, p.Map, p.PcfRef, p.ParticleBase);
        }

        private static bool IsSubjectSound(string path)
        {
            return path.StartsWith("sound/weapons/", StringComparison.Ordinal)
                || path.StartsWith("sound/player/", StringComparison.Ordinal)
                || path.StartsWith("sound/npc/", StringComparison.Ordinal)
                || path.StartsWith("sound/zombie/", StringComparison.Ordinal)
                || path.StartsWith("sound/horde", StringComparison.Ordinal);
        }

        private static bool IsCustomBase(string basetex)
        {
            if (string.IsNullOrEmpty(basetex)) return false;
            string s = basetex.Replace('\\', '/').ToLowerInvariant().TrimStart('/');
            if (s.StartsWith("materials/", StringComparison.Ordinal)) s = s.Substring(10);
            return !(s.StartsWith("models/", StringComparison.Ordinal) || s.StartsWith("particle/", StringComparison.Ordinal));
        }

        private static bool IsParticlePath(string basetex)
        {
            if (string.IsNullOrEmpty(basetex)) return false;
            string s = basetex.Replace('\\', '/').ToLowerInvariant().TrimStart('/');
            if (s.StartsWith("materials/", StringComparison.Ordinal)) s = s.Substring(10);
            return s.StartsWith("particle/", StringComparison.Ordinal);
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
            while (i < t.Length && list.Count < MaxEntries)
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

        private static byte[] ReadEntryAt(string vpk, Entry e, long dataStart, int innerOffset, int count)
        {
            try
            {
                if (e.Preload != null)
                {
                    if (innerOffset >= e.Preload.Length) return null;
                    int n1 = Math.Min(count, e.Preload.Length - innerOffset);
                    byte[] b1 = new byte[n1];
                    Array.Copy(e.Preload, innerOffset, b1, 0, n1);
                    return b1;
                }
                if (innerOffset < 0 || innerOffset >= (long)e.Length) return null;
                string target = vpk;
                long at = dataStart + e.Offset + innerOffset;
                if (e.Arch != 0x7FFF)
                {
                    string baseName = vpk.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase) ? vpk.Substring(0, vpk.Length - 8) : vpk.Substring(0, vpk.Length - 4);
                    target = baseName + "_" + e.Arch.ToString("000") + ".vpk";
                    at = e.Offset + innerOffset;
                }
                using (FileStream fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (at >= fs.Length) return null;
                    int want = (int)Math.Min((long)count, fs.Length - at);
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

        private static string VmtBaseTexture(byte[] raw)
        {
            string s = Encoding.ASCII.GetString(raw);
            int i = s.IndexOf("$basetexture", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            i += 12;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '"' || s[i] == '\r' || s[i] == '\n')) i++;
            int start = i;
            while (i < s.Length && s[i] != '"' && s[i] != '\r' && s[i] != '\n' && s[i] != ' ' && s[i] != '\t') i++;
            return i > start ? s.Substring(start, i - start) : "";
        }

        private static void CollectNames(byte[] raw, HashSet<string> set)
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

        private static string ExtOf(string path)
        {
            int d = path.LastIndexOf('.');
            return d < 0 ? "" : path.Substring(d + 1);
        }

        private static string StemOf(string path)
        {
            string f = path;
            int s = f.LastIndexOf('/');
            if (s >= 0) f = f.Substring(s + 1);
            int d = f.LastIndexOf('.');
            return (d < 0 ? f : f.Substring(0, d)).ToLowerInvariant();
        }

        private static Dictionary<string, string[]> LoadBaseline(string path)
        {
            Dictionary<string, string[]> map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return map;
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] c = lines[i].Split('\t');
                    if (c.Length < 5) continue;
                    string p = c[0];
                    int s = p.LastIndexOf('\\');
                    map[(s >= 0 ? p.Substring(s + 1) : p).ToLowerInvariant()] = new string[] { c[1], c[2], c[3], c[4] };
                }
            }
            catch { }
            return map;
        }
    }
}