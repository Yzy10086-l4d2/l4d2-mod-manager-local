using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 设置窗口：只放「本机偏好」和路径入口，不重复安装时逐次选择的组件。
    /// 版式参照 iOS 的分组列表：卡片圆角、组标题在卡片外、行内「标题 + 说明」、控件靠右。
    /// U 盘模式可指定主库所在盘；本地模式只扫描游戏目录，忽略所有外部库。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly MainForm owner;
        private Segmented segAppearance, segLang, segName;
        private Toggle tglMotion;
        private ComboBox cboGame;
        private Label lblKeyState;
        private CheckBox chkSteam, chkGb;
        private CheckBox chkMultiConn;      // 实验：单文件双连接下载
        private TextBox txtApiKey;
        private FlatButton btnGameBrowse, btnGameFind, btnSave;
        private FlatButton btnImport;
        private Label lblGameState, lblCfg, lblLog;
        private bool syncing = true;

        internal bool WantReduceMotion { get { return tglMotion != null && tglMotion.Checked; } }
        /// <summary>true = 用包内 addoninfo 的标题当显示名；false = 用 vpk 文件名。</summary>
        internal bool WantNameByTitle { get { return segName != null && segName.SelectedIndex == 1; } }
        /// <summary>自测用：模拟在设置窗口里点「外观」分段（走的是真实的点击 + SelectedIndexChanged 路径）。</summary>
        internal void PreviewAppearance(ThemeMode m)
        {
            syncing = false;
            segAppearance.PickIndex((int)m);
        }


        /// <summary>输入框里的游戏根目录（已去空格和尾部反斜杠）。空串表示用户没填。</summary>
        internal string WantGameRoot
        {
            get
            {
                if (cboGame == null) return "";
                string s = cboGame.Text.Trim().Trim('"').TrimEnd('\\');
                return s;
            }
        }

        public SettingsForm(MainForm owner)
        {
            this.owner = owner;

            Text = Lang.S("设置 · L4D2 MOD 管理工具");
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(620, 934);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            DoubleBuffered = true;

            Label title = new Label();
            title.Text = Lang.S("设置");
            title.SetBounds(20, 16, 300, 32);
            title.Font = Theme.H1;
            title.ForeColor = Theme.Ink;
            title.BackColor = Color.Transparent;
            Controls.Add(title);

            BuildGeneral();
            BuildGameDir();
            BuildWorkshop();
            BuildDiagnostics();
            BuildFooter();

            syncing = false;
            UpdateGameState();

            Load += delegate { Theme.ApplyChrome(this); };
            Shown += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };
        }

        // ---------------------------------------------------------- 版式零件

        /// <summary>分组标题 + 圆角卡片。标题在卡片外，是 iOS 分组列表的固定写法。</summary>
        private Card AddGroup(string caption, int top, int height)
        {
            Label head = new Label();
            head.Text = caption;
            head.SetBounds(24, top, 400, 16);
            head.Font = Theme.UI;
            head.ForeColor = Theme.Ink3;
            head.BackColor = Color.Transparent;
            Controls.Add(head);

            Card card = new Card();
            card.SetBounds(20, top + 18, ClientSize.Width - 40, height);
            Controls.Add(card);
            return card;
        }

        private Label RowTitle(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 18);
            l.Font = Theme.RowTitle;
            l.ForeColor = Theme.Ink;
            Theme.Dress(l);
            l.ForeColor = Theme.Ink;
            parent.Controls.Add(l);
            return l;
        }

        private Label RowNote(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 16);
            l.Font = Theme.UISmall;
            l.ForeColor = Theme.Ink3;
            l.BackColor = Color.Transparent;
            l.AutoEllipsis = true;
            parent.Controls.Add(l);
            return l;
        }

        private FlatButton SoftButton(Control parent, string text, int x, int y, int w, int h)
        {
            FlatButton b = new FlatButton();
            b.Text = Lang.S(text);        // 小按钮都从这里出，文案统一过一遍语言层
            b.Soft = true;
            b.SetBounds(x, y, w, h);
            parent.Controls.Add(b);
            return b;
        }

        // ---------------------------------------------------------- 分组

        private void BuildGeneral()
        {
            Card s = AddGroup(Lang.S("常规"), 56, 248);

            RowTitle(s, Lang.S("减少动态效果"), 16, 8, 220);
            RowNote(s, Lang.S("关掉按钮与状态切换的过渡，低配机器更轻快"), 16, 28, 320);

            tglMotion = new Toggle();
            tglMotion.SetBounds(520, 16, 44, 28);
            tglMotion.Checked = Core.Cfg.ReduceMotion;
            s.Controls.Add(tglMotion);

            s.Separate(96);

            // 外观：纯粹的显示开关，没有「生效代价」，所以点了就立刻应用并落配置。
            // 不跟着「保存设置」走 —— 用户点完看到颜色变了，关掉窗口却又变回去，会当成 bug。
            RowTitle(s, Lang.S("外观"), 16, 104, 220);
            RowNote(s, Lang.S("跟随系统 = 跟 Windows 走；选了立即生效"), 16, 124, 310);

            segAppearance = new Segmented();
            segAppearance.Items = new string[] { "跟随系统", "浅色", "深色" };
            segAppearance.SetBounds(332, 108, 232, 32);
            segAppearance.SelectedIndex = (int)Theme.Mode;
            segAppearance.SelectedIndexChanged += delegate
            {
                if (syncing) return;
                Theme.SetMode((ThemeMode)segAppearance.SelectedIndex);
                Theme.ApplyToOpenForms();
                try
                {
                    // 模式键一定要落盘：从「跟随系统」切到「浅色」时配色可能一模一样，
                    // 但配置里存的键不一样，下次启动的行为也会不一样。
                    Core.Cfg.Appearance = Theme.ModeKey;
                    Core.SaveConfig();
                    Core.Log("INFO", "设置：外观→" + Theme.ModeLabel);
                }
                catch { }
            };
            s.Controls.Add(segAppearance);

            s.Separate(148);

            // 语言：和外观一样「点了就生效」，但它必须重建界面（控件文案是构造时定下的），
            // 所以这里只落配置 + 打标记，由主窗口关掉自己、Program 那边重建一次。
            RowTitle(s, Lang.S("语言 / Language"), 16, 152, 240);
            RowNote(s, Lang.S("切换后界面立即重建，其它设置不会丢"), 16, 172, 320);

            segLang = new Segmented();
            segLang.Items = new string[] { "中文", "English" };
            segLang.SetBounds(332, 156, 232, 32);
            segLang.SelectedIndex = Lang.IsEn ? 1 : 0;
            segLang.SelectedIndexChanged += delegate
            {
                if (syncing) return;
                string want = segLang.SelectedIndex == 1 ? Lang.EN : Lang.ZH;
                if (string.Equals(Core.Cfg.Lang, want, StringComparison.OrdinalIgnoreCase)) return;
                Lang.Set(want);
                Core.Log("INFO", "设置：界面语言→" + (Lang.IsEn ? "English" : "中文"));
                if (owner != null) owner.RequestLangRestart();
            };
            s.Controls.Add(segLang);

            s.Separate(192);

            // MOD 名称：只影响显示和搜索，一个文件都不动。两种来源库里都现成：
            //   文件名 = vpk 原名；标题 = 包内 addoninfo 的 AddonTitle（工坊名那种）。
            RowTitle(s, Lang.S("MOD 名称"), 16, 200, 220);
            RowNote(s, Lang.S("文件名 = vpk 原名；标题 = 包内 addoninfo 的名字"), 16, 220, 320);

            segName = new Segmented();
            segName.Items = new string[] { Lang.S("文件名"), Lang.S("标题") };
            segName.SetBounds(332, 208, 232, 32);
            segName.SelectedIndex = Core.Cfg.NameByTitle ? 1 : 0;
            segName.SelectedIndexChanged += delegate
            {
                bool want = segName.SelectedIndex == 1;
                if (want == Core.Cfg.NameByTitle) return;
                Core.Cfg.NameByTitle = want;
                Core.SaveConfig();
                Core.Log("INFO", "设置：MOD 名称→" + (want ? "包内标题" : "文件名"));
                if (owner != null) { owner.RebuildViews(); owner.RefreshMods(); }
            };
            s.Controls.Add(segName);
        }

        /// <summary>只读提示：留空时把「自动会用到哪个目录」写出来，填了就报里面有没有组件包。</summary>


        /// <summary>
        /// 游戏目录：两种模式共用。U 盘模式决定装到哪，本地模式还额外决定扫哪里，
        /// 所以单独成组，不再只藏在主界面顶部那一行。
        /// </summary>
        private void BuildGameDir()
        {
            Card s = AddGroup(Lang.S("游戏目录"), 344, 112);

            RowTitle(s, Lang.S("游戏根目录"), 16, 8, 200);

            cboGame = new ComboBox();
            cboGame.SetBounds(16, 32, 348, 28);
            cboGame.DropDownStyle = ComboBoxStyle.DropDown;
            Theme.Dress(cboGame);
            cboGame.Text = Core.Cfg.GameRoot;
            cboGame.TextChanged += delegate { if (!syncing) UpdateGameState(); };
            s.Controls.Add(cboGame);

            btnGameBrowse = SoftButton(s, "浏览…", 376, 32, 76, 28);
            btnGameBrowse.Click += delegate { BrowseGameRoot(); };

            btnGameFind = SoftButton(s, "自动找", 456, 32, 86, 28);
            btnGameFind.Click += delegate { FindGameRoot(); };

            s.Separate(64);

            lblGameState = new Label();
            lblGameState.SetBounds(16, 72, 548, 16);
            lblGameState.Font = Theme.UISmall;
            lblGameState.AutoEllipsis = true;
            s.Controls.Add(lblGameState);

            RowNote(s, Lang.S("选里面有 left4dead2.exe 的那一层；本地模式只扫这里的 left4dead2\\addons。"), 16, 88, 548);
        }

        /// <summary>
        /// 工坊用哪条路：填了 key 走 api.steampowered.com（不经过被墙的网页），留空保持原来的网页路由。
        /// 这一栏是唯一一个「用户要自己弄」的设置，所以旁边必须写清怎么拿 key。
        /// </summary>
        private void BuildWorkshop()
        {
            Card s = AddGroup(Lang.S("MOD 来源"), 496, 156);

            // 左：Steam 工坊 + key
            RowTitle(s, Lang.S("Steam 创意工坊"), 16, 8, 250);
            chkSteam = new CheckBox();
            chkSteam.Text = Lang.S("启用");
            chkSteam.SetBounds(16, 30, 240, 22);
            chkSteam.Font = Theme.UI;
            chkSteam.Checked = Core.Cfg.SteamEnabled;
            s.Controls.Add(chkSteam);

            RowNote(s, Lang.S("全站搜索必须填 key；不填只能按 ID / 合集导入"), 16, 54, 250);

            txtApiKey = new TextBox();
            txtApiKey.SetBounds(16, 76, 168, 26);
            txtApiKey.BorderStyle = BorderStyle.FixedSingle;
            txtApiKey.Font = Theme.UI;
            txtApiKey.Text = Core.Cfg.WsApiKey ?? "";
            Theme.Dress(txtApiKey);
            s.Controls.Add(txtApiKey);

            FlatButton help = SoftButton(s, Lang.S("怎么拿 key"), 192, 76, 76, 26);
            help.Click += delegate { ShowApiKeyHelp(); };

            lblKeyState = new Label();
            lblKeyState.SetBounds(16, 108, 250, 18);
            lblKeyState.Font = Theme.UISmall;
            lblKeyState.ForeColor = Theme.Ink3;
            lblKeyState.AutoEllipsis = true;
            s.Controls.Add(lblKeyState);
            UpdateKeyState();

            // 右：GameBanana
            RowTitle(s, Lang.S("GameBanana"), 292, 8, 250);
            chkGb = new CheckBox();
            chkGb.Text = Lang.S("启用");
            chkGb.SetBounds(292, 30, 240, 22);
            chkGb.Font = Theme.UI;
            chkGb.Checked = Sources.IsEnabled("gb");
            s.Controls.Add(chkGb);

            RowNote(s, Lang.S("第三方站点，非 Steam 生态；L4D2 约 1800 个 MOD。默认关闭，Steam 不通时可当兜底"), 292, 54, 250);

            // 实验开关：单文件开两条连接。默认关 —— 实测在线路抖动时可能更慢，效果因网络而异。
            chkMultiConn = new CheckBox();
            chkMultiConn.Text = Lang.S("单文件双连接下载（实验）");
            chkMultiConn.SetBounds(292, 84, 250, 22);
            chkMultiConn.Font = Theme.UI;
            chkMultiConn.Checked = Core.Cfg.MultiConnDownload;
            s.Controls.Add(chkMultiConn);
            RowNote(s, Lang.S("同一文件切成两段同时下；服务器不支持或任一段失败会自动退回单连接。效果看线路，不灵就关掉"), 292, 108, 250);
        }

        /// <summary>
        /// 「怎么拿 key」：开向导，一步一步走完再把 key 交回输入框。
        /// 真正落盘仍归「保存设置」—— 向导不另开一处写配置的地方。
        /// </summary>
        private void ShowApiKeyHelp()
        {
            using (KeyGuideForm w = new KeyGuideForm(txtApiKey == null ? "" : txtApiKey.Text.Trim()))
            {
                if (w.ShowDialog(this) != DialogResult.OK) return;
                if (txtApiKey != null)
                {
                    txtApiKey.Text = w.Key;
                    txtApiKey.SelectionStart = txtApiKey.Text.Length;
                    txtApiKey.SelectionLength = 0;
                }
                UpdateKeyState();
            }
        }

        /// <summary>key 那一行状态：填没填、API 现在通不通。</summary>
        private void UpdateKeyState()
        {
            if (lblKeyState == null) return;
            try
            {
                bool hasKey = (Core.Cfg.WsApiKey ?? "").Trim().Length > 0;
                string api = WsNet.ApiOffline ? "API 最近失败过" : "API 可用";
                lblKeyState.Text = (hasKey ? "key：已填（可全站搜）" : "key：未填（只能按 ID/合集导入）") + "　·　" + api;
            }
            catch { lblKeyState.Text = ""; }
        }

        internal bool WantMultiConn { get { return chkMultiConn != null && chkMultiConn.Checked; } }
        internal bool WantSteamEnabled { get { return chkSteam != null && chkSteam.Checked; } }
        internal bool WantGbEnabled { get { return chkGb != null && chkGb.Checked; } }
        internal string WantApiKey { get { return txtApiKey == null ? "" : txtApiKey.Text.Trim(); } }

        private void BuildDiagnostics()
        {
            Card s = AddGroup(Lang.S("位置与诊断"), 676, 120);

            lblCfg = new Label();
            lblCfg.SetBounds(16, 8, 548, 16);
            lblCfg.Font = Theme.UISmall;
            lblCfg.ForeColor = Theme.Ink2;
            lblCfg.AutoEllipsis = true;
            lblCfg.Text = Lang.F("配置：{0}", Core.CfgPath);
            s.Controls.Add(lblCfg);

            lblLog = new Label();
            lblLog.SetBounds(16, 28, 548, 16);
            lblLog.Font = Theme.UISmall;
            lblLog.ForeColor = Theme.Ink2;
            lblLog.AutoEllipsis = true;
            lblLog.Text = Lang.F("日志：{0}", Core.LogDir);
            s.Controls.Add(lblLog);

            // 组件包目录（L4N / Skeeto / MQCam / dxvk）属于便携版：本地版不往游戏里复制任何东西，
            // 这一栏在本地版里没有意义，整块去掉。
            s.Separate(48);

            FlatButton openCfg = SoftButton(s, Lang.S("打开配置文件夹"), 16, 52, 124, 28);
            openCfg.Click += delegate { OpenConfigFolder(); };

            FlatButton openLog = SoftButton(s, Lang.S("打开日志"), 148, 52, 84, 28);
            openLog.Click += delegate { OpenFolder(Core.LogDir); };

            FlatButton env = SoftButton(s, Lang.S("环境自检"), 240, 52, 88, 28);
            env.Click += delegate { if (owner != null) owner.ShowEnv(); };

            btnImport = SoftButton(s, Lang.S("导入压缩包…"), 336, 52, 116, 28);
            btnImport.Click += delegate { ImportFromDialog(); };

            FlatButton lines = SoftButton(s, Lang.S("测工坊线路"), 460, 52, 108, 28);
            lines.Click += delegate { TestLines(); };

            FlatButton covers = SoftButton(s, Lang.S("补封面"), 460, 88, 108, 28);
            covers.Click += delegate { BackfillCovers(); };
        }

        /// <summary>补封面：库里带工坊 ID 的 vpk，能安全写进去的把封面写进包，写不进的退回旁边放图。</summary>
        private void BackfillCovers()
        {
            lblCfg.Text = Lang.S("正在补封面…");
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                int failed = 0;
                int ok = 0;
                try { ok = Workshop.BackfillCovers(delegate(string s) { }, out failed); } catch { }
                BeginInvoke((MethodInvoker)delegate()
                {
                    lblCfg.Text = Lang.F("配置：{0}", Core.CfgPath);
                    MessageBox.Show(this, Lang.F("补封面完成：写进 vpk {0} 个，跳过 {1} 个。\r\n\r\n跳过的是多卷/带 preload 的包（重写有风险），仍用旁边的同名图片。", ok, failed),
                        Lang.S("补封面"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 工坊线路自检：三根线各通不通、系统代理在不在。给用户一个「现在该用哪条路」的凭据，
        /// 不用点分类位置干等十几秒去猜。
        /// </summary>
        private void TestLines()
        {
            lblCfg.Text = Lang.S("正在测线路…");
            lblCfg.ForeColor = Theme.Ink2;
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                string api = WsNet.ProbeLine("https://api.steampowered.com/");
                string cdn = WsNet.ProbeLine("https://cdn.steamusercontent.com/");
                string web = WsNet.ProbeLine("https://steamcommunity.com/");
                string px = WsNet.SystemProxyDesc();
                string key = ((Core.Cfg.WsApiKey ?? "").Trim().Length > 0) ? "已填" : "未填";
                string msg = "api.steampowered.com：" + api + "\r\n"
                    + "cdn.steamusercontent.com：" + cdn + "\r\n"
                    + "steamcommunity.com：" + web + "\r\n\r\n"
                    + "系统代理：" + px + "\r\n"
                    + "Steam API key：" + key;
                BeginInvoke((MethodInvoker)delegate()
                {
                    lblCfg.Text = Lang.F("配置：{0}", Core.CfgPath);
                    MessageBox.Show(this, msg, Lang.S("工坊线路自检"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>把 vpk / 压缩包里的 vpk 搬进 addons，然后重扫一遍让树认出来。</summary>
        private void ImportFromDialog()
        {
            if (owner == null) return;
            string addons;
            try { addons = Core.GameAddonsDir(); }
            catch (Exception ex) { MessageBox.Show(this, "先指定游戏目录。\r\n\r\n" + ex.Message, Text); return; }
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = Lang.S("选要导入的包或 vpk");
                d.Filter = "压缩包或 vpk|*.vpk;*.zip;*.rar;*.7z|所有文件|*.*";
                d.Multiselect = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                int added, skipped;
                List<string> errs;
                string msg = Catalog.ImportTo(d.FileNames, addons, out added, out skipped, out errs);
                Core.Log(added > 0 ? "OK" : "WARN", "导入压缩包: " + msg.Replace("\r\n", " / "));
                MessageBox.Show(this, msg, Lang.S("导入结果"));
                if (added > 0) owner.RescanMods();
            }
        }

        // 安装选项整块已删除：严格校验 / 跳过·覆盖重装 / 换整套 都是复制或切换时代的开关，
        // 本地版只把勾选写成 addonlist 的 1/0，这三项都没意义。

        private void BuildFooter()
        {
            Panel rule = new Panel();
            rule.SetBounds(20, 826, 580, 0);
            rule.BackColor = Theme.Line;
            Controls.Add(rule);

            Label note = new Label();
            note.Text = Lang.S("保存后立即生效，不改已经装进游戏的文件。");
            note.SetBounds(20, 830, 360, 20);
            note.Font = Theme.UISmall;
            note.ForeColor = Theme.Ink3;
            note.BackColor = Color.Transparent;
            Controls.Add(note);

            FlatButton cancel = new FlatButton();
            cancel.Text = Lang.S("取消");
            cancel.Plain = true;
            cancel.SetBounds(408, 878, 88, 32);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            btnSave = new FlatButton();
            btnSave.Text = Lang.S("保存设置");
            btnSave.SetBounds(504, 878, 96, 32);
            btnSave.Primary = true;
            btnSave.DialogResult = DialogResult.OK;
            // 校验放进点击里：以前是「先关窗、再在 ShowSettings 里发现路径不对」，
            // 用户填的东西跟着窗口一起没了，只能重新打开重填一遍。
            btnSave.Click += delegate
            {
                string err = ValidateBeforeSave();
                if (err == null) return;
                MessageBox.Show(this, err, "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;      // 关键：不让窗口关掉
            };
            Controls.Add(btnSave);

            AcceptButton = btnSave;
            CancelButton = cancel;
        }

        /// <summary>保存前就地校验：返回 null = 可以存；否则是要弹给用户的提示。</summary>
        private string ValidateBeforeSave()
        {
            // 留空 = 不改：没填就保存不该把原来能用的游戏目录清掉
            string game = WantGameRoot;
            if (game.Length > 0 && !Core.TestGame(game))
                return "这个目录里没有 left4dead2.exe，设置没有保存：\r\n" + game;
            return null;
        }

        // ---------------------------------------------------------- 游戏目录

        private void UpdateGameState()
        {
            if (lblGameState == null) return;
            string root = WantGameRoot;
            if (root.Length == 0)
            {
                bool hasOld = Core.TestGame(Core.Cfg.GameRoot ?? "");
                lblGameState.ForeColor = hasOld ? Theme.Ink3 : Theme.Accent;
                lblGameState.Text = hasOld
                    ? "未填写：保存时保持当前目录不变"
                    : "未设置：安装前必须先指定游戏根目录";
            }
            else if (Core.TestGame(root))
            {
                lblGameState.ForeColor = Theme.Ok;
                lblGameState.Text = Lang.S("已识别：left4dead2.exe 在里面，可以扫描和安装");
            }
            else
            {
                lblGameState.ForeColor = Theme.Accent;
                lblGameState.Text = Lang.S("没认出：这一层找不到 left4dead2.exe");
            }
        }

        private void BrowseGameRoot()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选游戏根目录（里面有 left4dead2.exe 的那个文件夹）";
                string cur = WantGameRoot;
                if (cur.Length > 0 && Directory.Exists(cur)) d.SelectedPath = cur;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                cboGame.Text = d.SelectedPath.TrimEnd('\\');
                cboGame.SelectionStart = cboGame.Text.Length;
                cboGame.SelectionLength = 0;
            }
        }

        private void FindGameRoot()
        {
            List<string> found = Core.FindGames();
            if (found == null || found.Count == 0)
            {
                lblGameState.ForeColor = Theme.Accent;
                lblGameState.Text = Lang.S("没自动找到游戏，请用「浏览…」手动选");
                return;
            }
            cboGame.Text = found[0];
            if (found.Count > 1)
            {
                lblGameState.ForeColor = Theme.Warn;
                lblGameState.Text = "找到 " + found.Count + " 个，已填第一个；不对就点「浏览…」改";
            }
        }





        /// <summary>选盘时只认常见的库目录，不整盘递归；找不到就让用户明确选文件夹。</summary>





        private void OpenConfigFolder()
        {
            try
            {
                if (File.Exists(Core.CfgPath))
                    Process.Start("explorer.exe", "/select,\"" + Core.CfgPath + "\"");
                else
                    OpenFolder(Core.PkgDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打不开配置文件夹：" + ex.Message, "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void OpenFolder(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir)) Process.Start("explorer.exe", dir);
            }
            catch { }
        }

        internal string LayoutDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("设置窗口客户区: " + ClientSize.Width + " x " + ClientSize.Height);
            sb.AppendLine("本地版：只看游戏目录 addons，没有主库");
            sb.AppendLine("游戏目录: " + (WantGameRoot.Length == 0 ? "(未填)" : WantGameRoot));
            sb.AppendLine("游戏目录有效: " + Core.TestGame(WantGameRoot));
            sb.AppendLine("减少动效: " + (WantReduceMotion ? "开" : "关"));
            sb.AppendLine("MOD 名称: " + (WantNameByTitle ? "包内标题" : "文件名"));
            sb.AppendLine("外观: " + Theme.ModeLabel + "（当前渲染: " + (Theme.IsDark ? "深色" : "浅色") + "）");
            sb.AppendLine("附加库目录: " + (Core.Cfg.ModDirs == null ? 0 : Core.Cfg.ModDirs.Count) + " 个");
            sb.AppendLine("配置路径: " + Core.CfgPath);
            sb.AppendLine("日志路径: " + Core.LogDir);
            return sb.ToString();
        }
    }

    public partial class MainForm
    {
        /// <summary>自测：把设置窗口放到屏幕外渲染一张，检查布局和当前值。不会保存任何设置。</summary>
        internal void UiTestSettings()
        {
            using (SettingsForm f = new SettingsForm(this))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();
                f.PerformLayout();
                Application.DoEvents();

                Size full = f.Size;
                using (Bitmap bmp = new Bitmap(full.Width, full.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, full.Width, full.Height));
                    bmp.Save(Path.Combine(Core.LogDir, "uitest_settings.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                File.WriteAllText(Path.Combine(Core.LogDir, "uitest_settings.txt"), f.LayoutDump(), new UTF8Encoding(true));
                Core.Log("INFO", "SETTINGS 设置窗口自测完成");
            }
        }

        /// <summary>
        /// 自测：开着设置窗口连续切外观（浅→深→跟随→深→浅），每一步都渲染一张图。
        /// 切换过程中任何异常都记进日志，返回失败条数。
        /// </summary>
        internal int UiTestTheme()
        {
            int bad = 0;
            ThemeMode[] seq = new ThemeMode[]
            {
                ThemeMode.Light, ThemeMode.Dark, ThemeMode.System, ThemeMode.Dark, ThemeMode.Light
            };
            using (SettingsForm f = new SettingsForm(this))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();
                for (int i = 0; i < seq.Length; i++)
                {
                    try
                    {
                        Theme.SetMode(seq[i]);
                        Theme.ApplyToOpenForms();
                        Application.DoEvents();
                        PerformLayout();
                        f.PerformLayout();
                        Application.DoEvents();

                        string tag = "theme" + i + "_" + Theme.ModeLabel;
                        Size fs = f.Size;
                        using (Bitmap b = new Bitmap(fs.Width, fs.Height))
                        {
                            f.DrawToBitmap(b, new Rectangle(0, 0, fs.Width, fs.Height));
                            b.Save(Path.Combine(Core.LogDir, tag + "_settings.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Size ms = Size;
                        using (Bitmap b = new Bitmap(ms.Width, ms.Height))
                        {
                            DrawToBitmap(b, new Rectangle(0, 0, ms.Width, ms.Height));
                            b.Save(Path.Combine(Core.LogDir, tag + "_main.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Core.Log("INFO", "THEME " + i + " → " + Theme.ModeLabel + "（渲染 " + (Theme.IsDark ? "深色" : "浅色") + "）");
                    }
                    catch (Exception ex)
                    {
                        bad++;
                        Core.Log("FAIL", "THEME 切换失败 " + seq[i] + ": " + ex);
                    }
                }
            }
            Theme.SetMode(Theme.Parse(Core.Cfg.Appearance));
            Theme.ApplyToOpenForms();
            Core.Log("OK", "THEME 自测结束：失败 " + bad + " 条");
            return bad;
        }

        private void ShowSettings()
        {
            bool oldMotion = Core.Cfg.ReduceMotion;
            string oldGame = Core.Cfg.GameRoot ?? "";

            using (SettingsForm f = new SettingsForm(this))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;

                bool newMotion = f.WantReduceMotion;
                string newGame = f.WantGameRoot;

                // 留空 = 不改：避免没填就点保存，把原来能用的游戏目录清掉
                if (newGame.Length > 0 && !Core.TestGame(newGame))
                {
                    MessageBox.Show("这个目录里没有 left4dead2.exe，设置没有保存：\r\n" + newGame, "设置",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                bool gameChanged = newGame.Length > 0
                    && !string.Equals(newGame.TrimEnd('\\'), oldGame.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

                if (gameChanged)
                {
                    Core.Cfg.GameRoot = newGame;
                    Core.SaveConfig();
                    SetGameRootText(newGame);
                    Core.Log("OK", "设置：游戏目录→" + newGame);
                }

                // 安装选项整块已经删掉，这里不再有要写的开关

                if (newMotion != oldMotion)
                {
                    Core.Cfg.ReduceMotion = newMotion;
                    Core.SaveConfig();
                    Core.Log("INFO", newMotion ? "设置：已关闭轻量动效" : "设置：已开启轻量动效");
                }

                if (f.WantMultiConn != Core.Cfg.MultiConnDownload)
                {
                    Core.Cfg.MultiConnDownload = f.WantMultiConn;
                    Core.SaveConfig();
                    Core.Log("INFO", "设置：单文件双连接下载 " + (Core.Cfg.MultiConnDownload ? "开" : "关"));
                }

                // MOD 名称：显示名是渲染时现取的，所以改完只重建视图，不重扫库
                if (f.WantNameByTitle != Core.Cfg.NameByTitle)
                {
                    Core.Cfg.NameByTitle = f.WantNameByTitle;
                    Core.SaveConfig();
                    Core.Log("INFO", "设置：MOD 名称→" + (Core.Cfg.NameByTitle ? "包内标题" : "文件名"));
                    RebuildViews();
                    RefreshMods();          // 主列表的显示名在这一层，树重建不会带着它变
                }

                // 外观在对话框里已经实时套上去了，这里只负责落配置
                if (!string.Equals(Core.Cfg.Appearance, Theme.ModeKey, StringComparison.OrdinalIgnoreCase))
                {
                    Core.Cfg.Appearance = Theme.ModeKey;
                    Core.SaveConfig();
                    Core.Log("INFO", "设置：外观→" + Theme.ModeLabel);
                }

                // 来源开关：Steam（配置项）+ key + GameBanana（写回来源.ini 的 enabled）
                if (f.WantSteamEnabled != Core.Cfg.SteamEnabled)
                {
                    Core.Cfg.SteamEnabled = f.WantSteamEnabled;
                    Core.SaveConfig();
                    Core.Log("INFO", "设置：Steam 来源" + (Core.Cfg.SteamEnabled ? "开" : "关"));
                }
                string newKey = f.WantApiKey;
                if (!string.Equals(newKey, Core.Cfg.WsApiKey ?? "", StringComparison.Ordinal))
                {
                    Core.Cfg.WsApiKey = newKey;
                    Core.SaveConfig();
                    WsNet.Retry();
                    Core.Log("INFO", "设置：Steam API key " + (newKey.Length > 0 ? "已填（" + newKey.Length + " 位）" : "已清空"));
                }
                bool gbWant = f.WantGbEnabled;
                if (gbWant != Sources.IsEnabled("gb"))
                {
                    Sources.SetEnabled("gb", gbWant);
                    Core.Log("INFO", "设置：GameBanana 来源" + (gbWant ? "开" : "关"));
                }



                if (gameChanged) ReloadLibrary("正在按新的游戏目录重扫…");
            }
        }

        /// <summary>设置窗口改了游戏目录后，同步顶部输入框，别让界面还显示旧路径。</summary>
        private void SetGameRootText(string root)
        {
            if (txtGame == null) return;
            txtGame.Text = root;
            txtGame.SelectionStart = txtGame.Text.Length;
            txtGame.SelectionLength = 0;
        }

        /// <summary>主库或模式变更后重扫。extra 为空时状态文案由调用场景决定。</summary>
        private void ReloadLibrary(string extra)
        {
            SetBusy(true);
            SetStatus(extra ?? "正在按新的 MOD 主库重扫…");
            lblStatus.ForeColor = Theme.Ink2;
            bar.Style = ProgressBarStyle.Marquee;

            Thread th = new Thread(delegate()
            {
                string err = null;
                try { Core.ScanMods(); }
                catch (Exception ex) { err = ex.Message; Core.Log("FAIL", "切换主库后重扫失败: " + ex.Message); }
                SafeBeginInvoke(delegate()
                {
                    SetBusy(false);
                    bar.Style = ProgressBarStyle.Blocks;
                    RefreshMods();
                    RefreshConflicts(true);
                    if (treeView != null) treeView.Rebuild();
                    SetStatus(err == null ? "就绪" : "主库已切换，但重扫失败；请看日志");
                    if (err == null) lblStatus.ForeColor = Theme.Ink2;
                });
            });
            th.IsBackground = true;
            th.Start();
        }
    }

    /// <summary>
    /// 「怎么拿 Steam API key」向导：把这件唯一要用户自己动手的事拆成 5 步，
    /// 一路「下一步」走完，和新手机开机引导同一个节奏 —— 一屏只讲一件事，随时看到还剩几步。
    ///
    /// 为什么值得单独一个窗口：原来是一整段 MessageBox 文字，用户得边读边切浏览器、
    /// 再自己找回输入框；这里把「为什么需要」「去哪拿」「填什么」「粘进来验一下」
    /// 收进同一条路径，最后一步把 key 原样交回设置窗口，落盘仍由「保存设置」负责。
    /// </summary>
    internal sealed class KeyGuideForm : Form
    {
        private const int Steps = 5;
        private const int Pad = 28;      // 左/右内边距，与设置窗口同一档
        private const int W = 520;
        private const int Ht = 520;

        private int step;
        private Label lblTitle, lblStep, lblHint, lblResult;
        private Panel[] bar = new Panel[Steps];
        private FlatButton btnBack, btnNext, btnOpen, btnTest;
        private TextBox txtKey;
        private bool testing;

        /// <summary>向导里填的 key（已去首尾空格）。调用方负责把它塞回设置窗口。</summary>
        internal string Key { get { return txtKey == null ? "" : txtKey.Text.Trim(); } }

        public KeyGuideForm(string current)
        {
            Theme.UseDpi(this);
            Text = Lang.S("怎么拿 Steam API key");
            ClientSize = Dpi.Sz(W, Ht);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            DoubleBuffered = true;
            KeyPreview = true;

            lblTitle = new Label();
            lblTitle.SetBounds(Pad, 24, W - Pad * 2, 32);
            lblTitle.Font = Theme.H1;
            Theme.Dress(lblTitle);
            lblTitle.ForeColor = Theme.Ink;
            Controls.Add(lblTitle);

            lblStep = new Label();
            lblStep.SetBounds(Pad, 60, 300, 16);
            lblStep.Font = Theme.UISmall;
            Theme.Dress(lblStep);
            lblStep.ForeColor = Theme.Ink3;
            Controls.Add(lblStep);

            // 进度：5 小段。走过的填 Accent，没走的填 Line —— 一眼知道还差几步。
            int segW = (W - Pad * 2 - (Steps - 1) * 6) / Steps;
            for (int i = 0; i < Steps; i++)
            {
                bar[i] = new Panel();
                bar[i].SetBounds(Pad + i * (segW + 6), 82, segW, 4);
                bar[i].BackColor = Theme.Line;
                Controls.Add(bar[i]);
            }

            lblHint = new Label();
            lblHint.SetBounds(Pad, 104, W - Pad * 2, 200);
            lblHint.Font = Theme.UI;
            Theme.Dress(lblHint);
            lblHint.ForeColor = Theme.Ink2;
            Controls.Add(lblHint);

            // 第 2 步：打开申请页
            btnOpen = new FlatButton();
            btnOpen.Text = Lang.S("打开申请页");
            btnOpen.Soft = true;
            btnOpen.SetBounds(Pad, 312, 132, Theme.H);
            btnOpen.Click += delegate { OpenPage(); };
            Controls.Add(btnOpen);

            // 第 4 步：粘 key + 实测
            txtKey = new TextBox();
            txtKey.SetBounds(Pad, 312, 300, 26);
            txtKey.BorderStyle = BorderStyle.FixedSingle;
            txtKey.Font = Theme.UI;
            txtKey.Text = current == null ? "" : current;
            Theme.Dress(txtKey);
            Controls.Add(txtKey);

            btnTest = new FlatButton();
            btnTest.Text = Lang.S("测试一下");
            btnTest.Soft = true;
            btnTest.SetBounds(Pad + 312, 312, 100, 26);
            btnTest.Click += delegate { RunTest(); };
            Controls.Add(btnTest);

            lblResult = new Label();
            lblResult.SetBounds(Pad, 348, W - Pad * 2, 44);
            lblResult.Font = Theme.UISmall;
            Theme.Dress(lblResult);
            lblResult.ForeColor = Theme.Ink3;
            Controls.Add(lblResult);

            btnBack = new FlatButton();
            btnBack.Text = Lang.S("上一步");
            btnBack.SetBounds(Pad, 464, 84, Theme.H);
            btnBack.Click += delegate { if (step > 0) ShowStep(step - 1); };
            Controls.Add(btnBack);

            btnNext = new FlatButton();
            btnNext.Text = Lang.S("下一步");
            btnNext.Primary = true;
            btnNext.SetBounds(W - Pad - 112, 464, 112, Theme.H);
            btnNext.Click += delegate { Next(); };
            Controls.Add(btnNext);

            AcceptButton = btnNext;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            Load += delegate { Theme.ApplyChrome(this); };
            Shown += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };

            ShowStep(0);
        }

        /// <summary>画某一屏：标题 / 正文 / 该屏专属控件 / 进度条 / 按钮文案，一处改完。</summary>
        private void ShowStep(int i)
        {
            step = i;
            bool isOpen = i == 1, isPaste = i == 3;
            btnOpen.Visible = isOpen;
            txtKey.Visible = isPaste;
            btnTest.Visible = isPaste;
            lblResult.Visible = isPaste && lblResult.Text.Length > 0;
            btnBack.Visible = i > 0;
            btnNext.Text = Lang.S(i == Steps - 1 ? "完成" : (i == 0 ? "开始" : "下一步"));
            lblStep.Text = Lang.F("第 {0} 步 · 共 {1} 步", i + 1, Steps);
            for (int k = 0; k < Steps; k++) bar[k].BackColor = k <= i ? Theme.Accent : Theme.Line;

            switch (i)
            {
                case 0:
                    lblTitle.Text = Lang.S("为什么需要这个 key");
                    lblHint.Text =
                        Lang.S("全站搜索走的是 Steam 官方接口，它要求每个用户自己申请一个 key。") + "\r\n\r\n" +
                        Lang.S("不填也能用：按 MOD ID 搜索、粘贴合集链接导入，照样能装。") + "\r\n\r\n" +
                        Lang.S("key 只写进你这台机器的配置文件，不会上传到任何地方。");
                    break;
                case 1:
                    lblTitle.Text = Lang.S("第一步 · 打开申请页");
                    lblHint.Text =
                        Lang.S("在浏览器里打开下面这个页面，用你的 Steam 账号登录：") + "\r\n\r\n" +
                        "https://steamcommunity.com/dev/apikey\r\n\r\n" +
                        Lang.S("打不开的话：用 Steam 客户端自带的浏览器，或者手机开流量打开，再把 key 抄过来。");
                    break;
                case 2:
                    lblTitle.Text = Lang.S("第二步 · 页面上怎么填");
                    lblHint.Text =
                        Lang.S("· 域名那一栏随便写，例如 l4d2local —— 它只是个标记，不影响使用") + "\r\n\r\n" +
                        Lang.S("· 勾上同意条款，点「注册」") + "\r\n\r\n" +
                        Lang.S("· 页面会显示一串 32 位的字母加数字，这就是 key") + "\r\n\r\n" +
                        Lang.S("看到之后先点一次「复制」，下一步粘进来。");
                    break;
                case 3:
                    lblTitle.Text = Lang.S("第三步 · 粘进来");
                    lblHint.Text =
                        Lang.S("把复制到的 key 粘到下面，点「测试一下」确认能用。") + "\r\n\r\n" +
                        Lang.S("测试只查两件事：这台机器连不连得上 Steam 接口、这个 key 认不认。");
                    break;
                default:
                    lblTitle.Text = Lang.S("完成了");
                    lblHint.Text =
                        Lang.S("点「完成」，key 会填进设置窗口的输入框。") + "\r\n\r\n" +
                        Lang.S("记得再点一次「保存设置」—— 只有那一步才真正写进配置文件。");
                    break;
            }
        }

        private void Next()
        {
            if (step == 3)
            {
                if (Key.Length == 0)
                {
                    ShowResult(Lang.S("还没粘 key。拿不到也没关系：直接关掉这个窗口，以后在设置里补。"), false);
                    return;
                }
                if (Key.Length < 20)
                {
                    ShowResult(Lang.S("这串太短了，key 是 32 位 —— 多半没复制全，回去再复制一次。"), false);
                    return;
                }
            }
            if (step >= Steps - 1) { DialogResult = DialogResult.OK; Close(); return; }
            ShowStep(step + 1);
        }

        private void OpenPage()
        {
            try { Process.Start("https://steamcommunity.com/dev/apikey"); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Lang.S("打不开浏览器"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>实测放后台线程：接口慢的时候窗口不能卡成假死，按钮置灰就够说明它在跑。</summary>
        private void RunTest()
        {
            if (testing) return;
            string key = Key;
            if (key.Length < 20)
            {
                ShowResult(Lang.S("先粘上完整的 key（32 位）再测。"), false);
                return;
            }

            testing = true;
            btnTest.Enabled = false;
            ShowResult(Lang.S("正在测试…"), true);

            Thread th = new Thread(delegate()
            {
                string why = "";
                try { why = Workshop.TestApiKey(key); }
                catch (Exception ex) { why = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate()
                    {
                        testing = false;
                        btnTest.Enabled = true;
                        if (why.Length == 0) ShowResult(Lang.S("能用：这个 key 通过了 Steam 接口的验证。"), true);
                        else ShowResult(why, false);
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void ShowResult(string text, bool ok)
        {
            if (lblResult == null) return;
            lblResult.Text = text;
            lblResult.ForeColor = ok ? Theme.Ok : Theme.Warn;
            if (step == 3) lblResult.Visible = true;
        }
    }
}
