namespace Installer
{
    // ============ 识别规则表：单一事实源 ============
    // 原先散在 Identify.cs（ModIdentify）里的识别数据集中在这里；判定流程仍在 Identify.cs。
    // 表内「行顺序」仍是匹配优先级的来源（越具体的越靠前）；第 2 刀起每行另带显式 MatchMode / Priority。
    internal static class RuleTable
    {
        // 特征串 -> 显示名。顺序即优先级：越具体的排在越前面。
        // 名字对齐 L4D2 官方武器名：v_ / w_ 两个前缀靠同一段特征串归到同一件东西，
        // 否则 w_shotgun + v_pumpshotgun 会被当成两件，Subject 变成「霰弹枪/木喷」。
        internal static readonly string[,] Dict = new string[,]
        {
            // ---- 特感：先判，免得被下面的人名/枪名抢走
            {"hulk",             "Tank"},
            {"boomer",           "Boomer"},
            {"hunter",           "Hunter"},
            {"smoker",           "Smoker"},
            {"jockey",           "Jockey"},
            {"spitter",          "Spitter"},
            {"charger",          "Charger"},
            {"witch",            "Witch"},
            {"tank",             "Tank"},

            // ---- 普通感染者（尸潮里那种小僵尸）：换皮肤最常见的对象，特感那几条接不住。
            //      模型名都是 common_male_* / common_female_*，比裸 "infected" 稳
            //      （infected 连特感目录、血贴图、高亮特效都沾，会误判）。
            //      非普通（防暴/小丑/泥人/道路工/CEDA/堕落/吉米）必须先判：模型名同样是
            //      common_male_*，排在下面就被普通感染者吃掉，装上去只剩一个槽。
            {"common_male_riot",     "防暴警察"},
            {"common_male_ceda",     "CEDA"},
            {"common_male_clown",    "小丑"},
            {"common_male_mud",      "泥人"},
            {"common_male_roadcrew", "道路工"},
            {"common_male_jimmy",    "吉米"},
            {"common_male_fallen",   "堕落幸存者"},
            {"common_male",      "普通感染者"},
            {"common_female",    "普通感染者"},

            // ---- 幸存者：L4D2 四人 + L4D1 四人。具体模型名必须排在 "survivor" 之前
            {"survivor_namvet",   "比尔"},
            {"survivor_teenangst","佐伊"},
            {"survivor_manager",  "路易斯"},
            {"survivor_biker",    "弗朗西斯"},
            {"survivor_coach",    "教练"},
            {"survivor_producer", "罗谢尔"},     // producer = 新闻制片人 = Rochelle（原来错写成教练）
            {"survivor_mechanic", "埃利斯"},
            {"survivor_gambler",  "尼克"},
            {"survivor",          "幸存者"},

            // ---- 药品
            {"painpills",        "止痛药"},
            {"pain_pills",       "止痛药"},
            {"adrenaline",       "肾上腺素"},
            {"defibrillator",    "电击器"},
            {"firstaid",         "医疗包"},
            {"medkit",           "医疗包"},

            // ---- 投掷物
            {"vomitjar",         "胆汁"},
            {"vomit_jar",        "胆汁"},
            {"bile_flask",       "胆汁"},        // 官方胆汁模型名（来源：NekoVpk TaggedAssets, MIT）
            {"molotov",          "燃烧瓶"},
            {"pipebomb",         "土制炸弹"},
            {"grenade_launcher", "榴弹发射器"},

            // ---- 枪械：具体型号在前，泛称在后
            {"rifle_ak47",       "AK-47"},
            {"ak47",             "AK-47"},
            {"rifle_desert",     "SCAR-L"},      // 三连发 = Desert Rifle，玩家都按 SCAR 叫
            {"desert_rifle",     "SCAR-L"},
            {"rif_sg552",        "sg552"},       // v_ 版模型名是缩写 rif_sg552
            {"rifle_sg552",      "sg552"},
            {"sg552",            "sg552"},
            {"rifle_m60",        "M60"},
            {"machinegun_m60",   "M60"},
            {"m60",              "M60"},
            {"smg_mp5",          "MP5"},
            {"mp5",              "MP5"},
            {"smg_silenced",     "MAC-10"},
            {"silenced_smg",     "MAC-10"},
            {"mac-10",           "MAC-10"},
            {"mac10",            "MAC-10"},
            {"smg_a",            "MAC-10"},        // w_smg_a / v_smg_a 是 MAC-10 的模型，必须先于泛称 smg
            {"smg",              "UZI"},           // 裸 smg = v_smg = UZI
            {"uzi",              "UZI"},
            {"sniper_military",  "30发军用狙"},
            {"huntingrifle",     "15发木狙"},
            {"hunting_rifle",    "15发木狙"},
            {"sniper_scout",     "鸟狙"},
            {"sniper_awp",       "AWP"},
            {"snip_awp",         "AWP"},
            {"awp",              "AWP"},
            {"shotgun_chrome",   "铁喷"},{"chrome",           "铁喷"},
            {"shotgun_spas",     "二代连喷"},{"spas",             "二代连喷"},{"scattergun",       "二代连喷"},
            {"pumpshotgun",      "木喷"},
            {"autoshotgun",      "一代连喷"},
            {"autoshot",         "一代连喷"},    // 官方一代连喷贴图目录 materials/models/v_models/weapons/autoshot/
            {"shotgun",          "铁喷"},        // 裸 shotgun = w_shotgun.mdl = 铁喷的官方世界模型（木喷是 pumpshotgun）
            {"rifle",            "M16"},         // 裸 rifle = v_rifle = M16
            {"machinegun",       "M60"},
            {"pistol_magnum",    "马格南"},
            {"magnum",           "马格南"},
            {"pistol",           "手枪"},
            {"melee",            "近战武器"},

            // ---- 近战具体型号（14 种全；来源：NekoVpk TaggedAssets, MIT）
            {"katana",           "武士刀"},
            {"machete",          "砍刀"},
            {"crowbar",          "撬棍"},
            {"fireaxe",          "消防斧"},
            {"frying_pan",       "平底锅"},      // 官方模型名 frying_pan；fryingpan 是非常规模型名，兜在底下
            {"fryingpan",        "平底锅"},
            {"chainsaw",         "电锯"},
            {"cricket_bat",      "板球拍"},
            {"golfclub",         "高尔夫球杆"},
            {"tonfa",            "警棍"},
            {"shovel",           "铁锹"},
            {"pitchfork",        "干草叉"},
            {"electric_guitar",  "电吉他"},
            {"knife",            "小刀"},        // 官方 v_knife_t
            {"v_bat",            "棒球棍"},      // 官方 v_bat/w_bat；裸 "bat" 会误伤，必须带前缀
            {"w_bat",            "棒球棍"},

            // ---- 其余杂项（道具/弹药包；来源：NekoVpk TaggedAssets, MIT）
            {"explosive_ammopack",   "爆破弹药包"},
            {"incendiary_ammopack",  "燃烧弹药包"},
            {"laser_sights",     "激光瞄准"},
            {"oxygentank",       "氧气瓶"},
            {"propanecanister",  "煤气罐"},
            {"explosive_box",    "烟花盒"},
            {"barrel_fuel",      "燃料桶"},
            {"gnome",            "地精"},
            {"cola",             "可乐"},
            {"concrete_spawnchunk", "石头"},
            {"concrete_chunk01a",   "石头"},
            {"tank_rock",           "石头"},
            {"ammo",             "弹药"},
            {"gascan",           "油桶"},
            {"pills",            "止痛药"},
            // ---- 天空盒：改的是 materials/skybox/*.vmt，没有模型文件，只能靠材质路径认。
            //      "skybox" 只出现在这一个目录，不会误伤别的包。
            {"skybox",           "天空盒"},
        };

        // 同一件霰弹枪的视角/世界模型成对出现时，世界模型不再另算一个主体。
        // 不改变裸 world model 的既有映射，只消除包内成对证据造成的假「附带」。
        internal static readonly string[,] OfficialModelPairs = new string[,]
        {
            {"v_pumpshotgun", "w_shotgun", "木喷"},
            {"v_shotgun_chrome", "w_pumpshotgun_a", "铁喷"}
        };

        // ============ 第 2 刀：规则行的显式匹配方式与优先级 ============
        // 以前「行顺序 = 优先级」只写在注释里，增删一行靠手动插队；现在每行都带显式的
        // MatchMode 与 Priority，Feature/Lookup 按 Priority 升序遍历。
        // Priority 由当前 Dict 行顺序生成（0,1,2...）：排序后的遍历顺序与改造前的行顺序
        // 逐行一致，属于行为等价迁移（增删一行只需给新行一个 Priority，不再靠挪位置）。
        internal enum MatchMode
        {
            Exact = 0,      // 整串相等
            Segment = 1,    // 按分隔符切段后整段相等（路径段 / 词）
            Prefix = 2,     // 前缀
            Contains = 3    // 子串包含（现行全部规则的实际语义：IndexOf >= 0）
        }

        internal sealed class RuleRow
        {
            public readonly int Row;            // 在 Dict 里的行号（调试用）
            public readonly string Feature;     // 命中特征串（Dict[i,0]）
            public readonly string Subject;     // 显示名（Dict[i,1]）
            public readonly MatchMode Match;    // 匹配方式
            public readonly int Priority;       // 遍历优先级：数值小的先判
            public RuleRow(int row, string feature, string subject, MatchMode match, int priority)
            {
                Row = row; Feature = feature; Subject = subject; Match = match; Priority = priority;
            }
        }

        // 逐行从 Dict 生成：MatchMode 一律取现行语义 Contains（改造前就是 IndexOf >= 0）。
        // 以后要单独调某一行，改这里一处即可，不影响别的行。
        internal static readonly RuleRow[] Rules = BuildRules();

        // 按 Priority 升序排好的遍历下标（Priority 相同时保持原行号，稳定）。
        internal static readonly int[] RuleOrder = BuildRuleOrder();

        private static RuleRow[] BuildRules()
        {
            int n = Dict.GetLength(0);
            RuleRow[] rows = new RuleRow[n];
            for (int i = 0; i < n; i++)
                rows[i] = new RuleRow(i, Dict[i, 0], Dict[i, 1], MatchMode.Contains, i);
            return rows;
        }

        private static int[] BuildRuleOrder()
        {
            int[] order = new int[Rules.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            System.Array.Sort(order, delegate(int a, int b)
            {
                int c = Rules[a].Priority.CompareTo(Rules[b].Priority);
                return c != 0 ? c : a.CompareTo(b);
            });
            return order;
        }

        // 按一行的 MatchMode 比一次。现行全部规则都是 Contains，与改造前的
        // s.IndexOf(feature, StringComparison.Ordinal) >= 0 完全相同；
        // Exact / Segment / Prefix 供以后单独调整某一行时使用，不改动现有命中语义。
        internal static bool Match(string s, RuleRow row)
        {
            if (string.IsNullOrEmpty(s) || row == null || string.IsNullOrEmpty(row.Feature)) return false;
            switch (row.Match)
            {
                case MatchMode.Exact:
                    return string.Equals(s, row.Feature, System.StringComparison.Ordinal);
                case MatchMode.Prefix:
                    return s.StartsWith(row.Feature, System.StringComparison.Ordinal);
                case MatchMode.Segment:
                    return IsSegment(s, row.Feature);
                default:
                    return s.IndexOf(row.Feature, System.StringComparison.Ordinal) >= 0;
            }
        }

        // 段匹配：特征串落在分隔符边界上（前后都不是字母/数字）才算命中。
        private static bool IsSegment(string s, string f)
        {
            int at = s.IndexOf(f, System.StringComparison.Ordinal);
            while (at >= 0)
            {
                bool left = at == 0 || !char.IsLetterOrDigit(s[at - 1]);
                int end = at + f.Length;
                bool right = end >= s.Length || !char.IsLetterOrDigit(s[end]);
                if (left && right) return true;
                at = s.IndexOf(f, at + 1, System.StringComparison.Ordinal);
            }
            return false;
        }

        // 标题/描述里常见的玩家叫法 → 位置。只在包内容判不出时兜底。
        internal static readonly string[,] AliasKeys = new string[,]
        {
            {"铁喷","铁喷","枪械","霰弹枪"},{"铬制","铁喷","枪械","霰弹枪"},
            {"木喷","木喷","枪械","霰弹枪"},{"泵动","木喷","枪械","霰弹枪"},
            {"连喷","一代连喷","枪械","霰弹枪"},{"spas","二代连喷","枪械","霰弹枪"},
            {"连狙","30发军用狙","枪械","狙击枪"},{"军用狙","30发军用狙","枪械","狙击枪"},
            {"猎鹿","15发木狙","枪械","狙击枪"},{"鸟狙","鸟狙","枪械","狙击枪"},{"侦察步","鸟狙","枪械","狙击枪"},
            {"15连","15发木狙","枪械","狙击枪"},{"30连","30发军用狙","枪械","狙击枪"},
            {"15连狙","15发木狙","枪械","狙击枪"},{"30连狙","30发军用狙","枪械","狙击枪"},
            {"木狙","15发木狙","枪械","狙击枪"},{"军用狙","30发军用狙","枪械","狙击枪"},
            {"huntingrifle","15发木狙","枪械","狙击枪"},{"hunting rifle","15发木狙","枪械","狙击枪"},
            {"hunting_rifle","15发木狙","枪械","狙击枪"},{"military sniper","30发军用狙","枪械","狙击枪"},
            {"马格南","马格南","枪械","手枪"},{"沙鹰","马格南","枪械","手枪"},
            {"ak","AK-47","枪械","步枪"},{"m16","M16","枪械","步枪"},{"sg552","sg552","枪械","步枪"},
            {"scar","SCAR-L","枪械","步枪"},{"awp","AWP","枪械","狙击枪"},{"mp5","MP5","枪械","冲锋枪"},
            {"m60","M60","枪械","特殊武器"},{"uzi","UZI","枪械","冲锋枪"},{"乌兹","UZI","枪械","冲锋枪"},
            {"mac","MAC-10","枪械","冲锋枪"},{"消音","MAC-10","枪械","冲锋枪"},
            {"武士刀","武士刀","近战","近战"},{"砍刀","砍刀","近战","近战"},{"撬棍","撬棍","近战","近战"},
            {"医疗包","医疗包","药品","治疗与复活"},{"止痛药","止痛药","药品","暂时增益"},
            {"肾上腺素","肾上腺素","药品","暂时增益"},{"电击器","电击器","药品","治疗与复活"},
            {"燃烧瓶","燃烧瓶","投掷物","投掷物"},{"土制炸弹","土制炸弹","投掷物","投掷物"},
            // Tank 的投掷石：标题里常见 Tank Rock / 坦克石头，模型被作者换名时还能兜住
            {"tank rock","石头","僵尸","特感"},{"tank_rock","石头","僵尸","特感"},
            {"tankrock","石头","僵尸","特感"},
            {"坦克石头","石头","僵尸","特感"},{"坦克的石头","石头","僵尸","特感"},
            {"坦克扔的石头","石头","僵尸","特感"}
        };

        // 幸存者的老布局是按人分区：models/survivors/<人名>/<模型>.mdl。
        // 模型文件名（namvet_deathpose）里没有 survivor_namvet 这段，靠这张表补。
        internal static readonly string[,] SurvivorDirs = new string[,]
        {
            {"namvet","比尔"}, {"teenangst","佐伊"}, {"manager","路易斯"}, {"biker","弗朗西斯"},
            {"producer","罗谢尔"}, {"coach","教练"}, {"mechanic","埃利斯"}, {"gambler","尼克"}
        };

        // 特感的模型目录就是 models/infected/<名字>/，名字对得上就够
        internal static readonly string[,] InfectKeys = new string[,]
        {
            {"hulk","Tank"},{"boomer","Boomer"},{"hunter","Hunter"},{"smoker","Smoker"},
            {"jockey","Jockey"},{"spitter","Spitter"},{"charger","Charger"},{"witch","Witch"},
            {"tank","Tank"}
        };

        // 药品段（ClassifyByPaths 第 4 步）：模型/图标路径关键词 -> 位置。顺序即优先级。
        internal static readonly string[,] Medicine = new string[,]
        {
            {"medkit",         "药品", "医疗包"},
            {"painpills",      "药品", "止痛药"},
            {"adrenaline",     "药品", "肾上腺素"},
            {"defibrillator",  "药品", "电击器"}
        };

        // ClassifyByPaths 的判定顺序常量：数值小的先判，命中即早退。
        // Identify.cs 里各段代码的排列必须与 ClassifyOrder 逐条一致 —— 改顺序 = 改行为。
        internal enum ClassifyStage
        {
            Map = 1,             // ①    maps/*.bsp
            Campaign = 2,        // ①.5  missions/*.txt（战役素材卷）
            Scene = 3,           // ②    materials/skybox、materials/detail
            Survivor = 4,        // 2)   幸存者模型 survivor_*
            InfectedCommon = 5,  // 2.4  非普通感染者（小僵尸）
            InfectedMulti = 6,   // 2.5  多特感 -> 通用
            InfectedOne = 7,     // 3)   单个特感
            Medicine = 8,        // 4)   药品（Medicine 表）
            Weapon = 9,          // 5)   枪械/近战/投掷物/弹药升级
            Spray = 10,          // 7.1  喷漆
            HudIcon = 11,        // 7.2  单件物品的 HUD 图标
            Ui = 12,             // 7.3  UI 界面
            Tracer = 13,         // 7.4  弹道
            Effects = 14,        // 7.5  特效
            Sound = 15,          // 7.6  音效
            Script = 16          // 8)   脚本
        }

        // ClassifyOrder：与 ClassifyByPaths 的实际早退顺序一一对应（只作对照，不参与运行）。
        internal static readonly ClassifyStage[] ClassifyOrder = new ClassifyStage[]
        {
            ClassifyStage.Map, ClassifyStage.Campaign, ClassifyStage.Scene, ClassifyStage.Survivor,
            ClassifyStage.InfectedCommon, ClassifyStage.InfectedMulti, ClassifyStage.InfectedOne,
            ClassifyStage.Medicine, ClassifyStage.Weapon, ClassifyStage.Spray, ClassifyStage.HudIcon,
            ClassifyStage.Ui, ClassifyStage.Tracer, ClassifyStage.Effects, ClassifyStage.Sound,
            ClassifyStage.Script
        };
    }
}
