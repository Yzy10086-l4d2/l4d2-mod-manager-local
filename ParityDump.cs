using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Installer
{
    // 只读快照：用"生产函数"跑一遍库，输出现行分类结果
    // 用途：重构/修 bug 前后对比，任何一行变化都必须能解释
    internal static class ParityDump
    {
        public static int Run(string root, string outPath)
        {
            List<string> vpks = new List<string>();
            Collect(root, vpks, 0);
            vpks.Sort(StringComparer.OrdinalIgnoreCase);
            StringBuilder sb = new StringBuilder(1 << 20);
            sb.AppendLine("path\tcat\tsub\tsubject\tkey\tlevel\tkinds\tentries");
            foreach (string vpk in vpks)
            {
                string rel = vpk;
                try { rel = vpk.Substring(root.TrimEnd('\\').Length).TrimStart('\\'); } catch { }
                ModIdentify.Result r = ModIdentify.Analyze(vpk);
                string cat = "", sub = "";
                try { ModIdentify.ClassifyByContent(vpk, out cat, out sub); } catch { }
                sb.Append(rel.Replace('\t', ' ')).Append('\t')
                  .Append(cat).Append('\t').Append(sub).Append('\t')
                  .Append((r.Subject ?? "").Replace('\t', ' ')).Append('\t')
                  .Append((r.Key ?? "").Replace('\t', ' ')).Append('\t')
                  .Append((r.Level ?? "").Replace('\t', ' ')).Append('\t')
                  .Append((r.Kinds ?? "").Replace('\t', ' ')).Append('\t')
                  .Append(r.Entries).AppendLine();
            }
            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Console.WriteLine("write failed: " + ex.Message); return 3; }
            return 0;
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
