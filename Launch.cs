using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
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
            chk(CountSwitches(Default) == 5, "开关数 5（实际 " + CountSwitches(Default) + "）");
            chk(Clean(Default) == Default, "推荐串本身干净");
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
                "启用 L4N 的 Neko 引擎后处理（画面调色 / 特效）。没装 L4N 时写了无效。",
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
                "关闭 VAC。只连第三方 / 自建服时用；开着它进不去官方安全服。",
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

        /// <summary>建议标签的颜色：推荐绿、可选蓝、多余橙、别加红。</summary>
        public static Color ColorOf(string verdict)
        {
            if (verdict == "推荐") return Color.FromArgb(0, 132, 82);
            if (verdict == "可选") return Color.FromArgb(38, 92, 224);
            if (verdict == "多余") return Color.FromArgb(170, 105, 10);
            return Color.FromArgb(176, 62, 62);
        }
    }

    /// <summary>
    /// 推荐启动项窗口（方案 B）：左边按建议分四组勾选，右边讲清选中那条是干什么的，
    /// 底下实时拼出组合串，一个按钮复制走。这里没有编辑能力：参数是固定的推荐值。
    /// </summary>
    public class LaunchCatalogForm : Form
    {
        private ListView lv;
        private Label lblFlag, lblVerdict, lblWhat, lblWhere, lblCount, lblHint;
        private TextBox txtCombo;
        private FlatButton btnCopy, btnReset;
        private int hoverIdx = -1;     // 鼠标停在列表哪一行（右侧讲解跟着走）

        public LaunchCatalogForm()
        {
            Text = Lang.S("启动项组合");
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(900, 644);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Theme.DressForm(this);
            MinimizeBox = false;
            ShowInTaskbar = false;

            lblHint = new Label();
            lblHint.SetBounds(16, 12, 872, 20);
            lblHint.Font = Theme.UISmall;
            lblHint.ForeColor = Theme.Ink2;
            lblHint.Text = Lang.S("勾上要用的开关（默认勾好推荐那几条）；右边是选中那条的说明，底下一行是拼出来的组合。");
            Controls.Add(lblHint);

            lv = new ListView();
            lv.SetBounds(16, 36, 420, 496);
            lv.View = View.Details;
            lv.CheckBoxes = true;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.HideSelection = false;
            lv.HeaderStyle = ColumnHeaderStyle.None;
            lv.Font = Theme.UISmall;
            // 不设底色的话整个开关列表是一片系统白，深色窗口里像贴了张打印纸
            lv.BackColor = Theme.Surface;
            lv.ForeColor = Theme.Ink;
            Theme.Dress(lv);
            lv.Columns.Add("", Dpi.S(400));
            lv.SizeChanged += delegate { if (lv.Columns.Count > 0) lv.Columns[0].Width = Math.Max(120, lv.ClientSize.Width - Dpi.S(4)); };
            string[] groups = new string[] { "推荐（装了对应组件就勾）", "可选（按需）", "多余（写了不生效）", "别加" };
            foreach (string g in groups)
            {
                ListViewGroup grp = new ListViewGroup(Lang.S(g));
                lv.Groups.Add(grp);
            }
            for (int i = 0; i < groups.Length; i++)
                lv.Groups[i].Tag = i;
            foreach (LaunchOpt o in LaunchCatalog.Items)
            {
                ListViewItem it = new ListViewItem(o.Flag);
                it.Tag = o;
                it.Checked = o.InDefault;
                it.ForeColor = LaunchCatalog.ColorOf(o.Verdict);
                it.Group = lv.Groups[VerdictIndex(o.Verdict)];
                lv.Items.Add(it);
            }
            lv.ItemChecked += delegate { RefreshCombo(); };
            lv.SelectedIndexChanged += delegate
            {
                hoverIdx = lv.SelectedIndices.Count > 0 ? lv.SelectedIndices[0] : -1;
                ShowDetail();
            };
            // 右侧讲解跟着鼠标走：不用先点一下再看说明，扫过去就换内容
            lv.MouseMove += delegate(object s2, MouseEventArgs e2)
            {
                ListViewItem hit = lv.GetItemAt(e2.X, e2.Y);
                if (hit == null || hit.Index == hoverIdx) return;
                hoverIdx = hit.Index;
                if (!hit.Selected) hit.Selected = true;
            };
            lv.MouseLeave += delegate { hoverIdx = -1; };
            Controls.Add(lv);
            if (lv.Items.Count > 0) lv.Items[0].Selected = true;

            btnReset = new FlatButton();
            btnReset.SetBounds(14, 540, 118, Theme.H);
            btnReset.Text = Lang.S("恢复推荐勾选");
            btnReset.Click += delegate { ResetChecks(); };
            Controls.Add(btnReset);

            Panel right = new Panel();
            right.SetBounds(448, 36, 440, 496);
            right.BackColor = Theme.Surface;
            right.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(right);

            lblFlag = new Label();
            lblFlag.SetBounds(16, 12, 412, 28);
            lblFlag.Font = Theme.Mono;
            lblFlag.ForeColor = Theme.Ink;
            right.Controls.Add(lblFlag);

            lblVerdict = new Label();
            lblVerdict.SetBounds(16, 44, 412, 20);
            lblVerdict.Font = Theme.UIBold;
            right.Controls.Add(lblVerdict);

            lblWhat = new Label();
            lblWhat.SetBounds(16, 76, 412, 152);
            lblWhat.Font = Theme.UI;
            lblWhat.ForeColor = Theme.Ink2;
            right.Controls.Add(lblWhat);

            lblWhere = new Label();
            lblWhere.SetBounds(16, 176, 412, 60);
            lblWhere.Font = Theme.UISmall;
            lblWhere.ForeColor = Theme.Ink3;
            right.Controls.Add(lblWhere);

            lblCount = new Label();
            lblCount.SetBounds(144, 544, 560, 20);
            lblCount.Font = Theme.UISmall;
            lblCount.ForeColor = Theme.Ink3;
            Controls.Add(lblCount);

            txtCombo = new TextBox();
            txtCombo.SetBounds(16, 576, 700, 24);
            txtCombo.ReadOnly = true;
            Theme.Dress(txtCombo);
            txtCombo.BackColor = Theme.Surface;
            Controls.Add(txtCombo);

            btnCopy = new FlatButton();
            btnCopy.SetBounds(722, 574, 164, Theme.H);
            btnCopy.Text = Lang.S("一键复制组合");
            btnCopy.Click += delegate { CopyCombo(); };
            Controls.Add(btnCopy);

            AcceptButton = btnCopy;
            ShowDetail();
            RefreshCombo();
        }

        private static int VerdictIndex(string verdict)
        {
            if (verdict == "推荐") return 0;
            if (verdict == "可选") return 1;
            if (verdict == "多余") return 2;
            return 3;
        }

        private void ResetChecks()
        {
            foreach (ListViewItem it in lv.Items)
            {
                LaunchOpt o = it.Tag as LaunchOpt;
                if (o != null) it.Checked = o.InDefault;
            }
            RefreshCombo();
        }

        /// <summary>当前勾选的开关，按表格从上到下的顺序拼。</summary>
        private string Compose()
        {
            StringBuilder sb = new StringBuilder();
            foreach (ListViewItem it in lv.Items)
            {
                if (!it.Checked) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(it.Text);
            }
            return sb.ToString();
        }

        private void RefreshCombo()
        {
            string s = Compose();
            int n = LaunchArgs.CountSwitches(s);
            txtCombo.Text = s;
            lblCount.Text = n == 0
                ? Lang.S("还没勾任何开关 —— 勾上至少一条才能复制")
                : Lang.F("已勾 {0} 个开关，拼出来的就是左边这串", n);
            btnCopy.Enabled = n > 0;
        }

        private void ShowDetail()
        {
            LaunchOpt o = null;
            if (lv.SelectedItems.Count > 0) o = lv.SelectedItems[0].Tag as LaunchOpt;
            if (o == null)
            {
                lblFlag.Text = "";
                lblVerdict.Text = "";
                lblWhat.Text = "";
                lblWhere.Text = "";
                return;
            }
            lblFlag.Text = o.Flag;
            lblVerdict.Text = Lang.F("建议：{0}", o.Verdict);
            lblVerdict.ForeColor = LaunchCatalog.ColorOf(o.Verdict);
            lblWhat.Text = o.What;
            lblWhere.Text = Lang.F("依据：{0}", o.Where);
        }

        private void CopyCombo()
        {
            string s = Compose();
            if (s.Length == 0) return;
            try
            {
                Clipboard.SetText(s);
                btnCopy.Text = Lang.S("已复制");
                Core.Log("OK", "启动项组合已复制（" + LaunchArgs.CountSwitches(s) + " 个开关）");
            }
            catch (Exception ex)
            {
                Core.Log("WARN", "复制启动项组合失败: " + ex.Message);
                btnCopy.Text = Lang.S("复制失败");
            }
        }
    }
}
