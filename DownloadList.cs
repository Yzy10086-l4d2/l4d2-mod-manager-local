using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 工坊的「下载管理」：在这次下过什么、下到哪、成了还是失败，都看得见；
    /// 保存位置也在这里改 —— 下载这件事的两个疑问（"东西在哪"和"以后存哪"）放在同一个窗口里。
    ///
    /// 非模态：下载还在跑也能打开看进度，关掉不影响下载。
    /// </summary>
    internal sealed class DownloadListForm : Form
    {
        private readonly ListView lv = new ListView();
        private readonly ListView tree = new ListView();       // 左侧分类（大类 / 小分类，带数量）
        private string filterCat = "";
        private string filterSub = "";
        private readonly Dictionary<string, string[]> catOf = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly Label lblEmpty = new Label();
        private readonly Label lblSaveTo = new Label();
        private readonly Label lblDir = new Label();
        private readonly Label lblStatus = new Label();
        private readonly FlatButton btnDir = new FlatButton();
        private readonly FlatButton btnDefault = new FlatButton();
        private readonly FlatButton btnOpenDir = new FlatButton();
        private readonly FlatButton btnAgain = new FlatButton();
        private readonly FlatButton btnShop = new FlatButton();   // 回到我们自己的工坊页看这个 MOD
        private readonly MainForm owner;
        private readonly FlatButton btnLocate = new FlatButton();
        private readonly FlatButton btnDelete = new FlatButton();
        private readonly FlatButton btnClose = new FlatButton();

        private readonly System.Windows.Forms.Timer msgTimer = new System.Windows.Forms.Timer();   // 与 System.Threading.Timer 区分
        private string summary = "";      // 底部常驻统计；临时消息 3 秒后回落到它

        private const int M = 16;
        private static int S(int v) { return Dpi.S(v); }

        public DownloadListForm(MainForm mf = null)
        {
            owner = mf;
            Text = Lang.S("工坊 · 下载管理");
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(780, 560);
            MinimumSize = Dpi.Sz(640, 420);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = true;

            // ---- 顶部：保存位置 ----
            lblSaveTo.Text = Lang.S("保存到");
            lblSaveTo.Font = Theme.UISmall;
            lblSaveTo.ForeColor = Theme.Ink2;
            lblSaveTo.AutoSize = false;
            lblSaveTo.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lblSaveTo);

            lblDir.Font = Theme.UISmall;
            lblDir.ForeColor = Theme.Ink;
            lblDir.AutoSize = false;
            lblDir.AutoEllipsis = true;
            lblDir.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lblDir);

            btnDir.Text = Lang.S("改位置");
            btnDir.Click += delegate { PickDir(); };
            Controls.Add(btnDir);

            btnDefault.Text = Lang.S("用默认");
            btnDefault.Click += delegate { UseDefaultDir(); };
            Controls.Add(btnDefault);

            btnOpenDir.Text = Lang.S("打开文件夹");
            btnOpenDir.Click += delegate { OpenFolder(SafeDir()); };
            Controls.Add(btnOpenDir);

            // ---- 中间：记录 ----
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.HideSelection = false;
            lv.Columns.Add("MOD", S(300));
            lv.Columns.Add(Lang.S("大小"), S(130));
            lv.Columns.Add(Lang.S("状态"), S(190));
            lv.Columns.Add(Lang.S("时间"), S(120));
            lv.DoubleClick += delegate { Locate(); };
            lv.SelectedIndexChanged += delegate { UpdateButtons(); };
            // 滚轮发给有焦点的控件：鼠标进哪个列表就把焦点给它，免得"滚轮怎么滚都不动"
            lv.MouseEnter += delegate { if (!lv.Focused) lv.Focus(); };
            tree.MouseEnter += delegate { if (!tree.Focused) tree.Focus(); };
            Controls.Add(lv);

            // 左侧分类：按"这个 MOD 在方案树里的位置"归档，和工坊首页的分类思路一致
            tree.View = View.Details;
            tree.HeaderStyle = ColumnHeaderStyle.None;
            tree.FullRowSelect = true;
            tree.MultiSelect = false;
            tree.HideSelection = false;
            tree.BorderStyle = BorderStyle.FixedSingle;
            tree.BackColor = Theme.Surface;
            tree.ForeColor = Theme.Ink;
            tree.Font = Theme.UISmall;
            tree.Columns.Add(Lang.S("分类"), S(160));
            tree.SelectedIndexChanged += delegate { PickCatFilter(); };
            Controls.Add(tree);

            lblEmpty.Font = Theme.UISmall;
            lblEmpty.ForeColor = Theme.Ink3;
            lblEmpty.AutoSize = false;
            lblEmpty.BackColor = Theme.Bg;          // 实心遮罩：不然 ListView 的列头会从背后透出来
            lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblEmpty.Cursor = Cursors.Default;
            lblEmpty.Text = Lang.S(Lang.S("还没从工坊下过东西。\r\n在左边卡片上点「下载这个 MOD」，下完就会出现在这里。"));
            Controls.Add(lblEmpty);

            // ---- 底部 ----
            btnAgain.Text = Lang.S("重新下载");
            btnAgain.Click += delegate { Redownload(); };
            Controls.Add(btnAgain);

            btnShop.Text = Lang.S("在工坊查看");
            btnShop.Click += delegate { OpenInWorkshop(); };
            Controls.Add(btnShop);

            btnLocate.Text = Lang.S("打开所在位置");
            btnLocate.Click += delegate { Locate(); };
            Controls.Add(btnLocate);

            btnDelete.Text = Lang.S("从磁盘删除");
            btnDelete.Danger = true;
            btnDelete.Click += delegate { DeleteFile(); };
            Controls.Add(btnDelete);

            btnClose.Text = Lang.S("关闭");
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);
            CancelButton = btnClose;

            lblStatus.Font = Theme.UISmall;
            lblStatus.ForeColor = Theme.Ink3;
            lblStatus.AutoEllipsis = true;
            Controls.Add(lblStatus);

            msgTimer.Interval = 3000;
            msgTimer.Tick += delegate { msgTimer.Stop(); lblStatus.Text = summary; };

            WorkshopDownloads.Changed += OnRecordsChanged;
            FormClosed += delegate { WorkshopDownloads.Changed -= OnRecordsChanged; };
            Shown += delegate
            {
                Theme.ApplyChrome(this);
                Theme.DeepNative(this);
                RefreshDirLabel();
                RefreshList();          // 打开先把已有记录读进来，不然要等下次下载才会显示
            };
            ApplyLayout();
        }

        // ---------------------------------------------------------- 布局

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            int m = S(M);
            int y = S(14);

            // 顶部一行：左标签，右侧三颗按钮（从右往左排，窗口窄了先挤路径）
            int bx = W - m;
            btnOpenDir.SetBounds(bx - S(96), y, S(96), S(30)); bx -= S(96) + S(8);
            btnDefault.SetBounds(bx - S(76), y, S(76), S(30)); bx -= S(76) + S(8);
            btnDir.SetBounds(bx - S(76), y, S(76), S(30)); bx -= S(76) + S(10);

            lblSaveTo.SetBounds(m, y + S(6), S(48), S(18));
            lblDir.SetBounds(m + S(50), y + S(6), Math.Max(S(80), bx - (m + S(50))), S(18));

            // 中间：左分类树 + 右记录（分类和工坊首页同一套：大类 → 小分类）
            int top = S(54);
            int bottom = S(100);
            int listH = Math.Max(S(120), H - top - bottom);
            int treeW = Math.Max(S(150), Math.Min(S(210), (W - 2 * m) / 4));
            tree.SetBounds(m, top, treeW, listH);
            int lx = m + treeW + S(8);
            int listW = Math.Max(S(240), W - m - lx);
            lv.SetBounds(lx, top, listW, listH);
            lblEmpty.SetBounds(lx, top, listW, listH);

            // 名称列吃掉剩余宽度，其余三列定宽 —— 窗口拉宽时不该只在右边留一条空白
            // 注意：构造里设置 ClientSize 就会触发 OnResize，那时列还没加，得先判个数
            if (lv.Columns.Count >= 4)
            {
                int wSize = S(130), wState = S(190), wTime = S(120);
                lv.Columns[0].Width = Math.Max(S(180), listW - wSize - wState - wTime - S(6));
                lv.Columns[1].Width = wSize;
                lv.Columns[2].Width = wState;
                lv.Columns[3].Width = wTime;
            }

            // 底部：左边三颗操作，右边清空 + 关闭，中间留给状态文字
            int by = H - S(66);
            btnAgain.SetBounds(m, by, S(100), S(32));
            btnShop.SetBounds(m + S(108), by, S(112), S(32));
            btnLocate.SetBounds(m + S(228), by, S(120), S(32));
            btnDelete.SetBounds(m + S(356), by, S(116), S(32));

            btnClose.SetBounds(W - m - S(76), by, S(76), S(32));

            lblStatus.SetBounds(m, H - S(26), W - 2 * m, S(18));
        }

        // ---------------------------------------------------------- 数据

        /// <summary>记录变化可能来自下载线程：一律转回 UI 线程再刷，避免跨线程操作控件。</summary>
        private bool refreshPending;

        private void OnRecordsChanged()
        {
            if (IsDisposed || refreshPending) return;
            refreshPending = true;
            try
            {
                if (!IsHandleCreated) { refreshPending = false; return; }
                BeginInvoke(new MethodInvoker(delegate { refreshPending = false; RefreshList(); }));
            }
            catch { refreshPending = false; }
        }

        private void RefreshList()
        {
            if (IsDisposed) return;
            try { RefreshListCore(true); }
            catch (Exception ex) { Core.Log("FAIL", "下载管理刷新失败: " + ex); SetMsg("刷新失败：" + ex.Message); }
        }

        private void RefreshListCore(bool rebuildTree)
        {
            string keep = Selected() != null ? Selected().Id : "";

            List<WsDownload> all = WorkshopDownloads.All();
            catOf.Clear();
            foreach (WsDownload d0 in all) catOf[d0.Id] = CatOf(d0);

            if (rebuildTree) FillTree(all);      // 只在数据刷新时重建树；点分类时绝不能重建（见 PickCatFilter）

            List<WsDownload> shownList = new List<WsDownload>();
            foreach (WsDownload d0 in all)
            {
                string[] cs = catOf[d0.Id];
                if (filterCat.Length > 0 && cs[0] != filterCat) continue;
                if (filterSub.Length > 0 && cs[1] != filterSub) continue;
                shownList.Add(d0);
            }

            lv.BeginUpdate();
            lv.Items.Clear();
            int keepIdx = -1;
            for (int i = 0; i < shownList.Count; i++)
            {
                WsDownload d = shownList[i];
                ListViewItem li = new ListViewItem(d.Title.Length > 0 ? d.Title : ("workshop_" + d.Id));
                li.SubItems.Add(d.SizeText);
                li.SubItems.Add(d.StateText);
                li.SubItems.Add(d.Time.ToString("MM-dd HH:mm"));
                li.Tag = d;
                if (d.Running) li.ForeColor = Theme.Info;
                else if (d.Status == "fail") li.ForeColor = Theme.Accent;
                else if (!d.ExistsOnDisk && d.Status == "ok") li.ForeColor = Theme.Ink3;
                lv.Items.Add(li);
                if (keep.Length > 0 && d.Id == keep && keepIdx < 0) keepIdx = i;
            }
            lv.EndUpdate();
            if (keepIdx >= 0)
            {
                lv.Items[keepIdx].Selected = true;
                lv.Items[keepIdx].Focused = true;
            }

            bool empty = shownList.Count == 0;
            lv.Visible = true;
            lblEmpty.Visible = empty;
            if (empty) lblEmpty.BringToFront();

            UpdateButtons();
            UpdateFooterCount(all);
        }

        /// <summary>记录属于方案树的哪个位置：按落盘文件名和当前库对上；对不上算「未归类」。</summary>
        private string[] CatOf(WsDownload d)
        {
            try
            {
                string fn = d.Path.Length > 0 ? Path.GetFileName(d.Path) : "";
                if (fn.Length > 0)
                {
                    foreach (ModGroup g in Catalog.GroupByKey.Values)
                    {
                        foreach (ModItem p in g.Parts)
                        {
                            if (p == null || string.IsNullOrEmpty(p.FullPath)) continue;
                            if (!string.Equals(Path.GetFileName(p.FullPath), fn, StringComparison.OrdinalIgnoreCase)) continue;
                            return new string[] { g.Cat ?? "", g.Sub ?? "" };
                        }
                    }
                }
            }
            catch { }
            return new string[] { Lang.S("未归类"), "" };
        }

        /// <summary>
        /// 左树：全部 → 各「大类」（加粗） → 各「小分类」（缩进）。
        /// 数量是"这个分类下有多少条下载记录"，空分类不列，免得一屏全是 0。
        /// </summary>
        private void FillTree(List<WsDownload> all)
        {
            string keepCat = filterCat, keepSub = filterSub;
            buildingTree = true;
            tree.BeginUpdate();
            tree.Items.Clear();

            ListViewItem root = new ListViewItem(Lang.S("全部下载") + "  (" + all.Count + ")");
            root.Tag = new string[] { "", "" };
            root.Font = Theme.UIBold;
            tree.Items.Add(root);

            Dictionary<string, int> catCount = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, int>> subCount = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (WsDownload d in all)
            {
                string[] cs = catOf.ContainsKey(d.Id) ? catOf[d.Id] : new string[] { Lang.S("未归类"), "" };
                int n;
                catCount.TryGetValue(cs[0], out n);
                catCount[cs[0]] = n + 1;
                if (cs[1].Length > 0)
                {
                    Dictionary<string, int> m2;
                    if (!subCount.TryGetValue(cs[0], out m2)) { m2 = new Dictionary<string, int>(StringComparer.Ordinal); subCount[cs[0]] = m2; }
                    m2.TryGetValue(cs[1], out n);
                    m2[cs[1]] = n + 1;
                }
            }

            foreach (KeyValuePair<string, int> kv in catCount)
            {
                ListViewItem ci = new ListViewItem(kv.Key + "  (" + kv.Value + ")");
                ci.Tag = new string[] { kv.Key, "" };
                ci.Font = Theme.UIBold;
                tree.Items.Add(ci);
                Dictionary<string, int> m2;
                if (!subCount.TryGetValue(kv.Key, out m2)) continue;
                foreach (KeyValuePair<string, int> sv in m2)
                {
                    ListViewItem si = new ListViewItem("    " + sv.Key + "  (" + sv.Value + ")");
                    si.Tag = new string[] { kv.Key, sv.Key };
                    si.ForeColor = Theme.Ink2;
                    tree.Items.Add(si);
                }
            }

            // 恢复之前的选中项（刷新不该把筛选弹回全部）
            tree.Items[0].Selected = true;
            foreach (ListViewItem it in tree.Items)
            {
                string[] cs = it.Tag as string[];
                if (cs == null) continue;
                if (cs[0] == keepCat && cs[1] == keepSub) { it.Selected = true; it.Focused = true; break; }
            }
            tree.EndUpdate();
            buildingTree = false;
        }

        private bool buildingTree;

        /// <summary>自检探针：左树/右表/底部按钮有没有互相压。</summary>
        internal string LayoutProbe()
        {
            try
            {
                return "tree=" + tree.Bounds + " lv=" + lv.Bounds
                    + " overlapTreeLv=" + (tree.Right > lv.Left)
                    + " btnDeleteRight=" + btnDelete.Right + " btnCloseLeft=" + btnClose.Left
                    + " btnOverlap=" + (btnDelete.Right > btnClose.Left)
                    + " treeItems=" + tree.Items.Count;
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        /// <summary>自检用：分类树行数。</summary>
        internal int TreeCountForTest() { return tree.Items.Count; }

        /// <summary>自检用：选中第 i 行分类（等价于点一下）。</summary>
        internal void ClickTreeForTest(int i)
        {
            int cnt = tree.Items.Count;
            if (i < 0 || i >= cnt) { Core.Log("INFO", "DOWNSHOT 跳过 i=" + i + " 行数=" + cnt); return; }
            ListViewItem it = tree.Items[i];
            if (it == null) { Core.Log("FAIL", "DOWNSHOT tree.Items[" + i + "] 是 null（行数=" + cnt + "）"); return; }
            try { it.Selected = true; }
            catch (Exception ex) { Core.Log("FAIL", "DOWNSHOT 选 i=" + i + " 崩: " + ex.Message); return; }
            try { if (it.ListView != null) it.Focused = true; } catch { }
            Core.Log("INFO", "DOWNSHOT 点 i=" + i + " 后行数=" + tree.Items.Count);
        }

        /// <summary>自检用：选中第 i 行记录。</summary>
        internal void ClickRowForTest(int i)
        {
            if (i < 0 || i >= lv.Items.Count) return;
            lv.Items[i].Selected = true;
            lv.Items[i].Focused = true;
        }

        private void PickCatFilter()
        {
            if (buildingTree) return;
            string[] cs = tree.SelectedItems.Count == 0 ? null : tree.SelectedItems[0].Tag as string[];
            filterCat = cs == null ? "" : (cs[0] ?? "");
            filterSub = cs == null ? "" : (cs[1] ?? "");
            // 只换筛选项，不重建左树：在 ListView 的选中事件里清空它自己会崩（原生访问越界，用户 2026-11-21 真踩到）
            try { RefreshListCore(false); }
            catch (Exception ex) { Core.Log("FAIL", "下载管理筛选失败: " + ex); SetMsg("筛选失败：" + ex.Message); }
        }

        private void UpdateFooterCount(List<WsDownload> all)
        {
            int ok = 0, fail = 0, run = 0;
            foreach (WsDownload d in all)
            {
                if (d.Running) run++;
                else if (d.Status == "ok") ok++;
                else fail++;
            }
            summary = all.Count == 0 ? "" : "共 " + all.Count + " 条 · 成功 " + ok
                + (fail > 0 ? " · 失败/取消 " + fail : "")
                + (run > 0 ? " · 下载中 " + run : "");
            if (!msgTimer.Enabled) lblStatus.Text = summary;
        }

        private void SetMsg(string text)
        {
            lblStatus.Text = text;
            msgTimer.Stop();
            msgTimer.Start();
        }

        private WsDownload Selected()
        {
            if (lv.SelectedItems.Count == 0) return null;
            return lv.SelectedItems[0].Tag as WsDownload;
        }

        private void UpdateButtons()
        {
            WsDownload d = Selected();
            bool has = d != null && !d.Running;
            btnAgain.Enabled = has && d.Url.Length > 0;
            btnShop.Enabled = d != null && d.Id.Length > 0 && owner != null;
            btnLocate.Enabled = d != null && (d.ExistsOnDisk || d.Dir.Length > 0);
            btnDelete.Enabled = d != null && d.ExistsOnDisk;
        }

        // ---------------------------------------------------------- 保存位置

        private string SafeDir()
        {
            try { return Workshop.DestDir(); }
            catch (Exception ex) { return ex.Message; }
        }

        private void RefreshDirLabel()
        {
            bool custom = (Core.Cfg.WorkshopDir ?? "").Trim().Length > 0;
            string d;
            try { d = Workshop.DestDir(); }
            catch (Exception ex) { d = ex.Message; }
            lblDir.Text = custom ? d : d + Lang.S("    默认位置（跟随主库）");
            btnDefault.Enabled = custom;
        }

        /// <summary>
        /// 已下载的 MOD 能回到我们的工坊页：按记录里的工坊 ID 查一次，直接打开它的详情页
        /// （不跳 Steam 浏览器）。
        /// </summary>
        private void OpenInWorkshop()
        {
            WsDownload d = Selected();
            if (d == null || d.Id.Length == 0 || owner == null) return;
            string id = d.Id;
            SetMsg(Lang.S("正在打开工坊详情…"));
            btnShop.Enabled = false;
            Thread t = new Thread(delegate()
            {
                string err = null;
                WsItem it = null;
                try
                {
                    List<WsItem> one = Workshop.Fetch(new List<string>(new string[] { id }));
                    if (one != null && one.Count > 0) it = one[0];
                    else err = Lang.S("工坊里查不到这个物品（可能已下架）");
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        btnShop.Enabled = true;
                        if (it != null) { try { owner.OpenWorkshopFor("", it); } catch { } SetMsg(""); }
                        else SetMsg(err ?? "");
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        private void PickDir()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选工坊下载保存到哪个文件夹（U 盘上的目录也行）";
                string cur = SafeDir();
                if (Directory.Exists(cur)) dlg.SelectedPath = cur;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (Core.SetWorkshopDir(dlg.SelectedPath, true))
                {
                    RefreshDirLabel();
                    SetMsg("下载位置已改：之后的下载都放这里");
                    Core.Log("OK", "工坊下载位置 → " + Core.Cfg.WorkshopDir);
                }
                else
                {
                    SetMsg("这个目录建不了（可能没权限或盘没插），换一个试试");
                }
            }
        }

        private void UseDefaultDir()
        {
            Core.SetWorkshopDir("", true);
            RefreshDirLabel();
            SetMsg("已恢复默认：跟随主库");
            Core.Log("OK", "工坊下载位置 → 默认（跟随主库）");
        }

        // ---------------------------------------------------------- 行操作

        private void Locate()
        {
            WsDownload d = Selected();
            if (d == null) return;
            if (d.ExistsOnDisk) RevealInExplorer(d.Path);
            else if (d.Dir.Length > 0) OpenFolder(d.Dir);
            else SetMsg("这条记录没留下位置信息");
        }

        private static void RevealInExplorer(string file)
        {
            try { Process.Start("explorer.exe", "/select,\"" + file + "\""); }
            catch (Exception ex) { Core.Log("WARN", "打开位置失败: " + ex.Message); }
        }

        private void OpenFolder(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) { SetMsg("目录不在了：" + dir); return; }
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { SetMsg("打不开文件夹：" + ex.Message); }
        }

        private void DeleteFile()
        {
            WsDownload d = Selected();
            if (d == null || !d.ExistsOnDisk) return;
            if (MessageBox.Show(this,
                "把这个文件删掉？\r\n\r\n" + d.Path + "\r\n\r\n"
                + "只删游戏库里的这一份下载，不影响工坊上的物品。删了要再下就点「重新下载」。\r\n"
                + "（删掉没法恢复，会进回收站的是普通文件，U 盘里的不进回收站。）",
                "从磁盘删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            try
            {
                File.Delete(d.Path);
                Core.Log("OK", "工坊：已删除下载文件 " + d.Path);
                WorkshopDownloads.Remove(d);      // 文件都没了，这条记录留着只会误导
                SetMsg("已删除：" + Path.GetFileName(d.Path));
            }
            catch (Exception ex)
            {
                SetMsg("删不掉：" + ex.Message + "（文件可能正被游戏或杀软占用）");
                Core.Log("FAIL", "工坊：删除下载文件失败 " + d.Path + "：" + ex.Message);
            }
        }

        private void Redownload()
        {
            WsDownload d = Selected();
            if (d == null) return;
            if (d.Url.Length == 0)
            {
                SetMsg("这条记录没有直链（早期记录没存），去工坊重新搜一次这个 MOD 即可");
                return;
            }
            string dir;
            try { dir = Workshop.DestDir(); }
            catch (Exception ex) { SetMsg(ex.Message); return; }

            string name = Workshop.SafeFileName(d.Title.Length > 0 ? d.Title : ("workshop_" + d.Id), d.Id);
            string dest = Path.Combine(dir, name);
            if (File.Exists(dest) && MessageBox.Show(this,
                "同名文件已经在了：\r\n" + dest + "\r\n\r\n覆盖它？",
                "重新下载", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            WorkshopDownloads.Begin(d.Id, d.Title, d.Url, dir);
            SetMsg("重新下载：" + name);
            btnAgain.Enabled = false;

            WsItem it = new WsItem();
            it.Id = d.Id;
            it.Title = d.Title;
            it.Url = d.Url;

            Thread t = new Thread(delegate()
            {
                try
                {
                    Workshop.Download(it, dest, delegate(long got, long total)
                    {
                        WorkshopDownloads.Progress(d.Id, got, total);
                    });
                    if (Workshop.LastSavedPath.Length > 0) dest = Workshop.LastSavedPath;
                    if (Workshop.LastDownloadNote.Length > 0) SetMsg(Workshop.LastDownloadNote);
                    string rn = Workshop.RenameToAddonTitle(dest);
                    if (rn != null) dest = Path.Combine(dir, rn);
                    Workshop.SavePreview(it, dest);
                    long savedSz2 = 0;
                    try { savedSz2 = new FileInfo(dest).Length; } catch { }
                    Workshop.RememberDownload(d.Id, Path.GetFileName(dest), it.Updated, savedSz2);
                    long size = 0;
                    try { size = new FileInfo(dest).Length; } catch { }
                    WorkshopDownloads.Finish(d.Id, true, dest, size, "");
                    string gotName = Path.GetFileName(dest);
                    try
                    {
                        if (owner != null)
                            BeginInvoke(new MethodInvoker(delegate
                            {
                                try { owner.RefreshAfterImport(gotName); } catch { }
                            }));
                    }
                    catch { }
                    Core.Log("OK", "工坊：重新下载完成 " + d.Id + " → " + dest);
                }
                catch (Exception ex)
                {
                    WorkshopDownloads.Finish(d.Id, false, dest, 0, ex.Message);
                    Core.Log("FAIL", "工坊：重新下载失败 " + d.Id + "：" + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        // ---------------------------------------------------------- 截图自检用

        /// <summary>不联网的样例：验证列表、空态、按钮使能在真实 DPI 下排得开。</summary>
        internal void FillSample()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            string[,] rows = new string[,] {
                { "【绝对精品】AWP 皮肤替换", "18.4 MB", "下载中 62%", "11-12 07:30" },
                { "Zoey 更清晰的贴图 · 重新打包", "42.1 MB", "已下载", "11-12 07:22" },
                { "Tank 摇滚音乐包", "6.8 MB", "失败：无法连接到远程服务器", "11-12 07:10" },
                { "旧版武器音效", "3.2 MB", "文件已删除", "11-11 21:04" },
            };
            for (int i = 0; i < rows.GetLength(0); i++)
            {
                ListViewItem li = new ListViewItem(rows[i, 0]);
                li.SubItems.Add(rows[i, 1]);
                li.SubItems.Add(rows[i, 2]);
                li.SubItems.Add(rows[i, 3]);
                li.Tag = new WsDownload { Id = "sample" + i, Title = rows[i, 0], Status = i == 0 ? "downloading" : (i == 2 ? "fail" : "ok") };
                if (i == 0) li.ForeColor = Theme.Info;
                else if (i == 2) li.ForeColor = Theme.Accent;
                lv.Items.Add(li);
            }
            lv.EndUpdate();
            lblEmpty.Visible = false;
            lv.Items[1].Selected = true;
            lblStatus.Text = Lang.S("共 4 条 · 成功 2 · 失败/取消 1 · 下载中 1");
            UpdateButtons();
        }

        internal void ShowEmptyForTest()
        {
            lv.Items.Clear();
            lblEmpty.Visible = true;
            lblEmpty.BringToFront();
            lblStatus.Text = "";
            UpdateButtons();
        }
    }
}
