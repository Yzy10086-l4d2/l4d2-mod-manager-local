using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Installer
{
    // ============================================================
    //  方案树（A 版）
    //  左边按「能替换的东西」列成横向树：枪械 → AK-47 / M16 …
    //  右边是这件东西的候选 MOD，单选。
    //  规则：一个位置只留一个本体，选新的就把同位置原来的取消；
    //        只改第一人称模型 / 音效 / HUD 这类不换本体的，单独一栏，各管各的开关。
    // ============================================================

    /// <summary>一个「能替换的东西」：AK-47 / 医疗包 / 教练 …</summary>
    internal sealed class Slot
    {
        public string Key;                       // 被替换的模型名，如 w_rifle_ak47
        public string Name;                      // 显示名：AK-47
        public string Cat;                       // 大类：枪械 / 医疗 / 人物 …
        public string Sub = "其他";              // 小分类：步枪 / 霰弹枪 / 一代 / 医疗包 …
        public bool Manual;                      // 这个位置是人工指定出来的（可能还要并回自动的同名位置）
        // 「一堆」而不是「一个位置」：地图 / 喷漆 / 用户自建分支 —— 里面能同时开好几个，
        // 点一行是开 / 关，不是把别的替换掉。Items 里装的就是这一堆的全部候选。
        public bool Pile;
        // 库里还没有 MOD 的「标准位置」：照常占一行，让人看出还缺什么。
        // 它没有任何候选（Items 空着），点开只有「留原版」。
        public bool Empty;
        // 同一大类同一小分类里的排序权重（小的在前）。目前只有「我的分支」用它按父子顺序排，
        // 别的槽位都是 0，照旧按 PosOrder / 名字排。
        public int Rank;
        // 换本体的：一个位置只能有一个（同一把枪两套模型会打架）
        public readonly List<ModGroup> Items = new List<ModGroup>();
    }

    /// <summary>把 MOD 库按「改的是什么」聚成槽位。认不出改什么的，不进树。</summary>
    internal static class SlotIndex
    {
        public static List<Slot> Build()
        {
            Dictionary<string, Slot> map = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
            List<Slot> list = new List<Slot>();
            foreach (ModGroup g in Catalog.Groups)
            {
                ModItem m = g.Main;
                if (m == null) continue;
                // 地图能叠好几张、喷漆不占位置 —— 这两种没有「只能一个」的说法，不进树
                if (g.Cat == "地图" || g.Cat == "喷漆") continue;
                // 人工指定过就以人工为准：大类、位置都能改
                string cat = CatPrefs.CatOf(m, g.Cat);
                string hand = CatPrefs.SlotOf(m).Trim();
                string key, nm, hint = "";
                if (hand.Length > 0)
                {
                    key = "slot:" + cat + "/" + hand;
                    nm = hand;
                }
                else
                {
                    key = (m.SubjectKey ?? "").Trim();
                    nm = (m.Subject ?? "").Trim();
                    if (IsCountName(nm)) continue;                 // 「4 件」是「一个包里改了好几样」的上报格式，不是东西
                    nm = SplitKind(nm, out hint);                  // 「霰弹枪/木喷」→ 木喷（枪种当小分类）
                    // 一个包同时改了两件（冲锋枪 + MAC-10 共用一套动画，作者常一起换），位置只能占一个：
                    // 用户把它放在哪个目录，就归哪个 —— 目录名比「A/B」这种并列表述有用。
                    if (nm.IndexOf('/') >= 0)
                    {
                        string c0 = Concrete("", g, m);
                        if (c0.Length > 0 && HasHalf(nm, c0)) { nm = c0; key = "c:" + c0; }
                        else
                        {
                            // 本地版扫的是游戏 addons，没有「用户把它放在哪个目录」这条依据，
                            // 那就取斜杠里第一个能在标准位置表里找到的名字：MAC-10/冲锋枪 → MAC-10。
                            // 不这么做它会因为名字带斜杠谁都对不上，凭空消失（用户实测找不到）。
                            foreach (string half in nm.Split('/'))
                            {
                                string h = half.Trim();
                                if (h.Length > 0 && StandardName(h)) { nm = h; key = "c:" + h; break; }
                            }
                        }
                    }
                    // 只靠材质认出来的名字不能定死：铁喷的贴图就放在 materials/.../shotgun/ 下，
                    // 会被认成木喷。这种时候听目录名。
                    if (m.SubjectLevel == "材质" && cat != "杂项" && cat != "地图" && cat != "喷漆")
                    {
                        string c1 = Concrete(nm, g, m);
                        if (c1.Length > 0) { nm = c1; key = "c:" + c1; }
                    }
                    // 只认出「步枪 / 霰弹枪 / 幸存者」这种泛称时，用目录名或 MOD 名里的具体型号补回来。
                    // 用户自己的目录名（sg552 / 铁喷 / 黑胖Coach）比内容识别准得多。
                    if (Vague(nm))
                    {
                        string c = Concrete(nm, g, m);
                        if (c.Length > 0) { nm = c; key = "c:" + c; }
                    }
                    if (key.Length == 0)
                    {
                        // 什么都没认出来：看目录名是不是个具体型号（「铁喷」这种），是就照它归位
                        if (nm.Length == 0)
                        {
                            string c = Concrete("", g, m);
                            if (c.Length > 0) { nm = c; key = "c:" + c; }
                        }
                        // 人物、投掷物这类没有「模型名」，只有认出来的名字 —— 就用名字当位置
                        if (key.Length == 0)
                        {
                            if (nm.Length == 0) continue;              // 名字都认不出：不进树
                            key = "sub:" + nm;
                        }
                    }
                }
                // 近战 / 投掷物 / 弹药升级不算枪：提成和枪械平级的大类。
                // 只动自动归类的（人工指定过大类的以人工为准）；位置键带大类，不会和旧槽撞。
                string sub0 = "";
                if (hand.Length == 0 && cat == "枪械")
                {
                    sub0 = SubOf(cat, nm.Length > 0 ? nm : key, key, hint, g.Sub);
                    if (sub0 == "近战" || sub0 == "投掷物" || sub0 == "弹药升级") cat = sub0;
                }
                // 弹药堆（Ammo pile）：识别按模型名把它归成「杂项 · 可搬道具」，
                // 但它跟燃烧 / 爆破 / 激光那几个升级箱是一路货 —— 都是给枪补弹药的东西，
                // 用户 2026-11-09 定：归「弹药升级」，别在杂项里单独蹲一行。
                if (hand.Length == 0 && cat == "杂项" && IsAmmoPile(nm.Length > 0 ? nm : key)) cat = "弹药升级";
                // 别为「本游戏没有的枪」凭空造位置：P90 / M4A1 这种名字（常来自文件名或别的游戏）
                // 一律不进树，落进「未归类」让人自己定。
                if (hand.Length == 0 && cat == "枪械" && !StandardName(nm.Length > 0 ? nm : key)) continue;
                Slot s;
                // 位置键永远带上大类：枪械的「MAC-10」模型和杂项的「冲锋枪 改视角」用的是同一个模型名，
                // 但它们不是「二选一」的关系（改视角是叠加的），混进一个槽就错了。
                string mapKey = cat + "\u0001" + key;
                if (!map.TryGetValue(mapKey, out s))
                {
                    s = new Slot();
                    s.Key = mapKey;
                    s.Name = nm.Length > 0 ? nm : Tag(key);   // 只有模型名时别把 w_rifle_ak47 直接摆到树上
                    s.Cat = cat;
                    s.Manual = hand.Length > 0;
                    s.Sub = sub0.Length > 0 ? sub0 : SubOf(cat, s.Name, key, hint, g.Sub);
                    map[mapKey] = s;
                    list.Add(s);
                }
                else if (Vague(s.Name) && nm.Length > 0 && !Vague(nm))
                {
                    s.Name = nm;                               // 「步枪」这种泛称，换成更具体的名字
                    s.Sub = SubOf(cat, s.Name, key, hint, g.Sub);
                }
                s.Items.Add(g);
            }
            MergeManual(list);
            MergeSame(list);
            Disambiguate(list);
            EnsureStandard(list);               // 库里没有的枪 / 人物 / 药品也占一行
            // 被拖到别的小分类的位置，以手工归属为准。放在最后做：名字已经被 Disambiguate 定下来了
            foreach (Slot s in list)
            {
                string ov = CatPrefs.SubOverride(s.Cat, s.Name);
                if (ov.Length > 0) s.Sub = ov;
            }
            return list;
        }

        /// <summary>
        /// 人工写的位置名如果和自动认出来的位置同名（同一个大类），并成一行 ——
        /// 否则「AK-47」会分成人工一个、自动一个，看着像两个位置。
        /// </summary>
        private static void MergeManual(List<Slot> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Slot a = list[i];
                if (!a.Manual) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    if (j == i) continue;
                    Slot b = list[j];
                    if (b.Cat != a.Cat || b.Name != a.Name) continue;
                    if (b.Manual) continue;                   // 两个都是人工的：key 相同，不会发生
                    foreach (ModGroup g in a.Items) if (!b.Items.Contains(g)) b.Items.Add(g);
                    list.RemoveAt(i);
                    break;
                }
            }
        }

        /// <summary>
        /// 名字洗干净之后可能撞车：「霰弹枪/木喷」洗成「木喷」，就该和另一个「木喷」并成一个位置。
        /// 同一个大类、同一个小分类、同一个名字 —— 并。
        /// </summary>
        private static void MergeSame(List<Slot> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Slot a = list[i];
                for (int j = 0; j < i; j++)
                {
                    Slot b = list[j];
                    if (b.Cat != a.Cat || b.Sub != a.Sub || b.Name != a.Name) continue;
                    foreach (ModGroup g in a.Items) if (!b.Items.Contains(g)) b.Items.Add(g);
                    if (a.Manual) b.Manual = true;
                    list.RemoveAt(i);
                    break;
                }
            }
        }

        // ------------------------------------------------ 小分类

        /// <summary>「枪种/具体名」「人物名/泛称」这种识别残渣，拆开只留具体那一半。</summary>
        internal static string SplitKind(string nm, out string hint)
        {
            hint = "";
            if (string.IsNullOrEmpty(nm)) return "";
            int i = nm.IndexOf('/');
            if (i <= 0 || i >= nm.Length - 1) return nm;
            string a = nm.Substring(0, i).Trim();
            string b = nm.Substring(i + 1).Trim();
            bool ka = KindWord(a), kb = KindWord(b);
            if (ka && !kb) { hint = a; return b; }
            if (kb && !ka) { hint = b; return a; }
            return nm;
        }

        private static readonly string[] KindWords =
        {
            "步枪", "冲锋枪", "霰弹枪", "狙击枪", "手枪", "机枪", "近战", "枪械", "爆炸物",
            "幸存者", "特感", "投掷物", "医疗包", "止痛药", "肾上腺素", "药品", "其他"
        };

        private static bool KindWord(string s)
        {
            // 「MAC-10」这种带前缀的也算枪种词，否则「冲锋枪/MAC-10」拆不开
            foreach (string k in KindWords) if (s.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>小分类：大类下面再分一层（枪械 → 步枪 → AK-47）。</summary>
        public static string SubOf(string cat, string name, string key, string hint, string gsub)
        {
            string n = name ?? "";
            switch (cat)
            {
                case "枪械":
                {
                    string k = GunKindOf(n, key);
                    if (k != "其他枪械") return k;
                    // 「A/其他」这种残渣会把「其他」当成枪种词拆出来，别让它变成小分类名
                    if (hint != null && hint.Length > 0 && hint != "其他" && hint != "枪械") return hint;
                    // 还是推不出来时，用识别出来的细分兜一层（人工改名之后常这样）
                    if (IsGunKindName(gsub)) return gsub.Trim();
                    return k;
                }
                case "人物":
                {
                    string c = CharKindOf(n);
                    if (c != "八人包") return c;
                    // 名字可能是英文（人工改的位置名、英文包名）：先用别名表换成中文再判一次。
                    // 否则 "Zoey" 会自己在「其他人物」占一行，而标准位置「佐伊」还空着、又补一行，
                    // 树上就出现两个其实是同一个人的位置。
                    string alias = Find(CharPairs, n);
                    return alias == null ? c : CharKindOf(alias);
                }
                case "药品": return MedKindOf(n, key);
                // 这三个大类和枪械平级，而且各自只有一层（PosOrder 里 近战→近战、投掷物→投掷物…）。
                // 所以不能把 GunKindOf 的结果直接当小分类：名字里碰巧带「冲锋」「rifle」的包，
                // 会在「近战」大类底下冒出一个「冲锋枪」小分类，SubRank 还会把它排到该类第一位。
                case "近战": case "投掷物": case "弹药升级":
                {
                    string k = GunKindOf(n, key);
                    return k == cat ? k : cat;
                }
                case "僵尸": case "特感":          // 老配置里大类还叫「特感」，一起接住
                {
                    // 用户 2026-11-21 定：大类叫「僵尸」，下一级两个 ——「小僵尸」和「特感」。
                    // 哪个归小僵尸由位置表说了算（普通感染者 + 七种非普通），别在这里另抄一份名单。
                    Positions.Row pr = Positions.Find(n);
                    if (pr != null && !string.IsNullOrEmpty(pr.Sub)) return pr.Sub;
                    if (n.IndexOf("小僵尸", StringComparison.Ordinal) >= 0) return "小僵尸";
                    return n.IndexOf("普通", StringComparison.Ordinal) >= 0 ? "小僵尸" : "特感";
                }
                // 杂项里「能搬能扔」的那些（油桶 / 煤气罐 / 弹药堆）单独归一层，
                // 别和特效、火焰这类真·杂项混在一堆
                case "杂项":
                    if (IsPropName(n)) return "可搬道具";
                    break;
            }
            string s = (gsub ?? "").Trim();
            if (s.Length > 0 && s != "其他") return s;
            return "其他杂项";
        }

        /// <summary>是不是 PosOrder 里登记过的「可搬道具」。</summary>
        private static bool IsPropName(string nm)
        {
            if (string.IsNullOrEmpty(nm)) return false;
            for (int i = 0; i < PosOrder.GetLength(0); i++)
                if (PosOrder[i, 0] == "可搬道具" && PosOrder[i, 1] == nm) return true;
            return false;
        }

        internal static string GunKindOf(string nm, string key)
        {
            string s = ((nm ?? "") + " " + (key ?? "")).ToLowerInvariant();
            // 顺序有讲究：带 rifle 的「15发木狙/鸟狙」得先被狙击认走，否则会被步枪抢
            if (HasAny(s, "awp", "scout", "military", "hunting", "sniper", "鸟狙", "猎枪", "狙击", "狙"))
                return "狙击枪";
            if (HasAny(s, "pump", "chrome", "spas", "autoshotgun", "shotgun", "木喷", "铁喷", "连喷", "霰弹"))
                return "霰弹枪";
            if (HasAny(s, "uzi", "mp5", "mac10", "mac-10", "silenced", "消音", "smg", "冲锋"))
                return "冲锋枪";
            if (HasAny(s, "ak47", "ak-47", "m16", "scar", "sg552", "galil", "m4", "ar15", "ar-15", "desert", "rifle", "步枪"))
                return "步枪";
            // 榴弹发射器带 grenade，必须排在投掷物前面，否则会被当成手雷
            if (HasAny(s, "grenade_launcher", "launcher", "榴弹", "m60", "machinegun", "机枪", "特殊武器"))
                return "特殊武器";
            if (HasAny(s, "molotov", "pipebomb", "vomitjar", "vomit", "grenade", "投掷", "燃烧瓶", "土制", "胆汁"))
                return "投掷物";
            if (HasAny(s, "magnum", "pistol", "手枪", "马格南"))
                return "手枪";
            // 近战：14 种游戏自带的 + CS 小刀。只认得出「近战」的（没写型号）留在最后那条兜底
            if (HasAny(s, "katana", "machete", "crowbar", "fireaxe", "chainsaw", "nightstick", "tonfa",
                          "cricket", "baseball", "golf", "pitchfork", "shovel", "knife",
                          "武士刀", "砍刀", "撬棍", "消防斧", "电锯", "警棍", "板球", "棒球",
                          "高尔夫", "干草叉", "铁锹", "铁铲", "小刀",
                          "平底锅", "斧", "锤", "吉他", "球棍", "球棒", "近战"))
                return "近战";
            // 升级箱：给枪加燃烧 / 爆破 / 激光。带「榴弹」的早在特殊武器那条被接走了
            if (HasAny(s, "incendiary", "explosive", "laser", "燃烧弹药", "爆破弹药", "弹药包",
                          "高爆", "激光", "升级"))
                return "弹药升级";
            return "其他枪械";
        }

        private static string CharKindOf(string nm)
        {
            string n = nm ?? "";
            if (HasAny(n, "tank", "坦克", "witch", "女巫", "hunter", "smoker", "boomer", "spitter",
                          "jockey", "charger", "特感"))
                return "特感";
            if (HasAny(n, "比尔", "佐伊", "路易斯", "弗朗西斯")) return "一代";
            if (HasAny(n, "教练", "尼克", "埃利斯", "罗谢尔")) return "二代";
            return "八人包";
        }

        private static string MedKindOf(string nm, string key)
        {
            string s = ((nm ?? "") + " " + (key ?? "")).ToLowerInvariant();
            if (HasAny(s, "medkit", "医疗包", "除颤", "电击", "defib")) return "治疗与复活";
            if (HasAny(s, "pill", "止痛", "adrenaline", "肾上腺", "针")) return "暂时增益";
            return "其他药品";
        }

        private static readonly string[] GunKindNames =
        { "步枪", "冲锋枪", "霰弹枪", "狙击枪", "手枪", "特殊武器", "近战", "投掷物" };

        private static bool IsGunKindName(string s)
        {
            s = (s ?? "").Trim();
            foreach (string k in GunKindNames) if (s == k) return true;
            return false;
        }

        private static bool HasAny(string s, params string[] keys)        {
            foreach (string k in keys)
                if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static readonly string[] SubOrder =
        {
            "步枪", "冲锋枪", "霰弹枪", "狙击枪", "手枪", "特殊武器", "近战", "投掷物", "弹药升级", "其他枪械",
            "一代", "二代", "特感", "小僵尸", "普通感染者", "非普通感染者", "八人包",
            "治疗与复活", "暂时增益", "其他药品",
            // 杂项里的「整套替换」三栏排在最前：它们的包通常十几件，比零散道具更该先看到
            "UI 界面", "特效", "音效", "弹道",
            "可搬道具", "特效杂项", "其他杂项", "平台"
        };

        public static int SubRank(string sub)
        {
            for (int i = 0; i < SubOrder.Length; i++) if (SubOrder[i] == sub) return i;
            return 500;
        }

        // 同一小分类里谁排前面：照游戏里武器的默认顺序来（用户也是这么记的）。
        // 这张表同时也是「游戏里一共有哪些东西」的清单 —— 空位（库里没有 MOD）就照着它补。
        // 位置表只有一份：Positions.cs。这里只取它的二维视图（小分类, 显示名），
        // 顺序就是游戏默认顺序；新增位置去 Positions.cs 加一行，不要再往这里补名字。
        private static readonly string[,] PosOrder = Positions.Order();

        /// <summary>这个名字在标准位置表里吗（给小分类归属用）。</summary>
        internal static bool StandardName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < PosOrder.GetLength(0); i++)
                if (PosOrder[i, 1] == name) return true;
            return false;
        }

        public static int NameRank(string sub, string name)
        {
            for (int i = 0; i < PosOrder.GetLength(0); i++)
                if (PosOrder[i, 0] == sub && PosOrder[i, 1] == name) return i;
            return 1000;
        }

        /// <summary>两把不同的枪可能都叫「鸟狙」，加个尾巴区分开，否则树里看着像重复项。</summary>
        private static void Disambiguate(List<Slot> list)
        {
            Dictionary<string, int> freq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Slot s in list)
            {
                string k = s.Cat + "\u0001" + s.Name;      // 不同大类下同名不算重复
                int n;
                freq.TryGetValue(k, out n);
                freq[k] = n + 1;
            }
            foreach (Slot s in list)
            {
                int n;
                if (!freq.TryGetValue(s.Cat + "\u0001" + s.Name, out n) || n < 2) continue;
                s.Name = s.Name + " · " + Tag(s.Key);
            }
        }

        /// <summary>
        /// 重名时缀在名字后面的短标签。传进来的是槽位键（「大类\u0001位置键」），
        /// 先剥掉大类，再剥掉 c: / sub: / slot: 这些内部前缀 —— 否则标签会变成
        /// 「枪械C:鸟狙」这种，中间那个 \u0001 还会在界面上显示成方块。
        /// </summary>
        internal static string Tag(string key)
        {
            string k = key ?? "";
            int sep = k.IndexOf('\u0001');
            if (sep >= 0) k = k.Substring(sep + 1);
            if (k.StartsWith("#empty:", StringComparison.Ordinal)) k = k.Substring(7);
            else if (k.StartsWith("slot:", StringComparison.Ordinal))
            {
                k = k.Substring(5);
                int sl = k.LastIndexOf('/');
                if (sl >= 0) k = k.Substring(sl + 1);
            }
            else if (k.StartsWith("c:", StringComparison.Ordinal)) k = k.Substring(2);
            else if (k.StartsWith("sub:", StringComparison.Ordinal)) k = k.Substring(4);
            if (k.StartsWith("w_", StringComparison.OrdinalIgnoreCase)) k = k.Substring(2);
            else if (k.StartsWith("v_", StringComparison.OrdinalIgnoreCase)) k = k.Substring(2);
            int us = k.LastIndexOf('_');
            if (us >= 0 && us < k.Length - 1) k = k.Substring(us + 1);
            return k.ToUpperInvariant();
        }

        // ------------------------------------------------ 空位置

        /// <summary>
        /// 反查：这个名字在标准位置表里属于哪个大类 / 小分类。
        /// 识别那边「一个包只改了一件东西的 HUD 图标」时用得上 —— 图标画的是谁，包就该归到谁下面。
        /// </summary>
        public static bool PosOf(string name, out string cat, out string sub)
        {
            cat = ""; sub = "";
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < PosOrder.GetLength(0); i++)
                if (PosOrder[i, 1] == name)
                { sub = PosOrder[i, 0]; cat = CatOfSub(sub); return true; }
            return false;
        }

        /// <summary>小分类 → 大类。跟 SubOf 反着来，只覆盖 PosOrder 里那些标准位置用到的。</summary>
        internal static string CatOfSub(string sub)      // 位置表配套：小分类 → 大类，识别层也要用
        {
            switch (sub)
            {
                case "步枪": case "冲锋枪": case "霰弹枪": case "狙击枪":
                case "手枪": case "特殊武器":
                    return "枪械";
                // 这三样不算枪：各自一个大类，和枪械平级（撬棍 / 燃烧瓶 / 弹药升级箱）
                case "近战": return "近战";
                case "投掷物": return "投掷物";
                case "弹药升级": return "弹药升级";
                case "治疗与复活": case "暂时增益": case "其他药品":
                    return "药品";
                case "可搬道具": case "其他杂项": case "弹道":
                    return "杂项";
                case "特感": case "小僵尸": case "普通感染者": case "非普通感染者":
                    return "僵尸";
                case "八人包":
                    return "人物";
                // 场景自己一个大类（用户 2026-11-19 定）：天空盒 / 地面细节 / 场景贴图，
                // 换的不是某个人，挂在人物底下看着像要换角色。
                case "场景": case "天空盒": case "地面细节": case "场景贴图":
                    return "场景";
                case "平台":
                    return "平台";
            }
            return "人物";                       // 一代 / 二代
        }

        /// <summary>
        /// 把「标准位置」补齐：库里没有对应 MOD 的枪 / 人物 / 药品也各占一行。
        /// 树本来就是一张「这个游戏里能换什么」的清单 —— 空着的那几行正好告诉人还缺什么，
        /// 也能先认位置再去找包，而不是对着一片空白猜自己少装了哪把枪。
        /// 名字和顺序都取 PosOrder 那一套（跟游戏里武器排列一致）；已经有位置的不重复加。
        /// </summary>
        private static void EnsureStandard(List<Slot> list)
        {
            List<string> cats = CatPrefs.Cats();
            for (int i = 0; i < PosOrder.GetLength(0); i++)
            {
                string sub = PosOrder[i, 0], nm = PosOrder[i, 1];
                string cat = CatOfSub(sub);
                // 大类被改名或删掉了：别拿旧名字再造一个空分类回来
                if (cats.IndexOf(cat) < 0) continue;
                if (HasStandard(list, cat, nm)) continue;

                Slot s = new Slot();
                s.Key = cat + "\u0001#empty:" + nm;
                s.Name = nm;
                s.Cat = cat;
                s.Sub = sub;
                s.Empty = true;
                list.Add(s);
            }

            string miss = UncoveredNames();
            if (miss.Length > 0)
                Core.Log("INFO", "TREE 这些名字没有标准位置（出现时会落在「未归类」，可手动指定）：" + miss);
        }

        /// <summary>这个大类下是不是已经有叫这个名字的位置（Disambiguate 加过「· AK47」尾巴的也算）。</summary>
        private static bool HasSlot(List<Slot> list, string cat, string nm)
        {
            foreach (Slot s in list)
            {
                if (s.Cat != cat) continue;
                if (s.Name == nm) return true;
                if (s.Name.StartsWith(nm + " · ", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>别的大类里已经有同名的真实位置（比如「燃烧弹药」被识别成了杂项）。</summary>
        private static bool NamedAnywhere(List<Slot> list, string nm)
        {
            foreach (Slot s in list)
            {
                if (s.Empty) continue;                    // 只看真货，空位不算
                if (s.Name == nm) return true;
                if (s.Name.StartsWith(nm + " · ", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>这个标准位置算不算「已经有了」：自己那类里有，或者别处已经有了同名真位置。</summary>
        private static bool HasStandard(List<Slot> list, string cat, string nm)
        {
            return HasSlot(list, cat, nm) || NamedAnywhere(list, nm);
        }

        /// <summary>自检用：标准位置里还有几个没进树（既没有 MOD 也没占位）。正常是 0。</summary>
        public static int MissingStandard()
        {
            List<string> cats = CatPrefs.Cats();
            List<Slot> list = Build();
            int miss = 0;
            for (int i = 0; i < PosOrder.GetLength(0); i++)
            {
                string cat = CatOfSub(PosOrder[i, 0]), nm = PosOrder[i, 1];
                if (cats.IndexOf(cat) < 0) continue;
                if (!HasStandard(list, cat, nm)) miss++;
            }
            return miss;
        }

        /// <summary>自检用：树里一共有几个空位置。</summary>
        public static int EmptyCount(List<Slot> list)
        {
            int n = 0;
            foreach (Slot s in list) if (s.Empty) n++;
            return n;
        }

        // ------------------------------------------------ 识别表对账

        /// <summary>
        /// 识别得出、但故意不给空位的名字，后面写理由。
        /// 有了这张豁免表，核对就能做到「整张识别表 100% 有着落」，同时不往树里塞没意义的空行。
        /// </summary>
        private static readonly string[] NoSlotNames =
        {
            "石头",   // concrete_spawnchunk：地图里碎混凝土块的共用模型，不是一个能单独换的道具
        };

        /// <summary>位置表里有没有这个名字（不管在哪个小分类下）。</summary>
        private static bool HasPosition(string nm)
        {
            for (int i = 0; i < PosOrder.GetLength(0); i++)
                if (PosOrder[i, 1] == nm) return true;
            return false;
        }

        /// <summary>位置表登记了多少个位置。</summary>
        public static int PositionCount { get { return PosOrder.GetLength(0); } }

        /// <summary>
        /// 识别表里的名字，有几个在位置表里没有家（返回「名字/名字」，正常是空串）。
        /// 不是空 = 识别表新加了一种东西、位置表忘了跟 —— 那种包真出现时只会掉进未归类，
        /// 树上看着像「没装这个 MOD」，其实装了没认出来。
        /// </summary>
        public static string UncoveredNames()
        {
            List<string> miss = new List<string>();
            foreach (string n in ModIdentify.Subjects())
            {
                if (HasPosition(n)) continue;
                if (Array.IndexOf(NoSlotNames, n) >= 0) continue;
                miss.Add(n);
            }
            return string.Join("/", miss.ToArray());
        }

        /// <summary>识别表里一共登记了多少种东西。</summary>
        public static int SubjectCount { get { return ModIdentify.Subjects().Count; } }

        /// <summary>
        /// 位置表和分类规则对不对得上：把每个标准位置反推一遍小分类，应该正好回到它自己那一层。
        /// 对不上 = 同一个东西会被分成两行（位置表写「铁锹」、规则却按「铁铲」另起一行那种）。
        /// </summary>
        public static string MisfiledNames()
        {
            List<string> bad = new List<string>();
            for (int i = 0; i < PosOrder.GetLength(0); i++)
            {
                string sub = PosOrder[i, 0], nm = PosOrder[i, 1];
                string got = SubOf(CatOfSub(sub), nm, "", "", "");
                if (got != sub) bad.Add(nm + "→" + got + "（表里写的是 " + sub + "）");
            }
            return string.Join("，", bad.ToArray());
        }

        /// <summary>这个 MOD 替换的位置名（人工优先，其次识别结果）。搜索和树共用一套说法。</summary>
        public static string SlotNameOf(ModGroup g)
        {
            ModItem m = g == null ? null : g.Main;
            if (m == null) return "";
            string hand = CatPrefs.SlotOf(m).Trim();
            if (hand.Length > 0) return hand;
            string nm = (m.Subject ?? "").Trim();
            return IsCountName(nm) ? "" : nm;
        }

        /// <summary>弹药堆：识别表给的显示名是「弹药」，名字里带「弹药堆」的也算。</summary>
        internal static bool IsAmmoPile(string nm)
        {
            string n = (nm ?? "").Trim();
            return n == "弹药" || n.IndexOf("弹药堆", StringComparison.Ordinal) >= 0;
        }

        internal static bool IsCountName(string s)
        {
            if (s == null || s.Length < 2 || s[s.Length - 1] != '件') return false;
            for (int i = 0; i < s.Length - 1; i++)
                if (!char.IsDigit(s[i]) && s[i] != ' ') return false;
            return true;
        }

        internal static bool Vague(string s)
        {
            return s == "步枪" || s == "冲锋枪" || s == "霰弹枪" || s == "投掷物"
                || s == "手枪" || s == "机枪" || s == "狙击枪" || s == "特殊武器"
                || s == "爆炸物" || s == "其他枪械" || s == "其他枪"
                || s == "幸存者" || s == "人物" || s == "近战" || s == "近战武器"
                || s == "其他" || s.Length == 0;
        }



        // 泛称补全用：关键词 → 标准位置名。长的排前面，免得 "M4" 抢走 "M4A1"。
        private static readonly string[,] GunPairs = new string[,]
        {
            {"30发连狙","30发军用狙"},{"30连","30发军用狙"},{"军用狙","30发军用狙"},
            {"15发连狙","15发木狙"},{"15连","15发木狙"},{"木狙","15发木狙"},
            {"一代连喷","一代连喷"},{"二代连喷","二代连喷"},
            {"sg552","sg552"},
            {"AK-47","AK-47"},{"AK47","AK-47"},
            {"SCAR","SCAR-L"},{"三连发","SCAR-L"},
            
            {"M16","M16"},{"AWP","AWP"},{"MP5","MP5"},{"M60","M60"},
            {"冲锋枪","UZI"},{"MAC-10","MAC-10"},{"MAC10","MAC-10"},
            {"铁喷","铁喷"},{"铁盆","铁喷"},{"木喷","木喷"},
            {"鸟狙","鸟狙"},{"猎枪","15发木狙"},{"连狙","30发军用狙"},
            {"榴弹","榴弹发射器"},
            {"燃烧瓶","燃烧瓶"},{"土制","土制炸弹"},{"胆汁","胆汁"},
            // 升级箱：名字跟 Identify 对齐（弹药包 / 激光瞄准）
            {"燃烧弹药","燃烧弹药包"},{"燃烧弹","燃烧弹药包"},
            {"爆破弹药","爆破弹药包"},{"高爆","爆破弹药包"},
            {"激光瞄准","激光瞄准"},{"激光","激光瞄准"},
            // 近战：长名字排前面，免得「板球拍」被「球棍」先接走
            {"武士刀","武士刀"},{"katana","武士刀"},
            {"砍刀","砍刀"},{"machete","砍刀"},
            {"撬棍","撬棍"},{"crowbar","撬棍"},
            {"消防斧","消防斧"},{"fireaxe","消防斧"},{"fire_axe","消防斧"},
            {"电锯","电锯"},{"chainsaw","电锯"},
            {"平底锅","平底锅"},{"frying","平底锅"},
            {"高尔夫","高尔夫球杆"},{"golf","高尔夫球杆"},
            {"板球","板球拍"},{"cricket","板球拍"},
            {"棒球棍","棒球棍"},{"球棍","棒球棍"},{"baseball","棒球棍"},
            {"电吉他","电吉他"},{"吉他","电吉他"},{"guitar","电吉他"},
            {"警棍","警棍"},{"nightstick","警棍"},{"tonfa","警棍"},
            {"干草叉","干草叉"},{"pitchfork","干草叉"},
            {"铁锹","铁锹"},{"铁铲","铁锹"},{"shovel","铁锹"},
            {"小刀","小刀"},{"knife","小刀"},
            {"马格南","马格南"},
        };

        private static readonly string[,] CharPairs = new string[,]
        {
            {"弗朗西斯","弗朗西斯"},{"Francis","弗朗西斯"},
            {"埃利斯","埃利斯"},{"Ellis","埃利斯"},
            {"路易斯","路易斯"},{"Louis","路易斯"},
            {"罗谢尔","罗谢尔"},{"Rochelle","罗谢尔"},
            {"教练","教练"},{"Coach","教练"},
            {"尼克","尼克"},{"Nick","尼克"},
            {"佐伊","佐伊"},{"Zoey","佐伊"},
            {"比尔","比尔"},{"Bill","比尔"},
        };

        /// <summary>「冲锋枪/MAC-10」里是不是正好有 MAC-10 这一半。</summary>
        private static bool HasHalf(string nm, string one)
        {
            if (string.IsNullOrEmpty(nm) || string.IsNullOrEmpty(one)) return false;
            foreach (string p in nm.Split('/'))
                if (p.Trim() == one) return true;
            return false;
        }

        /// <summary>泛称 → 具体位置名。先看 MOD 所在目录，再看 MOD 自己的名字。</summary>
        private static string Concrete(string vague, ModGroup g, ModItem m)
        {
            string dir = "";
            string rel = m == null ? "" : (m.RelPath ?? "");
            int i = rel.LastIndexOf('\\');
            if (i > 0)
            {
                int j = rel.LastIndexOf('\\', i - 1);
                dir = j >= 0 ? rel.Substring(j + 1, i - j - 1) : rel.Substring(0, i);
            }

            bool charFirst = vague == "幸存者" || vague == "人物";
            // 注释说「先看目录再看名字」，实现也得真的这样：以前是把目录名、组名、条目名
            // 拼成一整串再去 Find，命中哪条全看 GunPairs 的表序 —— 「冲锋枪/MAC-10」放在 MAC-10
            // 目录里，照样会被表里靠前的 冲锋枪 抢走。
            string hit = charFirst ? Find(CharPairs, dir) : Find(GunPairs, dir);
            // vague 为空 = 调用方不知道这是枪还是人（A/B 拆不开、名字认不出来）：
            // 目录名里两种词表都得试一遍，否则「埃利斯」这种纯人名的目录会被跳过，
            // 转头去包名里捞到一个枪名，整个人物包就被补到枪械位置上了。
            if (hit == null && !charFirst && vague.Length == 0) hit = Find(CharPairs, dir);
            if (hit == null)
            {
                string text = (g == null ? "" : (g.Name ?? "")) + " " + (m == null ? "" : (m.Name ?? ""));
                hit = charFirst ? Find(CharPairs, text) : Find(GunPairs, text);
                if (hit == null && vague.Length == 0) hit = Find(CharPairs, text);
            }
            return hit ?? "";
        }

        private static string Find(string[,] pairs, string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            for (int i = 0; i < pairs.GetLength(0); i++)
                if (text.IndexOf(pairs[i, 0], StringComparison.OrdinalIgnoreCase) >= 0) return pairs[i, 1];
            return null;
        }

        /// <summary>把方案树摊平成文本，方便不开界面核对分类对不对（--dumptree）。</summary>
        public static string DumpTree()
        {
            List<Slot> slots = Build();
            List<string> order = CatOrder(slots);
            slots.Sort(delegate(Slot a, Slot b)
            {
                int ia = order.IndexOf(a.Cat), ib = order.IndexOf(b.Cat);
                if (ia != ib) return ia.CompareTo(ib);
                int sa = SubRank(a.Sub), sb2 = SubRank(b.Sub);
                if (sa != sb2) return sa.CompareTo(sb2);
                int na = NameRank(a.Sub, a.Name), nb = NameRank(b.Sub, b.Name);
                if (na != nb) return na.CompareTo(nb);
                int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string cat in order)
            {
                List<Slot> inCat = new List<Slot>();
                foreach (Slot s in slots) if (s.Cat == cat) inCat.Add(s);
                if (inCat.Count == 0) continue;
                sb.AppendLine();
                sb.AppendLine("【" + cat + "】");
                // 只有一层分组的大类不画那行标题，跟界面里一致
                int subCount = 0;
                string prev = null;
                foreach (Slot s in inCat) if (s.Sub != prev) { prev = s.Sub; subCount++; }
                bool showSub = subCount > 1;
                string lastSub = null;
                foreach (Slot s in inCat)
                {
                    if (showSub && s.Sub != lastSub) { sb.AppendLine("  〈" + s.Sub + "〉"); lastSub = s.Sub; }
                    sb.AppendLine("    " + s.Name + "  (" + s.Items.Count + " 个)"
                        + (s.Manual ? " [人工]" : ""));
                }
            }
            string f = System.IO.Path.Combine(Core.LogDir, "方案树.txt");
            System.IO.File.WriteAllText(f, sb.ToString(), new System.Text.UTF8Encoding(true));
            return f;
        }

        /// <summary>树里的大类顺序：跟着分类顺序走，地图/喷漆不进树。</summary>
        /// <summary>
        /// 方案树 / 总览共用的排序：大类 → 小分类 → 手工拖过的位置 → 名字。
        /// isOn 只是平局时让已装的排前面，传 null 表示不看勾选状态。
        /// </summary>
        public static void SortForTree(List<Slot> slots, Func<Slot, bool> isOn)
        {
            List<string> order = CatOrder(slots);
            slots.Sort(delegate(Slot a, Slot b)
            {
                int ia = order.IndexOf(a.Cat), ib = order.IndexOf(b.Cat);
                if (ia != ib) return ia.CompareTo(ib);
                int sa = CatTree.SubSortKey(a), sb = CatTree.SubSortKey(b);
                if (sa != sb) return sa.CompareTo(sb);
                int pa = CatTree.PosSortKey(a), pbb = CatTree.PosSortKey(b);
                bool ha = pa >= 0, hb = pbb >= 0;
                if (ha != hb) return ha ? -1 : 1;
                if (ha && pa != pbb) return pa.CompareTo(pbb);
                if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
                int na = NameRank(a.Sub, a.Name), nb = NameRank(b.Sub, b.Name);
                if (na != nb) return na.CompareTo(nb);
                int ra = CatPrefs.SlotRank(a.Cat, a.Name), rb = CatPrefs.SlotRank(b.Cat, b.Name);
                if (ra != rb) return ra.CompareTo(rb);
                if (isOn != null)
                {
                    bool x = isOn(a), y = isOn(b);
                    if (x != y) return x ? -1 : 1;
                }
                int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
        }

        public static List<string> CatOrder(List<Slot> slots)        {
            List<string> order = new List<string>();
            foreach (string c in CatPrefs.Cats())
                if (c != "地图" && c != "喷漆" && !order.Contains(c)) order.Add(c);
            foreach (string c in Catalog.Cats)
                if (c != "地图" && c != "喷漆" && !order.Contains(c)) order.Add(c);
            foreach (Slot s in slots) if (!order.Contains(s.Cat)) order.Add(s.Cat);
            return order;
        }
    }

    /// <summary>树里的一行：大类标题、小分类标题，或者一个槽位。</summary>
    internal sealed class TreeRow
    {
        public bool IsCat;
        public bool IsSub;       // 小分类标题：枪械 →「步枪」
        public string Cat;
        public string Sub;
        public Slot Slot;
    }

    // ============================================================
    //  方案树视图
    // ============================================================
    /// <summary>一个 18px 高的小开关（pill + 圆钮）：模式类选项用它，比复选框更符合规范。</summary>
    internal sealed class SwitchBox : Control
    {
        private bool on;
        private string caption = "";
        public event EventHandler Toggled;
        public SwitchBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = Theme.UISmall;
            Height = Dpi.S(22);
        }
        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                Invalidate();
                if (Toggled != null) Toggled(this, EventArgs.Empty);
            }
        }
        public string Caption
        {
            get { return caption; }
            set { caption = value ?? ""; Invalidate(); }
        }
        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            int tw = TextRenderer.MeasureText(caption, Font).Width;
            return new Size(Dpi.S(34) + 8 + tw + 4, Dpi.S(22));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int w = Dpi.S(34), h = Dpi.S(18);
            int y = (Height - h) / 2;
            using (SolidBrush b = new SolidBrush(on ? Theme.Info : Color.FromArgb(206, 212, 220)))
                g.FillPath(b, Theme.Round(new Rectangle(0, y, w, h), h / 2));
            int kd = h - Dpi.S(4);
            int kx = on ? w - kd - Dpi.S(2) : Dpi.S(2);
            using (SolidBrush b = new SolidBrush(Color.White))
                g.FillEllipse(b, kx, y + Dpi.S(2), kd, kd);
            int tx = w + Dpi.S(8);
            TextRenderer.DrawText(g, caption, Font, new Rectangle(tx, 0, Width - tx, Height),
                Theme.Ink2, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
    internal sealed class CatTree : UserControl
    {
        private readonly MainForm mf;
        private readonly ScrollHost scroll;
        private ColHead headMain, headExtra;
        // ---- 工坊推荐区（2026-11-21 用户定：中间候选列的下半截用来推荐工坊里同类的 MOD）----
        private Panel recPanel;
        private Label recTitle;
        private RecGrid recGrid;                               // 自绘卡片墙：带边框、悬停高亮、没有滚动条
        private int recGen;                                    // 换位置后，在途的旧结果作废
        private string recKey = "";
        private int recPage;                                   // 上一批推荐用的页码（换一换要避开它）
        private readonly FlatButton btnRecShuffle = new FlatButton();
        private int recDetailToken;                     // 悬停换条目后，在途的大图作废
        private Image recBigPic;                        // 详情栏当前那张大图（我们自己管释放）
        // 每次开程序推荐不同的东西：搜索页码随机（1~3），不落缓存；缓存只留一份防同一次会话重复拉
        private static readonly Random recRnd = new Random();
        private readonly SlotTree body;
        private readonly ListView lv;
        private readonly ListView lvExtra;                 // 下半：可叠加的扩展
        private readonly Label lblTitle, lblCur, lblFoot;
        private readonly Label lblExtraTitle;
        private readonly FlatButton btnPick;
        private readonly ToolTip tipExtra;                 // 「可叠加的扩展」小标题的悬停说明
        private readonly ToolTip tipRow = new ToolTip();    // 列表行悬停：用 ItemMouseHover 触发，比 MouseMove 可靠
        private int tipShownAt = -1;                        // 当前挂着的那一行（-1 = 没挂）：换行要先收旧的
        private SwitchBox chkMulti;                        // 「允许多选」：这个位置可以同时开好几个 MOD（模式类选项，用开关）
        private bool applyingMulti;                        // 程序自己改勾选时别触发联动
        private CoverPeek peek;                            // 封面预览：两列时是候选区上方的横条，三列时是右侧详情栏
        private Label lblDetail;                           // 三列布局时右栏的小标题
        private int peekIdx = -2;                          // 当前预览的是第几行（-2 = 还没算过，-1 = 没悬停）
        private Panel splitA, splitB;                     // 两处分栏拖条（树|候选、候选|详情）
        private int treeWpx, detailWpx;                   // 拖过分栏就用这里记着（0 = 按默认比例自动算）
        private int curTreeW, curDetailW;                 // 上一次排版算出来的实际宽度（拖条要用）
        private bool widthsLoaded;                        // 配置里的栏宽只读一次
        private int splitStartCursor, splitStartVal;      // 拖动起点
        private char splitMode;                           // 'A' = 树宽，'B' = 详情宽

        private List<Slot> slots = new List<Slot>();
        private int unmatched;                       // 一个位置都归不上的 MOD 数
        private readonly List<ModGroup> unmatchedGroups = new List<ModGroup>();   // 具体是哪几组（未归类页列出来）
        private readonly HashSet<string> folded = new HashSet<string>(StringComparer.Ordinal);
        private string selKey = "";

        public string SelectedKey { get { return selKey; } }
        public bool IsFolded(string cat) { return folded.Contains(cat); }
        /// <summary>自检 / 截图用：树本体（行号、拖动预览这些都在它身上）。</summary>
        internal SlotTree Body { get { return body; } }
        /// <summary>台面用：树排好的位置表（顺序也在这上面，台面照它的顺序建卡）。</summary>
        internal List<Slot> Slots { get { return slots; } }
        /// <summary>按分支名找分支槽（台面右键重命名用）。</summary>
        internal Slot BranchByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Slot s in slots)
                if (IsBranchSlot(s) && string.Equals(s.Name, name, StringComparison.Ordinal)) return s;
            return null;
        }

        /// <summary>把每一行所有格子的底色对齐成整行那个色（行底色是按格子刷的，见 ApplyStateCells）。</summary>
        private static void SyncRowBacks(ListView v)
        {
            if (v == null) return;
            foreach (ListViewItem li in v.Items)
            {
                Color back = li.BackColor;
                for (int i = 0; i < li.SubItems.Count; i++)
                    if (li.SubItems[i].BackColor != back) li.SubItems[i].BackColor = back;
            }
        }

        /// <summary>换主题后重建状态点：位图里存的是旧颜色，不重建会留在深底上看不清。</summary>
        internal void OnThemeChanged()
        {
            try
            {
                if (lv != null)
                {
                    ImageList old = lv.StateImageList;
                    lv.StateImageList = Dots();
                    if (old != null) old.Dispose();
                }
                if (lvExtra != null)
                {
                    ImageList old = lvExtra.StateImageList;
                    lvExtra.StateImageList = Dots();
                    if (old != null) old.Dispose();
                }
                if (scroll != null) scroll.BackColor = Theme.TreeBg;
                BackColor = Theme.Surface;
                body.BackColor = Theme.TreeBg;
                // 换主题只改颜色、不重建行：底色是按「整行」刷到每个格子上的，
                // 上面那轮重映射只要有格子没跟上，行就会从中间断开 —— 这里对齐一遍。
                SyncRowBacks(lv);
                SyncRowBacks(lvExtra);
                LayoutSelf();          // 列宽也按当前客户区重算，别留着上一个主题时算的宽度
                Invalidate(true);
            }
            catch { }
        }

        public CatTree(MainForm owner)
        {
            mf = owner;
            BackColor = Theme.Surface;
            Font = Theme.UI;
            // UserControl 默认会跟着父窗体再缩放一次：父窗体已经把这块控件放到放大后的位置了，
            // 这里再缩一次就是双倍。树内部的行高由 RowH 自己按 DPI 算。
            AutoScaleMode = AutoScaleMode.None;

            // ---- 左：树 ----
            scroll = new ScrollHost();
            scroll.BackColor = Theme.TreeBg;
            Controls.Add(scroll);

            body = new SlotTree(this);
            body.BackColor = Theme.TreeBg;
            scroll.Attach(body);
            // 树比可视区矮的时候，下面这块空白是 ScrollHost 的，不接这个就「右键空白处没菜单」
            scroll.BlankMenu = delegate(Point at) { TreeMenuFor(null, at); };

            // ---- 右上：换本体的候选 ----
            lv = NewCandList("MOD");
            lv.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;      // 右键只是弹菜单，别顺手换掉 MOD
                ListViewItem li = lv.GetItemAt(e.X, e.Y);
                if (li == null) return;
                // 点「开关」那一列 = 只翻 addonlist 的 1/0，不换 MOD；点别处还是老行为（换掉这个位置）
                if (ColAt(lv, e.X) == 4) { ToggleSwitch(li.Tag as ModGroup); return; }
                ModGroup g = li.Tag as ModGroup;
                if (selKey == "#search") { RevealGroup(g); return; }        // 搜索结果：跳回它在树里的位置
                Slot cur = Current;
                if (cur != null && cur.Pile) { TogglePile(g); return; }     // 地图 / 我的分支：开 / 关，不替换
                if (IsMulti(cur)) { ToggleMulti(cur, g); return; }          // 允许多选：点一下开、再点一下关
                Choose(g);
            };
            lv.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) SelectRow(lv, e);
            };
            // 双击套装行 = 挑部件（医疗包那种「模型 + 基础材质 + 选一个颜色」的组合）。
            // 单击已经套了默认部件；想换颜色只能走这里，不能让这条路只剩右键菜单里那条。
            lv.DoubleClick += delegate
            {
                if (lv.SelectedItems.Count == 0) return;
                if (selKey == "#unmatched") { mf.SetModCatIn(lv); return; }   // 未归类页：双击 = 手动指定
                ModGroup g = lv.SelectedItems[0].Tag as ModGroup;
                if (g == null || !g.IsSet) return;
                mf.PickPartsIn(lv);
                FillDetail();
            };
            lv.ContextMenuStrip = mf.BuildTreeMenu(lv);
            lv.Resize += delegate { LayoutCols(); };
            // 封面预览：悬停哪行看哪行，移开回落到「当前启用的那个」。
            // 源码原本没有任何 MOD 预览图（CatTree 里的 Thumb() 是滚动条滑块），这块是新加的。
            // 两处分栏拖条：树宽 / 详情宽都能拖，拖完记进配置
            splitA = MakeSplit();
            splitB = MakeSplit();
            Controls.Add(splitA);
            Controls.Add(splitB);

            peek = new CoverPeek();
            Controls.Add(peek);

            lblDetail = new Label();
            lblDetail.Text = Lang.S("详情 · 鼠标划过一行看哪条");
            lblDetail.Font = Theme.UISmall;
            lblDetail.ForeColor = Theme.Ink3;
            lblDetail.BackColor = Color.Transparent;
            lblDetail.Visible = false;
            Controls.Add(lblDetail);
            lv.MouseMove += delegate(object s2, MouseEventArgs e2)
            {
                if (peek == null || !peek.Visible) return;
                int idx = -1;
                ListViewItem hit = lv.GetItemAt(e2.X, e2.Y);
                if (hit != null) idx = hit.Index;
                if (idx == peekIdx) return;                 // 同一行反复触发会一直重解开图，必须挡住
                peekIdx = idx;
                UpdatePeek(hit == null ? null : hit.Tag as ModGroup);
            };
            lv.MouseLeave += delegate
            {
                if (peekIdx == -1) return;
                peekIdx = -1;
                UpdatePeek(null);
            };
            // 未归类页：按住一行往外拖 = 把这个 MOD 拖到左边的树上归位（拖到哪归到哪）。
            // 只有未归类页给拖；其他页的行拖出去没有意义（本来就归好了）。说明行 Tag 为空，天然拖不动。
            lv.ItemDrag += delegate(object s, ItemDragEventArgs e)
            {
                if (selKey != "#unmatched") return;
                // 多选一起拖：三个五个不用逐个来回
                List<ModGroup> picked = new List<ModGroup>();
                foreach (ListViewItem it in lv.SelectedItems)
                {
                    ModGroup gi = it.Tag as ModGroup;
                    if (gi != null && !picked.Contains(gi)) picked.Add(gi);
                }
                ListViewItem li = e.Item as ListViewItem;
                ModGroup g0 = li == null ? null : (li.Tag as ModGroup);
                if (picked.Count == 0 && g0 != null) picked.Add(g0);
                if (picked.Count == 0) return;
                lv.DoDragDrop(picked, DragDropEffects.Move);
            };
            // 拖动期间光标换形状：能放 = 四向移动箭头，不能放 = 禁止圈。
            // 系统默认的「拖动」光标太不明显，用户不知道已经开始拖了 —— GiveFeedback 在源头控制，
            // 全程跟随（悬在树上时 DragOver 给的 Effect 会实时反映到这里）。
            lv.GiveFeedback += delegate(object s, GiveFeedbackEventArgs e)
            {
                e.UseDefaultCursors = false;
                Cursor.Current = (e.Effect & DragDropEffects.Move) != 0 ? Cursors.SizeAll : Cursors.No;
            };
            lv.AccessibleName = "当前替换位置的 MOD 候选";
            lv.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space) return;
                if (lv.SelectedItems.Count == 0) return;
                ModGroup g = lv.SelectedItems[0].Tag as ModGroup;
                if (g == null) return;
                if (selKey == "#search") RevealGroup(g);
                else if (Current != null && Current.Pile) TogglePile(g);
                else Choose(g);
                FillDetail();
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            Controls.Add(lv);
            headMain = new ColHead(lv);
            Controls.Add(headMain);

            // 扩展栏已移除（2026-11-20）：这几个控件只留空壳，永远不显示、也不填内容，
            // 免得旧布局代码踩空（它们以前的创建代码整块删掉了）。
            lvExtra = NewCandList("扩展");
            lvExtra.Visible = false;
            Controls.Add(lvExtra);
            headExtra = new ColHead(lvExtra);
            headExtra.Visible = false;
            Controls.Add(headExtra);

            // ---- 工坊推荐：候选列下半截 ----
            recPanel = new Panel();
            recPanel.BackColor = Theme.Surface;
            recPanel.Visible = false;
            Controls.Add(recPanel);

            recTitle = new Label();
            recTitle.Font = Theme.UISmall;
            recTitle.ForeColor = Theme.Ink3;
            recTitle.BackColor = Color.Transparent;
            recTitle.Text = Lang.S("工坊推荐");
            recPanel.Controls.Add(recTitle);

            recGrid = new RecGrid();
            recGrid.HoverItem += delegate(WsItem it) { ShowRecDetail(it); };
            recGrid.Pick += delegate(WsItem it) { OpenRecPick(it); };
            recPanel.Controls.Add(recGrid);

            btnRecShuffle.Text = Lang.S("换一换");
            btnRecShuffle.Soft = true;
            btnRecShuffle.Font = Theme.UISmall;
            btnRecShuffle.Click += delegate { RefreshRecommend(); };
            recPanel.Controls.Add(btnRecShuffle);
            lblExtraTitle = new Label();
            lblExtraTitle.Visible = false;
            Controls.Add(lblExtraTitle);



            lblTitle = new Label();
            lblTitle.Font = Theme.UIBold;
            lblTitle.ForeColor = Theme.Ink;
            lblTitle.BackColor = Color.Transparent;
            lblTitle.AutoEllipsis = true;
            Controls.Add(lblTitle);

            lblCur = new Label();
            lblCur.Font = Theme.UISmall;
            lblCur.ForeColor = Theme.Ink2;
            lblCur.BackColor = Color.Transparent;
            lblCur.AutoEllipsis = true;
            Controls.Add(lblCur);

            chkMulti = new SwitchBox();
            chkMulti.Caption = Lang.S("允许多选");
            chkMulti.Visible = false;
            chkMulti.Toggled += delegate
            {
                Slot s = Current;
                if (s == null || applyingMulti) return;
                bool on = chkMulti.Checked;
                if (on)
                {
                    if (!Core.Cfg.MultiSlots.Contains(s.Key)) Core.Cfg.MultiSlots.Add(s.Key);
                    if (lblFoot != null) lblFoot.Text = "「" + s.Name + "」已允许同时开多个：点一个开一个，再点一次关掉";
                }
                else
                {
                    Core.Cfg.MultiSlots.Remove(s.Key);
                    // 关掉多选 = 这个位置退回单选：先把这里所有 MOD 关掉（用户要的就是这个语义）
                    Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (ModGroup g in s.Items)
                    {
                        mf.UncheckGroup(g);
                        foreach (ModItem p in g.Parts)
                            if (p.FromGame || p.Installed) st[p.Name] = false;
                    }
                    if (st.Count > 0) mf.ApplySwitches(st);
                    if (lblFoot != null) lblFoot.Text = "「" + s.Name + "」已退回单选：这里所有 MOD 都关掉了";
                }
                Core.SaveConfig();
            };
            Controls.Add(chkMulti);
            lblFoot = new Label();
            lblFoot.Font = Theme.UISmall;
            lblFoot.ForeColor = Theme.Ink3;
            lblFoot.BackColor = Color.Transparent;
            lblFoot.AutoEllipsis = true;
            Controls.Add(lblFoot);

            lblExtraTitle = new Label();
            lblExtraTitle.Font = Theme.UISmall;
            lblExtraTitle.ForeColor = Theme.Ink3;
            lblExtraTitle.BackColor = Color.Transparent;
            lblExtraTitle.AutoEllipsis = true;
            lblExtraTitle.Visible = false;
            Controls.Add(lblExtraTitle);

            btnPick = new FlatButton();
            btnPick.Text = Lang.S("留原版");
            btnPick.SetBounds(0, 0, 66, Theme.H);
            btnPick.Click += delegate { Choose(null); };
            btnPick.Visible = false;          // 列表里已经有「留原版」那一行，按钮就多余了
            Controls.Add(btnPick);

            // 未归类页的手动入口：选中一行点它（或双击那行），告诉程序这个 MOD 算哪一类、替换什么。
            // 用的是和「设置 MOD 分类…」同一个窗口，保存后立刻重新归组，认出位置就排进树里。

            tipExtra = new ToolTip();
        tipRow.InitialDelay = 100;
        tipRow.ReshowDelay = 50;
        tipRow.AutoPopDelay = 5000;
            // 小标题在窄窗口放不下整句话，完整的解释挂在这里
            tipExtra.SetToolTip(lblExtraTitle,
                "可叠加的扩展 = 不换本体模型、能和模型同时生效的东西：开火音效、第一人称手部模型、HUD 图标。\r\n"
                + "这个位置目前还没有这类 MOD；有的话会列在下面，点一下开 / 关，可以多选。");

            Resize += delegate { LayoutSelf(); };
            LayoutSelf();
        }

        /// <summary>候选列表的共同外观：三列、圆点状态、不显示表头焦点。</summary>
        private ListView NewCandList(string firstCol)
        {
            ListView v = new ListView();
            v.View = View.Details;
            v.FullRowSelect = true;
            // 多选：未归类那 19 个要靠一条条拖就太慢了。选中多行时右键菜单给
            // 「一起指定位置 / 一起删除」，单选时的点击行为（换 MOD、翻开关）一个字没改。
            v.MultiSelect = true;
            v.BorderStyle = BorderStyle.None;
            v.BackColor = Theme.Surface;
            v.ForeColor = Theme.Ink;
            v.Font = Theme.UI;
            v.HeaderStyle = ColumnHeaderStyle.None;   // 原生表头在深色下永远是一块白，改由 ColHead 自绘
            v.ShowItemToolTips = false;      // 关掉系统自带的：再开就是「系统气泡 + 我们自己的气泡」两个一起弹
            // 系统的行提示在这台机器上不稳（延迟长、换行不刷新），改用 ItemMouseHover 自己弹：
            // 这个事件是控件专门为「鼠标停在一行上」发的，不依赖 MouseMove 是否被上层吃掉。
            v.ItemMouseHover += delegate(object s, ListViewItemMouseHoverEventArgs e2)
            {
                // 换行时先把上一条收掉：不收的话这一条和上一条会一起挂着，像是「提示卡住不消失」。
                if (e2.Item == null || string.IsNullOrEmpty(e2.Item.ToolTipText))
                { tipRow.Hide(v); tipShownAt = -1; return; }
                if (tipShownAt != e2.Item.Index) { tipRow.Hide(v); tipShownAt = e2.Item.Index; }
                tipRow.Show(e2.Item.ToolTipText, v, Cursor.Position.X - v.PointToScreen(Point.Empty).X + 18,
                            Cursor.Position.Y - v.PointToScreen(Point.Empty).Y + 20, 5000);
            };
            // 离开列表 / 滚轮一滚 / 停在空白处：立刻收掉，不等那 5 秒自己到点。
            v.MouseLeave += delegate { tipRow.Hide(v); tipShownAt = -1; };
            v.MouseWheel += delegate { tipRow.Hide(v); tipShownAt = -1; };
            v.MouseMove += delegate(object s3, MouseEventArgs e3)
            {
                if (v.GetItemAt(e3.X, e3.Y) == null) { tipRow.Hide(v); tipShownAt = -1; }
            };
            v.HideSelection = false;
            ImageList pad = new ImageList();
            // 这一行决定 ListView 的行高。字体是按 point 放大的，行高还写死 28 的话，
            // 125% / 150% 下字会顶到上下一行的边界上（前面那一行字的下半截被裁掉）。
            // 32 = 控件高，列表行和按钮行落在同一条节奏上。
            pad.ImageSize = new Size(1, Math.Max(16, Dpi.S(Theme.RowH)));
            pad.ColorDepth = ColorDepth.Depth32Bit;
            v.SmallImageList = pad;
            v.StateImageList = Dots();
            v.Columns.Add(firstCol, 380);
            v.Columns.Add(Lang.S("改了什么"), 100);
            v.Columns.Add(Lang.S("大小"), 74).TextAlign = HorizontalAlignment.Right;
            v.Columns.Add(Lang.S("已装"), 84);                        // 游戏 addons 里有没有这一份
            v.Columns.Add(Lang.S("开关 ⇄"), 62);                        // addonlist 里是 1 还是 0（点一下能翻，⇄ 是这个意思）
            return v;
        }

        /// <summary>两列状态：3=已装（在不在游戏里）、4=开关（1/0，可点）。同一条判定给分类列表也用了。</summary>
        private void AddStateCells(ListViewItem li, ModGroup g)
        {
            // 每一列保留自己的状态色和字号；否则整行样式会盖住「已装 / 开关」列。
            li.UseItemStyleForSubItems = false;
            li.SubItems.Add("");
            li.SubItems.Add("");
            ApplyStateCells(li, g);
        }

        /// <summary>只改两列的文字与颜色，不动整行结构 —— 点开关时用它，列表不重排、滚动位置不跳。</summary>
        private void ApplyStateCells(ListViewItem li, ModGroup g)
        {
            if (li.SubItems.Count < 5) return;
            int total, inst, on, game;
            MainForm.CountState(g, out total, out inst, out on, out game);
            int shownOn = 0, ins = 0;
            foreach (ModItem p in g.Parts)
            {
                if (!(p.FromGame || p.Installed)) continue;
                ins++;
                if (p.AddonOn) shownOn++;
            }

            // 本地模式：游戏 addons 自己就是库，「已装 / 仅游戏内」这一维度恒为真、一点信息量都没有。
            // 那一列已经在 Cols 里收成 0 宽，剩下的唯一状态就是 addonlist 的开关 → 开启 / 关闭。
            bool local = Core.IsLocal;

            // 已装列（本地模式下宽度为 0，看不见，只留空串）
            string instText; Color instInk;
            if (local) { instText = ""; instInk = Theme.Ink3; }
            else if (inst == 0) { instText = Lang.S("未装"); instInk = Theme.Ink3; }
            else if (inst < total) { instText = "部分 " + inst + "/" + total; instInk = Theme.Info; }
            else if (game == total) { instText = "仅游戏内"; instInk = Theme.Ink2; }
            else { instText = Lang.S("已装"); instInk = Theme.Ok; }

            // 开关列：只有装进去的才谈得上开关。
            // 本地模式下这列就是唯一的状态列，所以用「开启 / 关闭」说全称，不再用「开 / 关」。
            string swText; Color swInk; Color rowBg;
            if (ins == 0) { swText = "—"; swInk = Theme.Ink3; rowBg = Theme.Surface; }
            else if (shownOn == ins) { swText = Lang.S(local ? "开启" : "开"); swInk = Theme.Ok; rowBg = Theme.RowOn; }
            else if (shownOn == 0) { swText = Lang.S(local ? "关闭" : "关"); swInk = Theme.Ink3; rowBg = Theme.RowOff; }
            else { swText = (local ? "开启 " : "") + shownOn + "/" + ins; swInk = Theme.Warn; rowBg = Theme.RowOff; }

            li.SubItems[3].Text = instText;
            li.SubItems[3].ForeColor = instInk;
            li.SubItems[3].Font = Theme.UISmall;
            li.SubItems[4].Text = swText;
            li.SubItems[4].ForeColor = swInk;
            li.SubItems[4].Font = Theme.UIBold;
            // 这一行关掉了「子项继承整行样式」（每列要自己的状态色），所以底色得逐个格子刷：
            // 只设 li.BackColor 的话，只有第一列是这行的底色，右边仍是列表底色 —— 行看起来从中间断开。
            if (li.BackColor != rowBg)
            {
                li.BackColor = rowBg;
                for (int i = 0; i < li.SubItems.Count; i++) li.SubItems[i].BackColor = rowBg;
            }
        }

        private static void SelectRow(ListView v, MouseEventArgs e)
        {
            ListViewItem hit = v.GetItemAt(e.X, e.Y);
            if (hit == null || hit.Selected) return;
            v.SelectedItems.Clear();
            hit.Selected = true;
            hit.Focused = true;
        }

        private static ImageList Dots()
        {
            int s = Math.Max(8, Dpi.S(14));
            ImageList il = new ImageList();
            il.ImageSize = new Size(s, s);
            il.ColorDepth = ColorDepth.Depth32Bit;
            il.TransparentColor = Color.Transparent;
            il.Images.Add(Dot(false));
            il.Images.Add(Dot(true));
            return il;
        }

        private static Bitmap Dot(bool on)
        {
            // 圆点也要跟着缩放：图标不放大而文字放大，列表看着就是「字大点小」，很别扭
            int s = Math.Max(8, Dpi.S(14));
            float k = s / 14f;
            Bitmap b = new Bitmap(s, s);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Pen p = new Pen(on ? Theme.Mark : (Theme.IsDark ? Color.FromArgb(96, 103, 112) : Color.FromArgb(184, 192, 200)), Math.Max(1f, 1.5f * k)))
                    g.DrawEllipse(p, 2.5f * k, 2.5f * k, 8f * k, 8f * k);
                if (on)
                    using (SolidBrush br = new SolidBrush(Theme.Mark))
                        g.FillEllipse(br, 5f * k, 5f * k, 4f * k, 4f * k);
            }
            return b;
        }

        // ---------------------------------------------------------- 构建

        /// <summary>重新分组并铺树。库变了才需要调用。</summary>
        /// <summary>只在数量变化时记一条，免得每改一次分类就刷一遍日志。</summary>
        void LogUnmatched()
        {
            if (unmatched == _lastUnmatchedLogged) return;
            _lastUnmatchedLogged = unmatched;
            if (unmatched <= 0) return;
            List<string> names = new List<string>();
            for (int i = 0; i < unmatchedGroups.Count && i < 5; i++) names.Add(unmatchedGroups[i].Name);
            Core.Log("INFO", "有 " + unmatched + " 个 MOD 没认出位置，收在方案树最底部「未归类」："
                + string.Join("、", names.ToArray()) + (unmatched > names.Count ? " 等" : ""));
        }
        int _lastUnmatchedLogged = -1;

        public void Rebuild()
        {
            slots = SlotIndex.Build();
            SortSlots();
            BuildPiles();                       // 地图 / 喷漆 / 我的分支：能叠着开的那些，排在最后
            // 未归类 = 库里剩下的、没进任何位置的 MOD，一个不漏全收进列表。
            HashSet<ModGroup> placed = new HashSet<ModGroup>();
            foreach (Slot s0 in slots)
            {
                foreach (ModGroup g0 in s0.Items) placed.Add(g0);
            }
            unmatchedGroups.Clear();
            foreach (ModGroup g in Catalog.Groups)
            {
                if (g.Cat == "地图" || g.Cat == "喷漆") continue;
                if (placed.Contains(g)) continue;
                unmatchedGroups.Add(g);
            }
            unmatchedGroups.Sort(delegate(ModGroup a, ModGroup b)
            { return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture); });
            unmatched = unmatchedGroups.Count;
            LogUnmatched();
            LoadFold();                          // 折叠状态：第一次打开 = 小分类全收起，只留目录
            if (selKey.Length > 0 && selKey != "#unmatched")
            {
                bool ok = false;
                foreach (Slot s in slots) if (s.Key == selKey) { ok = true; break; }
                if (!ok) selKey = "";                       // 分类一改，原来那个位置可能没了
            }
            if (selKey.Length == 0 && slots.Count > 0)
            {
                // 首选「有 MOD 的位置」：树头几行常常是空位，一进来就停在「还没有 MOD」上像出了事
                Slot first = null;
                foreach (Slot s in slots) if (!s.Empty) { first = s; break; }
                selKey = (first != null ? first : slots[0]).Key;
            }
            RevealFold(selKey);                  // 收起状态下，选中的那行也得看得见
            BuildRows();
            FillDetail();
        }

        /// <summary>小分类排序键：手工拖过就用手工序号，没拖过用识别层的固有顺序（步枪 → 霰弹枪 …）。</summary>
        internal static int SubSortKey(Slot s)
        {
            int m = CatPrefs.SubRankOf(s.Cat, s.Sub);
            return m >= 0 ? m : SlotIndex.SubRank(s.Sub);
        }

        /// <summary>位置排序键：-1 = 这个小分类没被手工排过，交给后面的自动规则。</summary>
        internal static int PosSortKey(Slot s)
        {
            if (!CatPrefs.HasPosOrder(s.Cat, s.Sub)) return -1;
            int m = CatPrefs.PosRankOf(s.Cat, s.Sub, s.Name);
            return m >= 0 ? m : 9000 + SlotIndex.NameRank(s.Sub, s.Name);
        }

        /// <summary>某个大类当前的小分类顺序（就是树里看到的顺序，落底稿时用）。</summary>
        internal List<string> CurrentSubs(string cat)
        {
            List<string> r = new List<string>();
            foreach (Slot s in slots)
                if (s.Cat == cat && !r.Contains(s.Sub)) r.Add(s.Sub);
            return r;
        }

        /// <summary>某个小分类当前的位置顺序（树里看到的顺序）。</summary>
        internal List<string> CurrentPositions(string cat, string sub)
        {
            List<string> r = new List<string>();
            foreach (Slot s in slots)
                if (s.Cat == cat && s.Sub == sub) r.Add(s.Name);
            return r;
        }

        private void SortSlots()
        {
            // 排序规则搬到 SlotIndex 里：总览也要用同一套顺序，
            // 两边各写一份迟早会飘（第一次做白模就因为顺序不同，19 个位置每个都触发分组标题）
            SlotIndex.SortForTree(slots, HasChecked);
        }

        /// <summary>
        /// 排在最后的三类「一堆」：
        ///   地图 / 喷漆 —— 能叠好几张，没有「一个位置只能一个」的说法，所以不做槽位互斥；
        ///   我的分支 —— 程序认不出改的是什么的，用户自己拉个分组装进去，同样允许叠着开。
        /// 它们都是 Slot.Pile=true，点一行 = 开 / 关，不会把同堆里别的顶掉。
        /// </summary>
        private void BuildPiles()
        {
            AddPile("地图", "三方图", "地图", delegate(ModGroup g) { return g.Cat == "地图"; });
            AddPile("喷漆", "喷漆", "喷漆", delegate(ModGroup g) { return g.Cat == "喷漆"; });

            // 我的分支：就算一个分支都没有，也留一行「＋ 新建分支…」，
            // 否则用户永远不知道有这个功能（没有别的入口）。
            MyBranch.Ensure();
            int rank = 0;
            foreach (string nm in MyBranch.Names())
            {
                Slot s = new Slot();
                s.Key = "#mine:" + nm;
                s.Name = nm;
                s.Cat = "我的分支";
                s.Sub = "";
                s.Pile = true;
                s.Rank = rank++;                 // Names() 是父子顺序，Rank 保住这个顺序
                foreach (ModGroup g in Catalog.Groups)
                    if (g.Main != null && MyBranch.BranchOf(g.Main.Name) == nm) s.Items.Add(g);
                slots.Add(s);
            }
            Slot add = new Slot();
            add.Key = "#mine+new";
            add.Name = "＋ 新建分支…";
            add.Cat = "我的分支";
            add.Sub = "";
            add.Pile = true;
            add.Rank = 100000;                   // 永远排在分支后面
            slots.Add(add);
        }

        private void AddPile(string cat, string name, string key, Predicate<ModGroup> pick)
        {
            Slot s = new Slot();
            s.Key = "#pile:" + key;
            s.Name = name;
            s.Cat = cat;
            s.Sub = "";
            s.Pile = true;
            foreach (ModGroup g in Catalog.Groups) if (pick(g)) s.Items.Add(g);
            if (s.Items.Count > 0) slots.Add(s);
        }

        private void BuildRows()
        {
            List<TreeRow> rows = new List<TreeRow>();
            List<string> order = SlotIndex.CatOrder(slots);
            foreach (string cat in order)
            {
                List<Slot> inCat = new List<Slot>();
                foreach (Slot s in slots) if (s.Cat == cat) inCat.Add(s);
                if (inCat.Count == 0) continue;

                TreeRow h = new TreeRow();
                h.IsCat = true; h.Cat = cat;
                // 一大类底下只有一个「堆」（目前就是地图：所有三方图都能同时开）时，
                // 把堆挂到类标题行上，标题行本身可点 —— 少一层「地图 → 三方图」的空壳。
                bool solePile = inCat.Count == 1 && inCat[0].Pile;
                if (solePile) h.Slot = inCat[0];
                rows.Add(h);
                if (folded.Contains(cat)) continue;
                if (solePile) continue;

                // 小分类（枪械 → 步枪 → AK-47）。slots 已按小分类排好，这里顺着切段。
                // 一大类底下只有一个分组时不画那行标题 ——「特感 → 特感 → Tank」这种重复层没意义。
                int subCount = 0;
                string prevSub = null;
                foreach (Slot s in inCat) if (s.Sub != prevSub) { prevSub = s.Sub; subCount++; }
                bool showSub = subCount > 1;
                string lastSub = null;
                foreach (Slot s in inCat)
                {
                    if (showSub && s.Sub != lastSub)
                    {
                        lastSub = s.Sub;
                        TreeRow sh = new TreeRow();
                        sh.IsSub = true; sh.Cat = cat; sh.Sub = s.Sub;
                        rows.Add(sh);
                    }
                    if (showSub && folded.Contains(SubKey(cat, s.Sub))) continue;        // 这一段整段收起
                    TreeRow r = new TreeRow();
                    r.Slot = s; r.Cat = cat; r.Sub = s.Sub;
                    rows.Add(r);
                }
            }
            // 未归类的包不再在树里占一行（用户 2026-11-16 要求）：
            // 改成右上角一个按钮「未识别 N 个」打开专门的手动分类页。
            body.SetRows(rows);
        }

        // ---------------------------------------------------------- 状态

        internal bool HasChecked(Slot s)
        {
            foreach (ModGroup g in s.Items) if (mf.CountChecked(g) > 0) return true;
            return false;
        }

        /// <summary>这个位置当前勾了几件（正常是 0 或 1；>1 说明用户手工叠了）。</summary>
        internal int CheckedGroups(Slot s)
        {
            int n = 0;
            foreach (ModGroup g in s.Items) if (mf.CountChecked(g) > 0) n++;
            return n;
        }


        /// <summary>
        /// 多选模式下点一行：开着的关掉、关着的打开，同位置别的**不动**。
        /// 只写这一个 MOD 的开关，不复制文件。
        /// </summary>
        private void ToggleMulti(Slot s, ModGroup g)
        {
            if (s == null || g == null) return;
            bool on = mf.CountChecked(g) > 0;
            Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem p in g.Parts)
                if (p.FromGame || p.Installed) st[p.Name] = !on;
            if (on) mf.UncheckGroup(g); else mf.CheckGroupDefault(g);
            if (st.Count > 0) mf.ApplySwitches(st);
            mf.AfterTreePick();
            if (lblFoot != null)
                lblFoot.Text = "「" + s.Name + "」：" + g.Name + (on ? " 已关" : " 已开") + "（多选模式，同位置别的没动）";
            FillDetail();
        }

        /// <summary>这个位置是不是「允许多选」：状态存配置里，按位置 key 记。</summary>
        private static bool IsMulti(Slot s)
        {
            // 平台是底座：KSEP / VRD / ADS 这类本来就该一起开着，默认就多选，不用用户先去找开关。
            if (s != null && s.Cat == "平台") return true;
            return s != null && Core.Cfg.MultiSlots.Contains(s.Key);
        }

        internal ModGroup ChosenOf(Slot s)
        {
            foreach (ModGroup g in s.Items) if (mf.CountChecked(g) > 0) return g;
            return null;
        }

        // ---------------- 「一堆」：地图 / 喷漆 / 我的分支 ----------------

        /// <summary>点「一堆」里的行：开着的关掉，关着的打开 —— 同堆别的照旧，不顶掉。</summary>
        internal void TogglePile(ModGroup g)
        {
            if (g == null) return;
            mf.ApplyGroupPick(g, mf.CountChecked(g) == 0);   // 勾选和 addonlist 一起动
            mf.AfterTreePick();
            FillDetail();
        }

        /// <summary>分支位置？（键以 #mine: 开头）</summary>
        internal static bool IsBranchSlot(Slot s)
        {
            return s != null && s.Key != null && s.Key.StartsWith("#mine:", StringComparison.Ordinal);
        }

        /// <summary>这个 MOD 在树里哪一格：搜索结果点一下、托盘定位都走它。</summary>
        internal bool RevealGroup(ModGroup g)
        {
            if (g == null) return false;
            foreach (Slot s in slots)
            {
                if (s.Pile) continue;
                if (s.Items.Contains(g)) return GoSlot(s.Key);
            }
            foreach (Slot s in slots)
                if (s.Pile && s.Items.Contains(g)) return GoSlot(s.Key);
            selKey = "#unmatched";
            body.Invalidate();
            FillDetail();
            return false;
        }

        private bool GoSlot(string key)
        {
            RevealFold(key);                     // 目标被折叠着就先展开，不然跳过去是空白
            selKey = key;
            body.Invalidate();
            FillDetail();
            body.ScrollToSlot(key);
            return true;
        }

        /// <summary>新建分支：树上「＋ 新建分支…」那行、右键菜单都走这里。</summary>
        internal void NewBranch()
        {
            NewBranch(null);
        }

        /// <summary>新建分支。parent 不为空 = 建在它下面（子分支）。</summary>
        internal void NewBranch(Slot parent)
        {
            NewBranchUnder(parent == null ? "" : parent.Name);
        }

        /// <summary>按父分支名新建（右键「新建同级分支」、拖到某行末位时也走这里）。</summary>
        internal void NewBranchUnder(string into)
        {
            into = into ?? "";
            string tip = into.Length > 0
                ? "子分支名（会缩进挂在「" + into + "」下面）。建好后右键任意 MOD 行 →「放进我的分支」。"
                : "分支名（例：音效 / 整活 / 收藏）。建好后右键任意 MOD 行，把它放进来。";
            string nm = PromptForm.Ask(tip, into.Length > 0 ? "在「" + into + "」下新建分支" : "新建分支");
            if (nm == null) return;
            string err = MyBranch.Create(nm, into);
            if (err != null) { MessageBox.Show(err, "建不了这个分支"); return; }
            string final = MyBranch.CleanName(nm);
            Rebuild();
            GoSlot("#mine:" + final);
            lblFoot.Text = into.Length > 0
                ? "子分支「" + final + "」建好了（在「" + into + "」下面）· 右键任意 MOD 行 →「放进我的分支」"
                : "分支「" + final + "」建好了 · 右键任意 MOD 行 →「放进我的分支」";
        }

        /// <summary>菜单文字里放 MOD 名用：太长就截断，免得菜单横到屏幕外。</summary>
        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>建一个和它同级的兄弟分支（右键分支 →「新建同级分支…」）。</summary>
        internal void NewBranchSibling(Slot s)
        {
            if (!IsBranchSlot(s)) return;
            NewBranchUnder(MyBranch.ParentOf(s.Name));
        }

        /// <summary>拖动松手后真正挪位置：只动分组记录，vpk 文件和勾选一个都不碰。</summary>
        internal bool MoveBranch(string name, string newParent, string before)
        {
            string err = MyBranch.Move(name, newParent, before);
            if (err != null)
            {
                lblFoot.Text = "没挪成：" + err;
                return false;
            }
            Rebuild();
            GoSlot("#mine:" + name);
            lblFoot.Text = "「" + name + "」已移到"
                + (string.IsNullOrEmpty(newParent) ? "顶层" : "「" + newParent + "」下面")
                + " · 拖住分支行还能再换位置";
            return true;
        }

        /// <summary>拖动大类排序：移到 target 大类前面；after=true 移到它后面。顺序写进分类表，重启也在。</summary>
        internal bool MoveCatTo(string name, string target, bool after)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(target)) return false;
            // 已经在那个位置上 = 明确告诉用户「没挪」，别让他以为拖动坏了
            List<string> cur = CatPrefs.Cats();
            int ci = cur.IndexOf(name), ti = cur.IndexOf(target);
            if (ci >= 0 && ti >= 0 && ((after && ci == ti + 1) || (!after && ci == ti - 1)))
            {
                lblFoot.Text = "「" + name + "」本来就在「" + target + "」" + (after ? "后面" : "前面")
                    + " —— 位置没变。拖到隔一个以上的大类（比如 枪械 / 杂项）才看得出挪动";
                Core.Log("INFO", "DRAGAPPLY 「" + name + "」本来就在「" + target + "」" + (after ? "后面" : "前面") + "，位置没变");
                return true;
            }
            // 「我的分支」这类不在默认大类表里的，先登记再挪 —— 否则 CatPrefs 里查不到它，移动直接失败
            CatPrefs.EnsureCat(name);
            CatPrefs.EnsureCat(target);
            if (!CatPrefs.MoveCatTo(name, target, after)) return false;
            Rebuild();
            // 把刚拖的那个大类滚回视野内
            body.ScrollToRow(FindCatRow(name));
            lblFoot.Text = "「" + name + "」已移到「" + target + "」" + (after ? "后面" : "前面")
                + " · 拖住大类行还能再换";
            return true;
        }

        /// <summary>拖动小分类排序：第一次拖时把当前顺序落成底稿，再挪 —— 重启后还在。</summary>
        internal bool MoveSubTo(string cat, string sub, string target, bool after)
        {
            if (string.IsNullOrEmpty(cat) || string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(target)) return false;
            List<string> cur = CurrentSubs(cat);
            if (!CatPrefs.HasSubOrder(cat)) CatPrefs.SetSubOrder(cat, cur);
            List<string> now = CatPrefs.SubOrderOf(cat) ?? cur;
            int ci = now.IndexOf(sub), ti = now.IndexOf(target);
            if (ci >= 0 && ti >= 0 && ((after && ci == ti + 1) || (!after && ci == ti - 1)))
            {
                lblFoot.Text = "「" + sub + "」本来就在「" + target + "」" + (after ? "后面" : "前面")
                    + " —— 位置没变。拖到隔一个的小分类上才看得出挪动";
                Core.Log("INFO", "DRAGAPPLY 小分类「" + sub + "」本来就在「" + target + "」" + (after ? "后面" : "前面") + "，位置没变");
                return true;
            }
            if (!CatPrefs.MoveSubTo(cat, sub, target, after)) return false;
            Rebuild();
            body.ScrollToRow(body.RowIndexOfSubOrFirst(cat, sub));
            lblFoot.Text = "「" + sub + "」已挪到「" + target + "」" + (after ? "后面" : "前面")
                + " · 拖住小分类行还能再换";
            return true;
        }

        /// <summary>
        /// 拖动位置：换顺序，或拖进另一个小分类（那一步会把归属一起记下来）。
        /// 两个小分类的顺序都先落成底稿 —— 否则只有被拖的那个有手工顺序，其余还会按自动顺序插进来。
        /// </summary>
        internal bool MovePosTo(string cat, string fromSub, string name, string toSub, string target, bool after)
        {
            if (string.IsNullOrEmpty(cat) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(toSub)) return false;
            bool cross = !string.Equals(fromSub, toSub, StringComparison.Ordinal);
            if (!CatPrefs.HasPosOrder(cat, fromSub)) CatPrefs.SetPosOrder(cat, fromSub, CurrentPositions(cat, fromSub));
            if (cross && !CatPrefs.HasPosOrder(cat, toSub)) CatPrefs.SetPosOrder(cat, toSub, CurrentPositions(cat, toSub));

            if (!cross)
            {
                List<string> now = CatPrefs.PosOrderOf(cat, toSub) ?? new List<string>();
                int ci = now.IndexOf(name), ti = string.IsNullOrEmpty(target) ? -1 : now.IndexOf(target);
                if (ci >= 0 && ti >= 0 && ((after && ci == ti + 1) || (!after && ci == ti - 1)))
                {
                    lblFoot.Text = "「" + name + "」本来就在「" + target + "」" + (after ? "后面" : "前面")
                        + " —— 位置没变。拖到隔一个的位置上才看得出挪动";
                    Core.Log("INFO", "DRAGAPPLY 位置「" + name + "」本来就在「" + target + "」" + (after ? "后面" : "前面") + "，位置没变");
                    return true;
                }
            }
            if (!CatPrefs.MovePosTo(cat, fromSub, name, toSub, target, after)) return false;
            if (cross) CatPrefs.SetSlotSub(cat, name, toSub);      // 归属跟着走，重扫之后也还在这一段里
            Rebuild();
            int at = body.RowIndexOfPos(cat, toSub, name);
            if (at >= 0) body.ScrollToRow(at);
            lblFoot.Text = cross
                ? "「" + name + "」已放进「" + toSub + "」" + (string.IsNullOrEmpty(target) ? (after ? "末尾" : "开头") : "「" + target + "」" + (after ? "后面" : "前面"))
                : "「" + name + "」已挪到「" + target + "」" + (after ? "后面" : "前面") + " · 拖住位置行还能再换";
            return true;
        }

        /// <summary>大类标题行在树里是第几行（没有返回 -1）。</summary>
        private int FindCatRow(string cat)        {
            for (int i = 0; i < body.RowCount; i++)
                if (body.RowIsCat(i) && string.Equals(body.RowCat(i), cat, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>新建分支，并直接把某个 MOD 放进去（右键 MOD 行 →「放进新分支…」）。</summary>
        internal void PutIntoNewBranch(ModGroup g)
        {
            if (g == null || g.Main == null) return;
            string nm = PromptForm.Ask("新分支名（例：音效 / 整活 / 收藏）。建好后「" + g.Name + "」就在里面。", "放进新分支");
            if (nm == null) return;
            string err = MyBranch.Create(nm);
            if (err != null) { MessageBox.Show(err, "建不了这个分支"); return; }
            string pick = MyBranch.CleanName(nm);
            MyBranch.Put(pick, g.Main.Name);
            Rebuild();
            GoSlot("#mine:" + pick);
            lblFoot.Text = "分支「" + pick + "」建好了，「" + g.Name + "」已经在里面 · 点一行 = 开 / 关";
        }

        internal void RenameBranch(Slot s)
        {
            if (!IsBranchSlot(s)) return;
            string nm = PromptForm.Ask("新名字（原来是「" + s.Name + "」）", "重命名分支", s.Name);
            if (nm == null) return;
            string err = MyBranch.Rename(s.Name, nm);
            if (err != null) { MessageBox.Show(err, "改不了"); return; }
            Rebuild();
            GoSlot("#mine:" + MyBranch.CleanName(nm));
        }

        internal void DeleteBranch(Slot s)
        {
            if (!IsBranchSlot(s)) return;
            if (MessageBox.Show("解散分支「" + s.Name + "」？\r\n\r\n"
                + "· 只删这个分组，vpk 文件一个都不动\r\n"
                + "· 里面的 MOD 回到各自原来的分类（认不出的回「未归类」）\r\n"
                + "· 已经勾着的不会被取消",
                "解散分支", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            MyBranch.Delete(s.Name);
            Rebuild();
            GoSlot("#mine+new");
        }

        /// <summary>把这个 MOD 放进某个分支（还没分支就先起名）。</summary>
        internal void PutIntoBranch(ModGroup g)
        {
            if (g == null || g.Main == null) return;
            string pick;
            List<string> names = MyBranch.Names();
            if (names.Count == 0)
            {
                pick = PromptForm.Ask("还没有分支。先起个名字（例：音效 / 整活 / 收藏）：", "放进我的分支");
                if (pick == null) return;
                string err = MyBranch.Create(pick);
                if (err != null) { MessageBox.Show(err, "建不了这个分支"); return; }
                pick = MyBranch.CleanName(pick);
            }
            else
            {
                BranchPickForm f = new BranchPickForm(names);
                if (f.ShowDialog() != DialogResult.OK) return;
                pick = f.Picked;
                if (pick == null) return;
                if (pick != null && pick.StartsWith("＋", StringComparison.Ordinal))   // 选了「＋ 新建分支…」
                {
                    string nm = PromptForm.Ask("新分支名（例：音效 / 整活 / 收藏）：", "新建分支");
                    if (nm == null) return;
                    string err2 = MyBranch.Create(nm);
                    if (err2 != null) { MessageBox.Show(err2, "建不了这个分支"); return; }
                    pick = MyBranch.CleanName(nm);
                }
            }
            MyBranch.Put(pick, g.Main.Name);
            Rebuild();
            GoSlot("#mine:" + pick);
            lblFoot.Text = "「" + g.Name + "」已放进分支「" + pick + "」· 点一行 = 开 / 关";
        }

        /// <summary>
        /// 未归类 MOD 拖到树上松手。落点三种：位置行 = 归到这个位置；
        /// 大类 / 小分类标题 = 只归大类；分支行 = 放进我的分支。归完跳过去，让人立刻看到结果。
        /// </summary>
        /// <summary>包自带的全名（addoninfo 标题）；没有就用文件名。</summary>
        internal static string FullNameOf(ModGroup g)
        {
            if (g == null) return "";
            ModItem main = g.Main;
            string title = main == null ? "" : (main.AddonTitle ?? "").Trim();
            return title.Length > 0 ? title : g.Name;
        }

        internal void DropModOn(ModGroup g, TreeRow r)
        {
            if (g == null || r == null) return;

            if (r.Slot != null)
            {
                Slot s = r.Slot;
                if (IsBranchSlot(s))
                {
                    if (g.Main == null) { lblFoot.Text = Lang.S("这个 MOD 没有主体 vpk，放不进分支"); return; }
                    MyBranch.Put(s.Name, g.Main.Name);
                    Rebuild();
                    GoSlot(s.Key);
                    lblFoot.Text = "「" + g.Name + "」已放进分支「" + s.Name + "」";
                    Core.Log("OK", "拖拽归类: " + g.Name + " → 分支「" + s.Name + "」");
                    return;
                }
                if (s.Pile)
                {
                    // 地图 / 喷漆这类「一堆」：判定按大类收，归到这一堆就够了，不占具体位置
                    mf.ApplyCatAssign(g, s.Cat, "");
                    Rebuild();
                    if (Unmatched > 0) SelectUnmatched(); else GoSlot(s.Key);
                    lblFoot.Text = "「" + g.Name + "」已归到「" + s.Cat + "」";
                    Core.Log("OK", "拖拽归类: " + g.Name + " → " + s.Cat + "（一堆）");
                    return;
                }
                mf.ApplyCatAssign(g, s.Cat, s.Name);
                Rebuild();
                // 还有没归类的就留在这一页继续分，别跳到目标位置 —— 以前一跳，列表看起来就"空了"
                if (Unmatched > 0) SelectUnmatched(); else GoSlot(s.Key);
                lblFoot.Text = "「" + g.Name + "」已归到「" + s.Cat + " · " + s.Name + "」" +
                    (Unmatched > 0 ? "；还剩 " + Unmatched + " 个没归类" : "");
                return;
            }

            // 大类 / 小分类标题：只改分类，位置仍按识别结果。留在未归类页（那行已经消失），
            // 滚到目标大类让用户看见它去哪了 —— GoSlot 没有大类键，别造一个 ScrollToSlot 不认的。
            string cat = r.Cat;
            if (string.IsNullOrEmpty(cat)) return;
            mf.ApplyCatAssign(g, cat, "");
            Rebuild();
            for (int i = 0; i < body.RowCount; i++)
                if (body.RowIsCat(i) && body.RowCat(i) == cat) { body.ScrollToRow(i); break; }
            lblFoot.Text = "「" + g.Name + "」已归到「" + cat + "」（位置仍按识别结果）";
        }

        /// <summary>多选批量拖：逐个套用同一个落点，最后只重建一次、只报一句总数。</summary>
        internal void DropModsOn(List<ModGroup> gs, TreeRow r)
        {
            if (gs == null || gs.Count == 0 || r == null) return;
            if (gs.Count == 1) { DropModOn(gs[0], r); return; }

            if (r.Slot != null)
            {
                Slot s = r.Slot;
                if (IsBranchSlot(s))
                {
                    int put = 0;
                    foreach (ModGroup g in gs) if (g.Main != null) { MyBranch.Put(s.Name, g.Main.Name); put++; }
                    Rebuild(); GoSlot(s.Key);
                    lblFoot.Text = Lang.F("{0} 个已放进分支「{1}」", put, s.Name);
                    Core.Log("OK", "批量拖拽归类: " + put + " 个 → 分支「" + s.Name + "」");
                    return;
                }
                // 「一堆」（地图 / 喷漆）：大类对得上就会收进这一堆，不占具体位置
                string slotName = s.Pile ? "" : s.Name;
                foreach (ModGroup g in gs) mf.ApplyCatAssign(g, s.Cat, slotName);
                Rebuild();
                if (Unmatched > 0) SelectUnmatched(); else GoSlot(s.Key);
                lblFoot.Text = s.Pile
                    ? Lang.F("{0} 个已归到「{1}」", gs.Count, s.Cat)
                    : Lang.F("{0} 个已归到「{1} · {2}」", gs.Count, s.Cat, s.Name);
                Core.Log("OK", "批量拖拽归类: " + gs.Count + " 个 → " + s.Cat + (slotName.Length > 0 ? " / " + slotName : ""));
                return;
            }

            string cat = r.Cat;
            if (string.IsNullOrEmpty(cat)) return;
            foreach (ModGroup g in gs) mf.ApplyCatAssign(g, cat, "");
            Rebuild();
            for (int i = 0; i < body.RowCount; i++)
                if (body.RowIsCat(i) && body.RowCat(i) == cat) { body.ScrollToRow(i); break; }
            lblFoot.Text = Lang.F("{0} 个只改了归类为「{1}」（位置还是空的）", gs.Count, cat);
            Core.Log("OK", "批量拖拽归类: " + gs.Count + " 个 → " + cat + "（只改大类）");
        }

        /// <summary>右键菜单用：这个 MOD 是不是在当前打开的分支里。</summary>
        internal bool InOpenBranch(ModGroup g)
        {
            Slot s = Current;
            return g != null && g.Main != null && IsBranchSlot(s)
                && string.Equals(MyBranch.BranchOf(g.Main.Name), s.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>从当前分支里把这个 MOD 移出（不删文件、不改勾选）。</summary>
        internal void TakeOutOfBranch(ModGroup g)
        {
            Slot s = Current;
            if (g == null || g.Main == null || !IsBranchSlot(s)) return;
            MyBranch.Take(g.Main.Name);
            Rebuild();
            GoSlot(s.Key);
            lblFoot.Text = "已从分支「" + s.Name + "」移出（文件和勾选都没动）";
        }

        /// <summary>树的右键菜单。点在空白处也能出（r 为 null）—— 建分支从此不用先找到「我的分支」那几行。</summary>
        internal void TreeMenuFor(TreeRow r, Point at)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Theme.UI;
            Slot s = r == null ? null : r.Slot;
            if (IsBranchSlot(s))
            {
                Slot ss = s;
                m.Items.Add("在这个分支下新建子分支…", null, delegate { NewBranch(ss); });
                m.Items.Add("新建同级分支…", null, delegate { NewBranchSibling(ss); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("重命名分支…", null, delegate { RenameBranch(ss); });
                if (MyBranch.HasParent(ss.Name))
                    m.Items.Add("移到顶层", null, delegate
                    {
                        MoveBranch(ss.Name, "", "");
                    });
                m.Items.Add("解散分支", null, delegate { DeleteBranch(ss); });
                m.Items.Add(new ToolStripSeparator());
            }
            else
            {
                m.Items.Add("新建分支…", null, delegate { NewBranch(); });
                // 已有分支时再给一层：不用先滚到那个分支行，右键哪儿都能挑一个分支往它下面建
                if (MyBranch.Names().Count > 0)
                {
                    ToolStripMenuItem under = new ToolStripMenuItem("在已有分支下新建子分支…");
                    foreach (string bn in MyBranch.Names())
                    {
                        string target = bn;
                        under.DropDownItems.Add(MyBranch.HasParent(target)
                            ? "在「" + Shorten(target, 16) + "」下（现挂在「" + Shorten(MyBranch.ParentOf(target), 12) + "」里）"
                            : "在「" + Shorten(target, 20) + "」下",
                            null, delegate { NewBranchUnder(target); });
                    }
                    m.Items.Add(under);
                }
                // 位置行上再给一条捷径：不用先点开位置、再去列表里翻那个 MOD
                if (s != null && s.Items.Count > 0)
                {
                    ModGroup pick = ChosenOf(s);
                    if (pick == null && s.Items.Count == 1) pick = s.Items[0];
                    if (pick != null)
                    {
                        ModGroup pg = pick;
                        m.Items.Add("新建分支，并把「" + Shorten(pg.Name, 18) + "」放进去…",
                            null, delegate { PutIntoNewBranch(pg); });
                    }
                }
                m.Items.Add(new ToolStripSeparator());
            }
            // 树有 70 多个位置，一屏看不完：先收成一页目录，找哪层再点开
            m.Items.Add("全部收起", null, delegate { FoldEverything(); });
            m.Items.Add("全部展开", null, delegate { UnfoldEverything(); });
            m.Items.Add(new ToolStripSeparator());

            // 库本身的活儿（导入、扫描范围、识别、去重）原来只在「分类页」的右键菜单里，
            // 分类页撤掉后搬到这儿，不然这些功能就没入口了。
            ToolStripMenuItem lib = new ToolStripMenuItem("MOD 库…");
            lib.DropDownItems.Add("从文件夹导入…", null, delegate { mf.BulkImport(); });
            lib.DropDownItems.Add("库文件夹管理…", null, delegate { mf.ManageModDirs(); });
            lib.DropDownItems.Add("识别压缩包内容", null, delegate { mf.ProbeArchivesUi(); });
            lib.DropDownItems.Add("清理同名重复", null, delegate { mf.CleanDupMods(); });
            lib.DropDownItems.Add(new ToolStripSeparator());
            lib.DropDownItems.Add("重新扫描 MOD 库", null, delegate { mf.RescanMods(); });
            m.Items.Add(lib);

            m.Show(at);
        }

        /// <summary>给树里的小动作（拖动取消之类）留一句脚注，不用专门弹窗。</summary>
        internal void KeepFoot(string s)
        {
            if (lblFoot != null && s != null) lblFoot.Text = s;
        }

        internal int Unmatched { get { return unmatched; } }

        /// <summary>自测用：未归类页列表当前列了几行（先 SelectUnmatched 再读）。</summary>
        internal int UnmatchedRows
        {
            get
            {
                int n = 0;
                foreach (ListViewItem li in lv.Items)
                    if (li.Tag is ModGroup) n++;      // 「同类包」伙头行不算
                return n;
            }
        }
        /// <summary>自测用：「指定位置…」手动入口是否可见。</summary>

        internal List<ModGroup> UnmatchedGroups { get { return unmatchedGroups; } }

        // ---------------- 同类圈伙（AI 聚类的落地用法） ----------------
        // 不给聚类起名、不拿它认槽位（实验证明那只有 75% 准、答不出"替换目标"）。
        // 它干一件查表干不了的事：把"内容确实像"的包（库一份+游戏一份的重复、
        // 同款不同名的变体）在未归类页圈成一伙，提示"挑一个装就行"。
        // 相似度 = 包内路径 token 集合的 Jaccard 系数，阈值取自聚类实验（0.6 时同伙纯度 93%）。
        private const double ClusterThreshold = 0.6;

        private static HashSet<string> ClusterTokens(ModItem m)
        {
            HashSet<string> t = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (m == null || string.IsNullOrEmpty(m.AssetPaths)) return t;
            foreach (string p in m.AssetPaths.Split(';'))
            {
                if (p.Length == 0) continue;
                string[] parts = p.Split('/');
                for (int i = 0; i < parts.Length; i++)
                {
                    string seg = parts[i];
                    if (seg.Length == 0) continue;
                    int dot = seg.LastIndexOf('.');
                    if (dot > 0)
                    {
                        t.Add("ext:" + seg.Substring(dot + 1));
                        string stem = seg.Substring(0, dot);
                        if (stem.StartsWith("v_", StringComparison.OrdinalIgnoreCase)
                            || stem.StartsWith("w_", StringComparison.OrdinalIgnoreCase))
                            stem = stem.Substring(2);
                        t.Add("name:" + stem);
                    }
                    else t.Add("dir:" + seg);
                }
                t.Add("top:" + parts[0]);
            }
            return t;
        }

        private static double Jaccard(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int inter = 0;
            foreach (string s in a) if (b.Contains(s)) inter++;
            return (double)inter / (a.Count + b.Count - inter);
        }

        /// <summary>把未归类的组按内容相似度圈伙：返回「伙 -> 成员」，只含有 2 个及以上成员的伙。</summary>
        internal static Dictionary<ModGroup, List<ModGroup>> ClusterUnmatched(List<ModGroup> groups, out int clustered)
        {
            clustered = 0;
            Dictionary<ModGroup, List<ModGroup>> result = new Dictionary<ModGroup, List<ModGroup>>();
            int n = groups.Count;
            if (n < 2) return result;
            List<HashSet<string>> tok = new List<HashSet<string>>(n);
            for (int i = 0; i < n; i++) tok.Add(ClusterTokens(groups[i].Main));
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            System.Func<int, int> find = null;
            find = delegate(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (Jaccard(tok[i], tok[j]) >= ClusterThreshold)
                    { int pi = find(i), pj = find(j); if (pi != pj) parent[pi] = pj; }
            Dictionary<int, List<ModGroup>> byRoot = new Dictionary<int, List<ModGroup>>();
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                if (!byRoot.ContainsKey(r)) byRoot[r] = new List<ModGroup>();
                byRoot[r].Add(groups[i]);
            }
            foreach (List<ModGroup> members in byRoot.Values)
            {
                if (members.Count < 2) continue;
                clustered += members.Count;
                foreach (ModGroup m in members) result[m] = members;
            }
            return result;
        }
        internal int RowCount { get { return body == null ? 0 : body.RowCount; } }
        internal int SlotCount { get { return slots.Count; } }
        internal int SlotCheckedCount
        {
            get { int n = 0; foreach (Slot s in slots) if (HasChecked(s)) n++; return n; }
        }

        /// <summary>外部勾选变了：只重画，不重建行。</summary>
        public void Sync()
        {
            body.Invalidate();
            FillDetail();
        }

        /// <summary>
        /// 重扫过后 Catalog.Groups 是新建的一批对象，槽位里还攥着旧的 —— 那样状态列会一直显示旧值。
        /// 这里探一下引用是否还新鲜：换了就整棵重建，没换就只重画（搜索打字不该重排列表）。
        /// </summary>
        public void SyncIfSame()
        {
            if (slots.Count == 0) { Rebuild(); return; }
            ModGroup probe = null;
            foreach (Slot s in slots)
            {
                if (s.Items.Count > 0) { probe = s.Items[0]; break; }
            }
            if (probe != null && Catalog.Groups.IndexOf(probe) < 0) { Rebuild(); return; }
            Sync();
        }

        public void SelectUnmatched()
        {
            if (selKey == "#unmatched") return;
            selKey = "#unmatched";
            body.Invalidate();
            FillDetail();
            // 这一页的固定说明（不随光标变，也不占别的行）
            if (lblFoot != null)
                lblFoot.Text = "这些是程序没认出位置的包：按住一行拖到左边的树上归类。" + (Catalog.BrokenCount > 0 ? "（另有 " + Catalog.BrokenCount + " 个包读不出内容，见日志）" : "");
        }

        public void SelectSlot(string key)
        {
            SelectSlot(key, false);
        }

        /// <summary>选中一个位置。reveal=true 时顺带把它滚进视野（搜索跳转 / 诊断用 --uiselect 走这条）。</summary>
        public void SelectSlot(string key, bool reveal)
        {
            if (key == null) return;
            if (key == "#mine+new") { NewBranch(); return; }   // 点「＋ 新建分支…」直接弹输入框
            if (key == selKey)
            {
                if (reveal) body.ScrollToSlot(key);
                return;
            }
            RevealFold(key);                     // 折叠着的小分类先展开，选中的行才看得见
            selKey = key;
            body.Invalidate();
            FillDetail();
            if (reveal) body.ScrollToSlot(key);
        }

        /// <summary>自测用：把第 n 个槽位选出来。</summary>
        public Slot SlotAt(int n)
        {
            return (n >= 0 && n < slots.Count) ? slots[n] : null;
        }

        /// <summary>自测用：按显示名找槽位。</summary>
        public Slot SlotByName(string name)
        {
            foreach (Slot s in slots) if (s.Name == name) return s;
            return null;
        }

        /// <summary>
        /// 自测：只走键盘路径 —— ↑↓ 移动焦点、Home/End/PageUp/PageDown 跳转、
        /// Enter 与空格选中槽位、←→ 折叠展开大类。返回不通过的条数。
        /// 之所以要单独跑一遍：鼠标路径一直有测，键盘路径以前只有代码没有证据。
        /// </summary>
        internal int SelfTestKeys()
        {
            int bad = 0;
            if (slots.Count == 0 || body.RowCount == 0)
            {
                Core.Log("WARN", "KEY 库里没有槽位，跳过键盘自测");
                return 0;
            }
            string orig = selKey;
            Core.Log("INFO", "KEY 开始：行数 " + body.RowCount + "，槽位 " + slots.Count + " 个");

            // 0) 空位：库里没有 MOD 的标准位置也要占一行（「每种物品都显示」）
            int empty = SlotIndex.EmptyCount(slots), dirty = 0;
            foreach (Slot s in slots) if (s.Empty && s.Items.Count > 0) dirty++;
            int missing = SlotIndex.MissingStandard();
            Core.Log(empty > 0 && missing == 0 && dirty == 0 ? "INFO" : (dirty == 0 && missing == 0 ? "WARN" : "FAIL"),
                "KEY 空位置 " + empty + " 个，标准位置漏掉的 " + missing + " 个，空位里混进候选的 " + dirty + " 个");
            if (missing > 0) bad++;
            if (dirty > 0) bad++;

            // 0b) 识别表对账：表里认得出来的每一种东西，树上都得有位置。
            //     不盯这条的话，往识别表加个新型号、忘了加位置，那个包装上以后只会掉进「未归类」。
            string uncovered = SlotIndex.UncoveredNames();
            Core.Log(uncovered.Length == 0 ? "INFO" : "FAIL",
                "KEY 识别表覆盖：认出 " + SlotIndex.SubjectCount + " 种，位置表 " + SlotIndex.PositionCount
                + " 个位置，没着落的 " + (uncovered.Length == 0 ? "0 个" : uncovered));
            if (uncovered.Length > 0) bad++;

            // 0c) 位置表反推：每个标准位置按分类规则重新分一次，得回到原来那一层。
            //     不然真出现那种包时会另起一行，看着像两个不同的东西。
            string misfiled = SlotIndex.MisfiledNames();
            Core.Log(misfiled.Length == 0 ? "INFO" : "FAIL",
                "KEY 位置归类：" + (misfiled.Length == 0 ? "全部对得上" : misfiled));
            if (misfiled.Length > 0) bad++;
            Slot firstFilled = null;
            foreach (Slot s in slots) if (!s.Empty) { firstFilled = s; break; }
            Core.Log(firstFilled != null && selKey == firstFilled.Key ? "INFO" : "WARN",
                "KEY 默认选中：" + (firstFilled == null ? "（库里没有 MOD）" : firstFilled.Name));

            // 1) 首行 / 上下移动
            body.TestKey(Keys.Home);
            if (body.FocusIndex != 0) { bad++; Core.Log("FAIL", "KEY Home 之后焦点在第 " + body.FocusIndex + " 行，应该是第 0 行"); }
            else Core.Log("INFO", "KEY Home → 第 0 行");

            if (body.RowCount > 2)
            {
                body.TestKey(Keys.Down);
                if (body.FocusIndex != 1) { bad++; Core.Log("FAIL", "KEY ↓ 之后焦点在第 " + body.FocusIndex + " 行，应该是第 1 行"); }
                else Core.Log("INFO", "KEY ↓ → 第 1 行");

                body.TestKey(Keys.Up);
                if (body.FocusIndex != 0) { bad++; Core.Log("FAIL", "KEY ↑ 没有回到第 0 行（现在第 " + body.FocusIndex + " 行）"); }
                else Core.Log("INFO", "KEY ↑ → 第 0 行");

                // 2) End / PageUp / PageDown（越界要夹住，不能跑到行外面）
                int last = body.RowCount - 1;
                body.TestKey(Keys.End);
                if (body.FocusIndex != last) { bad++; Core.Log("FAIL", "KEY End 焦点在第 " + body.FocusIndex + " 行，应该是最后一行 " + last); }
                else Core.Log("INFO", "KEY End → 最后一行（" + last + "）");

                body.TestKey(Keys.Down);
                if (body.FocusIndex != last) { bad++; Core.Log("FAIL", "KEY 最后一行再按 ↓ 跑出了范围（第 " + body.FocusIndex + " 行）"); }
                else Core.Log("INFO", "KEY 最后一行再按 ↓ 仍停在最后一行");

                body.TestKey(Keys.PageUp);
                int pu = body.FocusIndex;
                if (pu >= last) { bad++; Core.Log("FAIL", "KEY PageUp 没有往上走（仍在第 " + pu + " 行）"); }
                else Core.Log("INFO", "KEY PageUp → 第 " + pu + " 行");

                body.TestKey(Keys.PageDown);
                if (body.FocusIndex <= pu) { bad++; Core.Log("FAIL", "KEY PageDown 没有往下走（仍在第 " + body.FocusIndex + " 行）"); }
                else Core.Log("INFO", "KEY PageDown → 第 " + body.FocusIndex + " 行");
            }

            // 3) ←→ 折叠展开：第 0 行是大类
            body.TestKey(Keys.Home);
            if (body.FocusedRowIsCat)
            {
                int n0 = body.RowCount;
                body.TestKey(Keys.Left);
                int n1 = body.RowCount;
                if (n1 >= n0) { bad++; Core.Log("FAIL", "KEY ← 没折叠第 0 行的大类（行数 " + n0 + " → " + n1 + "）"); }
                else Core.Log("INFO", "KEY ← 折叠大类：行数 " + n0 + " → " + n1);

                body.TestKey(Keys.Right);
                int n2 = body.RowCount;
                if (n2 != n0) { bad++; Core.Log("FAIL", "KEY → 展开后行数是 " + n2 + "，应该回到 " + n0); }
                else Core.Log("INFO", "KEY → 展开回来：行数 " + n2);
            }
            else Core.Log("WARN", "KEY 第 0 行不是大类，跳过折叠测试（树结构可能变了）");

            // 3b) 小分类层也要能收：同一条代码路径，但走的是 SetSubFolded
            body.TestKey(Keys.Home);
            bool sawSub = false;
            for (int i = 0; i < body.RowCount; i++)
            {
                if (body.FocusedRowIsSub) { sawSub = true; break; }
                body.TestKey(Keys.Down);
            }
            if (!sawSub) Core.Log("WARN", "KEY 树里没有小分类行，跳过小分类折叠测试");
            else
            {
                int s0 = body.RowCount;
                body.TestKey(Keys.Left);
                int s1 = body.RowCount;
                if (s1 >= s0) { bad++; Core.Log("FAIL", "KEY ← 没折叠小分类（行数 " + s0 + " → " + s1 + "）"); }
                else Core.Log("INFO", "KEY ← 折叠小分类：行数 " + s0 + " → " + s1);
                Core.Log("INFO", "KEY 折叠后焦点在第 " + body.FocusIndex + " 行" + (body.FocusedRowIsSub ? "（仍是小分类行）" : "（不是小分类行了）"));

                body.TestKey(Keys.Right);
                int s2 = body.RowCount;
                if (s2 != s0) { bad++; Core.Log("FAIL", "KEY → 小分类展开后行数 " + s2 + "，应该回到 " + s0); }
                else Core.Log("INFO", "KEY → 小分类展开回来：行数 " + s2);
            }

            // 4) Enter 选中：一路 ↓ 找第一个槽位行
            body.TestKey(Keys.Home);
            string k1 = "";
            for (int i = 0; i < body.RowCount; i++)
            {
                if (body.FocusedRowIsSlot) { k1 = body.FocusedSlotKey; break; }
                body.TestKey(Keys.Down);
            }
            if (body.FocusedRowIsSlot && k1.Length == 0) k1 = body.FocusedSlotKey;
            if (k1.Length == 0)
            {
                bad++;
                Core.Log("FAIL", "KEY 一路按 ↓ 都没落到槽位行上");
            }
            else
            {
                body.TestKey(Keys.Enter);
                if (selKey != k1) { bad++; Core.Log("FAIL", "KEY Enter 之后选中「" + selKey + "」，应该是「" + k1 + "」"); }
                else Core.Log("INFO", "KEY Enter → 选中槽位「" + k1 + "」");
            }

            // 5) 空格选中另一件：两条键都得能选，不能只有 Enter 好使
            if (k1.Length > 0)
            {
                string k2 = "";
                for (int i = 0; i < body.RowCount; i++)
                {
                    body.TestKey(Keys.Down);
                    if (body.FocusedRowIsSlot && body.FocusedSlotKey != k1) { k2 = body.FocusedSlotKey; break; }
                }
                if (k2.Length == 0) Core.Log("WARN", "KEY 后面没有第二个槽位，跳过空格键测试");
                else
                {
                    body.TestKey(Keys.Space);
                    if (selKey != k2) { bad++; Core.Log("FAIL", "KEY 空格之后选中「" + selKey + "」，应该是「" + k2 + "」"); }
                    else Core.Log("INFO", "KEY 空格 → 选中槽位「" + k2 + "」");
                }
            }

            // 6) 点树的空白处：焦点应当被清掉。以前这里什么都不做，上一行的焦点框一直挂着，
            //    看着像「这行还选着」，按方向键又从那行接着走 —— 键盘用户点了空白完全没反应。
            body.TestKey(Keys.Home);
            int fb = body.TestClickBlank();
            if (fb != -1) { bad++; Core.Log("FAIL", "BLANK 点空白后焦点仍在第 " + fb + " 行，应该清掉"); }
            else Core.Log("INFO", "BLANK 点空白后焦点已清空");

            // 7) 大类与小分类的边界：近战 / 投掷物 / 弹药升级 各自只有一层。
            //    名字里碰巧带「冲锋」「步枪」的包，不能在这几个大类底下冒出别的枪种小分类
            //    （SubRank 还会把那个小分类排到该类第一位，看着像分类错乱）。
            int subBad = 0;
            string[] traps = { "MAC-10", "AK-47", "步枪", "冲锋枪/MAC-10", "rifle" };
            foreach (string t in traps)
            {
                string sb = SlotIndex.SubOf("近战", t, "", "", "");
                if (sb != "近战") { subBad++; Core.Log("FAIL", "SUB 近战大类下的「" + t + "」被分到「" + sb + "」"); }
            }
            if (SlotIndex.SubOf("投掷物", "榴弹发射器", "", "", "") != "投掷物")
            { subBad++; Core.Log("FAIL", "SUB 投掷物大类下的「榴弹发射器」被分到了别的层"); }
            if (SlotIndex.SubOf("弹药升级", "燃烧弹药包", "", "", "") != "弹药升级")
            { subBad++; Core.Log("FAIL", "SUB 弹药升级大类下的「燃烧弹药包」被分到了别的层"); }
            // 枪械这边不能反过来：AK-47 得老实落在「步枪」
            if (SlotIndex.SubOf("枪械", "AK-47", "", "", "") != "步枪")
            { subBad++; Core.Log("FAIL", "SUB 枪械大类下的「AK-47」没落在「步枪」"); }
            // 人物：英文名也得落回一代 / 二代，不能因为没换成中文就掉进「其他人物」
            if (SlotIndex.SubOf("人物", "Zoey", "", "", "") != "一代")
            { subBad++; Core.Log("FAIL", "SUB 英文名「Zoey」没落到「一代」"); }
            if (SlotIndex.SubOf("人物", "Coach", "", "", "") != "二代")
            { subBad++; Core.Log("FAIL", "SUB 英文名「Coach」没落到「二代」"); }
            if (SlotIndex.SubOf("人物", "幸存者", "", "", "") != "八人包")
            { subBad++; Core.Log("FAIL", "SUB 泛称「幸存者」应该留在「八人包」"); }
            bad += subBad;
            if (subBad == 0) Core.Log("INFO", "SUB 单层大类的子分类都守在自己那一层，枪械的四层也没串");

            // 8) 重名标签：槽位键是「大类\u0001位置键」，标签里不能漏出大类、控制字符和 c:/sub: 前缀
            string tg = SlotIndex.Tag("枪械\u0001c:鸟狙");
            string tg2 = SlotIndex.Tag("枪械\u0001w_sniper_scout");
            string tg3 = SlotIndex.Tag("枪械\u0001sub:幸存者");
            if (tg.IndexOf('\u0001') >= 0 || tg.IndexOf(':') >= 0 || tg.Length == 0
                || tg2.IndexOf('\u0001') >= 0 || tg3.IndexOf('\u0001') >= 0)
            {
                bad++;
                Core.Log("FAIL", "SUB 重名标签没洗干净：" + tg + " / " + tg2 + " / " + tg3);
            }
            else Core.Log("INFO", "SUB 重名标签 = " + tg + " / " + tg2 + " / " + tg3);

            // 9) 收尾：把选中恢复成测试前的位置，别改动用户看到的界面状态
            if (orig.Length > 0 && orig != "#unmatched" && selKey != orig) SelectSlot(orig);
            // 10) 我的分支的父子关系（自测会建两个临时分支，结束时删掉）
            bad += BranchSelfTest();
            // 11) 未归类 MOD 拖到树上归类
            bad += DragAssignSelfTest();
            Core.Log("INFO", "KEY 结束：不通过 " + bad + " 条");
            return bad;
        }

        /// <summary>
        /// 自检：未归类 MOD 拖到树上归类。不发起真 OLE 拖放（那是鼠标层的活），直接走
        /// SimulateExtDrop = 拖动判定 + 松手落点这整条逻辑链。对账三件事：
        /// 说明行拒绝落点、落进位置后真的离开未归类列表、恢复自动后回得来（零残留）。
        /// </summary>
        internal int DragAssignSelfTest()
        {
            int bad = 0;
            string orig = selKey;
            ModGroup g = null;
            try
            {
                SelectUnmatched();
                if (unmatchedGroups.Count == 0)
                {
                    Core.Log("INFO", "DND 库里没有未归类的 MOD，拖拽归类用例跳过");
                    return 0;
                }
                g = unmatchedGroups[0];


                // 2) 落到第一个普通位置行：必须真归类，且离开未归类列表
                Rebuild();
                int target = -1;
                for (int i = 0; i < body.RowCount; i++)
                {
                    Slot s = body.RowSlot(i);
                    if (s == null || CatTree.IsBranchSlot(s) || s.Pile) continue;
                    target = i; break;
                }
                if (target < 0) { Core.Log("WARN", "DND 树里没有普通位置行，归类用例跳过"); return bad; }
                Slot tgt = body.RowSlot(target);
                string done = body.SimulateExtDrop(target, g);
                if (!done.StartsWith("ok")) { bad++; Core.Log("FAIL", "DND 落到「" + tgt.Name + "」没执行：" + done); }
                else
                {
                    SelectUnmatched();
                    bool gone = true;
                    foreach (ModGroup x in unmatchedGroups) if (x == g) { gone = false; break; }
                    if (!gone) { bad++; Core.Log("FAIL", "DND 归类后「" + g.Name + "」还在未归类列表里"); }
                    else Core.Log("INFO", "DND 「" + g.Name + "」→「" + tgt.Cat + " · " + tgt.Name + "」，已离开未归类");
                }

                // 3) 恢复自动：回得来（这条保证自检不污染用户数据）
                CatPrefs.Reset(g);
                Catalog.ApplyCats();
                Rebuild();
                SelectUnmatched();
                bool back = false;
                foreach (ModGroup x in unmatchedGroups) if (x == g) { back = true; break; }
                if (!back) { bad++; Core.Log("FAIL", "DND 恢复自动后「" + g.Name + "」没回未归类"); }
                else Core.Log("INFO", "DND 恢复自动后回到未归类（自检零残留）");
            }
            catch (Exception ex)
            {
                bad++;
                Core.Log("FAIL", "DND 自检抛异常：" + ex.Message);
                try { if (g != null) CatPrefs.Reset(g); } catch { }
            }
            finally
            {
                try { if (orig.Length > 0 && orig != "#unmatched") SelectSlot(orig); } catch { }
            }
            return bad;
        }

        /// <summary>
        /// 自检：分支能不能挂在分支下面、树里顺序对不对、父分支解散后子分支会不会提上来。
        /// 会临时建两个名字带「自检」的分支，测完删掉，用户的真分支不动。
        /// </summary>
        internal int BranchSelfTest()
        {
            int bad = 0;
            string pa = "自检父" + Environment.TickCount.ToString();
            string ch = "自检子" + Environment.TickCount.ToString();
            string s1 = "自检甲" + Environment.TickCount.ToString();
            string s2 = "自检乙" + Environment.TickCount.ToString();
            List<string> catBackup = CatPrefs.Cats();          // 大类拖动自检会改顺序，测完按原名次摆回去
            List<string> foldBackup = new List<string>(folded);   // 自检要展开「我的分支」，测完按原折叠状态恢复
            SetFolded("我的分支", false);
            try
            {
                string err = MyBranch.Create(pa);
                if (err != null) { Core.Log("FAIL", "BRANCH 建父分支失败：" + err); bad++; return bad; }
                err = MyBranch.Create(ch, pa);
                if (err != null) { Core.Log("FAIL", "BRANCH 在父分支下建子分支失败：" + err); bad++; return bad; }

                if (MyBranch.ParentOf(ch) != pa) { bad++; Core.Log("FAIL", "BRANCH 子分支的父不是「" + pa + "」，是「" + MyBranch.ParentOf(ch) + "」"); }
                else Core.Log("INFO", "BRANCH 子分支「" + ch + "」挂在「" + pa + "」下面，层级 " + MyBranch.Depth(ch));

                List<string> ns = MyBranch.Names();
                int ia = ns.IndexOf(pa), ib = ns.IndexOf(ch);
                if (ia < 0 || ib < 0 || ib != ia + 1) { bad++; Core.Log("FAIL", "BRANCH 树顺序不对：父在第 " + ia + "，子在第 " + ib + "（应该紧跟在后面）"); }
                else Core.Log("INFO", "BRANCH 树顺序：父第 " + ia + "、子第 " + ib + "（紧挨着）");

                // 换成树里的槽位：子分支的行号必须紧跟父分支
                Rebuild();
                int si = -1, sj = -1;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].Key == "#mine:" + pa) si = i;
                    if (slots[i].Key == "#mine:" + ch) sj = i;
                }
                if (si < 0 || sj < 0 || sj <= si) { bad++; Core.Log("FAIL", "BRANCH 槽位顺序不对：父 " + si + "、子 " + sj); }
                else Core.Log("INFO", "BRANCH 槽位顺序：父 " + si + "、子 " + sj);

                // 右侧列表「在当前分支下新建子分支」的入口：打开分支页后要能拿到当前分支
                GoSlot("#mine:" + ch);
                Slot curB = CurrentBranch;
                if (curB == null || !string.Equals(curB.Name, ch, StringComparison.Ordinal))
                { bad++; Core.Log("FAIL", "BRANCH 打开「" + ch + "」后 CurrentBranch 取不到它（" + (curB == null ? "null" : curB.Name) + "）"); }
                else Core.Log("INFO", "BRANCH 打开「" + ch + "」后 CurrentBranch 正确，右键菜单能建它的子分支");
                GoSlot("#mine:" + pa);

                // 父分支解散 → 子分支提回顶层，不能跟着消失
                MyBranch.Delete(pa);
                if (!MyBranch.Has(ch)) { bad++; Core.Log("FAIL", "BRANCH 父分支解散后子分支「" + ch + "」也没了"); }
                else if (MyBranch.HasParent(ch)) { bad++; Core.Log("FAIL", "BRANCH 父分支解散后子分支仍挂在「" + MyBranch.ParentOf(ch) + "」上"); }
                else Core.Log("INFO", "BRANCH 父分支解散后子分支提回顶层");

                // 拖动管理：换顺序 / 改成子分支 / 拒绝成环 —— 和鼠标松手走同一条 ApplyDrop
                string e1 = MyBranch.Create(s1), e2 = MyBranch.Create(s2);
                if (e1 != null || e2 != null)
                {
                    bad++;
                    Core.Log("FAIL", "BRANCH 拖动自检：临时分支没建起来（" + (e1 ?? e2) + "）");
                }
                else
                {
                    Rebuild();
                    int r1 = body.RowIndexOfSlot("#mine:" + s1);
                    if (r1 < 0) { bad++; Core.Log("FAIL", "BRANCH 拖动自检：树里找不到「" + s1 + "」那一行"); }
                    else if (!body.ApplyDrop(s2, r1, 1)) { bad++; Core.Log("FAIL", "BRANCH 拖动自检：拖到「" + s1 + "」前面没执行"); }
                    else
                    {
                        List<string> nn = MyBranch.Names();
                        int a = nn.IndexOf(s1), b = nn.IndexOf(s2);
                        if (a < 0 || b < 0 || b > a) { bad++; Core.Log("FAIL", "BRANCH 拖动排序没生效：甲第 " + a + "、乙第 " + b); }
                        else Core.Log("INFO", "BRANCH 拖动排序：乙第 " + b + "、甲第 " + a + "（乙挪到前面了）");
                    }

                    Rebuild();
                    int ra = body.RowIndexOfSlot("#mine:" + s1);
                    if (ra < 0 || !body.ApplyDrop(s2, ra, 3)) { bad++; Core.Log("FAIL", "BRANCH 拖动自检：拖成子分支没执行"); }
                    else if (MyBranch.ParentOf(s2) != s1)
                    {
                        bad++;
                        Core.Log("FAIL", "BRANCH 拖成子分支失败：乙的父是「" + MyBranch.ParentOf(s2) + "」");
                    }
                    else Core.Log("INFO", "BRANCH 拖成子分支：乙挂在甲下面，层级 " + MyBranch.Depth(s2));

                    Rebuild();
                    int rb = body.RowIndexOfSlot("#mine:" + s2);
                    if (rb >= 0)
                    {
                        body.ApplyDrop(s1, rb, 3);      // 乙是甲的子孙，这一步必须被拒绝
                        if (MyBranch.ParentOf(s1).Length > 0)
                        {
                            bad++;
                            Core.Log("FAIL", "BRANCH 拖到自己的子分支下面居然成了（父=" + MyBranch.ParentOf(s1) + "）");
                        }
                        else Core.Log("INFO", "BRANCH 拖到自己的子分支下面被拒绝，甲还在顶层");
                    }

                    // 子分支拖回顶层：走真实鼠标路径（按下乙 → 挪到甲行上半 → 松手），
                    // 乙必须脱出甲回到顶层 —— 用户反馈「大分支下的小分支不能拖」，这条必须有
                    Rebuild();
                    int rc = body.RowIndexOfSlot("#mine:" + s2);
                    int rd = body.RowIndexOfSlot("#mine:" + s1);
                    if (rc < 0 || rd < 0) { bad++; Core.Log("FAIL", "BRANCH 树里找不到子分支行（乙=" + rc + " 甲=" + rd + "）"); }
                    else
                    {
                        string stD;
                        body.SimulateRealDrag(rc, rd, 1, out stD);
                        Core.Log("INFO", "BRANCH 子分支拖出路径 " + stD);
                        if (!stD.Contains("pressArm=True")) { bad++; Core.Log("FAIL", "BRANCH 子分支行按下去没有拖动资格（pressArm=False）"); }
                        else if (MyBranch.ParentOf(s2).Length > 0)
                        { bad++; Core.Log("FAIL", "BRANCH 子分支拖回顶层没生效，父还是「" + MyBranch.ParentOf(s2) + "」"); }
                        else Core.Log("INFO", "BRANCH 子分支拖回顶层生效（真实鼠标路径）");
                    }

                    // 拖到一半被系统收走鼠标（拖出窗口松手 / Alt+Tab）：必须自己收尾。
                    // 原来没有这层兜底，dragging 会一直挂着 —— 光标停在十字、树跟着鼠标画预览，
                    // 下一次点击还会拿上一次的落点去搬东西。
                    Rebuild();
                    int rl = body.RowIndexOfSlot("#mine:" + s1);
                    if (rl < 0) { bad++; Core.Log("FAIL", "DRAGLOST 树里找不到分支行"); }
                    else
                    {
                        string stL = body.SimulateLostCapture(rl);
                        Core.Log("INFO", "DRAGLOST 失去捕获 " + stL);
                        if (!stL.Contains("dragging=True")) { bad++; Core.Log("FAIL", "DRAGLOST 用例前提没成立：" + stL); }
                        else if (!stL.Contains("→ dragging=False pressArm=False preview=清空"))
                        { bad++; Core.Log("FAIL", "DRAGLOST 失去捕获后状态没清干净：" + stL); }
                        else Core.Log("INFO", "DRAGLOST 失去捕获后拖动已取消、预览已撤");
                    }

                    // 大类拖动：挪到另一个大类后面，走和鼠标松手一样的 ApplyDrop，
                    // 顺序必须真的变、且写进分类表（重启还在）
                    List<string> before = CatPrefs.Cats();
                    if (before.Count >= 2)
                    {
                        string a = before[0], b = before[1];
                        Rebuild();
                        int tb = FindCatRow(b);
                        if (tb < 0) { bad++; Core.Log("FAIL", "CATDRAG 树里找不到大类「" + b + "」那一行"); }
                        else if (!body.ApplyDrop(a, 2, tb, 2)) { bad++; Core.Log("FAIL", "CATDRAG 拖到「" + b + "」后面没执行"); }
                        else
                        {
                            List<string> after = CatPrefs.Cats();
                            int ca = after.IndexOf(a), cb = after.IndexOf(b);
                            if (ca != cb + 1) { bad++; Core.Log("FAIL", "CATDRAG 顺序没生效：" + a + " 第 " + ca + "、" + b + " 第 " + cb); }
                            else Core.Log("INFO", "CATDRAG 「" + a + "」挪到「" + b + "」后面，分类表顺序已更新");
                            // 拖到自己身上必须被拒绝
                            Rebuild();
                            int ta = FindCatRow(a);
                            if (ta >= 0 && body.ApplyDrop(a, 2, ta, 1))
                            {
                                bad++;
                                Core.Log("FAIL", "CATDRAG 大类拖到自己身上居然成功了");
                            }
                        }
                    }

                    // ---- 位置 / 小分类也能拖（kind 3 / 4）：用户要「全树任意行都能拖」------------
                    // 这几条会动 方案树分类.ini，所以先把原文整个拷走，测完一字不差写回去
                    string prefFile = CatPrefs.FileOf(), prefRaw = null;
                    try { if (File.Exists(prefFile)) prefRaw = Convert.ToBase64String(File.ReadAllBytes(prefFile)); } catch { }
                    try
                    {
                        Rebuild();
                        int p1 = -1, p2 = -1;
                        for (int i = 0; i + 1 < body.RowCount; i++)
                        {
                            Slot x1 = body.RowSlot(i), x2 = body.RowSlot(i + 1);
                            if (x1 == null || x2 == null) continue;
                            if (CatTree.IsBranchSlot(x1) || x1.Pile) continue;
                            if (x1.Cat != x2.Cat || x1.Sub != x2.Sub) continue;
                            p1 = i; p2 = i + 1; break;
                        }
                        if (p1 < 0) Core.Log("WARN", "ORDER 没有同一小分类下相邻的两个位置，跳过位置换序用例");
                        else
                        {
                            string c = body.RowSlot(p1).Cat, sb = body.RowSlot(p1).Sub;
                            string nmA = body.RowSlot(p1).Name, nmB = body.RowSlot(p2).Name;
                            if (!body.ApplyDrop(nmA, 4, p2, 2, c, sb)) { bad++; Core.Log("FAIL", "ORDER 位置「" + nmA + "」拖到「" + nmB + "」后面没执行"); }
                            else
                            {
                                int q1 = body.RowIndexOfPos(c, sb, nmA), q2 = body.RowIndexOfPos(c, sb, nmB);
                                if (q1 < 0 || q2 < 0 || q1 <= q2) { bad++; Core.Log("FAIL", "ORDER 位置换序没生效：" + nmA + "@" + q1 + "、" + nmB + "@" + q2); }
                                else Core.Log("INFO", "ORDER 位置「" + nmA + "」拖到「" + nmB + "」后面生效（" + q2 + " → " + q1 + "）");
                            }

                            // 同一件事再走一遍真实鼠标路径：按下必须有拖资格、起拖必须是 kind=4、落点必须有模式
                            Rebuild();
                            int ra1 = -1, ra2 = -1;
                            for (int i = 0; i + 1 < body.RowCount; i++)
                            {
                                Slot x1 = body.RowSlot(i), x2 = body.RowSlot(i + 1);
                                if (x1 == null || x2 == null) continue;
                                if (CatTree.IsBranchSlot(x1) || x1.Pile) continue;
                                if (x1.Cat != x2.Cat || x1.Sub != x2.Sub) continue;
                                ra1 = i; ra2 = i + 1; break;
                            }
                            if (ra1 < 0) Core.Log("WARN", "ORDER 没有相邻位置行，跳过鼠标路径用例");
                            else
                            {
                                string stP;
                                body.SimulateRealDrag(ra1, ra2, 2, out stP);
                                Core.Log("INFO", "ORDER 位置鼠标路径 " + stP);
                                if (!stP.Contains("pressArm=True")) { bad++; Core.Log("FAIL", "ORDER 位置行按下去没有拖动资格：" + stP); }
                                else if (!stP.Contains("kind=4")) { bad++; Core.Log("FAIL", "ORDER 位置行起拖的 kind 不是 4：" + stP); }
                                else if (!stP.Contains("dropMode=2")) { bad++; Core.Log("FAIL", "ORDER 位置行落点没解析出来：" + stP); }
                                else if (!stP.Contains("pv=" + body.RowCount + "/" + body.RowCount))
                                { bad++; Core.Log("FAIL", "ORDER 位置行拖动预览没画出来（行数应与原树一致）：" + stP); }
                                else Core.Log("INFO", "ORDER 位置行真实鼠标路径全程走通（含拖动预览）");
                            }
                        }

                        // 跨小分类：把一个位置拖进另一个小分类，归属必须跟着改
                        Rebuild();
                        string catX = null, subFrom = null, subTo = null;
                        int rowFrom = -1, rowTo = -1;
                        for (int i = 0; i < body.RowCount; i++)
                        {
                            Slot s = body.RowSlot(i);
                            if (s == null || CatTree.IsBranchSlot(s) || s.Pile) continue;
                            if (catX == null) { catX = s.Cat; subFrom = s.Sub; rowFrom = i; continue; }
                            if (s.Cat == catX && s.Sub != subFrom) { subTo = s.Sub; rowTo = i; break; }
                        }
                        if (catX == null || subTo == null) Core.Log("WARN", "ORDER 没有跨小分类的样本，跳过 kind=4 跨类用例");
                        else
                        {
                            string nmM = body.RowSlot(rowFrom).Name;
                            if (!body.ApplyDrop(nmM, 4, rowTo, 2, catX, subFrom)) { bad++; Core.Log("FAIL", "ORDER 位置「" + nmM + "」拖进「" + subTo + "」没执行"); }
                            else if (body.RowIndexOfPos(catX, subTo, nmM) < 0) { bad++; Core.Log("FAIL", "ORDER 位置「" + nmM + "」没进「" + subTo + "」这一段"); }
                            else Core.Log("INFO", "ORDER 位置「" + nmM + "」跨小分类拖进「" + subTo + "」生效");
                        }

                        // 小分类换顺序：把最后一个拖到第一个前面
                        Rebuild();
                        if (catX == null) Core.Log("WARN", "ORDER 没有大类样本，跳过 kind=3 用例");
                        else
                        {
                            List<string> subsNow = CurrentSubs(catX);
                            if (subsNow.Count < 2) Core.Log("WARN", "ORDER 「" + catX + "」只有一个小分类，跳过 kind=3 用例");
                            else
                            {
                                string sA = subsNow[subsNow.Count - 1], sB = subsNow[0];
                                int rA = body.RowIndexOfSub(catX, sA), rB = body.RowIndexOfSub(catX, sB);
                                if (rA < 0 || rB < 0) Core.Log("WARN", "ORDER 单分组大类不画小标题行，跳过 kind=3 用例");
                                else
                                {
                                    if (!body.ApplyDrop(sA, 3, rB, 1, catX, "")) { bad++; Core.Log("FAIL", "ORDER 小分类「" + sA + "」拖到「" + sB + "」前面没执行"); }
                                    else
                                    {
                                        List<string> after2 = CatPrefs.SubOrderOf(catX);
                                        int xa = after2 == null ? -1 : after2.IndexOf(sA);
                                        int xb = after2 == null ? -1 : after2.IndexOf(sB);
                                        if (xa < 0 || xb < 0 || xa > xb) { bad++; Core.Log("FAIL", "ORDER 小分类顺序没生效：" + sA + "@" + xa + "、" + sB + "@" + xb); }
                                        else Core.Log("INFO", "ORDER 小分类「" + sA + "」拖到「" + sB + "」前面生效");
                                    }
                                    // 跨大类必须被拒绝：小分类不属于别的大类
                                    int rOther = -1;
                                    for (int i = 0; i < body.RowCount; i++)
                                    {
                                        Slot s = body.RowSlot(i);
                                        if (s != null && s.Cat != catX && !CatTree.IsBranchSlot(s)) { rOther = i; break; }
                                    }
                                    if (rOther >= 0 && body.ApplyDrop(sA, 3, rOther, 1, catX, ""))
                                    {
                                        bad++;
                                        Core.Log("FAIL", "ORDER 小分类居然被拖进了别的大类");
                                    }

                                    // 小分类行也走一遍真实鼠标路径
                                    Rebuild();
                                    List<string> subsM = CurrentSubs(catX);
                                    int m1 = subsM.Count >= 2 ? body.RowIndexOfSub(catX, subsM[0]) : -1;
                                    int m2 = subsM.Count >= 2 ? body.RowIndexOfSub(catX, subsM[subsM.Count - 1]) : -1;
                                    if (m1 < 0 || m2 < 0) Core.Log("WARN", "ORDER 找不到两个小分类标题行，跳过小分类鼠标路径用例");
                                    else
                                    {
                                        string stQ;
                                        body.SimulateRealDrag(m2, m1, 1, out stQ);
                                        Core.Log("INFO", "ORDER 小分类鼠标路径 " + stQ);
                                        if (!stQ.Contains("pressArm=True")) { bad++; Core.Log("FAIL", "ORDER 小分类行按下去没有拖动资格：" + stQ); }
                                        else if (!stQ.Contains("kind=3")) { bad++; Core.Log("FAIL", "ORDER 小分类行起拖的 kind 不是 3：" + stQ); }
                                        else if (!stQ.Contains("dropMode=1")) { bad++; Core.Log("FAIL", "ORDER 小分类行落点没解析出来：" + stQ); }
                                        else if (!stQ.Contains("pv=" + body.RowCount + "/" + body.RowCount))
                                        { bad++; Core.Log("FAIL", "ORDER 小分类拖动预览没画出来：" + stQ); }
                                        else Core.Log("INFO", "ORDER 小分类行真实鼠标路径全程走通（含拖动预览）");
                                    }

                                    // 「我的分支」不在默认大类表里，拖它要先登记 —— 这条保证它也能拖到别的大类前面
                                    Rebuild();
                                    int rMine = FindCatRow("我的分支");
                                    List<string> catsNow = CatPrefs.Cats();
                                    int rFirst = catsNow.Count > 0 ? FindCatRow(catsNow[0]) : -1;
                                    if (rMine < 0 || rFirst < 0) Core.Log("WARN", "ORDER 树里没有「我的分支」，跳过特殊大类用例");
                                    else
                                    {
                                        string firstCat = catsNow[0];
                                        if (!body.ApplyDrop("我的分支", 2, rFirst, 1)) { bad++; Core.Log("FAIL", "ORDER 「我的分支」拖到「" + firstCat + "」前面没执行"); }
                                        else
                                        {
                                            List<string> after3 = CatPrefs.Cats();
                                            int im = after3.IndexOf("我的分支"), i0 = after3.IndexOf(firstCat);
                                            if (im < 0 || i0 < 0 || im > i0) { bad++; Core.Log("FAIL", "ORDER 「我的分支」没挪到最前：mine=" + im + "、" + firstCat + "=" + i0); }
                                            else Core.Log("INFO", "ORDER 「我的分支」拖到「" + firstCat + "」前面生效");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (prefRaw != null) File.WriteAllBytes(prefFile, Convert.FromBase64String(prefRaw));
                            else if (File.Exists(prefFile)) File.Delete(prefFile);
                            CatPrefs.Reload();
                            Rebuild();
                        }
                        catch (Exception ex) { Core.Log("WARN", "ORDER 还原 方案树分类.ini 失败: " + ex.Message); }
                    }

                    // 展开状态下把大类拖到别的分类的内容行上：必须自动归到所属大类再落位
                    // （用户实测：树展开后满屏内容行，旧版松在内容行上无声取消）
                    int tk = FindCatRow("人物");
                    if (tk < 0) Core.Log("WARN", "CATDRAG 树里没有「人物」，跳过内容行落点用例");
                    else
                    {
                        SetFolded("人物", false);
                        Rebuild();
                        // 人物的小分类（一代/二代/其他人物）可能整段收起，内容行可能全是小标题行 ——
                        // 落点解析对小标题行和位置行一视同仁（都向上归到大类），所以随便挑一行非大类行就够
                        int contentRow = (tk + 1 < body.RowCount && !body.RowIsCat(tk + 1)) ? tk + 1 : -1;
                        if (contentRow < 0) Core.Log("WARN", "CATDRAG 「人物」展开后没有内容行，跳过");
                        else
                        {
                            List<string> beforeC = CatPrefs.Cats();
                            string src = beforeC[0] == "人物" ? beforeC[1] : beforeC[0];
                            int fromRow = FindCatRow(src);
                            if (fromRow < 0) { bad++; Core.Log("FAIL", "CATDRAG 树里找不到源大类「" + src + "」"); }
                            else
                            {
                                string stC;
                                body.SimulateRealDrag(fromRow, contentRow, 2, out stC);
                                Core.Log("INFO", "CATDRAG 内容行路径 " + stC);
                                List<string> afterC = CatPrefs.Cats();
                                int xa = afterC.IndexOf(src), xb = afterC.IndexOf("人物");
                                if (xb < 0) { bad++; Core.Log("FAIL", "CATDRAG 内容行落点后找不到「人物」"); }
                                // 大类的地盘按整块对半分：落点在内容行的上/下半决定它前面 / 后面 ——
                                // 只要紧贴着「人物」就是归对了大类
                                else if (xa != xb + 1 && xa != xb - 1)
                                { bad++; Core.Log("FAIL", "CATDRAG 松在内容行上没归到大类：源=" + src + "(" + xa + ") 人物(" + xb + ")"); }
                                else Core.Log("INFO", "CATDRAG 松在「人物」的内容行上 → 自动归到「人物」旁边，顺序生效");

                                // 「本来就在」用例：先把 src 挪到人物后面，紧接着再挪一次 ——
                                // 第二次必须明确返回且顺序不变（用户实测场景：拖紧邻邻居看起来像「没反应」）
                                bool m1 = MoveCatTo(src, "人物", true);
                                List<string> snapB = CatPrefs.Cats();
                                bool m2 = MoveCatTo(src, "人物", true);
                                List<string> snapA = CatPrefs.Cats();
                                bool same = snapB.Count == snapA.Count;
                                if (same)
                                    for (int i = 0; i < snapB.Count; i++)
                                        if (snapB[i] != snapA[i]) { same = false; break; }
                                if (m1 && m2 && same) Core.Log("INFO", "CATDRAG 「本来就在」用例：重复挪到同一位置 → 明确提示没挪，顺序不变");
                                else { bad++; Core.Log("FAIL", "CATDRAG 「本来就在」用例异常 m1=" + m1 + " m2=" + m2 + " 顺序变了=" + !same); }

                                // 「拖紧邻邻居 = 对调」用例：人物在上、近战紧贴其下，把人物拖到近战行的上半
                                // —— 解析成「移到近战前面」= 原地不动，必须自动翻到后面 = 两者对调
                                Rebuild();
                                int qa = FindCatRow("人物"), qb = FindCatRow("近战");
                                if (qa >= 0 && qb >= 0)
                                {
                                string stS;
                                body.SimulateRealDrag(qa, qb, 1, out stS);
                                Core.Log("INFO", "CATDRAG 邻居对调路径 " + stS);
                                if (!stS.Contains("pv=" + body.RowCount + "/" + body.RowCount))
                                { bad++; Core.Log("FAIL", "CATDRAG 拖动预览行数不对（应与原树行数一致）：" + stS); }
                                List<string> afterS = CatPrefs.Cats();
                                    if (afterS.IndexOf("近战") == afterS.IndexOf("人物") - 1)
                                        Core.Log("INFO", "CATDRAG 拖紧邻邻居 → 自动对调位置");
                                    else { bad++; Core.Log("FAIL", "CATDRAG 拖紧邻邻居没对调：人物=" + afterS.IndexOf("人物") + " 近战=" + afterS.IndexOf("近战")); }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                bad++;
                Core.Log("FAIL", "BRANCH 自检抛异常：" + ex.Message);
            }
            finally
            {
                try { MyBranch.Delete(ch); MyBranch.Delete(pa); MyBranch.Delete(s1); MyBranch.Delete(s2); } catch { }
                // 大类顺序恢复原样：逐个挪回备份里的位置
                try
                {
                    List<string> cats = CatPrefs.Cats();
                    for (int i = 0; i < catBackup.Count && i < cats.Count; i++)
                    {
                        if (cats[i] == catBackup[i]) continue;
                        CatPrefs.MoveCatTo(catBackup[i], cats[i], false);
                        cats = CatPrefs.Cats();
                    }
                }
                catch { }
                try { Rebuild(); } catch { }
                // 折叠状态按测试前恢复，并落盘（SetFolded 已经把测试期间的展开写进文件了）
                try
                {
                    folded.Clear();
                    foreach (string f in foldBackup) folded.Add(f);
                    SaveFold();
                    BuildRows();
                }
                catch { }
            }
            return bad;
        }

        public void SetFolded(string cat, bool on)
        {
            bool ch = on ? folded.Add(cat) : folded.Remove(cat);
            BuildRows();
            if (ch) SaveFold();
        }

        internal static string SubKey(string cat, string sub) { return cat + "\u0001" + sub; }

        public bool IsSubFolded(string cat, string sub) { return folded.Contains(SubKey(cat, sub)); }

        public void SetSubFolded(string cat, string sub, bool on)
        {
            bool ch = on ? folded.Add(SubKey(cat, sub)) : folded.Remove(SubKey(cat, sub));
            BuildRows();
            if (ch) SaveFold();
        }

        // ---------------- 折叠状态的记忆 ----------------
        // 树一屏放不下 70 多个位置。默认按小分类收起，先看到一张目录（大类 + 小分类），
        // 点到哪一层再展开 —— 下次打开还停在上次的展开状态，不用重新找。

        private bool foldLoaded;

        private static string FoldFile
        {
            get { return string.IsNullOrEmpty(Core.PkgDir) ? "" : Path.Combine(Core.PkgDir, "方案树折叠.ini"); }
        }

        private void LoadFold()
        {
            if (foldLoaded) return;
            foldLoaded = true;
            folded.Clear();
            bool had = false;
            try
            {
                string f = FoldFile;
                if (f.Length > 0 && File.Exists(f))
                {
                    had = true;
                    foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        string s = raw.TrimEnd();
                        if (s.Length == 0 || s[0] == '#') continue;
                        if (s.StartsWith("cat=", StringComparison.Ordinal))
                        {
                            string v = s.Substring(4).Trim();
                            if (v.Length > 0) folded.Add(v);
                        }
                        else if (s.StartsWith("sub=", StringComparison.Ordinal))
                        {
                            string[] p = s.Substring(4).Split('\t');
                            if (p.Length >= 2 && p[0].Trim().Length > 0) folded.Add(SubKey(p[0].Trim(), p[1].Trim()));
                        }
                    }
                }
            }
            catch { }
            if (!had) FoldAllSubs();          // 第一次打开：只留目录，别一上来就铺 90 行
        }

        /// <summary>所有小分类都收起（大类标题还留着），文件里也记下来。</summary>
        private void FoldAllSubs()
        {
            folded.Clear();
            Dictionary<string, bool> keys = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (Slot s in slots)
            {
                if (string.IsNullOrEmpty(s.Sub)) continue;
                keys[SubKey(s.Cat, s.Sub)] = true;
            }
            foreach (string k in keys.Keys) folded.Add(k);
        }

        private void SaveFold()
        {
            string f = FoldFile;
            if (f.Length == 0) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 方案树折叠状态：cat=大类 / sub=大类<TAB>小分类（本工具自己写，手改也行）");
                List<string> cats = new List<string>();
                List<string> subs = new List<string>();
                foreach (string k in folded)
                {
                    int us = k.IndexOf('\u0001');
                    if (us < 0) cats.Add(k); else subs.Add(k);
                }
                cats.Sort(StringComparer.Ordinal);
                subs.Sort(StringComparer.Ordinal);
                foreach (string c in cats) sb.AppendLine("cat=" + c);
                foreach (string k in subs) sb.AppendLine("sub=" + k.Substring(0, k.IndexOf('\u0001')) + "\t" + k.Substring(k.IndexOf('\u0001') + 1));
                File.WriteAllText(f, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Core.Log("WARN", "折叠状态保存失败: " + ex.Message); }
        }

        /// <summary>右键「全部收起」：只留大类标题，一屏能看完整棵树。</summary>
        internal void FoldEverything()
        {
            folded.Clear();
            foreach (string c in SlotIndex.CatOrder(slots)) folded.Add(c);
            BuildRows();
            SaveFold();
            lblFoot.Text = Lang.S("全部收起 · 点大类或小分类那行就展开");
        }

        /// <summary>右键「全部展开」。</summary>
        internal void UnfoldEverything()
        {
            folded.Clear();
            BuildRows();
            SaveFold();
            lblFoot.Text = Lang.S("全部展开");
        }

        /// <summary>
        /// 选中某个位置时，把它所在的大类 / 小分类打开 —— 否则跳过去以后
        /// 树上看不到自己选中的那一行，像点了个空。
        /// </summary>
        private void RevealFold(string key)
        {
            if (string.IsNullOrEmpty(key) || key[0] == '#') return;
            Slot hit = null;
            foreach (Slot s in slots) if (s.Key == key) { hit = s; break; }
            if (hit == null) return;
            bool ch = folded.Remove(hit.Cat);
            if (!string.IsNullOrEmpty(hit.Sub)) ch |= folded.Remove(SubKey(hit.Cat, hit.Sub));
            if (!ch) return;
            BuildRows();
            SaveFold();
        }

        // ---------------------------------------------------------- 右侧

        private Slot Current
        {
            get
            {
                foreach (Slot s in slots) if (s.Key == selKey) return s;
                return null;
            }
        }

        /// <summary>右侧列表当前打开的是哪个分支；不在分支页上返回 null。建子分支的菜单入口要用。</summary>
        internal Slot CurrentBranch
        {
            get { return IsBranchSlot(Current) ? Current : null; }
        }

        /// <summary>鼠标 x 落在第几列（客户区坐标）。列宽是自适应的，没有横向滚动。</summary>
        private static int ColAt(ListView v, int x)
        {
            int acc = 0;
            for (int i = 0; i < v.Columns.Count; i++)
            {
                acc += v.Columns[i].Width;
                if (x < acc) return i;
            }
            return -1;
        }

        /// <summary>点「开关」列：把这个 MOD 在 addonlist 里的 1/0 翻过来，立刻写进文件。</summary>
        private void ToggleSwitch(ModGroup g)
        {
            if (g == null) return;
            List<ModItem> ins = new List<ModItem>();
            foreach (ModItem p in g.Parts) if (p.FromGame || p.Installed) ins.Add(p);
            if (ins.Count == 0)
            {
                MessageBox.Show("这个 MOD 只在只读库里，没有进游戏的 addons 目录，所以没有开关。\r\n\r\n"
                    + "要让它在游戏里生效，得先把它的 vpk 放进：\r\n"
                    + "  <游戏目录>\\left4dead2\\addons\r\n\r\n"
                    + "放好后点左栏「刷新游戏现状」，这里就会显示 开 / 关。\r\n"
                    + "（本地版只改开关，不会替你复制文件）",
                    "还没进游戏目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool allOn = true;
            foreach (ModItem p in ins) if (!p.AddonOn) { allOn = false; break; }
            bool target = !allOn;
            Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem p in ins) st[p.Name] = target;
            if (!mf.ApplySwitches(st)) return;
            if (target) mf.CheckGroupDefault(g); else mf.UncheckGroup(g);   // 勾选跟着走，否则树里还挂着「选中的那套」
            RefreshSwitchCells();
            lblFoot.Text = target ? "已开启（写进 addonlist，进游戏生效）" : "已关闭（写进 addonlist，进游戏生效）";
        }

        /// <summary>给右键菜单用：把这一组 MOD 的开关翻过来（和点「开关 ⇄」列同一个动作）。</summary>
        internal void ToggleFromMenu(ModGroup g) { ToggleSwitch(g); }

        /// <summary>右边那半重画一次（搜索词变了、外部改了勾选之后调）。</summary>
        internal void RefreshPane() { FillDetail(); }

        /// <summary>只重画受影响的两列文字和底色，不重建列表 —— 点开关时滚动位置不跳。</summary>
        private void RefreshSwitchCells()
        {
            if (lv != null)
                foreach (ListViewItem li in lv.Items)
                {
                    ModGroup g = li.Tag as ModGroup;
                    if (g != null) ApplyStateCells(li, g);
                }
            if (lvExtra != null)
                foreach (ListViewItem li in lvExtra.Items)
                {
                    ModGroup g = li.Tag as ModGroup;
                    if (g != null) ApplyStateCells(li, g);
                }
        }

        private void FillDetail()
        {
            PrepareRecommend();
            // 搜索优先：搜了词就只看结果（搜索只剩这一条路可走）
            string kw = mf.SearchText;
            if (kw.Length > 0) { ShowSearch(kw); return; }
            if (selKey == "#unmatched")
            {
                ShowUnmatched();
                return;
            }
            if (selKey == "#mine+new")
            {
                ShowNewBranch();
                return;
            }
            lv.Visible = true;
            // 骨架屏要跟着这次可见状态重排一次，否则 PrepareRecommend 里那次布局还按"列表不可见"算，推荐区被藏起来了
            if (recPanel != null && recKey.Length > 0) LayoutSelf();
            Slot s = Current;
            // 分支页也给「指定位置…」：放进分支的包多半是认不出位置的，
            // 在这里选中它就能直接告诉程序「它替换的是什么」，不用再绕回未归类页。
            lv.BeginUpdate();
            lvExtra.BeginUpdate();
            try
            {
                tipRow.Hide(lv);            // 列表整批换掉了，旧提示不能再挂在那儿
                tipShownAt = -1;
                lv.Items.Clear();
                lvExtra.Items.Clear();
                if (s == null)
                {
                    lblTitle.Text = Lang.S("选一个位置");
                    lblCur.Text = Lang.S("左边点一个武器 / 药品 / 人物，这里列它的 MOD");
                    lblExtraTitle.Visible = false;
                    lvExtra.Visible = false;
                    if (peek != null) peek.Visible = false;
                    return;
                }
                int n = CheckedGroups(s);
                // 「允许多选」只对单选的常规位置出现（地图/喷漆那种「一堆」天然就是多选）
                if (chkMulti != null)
                {
                    applyingMulti = true;
                    chkMulti.Visible = !s.Pile && !s.Empty && s.Cat != "平台";   // 平台恒多选，不给这个开关
                    chkMulti.Checked = IsMulti(s);
                    applyingMulti = false;
                }
                lblTitle.Text = s.Name;
                if (s.Empty)
                {
                    // 空位：不是「忘了选」，是这个位置压根还没有包 —— 话说清楚，别让人以为坏了
                    lblCur.Text = Lang.S("这个位置还没有 MOD · 游戏里就是原版");
                }
                else if (s.Cat == "平台")
                {
                    lblCur.Text = n == 0
                        ? "平台可以同时开好几个 · 共 " + s.Items.Count + " 个"
                        : "已开 " + n + " 个平台 · 多个平台能一起用（它们是底座，不抢位置）";
                }
                else if (s.Pile)
                {
                    // 一堆：能同时开好几个，「叠了 N 件」这种话在这里是正常的，不是警告
                    lblCur.Text = n == 0
                        ? "这一堆还没勾 · 共 " + s.Items.Count + " 个，能同时开好几个"
                        : "这一堆已勾 " + n + " 个 · 会一起装，冲不冲突自己确认";
                }
                else
                {
                    // 「当前」= 游戏里真正开着的（addonlist=1 且文件已经在 addons 里），
                    // 不是「勾选了哪个」。两者经常不一样：勾着的可能还没装，开着的可能是上次装的。
                    // 以前这行显示勾选，结果出现「当前：X」但 X 那行写着「关闭」的自相矛盾。
                    List<ModGroup> live = new List<ModGroup>();
                    foreach (ModGroup g in s.Items)
                        foreach (ModItem p in g.Parts)
                            if ((p.Installed || p.FromGame) && p.AddonOn) { live.Add(g); break; }
                    if (live.Count == 0) lblCur.Text = Lang.S("游戏里还是原版（这个位置没有开着的 MOD）");
                    else if (live.Count == 1)
                    {
                        string txt = Lang.F("当前：{0}", live[0].Show);
                        ModGroup pick = ChosenOf(s);
                        if (pick != null && pick != live[0]) txt += "　（已选：" + pick.Show + "，未启用）";
                        lblCur.Text = txt;
                    }
                    else
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        sb.Append("同一位置开着 ").Append(live.Count).Append(" 个：");
                        for (int i = 0; i < live.Count && i < 3; i++)
                            sb.Append(i > 0 ? "、" : "").Append(live[i].Show);
                        // 开了「允许多选」的位置是有意叠着的，别报警；没开的才提醒会互相覆盖
                        sb.Append(IsMulti(s) ? "　（多选模式：这是你要的，注意模型叠放顺序）" : "　（会互相覆盖，建议只留一个）");
                        lblCur.Text = sb.ToString();
                    }
                }

                foreach (ModGroup g in s.Items)
                {
                    bool on = mf.CountChecked(g) > 0;
                    ListViewItem li = new ListViewItem(g.Show);
                    li.SubItems.Add(KindsOf(g));
                    li.SubItems.Add(Core.Human(g.Size));
                    AddStateCells(li, g);
                    li.StateImageIndex = on ? 1 : 0;
                    if (on) li.Font = Theme.UIBold;
                    li.Tag = g;
                    if (g.IsSet)
                    {
                        // 组合装：改了什么列蓝字标出来，不用悬停也知道这行能挑部件
                        li.UseItemStyleForSubItems = false;
                        li.SubItems[1].Text = KindsOf(g) + "·组合装" + g.Parts.Count + "件";
                        li.SubItems[1].ForeColor = Theme.Info;
                        li.SubItems[1].Font = Theme.UIBold;
                        li.ToolTipText = FullNameOf(g) + "\r\n\r\n" + PartsTip(g);
                    }
                    AppendPluginTip(li, g);
                    lv.Items.Add(li);
                }

                if (s.Pile)
                {
                    lblFoot.Text = s.Items.Count + " 个 · 点一行 = 开 / 关（不是替换，能同时开好几个）"
                                 + (s.Key.StartsWith("#mine:", StringComparison.Ordinal)
                                    ? " · 选中一行可「指定位置」" : "");
                }
                else if (s.Empty)
                {
                    // 窄窗口下这一行只剩一百多像素，长句子必被省略号切 —— 怎么进货交给悬停提示（UpdateTip）
                    lblFoot.Text = Lang.S("这个位置还没有 MOD · 游戏里仍是原版");
                }
                else lblFoot.Text = s.Items.Count == 0
                    ? "这个位置还没有换模型的包"
                    : Lang.F("{0} 个模型候选 · 点一个就换掉原来的", s.Items.Count)
                      + (HasSet(s) ? " · 组合装双击可挑部件" : "")
                      + " · 「开关 ⇄」列点一下=把这个 MOD 开 / 关";


            }
            finally { lv.EndUpdate(); }
            peekIdx = -2;                 // 列表整批换过了，之前的悬停行号作废
            UpdatePeek(null);             // 先回落到「当前启用的那个」，一进这个位置就有图看
            LayoutSelf();
            LayoutCols();
        }

        /// <summary>
        /// 刷新候选区上方的封面预览。g 为 null 时回落到这个位置当前启用的那套，
        /// 再没有就用第一个候选 —— 进来先看到一张图，比看到一块空白有用。
        /// </summary>
        private void UpdatePeek(ModGroup g)
        {
            if (peek == null) return;
            Slot s = Current;
            bool unmatchedPage = selKey == "#unmatched";
            if (s == null && !unmatchedPage)
            {
                peek.Visible = false;
                return;
            }
            peek.Visible = lv.Visible;

            if (g == null && !unmatchedPage)
            {
                g = ChosenOf(s);
                if (g == null && s.Items.Count > 0) g = s.Items[0];
            }
            if (g == null)
            {
                // 未归类页没有「当前位置」这回事，只提示划过一行看详情
                peek.Clear(unmatchedPage ? Lang.S("鼠标划过一行，看这条 MOD 的详情")
                           : (s.Empty ? Lang.S("这个位置还没有 MOD") : Lang.S("没有可预览的 MOD")));
                return;
            }

            // 组合装取主体那一片；游戏原版没有包，也就没有图
            ModItem lead = null;
            foreach (ModItem p in g.Parts)
                if (p.FromGame || p.Installed) { lead = p; break; }
            if (lead == null && g.Parts.Count > 0) lead = g.Parts[0];

            string imgPath = null;
            if (lead != null && string.IsNullOrEmpty(lead.Container) && lead.FullPath != null)
            {
                try { imgPath = Cover.For(lead.FullPath); } catch { imgPath = null; }
            }

            string src;
            if (lead == null) src = "";
            else if (lead.FromWorkshop) src = Lang.S("来源：Steam 创意工坊订阅");
            else if (lead.FromGame) src = Lang.S("来源：游戏 addons 里已有");
            else if (!string.IsNullOrEmpty(lead.Container)) src = Lang.S("来源：压缩包 ") + System.IO.Path.GetFileName(lead.Container);
            else src = Lang.S("来源：") + System.IO.Path.GetFileName(lead.FullPath ?? "");

            string info = "";
            if (lead != null)
            {
                if (!string.IsNullOrEmpty(lead.Subject)) info += Lang.S("改了什么：") + lead.Subject + "\n\n";
                if (!string.IsNullOrEmpty(lead.AddonTitle)) info += Lang.S("游戏里叫：") + lead.AddonTitle + "\n\n";
                if (!string.IsNullOrEmpty(lead.AddonAuthor)) info += Lang.S("作者：") + lead.AddonAuthor + "\n\n";
                info += (lead.AddonOn ? Lang.S("状态：已启用") : Lang.S("状态：已关闭"));
                if (lead.FromWorkshop) info += "\n\n" + Lang.S("这是 Steam 创意工坊订阅的内容，程序不删也不搬；去掉它请在创意工坊取消订阅");
                else if (lead.FromGame) info += "\n\n" + Lang.S("这份就在游戏目录里，删文件得自己去资源管理器");
            }

            string meta = KindsOf(g) + " · " + Core.Human(g.Size);
            if (g.Parts.Count > 1) meta += " · " + g.Parts.Count + " 件";
            peek.Show(g.Show, meta, src, imgPath, null, info);
        }

        /// <summary>
        /// 搜索结果页：搜了词就只列名字里带这个词的 MOD，点一行跳回它在树里的位置。
        /// 搜索框搜出来的东西直接铺在树右边，点一行跳回它的位置。
        /// </summary>
        private void ShowSearch(string kw)
        {
            lblExtraTitle.Visible = false;
            lvExtra.Visible = false;
            lv.Visible = true;
            lblTitle.Text = "搜索：" + kw;
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                int n = 0;
                foreach (ModGroup g in Catalog.Groups)
                {
                    if (string.IsNullOrEmpty(g.Name) || !mf.MatchGroup(g, kw)) continue;
                    ListViewItem li = new ListViewItem(g.Show);
                    ModItem mi = g.Main;
                    li.SubItems.Add(g.Cat + (mi == null || string.IsNullOrEmpty(mi.Sub) ? "" : " · " + mi.Sub));
                    li.SubItems.Add(Core.Human(g.Size));
                    AddStateCells(li, g);
                    li.StateImageIndex = mf.CountChecked(g) > 0 ? 1 : 0;
                    li.Tag = g;
                    lv.Items.Add(li);
                    n++;
                }
                lblCur.Text = n + " 个匹配「" + kw + "」的 MOD · 名称 / 替换内容 / 位置都参与搜索 · 点一行跳到他该在的位置";
            }
            finally { lv.EndUpdate(); }
            lblFoot.Text = Lang.S("清空搜索框回到方案树；这一页只找东西，不改勾选。");
            LayoutSelf();
            LayoutCols();
        }

        /// <summary>「＋ 新建分支…」那行：右半边只放说明（真正的输入框在点上那一行时弹）。</summary>
        private void ShowNewBranch()
        {
            lblExtraTitle.Visible = false;
            lvExtra.Visible = false;
            lv.Visible = true;
            lv.Items.Clear();
            lblTitle.Text = Lang.S("我的分支");
            lblCur.Text = Lang.S("程序认不出改的是什么的 MOD，自己拉一组装进去（一组里能同时开好几个）");
            lblFoot.Text = Lang.S("这一行 = 新建顶层分支 · 右键一个已有分支 = 在它下面建子分支");
            LayoutSelf();
            LayoutCols();
        }

        /// <summary>
        /// 未归类页：把没进任何位置的 MOD 全部列出来（一个不漏），
        /// 选中一行点「指定位置…」或直接双击，就打开「设置 MOD 分类」窗口手动归位。
        /// </summary>
        private void ShowUnmatched()
        {
            lblExtraTitle.Visible = false;
            lvExtra.Visible = false;
            if (chkMulti != null) chkMulti.Visible = false;   // 未归类页没有「位置」，多选没意义
            lblTitle.Text = Lang.S("未归类");
            lblCur.Text = unmatched + " 个 MOD 没认出改的是哪件东西";
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                // 内容相似的圈成一伙、排在相邻位置，伙与伙之间不穿插；单干的保持原顺序跟在后面
                int clustered;
                Dictionary<ModGroup, List<ModGroup>> clan = ClusterUnmatched(unmatchedGroups, out clustered);
                HashSet<ModGroup> emitted = new HashSet<ModGroup>();
                foreach (ModGroup g in unmatchedGroups)
                {
                    if (emitted.Contains(g)) continue;
                    List<ModGroup> members;
                    bool inClan = clan.TryGetValue(g, out members);
                    if (inClan)
                    {
                        ListViewItem head = new ListViewItem("同类包 " + members.Count + " 个 · 内容高度相似，挑一个装就行");
                        head.Tag = null;
                        head.ForeColor = Theme.Ink3;
                        head.Font = Theme.UIBold;
                        lv.Items.Add(head);
                    }
                    foreach (ModGroup mg in inClan ? members : new List<ModGroup> { g })
                    {
                        if (emitted.Contains(mg)) continue;
                        emitted.Add(mg);
                        AddUnmatchedRow(mg, inClan);
                    }
                }
            }
            finally { lv.EndUpdate(); }
            lblFoot.Text = Lang.S("不影响装机，照常能勾。按住一行拖到左边的树上松手 = 归到那；或双击打开「设置 MOD 分类」细调。");
            peekIdx = -2;                 // 换了页面，悬停行号作废
            UpdatePeek(null);
            LayoutSelf();
            LayoutCols();
        }

        /// <summary>未归类页的一行。<paramref name="inClan"/>=true 表示上面还有「同类包」伙头，名字前加 └。</summary>
        private void AddUnmatchedRow(ModGroup g, bool inClan)
        {
            bool on = mf.CountChecked(g) > 0;
            ListViewItem li = new ListViewItem((inClan ? "└ " : "") + g.Show);
            li.SubItems.Add(KindsOf(g));
            li.SubItems.Add(Core.Human(g.Size));
            AddStateCells(li, g);
            li.StateImageIndex = on ? 1 : 0;
            if (on) li.Font = Theme.UIBold;
            li.Tag = g;
            li.ToolTipText = FullNameOf(g) + "\r\n\r\n" + PartsTip(g);
            if (g.IsSet)
            {
                li.UseItemStyleForSubItems = false;
                li.SubItems[1].Text = KindsOf(g) + "·组合装" + g.Parts.Count + "件";
                li.SubItems[1].ForeColor = Theme.Info;
                li.SubItems[1].Font = Theme.UIBold;
                li.ToolTipText = FullNameOf(g) + "\r\n\r\n" + PartsTip(g);
            }
            else li.ToolTipText = "按住这行拖到左边的树上松手 = 归到那；双击可打开「设置 MOD 分类」细调";
            // 名字/标题里带着玩家叫法的，直接告诉他「可能是什么」——新人不用先双击进窗口才知道
            string hint = Positions.Hint(g.Name, g.Main != null ? g.Main.AddonTitle : "", 3);
            if (hint.Length > 0) li.ToolTipText += "\r\n\r\n" + hint + "（双击可直接归到第一个）";
            // 识别经过：认不出时也要说清「看过什么、按什么猜」，别让人对着「未归类」干瞪眼
            string trace = g.Main != null ? (g.Main.SubjectTrace ?? "") : "";
            if (trace.Length > 0) li.ToolTipText += "\r\n识别经过：" + trace;
            AppendPluginTip(li, g);
            lv.Items.Add(li);
        }

        private static string KindsOf(ModGroup g)
        {
            ModItem m = g.Main;
            if (m == null) return "";
            string k = m.Kinds;
            if (string.IsNullOrEmpty(k)) k = m.SubjectLevel;
            k = k == null ? "" : k.Replace(",", "·");
            // 带插件组件的包单独标出来：它除了进 addons 还要往 bin\neko\plugins 写 DLL，
            // 不标的话和普通包在列表里长得一模一样，装完「手上空的」根本对不上号。
            if (g.Plugin != null) k = k.Length > 0 ? k + "·含插件" : "含插件";
            return k;
        }

        /// <summary>
        /// 悬停里的插件状态。「含插件」只说明这包带 DLL，带不等于在位：
        /// 文件缺了就是进游戏空手，所以把在 / 缺 / 停用区几个数直接摆出来。
        /// </summary>
        private static void AppendPluginTip(ListViewItem li, ModGroup g)
        {
            PluginPack pk = g == null ? null : g.Plugin;
            if (pk == null) return;
            string gameRoot = Core.Cfg == null || Core.Cfg.GameRoot == null ? "" : Core.Cfg.GameRoot.TrimEnd('\\');
            string park = Work.DisabledPluginDir();
            int here = 0, gone = 0, parked = 0;
            foreach (PluginFile f in pk.Files)
            {
                bool onDisk = false, inPark = false;
                try { onDisk = gameRoot.Length > 0 && File.Exists(Path.Combine(gameRoot, f.Rel)); }
                catch { }
                if (onDisk) { here++; continue; }
                try { inPark = File.Exists(Path.Combine(park, f.Rel)); }
                catch { }
                if (inPark) parked++; else gone++;
            }
            string tip = "插件：共 " + pk.Files.Count + " 个文件，已就位 " + here + " / 缺 " + gone + " / 停用区 " + parked;
            li.ToolTipText = string.IsNullOrEmpty(li.ToolTipText) ? tip : li.ToolTipText + "\r\n\r\n" + tip;
        }

        /// <summary>组合装行的悬停说明：部件一个一个念出来，外加怎么挑。</summary>
        private static string PartsTip(ModGroup g)
        {
            string t = "";
            foreach (ModItem p in g.Parts)
                t += (t.Length > 0 ? "\r\n　+ " : "") + Catalog.CleanName(p.Name)
                     + "（" + p.Role + "）";
            return g.Parts.Count + " 个 vpk 组成：" + t
                 + "\r\n点一下=装默认部件，双击=挑部件（材质 / 颜色）";
        }

        /// <summary>点了一个候选（null = 留原版）：清掉这个位置的旧件，再勾新件。</summary>
        private void Choose(ModGroup g)
        {
            Slot s = Current;
            if (s == null) return;
            // 再点一次已经选中的那一套 = 把它关掉，这个位置回到「没选」。
            // 以前必须点「留原版」那一行才能清空，用户嫌多一个选项。
            if (g != null && g == ChosenOf(s))
            {
                foreach (ModGroup o in s.Items) mf.UncheckGroup(o);
                Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (ModGroup o in s.Items)
                    foreach (ModItem p in o.Parts)
                        if (p.FromGame || p.Installed) st[p.Name] = false;
                if (st.Count > 0) mf.ApplySwitches(st);
                if (lblFoot != null) lblFoot.Text = "「" + s.Name + "」已清空：这里没有开着的 MOD，游戏里就是原版";
                FillDetail();
                return;
            }
            ChooseInSlot(s, g);
            FillDetail();
        }

        /// <summary>
        /// 给某个位置换候选（台面共用）：先清空这个位置，再勾这一个。
        /// 本地版里点一下就直接生效 —— 把这个位置开着的换成这一个，同位置其它的写成 0。
        /// 只写 addonlist，不复制、不移动、不删除任何文件；点错了再点回来就行。
        /// </summary>
        internal void ChooseInSlot(Slot s, ModGroup g)
        {
            if (s == null) return;
            if (IsMulti(s)) { ToggleMulti(s, g); return; }   // 多选位置：只翻这一套，不清别的
            foreach (ModGroup o in s.Items) mf.UncheckGroup(o);
            if (g != null) mf.CheckGroupDefault(g);
            mf.AfterTreePick();

            if (!Core.IsLocal || g == null) return;
            // 只动「已经在这个位置的游戏目录里」的 vpk：还没进游戏的（理论上本地版不会出现）
            // 没法凭空启用，跳过，别写出一个指向不存在文件的开关。
            Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ModGroup o in s.Items)
            {
                foreach (ModItem p in o.Parts)
                {
                    if (!(p.FromGame || p.Installed)) continue;
                    // 选中的那套：只开「勾着的部件」；套装里没勾的部件要写 0，
                    // 否则在「挑部件」里关掉一个，游戏里那个还是开着（实测踩到）。
                    st[p.Name] = (o == g) && mf.IsChecked(p.Key);
                }
            }
            if (st.Count == 0)
            {
                // 一条都没算出来：说明这个位置的 vpk 既没被标成「已在游戏里」也没标成「库里来的」，
                // 写开关无从下手。记一笔，排查时不用猜（用户看到的就是「点了没反应」）。
                Core.Log("WARN", "点选未写入：位置「" + s.Name + "」的候选没有已装标记（FromGame/Installed 全 false）");
                return;
            }
            if (!mf.ApplySwitches(st)) return;
            bool running = false;
            try { running = System.Diagnostics.Process.GetProcessesByName("left4dead2").Length > 0; } catch { }
            if (lblFoot != null)
                lblFoot.Text = "已替换「" + s.Name + "」为 " + g.Name + "（只写开关，没动文件）"
                    + (running ? "　· 游戏正在运行：下次进游戏生效，游戏退出时可能把这个文件覆盖回去" : "");
            Core.Log("OK", "点选即生效：" + s.Name + " → " + g.Name + "（同位置其它写成 0）");
        }

        /// <summary>这个位置的候选里有没有组合装（模型+材质+颜色的套装）。</summary>
        private static bool HasSet(Slot s)
        {
            foreach (ModGroup g in s.Items) if (g.IsSet) return true;
            return false;
        }

        /// <summary>右键菜单里的「用这个替换」：拿这一行的 Tag 去换，位置用当前选中的那个。</summary>
        internal void PickRow(ListViewItem li)
        {
            if (li == null) return;
            Choose(li.Tag as ModGroup);
        }

        /// <summary>自测用：方案树右边那张表的右键菜单，一路念出来。</summary>
        internal string TreeMenuText()
        {
            if (lv.ContextMenuStrip == null) return "（没有菜单）";
            string all = "";
            foreach (ToolStripItem it in lv.ContextMenuStrip.Items)
                all += (it is ToolStripSeparator ? " | " : it.Text + " · ");
            return all;
        }

        /// <summary>自测用：直接选某个槽位里的第 i 个候选。</summary>
        public string ChooseAt(int slotIdx, int itemIdx)
        {
            Slot s = SlotAt(slotIdx);
            if (s == null) return "没有槽位";
            selKey = s.Key;
            if (itemIdx < 0) { Choose(null); return "留原版"; }
            if (itemIdx >= s.Items.Count) return "候选越界";
            ModGroup g = s.Items[itemIdx];
            Choose(g);
            return g.Name;
        }

        // ---------------------------------------------------------- 布局

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (scroll == null) return;
            using (Pen p = new Pen(Theme.Line))
                e.Graphics.DrawLine(p, scroll.Right, 0, scroll.Right, Height);
        }

        /// <summary>分栏拖条：6px 宽的一条，拖它改左边那栏的宽度，松手写配置。</summary>
        private Panel MakeSplit()
        {
            Panel p = new Panel();
            p.Width = Dpi.S(6);
            p.BackColor = Theme.LineSoft;
            p.Cursor = Cursors.SizeWE;
            p.MouseDown += delegate(object s, MouseEventArgs e2)
            {
                if (e2.Button != MouseButtons.Left) return;
                splitMode = p == splitA ? 'A' : 'B';
                splitStartCursor = Cursor.Position.X;
                splitStartVal = splitMode == 'A' ? (treeWpx > 0 ? treeWpx : curTreeW) : (detailWpx > 0 ? detailWpx : curDetailW);
                p.Capture = true;
            };
            p.MouseMove += delegate(object s, MouseEventArgs e2)
            {
                if (!p.Capture || splitMode == 0) return;
                // 基准必须固定成「按下那一刻的宽度」：以前这里读的是已经被改过的 treeWpx/detailWpx，
                // 每动一次就把累计位移再加一遍 —— 越拖越快，轻轻一划就冲过头。
                int dx = Cursor.Position.X - splitStartCursor;
                // 按 8px 一档推进：鼠标一像素的抖动不再触发整窗重排，拖起来稳，重排次数也少八倍。
                dx = (int)Math.Round(dx / 8.0) * 8;
                int v;
                if (splitMode == 'A')
                {
                    v = Math.Max(Dpi.S(200), Math.Min(ClientSize.Width / 2, splitStartVal + dx));
                    v = (v / 8) * 8;
                    if (v == treeWpx) return;
                    treeWpx = v;
                }
                else
                {
                    v = Math.Max(Dpi.S(260), splitStartVal - dx);
                    v = Math.Min(v, Math.Max(Dpi.S(300), ClientSize.Width - curTreeW - Dpi.S(360)));
                    v = (v / 8) * 8;
                    if (v == detailWpx) return;
                    detailWpx = v;
                }
                LayoutSelf();
            };
            p.MouseUp += delegate(object s, MouseEventArgs e2)
            {
                if (splitMode == 0) return;
                splitMode = (char)0;
                p.Capture = false;
                Core.Cfg.TreeW = treeWpx > 0 ? Dpi.Un(treeWpx) : 0;
                Core.Cfg.DetailW = detailWpx > 0 ? Dpi.Un(detailWpx) : 0;
                Core.SaveConfig();
            };
            return p;
        }
        /// <summary>拖拽期间的滚轮：OLE 拖动时消息循环不经过 Application 的过滤器，改用低级鼠标钩子。</summary>
        private Native.HookProc wheelHookProc;
        private IntPtr wheelHook;

        internal void StartWheelHook()
        {
            if (wheelHook != IntPtr.Zero) return;
            wheelHookProc = WheelHookProc;                  // 必须留引用，否则委托被回收、钩子直接失效
            wheelHook = Native.SetWindowsHookEx(14, wheelHookProc, IntPtr.Zero, 0);   // WH_MOUSE_LL
        }

        internal void StopWheelHook()
        {
            if (wheelHook == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(wheelHook);
            wheelHook = IntPtr.Zero;
            wheelHookProc = null;
        }

        private IntPtr WheelHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL && body != null && body.ExtDragging)
            {
                if (RectangleToScreen(ClientRectangle).Contains(Cursor.Position))
                {
                    // MSLLHOOKSTRUCT: pt(x,y) 占前 8 字节，mouseData 在 +8，高 16 位是滚动量
                    int delta = unchecked((short)((System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF));
                    body.WheelScroll(delta);
                    return (IntPtr)1;                        // 吞掉：别让被拖的列表跟着滚
                }
            }
            return Native.CallNextHookEx(wheelHook, nCode, wParam, lParam);
        }

        private void LayoutSelf()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            if (W <= 0 || H <= 0) return;
            if (!widthsLoaded)
            {
                widthsLoaded = true;
                treeWpx = Core.Cfg.TreeW > 0 ? Dpi.S(Core.Cfg.TreeW) : 0;
                detailWpx = Core.Cfg.DetailW > 0 ? Dpi.S(Core.Cfg.DetailW) : 0;
            }
            // 三层之后每行多缩了一级，树比以前宽一点，否则物品名和「选了哪个」会挤在一起
            // 三列：方案树 / 该类候选 / 悬停详情。树不用占一半，右边两列才是干活的
            int treeW = treeWpx > 0 ? treeWpx : Math.Min(Dpi.S(340), Math.Max(Dpi.S(240), W * 26 / 100));
            treeW = Math.Min(Math.Max(treeW, Dpi.S(200)), Math.Max(Dpi.S(220), W / 2));
            curTreeW = treeW;
            if (scroll != null) scroll.SetBounds(0, 0, treeW, H);
            if (splitA != null) splitA.SetBounds(Math.Max(0, treeW - Dpi.S(3)), 0, Dpi.S(6), H);
            // Dock 让树体宽度自动跟着可视区走（含竖直滚动条出现/消失），不然右边「n 个」会被裁掉
            if (scroll != null) scroll.Relayout();

            int x = treeW + 1;
            int rightW = Math.Max(120, W - x);
            // 第三列（详情）：窗口够宽才开；不够宽就退回两列，候选吃满
            int detailW = 0;
            if (rightW >= Dpi.S(680))
            {
                detailW = detailWpx > 0 ? detailWpx : Math.Min(Dpi.S(480), Math.Max(Dpi.S(380), rightW * 44 / 100));
                detailW = Math.Min(detailW, Math.Max(Dpi.S(300), rightW - Dpi.S(360)));
                if (detailW < Dpi.S(260)) detailW = 0;
            }
            curDetailW = detailW;
            int candW = rightW - (detailW > 0 ? detailW + Theme.G : 0);
            int pad = Theme.CardPad;
            int actH = Theme.H;
            int by = H - actH - Theme.S;                   // 底部动作行的顶边
            int top = 42;                                  // 头部并成一行后，列表跟着上移
            // 两列时封面条夹在标题与表头之间；三列时它整块搬去右侧详情栏
            int peekH = Dpi.S(100);
            int peekTop = 60;
            if (detailW == 0) top = peekTop + peekH + Theme.S;
            int bottom = by - Theme.S;                     // 列表底边，与动作行之间留一个步长
            const int headH = 26;

            // 右半分栏：够宽时「候选」和「可叠加的扩展」并排。
            // 以前是上下按 56% 切，候选只有三行时上半留一大片空白、下半的扩展还被压扁；
            // 并排之后两栏各自铺满可用高度，一边有几行都不影响另一边。
            // 判据用条目数而不是 Visible：Visible 是本方法自己在改的，拿它当输入会互相打架
            // （上一次隐藏了 → 这次走窄窗分支 → 又把隐藏的那块摆出来 → 下一轮变成可见）。
            bool hasEx = lvExtra != null && lvExtra.Items.Count > 0;
            bool side = hasEx && candW >= Dpi.S(Theme.SideMin);
            int mainX = x, mainW = candW;
            int exX = 0, exW = 0;
            if (side)
            {
                // 扩展栏定宽，但最多占右半的三分之一：窗口再宽也不让它抢候选的地盘
                exW = Math.Min(Dpi.S(Theme.ExtraW), candW / 3);
                mainW = candW - exW - Theme.G;
                exX = x + mainW + Theme.G;
            }

            // 头部两行并一行：标题在左、当前在同一行右侧，开关贴这一行右端（少一行、没有空洞）
            if (lblTitle != null) lblTitle.SetBounds(mainX + pad, 10, Dpi.S(96), 22);
            if (lblCur != null) lblCur.SetBounds(mainX + pad + Dpi.S(102), 13, Math.Max(60, mainW - 2 * pad - 102 - 118), 18);
            if (chkMulti != null)
            {
                chkMulti.SetBounds(mainX + Math.Max(Dpi.S(60), mainW - Dpi.S(112)), 9, Dpi.S(106), 22);
                chkMulti.BringToFront();
            }

            if (peek != null)
            {
                if (detailW > 0)
                {
                    int dx = x + candW + Theme.G;
                    if (lblDetail != null)
                    {
                        lblDetail.SetBounds(dx, 12, Math.Max(80, detailW), 18);
                        lblDetail.Visible = lv != null && lv.Visible;
                    }
                    peek.SetBounds(dx, 34, Math.Max(Dpi.S(220), detailW), Math.Max(Dpi.S(240), H - 40));
                    if (splitB != null)
                    {
                        splitB.Visible = true;
                        splitB.SetBounds(Math.Max(0, dx - Dpi.S(7)), 0, Dpi.S(6), H);
                    }
                }
                else
                {
                    if (lblDetail != null) lblDetail.Visible = false;
                    if (splitB != null) splitB.Visible = false;
                    peek.SetBounds(mainX + pad, peekTop, Math.Max(Dpi.S(160), mainW - 2 * pad), peekH);
                }
                peek.Visible = lv != null && lv.Visible;
            }

            if (headMain != null)
            {
                headMain.SetBounds(mainX + 4, top, mainW - 8, headH);
                headMain.Visible = lv != null && lv.Visible;
            }
            int listH = Math.Max(40, bottom - top - headH);
            if (lv != null) lv.SetBounds(mainX + 4, top + headH, mainW - 8, listH);

            if (lvExtra != null)
            {
                if (side)
                {
                    // 并排：两栏表头对齐在同一水平线上，只剩标题行在右侧重复一次。
                    // 可见性也在这里定：走到这一支说明 Items 里确实有扩展，
                    // 不能指望 FillDetail 一定把 Visible 打开（空位那一轮会把它关掉）。
                    if (lblExtraTitle != null) lblExtraTitle.SetBounds(exX + pad, 34, Math.Max(60, exW - 2 * pad), 18);
                    lvExtra.Visible = true;
                    if (headExtra != null) { headExtra.SetBounds(exX + 4, top, exW - 8, headH); headExtra.Visible = true; }
                    lvExtra.SetBounds(exX + 4, top + headH, exW - 8, listH);
                }
                else
                {
                    // 窄窗（抽屉展开 / 小屏）：退回上下堆叠。候选先按内容收高，剩下的才给扩展；
                    // 这个位置没有扩展时，候选照旧占满，只在底部留一行灰字说明。
                    int rows = lv == null ? 0 : lv.Items.Count;
                    int want = rows * Dpi.S(Theme.RowH) + headH + Theme.S;
                    int gap = headH;
                    int mainH = hasEx
                        ? Math.Max(110, Math.Min(want, Math.Max(110, (bottom - top - gap) * 62 / 100)))
                        : Math.Max(110, bottom - top - gap);
                    if (lv != null) lv.SetBounds(mainX + 4, top + headH, mainW - 8, Math.Max(40, mainH - headH));
                    int exTop = top + mainH + gap;
                    if (lblExtraTitle != null)
                        lblExtraTitle.SetBounds(mainX + pad, Math.Max(top + 8, exTop - 22), Math.Max(80, mainW - 2 * pad), 18);
                    int exH = Math.Max(0, bottom - exTop);
                    if (exH >= 40 + headH)
                    {
                        lvExtra.SetBounds(mainX + 4, exTop + headH, mainW - 8, Math.Max(40, exH - headH));
                        lvExtra.Visible = true;
                        if (headExtra != null) { headExtra.SetBounds(mainX + 4, exTop, mainW - 8, headH); headExtra.Visible = true; }
                    }
                    else
                    {
                        lvExtra.Visible = false;
                        if (headExtra != null) headExtra.Visible = false;
                    }
                }
            }

            // ---- 工坊推荐区：占候选列下方三成多（最多 220px），列表让位 ----
            if (recPanel != null)
            {
                bool show = lv != null && lv.Visible && recKey.Length > 0 && lv.Width > Dpi.S(160);
                if (!show) recPanel.Visible = false;
                else
                {
                    int whole = lv.Height;
                    // 用户 2026-11-21 定：上下各一半，别做成一条扁带
                    int recH = Math.Max(Dpi.S(120), (whole - Dpi.S(6)) / 2);
                    if (whole - recH < Dpi.S(90)) { recPanel.Visible = false; }
                    else
                    {
                        lv.Height = whole - recH - Dpi.S(6);
                        recPanel.SetBounds(lv.Left, lv.Top + lv.Height + Dpi.S(6), lv.Width, recH);
                        recPanel.Visible = true;
                        int sw = Dpi.S(64);
                        btnRecShuffle.SetBounds(Math.Max(Dpi.S(4), recPanel.Width - sw - Dpi.S(4)), 0, sw, Dpi.S(18));
                        recTitle.SetBounds(Dpi.S(4), 0, Math.Max(60, recPanel.Width - sw - Dpi.S(12)), Dpi.S(16));
                        recGrid.SetBounds(Dpi.S(2), Dpi.S(21), Math.Max(60, recPanel.Width - 4), Math.Max(40, recH - Dpi.S(24)));
                    }
                }
            }

            if (btnPick != null) btnPick.SetBounds(x + pad, by, 76, actH);
            // 右下角从右往左排：「指定位置…」只在未归类页。脚注按实际占用让位。
            int edge = x + rightW - 8;
            int used = 0;
            if (lblFoot != null)
                lblFoot.SetBounds(x + pad, by + (actH - 18) / 2, Math.Max(60, candW - 2 * pad - used), 18);
            LayoutCols();
        }

        // ---------------- 工坊推荐（候选列下半截） ----------------

        /// <summary>当前位置在工坊里该搜什么词：和工坊窗口「分类」下拉用的是同一套映射。</summary>
        private string RecQuery()
        {
            if (mf != null && !string.IsNullOrEmpty(mf.SearchText)) return "";
            if (!string.IsNullOrEmpty(selKey) && selKey[0] == '#') return "";
            // 空分类也照样推荐：用户点进「这个位置一个 MOD 都没有」时，最需要的就是"去看看工坊有什么"
            Slot s = Current;
            if (s == null || string.IsNullOrEmpty(s.Name)) return "";
            string q = Workshop.SearchWord(s.Name ?? "", s.Key ?? "");
            return q ?? "";
        }

        /// <summary>换位置刷一次推荐；同一位置不重复拉网。搜不到/连不上只改标题那行，不弹窗。</summary>
        /// <summary>「换一换」：清掉当前键强制重拉一批（页码会避开上一批）。</summary>
        private void RefreshRecommend()
        {
            if (recPanel == null) return;
            string q = RecQuery();
            if (q.Length == 0) return;
            recKey = "";
            PrepareRecommend();
        }

        private void PrepareRecommend()
        {
            if (recPanel == null) return;
            string q = RecQuery();
            if (q.Length == 0) { recGen++; recKey = ""; recPanel.Visible = false; return; }
            if (q == recKey) return;
            recKey = q;
            int gen = ++recGen;
            recPanel.Visible = true;
            recTitle.Text = Lang.S("工坊推荐：正在搜…");
            ShowRecSkeleton();
            LayoutSelf();

            int tw = Math.Max(64, Dpi.S(104)), th = Math.Max(36, Dpi.S(58));
            string key = q;
            System.Threading.Tasks.Task.Run(delegate
            {
                List<WsItem> got = null;
                // 每次开程序都换一批：页码随机，不落磁盘、不跨会话缓存
                int pg = recRnd.Next(1, 4);
                if (pg == recPage) pg = pg % 3 + 1;      // 换一换别撞回同一页
                recPage = pg;
                try { got = Workshop.SearchItems(key, "trend", pg); } catch { }
                if (got != null) { try { got = new List<WsItem>(got); } catch { } }
                // 缩略图并发取（6 路）：单线程下 8 张图就是 8 个往返
                List<WsItem> cand = new List<WsItem>();
                if (got != null)
                    foreach (WsItem it in got)
                    {
                        if (cand.Count >= 24) break;
                        if (it == null || string.IsNullOrEmpty(it.Preview)) continue;
                        cand.Add(it);
                    }
                Image[] arr = new Image[cand.Count];
                System.Threading.Tasks.Parallel.For(0, cand.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 },
                    delegate(int i)
                    {
                        try { arr[i] = WsThumb.LoadCover(cand[i].Preview, cand[i].Id, tw, th); } catch { }
                    });
                List<Image> pics = new List<Image>();
                foreach (Image im in arr) if (im != null) pics.Add(im);
                if (pics.Count > 0 && got != null)
                {
                    // pics 可能因个别失败和 got 错位：按成功顺序重建条目表，避免封面和标题对不上
                    List<WsItem> aligned = new List<WsItem>();
                    int k = 0;
                    for (int i = 0; i < cand.Count; i++) if (arr[i] != null) { aligned.Add(cand[i]); k++; }
                    got = aligned;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (gen != recGen) { foreach (Image p in pics) { try { p.Dispose(); } catch { } } return; }
                        if (got == null) { recTitle.Text = Lang.S("工坊推荐：连不上，稍后再试"); return; }
                        FillRecUi(key, got, pics);
                        LayoutSelf();
                    });
                }
                catch { }
            });
        }

        /// <summary>
        /// 骨架屏：还没搜回来时先铺几张空卡，别让人对着白板猜（和工坊窗口的骨架同一个意思）。
        /// 搜回来/失败时整批换掉。
        /// </summary>
        private void ShowRecSkeleton()
        {
            recGrid.ShowSkeleton();
        }

        /// <summary>
        /// 鼠标悬停到一张推荐上：右侧详情栏按「已选 MOD」同一套排版显示它（封面/标题/订阅数/简介）。
        /// 底部那句"双击去工坊…"用户说白占位置，不给了；点一下就直接开窗口。
        /// </summary>
        private void ShowRecDetail(WsItem it)
        {
            if (peek == null || it == null) return;
            Image pic = null;
            try { pic = recGrid.PicOf(it); } catch { }
            int dtok = ++recDetailToken;
            string meta = it.Subs > 0
                ? Lang.F("订阅 {0} · 浏览 {1}", WsText.Compact(it.Subs), WsText.Compact(it.Views))
                : Lang.F("浏览 {0}", WsText.Compact(it.Views));
            string desc = WsText.Clean(it.Desc ?? "");
            if (desc.Length > 700) desc = desc.Substring(0, 700) + "…";
            peek.ShowImage(it.Title ?? "", meta, Lang.S("来源：Steam 创意工坊"), pic, "", desc);

            // 卡片图只有 104×58，塞进详情栏必然糊；后台按详情尺寸再取一张大的换上去
            string url = it.Preview, id = it.Id, title = it.Title ?? "";
            if (string.IsNullOrEmpty(url)) return;
            int w = Math.Max(320, Dpi.S(560)), h = w * 9 / 16;
            System.Threading.Tasks.Task.Run(delegate
            {
                Image big = null;
                try { big = WsThumb.LoadCover(url, id, w, h); } catch { }
                if (big == null) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (dtok != recDetailToken) { try { big.Dispose(); } catch { } return; }
                        if (recBigPic != null) { try { recBigPic.Dispose(); } catch { } }
                        recBigPic = big;
                        peek.ShowImage(title, meta, Lang.S("来源：Steam 创意工坊"), big, "", desc);
                    });
                }
                catch { try { big.Dispose(); } catch { } }
            });
        }

        /// <summary>把搜到的东西交给卡片墙：能放几个就画几个（没有滚动条）。</summary>
        private void FillRecUi(string q, List<WsItem> items, List<Image> pics)
        {
            List<WsItem> okItems = new List<WsItem>();
            List<Image> okPics = new List<Image>();
            for (int i = 0; i < items.Count && i < pics.Count; i++)
            {
                if (items[i] == null || pics[i] == null) continue;
                okItems.Add(items[i]);
                okPics.Add(pics[i]);
            }
            recGrid.SetItems(okItems, okPics);
            recTitle.Text = okItems.Count > 0 ? Lang.S("工坊推荐 · ") + q
                                              : Lang.S("工坊里没搜到相关 MOD：") + q;
        }

        /// <summary>点一张推荐 = 单开一个工坊窗口，按这个词看（推荐区本身不是搜索页）。</summary>
        private void OpenRecPick(WsItem it)
        {
            if (it == null || mf == null) return;
            // 点哪张就打开哪张的详情页（以前只按分类打开搜索结果墙，用户 2026-11-21 反馈）
            mf.OpenWorkshopFor(recKey, it);
        }

        private void LayoutCols()
        {
            Cols(lv);
            Cols(lvExtra);
            // 列宽已经按客户区算好，不需要横向滚动；系统那条亮色滚动条只会破坏深色
            if (lv != null && lv.IsHandleCreated) Native.HideHScroll(lv.Handle);
            if (lvExtra != null && lvExtra.IsHandleCreated) Native.HideHScroll(lvExtra.Handle);
            if (headMain != null) headMain.Invalidate();
            if (headExtra != null) headExtra.Invalidate();
        }

        private static void Cols(ListView v)
        {
            if (v == null || v.Columns.Count < 3) return;
            // 下面每一个数字都是 96dpi 下的设计值。WinForms 的 AutoScaleMode.Dpi 只缩放
            // 控件尺寸和字体，ListView 的 Columns[].Width 不在其列 —— 不换算的话 125%/150%
            // 下字变大了、四列还是原来的像素宽，「改了什么 / 大小 / 已装 / 开关」就会截字，
            // 只有名字列因为吃剩余宽度反而变宽，看着尤其别扭。
            // 只在真要出竖滚动条时才扣它的宽度：以前一律扣「滚动条 + 8」，列表不滚动时右侧就空出一条，
            // 行底色跟着断在那儿，看着像一道缝。列宽铺满后，行底色自然铺满整行。
            int sbW = v.Items.Count > 0 && (v.Items.Count * Dpi.S(24)) > v.ClientSize.Height
                ? SystemInformation.VerticalScrollBarWidth : 0;
            int w = Math.Max(Dpi.S(120), v.ClientSize.Width - sbW);
            int kindW, sizeW, instW, swW;
            if (w >= Dpi.S(410))
            {
                kindW = Dpi.S(100); sizeW = Dpi.S(74); instW = Dpi.S(84); swW = Dpi.S(62);
            }
            else
            {
                // 最小窗口下先把信息量最低的“改了什么”收掉，保住名称、大小、已装、开关。
                // 不横向滚动：这一栏本来就是窄窗口里最容易被裁掉的区域。
                kindW = 0;
                if (w >= Dpi.S(340)) { sizeW = Dpi.S(64); instW = Dpi.S(70); swW = Dpi.S(54); }
                else { sizeW = Dpi.S(56); instW = Dpi.S(60); swW = Dpi.S(46); }
            }

            // 本地模式：没有「库」可比，「已装」恒为真 —— 这一列纯属噪音，收成 0 宽、把宽度并给开关列。
            // 于是本地模式下能看到的只剩「开启 / 关闭」两个状态（开关列同时也是点击入口）。
            bool local = Core.IsLocal && v.Columns.Count >= 5;
            if (local) { instW = 0; }        // 本地模式没有「已装」这列，腾出来的宽度给名字列，别让开关列空占一大截

            int nameW = Math.Max(Dpi.S(90), w - kindW - sizeW - instW - swW);
            v.Columns[0].Width = nameW;
            v.Columns[1].Width = kindW;
            v.Columns[2].Width = sizeW;
            if (v.Columns.Count >= 5)
            {
                v.Columns[3].Width = instW;
                v.Columns[4].Width = swW;
                // 表头跟着模式换：本地模式下没有「已装」，那一列叫「已装」会误导
                string t3 = local ? "" : Lang.S("已装");
                string t4 = local ? "开启 ⇄" : "开关 ⇄";
                if (v.Columns[3].Text != t3) v.Columns[3].Text = t3;
                if (v.Columns[4].Text != t4) v.Columns[4].Text = t4;
            }

            // 兜底：上面算出来的列宽加起来没铺满客户区（滚动条预留、DPI 取整都会差十几像素），
            // 差额补到最后一列 —— 不补的话行底色到那儿就断，右边空出一条。
            int cw = 0;
            for (int i = 0; i < v.Columns.Count; i++) cw += v.Columns[i].Width;
            int slack = v.ClientSize.Width - cw;
            if (slack > 0) v.Columns[v.Columns.Count - 1].Width += slack;
        }

        /// <summary>
        /// 自检用：给定客户区宽度算一遍列宽，返回 [改了什么, 大小, 已装, 开关] 四个定宽列。
        /// 走的就是真实的 <see cref="Cols"/>，不另写一份算法 —— 否则测的和用的又是两套。
        /// </summary>
        /// <summary>
        /// 自检：拿一个带扩展的位置跑一遍真实布局，回报右半是并排还是堆叠、两栏几何对不对。
        /// 走的是 LayoutSelf 本身，不是另写一份算法；测完把选中恢复成原样。
        /// </summary>
        internal string LayoutProbe(out bool side, out bool measurable)
        {
            side = false;
            measurable = false;
            string saved = selKey;
            Slot target = null;
            foreach (Slot s in slots)
                if (s != null && s.Items.Count > 1) { target = s; break; }
            if (target == null) return "库里没有带扩展的位置";
            measurable = true;

            selKey = target.Key;
            FillDetail();
            LayoutSelf();

            // 不要用 Visible 判断：自检不弹窗口，父级不可见时子控件的 Visible 一律返回 false。
            // 只看几何——并排的特征是扩展栏整体落在候选栏右侧且有自己的宽度。
            bool both = lv != null && lvExtra != null && lvExtra.Width > 0
                     && headExtra != null && headExtra.Width > 0 && lvExtra.Left >= lv.Right;
            side = both;
            string r = target.Name + "：候选 x" + lv.Left + "–" + lv.Right
                     + "（宽 " + lv.Width + "），扩展 x" + lvExtra.Left + "–" + lvExtra.Right
                     + "（宽 " + lvExtra.Width + "），" + (side ? "并排" : "上下堆叠")
                     + "；动作行 y" + lblFoot.Top + "，列表底 y" + (lv.Top + lv.Height);

            if (saved != selKey) { selKey = saved; FillDetail(); LayoutSelf(); }
            return r;
        }

        internal static int[] ColsForTest(int clientWidth)
        {
            ListView v = new ListView();
            try
            {
                v.Columns.Add(Lang.S("名称"), 380);
                v.Columns.Add(Lang.S("改了什么"), 100);
                v.Columns.Add(Lang.S("大小"), 74);
                v.Columns.Add(Lang.S("已装"), 84);
                v.Columns.Add(Lang.S("开关 ⇄"), 62);
                v.ClientSize = new Size(clientWidth, 400);
                Cols(v);
                return new int[] { v.Columns[1].Width, v.Columns[2].Width, v.Columns[3].Width, v.Columns[4].Width };
            }
            finally { v.Dispose(); }
        }
    }

    /// <summary>
    /// 候选列表的表头。原生 ListView 表头在深色主题下始终是白色的一条
    /// （给表头窗口换主题也不生效），所以干脆不用它，按列宽自己画一条。
    /// </summary>
    internal sealed class ColHead : Control
    {
        private readonly ListView lv;

        public ColHead(ListView owner)
        {
            lv = owner;
            TabStop = false;
            Font = Theme.UISmall;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            if (lv == null || lv.Columns.Count == 0) return;

            int x = 2;
            for (int i = 0; i < lv.Columns.Count; i++)
            {
                ColumnHeader c = lv.Columns[i];
                int w = c.Width;
                if (w <= 0) continue;
                TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                                  | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                f |= c.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
                Rectangle r = new Rectangle(x + 4, 1, Math.Max(8, w - 8), Height - 2);
                TextRenderer.DrawText(g, c.Text, Font, r, Theme.Ink3, f);
                x += w;
            }

            using (Pen p = new Pen(Theme.Line))
                g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>
    /// 自己画的滚动容器。系统的滚动条在深色主题下始终是一条白轨（换窗口主题、
    /// 打 DWM 深色标记都不管用），树本身又是自绘的，索性把整条滚动条也自己画：
    /// 10px 宽、跟主题同色、能滚轮、能拖、点轨道翻页。
    /// </summary>
    internal sealed class ScrollHost : Panel
    {
        private static readonly int BarW = Dpi.S(10);
        private Control body;
        private int offset;
        private bool dragging, hoverBar;
        private int dragY, dragOffset;

        /// <summary>树下面的空白被右键时调它（树比可视区矮时，那块的宿主是 Panel，不是树）。</summary>
        internal Action<Point> BlankMenu;

        public ScrollHost()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Attach(Control b)
        {
            body = b;
            Controls.Add(b);
            Relayout();
        }

        private int MaxOffset { get { return body == null ? 0 : Math.Max(0, body.Height - ClientSize.Height); } }
        internal bool CanScroll { get { return MaxOffset > 0; } }
        internal int Offset { get { return offset; } }

        internal void ScrollTo(int v)
        {
            int nv = Math.Max(0, Math.Min(MaxOffset, v));
            if (nv == offset) return;
            offset = nv;
            if (body != null)
            {
                body.Top = -offset;
                // 必须整体重绘：只挪位置的话，Windows 对子窗口做位块搬移，
                // 搬的是「上一次画过的内容」。视口裁剪的自绘控件（卡片墙）在初始视口外的
                // 表面从来没画过 —— 往下滚就会滚出一段空白， 直到有别的机会触发重绘。
                body.Invalidate();
            }
            Invalidate();
        }

        internal void Relayout()
        {
            if (body == null) return;
            offset = Math.Max(0, Math.Min(offset, MaxOffset));
            bool bar = MaxOffset > 0;
            int w = Math.Max(20, ClientSize.Width - (bar ? BarW : 0));
            body.SetBounds(0, -offset, w, body.Height);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        private Rectangle Thumb()
        {
            int h = ClientSize.Height;
            if (body == null || h <= 0) return new Rectangle(Width - BarW, 0, BarW, 0);
            int th = Math.Max(36, (int)((long)h * h / Math.Max(1, body.Height)));
            th = Math.Min(h - 2, th);
            int travel = Math.Max(0, h - th - 2);
            int pos = MaxOffset == 0 ? 0 : (int)((long)travel * offset / MaxOffset);
            return new Rectangle(Width - BarW + 3, 1 + pos, BarW - 6, th);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (!CanScroll) return;
            Rectangle t = Thumb();
            using (GraphicsPath p = Theme.Round(t, (BarW - 6) / 2))
            using (SolidBrush b = new SolidBrush(dragging ? Theme.Ink3 : (hoverBar ? Theme.Ink3 : Theme.Line)))
                g.FillPath(b, p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                int travel = Math.Max(1, ClientSize.Height - Thumb().Height - 2);
                int d = e.Y - dragY;
                ScrollTo(dragOffset + (int)((long)d * MaxOffset / travel));
                return;
            }
            bool hot = e.X >= Width - BarW && CanScroll;
            if (hot != hoverBar) { hoverBar = hot; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverBar = false;
            Invalidate();
        }

        /// <summary>键盘滚：PageUp/PageDown/Home/End（子控件拿到焦点时用；滚轮见下面原有的 OnMouseWheel）。</summary>
        internal void ScrollByKey(Keys key)
        {
            int page = Math.Max(1, ClientSize.Height - Dpi.S(60));
            if (key == Keys.PageDown) ScrollTo(offset + page);
            else if (key == Keys.PageUp) ScrollTo(offset - page);
            else if (key == Keys.Home) ScrollTo(0);
            else if (key == Keys.End) ScrollTo(MaxOffset);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right && e.X < Width - BarW && BlankMenu != null)
            {
                BlankMenu(PointToScreen(e.Location));
                return;
            }
            if (!CanScroll || e.X < Width - BarW) return;
            Rectangle t = Thumb();
            if (t.Contains(e.Location))
            {
                dragging = true;
                dragY = e.Y;
                dragOffset = offset;
                Capture = true;            // 拖着滑块滑出窗口再松手，也得知道松手了
            }
            else
            {
                // 点轨道 = 翻一屏
                ScrollTo(offset + (e.Y < t.Top ? -ClientSize.Height : ClientSize.Height));
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            if (Capture) Capture = false;
            Invalidate();
        }

        /// <summary>滑块拖到窗口外松手时 MouseUp 收不到，靠捕获被收回这个时机复位。</summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || !dragging) return;
            dragging = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (CanScroll)
            {
                ScrollTo(offset - e.Delta);
                return;
            }
            base.OnMouseWheel(e);
        }
    }

    // ============================================================
    //  树体：自绘，三层（大类 / 小分类 / 槽位）
    // ============================================================
    internal sealed class SlotTree : Control
    {
        // 行高跟着 DPI 走：树是自绘的，控件被放大而这里还是 28 的话，
        // 字体按 point 变大、行距不变，几行文字就叠在一起了。
        // 100% 缩放下 Dpi.S 原样返回设计值，行高与列表行、按钮行同为 32。
        private static readonly int RowH = Dpi.S(Theme.RowH);
        private readonly CatTree owner;
        private readonly List<TreeRow> rows = new List<TreeRow>();
        private int hover = -1;
        private int focus = -1;
        private readonly ToolTip tip = new ToolTip();
        private int tipRow = -2;

        // 拖动分支：按住分支行上下拖，松手就换顺序 / 换层级。
        // 行高只有 28px，所以每一行分三段命中：上 30% = 插到它前面，下 30% = 插到它后面，中间 = 变成它的子分支。
        private bool pressArm;        // 这次按下的是「能拖」的行（大类 / 小分类 / 分支 / 位置）
        private int pressRow = -1;
        private Point pressPt;
        private bool dragging;
        private string dragName = "";
        private string dragCat = "";  // 拖的东西在哪个大类（小分类行 / 位置行要用）
        private string dragSub = "";  // 位置行原来在哪个小分类
        private int dragKind;         // 1=分支行 2=大类标题行 3=小分类行 4=位置行
        private int dropRow = -1;     // 落点行（-1 = 没有落点，松手就当放弃）
        private int dropMode;         // 1=插前面 2=插后面 3=放进它下面
        // 拖动预览：树直接画成「松手后」的样子 —— 搬的块从原地消失、出现在新位置
        private List<TreeRow> previewRows;   // null = 没有合法落点，照常画
        private int pvIns = -1;       // 搬的块在预览里的起始行
        private int pvB1 = -1;        // 搬的块在预览里的结束行（含）
        private Point dragPt;         // 跟随鼠标的小标签画在这儿
        private int lastMoveLog;      // DRAGIN 按住未起拖时的移动日志节流

        // 从「未归类」列表 OLE 拖过来的 MOD（与上面自绘的行拖动是两套，不会同时发生）：
        // 悬停时高亮落点行 + 跟随光标的小标签，悬在视口上下边自动滚树。
        private bool extDrag;
        private string extName = "";
        private int extRow = -1;
        private List<ModGroup> extGroups = new List<ModGroup>();   // 拖进来的那几组（支持多选批量拖）
        private readonly System.Windows.Forms.Timer extExpand = new System.Windows.Forms.Timer();  // 悬停折叠分类：到点自动展开
        private int extExpandRow = -1;
        private Point extPt;
        private readonly Timer extScroll;

        public SlotTree(CatTree o)
        {
            owner = o;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = Theme.UI;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "MOD 方案树";
            AccessibleDescription = "用上下方向键移动，左右键展开或折叠，Enter 或空格选择";

            // ---- 接收从未归类列表拖来的 MOD ----
            AllowDrop = true;
            DragEnter += ExtDragOver;
            DragOver += ExtDragOver;
            DragLeave += delegate { ExtDragEnd(); };
            DragDrop += ExtDragDrop;
            extScroll = new Timer { Interval = 45 };
            extScroll.Tick += delegate { ExtAutoScroll(); };
            extExpand.Interval = 600;
            extExpand.Tick += delegate
            {
                extExpand.Stop();
                if (!extDrag || extExpandRow < 0 || extExpandRow >= rows.Count) return;
                TreeRow rr = rows[extExpandRow];
                if (rr.IsCat && owner.IsFolded(rr.Cat)) owner.SetFolded(rr.Cat, false);
                else if (rr.IsSub && owner.IsSubFolded(rr.Cat, rr.Sub)) owner.SetSubFolded(rr.Cat, rr.Sub, false);
                else return;
                extRow = -1;                 // 摊开后行号全变了，等下一次 DragOver 重算
                extExpandRow = -1;
                Invalidate();
            };
        }

        public int RowCount { get { return rows.Count; } }
        internal int FocusIndex { get { return focus; } }

        public void SetRows(List<TreeRow> r)
        {
            // 重建行（折叠 / 展开 / 重新分组）时按住键盘焦点原来那一行。
            // 以前一律 focus = -1，结果按 ← 收一段之后焦点跳回第 0 行，
            // 紧接着按 → 展开的就不是刚才那一段了 —— 键盘用户会直接迷路。
            string keepSlot = null, keepCat = null, keepSub = null;
            if (focus >= 0 && focus < rows.Count)
            {
                TreeRow old = rows[focus];
                if (old.Slot != null) keepSlot = old.Slot.Key;
                else if (old.IsSub) { keepCat = old.Cat; keepSub = old.Sub; }
                else if (old.IsCat) keepCat = old.Cat;
            }

            rows.Clear();
            if (r != null) rows.AddRange(r);
            tipRow = -2;
            focus = -1;
            if (keepSlot != null || keepCat != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    TreeRow x = rows[i];
                    // 四种行分别按各自的身份找，别串：小分类行也带着大类名，
                    // 混着判会先撞上大类标题，焦点就被拽到顶上了。
                    if (keepSlot != null) { if (x.Slot != null && x.Slot.Key == keepSlot) { focus = i; break; } }
                    else if (keepSub != null) { if (x.IsSub && x.Cat == keepCat && x.Sub == keepSub) { focus = i; break; } }
                    else if (keepCat != null) { if (x.IsCat && x.Cat == keepCat) { focus = i; break; } }
                }
                // 那一行被折叠掉了（子项全收起来），就退回它所属的大类标题，别让焦点凭空消失
                if (focus < 0 && keepCat != null)
                    for (int i = 0; i < rows.Count; i++)
                        if (rows[i].IsCat && rows[i].Cat == keepCat) { focus = i; break; }
            }
            Height = Math.Max(12, rows.Count * RowH + 6);
            ScrollHost host = Parent as ScrollHost;
            if (host != null) host.Relayout();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.TreeBg);
            int W = Width;
            if (W <= 0) return;

            using (Pen branch = new Pen(Theme.TreeBranch))
            using (SolidBrush selBg = new SolidBrush(Theme.TreeSel))
            using (SolidBrush hovBg = new SolidBrush(Theme.TreeHover))
            {
                // 拖动且有预览时，画的就是「松手后」的树 —— 搬的块已出现在新位置
                List<TreeRow> view = (dragging && previewRows != null) ? previewRows : rows;
                int hov = dragging ? -1 : hover;
                // 大类 / 小分类右边那个数字：按完整位置表算，收起子分类也不变
                List<Slot> all = owner.Slots;
                for (int i = 0; i < view.Count; i++)
                {
                    TreeRow r = view[i];
                    int y = i * RowH + 4;
                    bool focused = Focused && !dragging && i == focus;

                    if (r.IsCat)
                    {
                        bool open = !owner.IsFolded(r.Cat);
                        // 「地图」这类：类标题行本身就是容器，选中时给和位置行一样的高亮，
                        // 否则点完看不出到底选没选中。
                        bool catSel = r.Slot != null && string.Equals(r.Slot.Key, owner.SelectedKey, StringComparison.Ordinal);
                        if (catSel) g.FillRectangle(selBg, 0, y, W, RowH);
                        else if (i == hov) g.FillRectangle(hovBg, 0, y, W, RowH);
                        DrawTri(g, 10, y + RowH / 2, open);
                        Glyph.Draw(g, Glyph.ForCat(r.Cat), new Rectangle(18, y + (RowH - 18) / 2, 18, 18), Theme.Ink2);
                        TextRenderer.DrawText(g, Lang.S(r.Cat), Theme.UIBold, new Rectangle(42, y, W - 106, RowH), Theme.Ink,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        // 计数按整个大类算，不受折叠影响 —— 收起来只是看不见行，
                        // 位置数不该跟着变（桌面上那张卡写的也是这个数）
                        int n = 0;
                        if (all != null) foreach (Slot sl in all) if (sl.Cat == r.Cat) n++;
                        string num = Lang.F("{0} 个", r.Cat == "未归类" ? owner.Unmatched : n);
                        TextRenderer.DrawText(g, num, Theme.UISmall, new Rectangle(W - 62, y, 50, RowH), Theme.Ink3,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        continue;
                    }

                    if (r.IsSub)
                    {
                        bool open = !owner.IsSubFolded(r.Cat, r.Sub);
                        if (i == hov) g.FillRectangle(hovBg, 0, y, W, RowH);
                        g.DrawLine(branch, 17, y, 17, y + RowH);
                        g.DrawLine(branch, 17, y + RowH / 2, 26, y + RowH / 2);
                        DrawTri(g, 32, y + RowH / 2, open);
                        Glyph.Draw(g, Glyph.ForSub(r.Sub, r.Cat), new Rectangle(40, y + (RowH - 17) / 2, 17, 17), Theme.Ink2);
                        TextRenderer.DrawText(g, Lang.S(r.Sub), Theme.UIBold, new Rectangle(62, y, W - 126, RowH), Theme.Ink2,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        int k = 0;
                        if (all != null) foreach (Slot sl in all) if (sl.Cat == r.Cat && sl.Sub == r.Sub) k++;
                        string num = Lang.F("{0} 个", k);
                        TextRenderer.DrawText(g, num, Theme.UISmall, new Rectangle(W - 62, y, 50, RowH), Theme.Ink3,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        // 往下连一小段，免得和位置行的竖线断开
                        if (open && k > 0) g.DrawLine(branch, 39, y + RowH / 2, 39, y + RowH);
                        continue;
                    }


                    Slot s = r.Slot;
                    if (s == null) continue;

                    // 「＋ 新建分支…」不是位置：只画一行灰字，点了弹输入框
                    if (s.Key == "#mine+new")
                    {
                        if (i == hov) g.FillRectangle(hovBg, 0, y, W, RowH);
                        g.DrawLine(branch, 39, y, 39, y + RowH / 2);
                        g.DrawLine(branch, 39, y + RowH / 2, 48, y + RowH / 2);
                        TextRenderer.DrawText(g, s.Name, Theme.UI, new Rectangle(54, y, W - 66, RowH), Theme.Ink3,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        continue;
                    }

                    bool sel = s.Key == owner.SelectedKey;
                    if (sel) g.FillRectangle(selBg, 0, y, W, RowH);
                    else if (i == hov) g.FillRectangle(hovBg, 0, y, W, RowH);
                    if (sel) using (SolidBrush b = new SolidBrush(Theme.Mark)) g.FillRectangle(b, 0, y + 3, 2, RowH - 6);

                    // 分支线：竖线 + 一根横线（位置层缩进到小分类下面）
                    bool nextSame = (i + 1 < view.Count) && !view[i + 1].IsCat && !view[i + 1].IsSub
                                    && view[i + 1].Cat == r.Cat && view[i + 1].Sub == r.Sub;
                    int bx = 39;
                    // 子分支按层级往右缩进：父子关系一眼看得出来，不用点开猜
                    if (CatTree.IsBranchSlot(s)) bx += MyBranch.Depth(s.Name) * 12;
                    g.DrawLine(branch, bx, y, bx, y + (nextSame ? RowH : RowH / 2 + 1));
                    g.DrawLine(branch, bx, y + RowH / 2, bx + 9, y + RowH / 2);

                    // 状态点：绿=选了本体，橙=叠了好几件本体，蓝=只有扩展（音效/只改第一人称模型），灰=空
                    // 「一堆」（地图 / 我的分支）里勾好几个是正常事，别用橙色报警
                    int cnt = owner.CheckedGroups(s);
                    int ecnt = 0;   // 扩展已移除：这里恒 0，下面几处判断自然走不到
                    // 「允许多选」的位置叠着是正常的：用蓝色（信息色）而不是黄色，免得看着像警告
                    bool multiSlot = Core.Cfg.MultiSlots.Contains(s.Key);
                    Color dot = s.Pile ? (cnt > 0 ? Theme.Ok : Theme.TreeBranch)
                              : cnt > 1 ? (multiSlot ? Theme.Info : Theme.Warn)
                              : cnt == 1 ? Theme.Ok
                              : Theme.TreeBranch;
                    using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, bx + 17, y + RowH / 2 - 3, 6, 6);

                    int nx = bx + 30;
                    int nameW = 86;
                    // 「步枪」小分类下又有个位置叫「步枪」= 认不出具体型号的那一堆，改个说法免得看着像重复。
                    // 空位不套这条：空位本来就叫得准（手枪 / 近战武器），改成「未分型号」反而看不懂。
                    // 平台那一栏只有一个位置，名字本来就叫「平台」：别套「未分型号」，那是给枪种泛称用的。
                    string showName = (s.Name == s.Sub && !s.Empty && s.Cat != "平台") ? Lang.S("未分型号") : Lang.S(s.Name);
                    // 空位的名字压暗一档：一眼分得出「这里还缺 MOD」和「这里有包没选」
                    TextRenderer.DrawText(g, showName, sel ? Theme.UIBold : Theme.UI,
                        new Rectangle(nx, y, nameW, RowH), s.Empty ? Theme.Ink3 : Theme.Ink,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                    // 「扩」角标：名字后面挂一个蓝底小图标，有可叠扩展的位置不用看数字就能扫出来

                    ModGroup chosen = owner.ChosenOf(s);
                    string sub = chosen == null ? (ecnt > 0 ? "原版" : "未选") : chosen.Name;
                    if (s.Empty) sub = "还没有 MOD";
                    else if (s.Pile)
                    {
                        sub = cnt == 0 ? "未勾 · 共 " + s.Items.Count + " 个" : "已勾 " + cnt + " 个";
                    }
                    else
                    {
                        if (cnt > 1) sub = "叠了 " + cnt + " 件";
                    }

                    // 右端数字：本体候选数；有扩展时「+N」单独用信息蓝 —— 扫一眼，带蓝色 +N 的就是有扩展可叠
                    string plus = "";
                    int pw = 0;
                    if (plus.Length > 0)
                    {
                        Size sz = TextRenderer.MeasureText(g, plus, Theme.UISmall,
                            new Size(80, RowH), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        pw = sz.Width;
                        TextRenderer.DrawText(g, plus, Theme.UISmall,
                            new Rectangle(W - 8 - pw, y, pw + 2, RowH), Theme.Info,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                            | TextFormatFlags.NoPrefix);
                    }
                    TextRenderer.DrawText(g, s.Items.Count.ToString(), Theme.UISmall,
                        new Rectangle(W - 42 - pw, y, 32, RowH), Theme.Ink3,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                    int subX = nx + nameW + 26;      // 角标固定占一列，没角标的行文字也对得齐
                    int subW = W - subX - 46 - pw;
                    string subText = sub;
                    // 窄窗口下这一列只剩三四十像素，「还没有 MOD」会被截成「还没有 M…」，
                    // 看着像排版坏了。空位换成两个字的短说法，宁可少说一个词。
                    if (s.Empty && subW > 20)
                    {
                        Size need = TextRenderer.MeasureText(g, sub, Theme.UISmall,
                            new Size(subW, RowH), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        if (need.Width > subW) subText = "空位";
                    }
                    if (subW > 20)
                        TextRenderer.DrawText(g, subText, Theme.UISmall, new Rectangle(subX, y, subW, RowH), Theme.Ink3,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }

            // 键盘焦点必须看得见；虚线画在整行外沿，不与“已选”左边的实条混在一起。
            if (Focused && focus >= 0 && focus < rows.Count)
            {
                int fy = focus * RowH + 4;
                using (Pen p = new Pen(Theme.Ink, 1f))
                {
                    p.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(p, 1, fy + 1, Math.Max(1, W - 3), RowH - 2);
                }
            }

            // 按下还没拖动的瞬间：先把要搬的那行描出来 —— 用户按下去就知道抓住的是哪行，
            // 而不是拖了一半啥都没有
            if (pressArm && !dragging && pressRow >= 0 && pressRow < rows.Count)
            {
                int py = pressRow * RowH + 4;
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(40, Theme.Info)))
                using (Pen p = new Pen(Theme.Info, 2f))
                {
                    g.FillRectangle(fill, 1, py, Math.Max(1, W - 3), RowH);
                    g.DrawRectangle(p, 1, py + 1, Math.Max(1, W - 4), RowH - 2);
                }
            }

            // 拖动中的反馈：有预览 = 树已是松手后的样子，搬的块垫淡色、插入处一条线加箭头；
            // 没预览（还没找到合法落点）= 退回旧画法：落点行铺底色、源行压暗
            if (dragging)
            {
                if (previewRows != null && pvIns >= 0)
                {
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(48, Theme.Info)))
                    using (Pen pl = new Pen(Theme.Info, 2f))
                    using (SolidBrush b = new SolidBrush(Theme.Info))
                    {
                        int y0 = pvIns * RowH + 4, y1 = pvB1 * RowH + 4 + RowH;
                        g.FillRectangle(fill, 1, y0, Math.Max(1, W - 3), y1 - y0);
                        g.DrawLine(pl, 2, y0 - 2, W - 6, y0 - 2);
                        g.FillPolygon(b, new Point[] { new Point(2, y0 - 2), new Point(9, y0 - 7), new Point(9, y0 + 3) });
                    }
                }
                else
                {
                    if (dropRow >= 0 && dropRow < rows.Count && dropMode > 0)
                    {
                        int dy = dropRow * RowH + 4;
                        Rectangle rowRc = new Rectangle(1, dy, Math.Max(1, W - 3), RowH);
                        using (SolidBrush fill = new SolidBrush(Color.FromArgb(64, Theme.Info)))
                        using (Pen p = new Pen(Theme.Info, 2f))
                        using (SolidBrush b = new SolidBrush(Theme.Info))
                        {
                            if (dropMode == 1)                    // 上半行：插到这一行前面
                            {
                                g.FillRectangle(fill, rowRc.X, rowRc.Y, rowRc.Width, rowRc.Height / 2);
                                g.DrawLine(p, 2, dy - 1, W - 6, dy - 1);
                                g.FillPolygon(b, new Point[] { new Point(2, dy - 1), new Point(9, dy - 6), new Point(9, dy + 4) });
                            }
                            else if (dropMode == 2)               // 下半行：插到这一行后面
                            {
                                int half = rowRc.Height / 2;
                                g.FillRectangle(fill, rowRc.X, dy + half, rowRc.Width, rowRc.Height - half);
                                g.DrawLine(p, 2, dy + RowH - 1, W - 6, dy + RowH - 1);
                                g.FillPolygon(b, new Point[] { new Point(2, dy + RowH - 1), new Point(9, dy + RowH - 6), new Point(9, dy + RowH + 4) });
                            }
                            else                                  // 整行：放进它下面（成为子分支）
                            {
                                g.FillRectangle(fill, rowRc);
                                g.DrawRectangle(p, 2, dy + 1, Math.Max(1, W - 8), RowH - 3);
                            }
                        }
                    }
                    // 拖起来的那一行压暗：明确「搬的是这一行」，不然拖到一半就分不清了
                    if (pressRow >= 0 && pressRow < rows.Count)
                    {
                        int sy = pressRow * RowH + 4;
                        using (SolidBrush mask = new SolidBrush(Color.FromArgb(150, Theme.TreeBg)))
                            g.FillRectangle(mask, 1, sy, Math.Max(1, W - 3), RowH);
                    }
                }
                DrawDragTag(g, W);
            }

            // 未归类 MOD 拖进来：高亮落点行 + 跟随光标的小标签（与上面的行拖动互斥，不会同屏）
            if (extDrag) DrawExtTag(g, W);
        }

        /// <summary>
        /// 未归类 MOD 悬在树上：落点行整行填色（合法=信息蓝、非法=警示红），
        /// 光标边上的标签写清「松手归到哪 / 替换哪个」。只描边不填色用户看不见，必须有底色。
        /// </summary>
        private void DrawExtTag(Graphics g, int W)
        {
            bool inRow = extRow >= 0 && extRow < rows.Count;
            bool ok = inRow && ExtAccepts(rows[extRow]);
            if (inRow)
            {
                // 与自绘行拖动同一种画法：淡色铺满整行 + 2px 描边，隔着字也看得清
                Rectangle rr = new Rectangle(2, extRow * RowH + 4, Math.Max(1, W - 6), RowH - 1);
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(ok ? 60 : 45, ok ? Theme.Info : Theme.Accent)))
                using (Pen pen = new Pen(ok ? Theme.Info : Theme.Accent, 2f))
                {
                    g.FillRectangle(fill, rr);
                    g.DrawRectangle(pen, rr);
                }
            }

            string to;
            if (ok)
            {
                TreeRow r = rows[extRow];
                if (r.Slot != null)
                {
                    if (CatTree.IsBranchSlot(r.Slot)) to = " → 放进分支「" + r.Slot.Name + "」";
                    else if (r.Slot.Pile) to = " → 归到「" + r.Slot.Cat + "」这一堆";
                    else to = " → 替换「" + r.Slot.Cat + " · " + r.Slot.Name + "」";
                }
                else if (r.IsSub) to = " → 只改归类为「" + r.Cat + " / " + r.Sub + "」（位置不变）";
                else to = " → 只改归类为「" + r.Cat + "」（位置不变）";
            }
            else to = inRow ? " → 这里放不了，松手取消" : " → 松手取消";
            string txt = "「" + (extName.Length > 0 ? extName : "?") + "」" + to;
            Size sz = TextRenderer.MeasureText(txt, Theme.UISmall, new Size(2000, 40),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int w = Math.Min(Math.Max(60, W - 12), sz.Width + 18), h = 22;
            int x = Math.Max(2, Math.Min(Math.Max(2, W - w - 4), extPt.X + 14));
            int y = Math.Max(2, Math.Min(Math.Max(2, Height - h - 2), extPt.Y + 10));
            Rectangle box = new Rectangle(x, y, w, h);
            using (GraphicsPath p = Theme.Round(box, 4))
            using (SolidBrush b = new SolidBrush(Theme.Surface))
            using (Pen pen = new Pen(ok ? Theme.Info : Theme.Accent, ok ? 1f : 2f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, txt, Theme.UISmall,
                new Rectangle(box.X + 8, box.Y, box.Width - 12, box.Height), Theme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        /// <summary>拖动时跟着鼠标的小标签：写着搬的是哪个分支、松手会落到哪儿。</summary>
        private void DrawDragTag(Graphics g, int W)
        {
            string to;
            bool bad = dropMode <= 0;          // 没有合法落点：标签转红，把话说透
            if (dropRow >= 0 && dropRow < rows.Count)
            {
                TreeRow dr = rows[dropRow];
                if (dr.IsCat)
                    to = dropMode == 1 ? " → 移到「" + dr.Cat + "」前面"
                       : dropMode == 2 ? " → 移到「" + dr.Cat + "」后面" : "";
                else if (dr.Slot != null)
                {
                    string tn = dr.Slot.Name;
                    to = dropMode == 1 ? " → 插到「" + tn + "」前面"
                       : dropMode == 2 ? " → 插到「" + tn + "」后面"
                       : dropMode == 3 ? " → 放进「" + tn + "」"
                       : "";
                }
                else to = " → 松手取消";
            }
            else to = " → 松手取消";
            if (bad) to = " → 松手取消（这里放不了，看底部提示）";
            string txt = dragName + to;
            Size sz = TextRenderer.MeasureText(txt, Theme.UISmall, new Size(2000, 40),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int w = Math.Min(Math.Max(60, W - 12), sz.Width + 18), h = 22;
            int x = Math.Max(2, Math.Min(Math.Max(2, W - w - 4), dragPt.X + 14));
            int y = Math.Max(2, Math.Min(Math.Max(2, Height - h - 2), dragPt.Y + 10));
            Rectangle box = new Rectangle(x, y, w, h);
            using (GraphicsPath p = Theme.Round(box, 4))
            using (SolidBrush b = new SolidBrush(Theme.Surface))
            using (Pen pen = new Pen(bad ? Theme.Accent : Theme.Info, bad ? 2f : 1f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, txt, Theme.UISmall,
                new Rectangle(box.X + 8, box.Y, box.Width - 12, box.Height), Theme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static void DrawTri(Graphics g, int cx, int cy, bool open)
        {
            using (SolidBrush b = new SolidBrush(Theme.Ink3))
            {
                if (open)
                {
                    Point[] p = { new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3) };
                    g.FillPolygon(b, p);
                }
                else
                {
                    Point[] p = { new Point(cx - 2, cy - 4), new Point(cx - 2, cy + 4), new Point(cx + 3, cy) };
                    g.FillPolygon(b, p);
                }
            }
        }

        /// <summary>「扩」小角标：蓝底白字，挂在名字后面，表示这个位置有可叠加的扩展（信息蓝，不是冲突红）。</summary>
        private static void DrawTag(Graphics g, int x, int y)
        {
            const int w = 17, h = 16;
            int iy = y + (RowH - h) / 2;
            using (SolidBrush b = new SolidBrush(Theme.Info))
            {
                using (GraphicsPath p = new GraphicsPath())
                {
                    int r = 3;
                    p.AddArc(x, iy, r * 2, r * 2, 180, 90);
                    p.AddArc(x + w - r * 2, iy, r * 2, r * 2, 270, 90);
                    p.AddArc(x + w - r * 2, iy + h - r * 2, r * 2, r * 2, 0, 90);
                    p.AddArc(x, iy + h - r * 2, r * 2, r * 2, 90, 90);
                    p.CloseFigure();
                    g.FillPath(b, p);
                }
            }
            TextRenderer.DrawText(g, "扩", Theme.UISmall, new Rectangle(x, iy - 1, w, h + 2), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private int IndexAt(int y)
        {
            // 绘制时第一行从 y=4 开始，命中区也必须扣除同样的顶部偏移。
            if (y < 4) return -1;
            int i = (y - 4) / RowH;
            return (i >= 0 && i < rows.Count) ? i : -1;
        }

        // ------------------------------------------------------------
        //  未归类 MOD 拖进树：拖到哪一行，就归到哪一行
        // ------------------------------------------------------------

        /// <summary>拖进来的 MOD 能落在哪一行：位置行（含分支）、大类 / 小分类标题。说明行和「未归类」桶不行。</summary>
        private static bool ExtAccepts(TreeRow r)
        {
            if (r == null) return false;
            if (r.IsCat) return !string.IsNullOrEmpty(r.Cat) && r.Cat != "未归类";
            if (r.IsSub) return !string.IsNullOrEmpty(r.Cat);
            return r.Slot != null;      // 位置行 / 我的分支 / ＋新建分支交给 DropModOn 分流
        }

        private void ExtDragOver(object s, DragEventArgs e)
        {
            List<ModGroup> gs = e.Data.GetData(typeof(List<ModGroup>)) as List<ModGroup>;
            if (gs == null)
            {
                ModGroup one = e.Data.GetData(typeof(ModGroup)) as ModGroup;
                if (one != null) gs = new List<ModGroup> { one };
            }
            if (gs == null || gs.Count == 0) { if (extDrag) ExtDragEnd(); e.Effect = DragDropEffects.None; return; }

            extGroups = gs;
            extName = gs.Count > 1 ? Lang.F("{0} 个 MOD", gs.Count) : gs[0].Name;
            extPt = PointToClient(new Point(e.X, e.Y));
            int row = IndexAt(extPt.Y);
            e.Effect = (row >= 0 && row < rows.Count && ExtAccepts(rows[row]))
                ? DragDropEffects.Move : DragDropEffects.None;

            if (!extDrag) { extScroll.Start(); owner.StartWheelHook(); }
            extDrag = true;
            if (row != extRow)
            {
                extRow = row;
                extExpand.Stop();
                extExpandRow = row;
                // 悬停折叠的分类：停 0.6 秒自动摊开，省得先松手、点开、再拖一次
                if (row >= 0 && row < rows.Count)
                {
                    TreeRow rr = rows[row];
                    if ((rr.IsCat && owner.IsFolded(rr.Cat)) || (rr.IsSub && owner.IsSubFolded(rr.Cat, rr.Sub))) extExpand.Start();
                }
                Invalidate();
            }
        }

        /// <summary>外部拖拽（未归类 MOD 拖进树）正在发生：滚轮要转给树用。</summary>
        internal bool ExtDragging { get { return extDrag; } }

        /// <summary>拖拽中滚轮滚动树（delta 正 = 向上滚）。</summary>
        internal void WheelScroll(int delta)
        {
            ScrollHost host = Parent as ScrollHost;
            if (host == null || !host.CanScroll) return;
            int lines = Math.Max(1, Math.Abs(delta) / 120) * 3;
            host.ScrollTo(host.Offset - Math.Sign(delta) * Dpi.S(Theme.RowH) * lines);
        }

        private void ExtDragEnd()
        {
            extDrag = false;
            extRow = -1;
            extExpandRow = -1;
            extScroll.Stop();
            extExpand.Stop();
            owner.StopWheelHook();
            Invalidate();
        }

        /// <summary>光标悬在视口上 / 下边一个行高以内时自动滚树——不然折叠状态下够不着下面的位置。</summary>
        private void ExtAutoScroll()
        {
            ScrollHost host = Parent as ScrollHost;
            if (host == null || !extDrag) { extScroll.Stop(); return; }
            int vh = host.ClientSize.Height;
            if (vh <= 0) return;
            // extPt 是树体坐标：视口上沿在树体坐标里是 offset，下沿是 offset + 视口高
            // 越靠近边缘滚得越快：进入 3 行高的感应带就开始滚，一 tick 1~3 行
            int band = Math.Max(Dpi.S(24), RowH * 3);
            int dTop = extPt.Y - host.Offset;
            int dBottom = host.Offset + vh - extPt.Y;
            if (dTop < band) host.ScrollTo(host.Offset - Math.Max(Dpi.S(6), (band - dTop) / 3));
            else if (dBottom < band) host.ScrollTo(host.Offset + Math.Max(Dpi.S(6), (band - dBottom) / 3));
        }

        private void ExtDragDrop(object s, DragEventArgs e)
        {
            List<ModGroup> gs = extGroups;
            if (gs == null || gs.Count == 0)
            {
                ModGroup one = e.Data.GetData(typeof(ModGroup)) as ModGroup;
                if (one != null) gs = new List<ModGroup> { one };
            }
            bool ok = gs != null && gs.Count > 0 && extRow >= 0 && extRow < rows.Count && ExtAccepts(rows[extRow]);
            TreeRow r = ok ? rows[extRow] : null;
            ExtDragEnd();
            if (ok) owner.DropModsOn(gs, r);
            extGroups = new List<ModGroup>();
        }

        /// <summary>自测用：不发起真 OLE 拖放，直接走一遍「松手在 row」的落点逻辑，返回结果描述。</summary>
        internal string SimulateExtDrop(int row, ModGroup g)
        {
            if (g == null) return "没有拖的东西";
            if (row < 0 || row >= rows.Count) return "行号越界 " + row;
            if (!ExtAccepts(rows[row])) return "这一行不收：" + RowDesc(rows[row]);
            owner.DropModOn(g, rows[row]);
            return "ok → " + RowDesc(rows[row]);
        }

        /// <summary>自测用：外部 OLE 拖放悬停在某一行时的状态串。</summary>
        internal string ExtState() { return "extDrag=" + extDrag + " extRow=" + extRow; }

        /// <summary>截图专用：摆成「正把未归类 MOD 悬在第 row 行」的样子，不动任何数据。</summary>
        public void SetExtDragFake(string name, int row)
        {
            extName = name ?? "?";
            extRow = row;
            extDrag = row >= 0;
            extPt = new Point(Width / 2, Math.Max(8, row * RowH + RowH / 2));
            Invalidate();
        }

        /// <summary>这一行能不能按住拖：大类 / 小分类 / 分支 / 位置都能，只有说明行和「一堆」不行。</summary>
        private static bool CanDragRow(TreeRow r)
        {
            if (r == null) return false;
            // 「未归类」是兜底桶，位置由 BuildRows 钉在最后，拖它没有落点 —— 干脆别给拖动的光标
            if (r.IsCat) return r.Cat != "未归类";
            if (r.IsSub) return true;
            if (r.Slot == null) return false;
            if (CatTree.IsBranchSlot(r.Slot)) return true;
            return !r.Slot.Pile;          // 「一堆」「＋ 新建分支…」这类不参与排序
        }

        /// <summary>日志里怎么称呼这一行。</summary>
        private static string RowDesc(TreeRow r)
        {
            if (r == null) return "（空）";
            if (r.IsCat) return "大类「" + r.Cat + "」";
            if (r.IsSub) return "小分类「" + r.Cat + " / " + r.Sub + "」";
            if (r.Slot != null) return "位置「" + r.Slot.Name + "」";
            return "说明行";
        }

        /// <summary>某一行属于哪个大类 / 小分类。大类标题行只给得出大类。</summary>
        private static void SubOfRow(TreeRow r, out string cat, out string sub)
        {
            cat = null; sub = null;
            if (r == null) return;
            if (r.Slot != null) { cat = r.Slot.Cat; sub = r.Slot.Sub; return; }
            if (r.IsSub) { cat = r.Cat; sub = r.Sub; return; }
            if (r.IsCat) cat = r.Cat;
        }

        /// <summary>小分类标题行的下标；只有一个分组时树里不画那行标题，返回 -1 由调用方兜底。</summary>
        internal int RowIndexOfSub(string cat, string sub)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].IsSub && rows[i].Cat == cat && rows[i].Sub == sub) return i;
            return -1;
        }

        /// <summary>小分类标题行；没有标题行（单分组大类）就退回这一段的第一行位置。</summary>
        internal int RowIndexOfSubOrFirst(string cat, string sub)
        {
            int i = RowIndexOfSub(cat, sub);
            if (i >= 0) return i;
            for (int k = 0; k < rows.Count; k++)
                if (rows[k].Slot != null && rows[k].Slot.Cat == cat && rows[k].Slot.Sub == sub) return k;
            return -1;
        }

        internal int RowIndexOfPos(string cat, string sub, string name)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Slot s = rows[i].Slot;
                if (s != null && s.Cat == cat && s.Sub == sub && s.Name == name) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                DragAutoScroll(e.Y);
                dragPt = e.Location;
                // 命中按「看到的树」算：有预览时用户看到的是松手后的行序
                List<TreeRow> view = previewRows != null ? previewRows : rows;
                int p = (e.Y < 4) ? -1 : Math.Min((e.Y - 4) / RowH, view.Count - 1);
                // 悬在自己（源行 / 预览里的幽灵块）上 = 不解析，免得起拖瞬间乱跳
                bool onSelf = (previewRows == null && p == pressRow)
                    || (previewRows != null && p >= pvIns && p <= pvB1);
                int t = -1, mode = 0;
                // 目标用名字记，最后再翻译回 rows 下标（拖完 Rebuild 会换掉整个 rows）
                string tCat = null, tBr = null, tSub = null, tPos = null;
                if (p >= 0 && !onSelf)
                {
                    TreeRow vr = view[p];
                    if (dragKind == 2)
                    {
                        // 大类拖大类：整个大类的地盘都是落点，上半地盘 = 它前面，下半地盘 = 它后面
                        // （按整块高度对半分，展开的大类不再只会判「后面」）
                        int ct = p;
                        while (ct >= 0 && !view[ct].IsCat) ct--;
                        if (ct >= 0 && view[ct].Cat != "未归类"
                            && !string.Equals(view[ct].Cat, dragName, StringComparison.OrdinalIgnoreCase))
                        {
                            tCat = view[ct].Cat;
                            int be = ct;
                            while (be + 1 < view.Count && !view[be + 1].IsCat) be++;
                            int midY = 4 + (ct + (be - ct + 1) / 2) * RowH;
                            mode = e.Y < midY ? 1 : 2;
                        }
                    }
                    else if (dragKind == 3)
                    {
                        // 小分类：只能在同一个大类里换顺序。落点是小分类标题行、或它底下的位置行都算
                        string c, sb;
                        SubOfRow(vr, out c, out sb);
                        if (sb != null && string.Equals(c, dragCat, StringComparison.Ordinal)
                            && !string.Equals(sb, dragName, StringComparison.Ordinal))
                        {
                            tSub = sb;
                            mode = e.Y - 4 - p * RowH < RowH * 30 / 100 ? 1 : 2;
                        }
                    }
                    else if (dragKind == 4)
                    {
                        // 位置：落在位置行上 = 插到它前 / 后（跨小分类时顺便换过去）；
                        // 落在小分类标题上 = 放到那一段的开头 / 末尾。
                        string c, sb;
                        SubOfRow(vr, out c, out sb);
                        if (sb != null && string.Equals(c, dragCat, StringComparison.Ordinal))
                        {
                            int ry = e.Y - 4 - p * RowH;
                            bool onSlot = vr.Slot != null && !CatTree.IsBranchSlot(vr.Slot);
                            if (onSlot && string.Equals(vr.Slot.Name, dragName, StringComparison.Ordinal))
                            {
                                tSub = null;                       // 落回自己身上：不解析
                            }
                            else
                            {
                                tSub = sb;
                                if (onSlot) tPos = vr.Slot.Name;
                                mode = ry < RowH * (onSlot ? 30 : 50) / 100 ? 1 : 2;
                            }
                        }
                    }
                    else if (dragKind == 1 && vr.Slot != null && CatTree.IsBranchSlot(vr.Slot)
                        && !string.Equals(vr.Slot.Name, dragName, StringComparison.OrdinalIgnoreCase)
                        && !IsDescendantBranch(vr.Slot.Name, dragName))          // 自己的子分支接不住（成环）
                    {
                        tBr = vr.Slot.Name;
                        int ry = e.Y - 4 - p * RowH;
                        mode = ry < RowH * 30 / 100 ? 1 : ry > RowH * 70 / 100 ? 2 : 3;
                    }
                    else if (dragKind == 1 && vr.Slot != null && string.Equals(vr.Slot.Cat, "我的分支", StringComparison.Ordinal)
                        && !string.Equals(vr.Slot.Name, dragName, StringComparison.OrdinalIgnoreCase)
                        && !IsDescendantBranch(vr.Slot.Name, dragName))
                    {
                        // 分支拖到「我的分支」地盘的非分支行上：找上面最近的分支行，插到它后面
                        int bt = p;
                        while (bt >= 0 && !(view[bt].Slot != null && CatTree.IsBranchSlot(view[bt].Slot))) bt--;
                        if (bt >= 0 && !string.Equals(view[bt].Slot.Name, dragName, StringComparison.OrdinalIgnoreCase))
                        {
                            tBr = view[bt].Slot.Name;
                            mode = 2;
                        }
                    }
                    // 都没接住：吸附到视图里最近的合法落点，让随便松哪儿都有个确定的结果
                    if (mode == 0)
                    {
                        if (dragKind == 2)
                        {
                            int up = -1, down = -1;
                            for (int k = p - 1; k >= 0; k--)
                                if (view[k].IsCat && view[k].Cat != "未归类"
                                    && !string.Equals(view[k].Cat, dragName, StringComparison.OrdinalIgnoreCase)) { up = k; break; }
                            for (int k = p + 1; k < view.Count; k++)
                                if (view[k].IsCat && view[k].Cat != "未归类"
                                    && !string.Equals(view[k].Cat, dragName, StringComparison.OrdinalIgnoreCase)) { down = k; break; }
                            if (up >= 0 && (down < 0 || p - up <= down - p)) { tCat = view[up].Cat; mode = 2; }
                            else if (down >= 0) { tCat = view[down].Cat; mode = 1; }
                        }
                        else if (dragKind == 1)
                        {
                            int up = -1, down = -1;
                            for (int k = p - 1; k >= 0; k--)
                                if (view[k].Slot != null && CatTree.IsBranchSlot(view[k].Slot)
                                    && !string.Equals(view[k].Slot.Name, dragName, StringComparison.OrdinalIgnoreCase)) { up = k; break; }
                            for (int k = p + 1; k < view.Count; k++)
                                if (view[k].Slot != null && CatTree.IsBranchSlot(view[k].Slot)
                                    && !string.Equals(view[k].Slot.Name, dragName, StringComparison.OrdinalIgnoreCase)) { down = k; break; }
                            if (up >= 0 && (down < 0 || p - up <= down - p)) { tBr = view[up].Slot.Name; mode = 2; }
                            else if (down >= 0) { tBr = view[down].Slot.Name; mode = 1; }
                        }
                        else
                        {
                            // 小分类 / 位置：吸附到同一个大类里最近的一段，别让「松在空白处」变成没反应
                            for (int k = p; k >= 0 && mode == 0; k--)
                            {
                                string c, sb;
                                SubOfRow(view[k], out c, out sb);
                                if (sb == null || !string.Equals(c, dragCat, StringComparison.Ordinal)) continue;
                                if (dragKind == 4 && view[k].Slot != null && !CatTree.IsBranchSlot(view[k].Slot))
                                {
                                    if (string.Equals(view[k].Slot.Name, dragName, StringComparison.Ordinal)) continue;
                                    tPos = view[k].Slot.Name;
                                }
                                tSub = sb; mode = 2;
                            }
                            for (int k = p + 1; k < view.Count && mode == 0; k++)
                            {
                                string c, sb;
                                SubOfRow(view[k], out c, out sb);
                                if (sb == null || !string.Equals(c, dragCat, StringComparison.Ordinal)) continue;
                                if (dragKind == 4 && view[k].Slot != null && !CatTree.IsBranchSlot(view[k].Slot))
                                {
                                    if (string.Equals(view[k].Slot.Name, dragName, StringComparison.Ordinal)) continue;
                                    tPos = view[k].Slot.Name;
                                }
                                tSub = sb; mode = 1;
                            }
                        }
                    }
                }
                // 原地不动 → 翻到目标的另一侧：拖紧邻的邻居本来就在那个位置，
                // 用户要的是「换位置」，直接对调才看得出挪动（用户实测踩了两次）
                if (mode != 0)
                {
                    if (dragKind == 2)
                    {
                        List<string> cur = CatPrefs.Cats();
                        int ci = cur.IndexOf(dragName), ti = cur.IndexOf(tCat);
                        if (ci >= 0 && ti >= 0 && ((mode == 2 && ci == ti + 1) || (mode == 1 && ci == ti - 1)))
                            mode = mode == 2 ? 1 : 2;
                    }
                    else if (dragKind == 1)
                    {
                        List<string> cur = MyBranch.Names();
                        int ci = cur.IndexOf(dragName), ti = cur.IndexOf(tBr);
                        if (ci >= 0 && ti >= 0 && ((mode == 2 && ci == ti + 1) || (mode == 1 && ci == ti - 1)))
                            mode = mode == 2 ? 1 : 2;
                    }
                    else if (dragKind == 3)
                    {
                        List<string> cur = owner.CurrentSubs(dragCat);
                        int ci = cur.IndexOf(dragName), ti = cur.IndexOf(tSub);
                        if (ci >= 0 && ti >= 0 && ((mode == 2 && ci == ti + 1) || (mode == 1 && ci == ti - 1)))
                            mode = mode == 2 ? 1 : 2;
                    }
                    else if (dragKind == 4)
                    {
                        // 同一个小分类里拖紧邻的邻居，本来就在那个位置 → 翻到另一侧才对调得动
                        if (!string.IsNullOrEmpty(tPos))
                        {
                            List<string> cur = owner.CurrentPositions(dragCat, tSub);
                            int ci = cur.IndexOf(dragName), ti = cur.IndexOf(tPos);
                            if (ci >= 0 && ti >= 0 && ((mode == 2 && ci == ti + 1) || (mode == 1 && ci == ti - 1)))
                                mode = mode == 2 ? 1 : 2;
                        }
                    }
                }
                // 名字 → rows 下标（松手落位、小标签都吃 rows 空间）
                if (mode != 0)
                {
                    if (dragKind == 2) t = RowIndexOfCat(tCat);
                    else if (dragKind == 1) t = RowIndexOfBranch(tBr);
                    else if (dragKind == 3) t = RowIndexOfSubOrFirst(dragCat, tSub);
                    else t = string.IsNullOrEmpty(tPos)
                        ? RowIndexOfSubOrFirst(dragCat, tSub)
                        : RowIndexOfPos(dragCat, tSub, tPos);
                    if (t < 0) mode = 0;
                }
                if (t != dropRow || mode != dropMode)
                {
                    dropRow = t; dropMode = mode;
                    previewRows = null; pvIns = -1; pvB1 = -1;
                    if (mode > 0)
                        BuildPreview(dragKind, tCat, tSub, tPos, tBr, mode);
                }
                Invalidate();                 // 小标签跟着手走，整块重绘最省事
                return;
            }
            // 按住分支行 / 大类标题行挪开几个像素 = 开始拖动（不是点击）
            if (pressArm && pressRow >= 0 && pressRow < rows.Count
                && (e.Button & MouseButtons.Left) == MouseButtons.Left
                && Math.Abs(e.X - pressPt.X) + Math.Abs(e.Y - pressPt.Y) >= 4)
            {
                dragging = true;
                TreeRow src = rows[pressRow];
                dragKind = src.IsCat ? 2 : src.IsSub ? 3 : CatTree.IsBranchSlot(src.Slot) ? 1 : 4;
                dragCat = src.IsCat || src.IsSub ? src.Cat : src.Slot.Cat;
                dragSub = src.IsSub ? src.Sub : (src.Slot != null ? src.Slot.Sub : "");
                dragName = src.IsCat ? src.Cat : src.IsSub ? src.Sub : src.Slot.Name;
                dragPt = e.Location;
                dropRow = pressRow;
                dropMode = 0;
                Cursor = Cursors.SizeAll;
                tip.RemoveAll();
                tipRow = -2;
                Core.Log("INFO", "DRAGIN 开始拖动 " + RowDesc(src) + "（kind=" + dragKind + "）");
                if (dragKind == 1 && MyBranch.Names().Count < 2)
                    owner.KeepFoot("「我的分支」里只有这一个分支，拖它没有可去的位置 —— 想试拖动，先右键空白处新建一个分支，或者拖上面的大类试试");
                Invalidate();
                return;
            }
            // 诊断：按住还没拖成时，每 ~150ms 记一次真实移动 —— 真机输入到没到，一看便知
            if (pressArm && !dragging && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                int now = Environment.TickCount;
                if (now - lastMoveLog >= 150)
                {
                    lastMoveLog = now;
                    Core.Log("INFO", "DRAGIN 按住未起拖 位移="
                        + (Math.Abs(e.X - pressPt.X) + Math.Abs(e.Y - pressPt.Y)) + " 键=" + e.Button);
                }
            }
            int i = IndexAt(e.Y);
            if (i != hover)
            {
                hover = i;
                // 悬停在可拖行上时给个十字光标：不用按下去就知道这儿能拖
                bool canDrag = i >= 0 && i < rows.Count && CanDragRow(rows[i]);
                Cursor = canDrag ? Cursors.SizeAll : Cursors.Hand;
                Invalidate(); UpdateTip(i);
            }
            base.OnMouseMove(e);
        }

        /// <summary>拖到上下边缘时自动滚动，不然长树里搬东西得先松手、滚一屏、再拖一次。</summary>
        private void DragAutoScroll(int y)
        {
            ScrollHost p = Parent as ScrollHost;
            if (p == null || !p.CanScroll) return;
            // 这里以前拿 Height（整棵树的内容高）当视口底边，只有拖到内容最末一行才会滚；
            // 要按「视口」算：树体坐标 y 落在视口上下边一个行高内才滚。
            int vh = p.ClientSize.Height;
            if (vh <= 0) return;
            if (y - p.Offset < RowH / 2) p.ScrollTo(p.Offset - RowH);
            else if (p.Offset + vh - y < RowH / 2) p.ScrollTo(p.Offset + RowH);
        }

        // 悬停说明：这行有几个本体候选、有没有可叠的扩展、有没有组合装。
        // 右端的「+2」再显眼也得有个地方讲清它是什么意思，就放这里。
        private void UpdateTip(int i)
        {
            if (i == tipRow) return;
            tipRow = i;
            string t = "";
            if (i >= 0 && i < rows.Count)
            {
                Slot s = rows[i].Slot;
                if (s != null && s.Empty)
                {
                    t = "这个位置还没有 MOD\r\n把 vpk 放进 MOD 库（或游戏 addons），重新扫描就会出现在这里";
                }
                else if (s != null)
                {
                    t = "本体候选 " + s.Items.Count + " 个";
                    bool hasSet = false;
                    foreach (ModGroup g in s.Items) if (g.IsSet) { hasSet = true; break; }
                    if (hasSet) t += "\r\n候选里有组合装：双击那行可挑部件（材质 / 颜色）";
                }
            }
            if (t.Length == 0) tip.RemoveAll(); else tip.SetToolTip(this, t);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!dragging && !pressArm) Cursor = Cursors.Hand;
            if (hover != -1) { hover = -1; Invalidate(); }
            tipRow = -2;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexAt(e.Y);
            if (i < 0)
            {
                // 空白处也能出菜单：建分支不该要求先滚到「我的分支」那几行
                if (e.Button == MouseButtons.Right) owner.TreeMenuFor(null, PointToScreen(e.Location));
                else if (e.Button == MouseButtons.Left)
                {
                    // 点空白 = 取消焦点。以前这里直接 return，上一行的焦点框一直挂着，
                    // 看着像「这行还选着」，按方向键又从那行接着走，跟点空白前没区别。
                    // 顺序要紧：Focus() 会触发 OnGotFocus，那里发现 focus<0 会把它找回到
                    // 上次选中的槽位，先清后拿焦点就白清了。
                    Focus();
                    if (focus != -1) { int old = focus; focus = -1; InvalidateRow(old); }
                }
                return;
            }
            TreeRow r = rows[i];
            Focus();
            focus = i;
            Invalidate();
            // 右键：分支的重命名 / 解散 / 新建子分支，以及任意地方「新建分支…」
            if (e.Button == MouseButtons.Right)
            {
                owner.TreeMenuFor(r, PointToScreen(e.Location));
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                // 「地图」这类可选的类标题行：点左边三角 = 折叠；点文字 = 直接选中容器。
                // 这里不能走「按下记起点、松手再激活」那套：类标题行本来就能拖（换大类顺序），
                // 一动阈值就被判成拖动，结果点了没反应 —— 用户实测「点不开」。
                if (r.IsCat && r.Slot != null)
                {
                    if (e.X < 26) owner.SetFolded(r.Cat, !owner.IsFolded(r.Cat));
                    else owner.SelectSlot(r.Slot.Key);
                    return;
                }
                // 记下起点：大类 / 小分类 / 分支 / 位置行都能接着拖，别的行（「未归类」说明行）照旧只当点击
                pressArm = CanDragRow(r);
                pressRow = pressArm ? i : -1;
                pressPt = e.Location;
                // 激活（折叠 / 选中）推迟到 MouseUp：按下就做的话行会立刻重建，拖动根本起不来
                if (!pressArm) ActivateRow(i);
                // 可拖行按下立刻高亮（OnPaint 里画），并把真实输入记进日志：
                // 有用户反馈「拖不动又看不出抓没抓住」，这两样是远程定位的唯一手段
                if (pressArm)
                {
                    Core.Log("INFO", "DRAGIN 按下 行=" + i + " " + RowDesc(r) + " 键=" + e.Button);
                    Cursor = Cursors.SizeAll;
                    InvalidateRow(i);
                    // 按下就把鼠标抓住：不然拖到控件外（左栏、窗口边缘之外）松手时，
                    // MouseUp 根本发不到这里，dragging 会一直挂着 —— 光标卡在十字上、
                    // 树还跟着鼠标画预览，下一次点击会拿上一次的落点去搬东西。
                    Capture = true;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                // 先把落点抄下来：ApplyDrop 里会 Rebuild，rows 整个换掉
                string nm = dragName, fcat = dragCat, fsub = dragSub;
                int kind = dragKind, row = dropRow, mode = dropMode;
                ResetDrag();
                Core.Log("INFO", "DRAGIN 松手 「" + nm + "」 落点行=" + row + " 模式=" + mode + " kind=" + kind);
                if (!ApplyDrop(nm, kind, row, mode, fcat, fsub))
                    owner.KeepFoot(kind == 2
                        ? "没挪动：拖到别的大类的地盘上（上半 = 移到它前面，下半 = 移到它后面）"
                        : kind == 3
                        ? "没挪动：小分类只能在同一大类里换顺序（拖到另一个小分类的上 / 下半）"
                        : kind == 4
                        ? "没挪动：位置拖到另一个位置的上 / 下半换顺序；拖到别的小分类标题上 = 放进那一段"
                        : "没挪动：拖到另一个分支行附近（上半 / 下半 = 换顺序，中间 = 变成子分支）");
                return;
            }
            // 没拖成 = 普通的一下点击：折叠 / 展开 / 选中，走的还是同一套激活逻辑
            if (pressArm && pressRow >= 0 && pressRow < rows.Count)
                ActivateRow(pressRow);
            if (pressArm) { Cursor = Cursors.Hand; InvalidateRow(pressRow); }   // 撤掉按下高亮
            pressArm = false;
            pressRow = -1;
            if (Capture) Capture = false;
            base.OnMouseUp(e);
        }

        /// <summary>
        /// 拖到窗口外松手、Alt+Tab、被别的窗口抢走焦点 —— 这几种情况下 MouseUp 不会送到这里，
        /// 但系统会先收回鼠标捕获。趁这个时机把拖动状态清干净，别让它挂在那儿。
        /// </summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture) return;                       // 自己设的捕获，不是被收走
            if (!dragging && !pressArm) return;
            Core.Log("WARN", "拖动被中断（失去鼠标捕获），已取消本次拖拽");
            ResetDrag();
            owner.KeepFoot("拖动已取消：鼠标离开了窗口，或者窗口被切走了。要挪动请重新按住再拖");
        }

        /// <summary>
        /// 松手落位：按落点行和模式把分支挪过去。上 30% / 下 30% = 插到这一行前 / 后，中间 = 变成它的子分支。
        /// 鼠标松手和自检走的是同一条路 —— 免得「手能拖、测不到」。
        /// </summary>
        internal bool ApplyDrop(string name, int row, int mode)
        {
            return ApplyDrop(name, 1, row, mode);
        }

        /// <summary>rows 空间里某个大类标题行的下标（没有返回 -1）。</summary>
        private int RowIndexOfCat(string cat)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].IsCat && rows[i].Cat == cat) return i;
            return -1;
        }

        /// <summary>rows 空间里某个分支行的下标（没有返回 -1）。</summary>
        private int RowIndexOfBranch(string name)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Slot != null && CatTree.IsBranchSlot(rows[i].Slot) && rows[i].Slot.Name == name) return i;
            return -1;
        }

        /// <summary>分支 name 是不是 anc 的后代（防成环、防拖进自己的地盘）。</summary>
        private static bool IsDescendantBranch(string name, string anc)
        {
            string par = MyBranch.ParentOf(name);
            int guard = 0;
            while (par.Length > 0 && guard++ < 32)
            {
                if (par == anc) return true;
                par = MyBranch.ParentOf(par);
            }
            return false;
        }

        private static int PreviewRowOfCat(List<TreeRow> pv, string cat)
        {
            for (int i = 0; i < pv.Count; i++) if (pv[i].IsCat && pv[i].Cat == cat) return i;
            return -1;
        }

        private static int PreviewRowOfBranch(List<TreeRow> pv, string name)
        {
            for (int i = 0; i < pv.Count; i++)
                if (pv[i].Slot != null && CatTree.IsBranchSlot(pv[i].Slot) && pv[i].Slot.Name == name) return i;
            return -1;
        }

        private static int PreviewRowOfSub(List<TreeRow> pv, string cat, string sub)
        {
            for (int i = 0; i < pv.Count; i++)
                if (pv[i].IsSub && pv[i].Cat == cat && pv[i].Sub == sub) return i;
            for (int i = 0; i < pv.Count; i++)
                if (pv[i].Slot != null && pv[i].Slot.Cat == cat && pv[i].Slot.Sub == sub) return i;
            return -1;
        }

        private static int PreviewRowOfPos(List<TreeRow> pv, string cat, string sub, string name)
        {
            for (int i = 0; i < pv.Count; i++)
            {
                Slot s = pv[i].Slot;
                if (s != null && s.Cat == cat && s.Sub == sub && s.Name == name) return i;
            }
            return -1;
        }

        /// <summary>
        /// 拖动预览：把 rows 复制一份，把搬的块（大类整块地盘 / 小分类整段 / 分支连同子分支 / 单个位置）
        /// 从原地取出、插到目标位置 —— 树画出来就是松手后的样子。失败（目标不在了）就退回普通画法。
        /// </summary>
        private void BuildPreview(int kind, string tCat, string tSub, string tPos, string tBr, int mode)
        {
            try
            {
                previewRows = null; pvIns = -1; pvB1 = -1;
                int b0;
                if (kind == 2) b0 = RowIndexOfCat(dragName);
                else if (kind == 3) b0 = RowIndexOfSub(dragCat, dragName);
                else if (kind == 1) b0 = RowIndexOfBranch(dragName);
                else b0 = RowIndexOfPos(dragCat, dragSub, dragName);
                if (b0 < 0) return;

                int n = 1;
                while (b0 + n < rows.Count)
                {
                    TreeRow x = rows[b0 + n];
                    if (kind == 4) break;                                   // 位置只搬一行
                    if (x.IsCat) break;
                    if (kind == 3 && x.IsSub) break;
                    if (kind == 1 && !(x.Slot != null && CatTree.IsBranchSlot(x.Slot)
                        && IsDescendantBranch(x.Slot.Name, dragName))) break;
                    n++;
                }

                List<TreeRow> pv = new List<TreeRow>(rows);
                List<TreeRow> block = pv.GetRange(b0, n);
                pv.RemoveRange(b0, n);

                int ti;
                if (kind == 2) ti = PreviewRowOfCat(pv, tCat);
                // 小分类 / 位置：落点一定和源在同一个大类里，用 dragCat 找（tCat 只在拖大类时有值）
                else if (kind == 3) ti = PreviewRowOfSub(pv, dragCat, tSub);
                else if (kind == 4) ti = string.IsNullOrEmpty(tPos)
                    ? PreviewRowOfSub(pv, dragCat, tSub)
                    : PreviewRowOfPos(pv, dragCat, tSub, tPos);
                else ti = PreviewRowOfBranch(pv, tBr);
                if (ti < 0) return;

                int ins;
                if (mode == 1) ins = ti;
                else if (mode == 3) ins = ti + 1;                            // 变成第一个子分支
                else if (kind == 4 && !string.IsNullOrEmpty(tPos)) ins = ti + 1;
                else
                {
                    ins = ti + 1;                                            // 目标那一整块之后
                    while (ins < pv.Count)
                    {
                        TreeRow x = pv[ins];
                        if (x.IsCat) break;
                        if (kind != 2 && x.IsSub) break;
                        if (kind == 1 && !(x.Slot != null && CatTree.IsBranchSlot(x.Slot)
                            && IsDescendantBranch(x.Slot.Name, tBr))) break;
                        ins++;
                    }
                }
                pv.InsertRange(ins, block);
                previewRows = pv;
                pvIns = ins;
                pvB1 = ins + n - 1;
            }
            catch { previewRows = null; pvIns = -1; pvB1 = -1; }
        }

        internal bool ApplyDrop(string name, int kind, int row, int mode, string fromCat = null, string fromSub = null)
        {
            try
            {
                if (string.IsNullOrEmpty(name) || row < 0 || row >= rows.Count || mode <= 0)
                {
                    Core.Log("INFO", "DRAGAPPLY 拒绝 「" + name + "」 行=" + row + " 模式=" + mode + "（行号/模式无效）");
                    return false;
                }
                TreeRow tr = rows[row];
                Core.Log("INFO", "DRAGAPPLY 「" + name + "」 → 行=" + row
                    + (tr.IsCat ? " 大类「" + tr.Cat + "」" : " 行「" + (tr.Slot != null ? tr.Slot.Name : "?") + "」")
                    + " 模式=" + mode);
                if (kind == 2)
                {
                    // 大类：只能插到别的大类前 / 后
                    if (!tr.IsCat || string.Equals(tr.Cat, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Core.Log("FAIL", "DRAGAPPLY 大类落点无效：行 " + row + " 不是别的大类标题行");
                        return false;
                    }
                    bool ok = owner.MoveCatTo(name, tr.Cat, mode == 2);
                    Core.Log(ok ? "INFO" : "FAIL", "DRAGAPPLY 大类「" + name + "」挪到「" + tr.Cat + "」" + (mode == 2 ? "后面" : "前面") + (ok ? " 完成" : " 被 CatPrefs 拒绝"));
                    return ok;
                }
                Slot t = tr.Slot;
                if (kind == 3)
                {
                    // 小分类换顺序：落点必须是同一个大类里的行
                    string c, sb;
                    SubOfRow(tr, out c, out sb);
                    if (sb == null || !string.Equals(c, fromCat, StringComparison.Ordinal))
                    {
                        Core.Log("FAIL", "DRAGAPPLY 小分类落点无效：行 " + row + " 不在「" + fromCat + "」里");
                        return false;
                    }
                    if (string.Equals(sb, name, StringComparison.Ordinal)) { Core.Log("INFO", "DRAGAPPLY 小分类落点是自己 → 取消"); return false; }
                    return owner.MoveSubTo(fromCat, name, sb, mode == 2);
                }
                if (kind == 4)
                {
                    // 位置换顺序 / 换小分类：落点是小分类标题行（放到那一段头/尾）或某个位置行
                    string c, sb;
                    SubOfRow(tr, out c, out sb);
                    if (sb == null || !string.Equals(c, fromCat, StringComparison.Ordinal))
                    {
                        Core.Log("FAIL", "DRAGAPPLY 位置落点无效：行 " + row + " 不在「" + fromCat + "」里");
                        return false;
                    }
                    bool onSlot = tr.Slot != null && !CatTree.IsBranchSlot(tr.Slot);
                    string tPos = onSlot ? tr.Slot.Name : null;
                    return owner.MovePosTo(fromCat, fromSub, name, sb, tPos, mode == 2);
                }
                if (t == null || !CatTree.IsBranchSlot(t))
                {
                    Core.Log("FAIL", "DRAGAPPLY 分支落点无效：行 " + row + " 不是分支行");
                    return false;
                }
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    Core.Log("INFO", "DRAGAPPLY 分支落点是自己 → 取消");
                    return false;
                }
                string parent = mode == 3 ? t.Name : MyBranch.ParentOf(t.Name);
                string before = mode == 1 ? t.Name : mode == 2 ? MyBranch.NextSibling(t.Name) : "";
                bool okb = owner.MoveBranch(name, parent, before);
                Core.Log(okb ? "INFO" : "FAIL", "DRAGAPPLY 分支「" + name + "」→ 父=" + (parent.Length == 0 ? "(顶层)" : parent) + " 前插=" + before + (okb ? " 完成" : " 被拒绝"));
                return okb;
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "DRAGAPPLY 异常：" + ex.GetType().Name + " " + ex.Message + " @ " + ex.StackTrace);
                return false;
            }
        }

        /// <summary>自检用：某个分支在树里是第几行（没有返回 -1）。</summary>
        internal int RowIndexOfSlot(string key)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Slot != null && rows[i].Slot.Key == key) return i;
            return -1;
        }

        /// <summary>自检用：第 i 行是哪个槽位（标题行返回 null）。</summary>
        internal Slot RowSlot(int i)
        {
            return (i >= 0 && i < rows.Count) ? rows[i].Slot : null;
        }

        /// <summary>截图专用：摆成「正拖着某个分支、落点在第 row 行、模式 mode」的样子，不动数据。</summary>
        internal void TestDragVisual(string name, int row, int mode, Point at)
        {
            dragging = true;
            dragName = name ?? "";
            dragKind = 1;
            pressRow = row;
            dropRow = row;
            dropMode = mode;
            dragPt = at;
            pressArm = false;
            previewRows = null; pvIns = -1; pvB1 = -1;
            if (row >= 0 && row < rows.Count && rows[row].Slot != null && CatTree.IsBranchSlot(rows[row].Slot))
                BuildPreview(1, null, null, null, rows[row].Slot.Name, mode);   // 截图也走新预览画法
            Cursor = Cursors.SizeAll;
            Invalidate();
        }

        /// <summary>截图专用：摆成「正拖着某个大类、落点在第 row 行」的样子，不动数据。</summary>
        internal void TestCatDragVisual(string name, int row, int mode, Point at)
        {
            dragging = true;
            dragName = name ?? "";
            dragKind = 2;
            pressRow = row;
            dropRow = row;
            dropMode = mode;
            dragPt = at;
            pressArm = false;
            previewRows = null; pvIns = -1; pvB1 = -1;
            if (row >= 0 && row < rows.Count && rows[row].IsCat)
                BuildPreview(2, rows[row].Cat, null, null, null, mode);      // 截图也走新预览画法
            Cursor = Cursors.SizeAll;
            Invalidate();
        }

        internal void TestDragEnd() { ResetDrag(); }

        /// <summary>
        /// 自检用：按真实鼠标事件路径模拟一次拖动（OnMouseDown → 挪 6px → 挪到落点 → OnMouseUp）。
        /// 直接调受保护的 On* 方法，和真鼠标走的是同一段代码 —— 手能拖、这个必须过；手拖不动、这个能复现。
        /// part：1=落点行上半（插前） 2=下半（插后） 3=中间（放进）。
        /// </summary>
        internal bool SimulateRealDrag(int fromRow, int toRow, int part, out string steps)
        {
            steps = "";
            if (fromRow < 0 || fromRow >= rows.Count || toRow < 0 || toRow >= rows.Count)
            { steps = "行号越界 from=" + fromRow + " to=" + toRow; return false; }
            int fromY = fromRow * RowH + 4 + RowH / 2;
            int ty = toRow * RowH + 4 + (part == 1 ? 4 : part == 2 ? RowH - 4 : RowH / 2);
            const int x = 30;
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, fromY, 0));
            steps += "down: pressArm=" + pressArm + " pressRow=" + pressRow + "; ";
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 1, x, fromY + 3, 0));
            steps += "move3: dragging=" + dragging + "; ";
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 1, x, fromY + 6, 0));
            steps += "move6: dragging=" + dragging + " kind=" + dragKind + " name=" + dragName + "; ";
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 1, x, ty, 0));
            steps += "to: dropRow=" + dropRow + " dropMode=" + dropMode
                + " pv=" + (previewRows != null ? previewRows.Count : -1) + "/" + rows.Count + "; ";
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, ty, 0));
            steps += "up: dragging=" + dragging;
            return true;
        }

        /// <summary>
        /// 自检用：模拟「拖到一半鼠标捕获被系统收走」（拖出窗口松手 / Alt+Tab / 弹窗抢焦点）。
        /// 这几种情况 MouseUp 根本不会来，全靠 OnMouseCaptureChanged 兜底。
        /// </summary>
        internal string SimulateLostCapture(int row)
        {
            if (row < 0 || row >= rows.Count) return "行号越界 " + row;
            int y = row * RowH + 4 + RowH / 2;
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 30, y, 0));
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 1, 30, y + 8, 0));
            if (!dragging) return "没进入拖动状态（前提不成立）";
            string before = "dragging=" + dragging + " pressArm=" + pressArm;
            Capture = false;                        // 真机上这是系统做的，自检里手动来一下
            OnMouseCaptureChanged(EventArgs.Empty); // 没有消息循环，直接走一遍处理
            return before + " → dragging=" + dragging + " pressArm=" + pressArm
                 + " preview=" + (previewRows == null ? "清空" : "残留")
                 + " cursor=" + Cursor;
        }

        /// <summary>
        /// 自检用：模拟左键点在树的空白处（最后一行下面）。返回点完之后的焦点行号（-1 = 已清空）。
        /// </summary>
        internal int TestClickBlank()
        {
            int y = 4 + rows.Count * RowH + RowH / 2;
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 30, y, 0));
            return focus;
        }

        private void ResetDrag()
        {
            dragging = false;
            pressArm = false;
            pressRow = -1;
            dropRow = -1;
            dropMode = 0;
            dragKind = 0;
            dragName = "";
            previewRows = null;
            pvIns = -1;
            pvB1 = -1;
            Cursor = Cursors.Hand;
            // 状态先清完再放捕获：放捕获会触发 OnMouseCaptureChanged，
            // 那时 dragging / pressArm 已经是 false，不会反过来又走一遍取消。
            if (Capture) Capture = false;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            if (focus < 0)
            {
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i].Slot != null && rows[i].Slot.Key == owner.SelectedKey) { focus = i; break; }
                if (focus < 0 && rows.Count > 0) focus = 0;
            }
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (dragging && k == Keys.Escape) return true;      // 拖到一半按 Esc 取消（平时 Esc 照旧交给窗口）
            if (k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right
                || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Home || k == Keys.End
                || k == Keys.Enter || k == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (dragging && e.KeyCode == Keys.Escape) { ResetDrag(); e.Handled = true; return; }
            if (rows.Count == 0) { base.OnKeyDown(e); return; }
            if (focus < 0 || focus >= rows.Count) focus = 0;

            Keys k = e.KeyCode;
            if (k == Keys.Up) MoveFocus(focus - 1);
            else if (k == Keys.Down) MoveFocus(focus + 1);
            else if (k == Keys.PageUp) MoveFocus(focus - Math.Max(1, Parent.ClientSize.Height / RowH));
            else if (k == Keys.PageDown) MoveFocus(focus + Math.Max(1, Parent.ClientSize.Height / RowH));
            else if (k == Keys.Home) MoveFocus(0);
            else if (k == Keys.End) MoveFocus(rows.Count - 1);
            else if (k == Keys.Enter || k == Keys.Space) ActivateRow(focus);
            else if (k == Keys.Left || k == Keys.Right)
            {
                TreeRow r = rows[focus];
                bool open = r.IsCat ? !owner.IsFolded(r.Cat)
                          : r.IsSub ? !owner.IsSubFolded(r.Cat, r.Sub)
                          : false;
                // 注意：SetFolded 的第二个参数是「折叠」，不是「展开」。
                // 当前是展开的（open=true）要折叠 → 传 true。以前写成 !open，等于每次都在写回原值，
                // 按左右键看着像没反应（键盘自测就是这么抓出来的）。
                if (r.IsCat && ((k == Keys.Left && open) || (k == Keys.Right && !open)))
                    owner.SetFolded(r.Cat, open);
                else if (r.IsSub && ((k == Keys.Left && open) || (k == Keys.Right && !open)))
                    owner.SetSubFolded(r.Cat, r.Sub, open);
            }
            else { base.OnKeyDown(e); return; }

            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void MoveFocus(int i)
        {
            if (rows.Count == 0) return;
            int old = focus;
            focus = Math.Max(0, Math.Min(rows.Count - 1, i));
            if (old != focus)
            {
                InvalidateRow(old);
                InvalidateRow(focus);
            }
            EnsureRowVisible(focus);
        }

        private void InvalidateRow(int i)
        {
            if (i >= 0 && i < rows.Count) Invalidate(new Rectangle(0, i * RowH, Width, RowH + 4));
        }

        private void EnsureRowVisible(int i)
        {
            ScrollHost p = Parent as ScrollHost;
            if (p == null || !p.CanScroll) return;
            int y = i * RowH;
            int h = p.ClientSize.Height;
            int top = p.Offset;
            int bottom = top + h;
            int v = top;
            if (y < top + 4) v = Math.Max(0, y - RowH);
            else if (y + RowH > bottom - 4) v = y - h + RowH * 2;
            if (v == top) return;
            p.ScrollTo(v);
        }

        private void ActivateRow(int i)
        {
            if (i < 0 || i >= rows.Count) return;
            TreeRow r = rows[i];
            if (r.IsCat)
            {
                if (r.Slot != null) owner.SelectSlot(r.Slot.Key);     // 「地图」这类：类标题行就是容器
                else owner.SetFolded(r.Cat, !owner.IsFolded(r.Cat));
            }
            else if (r.IsSub) owner.SetSubFolded(r.Cat, r.Sub, !owner.IsSubFolded(r.Cat, r.Sub));
            else if (r.Slot != null) owner.SelectSlot(r.Slot.Key);
        }

        // 自测钩子：验证焦点移动和 Enter 激活走的是同一套键盘路径。
        internal bool FocusedRowIsSlot
        {
            get { return focus >= 0 && focus < rows.Count && rows[focus].Slot != null; }
        }
        internal bool FocusedRowIsCat
        {
            get { return focus >= 0 && focus < rows.Count && rows[focus].IsCat; }
        }
        internal bool FocusedRowIsSub
        {
            get { return focus >= 0 && focus < rows.Count && rows[focus].IsSub; }
        }
        internal string FocusedSlotKey
        {
            get { return FocusedRowIsSlot ? rows[focus].Slot.Key : ""; }
        }
        internal void TestKey(Keys k)
        {
            OnKeyDown(new KeyEventArgs(k));
        }

        /// <summary>把某个位置滚进可视区（搜索跳转、定位用）。</summary>
        public void ScrollToSlot(string key)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Slot == null || rows[i].Slot.Key != key) continue;
                ScrollHost p = Parent as ScrollHost;
                if (p == null || !p.CanScroll) return;
                int y = i * RowH;
                int h = p.ClientSize.Height;
                int top = p.Offset;
                if (y >= top && y + RowH <= top + h) return;              // 已经在视野里
                p.ScrollTo(Math.Max(0, y - h / 3));
                return;
            }
        }

        /// <summary>自检 / 拖动大类后用：把第 i 行滚进视野（-1 忽略）。</summary>
        public void ScrollToRow(int i)
        {
            if (i < 0 || i >= rows.Count) return;
            ScrollHost p = Parent as ScrollHost;
            if (p == null || !p.CanScroll) return;
            int y = i * RowH;
            int h = p.ClientSize.Height;
            int top = p.Offset;
            if (y >= top && y + RowH <= top + h) return;              // 已经在视野里
            p.ScrollTo(Math.Max(0, y - h / 3));
        }

        /// <summary>第 i 行是不是大类标题行。</summary>
        internal bool RowIsCat(int i) { return i >= 0 && i < rows.Count && rows[i].IsCat; }

        /// <summary>第 i 行的大类名（不是大类行返回空串）。</summary>
        internal string RowCat(int i) { return RowIsCat(i) ? rows[i].Cat : ""; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollHost p = Parent as ScrollHost;
            if (p != null && p.CanScroll)
            {
                p.ScrollTo(p.Offset - e.Delta);
                return;
            }
            base.OnMouseWheel(e);
        }
    }
}

namespace Installer
{
    public partial class MainForm
    {
        /// <summary>自测：切到方案树，只走键盘路径跑一遍。返回不通过条数。</summary>
        internal int UiTestKeys()
        {
            if (viewHost == null) { Core.Log("FAIL", "KEY 没有 viewHost"); return 1; }
            ShowTree();
            Application.DoEvents();
            return treeView.SelfTestKeys();
        }

        /// <summary>
        /// 截图专用：把树摆成「正拖着某个分支」的样子（落点选它下面第一个分支行），看完调 UiTestDragEnd 收掉。
        /// 只改绘制状态，不动任何数据 —— 拖拽的画面对不对，靠这个看。
        /// </summary>
        internal void UiTestDrag(string branchName)
        {
            if (viewHost == null) { Core.Log("FAIL", "DRAG 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();
            Slot s = treeView.SlotByName(branchName);
            if (s == null || !CatTree.IsBranchSlot(s)) { Core.Log("WARN", "DRAG 没找到分支: " + branchName); return; }
            treeView.SelectSlot(s.Key, true);
            Application.DoEvents();
            SlotTree body = treeView.Body;
            int from = body.RowIndexOfSlot(s.Key);
            int to = -1;
            for (int i = from + 1; i < body.RowCount; i++)
            {
                Slot t = body.RowSlot(i);
                if (t != null && CatTree.IsBranchSlot(t)) { to = i; break; }
            }
            if (from < 0 || to < 0) { Core.Log("WARN", "DRAG 旁边没有别的分支行，画不出落点"); return; }
            body.TestDragVisual(s.Name, to, 3, new Point(120, to * Dpi.S(Theme.RowH) + Dpi.S(20)));
            Core.Log("INFO", "DRAG 预览：「" + s.Name + "」拖到第 " + to + " 行（成为子分支）");
        }

        /// <summary>截图收尾：把拖动状态清掉，免得留在真界面上。</summary>
        internal void UiTestDragEnd() { if (treeView != null) treeView.Body.TestDragEnd(); }

        /// <summary>截图用：摆成「正拖着某大类、落点在另一大类行」的样子，不动数据。</summary>
        internal void UiTestCatDrag(string catName, string ontoCat)
        {
            if (treeView == null) { Core.Log("FAIL", "DRAGCAT 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();
            int from = -1, to = -1;
            for (int i = 0; i < treeView.Body.RowCount; i++)
            {
                if (!treeView.Body.RowIsCat(i)) continue;
                string c = treeView.Body.RowCat(i);
                if (string.Equals(c, catName, StringComparison.Ordinal)) from = i;
                else if (string.Equals(c, ontoCat, StringComparison.Ordinal)) to = i;
            }
            if (from < 0 || to < 0) { Core.Log("WARN", "DRAGCAT 没找到大类: " + catName + " / " + ontoCat); return; }
            treeView.Body.TestCatDragVisual(catName, to, 2, new Point(120, to * Dpi.S(Theme.RowH) + Dpi.S(20)));
            Core.Log("INFO", "DRAGCAT 预览：「" + catName + "」拖到「" + ontoCat + "」后面");
        }

        /// <summary>
        /// 自检：用真实鼠标事件路径拖一次（区别于 UiTestCatDrag 的画图预览，这里真挪数据）。
        /// 自动识别 fromName 是大类还是分支；做完把顺序还原，不污染用户数据。
        /// </summary>
        internal void UiTestRealDrag(string fromName, string toName)
        {
            if (treeView == null) { Core.Log("FAIL", "DRAGREAL 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();

            int from, to; bool isCat;
            if (!FindDragRows(fromName, toName, out from, out to, out isCat)) return;

            List<string> before = isCat ? CatPrefs.Cats() : MyBranch.Names();
            string steps;
            treeView.Body.SimulateRealDrag(from, to, isCat ? 2 : 3, out steps);
            Core.Log("INFO", "DRAGREAL 路径 " + steps);
            CheckAndRestoreDrag(fromName, toName, isCat, before, "DRAGREAL");
            treeView.Rebuild();
        }

        /// <summary>找拖动起点 / 落点行：大类按大类行找，分支按分支槽位找。</summary>
        private bool FindDragRows(string fromName, string toName, out int from, out int to, out bool isCat)
        {
            from = -1; to = -1; isCat = false;
            for (int i = 0; i < treeView.Body.RowCount; i++)
                if (treeView.Body.RowIsCat(i) && string.Equals(treeView.Body.RowCat(i), fromName, StringComparison.Ordinal))
                { from = i; isCat = true; break; }
            if (from < 0)
            {
                for (int i = 0; i < treeView.Body.RowCount; i++)
                {
                    Slot s = treeView.Body.RowSlot(i);
                    if (s != null && CatTree.IsBranchSlot(s) && string.Equals(s.Name, fromName, StringComparison.Ordinal)) { from = i; break; }
                }
            }
            if (isCat)
            {
                for (int i = 0; i < treeView.Body.RowCount; i++)
                    if (treeView.Body.RowIsCat(i) && string.Equals(treeView.Body.RowCat(i), toName, StringComparison.Ordinal)) { to = i; break; }
            }
            else
            {
                for (int i = 0; i < treeView.Body.RowCount; i++)
                {
                    Slot s = treeView.Body.RowSlot(i);
                    if (s != null && CatTree.IsBranchSlot(s) && string.Equals(s.Name, toName, StringComparison.Ordinal)) { to = i; break; }
                }
            }
            if (from < 0 || to < 0)
                Core.Log("WARN", "没找到拖动行: " + fromName + "(" + from + ") / " + toName + "(" + to + ")" + (isCat ? " [大类]" : " [分支]"));
            return from >= 0 && to >= 0;
        }

        /// <summary>比对拖动前后顺序，变了记 OK、没变记 FAIL（复现「手拖不动」），最后还原。</summary>
        private void CheckAndRestoreDrag(string fromName, string toName, bool isCat, List<string> before, string tag)
        {
            List<string> after = isCat ? CatPrefs.Cats() : MyBranch.Names();
            bool moved = false;
            if (after.Count == before.Count)
                for (int i = 0; i < after.Count; i++)
                    if (after[i] != before[i]) { moved = true; break; }
            if (!moved) Core.Log("FAIL", tag + " 「" + fromName + "」拖到「" + toName + "」顺序没变 —— 复现了拖不动");
            else Core.Log("INFO", tag + " 「" + fromName + "」已挪过");

            if (isCat)
            {
                for (int i = 0; i < before.Count; i++)
                    if (i >= after.Count || after[i] != before[i])
                        CatPrefs.MoveCatTo(before[i], i == 0 ? "" : before[i - 1], true);
            }
            else
            {
                for (int i = 0; i < before.Count; i++)
                    if (i >= after.Count || after[i] != before[i])
                        MyBranch.Move(before[i], i == 0 ? "" : MyBranch.ParentOf(before[i - 1]),
                            i + 1 < before.Count ? before[i + 1] : "");
            }
        }

        /// <summary>
        /// 端到端自检：用 Win32 真实鼠标输入（SetCursorPos + mouse_event）拖一次。
        /// 连 OS 层的输入路径一起测 —— 事件模拟过了、这个还不过，那问题就在真机和模拟的差异上。
        /// </summary>
        internal void UiTestMouseDrag(string fromName, string toName)
        {
            if (treeView == null) { Core.Log("FAIL", "MOUSEDRAG 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();

            int from, to; bool isCat;
            if (!FindDragRows(fromName, toName, out from, out to, out isCat)) return;

            // 窗口可能被记住的位置摆在屏幕外（多显示器拔过线之类），真实鼠标点不到 —— 拉回可见区再测
            WindowState = FormWindowState.Normal;
            SetBounds(80, 80, Math.Max(900, Width), Math.Max(620, Height));
            Activate();
            BringToFront();
            Application.DoEvents();

            // 保证两行都在可视区（大类行挨着，分支选相邻的也够用）：滚到靠上那一行
            treeView.Body.ScrollToRow(Math.Min(from, to));
            Application.DoEvents();

            Native.ForceForeground(Handle);
            Native.ShowWindow(Handle, 9);          // SW_RESTORE
            Native.ForceForeground(Handle);
            Application.DoEvents();

            SlotTree body = treeView.Body;
            // 行高跟着 DPI 走（和 SlotTree.RowH 同一个值）；这里原来写死 28，
            // 高缩放下算出来的屏幕坐标会落在相邻行上，自动化拖动就拖错东西了。
            int rh = Math.Max(16, Dpi.S(Theme.RowH));
            Point start = body.PointToScreen(new Point(40, from * rh + Dpi.S(18)));
            Point end = body.PointToScreen(new Point(40, to * rh + (isCat ? Dpi.S(28) : Dpi.S(18))));
            Native.POINT cp;
            Native.GetCursorPos(out cp);
            Core.Log("INFO", "MOUSEDRAG 屏幕坐标 起点(" + start.X + "," + start.Y + ") 落点(" + end.X + "," + end.Y + ")"
                + " 起点处窗口=" + Native.GetWindowText(Native.WindowFromPoint(new Native.POINT { X = start.X, Y = start.Y }))
                + " 当前前台=" + Native.GetWindowText(Native.GetForegroundWindow()) + " 光标(" + cp.X + "," + cp.Y + ")");

            List<string> before = isCat ? CatPrefs.Cats() : MyBranch.Names();
            Native.SetCursorPos(start.X, start.Y);
            System.Threading.Thread.Sleep(120);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(60);
            for (int i = 1; i <= 8; i++)
            {
                Native.SetCursorPos(start.X + (end.X - start.X) * i / 8, start.Y + (end.Y - start.Y) * i / 8);
                System.Threading.Thread.Sleep(40);
                Application.DoEvents();
            }
            System.Threading.Thread.Sleep(60);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(150);
            Application.DoEvents();

            CheckAndRestoreDrag(fromName, toName, isCat, before, "MOUSEDRAG");
            treeView.Rebuild();
        }

        /// <summary>自检：切到方案树并选某个槽位（按显示名）；传「未归类」就打开未归类页。</summary>
        /// <summary>诊断：展开左栏（装什么 / 安装选项），可选连日志一起展开，给截图核对台面视图。</summary>
        internal void UiTestDrawer(bool withLog)
        {
            SetDrawer(true);
            if (withLog) SetLogOpen(true);
            ShowTree();
            Application.DoEvents();
            Core.Log("INFO", "DRAWER 左栏已展开" + (withLog ? "，日志展开 高 " + gLog.Height + "px" : "，日志收起"));
        }

        internal void UiTestSelect(string slotName)
        {
            if (viewHost == null) { Core.Log("FAIL", "TREE 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();
            if (slotName == "未归类")
            {
                treeView.SelectUnmatched();
                Core.Log("INFO", "SELECT 已打开未归类页（" + treeView.Unmatched + " 个）");
                return;
            }
            Slot s = treeView.SlotByName(slotName);
            if (s == null) { Core.Log("WARN", "SELECT 没找到槽位: " + slotName); return; }
            treeView.SelectSlot(s.Key, true);      // 诊断截图要看得到那一行，顺带滚进视野
            Core.Log("INFO", "SELECT 已选中槽位 " + s.Name + (s.Empty ? "（空位）" : ""));
        }

        /// <summary>诊断截图（--dragpreview）：未归类页 + 假装正把第一个未归类 MOD 悬在第一个普通位置行上，验证拖放反馈的视觉效果。</summary>
        internal void UiTestDragHover()
        {
            if (viewHost == null) { Core.Log("FAIL", "DND 没有 viewHost"); return; }
            ShowTree();
            treeView.SelectUnmatched();
            Application.DoEvents();
            SlotTree b = treeView.Body;
            string name = "示例 MOD";
            List<ModGroup> gs = treeView.UnmatchedGroups;
            if (gs != null && gs.Count > 0) name = gs[0].Name;
            for (int i = 0; i < b.RowCount; i++)
            {
                Slot t = b.RowSlot(i);
                if (t == null || CatTree.IsBranchSlot(t) || t.Pile) continue;
                b.SetExtDragFake(name, i);
                Core.Log("INFO", "DRAGHOVER 悬在「" + t.Cat + " · " + t.Name + "」（第 " + i + " 行）");
                return;
            }
            Core.Log("WARN", "DRAGHOVER 没找到可悬停的位置行");
        }

        /// <summary>自测：切到方案树、数槽位、验证「一个位置只留一个」的替换逻辑。</summary>
        internal void UiTestTree()
        {
            if (viewHost == null) { Core.Log("FAIL", "TREE 没有 viewHost"); return; }
            // 自测会改勾选，先留一份底，测完原样放回去 —— 不能把用户的方案改坏
            List<string> backup = new List<string>(checkedKeys);
            ShowTree();
            Application.DoEvents();

            Core.Log("INFO", "TREE 候选表右键菜单：" + treeView.TreeMenuText());
            int slots = treeView.SlotCount;
            int used = treeView.SlotCheckedCount;
            Core.Log("INFO", "TREE 槽位 " + slots + " 个，其中已选 " + used + " 个");

            // 分类分布
            System.Collections.Generic.Dictionary<string, int> byCat =
                new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            System.Collections.Generic.Dictionary<string, int> bySub =
                new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < slots; i++)
            {
                Slot s = treeView.SlotAt(i);
                if (s == null) continue;
                int n;
                byCat.TryGetValue(s.Cat, out n);
                byCat[s.Cat] = n + 1;
                string sk = s.Cat + " \u0001 " + s.Sub;
                bySub.TryGetValue(sk, out n);
                bySub[sk] = n + 1;
                Core.Log("INFO", "TREE  " + s.Cat + " / " + s.Sub + " / " + s.Name + "  候选 " + s.Items.Count
                                                                        + (s.Empty ? "  [空位]" : "")
                                            + "  已选 " + (treeView.HasChecked(s) ? "是" : "否"));
            }
            foreach (KeyValuePair<string, int> kv in byCat)
                Core.Log("INFO", "TREE 分类 " + kv.Key + " = " + kv.Value + " 个位置");
            foreach (KeyValuePair<string, int> kv in bySub)
            {
                string[] pp = kv.Key.Split('\u0001');
                Core.Log("INFO", "TREE 小分类 " + pp[0].Trim() + " → " + pp[1].Trim() + " = " + kv.Value + " 个位置");
            }

            // 覆盖诊断：每类有多少个 mod 认得出来
            foreach (string c in Catalog.Cats)
            {
                int tot = 0, known = 0;
                foreach (ModGroup g in Catalog.Groups)
                {
                    if (g.Cat != c) continue;
                    tot++;
                    ModItem mi = g.Main;
                    if (mi != null && ((mi.Subject ?? "").Trim().Length > 0 || (mi.SubjectKey ?? "").Trim().Length > 0)) known++;
                }
                Core.Log("INFO", "TREE 覆盖 " + c + "  " + known + "/" + tot + " 认得出来");
            }

            // 找一个候选多于 3 的槽位来测替换
            int idx = -1;
            for (int i = 0; i < slots; i++)
            {
                Slot s = treeView.SlotAt(i);
                if (s != null && s.Items.Count >= 3) { idx = i; break; }
            }
            if (idx < 0) { Core.Log("FAIL", "TREE 没有候选 >=3 的槽位，测不了替换"); return; }

            Slot t = treeView.SlotAt(idx);
            Core.Log("INFO", "TREE 试验槽位 " + t.Name + "（" + t.Items.Count + " 个候选）");

            int n0 = TotalCheckedParts();
            string pick1 = treeView.ChooseAt(idx, 1);
            Application.DoEvents();
            int n1 = TotalCheckedParts();
            Core.Log("INFO", "TREE 选第 2 个候选「" + pick1 + "」：勾选数 " + n0 + " → " + n1);

            string pick2 = treeView.ChooseAt(idx, 2);
            Application.DoEvents();
            int n2 = TotalCheckedParts();
            Core.Log("INFO", "TREE 换成第 3 个「" + pick2 + "」：勾选数 " + n1 + " → " + n2
                            + "（应该只换不增）");
            if (n2 != n1) Core.Log("FAIL", "TREE 替换后勾选数变了 " + n1 + " → " + n2);

            int groupsInSlot = treeView.CheckedGroups(treeView.SlotAt(idx));
            Core.Log("INFO", "TREE 这个位置现在有 " + groupsInSlot + " 件");
            if (groupsInSlot != 1) Core.Log("FAIL", "TREE 一个位置出现了 " + groupsInSlot + " 件");

            treeView.ChooseAt(idx, -1);
            Application.DoEvents();
            int n3 = TotalCheckedParts();
            Core.Log("INFO", "TREE 点「留原版」：勾选数 " + n2 + " → " + n3);
            if (n3 != n2 - treeView.SlotAt(idx).Items.Count && n3 >= n2) Core.Log("FAIL", "TREE 留原版没清掉");

            // 折叠 / 展开：大类一层、小分类一层都要能收
            int rowsBefore = treeView.RowCount;
            treeView.SetFolded("枪械", true);
            Application.DoEvents();
            int rowsAfter = treeView.RowCount;
            Core.Log("INFO", "TREE 折叠「枪械」：行数 " + rowsBefore + " → " + rowsAfter);
            if (rowsAfter >= rowsBefore) Core.Log("FAIL", "TREE 折叠没生效");
            treeView.SetFolded("枪械", false);
            Application.DoEvents();
            Core.Log("INFO", "TREE 展开回去：行数 " + treeView.RowCount);

            string cat1 = null, sub1 = null;
            for (int i = 0; i < treeView.SlotCount; i++)
            {
                Slot s1 = treeView.SlotAt(i);
                if (s1 == null) continue;
                if (cat1 == null) { cat1 = s1.Cat; sub1 = s1.Sub; }
                if (s1.Cat == cat1 && s1.Sub == sub1) continue;
                break;
            }
            if (cat1 != null && sub1 != null)
            {
                int r0 = treeView.RowCount;
                int inSub = 0;
                for (int i = 0; i < treeView.SlotCount; i++)
                {
                    Slot s1 = treeView.SlotAt(i);
                    if (s1 != null && s1.Cat == cat1 && s1.Sub == sub1) inSub++;
                }
                treeView.SetSubFolded(cat1, sub1, true);
                Application.DoEvents();
                int r1 = treeView.RowCount;
                Core.Log("INFO", "TREE 折叠小分类「" + cat1 + " / " + sub1 + "」（" + inSub + " 个位置）：行数 "
                                + r0 + " → " + r1);
                if (r1 >= r0) Core.Log("FAIL", "TREE 小分类折叠没生效");
                treeView.SetSubFolded(cat1, sub1, false);
                Application.DoEvents();
                if (treeView.RowCount != r0) Core.Log("FAIL", "TREE 小分类展开后行数对不上");
            }

            // 未归类入口：剩下的 MOD 全部列出来，且提供「指定位置…」手动入口
            treeView.SelectUnmatched();
            Application.DoEvents();
            Core.Log("INFO", "TREE 未归类 " + treeView.Unmatched + " 个，列表 " + treeView.UnmatchedRows + " 行");
            if (treeView.UnmatchedRows != treeView.Unmatched)
                Core.Log("FAIL", "TREE 未归类列表行数 " + treeView.UnmatchedRows + " 和计数 " + treeView.Unmatched + " 对不上");
                Core.Log("FAIL", "TREE 未归类页没有「指定位置…」手动入口");
            // 名单跟着日志走：未归类数一波动，直接看日志就知道是哪几组
            {
                List<string> names = new List<string>();
                foreach (ModGroup g in treeView.UnmatchedGroups)
                {
                    ModItem mm = g.Main;
                    names.Add((mm == null ? g.Name : mm.Name) + "(" + (mm == null ? "?" : (mm.Subject == "" ? "无主体" : mm.Subject)) + ")");
                    if (names.Count >= 30) break;
                }
                Core.Log("INFO", "TREE 未归类名单: " + string.Join("、", names.ToArray()));
                int clu;
                Dictionary<ModGroup, List<ModGroup>> clans = CatTree.ClusterUnmatched(treeView.UnmatchedGroups, out clu);
                HashSet<List<ModGroup>> clanSet = new HashSet<List<ModGroup>>();
                foreach (List<ModGroup> mm2 in clans.Values) clanSet.Add(mm2);
                Core.Log("INFO", "TREE 未归类圈伙: " + (clu == 0 ? "没有相似包" : clu + " 个包圈成 " + clanSet.Count + " 伙"));
            }

            // 扩展（音效 / 只改第一人称模型 / HUD）是叠上去的：自己能开关，换模型时不该被一起关掉
            int exIdx = -1;
            // 扩展功能已移除（2026-11-20）：这一段「扩展开关 / 换模型不关扩展」的自测一起删掉。

            // 原样放回：按备份逐条还原，不是「再点一下」—— 点一下只会换成另一个候选
            checkedKeys.Clear();
            foreach (string k in backup) checkedKeys.Add(k);
            AfterTreePick();
            treeView.Sync();
            Application.DoEvents();
            int n4 = TotalCheckedParts();
            Core.Log("INFO", "TREE 还原后勾选数 " + n4 + "（原 " + n0 + "）");
            if (n4 != n0) Core.Log("FAIL", "TREE 还原后勾选数对不上 " + n0 + " → " + n4);

            // 截图停在「有扩展」的位置上，这样右半边的上下两栏都能看清
            Slot shot = null;
            for (int i = 0; i < slots; i++)
            {
                Slot sx = treeView.SlotAt(i);
                if (sx != null && sx.Items.Count > 0 && sx.Items.Count > 1) { shot = sx; break; }
            }
            if (shot == null) shot = treeView.SlotAt(0);
            if (shot != null) treeView.SelectSlot(shot.Key);
        }

        /// <summary>把勾选恢复成某个方案的样子并存盘（自测把方案改坏时用来救回来）。</summary>
        internal void UiFixProfile(string name)
        {
            ApplyProfileByName(name);
            Application.DoEvents();
            Core.Cfg.Mods = new List<string>(checkedKeys);
            Core.SaveConfig();
            Core.Log("INFO", "FIX 已按方案「" + name + "」恢复勾选，共 " + checkedKeys.Count + " 个文件");
        }

        private int TotalCheckedParts()        {
            int n = 0;
            foreach (ModGroup g in Catalog.Groups) n += CountChecked(g);
            return n;
        }
    }
}

namespace Installer
{
    public partial class MainForm
    {
        /// <summary>自测：「设置 MOD 分类」这条路走一遍 —— 人工归类要真的进树，测完原样还原。</summary>
        internal void UiTestSetCat()
        {
            ShowTree();
            Application.DoEvents();
            if (treeView == null) { Core.Log("FAIL", "SETCAT 没有树"); return; }

            // 分类是持久化的：先留底，自测结束原样写回，不能给用户留一个「测试分类」
            string f = CatPrefs.FileOf();
            string backup = System.IO.File.Exists(f) ? System.IO.File.ReadAllText(f) : null;
            int before = treeView.SlotCount;
            Core.Log("INFO", "SETCAT 起始槽位 " + before + " 个，分类顺序 " + string.Join(" / ", CatPrefs.Cats().ToArray()));

            List<Slot> now = SlotIndex.Build();
            ModGroup miss = null, known = null;
            foreach (ModGroup g in Catalog.Groups)
            {
                if (g.Cat == "地图" || g.Cat == "喷漆" || g.Main == null) continue;
                bool inTree = false;
                // 扩展也算进树：Extras 里的（音效 / 只改第一人称）不缺位置
                foreach (Slot s in now) if (s.Items.Contains(g)) { inTree = true; break; }
                if (!inTree) { if (miss == null) miss = g; }
                else if (known == null && (g.Main.Subject ?? "").Trim().Length > 0) known = g;
            }
            if (miss != null) Core.Log("INFO", "SETCAT 未归类样本：" + miss.Name);
            if (known == null) { Core.Log("FAIL", "SETCAT 找不到已知位置的样本"); return; }

            ModGroup t1 = miss != null ? miss : known;
            string autoCatOfT1 = t1.Cat;
            CatPrefs.Assign(t1, "测试分类", "测试位置");
            Catalog.ApplyCats();                 // 分类落到 ModGroup.Cat 上：树 / 搜索一起变
            treeView.Rebuild();
            bool inSlot = false, catOk = false;
            foreach (Slot s in SlotIndex.Build())
                if (s.Name == "测试位置" && s.Items.Contains(t1))
                { inSlot = true; catOk = s.Cat == "测试分类"; }
            Core.Log(inSlot && catOk ? "OK" : "FAIL", "SETCAT 全人工：" + t1.Name + " → 测试分类 / 测试位置，进树=" + inSlot + " 归类=" + catOk);
            Core.Log(t1.Cat == "测试分类" ? "OK" : "FAIL",
                "SETCAT 分类落到数据上：ModGroup.Cat=" + t1.Cat + "（树跟着走）");
            Core.Log(Array.IndexOf(Catalog.Cats, "测试分类") >= 0 ? "OK" : "FAIL",
                "SETCAT 分类表里有「测试分类」：" + string.Join(" / ", Catalog.Cats));

            List<string> order = CatPrefs.Cats();
            Core.Log(order[order.Count - 1] == "测试分类" ? "OK" : "FAIL",
                "SETCAT 新建分类排在末尾：" + string.Join(" / ", order.ToArray()));
            Core.Log(treeView.SlotCount > before ? "OK" : "FAIL",
                "SETCAT 槽位数 " + before + " → " + treeView.SlotCount + "（未归类的进来了）");

            string autoName = (known.Main.Subject ?? "").Trim();
            CatPrefs.Assign(known, "测试分类", "");
            Catalog.ApplyCats();
            treeView.Rebuild();
            bool moved = false;
            foreach (Slot s in SlotIndex.Build())
                if (s.Items.Contains(known))
                    moved = s.Cat == "测试分类";   // 位置名可能带后缀，只认「这个 MOD 在哪一类的槽里」
            Core.Log(moved ? "OK" : "FAIL", "SETCAT 只改归类：" + known.Name + " → 测试分类（位置仍是 " + autoName + "）");

            CatPrefs.Reset(t1);
            CatPrefs.Reset(known);
            Catalog.ApplyCats();
            treeView.Rebuild();
            bool gone = true;
            foreach (Slot s in SlotIndex.Build()) if (s.Name == "测试位置") { gone = false; break; }
            Core.Log(gone ? "OK" : "FAIL", "SETCAT 恢复自动后「测试位置」已消失");
            Core.Log(t1.Cat == autoCatOfT1 ? "OK" : "FAIL",
                "SETCAT 恢复自动后 ModGroup.Cat 回到 " + t1.Cat + "（原 " + autoCatOfT1 + "）");
            Core.Log(treeView.SlotCount == before ? "OK" : "FAIL",
                "SETCAT 槽位数回到 " + treeView.SlotCount + "（原 " + before + "）");

            // 大类改名：整类跟着搬，不能因为旧名字从分类表里消失就掉出树
            int medBefore = 0;
            foreach (ModGroup x in Catalog.Groups) if (x.Cat == "药品") medBefore++;
            CatPrefs.RenameCat("药品", "测试改名类");
            Catalog.AdoptCatRename("药品", "测试改名类");
            int medAfter = 0;
            foreach (ModGroup x in Catalog.Groups) if (x.Cat == "测试改名类") medAfter++;
            bool inTabs = Array.IndexOf(Catalog.Cats, "测试改名类") >= 0 && Array.IndexOf(Catalog.Cats, "药品") < 0;
            Core.Log(medBefore == medAfter && inTabs ? "OK" : "FAIL",
                "SETCAT 大类改名：药品 " + medBefore + " 个 → 测试改名类 " + medAfter + " 个，树跟上了=" + inTabs);

            if (backup == null) { try { System.IO.File.Delete(f); } catch { } }
            else System.IO.File.WriteAllText(f, backup, new System.Text.UTF8Encoding(true));
            CatPrefs.Reload();
            Catalog.ApplyCats();
            treeView.Rebuild();
            Core.Log(CatPrefs.Cats().Contains("测试分类") ? "FAIL" : "OK",
                "SETCAT 配置已还原：" + string.Join(" / ", CatPrefs.Cats().ToArray()));
        }

        /// <summary>自测：把「设置 MOD 分类」窗口拉起来截一张，人眼过一遍排版。</summary>
        internal void UiTestCatForm()
        {
            // 候选提示也要验：造一个认不出位置、但名字里带玩家叫法（佐伊 / 止痛药）的假 MOD
            ModGroup fake = new ModGroup();
            fake.Key = "#catself";
            fake.Name = "测试_佐伊和止痛药.vpk";
            fake.Cat = "其他";
            fake.Sub = "其他";
            ModItem fi = new ModItem();
            fi.Key = "#catself#1";
            fi.Name = fake.Name;
            fi.Cat = "其他";
            fi.Sub = "其他";
            fi.Subject = "";
            fake.Parts.Add(fi);
            using (ModCatForm ff = new ModCatForm(fake))
            {
                ff.Show();
                Application.DoEvents();
                ff.PerformLayout();
                Application.DoEvents();
                List<string> sugNames = new List<string>();
                foreach (Positions.Row r in Positions.Suggest("测试_佐伊和止痛药.vpk", 3)) sugNames.Add(r.Name);
                if (sugNames.Count == 0) Core.Log("FAIL", "CATFORM 候选没猜出来（测试_佐伊和止痛药.vpk）");
                else Core.Log("INFO", "CATFORM 候选: " + string.Join(" / ", sugNames.ToArray())
                    + "（对话框预选: " + (ff.lblSuggested.Length > 0 ? ff.lblSuggested : "无") + "）");
                try
                {
                    using (System.Drawing.Bitmap fb = new System.Drawing.Bitmap(ff.Width, ff.Height))
                    {
                        ff.DrawToBitmap(fb, new System.Drawing.Rectangle(0, 0, ff.Width, ff.Height));
                        fb.Save(System.IO.Path.Combine(Core.LogDir, "modcat-suggest.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch { }
                ff.Close();
            }

            ModGroup g = null;
            foreach (ModGroup x in Catalog.Groups)
                if (x.Cat != "地图" && x.Cat != "喷漆" && x.Main != null) { g = x; break; }
            if (g == null) { Core.Log("FAIL", "CATFORM 没有样本"); return; }
            using (ModCatForm f = new ModCatForm(g))
            {
                f.Show();
                Application.DoEvents();
                f.PerformLayout();
                Application.DoEvents();
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(System.IO.Path.Combine(Core.LogDir, "modcat.png"),
                             System.Drawing.Imaging.ImageFormat.Png);
                }
                Core.Log("INFO", "CATFORM " + g.Name + "（" + g.Cat + "）截图已存 modcat.png"
                    + (f.lblSuggested.Length > 0 ? "｜候选: " + f.lblSuggested : "｜无候选"));
                f.Close();
            }
        }

        /// <summary>自测：拿个假游戏目录打开「已装 MOD 开关」，截图看按钮排版。</summary>
        internal void UiTestAddonSwitch()
        {
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "l4d2-switch-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string addons = System.IO.Path.Combine(tmp, "left4dead2", "addons");
            try
            {
                System.IO.Directory.CreateDirectory(addons);
                List<string> names = new List<string>();
                foreach (ModGroup x in Catalog.Groups)
                {
                    if (x.Main == null || string.IsNullOrEmpty(x.Main.Name)) continue;
                    names.Add(x.Main.Name);
                    if (names.Count >= 6) break;
                }
                int i = 0;
                foreach (string n in names)
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(addons, n), "x");
                    i++;
                }
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("\"AddonList\"");
                sb.AppendLine("{");
                for (int k = 0; k < names.Count; k++)
                    sb.AppendLine("\t\"" + names[k] + "\"\t\t\"" + (k % 3 == 0 ? "1" : "0") + "\"");
                sb.AppendLine("}");
                System.IO.File.WriteAllText(AddonList.FileOf(tmp), sb.ToString(), System.Text.Encoding.Default);

                using (AddonSwitchForm f = new AddonSwitchForm(tmp))
                {
                    f.Show();
                    Application.DoEvents();
                    f.PerformLayout();
                    Application.DoEvents();
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(System.IO.Path.Combine(Core.LogDir, "addonswitch.png"),
                                 System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Core.Log("INFO", "ADDONSW " + i + " 个假 MOD，截图已存 addonswitch.png");
                    f.Close();
                }
            }
            catch (Exception ex) { Core.Log("FAIL", "ADDONSW 截图失败: " + ex.Message); }
            finally { try { System.IO.Directory.Delete(tmp, true); } catch { } }
        }
    }

    /// <summary>
    /// 工坊推荐的卡片墙：自绘（细边框 + 悬停高亮），**不放滚动条** —— 一屏能放几个就放几个。
    /// 悬停 → HoverItem（右侧详情跟着换）；单击 → Pick（单开一个工坊窗口看）。
    /// </summary>
    internal sealed class RecGrid : Control
    {
        private readonly List<WsItem> items = new List<WsItem>();
        private readonly List<Image> pics = new List<Image>();
        private bool skeleton;
        private int hover = -1;
        private readonly Timer shimmer = new Timer();     // 骨架屏扫光：30ms 一动
        private int shimmerX;

        public event Action<WsItem> Pick;
        public event Action<WsItem> HoverItem;

        private int G { get { return Dpi.S(8); } }
        private int CW { get { return Dpi.S(104); } }
        private int CH { get { return Dpi.S(58); } }
        private int TH { get { return Dpi.S(18); } }
        private int Pad { get { return Dpi.S(2); } }

        public RecGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            shimmer.Interval = 33;
            shimmer.Tick += delegate
            {
                shimmerX += Dpi.S(18);
                if (shimmerX > Width + Dpi.S(240)) shimmerX = -Dpi.S(240);
                Invalidate();
            };
        }

        private int Cols { get { int w = Width - Pad * 2 + G; return Math.Max(0, w / (CW + G)); } }
        private int Rows { get { int h = Height - Pad * 2 + G; return Math.Max(0, h / (CH + TH + G)); } }
        public int Capacity { get { return Cols * Rows; } }

        public void ShowSkeleton()
        {
            skeleton = true; hover = -1;
            foreach (Image p in pics) { try { p.Dispose(); } catch { } }
            pics.Clear(); items.Clear();
            shimmerX = -Dpi.S(240);
            if (!shimmer.Enabled) shimmer.Start();
            Invalidate();
        }

        public void SetItems(List<WsItem> its, List<Image> imgs)
        {
            foreach (Image p in pics) { try { p.Dispose(); } catch { } }
            pics.Clear(); items.Clear();
            if (shimmer.Enabled) shimmer.Stop();
            skeleton = false; hover = -1;
            int n = Math.Min(its.Count, imgs.Count);
            for (int i = 0; i < n; i++) { items.Add(its[i]); pics.Add(imgs[i]); }
            imgs.Clear();                       // 所有权转过来，调用方别再释放
            Invalidate();
        }

        public Image PicOf(WsItem it)
        {
            int i = items.IndexOf(it);
            return i >= 0 && i < pics.Count ? pics[i] : null;
        }

        private Rectangle CardRect(int i)
        {
            int cols = Math.Max(1, Cols);
            int r = i / cols, c = i % cols;
            return new Rectangle(Pad + c * (CW + G), Pad + r * (CH + TH + G), CW, CH);
        }

        private int HitTest(Point p)
        {
            int cap = Math.Min(items.Count, Capacity);
            for (int i = 0; i < cap; i++)
            {
                Rectangle r = CardRect(i);
                r.Height += TH;
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(BackColor);
            if (skeleton)
            {
                Color baseCol = Theme.IsDark ? Color.FromArgb(44, 48, 54) : Color.FromArgb(232, 236, 242);
                for (int i = 0; i < Capacity; i++)
                {
                    Rectangle r = CardRect(i);
                    using (SolidBrush b = new SolidBrush(baseCol)) g.FillRectangle(b, r);
                }
                // 扫光：一道更亮的斜带从左往右扫过整块骨架，告诉人"在加载"而不是"画坏了"
                if (Width > 0)
                {
                    int bandW = Math.Max(Dpi.S(90), Width / 5);
                    Rectangle band = new Rectangle(shimmerX, -Dpi.S(40), bandW, Height + Dpi.S(80));
                    Color bright = Theme.IsDark ? Color.FromArgb(70, 76, 84) : Color.FromArgb(247, 249, 252);
                    Color clear = Color.FromArgb(0, bright);
                    using (System.Drawing.Drawing2D.LinearGradientBrush lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                               band, clear, clear, System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
                    {
                        System.Drawing.Drawing2D.ColorBlend cb = new System.Drawing.Drawing2D.ColorBlend(3);
                        cb.Colors = new Color[] { clear, bright, clear };
                        cb.Positions = new float[] { 0f, 0.5f, 1f };
                        lg.InterpolationColors = cb;
                        g.FillRectangle(lg, band);
                    }
                }
                return;
            }
            int cap = Math.Min(items.Count, Capacity);
            for (int i = 0; i < cap; i++)
            {
                Rectangle r = CardRect(i);
                bool hot = i == hover;
                if (hot)
                    using (SolidBrush b = new SolidBrush(Theme.SurfaceAlt))
                        g.FillRectangle(b, new Rectangle(r.X - 1, r.Y - 1, r.Width + 2, r.Height + TH + 2));
                if (i < pics.Count && pics[i] != null) { try { g.DrawImage(pics[i], r); } catch { } }
                using (Pen pen = new Pen(hot ? Theme.Ink3 : Theme.LineSoft))
                    g.DrawRectangle(pen, r);
                string t = items[i].Title ?? "";
                if (t.Length > 14) t = t.Substring(0, 13) + "…";
                using (SolidBrush tb = new SolidBrush(Theme.Ink2))
                    g.DrawString(t, Theme.UISmall, tb, new RectangleF(r.X, r.Bottom + 1, r.Width, TH));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { shimmer.Stop(); shimmer.Dispose(); } catch { } }
            base.Dispose(disposing);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTest(e.Location);
            if (i == hover) return;
            hover = i;
            Invalidate();
            if (i >= 0 && i < items.Count && HoverItem != null) HoverItem(items[i]);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover < 0) return;
            hover = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = HitTest(e.Location);
            if (i >= 0 && i < items.Count && Pick != null) Pick(items[i]);
        }
    }
}
