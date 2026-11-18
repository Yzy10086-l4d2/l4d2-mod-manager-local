using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 推荐启动项：只展示 + 复制，工具不替任何人写 Steam。
    /// 「认不认」这些开关，是拿游戏目录里的 exe/dll 搜字符串验的（2026-11-18 实测本机）：
    ///   engine.dll 认 -novid / -console / -high / -heapsize / -insecure / -windowed / -noborder /
    ///     -fullscreen / -startwindowed / -freq / -adapter / -lv / -dev / -allowdebug /
    ///     -nominidumps / -nocrashdialog / -nosound / -sw / -preload / -useforcedmparms；
    ///   materialsystem.dll 认 -dx9；left4neko.dll 认 -heapsize 与 -l4n_*；skeeto.dll 认 -skeeto_*。
    /// -num_edicts / -processheap / -highpriority 在所有游戏文件里都搜不到，写上也不生效。
    /// </summary>
    internal static class LaunchArgs
    {
        /// <summary>推荐的一整串：只收游戏真认、且这台机器装了对应组件的开关。</summary>
        public const string Default =
            "-heapsize 2096999 -high -l4n_use_neko_engine_post -skeeto_particle_platform 1 -novid";

        /// <summary>v1.7 及更早的老默认值，只用来认老配置（现在配置里不再存启动项）。</summary>
        public const string Legacy = "-novid -high";

        /// <summary>换行/多个空格折成一个空格，去首尾 —— Steam 那边只认一行。</summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            return Regex.Replace(s, " {2,}", " ").Trim();
        }

        /// <summary>数一下有几个开关（以 - 开头的段）。</summary>
        public static int CountSwitches(string s)
        {
            s = Clean(s);
            if (s.Length == 0) return 0;
            int n = 0;
            foreach (string part in s.Split(' '))
                if (part.StartsWith("-", StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>自测：清洗 / 推荐串内容。返回失败条数。</summary>
        public static int SelfTest()
        {
            int bad = 0;
            Action<bool, string> chk = delegate(bool ok, string name)
            {
                if (ok) Core.Log("OK", "LAUNCHSELF " + name);
                else { bad++; Core.Log("FAIL", "LAUNCHSELF " + name); }
            };
            chk(Default.Length > 0, "推荐串非空");
            chk(Clean("  -a   -b \r\n\t-c  ") == "-a -b -c", "清洗换行和多空格");
            chk(Clean(null) == "" && Clean("   ") == "", "空值不炸");
            chk(!Default.Contains("0/1"), "推荐串里没有 0/1 占位");
            chk(Default.Contains("-skeeto_particle_platform 1"), "Skeeto 粒子平台明确写 1");
            chk(Default.Contains("-l4n_use_neko_engine_post"), "带 L4N 后处理");
            chk(Default.Contains("-novid") && Default.StartsWith("-heapsize 2096999"), "heapsize 在头、novid 在尾");
            chk(CountSwitches(Default) == 5, "开关数 5（实际 " + CountSwitches(Default) + "）");
            chk(Clean(Default) == Default, "推荐串本身干净");
            chk(CountSwitches("") == 0, "空串开关数 0");
            return bad;
        }
    }

    /// <summary>启动项大全里的一条：参数 / 干什么用 / 建议 / 依据。</summary>
    internal sealed class LaunchOpt
    {
        public readonly string Flag;
        public readonly string What;
        public readonly string Verdict;
        public readonly string Where;
        public readonly bool InDefault;

        public LaunchOpt(string flag, string what, string verdict, string where, bool inDefault)
        {
            Flag = flag; What = what; Verdict = verdict; Where = where; InDefault = inDefault;
        }
    }

    internal static class LaunchCatalog
    {
        public static readonly LaunchOpt[] Items = new LaunchOpt[] {
            new LaunchOpt("-heapsize 2096999",
                "给引擎预租约 2GB 内存池（单位 KB）。L4N 下会被强制锁到 2048MB；原版这个数值有溢出 bug，删掉也能玩。",
                "推荐", "engine.dll / left4neko.dll", true),
            new LaunchOpt("-high",
                "进程优先级调高，抢 CPU 更积极，帧率更稳。",
                "推荐", "engine.dll / client.dll", true),
            new LaunchOpt("-l4n_use_neko_engine_post",
                "启用 L4N 的 Neko 引擎后处理（画面调色/特效）。没装 L4N 时写了无效。",
                "推荐", "left4neko.dll", true),
            new LaunchOpt("-skeeto_particle_platform 1",
                "Skeeto 粒子平台开关：1 开 / 0 关；不写按 0 处理。装了 Skeeto 想要粒子效果就写 1。",
                "推荐", "skeeto.dll", true),
            new LaunchOpt("-novid",
                "跳过开场 Valve 动画，进游戏快几秒。",
                "推荐", "engine.dll", true),
            new LaunchOpt("-num_edicts 4096",
                "金源引擎的实体上限参数，起源引擎（L4D2）根本不认，留着无害、删了也没影响。",
                "多余", "游戏文件里搜不到", false),
            new LaunchOpt("-processheap",
                "老 L4D2 内存补丁年代的写法，游戏文件里搜不到，等于没写。",
                "多余", "游戏文件里搜不到", false),
            new LaunchOpt("-highpriority",
                "和 -high 重复的老写法，游戏文件里搜不到。",
                "多余", "游戏文件里搜不到", false),
            new LaunchOpt("-console",
                "开局直接打开控制台（改 cvar、看指令用）。",
                "可选", "engine.dll", false),
            new LaunchOpt("-insecure",
                "关闭 VAC。只连第三方/自建服时用；开着它进不去官方安全服。",
                "可选", "engine.dll", false),
            new LaunchOpt("-windowed / -noborder",
                "窗口化 / 无边框窗口，配合 -w 1280 -h 720 指定窗口大小。",
                "可选", "engine.dll", false),
            new LaunchOpt("-freq 120",
                "指定显示器刷新率，防止游戏把你锁在 60Hz。数值按自己屏幕填。",
                "可选", "engine.dll", false),
            new LaunchOpt("-lv",
                "降低暴力血腥表现（断肢、血雾少一些）。",
                "可选", "engine.dll", false),
            new LaunchOpt("-nominidumps / -nocrashdialog",
                "崩溃时不写转储文件、不弹崩溃框（省硬盘、不卡住）。",
                "可选", "engine.dll", false),
            new LaunchOpt("-dx9",
                "强制用 DX9 渲染，老显卡或驱动出问题时用。",
                "可选", "materialsystem.dll", false),
            new LaunchOpt("-dev / -allowdebug",
                "开发者模式 / 允许调试。会掉帧，还可能把调试信息露给服务器，平时别开。",
                "别加", "engine.dll / client.dll", false),
            new LaunchOpt("-nosound",
                "全程静音，只用来排查声音问题。",
                "别加", "engine.dll", false),
            new LaunchOpt("-sw",
                "软件渲染（不用显卡加速），画面极慢，只做故障对照。",
                "别加", "engine.dll", false),
        };
    }

    /// <summary>
    /// 推荐启动项大全：只读表格。上面一排按钮能复制推荐整串或选中的那种，双击一行也是复制。
    /// 这里没有任何编辑能力 —— 参数是固定的推荐值，要改就自己复制到 Steam 里改。
    /// </summary>
    public class LaunchCatalogForm : Form
    {
        private ListView lv;
        private Label lblMsg;

        public LaunchCatalogForm()
        {
            Text = Lang.S("推荐启动项大全");
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(900, 520);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;

            Label hint = new Label();
            hint.SetBounds(14, 12, 872, 34);
            hint.Font = Theme.UISmall;
            hint.ForeColor = Theme.Ink2;
            hint.Text = "推荐的一整串（下面「复制推荐整套」可直接拿去 Steam）："
                      + "\r\n" + LaunchArgs.Default
                      + "\r\n「建议」按游戏自己认不认来标：多余 = 写了不生效；别加 = 会掉帧或泄露信息。";
            Controls.Add(hint);

            lv = new ListView();
            lv.SetBounds(14, 74, 872, 364);
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.HideSelection = false;
            lv.GridLines = false;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.Font = Theme.UISmall;
            Theme.Dress(lv);
            lv.Columns.Add(Lang.S("参数"), Dpi.S(210));
            lv.Columns.Add(Lang.S("建议"), Dpi.S(58));
            lv.Columns.Add(Lang.S("作用"), Dpi.S(440));
            lv.Columns.Add(Lang.S("依据"), Dpi.S(150));
            foreach (LaunchOpt o in LaunchCatalog.Items)
            {
                ListViewItem it = new ListViewItem(o.Flag);
                it.SubItems.Add(o.Verdict);
                it.SubItems.Add(o.What);
                it.SubItems.Add(o.Where);
                it.Tag = o;
                if (o.InDefault) it.Font = Theme.UIBold;
                else it.ForeColor = Theme.Ink2;
                lv.Items.Add(it);
            }
            lv.SizeChanged += delegate { FitColumns(); };
            lv.DoubleClick += delegate { CopyOne(); };
            Controls.Add(lv);

            FlatButton ball = new FlatButton();
            ball.Text = Lang.S("复制推荐整套");
            ball.SetBounds(14, 450, 140, Theme.H);
            ball.Click += delegate { CopyText(LaunchArgs.Default, "推荐整串已复制（" + LaunchArgs.CountSwitches(LaunchArgs.Default) + " 个开关）"); };
            Controls.Add(ball);

            FlatButton bone = new FlatButton();
            bone.Text = Lang.S("复制选中这条");
            bone.SetBounds(162, 450, 130, Theme.H);
            bone.Click += delegate { CopyOne(); };
            Controls.Add(bone);

            lblMsg = new Label();
            lblMsg.SetBounds(300, 455, 330, 20);
            lblMsg.Font = Theme.UISmall;
            lblMsg.ForeColor = Theme.Ink3;
            Controls.Add(lblMsg);

            FlatButton bclose = new FlatButton();
            bclose.Text = Lang.S("关闭");
            bclose.SetBounds(770, 450, 116, Theme.H);
            bclose.Click += delegate { Close(); };
            bclose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.Add(bclose);

            AcceptButton = bclose;
            CancelButton = bclose;
        }

        private void FitColumns()
        {
            if (lv == null) return;
            int rest = lv.ClientSize.Width - lv.Columns[0].Width - lv.Columns[1].Width - lv.Columns[3].Width - Dpi.S(6);
            if (rest > 120) lv.Columns[2].Width = rest;
        }

        private void CopyOne()
        {
            if (lv.SelectedItems.Count == 0) { lblMsg.Text = "先点一行，再复制"; return; }
            LaunchOpt o = lv.SelectedItems[0].Tag as LaunchOpt;
            if (o == null) return;
            CopyText(o.Flag, "已复制这条：" + o.Flag);
        }

        private void CopyText(string s, string msg)
        {
            try { Clipboard.SetText(s); lblMsg.Text = msg; }
            catch (Exception ex) { lblMsg.Text = "复制失败：" + ex.Message; }
            Core.Log("OK", msg);
        }
    }
}
