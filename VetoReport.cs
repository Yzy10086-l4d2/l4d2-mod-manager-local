using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Installer
{
    // 只读：模拟"开启 EffectsOnly 否决"后的分类变化，不写正式状态
    internal static class VetoReport
    {
        public static int Run(string root, string outPath)
        {
            List<string> vpks = new List<string>();
            Collect(root, vpks, 0);
            vpks.Sort(StringComparer.OrdinalIgnoreCase);
            StringBuilder sb = new StringBuilder(1 << 20);
            sb.AppendLine("path\tcat\tsub\tsubject\tsubjects\tveto\treason\tpostCat\tpostSub\tpostSubject");
            int vetoes = 0, changes = 0;
            foreach (string vpk in vpks)
            {
                string rel = vpk;
                try { rel = vpk.Substring(root.TrimEnd('\\').Length).TrimStart('\\'); } catch { }
                ModIdentify.Result r = ModIdentify.Analyze(vpk);
                string cat = "", sub = "";
                try { ModIdentify.ClassifyByContent(vpk, out cat, out sub); } catch { }
                string reason = "";
                bool veto = false;
                try { veto = EffectsVeto.ShouldVetoCore(vpk, out reason); } catch { }
                string pcat = cat, psub = sub, psubj = r.Subject ?? "";
                if (veto)
                {
                    vetoes++;
                    pcat = "杂项"; psub = "特效"; psubj = "特效套装";
                    if (cat != pcat || sub != psub) changes++;
                }
                sb.Append(rel.Replace('\t', ' ')).Append('\t')
                  .Append(cat).Append('\t').Append(sub).Append('\t').Append((r.Subject ?? "").Replace('\t', ' ')).Append('\t')
                  .Append(SubjectsOf(r)).Append('\t')
                  .Append(veto ? "1" : "0").Append('\t').Append((reason ?? "").Replace('\t', ' ')).Append('\t')
                  .Append(pcat).Append('\t').Append(psub).Append('\t').Append(psubj.Replace('\t', ' ')).AppendLine();
            }
            sb.AppendLine("# 汇总\t包=" + vpks.Count + "\t否决=" + vetoes + "\t分类变化=" + changes);
            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Console.WriteLine("write failed: " + ex.Message); return 3; }
            return 0;
        }

        private static string SubjectsOf(ModIdentify.Result r)
        {
            if (r == null || r.Subjects == null || r.Subjects.Count == 0) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (ModIdentify.SubjectRef s in r.Subjects)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(s.Object).Append('|').Append(s.Tier).Append('|').Append(s.Status);
            }
            return sb.ToString().Replace('\t', ' ');
        }

        private static void Collect(string dir, List<string> list, int depth)
        {
            if (depth > 4 || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.vpk", SearchOption.TopDirectoryOnly))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (n.StartsWith("pak01", StringComparison.OrdinalIgnoreCase)) continue;
                    if (n.Length > 4 && n[n.Length - 4] == '_' && char.IsDigit(n[n.Length - 3]) && char.IsDigit(n[n.Length - 2]) && char.IsDigit(n[n.Length - 1])) continue;
                    list.Add(f);
                }
                foreach (string d in Directory.GetDirectories(dir)) Collect(d, list, depth + 1);
            }
            catch { }
        }
    }
}