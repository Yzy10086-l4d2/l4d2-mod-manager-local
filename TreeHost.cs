using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    public partial class MainForm
    {
        private OverviewCanvas map;
        private bool mapHooked;

        /// <summary>套装总览：整窗口一块看板，盖在左栏和方案树上面（顶栏和底栏留着）。</summary>
        internal void ShowOverview()
        {
            EnsureTree();
            DropLoading();
            treeView.Rebuild();

            if (map == null)
            {
                map = new OverviewCanvas(this);
                Controls.Add(map);
            }
            if (!mapHooked)
            {
                mapHooked = true;
                Resize += delegate { if (OverviewOpen) FitMap(); };
            }

            FitMap();
            map.Rebuild();
            map.Visible = true;
            map.BringToFront();
            if (btnView != null) btnView.Text = Lang.S("方案树");
            map.Focus();
            UpdateModCaption();
        }

        private void FitMap()
        {
            if (map == null) return;
            const int top = 56;                    // 顶栏下面
            // 以前这里还减掉 104 的底栏，可底栏早撤了（BottomBarH=0），
            // 结果画布下面空出 104px，方案树的残影从缝里透出来（2026-11-24 截图实锤）。
            map.SetBounds(0, top, ClientSize.Width,
                Math.Max(140, ClientSize.Height - top));
        }

        internal void HideOverview()
        {
            if (map != null) map.Visible = false;
            if (overviewWindow != null && !overviewWindow.IsDisposed) overviewWindow.Close();
            if (btnView != null) btnView.Text = Lang.S("方案管理");
            ShowTree();
        }

        internal bool OverviewOpen
        {
            get { return (overviewWindow != null && !overviewWindow.IsDisposed && overviewWindow.Visible)
                || (map != null && map.Visible); }
        }

        /// <summary>截图自检：把总览画布存成 PNG（只抓画布，不抓整窗 —— 屏幕上可能盖着别的窗口）。</summary>
        internal void OverviewShot()
        {
            EnsureTree();
            if (overviewWindow != null && !overviewWindow.IsDisposed) overviewWindow.Close();
            overviewWindow = new OverviewForm(this);
            overviewWindow.FormClosed += delegate { overviewWindow = null; };
            overviewWindow.Show(this);
            Pump(700);
            Shot(overviewWindow, "overview-window");
            overviewWindow.ClientSize = Dpi.Sz(880, 560);
            Pump(300);
            Shot(overviewWindow, "overview-window-min");
            overviewWindow.TestEscape();
            Pump(120);
        }

        private static void Pump(int ms)
        {
            DateTime until = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(15); }
        }

        private void ShotCanvas(string tag)
        {
            if (map == null) return;
            using (Bitmap b = new Bitmap(Math.Max(2, map.Width), Math.Max(2, map.Height)))
            {
                map.DrawToBitmap(b, new Rectangle(0, 0, map.Width, map.Height));
                b.Save(System.IO.Path.Combine(Core.LogDir, tag + ".png"),
                       System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        /// <summary>总览自检：数字对得上、每个大类都排得下、点一行跳得到对应位置。返回失败条数。</summary>
        internal int OverviewSelfTest()
        {
            int bad = 0;

            // OVSIZE=1180x780：按指定客户区尺寸跑一遍。
            // 排版问题是「某个尺寸下才出现」的，本机窗口大小和 CI 常常不一样，
            // 没有这个入口就只能等 CI 报错才知道（2026-11-12 人物/幸存者被切掉那次就是）。
            string ovSize = Environment.GetEnvironmentVariable("OVSIZE");
            if (!string.IsNullOrEmpty(ovSize))
            {
                string[] wh = ovSize.Split('x', 'X');
                int ow, oh;
                if (wh.Length == 2 && int.TryParse(wh[0], out ow) && int.TryParse(wh[1], out oh) && ow > 400 && oh > 300)
                {
                    ClientSize = new Size(ow, oh);
                    Pump(120);
                    Core.Log("INFO", "OVSELF 指定尺寸: " + ow + "x" + oh);
                }
            }

            EnsureTree();
            treeView.Rebuild();
            ShowOverview();
            Pump(300);

            if (map == null) { Core.Log("FAIL", "ovself 画布没建起来"); return 1; }

            Core.Log("INFO", "OVSELF 尺寸: 画布 " + map.Width + "x" + map.Height
                            + "，窗体客户区 " + ClientSize.Width + "x" + ClientSize.Height
                            + "，可见 " + map.Visible);

            // 数字必须和方案树同一份：位置总数、已装位置数
            Core.Log("INFO", "OVSELF 大类 " + map.CatCount + " / 位置 " + map.SlotCount
                            + "（方案树 " + treeView.SlotCount + "），已装 " + map.SlotsOn
                            + "（方案树 " + treeView.SlotCheckedCount + "）");
            if (map.SlotCount != treeView.SlotCount)
            {
                Core.Log("FAIL", "OVSELF 位置数对不上：" + map.SlotCount + " vs " + treeView.SlotCount);
                bad++;
            }
            if (map.SlotsOn != treeView.SlotCheckedCount)
            {
                Core.Log("FAIL", "OVSELF 已装位置数对不上：" + map.SlotsOn + " vs " + treeView.SlotCheckedCount);
                bad++;
            }

            // 每个大类都排一遍：行高、边界、放得下就别滚
            string[] cats = map.CatTitles;
            int fit = 0;
            foreach (string cat in cats)
            {
                string why = map.LayoutProblem(cat);
                if (why != null) { Core.Log("FAIL", "OVSELF 排版 " + why); bad++; }
                else fit++;
            }
            Core.Log("INFO", "OVSELF 排版检查：" + fit + "/" + cats.Length + " 个大类通过");

            // 点一行：命中到的位置要和画面上那一行是同一个
            map.SelectCatForShot(cats.Length > 0 ? cats[0] : "");
            RectangleF r = map.RowRect(0);
            Slot want = map.RowSlot(0);
            Slot got = r.IsEmpty ? null : map.SlotAtPoint(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);
            Core.Log("INFO", "OVSELF 点第一行：" + (want == null ? "无" : want.Name)
                            + " → 命中 " + (got == null ? "无" : got.Name));
            if (want != null && want != got) { Core.Log("FAIL", "OVSELF 点一行命中的不是那个位置"); bad++; }

            // 键盘：→ 进明细、↓ 走一行、← 退回大类
            map.SelectCatForShot(cats.Length > 0 ? cats[0] : "");
            map.TestKey(Keys.Right);
            int f1 = map.RowFocus;
            map.TestKey(Keys.Down);
            int f2 = map.RowFocus;
            map.TestKey(Keys.Left);
            int f3 = map.RowFocus;
            Core.Log("INFO", "OVSELF 键盘：→ 后 " + f1 + "，↓ 后 " + f2 + "，← 后 " + f3);
            if (f1 != 0) { Core.Log("FAIL", "OVSELF 按 → 没进明细第一行"); bad++; }
            if (f2 != 1) { Core.Log("FAIL", "OVSELF 按 ↓ 没往下走一行"); bad++; }
            if (f3 != -1) { Core.Log("FAIL", "OVSELF 按 ← 没退回大类"); bad++; }

            // 开关来回：关掉再打开，勾选数要回到原样（2026-11-09 用户报「关了打不开」）
            map.SelectCatForShot(cats.Length > 0 ? cats[0] : "");
            string rt = map.TestToggleRoundTrip();
            Core.Log("INFO", "OVSELF 开关来回：" + (rt == null ? "没有可测的位置" : rt));
            if (rt != null && rt != "关掉后 0，再打开后 1")
            {
                Core.Log("FAIL", "OVSELF 关掉再打开没回到原样：" + rt);
                bad++;
            }

            // 点一行真的会跳回方案树，并且选中那个位置（看板不收起来的话，树会被盖在下面）
            if (want != null)
            {
                ShowSlotInTree(want);
                Pump(120);
                bool back = !OverviewOpen;
                bool sel = treeView != null && treeView.SelectedKey == want.Key;
                Core.Log("INFO", "OVSELF 点行跳转：" + want.Name + " → 看板已收=" + back + "，选中对=" + sel);
                if (!back) { Core.Log("FAIL", "OVSELF 点了行看板没关，方案树被盖在下面"); bad++; }
                if (!sel) { Core.Log("FAIL", "OVSELF 跳转后方案树选中的不是那个位置"); bad++; }
                ShowOverview();                       // 后面还要写文本存档
                Pump(80);
            }

            // 摊成文本存档，数字对不上时好核对
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(Core.LogDir, "总览.txt"),
                    map.Dump(), new System.Text.UTF8Encoding(false));
            }
            catch { }

            HideOverview();

            overviewWindow = new OverviewForm(this);
            overviewWindow.FormClosed += delegate { overviewWindow = null; };
            overviewWindow.Show(this);
            Pump(300);
            Shot(overviewWindow, "overview-window");
            if (!overviewWindow.Visible) { Core.Log("FAIL", "OVSELF 独立总览窗没有显示"); bad++; }
            if (want != null)
            {
                ShowSlotInTree(want);
                Pump(120);
                if (overviewWindow != null) { Core.Log("FAIL", "OVSELF 独立总览窗点行后没有关闭"); bad++; }
                if (treeView == null || treeView.SelectedKey != want.Key) { Core.Log("FAIL", "OVSELF 独立总览窗点行后没有选中对应位置"); bad++; }
            }
            overviewWindow = new OverviewForm(this);
            overviewWindow.FormClosed += delegate { overviewWindow = null; };
            overviewWindow.Show(this);
            Pump(120);
            overviewWindow.TestEscape();
            Pump(120);
            if (overviewWindow != null) { Core.Log("FAIL", "OVSELF 独立总览窗 Esc 没有关闭"); bad++; }

            Console.WriteLine("ovself failures = " + bad);
            return bad;
        }

        /// <summary>
        /// 布局自检：把窗口撑到 1920×940，挑一个带扩展的位置，看右半是不是真的并排了。
        /// 右半宽 = 面板宽 − 树列(46%，封顶 430)，要 ≥ SideMin(900) 才会并排；
        /// 1440/1600 设计宽下算下来只有 537/670，按设计就该退回上下堆叠，别拿它当失败。
        /// 排布算法本身在 CatTree.LayoutSelf 里，这里只负责给一个真实尺寸和真实位置。
        /// </summary>
        internal int TriPaneSelfTest()
        {
            // 2026-11-20：扩展栏整块移除，右半不再有「候选 + 扩展」并排这回事，
            // 候选区独占右半。这条自检测的东西已经不存在了，保留它会永远报失败。
            Console.WriteLine("tripane skipped: 扩展栏已移除，候选区独占右半");
            return 0;
#pragma warning disable 0162
            int bad = 0;
            Size saved = ClientSize;
            try
            {
                ClientSize = new Size(Dpi.S(1920), Dpi.S(940));
                lastRespW = lastRespH = -1;
                ApplyResponsive();
                EnsureTree();
                // layoutself 走的是「不弹窗口」的直线路径，没有经过正常启动那轮的扫库，
                // 这时 slots 是空的 —— 不补这一下，探测只会得到「库里没有带扩展的位置」。
                if (treeView.SlotCount == 0) Core.ScanMods();
                treeView.Rebuild();

                // 库里一个 MOD 都没有（CI 的干净 runner 就是这种）：没有「带扩展的位置」可量，
                // 与其把一个环境问题报成排版失败，不如明确跳过 —— 否则这条自检永远只能挂
                // continue-on-error，等于没做门禁。
                if (treeView.SlotCount == 0)
                {
                    Core.Log("INFO", "TRI 跳过：库里没有 MOD，量不了并排布局（需要带扩展的位置）");
                    Console.WriteLine("tripane skipped: 库里没有 MOD");
                    return 0;
                }

                bool side, measurable;
                string r = treeView.LayoutProbe(out side, out measurable);
                Core.Log("INFO", "TRI " + r);
                if (!measurable)
                {
                    // 库里没有「带扩展的位置」（干净 runner、或本地一个 MOD 都没装）时量不了，
                    // 这是环境不足，不是排版坏了：明确跳过，才能把这条自检当 CI 硬门禁。
                    Console.WriteLine("tripane skipped: 库里没有带扩展的位置");
                    return 0;
                }
                if (!side)
                {
                    bad++;
                    Core.Log("FAIL", "TRI 1920 宽下没走并排（右半要 ≥ " + Theme.SideMin + " 设计像素，扩展栏该独立成右栏）");
                }
            }
            finally
            {
                ClientSize = saved;
                lastRespW = lastRespH = -1;
                ApplyResponsive();
            }
            Console.WriteLine("tripane failures = " + bad);
            return bad;
        }

        /// <summary>扫库竞态自检：伪造「扫描中」，切目录应被记账，扫完自动补切，不炸不丢。</summary>
        internal int ScanRaceTest()
        {
            int bad = 0;
            string root = Core.Cfg.GameRoot;
            if (string.IsNullOrEmpty(root) || !Core.TestGame(root))
            {
                Core.Log("INFO", "SCANRACE 没有可用的游戏目录，跳过");
                return 0;
            }

            scanning = true;                        // 伪造：后台正在扫库
            SwitchGameRoot(root, "SCANRACE");
            if (pendingRoot == null)
            {
                Core.Log("FAIL", "SCANRACE 扫库期间切目录没有被记账（会当场枚举正被改的表）");
                bad++;
            }
            else Core.Log("INFO", "SCANRACE 扫库期间切目录已记账：" + pendingRoot);

            scanning = false;                       // 扫完了
            RunPendingRoot();
            if (pendingRoot != null) { Core.Log("FAIL", "SCANRACE 补切没跑掉"); bad++; }
            else Core.Log("INFO", "SCANRACE 补切完成，目录 = " + Core.Cfg.GameRoot);

            Console.WriteLine("scanrace failures = " + bad);
            return bad;
        }

        /// <summary>方案树：按「能替换的东西」看这套方案，右边是候选 MOD。</summary>
        internal void ShowTree()
        {
            EnsureTree();
            DropLoading();
            viewHost.Visible = true;
            treeView.Visible = true;
            treeView.SetBounds(0, 0, Math.Max(200, viewHost.ClientSize.Width), Math.Max(120, viewHost.ClientSize.Height));
            treeView.BringToFront();
            treeView.Rebuild();
            // 树是自绘控件，会盖住底部那条状态行：每次换回树都把状态行重新顶到最前
            if (map != null) map.Visible = false;
            if (lblStatus != null) lblStatus.BringToFront();
            if (lblNotice != null) lblNotice.BringToFront();
            UpdateModCaption();
            RefreshGameNow();          // 树的「未归类」数只有建完树才知道，卡片跟着补一次
            // 树是后加进来的，会盖在加载占位上：还在扫就把占位顶回去
            if (lblLoading != null && lblLoading.Visible) lblLoading.BringToFront();
        }

        internal CatTree Tree { get { return treeView; } }

        /// <summary>总览里点一行：先把看板收起来，回方案树定位到那个位置（不收起的话树被盖在下面）。</summary>
        internal void ShowSlotInTree(Slot s)
        {
            if (s == null) return;
            if (overviewWindow != null && !overviewWindow.IsDisposed) overviewWindow.Close();
            if (OverviewOpen) HideOverview();
            else ShowTree();
            if (treeView != null) treeView.SelectSlot(s.Key);
        }

        /// <summary>数据变了（搜索、切模式、库变了）：树重建；桌面那边自己轮询，会跟着变。</summary>
        internal void RebuildViews()
        {
            if (treeView != null) treeView.Rebuild();
        }

        /// <summary>收起加载占位。保留实例：切模式 / 换目录 / 重扫时还要再用。</summary>
        private void DropLoading()
        {
            if (lblLoading == null) return;
            lblLoading.HideLoad();
        }

        /// <summary>
        /// 显示加载占位。full=true 铺满右栏（内容还空着：报步骤 + 不确定进度）；
        /// full=false 只在顶部给一条走动的进度带（已经有内容，别遮住）。
        /// </summary>
        internal void ShowLoading(string title, string hint, bool full)
        {
            if (lblLoading == null) return;
            lblLoading.ShowLoad(title, hint, full);
        }

        internal void HideLoading()
        {
            if (lblLoading == null) return;
            lblLoading.HideLoad();
        }

        private void EnsureTree()
        {
            if (treeView != null) return;
            treeView = new CatTree(this);
            treeView.SetBounds(0, 0, Math.Max(200, viewHost.ClientSize.Width), Math.Max(120, viewHost.ClientSize.Height));
            viewHost.Controls.Add(treeView);
        }

        /// <summary>勾选变了：树的勾、套装计数、底部已选数一起对齐（不重建，滚动位置不动）。</summary>
        internal void SyncPicks()
        {
            if (treeView != null && treeView.Visible) treeView.Sync();
            if (OverviewOpen) map.RefreshPicks();
            UpdateSel();
            UpdateModCaption();          // 台面标题上的「已选 N」也要跟着动
        }

        /// <summary>右栏标题栏：几个位置、已选几个。</summary>
        internal void UpdateModCaption()
        {
            if (gMod == null) return;
            int sc = treeView == null ? 0 : treeView.SlotCount;
            int sk = treeView == null ? 0 : treeView.SlotCheckedCount;
            string tail = treeView != null && treeView.Unmatched > 0 ? " · " + treeView.Unmatched + " 个未归类" : "";
            // 上下文条左边先说「在树的哪一层」，再接数量和已选（没选中位置就还是老文案）
            string crumb = treeView == null ? "" : treeView.CurrentPathText;
            if (lblCtx != null)
                lblCtx.Text = crumb.Length == 0
                    ? Lang.F("方案树 · {0} 个位置 · 已选 {1}{2}", sc, sk, tail)
                    : crumb + "   ·   " + Lang.F("{0} 个位置 · 已选 {1}{2}", sc, sk, tail);
            // 底部状态条右段 = 「MOD 库存信息」：库里的存货（MOD 组数 + 总体积）+ 最近一次装进 addons 的时间。
            // 扫库时 Catalog.Groups 正被后台线程重建，枚举会抛「集合已修改」，这一瞬间沿用上一次的文字。
            if (!Core.ScanInProgress)
            {
                int mods = 0;
                long bytes = 0;
                foreach (ModGroup g in Catalog.Groups)
                {
                    if (g == null) continue;
                    mods++;
                    bytes += g.Size;
                }
                string inv = Lang.F("{0} 个 MOD · {1}", mods, Core.Human(bytes));
                // 装机时间不另存字段：本地模式 addons 就是库，最新那个 vpk 的写入时间就是「上次装过」。
                DateTime last = DateTime.MinValue;
                string addons = Core.GameAddonsDir();
                if (addons != null)
                {
                    try
                    {
                        foreach (string f in System.IO.Directory.GetFiles(addons, "*.vpk"))
                        {
                            DateTime t = System.IO.File.GetLastWriteTime(f);
                            if (t > last) last = t;
                        }
                    }
                    catch { }
                }
                if (last != DateTime.MinValue)
                    inv += " · " + (last.Date == DateTime.Today
                        ? Lang.F("今天 {0}", last.ToString("HH:mm"))
                        : last.ToString("MM-dd HH:mm")) + Lang.S(" 装过");
                statusRight = inv;
            }
            if (statusBar != null) statusBar.Invalidate();
            gMod.Caption = "";
            // 没认出来的 MOD 为 0 时，这个胶囊点了也没内容可看；常驻只增加噪音。
            int unk = treeView == null ? 0 : treeView.Unmatched;
            bool wasOn = chipUnmatchOn;
            string wasText = unmatchedText;
            chipUnmatchOn = unk > 0;
            unmatchedText = Lang.F("未识别 {0} 个", unk);
            if (wasOn != chipUnmatchOn || !string.Equals(wasText, unmatchedText, StringComparison.Ordinal))
            {
                if (placeCtxBar != null) placeCtxBar();
            }
        }
    }
}
