using System;
using System.Collections.Generic;

namespace Installer
{
    /// <summary>
    /// 位置表：游戏里「能替换的东西」只在这里定义一次。
    /// 小分类 / 显示名 / 英文名 / 别名（旧名、玩家叫法、英文关键词）都以本表为准，
    /// 方案树的 PosOrder、翻译表 Lang、识别结果的归一（Canonical）全部从它读 ——
    /// 以前同一个名字写在四个文件里，漏同步就表现成「装了没认出来」。
    /// 新增一个位置 = 这里加一行；别再去别处补名字。
    /// </summary>
    internal static class Positions
    {
        internal sealed class Row
        {
            public string Sub;        // 小分类，决定它归哪个大类
            public string Name;       // 显示名（也是识别的标准结论）
            public string En;         // 英文界面用
            public string[] Aliases;  // 别名：旧名 / 玩家叫法 / 英文关键词
        }

        private static readonly Row[] _rows = new Row[]
        {
            new Row { Sub = "步枪", Name = "AK-47", En = "AK-47", Aliases = new string[] { "ak", "AK47" } },
            new Row { Sub = "步枪", Name = "M16", En = "M16", Aliases = new string[] { "m16" } },
            new Row { Sub = "步枪", Name = "sg552", En = "sg552", Aliases = new string[0] },
            new Row { Sub = "步枪", Name = "SCAR-L", En = "SCAR-L", Aliases = new string[] { "scar", "SCAR", "三连发" } },
            new Row { Sub = "冲锋枪", Name = "UZI", En = "UZI", Aliases = new string[] { "冲锋枪" } },
            new Row { Sub = "冲锋枪", Name = "MAC-10", En = "MAC-10", Aliases = new string[] { "mac", "消音", "MAC10" } },
            new Row { Sub = "冲锋枪", Name = "MP5", En = "MP5", Aliases = new string[] { "mp5" } },
            new Row { Sub = "霰弹枪", Name = "木喷", En = "Pump Shotgun", Aliases = new string[] { "泵动" } },
            new Row { Sub = "霰弹枪", Name = "铁喷", En = "Chrome Shotgun", Aliases = new string[] { "铬制", "铁盆" } },
            new Row { Sub = "霰弹枪", Name = "一代连喷", En = "Auto Shotgun", Aliases = new string[] { "连喷" } },
            new Row { Sub = "霰弹枪", Name = "二代连喷", En = "Combat Shotgun", Aliases = new string[] { "spas" } },
            new Row { Sub = "狙击枪", Name = "AWP", En = "AWP", Aliases = new string[] { "awp" } },
            new Row { Sub = "狙击枪", Name = "鸟狙", En = "Hunting Rifle", Aliases = new string[] { "侦察步" } },
            new Row { Sub = "狙击枪", Name = "15发木狙", En = "Hunting Rifle (15)", Aliases = new string[] { "猎鹿", "15连", "15连狙", "木狙", "huntingrifle", "hunting rifle", "hunting_rifle", "15发连狙", "15连", "木狙", "猎枪" } },
            new Row { Sub = "狙击枪", Name = "30发军用狙", En = "Military Sniper", Aliases = new string[] { "连狙", "军用狙", "30连", "30连狙", "军用狙", "military sniper", "30发连狙", "30连", "军用狙", "连狙" } },
            new Row { Sub = "特殊武器", Name = "M60", En = "M60", Aliases = new string[] { "m60" } },
            new Row { Sub = "特殊武器", Name = "榴弹发射器", En = "Grenade Launcher", Aliases = new string[] { "榴弹" } },
            new Row { Sub = "投掷物", Name = "燃烧瓶", En = "Molotov", Aliases = new string[0] },
            new Row { Sub = "投掷物", Name = "土制炸弹", En = "Pipe Bomb", Aliases = new string[] { "土制" } },
            new Row { Sub = "投掷物", Name = "胆汁", En = "Bile Jar", Aliases = new string[0] },
            new Row { Sub = "手枪", Name = "手枪", En = "Pistols", Aliases = new string[0] },
            new Row { Sub = "手枪", Name = "马格南", En = "Magnum", Aliases = new string[] { "沙鹰" } },
            new Row { Sub = "近战", Name = "近战武器", En = "Melee weapons", Aliases = new string[0] },
            new Row { Sub = "近战", Name = "消防斧", En = "Fire Axe", Aliases = new string[] { "fireaxe", "fire_axe" } },
            new Row { Sub = "近战", Name = "武士刀", En = "Katana", Aliases = new string[] { "katana" } },
            new Row { Sub = "近战", Name = "砍刀", En = "Machete", Aliases = new string[] { "machete" } },
            new Row { Sub = "近战", Name = "撬棍", En = "Crowbar", Aliases = new string[] { "crowbar" } },
            new Row { Sub = "近战", Name = "电锯", En = "Chainsaw", Aliases = new string[] { "chainsaw" } },
            new Row { Sub = "近战", Name = "平底锅", En = "Frying Pan", Aliases = new string[] { "frying" } },
            new Row { Sub = "近战", Name = "棒球棍", En = "Baseball Bat", Aliases = new string[] { "球棍", "baseball" } },
            new Row { Sub = "近战", Name = "板球拍", En = "Cricket bat", Aliases = new string[] { "板球", "cricket" } },
            new Row { Sub = "近战", Name = "高尔夫球杆", En = "Golf club", Aliases = new string[] { "高尔夫", "golf" } },
            new Row { Sub = "近战", Name = "电吉他", En = "Electric guitar", Aliases = new string[] { "吉他", "guitar" } },
            new Row { Sub = "近战", Name = "警棍", En = "Nightstick", Aliases = new string[] { "nightstick", "tonfa" } },
            new Row { Sub = "近战", Name = "干草叉", En = "Pitchfork", Aliases = new string[] { "pitchfork" } },
            new Row { Sub = "近战", Name = "铁锹", En = "Shovel", Aliases = new string[] { "铁铲", "shovel" } },
            new Row { Sub = "近战", Name = "小刀", En = "Knife", Aliases = new string[] { "knife" } },
            new Row { Sub = "弹药升级", Name = "燃烧弹药包", En = "Incendiary Ammo", Aliases = new string[] { "燃烧弹药", "燃烧弹" } },
            new Row { Sub = "弹药升级", Name = "爆破弹药包", En = "Explosive Ammo", Aliases = new string[] { "爆破弹药", "高爆" } },
            new Row { Sub = "弹药升级", Name = "激光瞄准", En = "Laser Sight", Aliases = new string[] { "激光" } },
            new Row { Sub = "弹药升级", Name = "弹药", En = "Ammo", Aliases = new string[0] },
            new Row { Sub = "治疗与复活", Name = "医疗包", En = "Medkit", Aliases = new string[0] },
            new Row { Sub = "治疗与复活", Name = "电击器", En = "Defibrillator", Aliases = new string[0] },
            new Row { Sub = "暂时增益", Name = "肾上腺素", En = "Adrenaline", Aliases = new string[0] },
            new Row { Sub = "暂时增益", Name = "止痛药", En = "Pain Pills", Aliases = new string[] { "药丸" } },
            new Row { Sub = "一代", Name = "比尔", En = "Bill", Aliases = new string[0] },
            new Row { Sub = "一代", Name = "佐伊", En = "Zoey", Aliases = new string[0] },
            new Row { Sub = "一代", Name = "路易斯", En = "Louis", Aliases = new string[0] },
            new Row { Sub = "一代", Name = "弗朗西斯", En = "Francis", Aliases = new string[0] },
            new Row { Sub = "二代", Name = "教练", En = "Coach", Aliases = new string[0] },
            new Row { Sub = "二代", Name = "埃利斯", En = "Ellis", Aliases = new string[0] },
            new Row { Sub = "二代", Name = "尼克", En = "Nick", Aliases = new string[0] },
            new Row { Sub = "二代", Name = "罗谢尔", En = "Rochelle", Aliases = new string[0] },
            new Row { Sub = "八人包", Name = "幸存者", En = "Survivor", Aliases = new string[0] },
            new Row { Sub = "特感", Name = "通用", En = "Generic", Aliases = new string[0] },
            new Row { Sub = "特感", Name = "Boomer", En = "Boomer", Aliases = new string[0] },
            new Row { Sub = "特感", Name = "Charger", En = "Charger", Aliases = new string[0] },
            new Row { Sub = "特感", Name = "Hunter", En = "Hunter", Aliases = new string[0] },
            new Row { Sub = "特感", Name = "Jockey", En = "Jockey", Aliases = new string[0] },
            new Row { Sub = "特感", Name = "Smoker", En = "Smoker", Aliases = new string[0] },
            new Row { Sub = "特感", Name = "Spitter", En = "Spitter", Aliases = new string[0] },
            new Row { Sub = "特感", Name = "Tank", En = "Tank", Aliases = new string[0] },
            new Row { Sub = "特感", Name = "Witch", En = "Witch", Aliases = new string[0] },
            new Row { Sub = "普通感染者", Name = "普通感染者", En = "Common infected", Aliases = new string[0] },
            new Row { Sub = "可搬道具", Name = "氧气瓶", En = "Oxygen Tank", Aliases = new string[0] },
            new Row { Sub = "可搬道具", Name = "煤气罐", En = "Gas Can", Aliases = new string[0] },
            new Row { Sub = "可搬道具", Name = "油桶", En = "Oil Barrel", Aliases = new string[0] },
            new Row { Sub = "可搬道具", Name = "燃料桶", En = "Fuel Barrel", Aliases = new string[0] },
            new Row { Sub = "可搬道具", Name = "烟花盒", En = "Fireworks Crate", Aliases = new string[0] },
            new Row { Sub = "可搬道具", Name = "地精", En = "Gnome", Aliases = new string[0] },
            new Row { Sub = "可搬道具", Name = "可乐", En = "Cola Bottles", Aliases = new string[0] },
            new Row { Sub = "天空盒", Name = "天空盒", En = "Skybox", Aliases = new string[0] },
            new Row { Sub = "地面细节", Name = "地面细节", En = "Ground detail", Aliases = new string[0] },
            new Row { Sub = "场景贴图", Name = "场景贴图", En = "Scene textures", Aliases = new string[0] },
        };

        internal static Row[] All { get { return _rows; } }
        internal static int Count { get { return _rows.Length; } }

        /// <summary>方案树用的二维表（小分类, 显示名），顺序就是游戏默认顺序。</summary>
        internal static string[,] Order()
        {
            string[,] t = new string[_rows.Length, 2];
            for (int i = 0; i < _rows.Length; i++) { t[i, 0] = _rows[i].Sub; t[i, 1] = _rows[i].Name; }
            return t;
        }

        private static Dictionary<string, Row> _byName;
        private static Dictionary<string, Row> _byAlias;

        private static void Ensure()
        {
            if (_byName != null) return;
            _byName = new Dictionary<string, Row>(StringComparer.Ordinal);
            _byAlias = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            foreach (Row r in _rows)
            {
                _byName[r.Name] = r;
                if (r.Aliases == null) continue;
                foreach (string a in r.Aliases)
                    if (!string.IsNullOrEmpty(a) && !_byAlias.ContainsKey(a)) _byAlias[a] = r;
            }
        }

        /// <summary>标准名 → 行（没有就 null）。</summary>
        internal static Row Find(string name)
        {
            Ensure();
            Row r;
            return name != null && _byName.TryGetValue(name, out r) ? r : null;
        }

        /// <summary>
        /// 名字归一：识别表、老配置、玩家叫法都可能写成别的样子（「15发连狙」「药丸」），
        /// 统一映射到本表的显示名；对不上就原样返回，交给「未归类」处理。
        /// </summary>
        internal static string Canonical(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            Ensure();
            Row r;
            if (_byName.TryGetValue(raw, out r)) return r.Name;
            if (_byAlias.TryGetValue(raw, out r)) return r.Name;
            return raw;
        }

        /// <summary>
        /// 从一段文字（文件名 / addoninfo 标题）里猜它属于哪个位置，按命中长度排序。
        /// 只给「未归类」页提候选用 —— 猜错了不落库，新人点一下才算数。
        /// </summary>
        internal static List<Row> Suggest(string text, int max)
        {
            List<Row> hit = new List<Row>();
            if (string.IsNullOrEmpty(text) || max <= 0) return hit;
            Ensure();
            List<Row> found = new List<Row>();
            List<int> score = new List<int>();
            foreach (Row r in _rows)
            {
                int s = MatchScore(r, text);
                if (s <= 0) continue;
                int at = -1;
                for (int i = 0; i < found.Count; i++) if (found[i] == r) { at = i; break; }
                if (at >= 0) { if (s > score[at]) score[at] = s; continue; }
                found.Add(r); score.Add(s);
            }
            // 命中越长越可信（「军用狙」比「狙」靠谱）
            while (hit.Count < max && found.Count > 0)
            {
                int best = 0;
                for (int i = 1; i < score.Count; i++) if (score[i] > score[best]) best = i;
                hit.Add(found[best]); found.RemoveAt(best); score.RemoveAt(best);
            }
            return hit;
        }

        private static int MatchScore(Row r, string text)
        {
            int best = 0;
            if (!string.IsNullOrEmpty(r.Name) && r.Name.Length >= 2
                && text.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0) best = r.Name.Length;
            if (r.Aliases == null) return best;
            foreach (string a in r.Aliases)
            {
                if (string.IsNullOrEmpty(a) || a.Length < 2) continue;
                if (text.IndexOf(a, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (a.Length > best) best = a.Length;
            }
            return best;
        }

        /// <summary>英文名：表里没写就用显示名（AK-47 这种本来就不该翻译）。</summary>
        internal static string En(string name)
        {
            Row r = Find(name);
            return r == null || string.IsNullOrEmpty(r.En) ? name : r.En;
        }

        /// <summary>自检：别名的目标必须是一个真实位置，别名之间不许撞车。</summary>
        internal static List<string> Audit()
        {
            List<string> bad = new List<string>();
            Ensure();
            Dictionary<string, string> seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Row r in _rows)
            {
                if (string.IsNullOrEmpty(r.Name)) { bad.Add("有空名字的行"); continue; }
                if (string.IsNullOrEmpty(r.En)) bad.Add("位置没英文名: " + r.Name);
                if (r.Aliases == null) continue;
                foreach (string a in r.Aliases)
                {
                    if (string.IsNullOrEmpty(a)) { bad.Add("空别名: " + r.Name); continue; }
                    string owner;
                    if (seen.TryGetValue(a, out owner)) { if (owner != r.Name) bad.Add("别名撞车: " + a + "（" + owner + " / " + r.Name + "）"); continue; }
                    seen[a] = r.Name;
                    if (_byName.ContainsKey(a)) bad.Add("别名和另一个位置同名: " + a);
                }
            }
            return bad;
        }
    }
}