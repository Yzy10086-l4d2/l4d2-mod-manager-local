using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LocalModMgr
{
    /// <summary>
    /// 本地版主窗口：只做三件事 —— 看游戏 addons 里有什么、开关、去重。
    /// 不复制、不移动任何文件；开关写的是 left4dead2\\addonlist.txt。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private TextBox txtGame, txtSearch;
        private Button btnBrowse, btnFind, btnRefresh, btnLaunch, btnFolder, btnAllOn, btnAllOff;
        private ListView lv;
        private Label lblStatus;
        private List<ModRow> rows = new List<ModRow>();
        private bool busy;

        public MainForm()
        {
            Text = "L4D2 MOD 管理工具 · 本地版";
            ClientSize = new Size(1000, 620);
            MinimumSize = new Size(860, 480);
            Font = new Font("Microsoft YaHei UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;

            Label l1 = new Label();
            l1.Text = "游戏目录";
            l1.SetBounds(12, 16, 60, 20);
            Controls.Add(l1);

            txtGame = new TextBox();
            txtGame.SetBounds(74, 13, 620, 24);
            txtGame.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(txtGame);

            btnBrowse = MakeButton("浏览…", 706, 12, 72, 26);
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Click += delegate { Browse(); };

            btnFind = MakeButton("自动找", 786, 12, 72, 26);
            btnFind.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFind.Click += delegate
            {
                string g = Core.FindGame();
                if (g.Length == 0)
                {
                    MessageBox.Show("没找到 Left 4 Dead 2。\r\n请点「浏览…」选到 ...\\steamapps\\common\\Left 4 Dead 2（里面有 left4dead2.exe）。", "没找到");
                    return;
                }
                ApplyGame(g);
            };

            btnRefresh = MakeButton("刷新", 866, 12, 60, 26);
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRefresh.Click += delegate { Refresh(); };

            Label l2 = new Label();
            l2.Text = "搜索";
            l2.SetBounds(12, 52, 40, 20);
            Controls.Add(l2);

            txtSearch = new TextBox();
            txtSearch.SetBounds(74, 49, 300, 24);
            txtSearch.TextChanged += delegate { Fill(); };
            Controls.Add(txtSearch);

            btnAllOn = MakeButton("全部开启", 400, 48, 88, 26);
            btnAllOn.Click += delegate { SetAll(true); };

            btnAllOff = MakeButton("全部关闭", 494, 48, 88, 26);
            btnAllOff.Click += delegate { SetAll(false); };

            btnLaunch = MakeButton("启动游戏", 610, 48, 88, 26);
            btnLaunch.Click += delegate { LaunchGame(); };

            btnFolder = MakeButton("打开 addons", 704, 48, 100, 26);
            btnFolder.Click += delegate
            {
                string d = Core.AddonsDir;
                if (d.Length == 0 || !Directory.Exists(d)) { MessageBox.Show("还没找到游戏目录。", "打不开"); return; }
                Process.Start("explorer.exe", "\"" + d + "\"");
            };

            lv = new ListView();
            lv.SetBounds(12, 84, ClientSize.Width - 24, ClientSize.Height - 130);
            lv.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.MultiSelect = true;
            lv.Columns.Add("MOD 文件", 420);
            lv.Columns.Add("大小", 90, HorizontalAlignment.Right);
            lv.Columns.Add("状态", 90);
            lv.Columns.Add("来源", 120);
            lv.MouseUp += delegate(object s, MouseEventArgs e) { MaybeToggle(e); };
            lv.DoubleClick += delegate { ToggleSelected(); };
            Controls.Add(lv);

            lblStatus = new Label();
            lblStatus.SetBounds(12, ClientSize.Height - 34, ClientSize.Width - 24, 20);
            lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lblStatus.ForeColor = Color.DimGray;
            Controls.Add(lblStatus);

            txtGame.Text = Core.GameRoot;
            txtGame.TextChanged += delegate
            {
                string g = txtGame.Text.Trim();
                if (g.Length > 0 && Core.TestGame(g)) ApplyGame(g);
            };

            Load += delegate { Refresh(); };
        }

        private Button MakeButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.FlatStyle = FlatStyle.System;
            Controls.Add(b);
            return b;
        }

        private void Browse()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选到 Left 4 Dead 2 那一层（里面有 left4dead2.exe）";
                if (Core.TestGame(txtGame.Text)) d.SelectedPath = txtGame.Text;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                ApplyGame(d.SelectedPath.TrimEnd('\\'));
            }
        }

        private void ApplyGame(string root)
        {
            if (!Core.TestGame(root))
            {
                MessageBox.Show("这个目录里没有 left4dead2.exe。\r\n要选到 ...\\steamapps\\common\\Left 4 Dead 2 这一层。", "路径不对");
                return;
            }
            Core.GameRoot = root;
            Core.Save();
            if (txtGame.Text != root) txtGame.Text = root;
            Core.Log("OK", "游戏目录=" + root);
            Refresh();
        }

        private void Refresh()
        {
            if (Core.GameRoot.Length == 0) lblStatus.Text = "先指定游戏目录（点「自动找」或「浏览…」）";
            if (busy) return;
            busy = true;
            Cursor = Cursors.WaitCursor;
            try
            {
                rows = Core.Scan();
                Fill();
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "扫描失败: " + ex.Message);
                MessageBox.Show("扫描游戏目录失败：\r\n" + ex.Message, "扫描失败");
            }
            finally { busy = false; Cursor = Cursors.Default; }
        }

        private void Fill()
        {
            string kw = txtSearch.Text.Trim();
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                int shown = 0, on = 0;
                foreach (ModRow r in rows)
                {
                    if (kw.Length > 0 && r.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    ListViewItem it = new ListViewItem(r.Name);
                    it.SubItems.Add(Core.Human(r.Size));
                    it.SubItems.Add(r.On ? "开启" : "关闭");
                    it.SubItems.Add(r.Workshop ? "创意工坊" : "自己放的");
                    it.ForeColor = r.On ? Color.FromArgb(24, 140, 76) : Color.FromArgb(176, 96, 24);
                    if (shown % 2 == 1) it.BackColor = Color.FromArgb(248, 248, 248);
                    it.Tag = r;
                    lv.Items.Add(it);
                    shown++;
                    if (r.On) on++;
                }
                lblStatus.Text = "共 " + rows.Count + " 个 vpk · 开启 " + on + " · 关闭 " + (rows.Count - on)
                    + (kw.Length > 0 ? "（搜索后显示 " + shown + "）" : "")
                    + " · 开关写进 left4dead2\\addonlist.txt";
            }
            finally { lv.EndUpdate(); }
        }

        /// <summary>点「状态」列 = 翻这个 MOD 的开关；点别处只是选中。</summary>
        private void MaybeToggle(MouseEventArgs e)
        {
            ListViewHitTestInfo hit = lv.HitTest(e.Location);
            if (hit.Item == null) return;
            ModRow r = hit.Item.Tag as ModRow;
            if (r == null) return;
            if (e.Clicks < 2 && hit.SubItem != hit.Item.SubItems[2]) return;
            ApplySwitch(new List<ModRow> { r }, !r.On);
        }

        private void ToggleSelected()
        {
            if (lv.SelectedItems.Count == 0) return;
            List<ModRow> sel = new List<ModRow>();
            bool anyOff = false;
            foreach (ListViewItem it in lv.SelectedItems)
            {
                ModRow r = it.Tag as ModRow;
                if (r == null) continue;
                sel.Add(r);
                if (!r.On) anyOff = true;
            }
            ApplySwitch(sel, anyOff);
        }

        private void SetAll(bool on)
        {
            if (rows.Count == 0) return;
            string q = on ? "把列出来的 " + rows.Count + " 个 MOD 全部开启？" : "把列出来的 " + rows.Count + " 个 MOD 全部关闭？";
            if (MessageBox.Show(q + "\r\n\r\n只改开关，不复制、不移动、不删除任何文件。", "确认",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            ApplySwitch(rows, on);
        }

        private void ApplySwitch(List<ModRow> list, bool on)
        {
            if (Core.GameRoot.Length == 0) return;
            try
            {
                Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (ModRow r in list) st[r.Name] = on;
                Core.WriteAddonList(st);
                foreach (ModRow r in list) r.On = on;
                Core.Log("OK", (on ? "开启 " : "关闭 ") + list.Count + " 个 MOD（只写 addonlist）");
                Fill();
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "写 addonlist 失败: " + ex.Message);
                MessageBox.Show("写开关失败（游戏开着一般也能写，文件被占用时不行）：\r\n" + ex.Message
                    + "\r\n\r\n如果一直失败，试试以管理员身份运行。", "写开关失败");
            }
        }

        private void LaunchGame()
        {
            if (Core.GameRoot.Length == 0) { MessageBox.Show("先指定游戏目录。", "还没设置"); return; }
            try
            {
                Process.Start("steam://rungameid/550");
                Core.Log("OK", "启动游戏（steam://rungameid/550）");
            }
            catch
            {
                try
                {
                    Process.Start(Path.Combine(Core.GameRoot, "left4dead2.exe"));
                    Core.Log("OK", "启动游戏（直接跑 left4dead2.exe）");
                }
                catch (Exception ex2)
                {
                    MessageBox.Show("启动失败：\r\n" + ex2.Message, "启动失败");
                }
            }
        }
    }
}
