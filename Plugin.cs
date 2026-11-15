using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Installer
{
    // ---------- 插件组件（bin\neko\plugins） ----------
    // 有些 MOD 不止一个 vpk：它还带一套 DLL，要落到 <游戏>\bin\neko\plugins 里才显示得出来
    // （CFX 渲染框架 + 这把枪的插件）。这类包按 addons 那套逻辑装不完整 —— 模型进了 addons，
    // 插件没进 bin，进游戏手上是空的，而且不崩不报错，最难查。
    //
    // 这里只做两件事：认出哪个包带插件、把每个文件分成「共用」和「独占」。
    // 落盘在 Work.CopyPlugins，还原在 Work.FullRestore。

    /// <summary>插件包里的一个文件。Rel 是相对游戏根的路径，装的时候直接拼上去。</summary>
    public class PluginFile
    {
        public string Rel;      // bin\neko\plugins\shu_weapon.dll
        public string Src;      // 库里那一份的绝对路径
        public long Size;
        /// <summary>共用框架：别的插件也在用（CFX\Framework 这些）。卸载本包不能删它。</summary>
        public bool Shared;
    }

    public class PluginPack
    {
        /// <summary>唯一键：库里插件目录的绝对路径。同一份插件被两个 vpk 同时认出来时靠它去重。</summary>
        public string Key;
        public string Source;
        /// <summary>相对游戏根的落点，恒为 bin\neko\plugins。</summary>
        public string Anchor;
        public string Name;
        public List<PluginFile> Files = new List<PluginFile>();
        public long Size;
        public int OwnedCount;
        public int SharedCount;
        /// <summary>包里至少有一个 .dll。只有 .h/.ini 的目录是 SDK 头文件，不是能装的插件。</summary>
        public bool HasDll;
        /// <summary>包里有 CFX\Weapons\&lt;枪&gt;\ 这一层：这是「某个 MOD 的插件」最硬的特征。</summary>
        public bool HasWeaponDir;

        /// <summary>「独占」那些的短名字，装机确认和日志里念给用户听。</summary>
        public string OwnedList(int max)
        {
            List<string> names = new List<string>();
            foreach (PluginFile f in Files)
            {
                if (f.Shared) continue;
                string rel = f.Rel;
                if (rel.StartsWith(Anchor + "\\", StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(Anchor.Length + 1);
                names.Add(rel);
                if (names.Count >= max) { names.Add("…"); break; }
            }
            return string.Join("、", names.ToArray());
        }
    }

    public static class PluginKit
    {
        public const string Anchor = "bin\\neko\\plugins";

        // 同一个插件目录会被同一个包里的好几个 vpk 各找一次，装的时候还会再找一次。
        // 缓存按目录存，扫描一次只枚举一遍文件树。
        private static readonly Dictionary<string, PluginPack> cache =
            new Dictionary<string, PluginPack>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> misses =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache()
        {
            cache.Clear();
            misses.Clear();
        }

        /// <summary>
        /// 从一个 vpk 往上找插件目录：`&lt;包目录&gt;\bin\neko\plugins`。
        /// 最多往上走两层（包里常见的两种摆法：bin 和 vpk 同级，或者 bin 在外面一层）。
        /// 找不到返回 null —— 绝大多数 MOD 都是这种，不走后面的枚举。
        /// </summary>
        public static PluginPack Find(string vpkPath)
        {
            if (string.IsNullOrEmpty(vpkPath)) return null;
            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(vpkPath)); }
            catch { return null; }
            if (string.IsNullOrEmpty(dir)) return null;

            int levels = 0;
            for (int up = 0; up <= 2 && !string.IsNullOrEmpty(dir); up++)
            {
                string probe = Path.Combine(Path.Combine(Path.Combine(dir, "bin"), "neko"), "plugins");
                if (Directory.Exists(probe))
                {
                    levels = up;
                    PluginPack p = Load(probe);
                    if (p == null) return null;

                    // 只有 .h / .ini 的目录是平台带的 SDK 头（L4N 的 l4n_plugin.h 就是这种），
                    // 当插件收下来会变成「装个枪模却往 bin 里塞一堆没用的头文件」。
                    if (!p.HasDll)
                    {
                        Core.Log("INFO", "跳过非插件目录（没有 DLL）: " + probe);
                        return null;
                    }
                    // bin 与 vpk 同级 → 这就是这个包的插件，认。
                    // 跨层找到的（比如 vpk 在 <平台>\addons\、插件在 <平台>\bin\）必须自证：
                    // 里面得有 CFX\Weapons\<枪>\ 这一层，否则多半是平台自己的运行库，不能算这个 MOD 的。
                    if (levels > 0 && !p.HasWeaponDir)
                    {
                        Core.Log("INFO", "跳过疑似平台运行库的插件目录（跨 " + levels + " 层）: " + probe);
                        return null;
                    }
                    return p;
                }
                try { dir = Path.GetDirectoryName(dir); } catch { return null; }
            }
            return null;
        }

        /// <summary>把 bin\neko\plugins 整个读成一份清单。目录不存在或空目录返回 null（没有可装的东西）。</summary>
        public static PluginPack Load(string pluginsDir)
        {
            string full;
            try { full = Path.GetFullPath(pluginsDir).TrimEnd('\\'); }
            catch { return null; }

            PluginPack hit;
            if (cache.TryGetValue(full, out hit)) return hit;
            string miss;
            if (misses.TryGetValue(full, out miss)) return null;

            if (!Directory.Exists(full)) { misses[full] = "1"; return null; }

            PluginPack p = new PluginPack();
            p.Key = full.ToLowerInvariant();
            p.Source = full;
            p.Anchor = Anchor;
            p.Name = PackNameOf(full);

            try
            {
                foreach (string f in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                {
                    string inner = f.Substring(full.Length).TrimStart('\\');
                    if (inner.Length == 0) continue;
                    FileInfo fi;
                    try { fi = new FileInfo(f); } catch { continue; }
                    PluginFile pf = new PluginFile();
                    pf.Rel = Anchor + "\\" + inner;
                    pf.Src = f;
                    pf.Size = fi.Length;
                    pf.Shared = IsSharedPath(inner);
                    p.Files.Add(pf);
                    p.Size += pf.Size;
                    if (pf.Shared) p.SharedCount++; else p.OwnedCount++;
                    if (inner.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) p.HasDll = true;
                    string[] seg = inner.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (seg.Length >= 4
                        && string.Equals(seg[0], "CFX", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(seg[1], "Weapons", StringComparison.OrdinalIgnoreCase))
                        p.HasWeaponDir = true;
                }
            }
            catch (Exception ex)
            {
                Core.Log("WARN", "读插件目录失败 " + full + ": " + ex.Message);
                misses[full] = "1";
                return null;
            }

            if (p.Files.Count == 0) { misses[full] = "1"; return null; }
            p.Files.Sort(delegate(PluginFile a, PluginFile b)
            {
                return string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase);
            });
            cache[full] = p;
            return p;
        }

        /// <summary>包名：插件目录往上第四层（...\RAM7 星月夜（CFX 插件版）\bin\neko\plugins）。</summary>
        private static string PackNameOf(string pluginsDir)
        {
            try
            {
                string neko = Path.GetDirectoryName(pluginsDir);          // neko
                string bin = neko == null ? null : Path.GetDirectoryName(neko);
                string pack = bin == null ? null : Path.GetDirectoryName(bin);
                if (pack != null) return Path.GetFileName(pack);
            }
            catch { }
            return "插件";
        }

        /// <summary>
        /// 相对 bin\neko\plugins 的路径 → 是不是共用框架。
        ///
        /// 判错两个方向的代价差很多：
        ///   · 把共用的当独占 → 卸载 A 包时删掉框架，B 包的枪全哑（这个更严重）
        ///   · 把独占的当共用 → 卸载后留几个文件，用户看得见、也不影响游戏
        /// 所以拿不准时一律按「共用」算：留着不删。
        /// </summary>
        public static bool IsSharedPath(string inner)
        {
            if (string.IsNullOrEmpty(inner)) return false;                 // 根目录本身，不算文件
            string[] seg = inner.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length == 0) return false;
            if (seg.Length == 1) return false;                             // plugins\xxx.dll：这个包自己的加载器

            string top = seg[0];
            if (string.Equals(top, "CFX", StringComparison.OrdinalIgnoreCase))
            {
                if (seg.Length == 2) return true;                          // CFX\cfx_host.ini：框架配置，谁都在读
                string sub = seg[1];
                if (string.Equals(sub, "Framework", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(sub, "renderer", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(sub, "Weapons", StringComparison.OrdinalIgnoreCase)) return false;  // CFX\Weapons\<枪>\**
                return true;                                               // CFX 下不认识的子目录：按共用保命
            }
            return false;                                                  // 别的框架：按这个包自己的算
        }

        /// <summary>给用户看的一句话：这个包带几个插件文件，落到哪。</summary>
        public static string Describe(PluginPack p)
        {
            if (p == null) return "";
            return "插件 " + p.Files.Count + " 个（独占 " + p.OwnedCount + " / 共用 " + p.SharedCount + "）→ " + p.Anchor;
        }

        // ---------- 自检（--plugself，纯离线，不碰游戏目录） ----------
        public static int SelfTest()
        {
            int bad = 0;
            // 1) 共享判定：方案里定的三条边界
            bad += CheckB(IsSharedPath("shu_weapon.dll"), false, "plugins 根下的 dll 算这个包自己的");
            bad += CheckB(IsSharedPath("CFX\\Framework\\CfxCore.dll"), true, "CFX 框架算共用");
            bad += CheckB(IsSharedPath("CFX\\Framework\\shaders\\a.cso"), true, "框架子目录算共用");
            bad += CheckB(IsSharedPath("CFX\\renderer\\game_shader_generic_cfx.dll"), true, "渲染器算共用");
            bad += CheckB(IsSharedPath("CFX\\cfx_host.ini"), true, "CFX 根下的配置算共用");
            bad += CheckB(IsSharedPath("CFX\\Weapons\\RAM7_Starlit\\CfxRam7Starlit.dll"), false, "每把枪自己的插件算独占");
            bad += CheckB(IsSharedPath("CFX\\Weapons\\RAM7_Starlit\\user_options.ini"), false, "枪目录里的配置算独占");
            bad += CheckB(IsSharedPath("CFX\\Weird\\x.dll"), true, "CFX 下不认识的子目录按共用（宁可留着）");

            // 2) 目录 → 清单：建一个假包，验文件数、路径前缀、共享分类
            string tmp = Path.Combine(Path.GetTempPath(), "plugself-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                string plug = Path.Combine(tmp, "假MOD", "bin", "neko", "plugins");
                Directory.CreateDirectory(Path.Combine(plug, "CFX", "Framework"));
                Directory.CreateDirectory(Path.Combine(plug, "CFX", "Weapons", "Gun1"));
                File.WriteAllText(Path.Combine(plug, "loader.dll"), "x");
                File.WriteAllText(Path.Combine(plug, "CFX", "Framework", "core.dll"), "yy");
                File.WriteAllText(Path.Combine(plug, "CFX", "Weapons", "Gun1", "gun.dll"), "zzz");
                File.WriteAllText(Path.Combine(tmp, "假MOD", "a.vpk"), "not a real vpk");

                ClearCache();
                PluginPack p = Find(Path.Combine(tmp, "假MOD", "a.vpk"));
                bad += CheckB(p != null, true, "从 vpk 往上能找到 bin\\neko\\plugins");
                if (p != null)
                {
                    bad += CheckN(p.Files.Count, 3, "清单收了 3 个文件");
                    bad += CheckN(p.OwnedCount, 2, "独占 2 个（loader + gun）");
                    bad += CheckN(p.SharedCount, 1, "共用 1 个（Framework\\core.dll）");
                    bad += CheckS(p.Name, "假MOD", "包名取插件目录往上第四层");
                    bool prefixed = true;
                    foreach (PluginFile f in p.Files)
                        if (!f.Rel.StartsWith(Anchor + "\\", StringComparison.OrdinalIgnoreCase)) prefixed = false;
                    bad += CheckB(prefixed, true, "所有落点都带 bin\\neko\\plugins 前缀");
                    bad += CheckB(Core.IsUnder(tmp, Path.Combine(tmp, p.Files[0].Rel)), true, "拼出来的落点仍在游戏根下面");
                }

                // 3) 没有插件目录的普通包：不能误判
                string plain = Path.Combine(tmp, "普通包");
                Directory.CreateDirectory(plain);
                File.WriteAllText(Path.Combine(plain, "b.vpk"), "x");
                ClearCache();
                bad += CheckB(Find(Path.Combine(plain, "b.vpk")) == null, true, "普通 MOD 找不到插件，返回空");

                // 4) 跨层找到、但里面没有 CFX\Weapons：平台自带的运行库，不能算这个 MOD 的插件
                //    （真实踩过：扩展平台\L4N平台\L4N\addons 里的 vpk 把 L4N 的 plugins 认成了自己的）
                string plat = Path.Combine(tmp, "平台");
                Directory.CreateDirectory(Path.Combine(plat, "bin", "neko", "plugins"));
                Directory.CreateDirectory(Path.Combine(plat, "addons"));
                File.WriteAllText(Path.Combine(plat, "bin", "neko", "plugins", "core.dll"), "x");
                File.WriteAllText(Path.Combine(plat, "addons", "gun.vpk"), "x");
                ClearCache();
                bad += CheckB(Find(Path.Combine(plat, "addons", "gun.vpk")) == null, true, "跨层找到的平台运行库不认");

                // 5) 只有头文件没有 DLL：SDK 目录，不是能装的插件
                string sdk = Path.Combine(tmp, "SDK包");
                Directory.CreateDirectory(Path.Combine(sdk, "bin", "neko", "plugins"));
                File.WriteAllText(Path.Combine(sdk, "bin", "neko", "plugins", "plugin.h"), "x");
                File.WriteAllText(Path.Combine(sdk, "a.vpk"), "x");
                ClearCache();
                bad += CheckB(Find(Path.Combine(sdk, "a.vpk")) == null, true, "只有头文件的目录不算插件");

                // 6) 跨层、但确实带 CFX\Weapons\<枪>：这是「按游戏根摆」的包，认
                string mirror = Path.Combine(tmp, "镜像包");
                Directory.CreateDirectory(Path.Combine(mirror, "bin", "neko", "plugins", "CFX", "Weapons", "Gun2"));
                Directory.CreateDirectory(Path.Combine(mirror, "addons"));
                File.WriteAllText(Path.Combine(mirror, "bin", "neko", "plugins", "CFX", "Weapons", "Gun2", "g.dll"), "x");
                File.WriteAllText(Path.Combine(mirror, "addons", "m.vpk"), "x");
                ClearCache();
                bad += CheckB(Find(Path.Combine(mirror, "addons", "m.vpk")) != null, true, "跨层但带 CFX\\Weapons 的镜像包要认");
            }
            catch (Exception ex)
            {
                Console.WriteLine("PLUG 自检异常: " + ex.Message);
                bad++;
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                ClearCache();
            }
            return bad;
        }

        private static int Fail(string what, string want, string got)
        {
            Console.WriteLine("PLUG FAIL " + what + "：期望 " + want + "，实际 " + got);
            return 1;
        }

        private static int CheckB(bool got, bool want, string what)
        {
            return got == want ? 0 : Fail(what, want.ToString(), got.ToString());
        }

        private static int CheckN(int got, int want, string what)
        {
            return got == want ? 0 : Fail(what, want.ToString(), got.ToString());
        }

        private static int CheckS(string got, string want, string what)
        {
            return string.Equals(got, want, StringComparison.Ordinal) ? 0 : Fail(what, want, got);
        }
    }
}
