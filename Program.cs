using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);

        private static void ActivateExisting()
        {
            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("L4D2LocalSetup"))
                {
                    p.Refresh();
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    ShowWindow(h, 9);
                    SetForegroundWindow(h);
                    return;
                }
            }
            catch { }
        }

        // 界面线程 / 后台线程的漏网异常都兜住：写日志 + 告诉用户去哪儿看，别弹 .NET 灰框
        private static void InstallCrashHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try { Core.Log("FAIL", "界面异常: " + e.Exception); } catch { }
                try
                {
                    MessageBox.Show("出错了，已经写进日志。\r\n\r\n" + e.Exception.Message + "\r\n\r\n日志目录：" + Core.LogDir,
                        "L4D2 MOD 管理工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { Core.Log("FAIL", "致命异常: " + e.ExceptionObject); Core.CloseLog(); } catch { }
            };
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string gameRoot = null;
            bool selfTest = false;
            bool uiTest = false;
            bool copyTest = false;
            string uiSize = "1280x900";
            string modeOverride = null;
            string themeOverride = null;
            string uiFilter = null;
            string uiDeep = null;
            bool uiSettings = false;
            bool uiTheme = false;
            bool uiTree = false;
            bool mapShot = false;
            bool loadShot = false;
            bool downsShot = false;
            bool mainShot = false;
            bool gallerySelf = false;
            bool langSelf = false;
            bool mapSelf = false;
            bool scanRace = false;
            string uiSelect = null;
            bool uiDrawer = false;
            bool uiDragPrev = false;
            bool uiLogOpen = false;
            string uiDrag = null;
            string uiDragCat = null;
            string uiDragReal = null;
            string mouseDrag = null;
            bool uiSetCat = false;
            bool uiCatForm = false;
            bool uiAddons = false;
            bool uiKeys = false;
            bool wsSelf = false;
            string wsNet = null;
            string wsSearch = null;
            bool uiWorkshop = false;
            bool uiGallery = false;
            int deskServe = 0;
            bool deskSelf = false;
            bool deskLaunch = false;
            string uiFix = null;
            string uiParts = null;
            bool uiLaunch = false;
            bool uiPaint = false;
            bool uiGlyphs = false;
            bool uiDup = false;
            bool dupSelf = false;
            bool launchSelf = false;
                bool addonSelf = false;
            bool safeSelf = false;
            bool dumpId = false;
            string dumpIdTo = null;      // --dumpid=文件：把识别快照写成基线
            string idCheckTo = null;     // --idcheck=文件：拿当前结果跟基线比，改判就报
            bool dumpTree = false;
            bool ovDump = false;
            bool recogSelf = false;
            bool plugSelf = false;
            string plugTry = null;
            bool layoutSelf = false;
            bool importSelf = false;
            bool exclSelf = false;
            bool imgSelf = false;
            bool noDpi = false;
            string vpkFile = null;


            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selfTest = true;
                else if (string.Equals(a, "--uitest", StringComparison.OrdinalIgnoreCase)) uiTest = true;
                else if (a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase)) modeOverride = a.Substring(7).Trim();
                else if (a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase)) themeOverride = a.Substring(8).Trim();
                else if (string.Equals(a, "--copytest", StringComparison.OrdinalIgnoreCase)) copyTest = true;
                else if (a.StartsWith("--uitsize=", StringComparison.OrdinalIgnoreCase)) uiSize = a.Substring(10);
                // 只给 --uifilter 不给 --uitest 时，千万别落进 GUI 主循环 —— 命令行会一直挂着不返回
                else if (a.StartsWith("--uifilter=", StringComparison.OrdinalIgnoreCase)) { uiFilter = a.Substring(11); uiTest = true; }
                else if (a.StartsWith("--uideep=", StringComparison.OrdinalIgnoreCase)) uiDeep = a.Substring(9);
                else if (string.Equals(a, "--uisettings", StringComparison.OrdinalIgnoreCase)) { uiSettings = true; uiTest = true; }
                else if (string.Equals(a, "--uitheme", StringComparison.OrdinalIgnoreCase)) { uiTheme = true; uiTest = true; }
                else if (string.Equals(a, "--uitree", StringComparison.OrdinalIgnoreCase)) uiTree = true;
                else if (string.Equals(a, "--treeshot", StringComparison.OrdinalIgnoreCase)) { mapShot = true; uiTest = true; }
                // 诊断截图：把「读取中」的启动占位截一张，确认步骤文案与进度条在真机上好看
                else if (string.Equals(a, "--loadshot", StringComparison.OrdinalIgnoreCase)) { loadShot = true; uiTest = true; }
                else if (string.Equals(a, "--downsshot", StringComparison.OrdinalIgnoreCase)) { downsShot = true; uiTest = true; }
                else if (string.Equals(a, "--mainshot", StringComparison.OrdinalIgnoreCase)) { mainShot = true; uiTest = true; }
                else if (string.Equals(a, "--treeself", StringComparison.OrdinalIgnoreCase)) { mapSelf = true; uiTest = true; }
                else if (string.Equals(a, "--scanrace", StringComparison.OrdinalIgnoreCase)) { scanRace = true; uiTest = true; }
                else if (a.StartsWith("--uiselect=", StringComparison.OrdinalIgnoreCase)) uiSelect = a.Substring(11);
                // 诊断截图：展开左栏抽屉（装什么 / 安装选项 / 日志），看台面视图的排版
                else if (string.Equals(a, "--drawer", StringComparison.OrdinalIgnoreCase)) uiDrawer = true;
                // 诊断截图：未归类页假装拖拽悬停，验证落点行填色与跟随标签
                else if (string.Equals(a, "--dragpreview", StringComparison.OrdinalIgnoreCase)) uiDragPrev = true;
                // 诊断截图：把左栏日志一并展开（默认是收起的）
                else if (string.Equals(a, "--logopen", StringComparison.OrdinalIgnoreCase)) uiLogOpen = true;
                else if (a.StartsWith("--uidrag=", StringComparison.OrdinalIgnoreCase)) uiDrag = a.Substring(9);
                else if (a.StartsWith("--uidragcat=", StringComparison.OrdinalIgnoreCase)) uiDragCat = a.Substring(12);
                else if (a.StartsWith("--uidragreal=", StringComparison.OrdinalIgnoreCase)) uiDragReal = a.Substring(13);
                else if (a.StartsWith("--mousedrag=", StringComparison.OrdinalIgnoreCase)) mouseDrag = a.Substring(12);
                else if (string.Equals(a, "--uisetcat", StringComparison.OrdinalIgnoreCase)) uiSetCat = true;
                else if (string.Equals(a, "--uicatform", StringComparison.OrdinalIgnoreCase)) uiCatForm = true;
                else if (string.Equals(a, "--uiaddons", StringComparison.OrdinalIgnoreCase)) uiAddons = true;
                else if (string.Equals(a, "--uikeys", StringComparison.OrdinalIgnoreCase)) { uiKeys = true; uiTest = true; }
                // 工坊自检：只测解析/文件名/JSON，不联网
                else if (string.Equals(a, "--workshopself", StringComparison.OrdinalIgnoreCase)) wsSelf = true;
                // 工坊联网端到端：查一次、下一次、校验后删除，不落库
                else if (a.StartsWith("--workshopnet=", StringComparison.OrdinalIgnoreCase)) wsNet = a.Substring(14).Trim();
                else if (a.StartsWith("--workshopsearch=", StringComparison.OrdinalIgnoreCase)) wsSearch = a.Substring(17).Trim();
                // 诊断截图：打开工坊窗口、填假数据、存 workshop.png，不联网
                else if (string.Equals(a, "--workshopui", StringComparison.OrdinalIgnoreCase)) { uiWorkshop = true; uiTest = true; }
                else if (string.Equals(a, "--galleryui", StringComparison.OrdinalIgnoreCase)) { uiGallery = true; uiTest = true; }
                else if (string.Equals(a, "--gallerself", StringComparison.OrdinalIgnoreCase)) { gallerySelf = true; uiTest = true; }
                else if (string.Equals(a, "--langself", StringComparison.OrdinalIgnoreCase)) { langSelf = true; uiTest = true; }
                else if (a.StartsWith("--deskserve=", StringComparison.OrdinalIgnoreCase)) { int.TryParse(a.Substring(12), out deskServe); uiTest = true; }
                else if (string.Equals(a, "--deskself", StringComparison.OrdinalIgnoreCase)) { deskSelf = true; uiTest = true; }
                else if (string.Equals(a, "--desklaunch", StringComparison.OrdinalIgnoreCase)) { deskLaunch = true; uiTest = true; }
                else if (a.StartsWith("--uifix=", StringComparison.OrdinalIgnoreCase)) uiFix = a.Substring(8).Trim('"');
                else if (a.StartsWith("--parts=", StringComparison.OrdinalIgnoreCase)) uiParts = a.Substring(8);
                else if (string.Equals(a, "--launch", StringComparison.OrdinalIgnoreCase)) uiLaunch = true;
                else if (string.Equals(a, "--uipaint", StringComparison.OrdinalIgnoreCase)) { uiPaint = true; uiTest = true; }
                else if (string.Equals(a, "--uiglyphs", StringComparison.OrdinalIgnoreCase)) { uiGlyphs = true; uiTest = true; }
                else if (string.Equals(a, "--dup", StringComparison.OrdinalIgnoreCase)) uiDup = true;
                else if (string.Equals(a, "--dupself", StringComparison.OrdinalIgnoreCase)) dupSelf = true;
                else if (string.Equals(a, "--launchself", StringComparison.OrdinalIgnoreCase)) launchSelf = true;
                    else if (string.Equals(a, "--addonself", StringComparison.OrdinalIgnoreCase)) addonSelf = true;
                else if (string.Equals(a, "--safeself", StringComparison.OrdinalIgnoreCase)) safeSelf = true;
                else if (string.Equals(a, "--dumpid", StringComparison.OrdinalIgnoreCase)) dumpId = true;
            else if (a.StartsWith("--dumpid=", StringComparison.OrdinalIgnoreCase)) dumpIdTo = a.Substring(9).Trim('"');
            else if (a.StartsWith("--idcheck=", StringComparison.OrdinalIgnoreCase)) idCheckTo = a.Substring(10).Trim('"');
                else if (string.Equals(a, "--dumptree", StringComparison.OrdinalIgnoreCase)) dumpTree = true;
                else if (string.Equals(a, "--ovdump", StringComparison.OrdinalIgnoreCase)) ovDump = true;
                else if (string.Equals(a, "--recogself", StringComparison.OrdinalIgnoreCase)) recogSelf = true;
                else if (string.Equals(a, "--plugself", StringComparison.OrdinalIgnoreCase)) plugSelf = true;
                else if (a.StartsWith("--plugtry=", StringComparison.OrdinalIgnoreCase)) plugTry = a.Substring(10).Trim('"');
                else if (string.Equals(a, "--nodpi", StringComparison.OrdinalIgnoreCase)) noDpi = true;
                else if (string.Equals(a, "--layoutself", StringComparison.OrdinalIgnoreCase)) layoutSelf = true;
                else if (string.Equals(a, "--importself", StringComparison.OrdinalIgnoreCase)) importSelf = true;
                else if (string.Equals(a, "--excluself", StringComparison.OrdinalIgnoreCase)) exclSelf = true;
                else if (string.Equals(a, "--imgself", StringComparison.OrdinalIgnoreCase)) imgSelf = true;
                else if (a.StartsWith("--vpk=", StringComparison.OrdinalIgnoreCase)) vpkFile = a.Substring(6).Trim('"');

                else if (a.StartsWith("--game=", StringComparison.OrdinalIgnoreCase)) gameRoot = a.Substring(7).Trim('"');
            }

            // 自检全程不许写配置：以前 --addonself 把 Mode 改成本地模式后没写回去，
            // 跑一次自检用户的工作模式就变了（库从 223 个 MOD 变成 49 个）。
            // 必须在下面这些分支 return **之前** 置位 —— 它们一样会跑扫描、开界面，一样会改到配置；
            // 以前这行写在所有自检分支后面，等于对它们全部无效。
            Core.SelfTesting = selfTest || uiTest || copyTest || launchSelf || dupSelf
                            || addonSelf || safeSelf || dumpId || dumpTree || ovDump || recogSelf || plugSelf || plugTry != null
                            || dumpIdTo != null || idCheckTo != null
                            || mapShot || mapSelf || scanRace || layoutSelf || imgSelf || wsSelf || wsNet != null || wsSearch != null;

            // 单实例只约束界面模式：已经开着就把那个窗口叫到前面来。
            // 用 Core.SelfTesting 判，不再手写参数清单 —— 以前漏了 --workshopself / --layoutself /
            // --workshopnet=… / --wssearch=…，GUI 开着时跑这些自检会激活旧窗口然后 return 0，
            // CI 拿到 exit 0 误以为自检通过。
            bool created;
            // 单实例名要跟便携版分开：两个 exe 同时开着是正常用法（一个管库一个管本机）
            Mutex only = new Mutex(true, "L4D2Local_SingleInstance", out created);
            if (!created && !Core.SelfTesting && vpkFile == null)
            {
                ActivateExisting();
                try { only.Dispose(); } catch { }
                return 0;
            }

            // 先把崩溃钩子装上，再碰磁盘。Init 里要定日志目录、建目录，
            // 在只读位置（Program Files / 写保护的 U 盘）上那一步会失败 ——
            // 钩子以前装在 Init 之后，这类失败一条记录都不留，看起来就是「双击没反应」。
            InstallCrashHandlers();
            Core.Init();
            Theme.Init();
            // 高 DPI：必须在任何窗口创建之前声明，否则系统已经按老规矩把窗口当位图拉伸了。
            // --nodpi 或配置里 DpiAware=0 时完全跳过，界面行为和以前一模一样。
            Dpi.Init(!noDpi && Core.Cfg.DpiAware);
            if (Dpi.Scaled) Core.Log("INFO", "界面缩放：" + (int)Math.Round(Dpi.Factor * 100) + "%（按设计值等比放大，不再是模糊拉伸）");
            // 诊断用：临时覆盖外观，不写回配置
            if (!string.IsNullOrEmpty(themeOverride)) Theme.SetMode(Theme.Parse(themeOverride));
            // 诊断用：临时覆盖工作模式，不写回配置
            if (!string.IsNullOrEmpty(modeOverride))
            {
                Core.ModeLock = Core.Cfg.Mode;      // 保存配置时写回原值：诊断参数不落盘
                if (string.Equals(modeOverride, "local", StringComparison.OrdinalIgnoreCase)) Core.Cfg.Mode = "local";
                else if (string.Equals(modeOverride, "usb", StringComparison.OrdinalIgnoreCase)) Core.Cfg.Mode = "usb";
                else Core.Log("WARN", "未知的 --mode 值: " + modeOverride + "（只认 local / usb）");
            }
            if (vpkFile != null)
            {
                // 诊断用：把某个 vpk 的目录表打到 _装机记录\vpklist.txt，方便核对解析对不对
                System.Collections.Generic.List<string> ps = Vpk.Paths(vpkFile, 300000);
                System.Text.StringBuilder sbv = new System.Text.StringBuilder();
                sbv.AppendLine("# " + vpkFile);
                sbv.AppendLine("# 共 " + ps.Count + " 条");
                foreach (string p in ps) sbv.AppendLine(p);
                System.IO.File.WriteAllText(System.IO.Path.Combine(Core.LogDir, "vpklist.txt"), sbv.ToString(), new System.Text.UTF8Encoding(false));
                return 0;
            }
            if (layoutSelf)
            {
                // 布局自检：不显示窗口，只把主窗口的排版在几档缩放下算一遍，
                // 看按钮有没有叠在一起、右栏有没有跑到窗口外面。
                using (MainForm lf = new MainForm(gameRoot))
                {
                    int bad = 0;
                    float[] scales = new float[] { 1f, 1.25f, 1.5f, 2f };
                    foreach (float k in scales) bad += lf.LayoutSelfTest(k, 1440, 940);
                    foreach (float k in scales) bad += lf.LayoutSelfTest(k, 1120, 740);
                    bad += lf.TriPaneSelfTest();          // 右半并排：树 / 候选 / 扩展 三栏
                    bad += lf.WorkshopSmoke();            // 工坊窗口：构造 + 填样例 + 换尺寸
                    Console.WriteLine("layoutself failures = " + bad);
                    return bad == 0 ? 0 : 1;
                }
            }
            if (importSelf)
            {
                // 导入自检：假 addons 里塞一个 vpk 和一个装着两个 vpk 的 zip，
                // 再跑一遍确认「同名跳过」—— 不碰真游戏目录。
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "l4d2import_" + DateTime.Now.ToString("HHmmss"));
                string pack = System.IO.Path.Combine(root, "src");
                string dst = System.IO.Path.Combine(root, "addons");
                System.IO.Directory.CreateDirectory(pack);
                System.IO.Directory.CreateDirectory(dst);
                System.IO.File.WriteAllText(System.IO.Path.Combine(pack, "a.vpk"), "dummy-a");
                System.IO.File.WriteAllText(System.IO.Path.Combine(pack, "b.vpk"), "dummy-b");
                string zip = System.IO.Path.Combine(root, "pack.zip");
                System.IO.Compression.ZipFile.CreateFromDirectory(pack, zip);
                string lone = System.IO.Path.Combine(root, "c.vpk");
                System.IO.File.WriteAllText(lone, "dummy-c");

                int added, skipped; System.Collections.Generic.List<string> errs;
                int bad = 0;
                string first = Catalog.ImportTo(new string[] { zip, lone }, dst, out added, out skipped, out errs);
                if (added != 3) { bad++; Console.WriteLine("IMPORTSELF 首批应导入 3 个，实际 " + added + "（" + first + "）"); }
                if (!System.IO.File.Exists(System.IO.Path.Combine(dst, "a.vpk")) || !System.IO.File.Exists(System.IO.Path.Combine(dst, "c.vpk"))) { bad++; Console.WriteLine("IMPORTSELF 文件没落地"); }
                Catalog.ImportTo(new string[] { zip, lone }, dst, out added, out skipped, out errs);
                if (added != 0 || skipped != 3) { bad++; Console.WriteLine("IMPORTSELF 重复导入应全跳过，实际 added=" + added + " skipped=" + skipped); }
                Console.WriteLine("importself failures = " + bad);
                return bad == 0 ? 0 : 1;
            }
            if (exclSelf)
            {
                // 八人包 ↔ 一代 / 二代 互斥：纯规则自检，不碰界面、不写 addonlist。
                // 规则（2026-11-19 改）：①开八人包 → 人物里其他全关；
                // ②反向要一代 4 + 二代 4 全被单独 MOD 占满才关八人包，没占满就留着打底。
                int bad = 0;
                string[] soloNames = new string[] { "比尔", "佐伊", "路易斯", "弗朗西斯", "教练", "埃利斯", "尼克", "罗谢尔" };
                System.Collections.Generic.Dictionary<string, string> sub =
                    new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                System.Collections.Generic.Dictionary<string, string> pos =
                    new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                System.Collections.Generic.List<string> soloPos = new System.Collections.Generic.List<string>();
                sub["八人.vpk"] = "八人包";
                for (int i = 0; i < soloNames.Length; i++)
                {
                    sub[soloNames[i] + ".vpk"] = i < 4 ? "一代" : "二代";
                    pos[soloNames[i] + ".vpk"] = "pos:" + soloNames[i];
                    soloPos.Add("pos:" + soloNames[i]);
                }
                sub["枪.vpk"] = "步枪";
                sub["双人.vpk"] = "其他";

                // 1) 打开八人包 → 人物里其他全关；枪械不碰
                System.Collections.Generic.Dictionary<string, bool> pre =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                pre["比尔.vpk"] = true; pre["教练.vpk"] = true; pre["枪.vpk"] = true;
                System.Func<string, bool> isOn = delegate(string n) { bool v; return pre.TryGetValue(n, out v) && v; };
                System.Collections.Generic.Dictionary<string, bool> st1 =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                st1["八人.vpk"] = true;
                MainForm.ApplyEightPackRule(st1, sub, pos, soloPos, isOn);
                if (!(st1.ContainsKey("比尔.vpk") && !st1["比尔.vpk"])) { bad++; Console.WriteLine("EXCL 八人包没关掉比尔"); }
                if (!(st1.ContainsKey("教练.vpk") && !st1["教练.vpk"])) { bad++; Console.WriteLine("EXCL 八人包没关掉教练"); }
                if (st1.ContainsKey("枪.vpk")) { bad++; Console.WriteLine("EXCL 误动了非人物"); }

                // 2) 只开一个人、八个位置没占满 → 八人包留着不动
                System.Collections.Generic.Dictionary<string, bool> pre2 =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                pre2["八人.vpk"] = true;
                System.Func<string, bool> isOn2 = delegate(string n) { bool v; return pre2.TryGetValue(n, out v) && v; };
                System.Collections.Generic.Dictionary<string, bool> st2 =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                st2["比尔.vpk"] = true;
                MainForm.ApplyEightPackRule(st2, sub, pos, soloPos, isOn2);
                if (st2.ContainsKey("八人.vpk")) { bad++; Console.WriteLine("EXCL 只开一个人就关掉了八人包"); }

                // 3) 一代 4 + 二代 4 全占满（最后点的是罗谢尔）→ 关八人包
                System.Collections.Generic.Dictionary<string, bool> pre3 =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                pre3["八人.vpk"] = true;
                for (int i = 0; i < soloNames.Length; i++) if (soloNames[i] != "罗谢尔") pre3[soloNames[i] + ".vpk"] = true;
                System.Func<string, bool> isOn3 = delegate(string n) { bool v; return pre3.TryGetValue(n, out v) && v; };
                System.Collections.Generic.Dictionary<string, bool> st3 =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                st3["罗谢尔.vpk"] = true;
                MainForm.ApplyEightPackRule(st3, sub, pos, soloPos, isOn3);
                if (!(st3.ContainsKey("八人.vpk") && !st3["八人.vpk"])) { bad++; Console.WriteLine("EXCL 八个位置占满后没关八人包"); }

                // 4) 同阵营（一代 ↔ 一代）不互斥
                System.Collections.Generic.Dictionary<string, bool> pre4 =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                pre4["佐伊.vpk"] = true;
                System.Func<string, bool> isOn4 = delegate(string n) { bool v; return pre4.TryGetValue(n, out v) && v; };
                System.Collections.Generic.Dictionary<string, bool> st4 =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                st4["比尔.vpk"] = true;
                MainForm.ApplyEightPackRule(st4, sub, pos, soloPos, isOn4);
                if (st4.ContainsKey("佐伊.vpk")) { bad++; Console.WriteLine("EXCL 误伤了同阵营的一代"); }

                // 5) 双人包（小分类=其他）不触发互斥，八人包也不动
                System.Collections.Generic.Dictionary<string, bool> pre5 =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                pre5["八人.vpk"] = true;
                System.Func<string, bool> isOn5 = delegate(string n) { bool v; return pre5.TryGetValue(n, out v) && v; };
                System.Collections.Generic.Dictionary<string, bool> st5 =
                    new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                st5["双人.vpk"] = true;
                MainForm.ApplyEightPackRule(st5, sub, pos, soloPos, isOn5);
                if (st5.ContainsKey("八人.vpk") || st5.ContainsKey("比尔.vpk")) { bad++; Console.WriteLine("EXCL 双人包不该触发互斥"); }

                Console.WriteLine("excluself failures = " + bad);
                return bad == 0 ? 0 : 1;
            }
            if (wsSelf)
            {
                // 工坊自检：纯离线，测链接解析、文件名清理、JSON 提取。
                int badw = Workshop.SelfTest();
                Console.WriteLine("workshopself failures = " + badw);
                return badw == 0 ? 0 : 1;
            }
            if (wsNet != null)
            {
                int badn = Workshop.NetTest(wsNet);
                Console.WriteLine("workshopnet failures = " + badn);
                return badn == 0 ? 0 : 1;
            }
            if (wsSearch != null)
            {
                int bads = Workshop.SearchTest(wsSearch);
                Console.WriteLine("workshopsearch failures = " + bads);
                return bads == 0 ? 0 : 1;
            }
            if (dumpIdTo != null)
            {
                // 写回归基线（识别快照），改动前后跑 --idcheck 对比
                Core.ScanMods();
                string f = Catalog.DumpIdentifyTo(dumpIdTo);
                Console.WriteLine("baseline: " + f + "（" + Catalog.Groups.Count + " 个 MOD）");
                return 0;
            }
            if (idCheckTo != null)
            {
                Core.ScanMods();
                int changed = Catalog.CheckIdentify(idCheckTo);
                return changed == 0 ? 0 : 1;
            }
            if (dumpId)
            {
                Core.ScanMods();
                string f = Catalog.DumpIdentify();
                Console.WriteLine("dumpid: " + f + "（" + Catalog.Groups.Count + " 个 MOD）");
                return 0;
            }
            if (dumpTree)
            {
                Core.ScanMods();
                string f = SlotIndex.DumpTree();
                Console.WriteLine("dumptree: " + f);
                return 0;
            }
            if (ovDump)
            {
                // 总览：不开窗口，按当前勾选摊一份文本 —— 核对「哪个位置装了哪个」和各类的计数
                Core.ScanMods();
                System.Collections.Generic.List<Slot> tslots = SlotIndex.Build();
                SlotIndex.SortForTree(tslots, null);      // 跟方案树同一套顺序
                System.Collections.Generic.HashSet<string> onKeys =
                    new System.Collections.Generic.HashSet<string>(Core.Cfg.Mods, StringComparer.OrdinalIgnoreCase);
                OvModel om = OvModel.Build(tslots,
                    delegate(ModGroup g)
                    {
                        int n = 0;
                        foreach (ModItem pi in g.Parts) if (onKeys.Contains(pi.Key)) n++;
                        return n;
                    },
                    delegate(ModItem pi) { return onKeys.Contains(pi.Key); },
                    "", 0, 0);
                string txt = System.IO.Path.Combine(Core.LogDir, "总览.txt");
                System.IO.File.WriteAllText(txt, om.Dump(), new System.Text.UTF8Encoding(false));
                Console.WriteLine("ovdump: " + txt + "（大类 " + om.Cats.Count
                    + " / 位置 " + om.SlotsTotal + " / 已装 " + om.SlotsOn
                    + " / vpk " + om.Parts + " / " + Core.Human(om.Size) + "）");
                return 0;
            }
            if (recogSelf)
            {
                int bad = Catalog.SelfTestRecognition();
                Console.WriteLine("recogself failures = " + bad);
                return bad == 0 ? 0 : 1;
            }
            if (plugSelf)
            {
                // 插件组件自检：纯离线，只验共享判定和目录解析，不碰游戏目录
                int bad = PluginKit.SelfTest();
                Console.WriteLine("plugself failures = " + bad);
                return bad == 0 ? 0 : 1;
            }
            if (plugTry != null)
            {
                // 真实库 → 假游戏目录：完整走一遍装插件 / 写记录 / 一键还原
                int bad = Work.PluginRoundTrip(plugTry);
                return bad == 0 ? 0 : 1;
            }
            if (addonSelf)
            {
                int bad = Work.SelfTestAddons();
                Console.WriteLine("addonself failures = " + bad);
                return bad == 0 ? 0 : 1;
            }
            if (safeSelf)
            {
                int bad = Work.SelfTestSafe();
                return bad == 0 ? 0 : 1;
            }
            if (launchSelf)
            {
                int bad = LaunchArgs.SelfTest();
                Console.WriteLine("launchself failures = " + bad);
                return bad == 0 ? 0 : 1;
            }
            if (dupSelf)
            {
                int bad = DupVerify.SelfTest();
                Console.WriteLine("dupself failures = " + bad);
                return bad == 0 ? 0 : 1;
            }
            if (imgSelf)
            {
                // 封面图自检：遍历游戏 addons，看 ReadAddonImage 到底能解出几张、解出来的是不是真图片。
                // 只读不写，不解包到临时目录，跑一遍就能判断这条路的覆盖率。
                string addons = Core.GameAddonsDir();
                if (addons == null)
                {
                    Console.WriteLine("imgself: 找不到 addons 目录（游戏路径没配？）");
                    return 1;
                }
                string[] vpks = System.IO.Directory.GetFiles(addons, "*.vpk", System.IO.SearchOption.AllDirectories);
                Console.WriteLine("[imgself] addons = " + addons);
                Console.WriteLine("[imgself] vpk 总数 " + vpks.Length);
                int hit = 0, miss = 0, broken = 0, sidecar = 0;
                foreach (string f in vpks)
                {
                    // 走 Cover 这一层：解包 → 裁成 16:9 → 写缓存，一条链路全验
                    string thumb = Cover.For(f);
                    if (thumb == null)
                    {
                        miss++;
                        // 工坊 MOD 的图不在包里，是同目录的 <id>.jpg，这里顺手统计一下能补上多少
                        string stem = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f),
                                                             System.IO.Path.GetFileNameWithoutExtension(f));
                        if (System.IO.File.Exists(stem + ".jpg") || System.IO.File.Exists(stem + ".png")
                            || System.IO.File.Exists(stem + ".gif") || System.IO.File.Exists(stem + ".jpeg"))
                            sidecar++;
                        continue;
                    }
                    try
                    {
                        using (System.Drawing.Image im = System.Drawing.Image.FromFile(thumb))
                        {
                            hit++;
                            if (im.Width != Cover.ThumbW || im.Height != Cover.ThumbH) broken++;
                            if (hit <= 6)
                                Console.WriteLine("  OK " + System.IO.Path.GetFileName(f)
                                    + "  → " + im.Width + "x" + im.Height
                                    + "  " + (new System.IO.FileInfo(thumb).Length / 1024) + " KB");
                        }
                    }
                    catch (Exception ex)
                    {
                        broken++;
                        Console.WriteLine("  缩略图打不开 " + System.IO.Path.GetFileName(f) + "  " + ex.Message);
                    }
                }
                // 第二遍必须全命中缓存，否则说明缓存没写进去或 key 不稳定
                int hits2 = 0, drift = 0;
                foreach (string f in vpks)
                {
                    try { if (Cover.For(f) != null) hits2++; } catch { drift++; }
                }
                Console.WriteLine("[imgself] 有封面 " + hit + " / 无封面 " + miss + "（其中同 ID 配图可补 " + sidecar
                                  + "） / 尺寸异常或打不开 " + broken);
                Console.WriteLine("[imgself] 二次调用命中 " + hits2 + " / 异常 " + drift + "（应等于上面的有封面数）");
                return broken == 0 && drift == 0 && hits2 == hit ? 0 : 1;
            }

            if (selfTest) return Work.SelfTest(gameRoot);
            if (copyTest)
            {
                int bad = 0;      // 任何一个用例不对就让退出码非 0，CI/脚本才判得出来
                Core.ScanMods();
                System.Collections.Generic.List<ModItem> pick = new System.Collections.Generic.List<ModItem>();
                foreach (ModItem m in Core.ModList) { if (!m.IsArchive && m.Size > 1000000 && m.Size < 60000000) { pick.Add(m); if (pick.Count >= 4) break; } }
                // 再加一个压缩包里的 vpk，验证「从 rar 里取出单个文件铺到 addons」这条路
                ModItem arcOne = null;
                foreach (ModItem m in Core.ModList) if (m.IsArchive && m.Size > 0 && m.Size < 80000000) { arcOne = m; break; }
                if (arcOne != null) pick.Add(arcOne);
                if (pick.Count == 0) { Console.WriteLine("copytest: 库里找不到可测的文件，跳过"); bad++; }

                // 7z 用例：Rar.exe 不认 7z，解包要换图形版 WinRAR.exe，这里真解一次核对字节数
                string z7line;
                ModItem seven = null;
                foreach (ModItem m in Core.ModList)
                    if (m.IsArchive && m.Size > 0 && m.FullPath != null
                        && m.FullPath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) { seven = m; break; }
                if (seven == null) z7line = "7z 解包: （库里没有 7z）\r\n";
                else
                {
                    string zDst = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copytest-7z.vpk");
                    string zWhy;
                    bool zOk = Work.ExtractOne(seven, zDst, out zWhy);
                    long zGot = -1;
                    try { if (System.IO.File.Exists(zDst)) zGot = new System.IO.FileInfo(zDst).Length; } catch { }
                    z7line = "7z 解包: " + seven.Inner + " | 期望 " + seven.Size + " | 实得 " + zGot
                           + (zOk && zGot == seven.Size ? "  [OK]\r\n" : "  [FAIL " + (zWhy ?? "字节数不对") + "]\r\n");
                    if (!(zOk && zGot == seven.Size)) bad++;
                    try { if (System.IO.File.Exists(zDst)) System.IO.File.Delete(zDst); } catch { }
                }
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copytest");
                if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmp, "left4dead2\\addons"));
                int c1, s1; System.Collections.Generic.List<string> a1, f1;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                Work.CopyMods(tmp, pick, null, out c1, out s1, out a1, out f1);
                sw.Stop();
                if (f1.Count > 0) bad++;
                System.IO.File.WriteAllText(System.IO.Path.Combine(Core.LogDir, "copytest.txt"),
                    "第一遍: 新装 " + c1 + " 跳过 " + s1 + " 失败 " + f1.Count + " 用时 " + Math.Round(sw.Elapsed.TotalSeconds, 2) + " 秒\r\n" +
                    "落盘 " + System.IO.Directory.GetFiles(System.IO.Path.Combine(tmp, "left4dead2\\addons")).Length + " 个\r\n" +
                    "压缩包项: " + (arcOne == null ? "（没找到可测的）" : (arcOne.Container + " | " + arcOne.Inner + " | 期望 " + arcOne.Size + " 字节")) + "\r\n" +
                    z7line +
                    "失败项: " + (f1.Count == 0 ? "无" : string.Join("、", f1.ToArray())) + "\r\n");
                int c2, s2; System.Collections.Generic.List<string> a2, f2;
                System.Diagnostics.Stopwatch sw2 = System.Diagnostics.Stopwatch.StartNew();
                Work.CopyMods(tmp, pick, null, out c2, out s2, out a2, out f2);
                sw2.Stop();
                System.IO.File.AppendAllText(System.IO.Path.Combine(Core.LogDir, "copytest.txt"),
                    "第二遍（跳过模式）: 新装 " + c2 + " 跳过 " + s2 + " 失败 " + f2.Count + " 用时 " + Math.Round(sw2.Elapsed.TotalSeconds, 2) + " 秒\r\n");
                // 第三遍：覆盖模式 —— 已经装好的也要全部重写，跳过数必须是 0
                int c3, s3; System.Collections.Generic.List<string> a3, f3;
                Work.Overwrite = true;
                System.Diagnostics.Stopwatch sw3 = System.Diagnostics.Stopwatch.StartNew();
                Work.CopyMods(tmp, pick, null, out c3, out s3, out a3, out f3);
                sw3.Stop();
                Work.Overwrite = false;
                System.IO.File.AppendAllText(System.IO.Path.Combine(Core.LogDir, "copytest.txt"),
                    "第三遍（覆盖模式）: 重写 " + c3 + " 跳过 " + s3 + " 失败 " + f3.Count + " 用时 " + Math.Round(sw3.Elapsed.TotalSeconds, 2) + " 秒" +
                    (c3 == pick.Count && s3 == 0 ? "  [OK 全部重写]\r\n" : "  [FAIL 应该全部重写]\r\n"));
                if (!(c3 == pick.Count && s3 == 0)) bad++;

                // 停止语义：在开始前请求停止，复制任务应当一个文件都不开写。
                string tmpStop = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copytest-stop");
                if (System.IO.Directory.Exists(tmpStop)) System.IO.Directory.Delete(tmpStop, true);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmpStop, "left4dead2\\addons"));
                int cs, ss; System.Collections.Generic.List<string> as1, fs1;
                Work.CancelRequested = true;
                Work.CopyMods(tmpStop, pick, null, out cs, out ss, out as1, out fs1);
                Work.CancelRequested = false;
                int stopFiles = System.IO.Directory.GetFiles(System.IO.Path.Combine(tmpStop, "left4dead2\\addons")).Length;
                System.IO.File.AppendAllText(System.IO.Path.Combine(Core.LogDir, "copytest.txt"),
                    "停止测试: 新装 " + cs + " 跳过 " + ss + " 失败 " + fs1.Count + " 落盘 " + stopFiles + " 个"
                    + (cs == 0 && stopFiles == 0 ? "  [OK 停止后没有开写]\r\n" : "  [FAIL 停止后仍然复制了文件]\r\n"));
                if (!(cs == 0 && stopFiles == 0)) bad++;
                System.IO.Directory.Delete(tmpStop, true);
                System.IO.Directory.Delete(tmp, true);
                Console.WriteLine("copytest failures = " + bad + (bad == 0 ? "" : "（明细见 copytest.txt）"));
                return bad == 0 ? 0 : 1;
            }

            if (uiTest)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // --langself 要在建窗口之前就把语言切成英文，否则控件文案还是按中文建出来的
                if (langSelf) Lang.Cur = Lang.EN;
                using (MainForm f = new MainForm(gameRoot))
                {
                    // --uitsize=0x0 表示不强制尺寸，按窗口自己的初始大小截（用来核对默认值）
                    string[] wh = uiSize.Split('x');
                    int uw, uh;
                    if (wh.Length == 2 && int.TryParse(wh[0], out uw) && int.TryParse(wh[1], out uh) && uw > 0 && uh > 0)
                        f.ClientSize = new System.Drawing.Size(uw, uh);
                    else if (!string.Equals(uiSize.Trim(), "0x0", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("uitsize 参数看不懂: " + uiSize + "（要 1280x900 这种；0x0 表示不强制）");
                    f.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                    f.Location = new System.Drawing.Point(-4000, -4000);
                    f.Show();
                    System.Windows.Forms.Application.DoEvents();
                    f.PerformLayout();
                    // 后台在扫库和填表（现在还要读几十个压缩包的目录），截图前得等它真的扫完
                    for (int i = 0; i < 2400; i++)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        System.Threading.Thread.Sleep(50);
                        if (i > 20 && f.Ready) break;
                    }
                    System.Windows.Forms.Application.DoEvents();
                    if (uiGlyphs)
                    {
                        f.UiTestGlyphs();
                        System.Windows.Forms.Application.DoEvents();
                        Console.WriteLine("glyphsheet ok");
                        return 0;
                    }
                    if (uiPaint)
                    {
                        // 真实屏幕渲染的主题回归：抓屏 + 记录原生控件底色，失败条数当退出码
                        int badp = f.UiPaintTest();
                        System.Windows.Forms.Application.DoEvents();
                        Console.WriteLine("paintself failures = " + badp);
                        return badp == 0 ? 0 : 1;
                    }
                    if (deskServe > 0)
                    {
                        // 只把桌面服务起起来，不弹浏览器：给无头截图用
                        f.DeskServe(deskServe);
                        return 0;
                    }
                    if (deskLaunch)
                    {
                        // 真开一次桌面窗口：起没起来、重复点、关掉退没退
                        int badl = f.DeskLaunchTest();
                        System.Windows.Forms.Application.DoEvents();
                        return badl == 0 ? 0 : 1;
                    }
                    if (deskSelf)
                    {
                        // 台面回归：建卡 / 拖动落位 / 折叠 / 重启后还记得位置。结束时还原 台面布局.ini。
                        int badd = f.DeskSelfTest();
                        System.Windows.Forms.Application.DoEvents();
                        Console.WriteLine("deskself failures = " + badd);
                        return badd == 0 ? 0 : 1;
                    }
                    if (uiKeys)
                    {
                        // 键盘路径回归：只按方向键 / Enter / 空格，不看鼠标。失败条数当退出码。
                        int bad = f.UiTestKeys();
                        System.Windows.Forms.Application.DoEvents();
                        Console.WriteLine("keyself failures = " + bad);
                        return bad == 0 ? 0 : 1;
                    }
                    if (uiParts != null) { f.UiTestParts(uiParts); System.Windows.Forms.Application.DoEvents(); }
                    if (uiLaunch) { f.UiTestLaunch(); System.Windows.Forms.Application.DoEvents(); }
                    if (uiDup) { f.UiTestDup(); System.Windows.Forms.Application.DoEvents(); }
                    // --logopen 单独就能生效：截图要看「日志展开时左栏怎么排」，不该非得先进抽屉自检
                    if (uiLogOpen && !uiDrawer) { f.SetLogOpen(true); System.Windows.Forms.Application.DoEvents(); }
                    if (mapShot) { f.OverviewShot(); System.Windows.Forms.Application.DoEvents(); return 0; }
                    if (loadShot) { f.LoadingShot(); System.Windows.Forms.Application.DoEvents(); return 0; }
                    if (downsShot) { f.DownsShot(); System.Windows.Forms.Application.DoEvents(); return 0; }
                    if (mainShot) { f.MainShot(); System.Windows.Forms.Application.DoEvents(); return 0; }
                    if (scanRace) { f.ScanRaceTest(); System.Windows.Forms.Application.DoEvents(); return 0; }
                    if (mapSelf)
                    {
                        int badm = f.OverviewSelfTest();
                        System.Windows.Forms.Application.DoEvents();
                        return badm == 0 ? 0 : 1;
                    }
                    if (uiTree) { f.UiTestTree(); System.Windows.Forms.Application.DoEvents(); }
                    if (uiSelect != null) { f.UiTestSelect(uiSelect); System.Windows.Forms.Application.DoEvents(); }
                    if (uiDrawer) { f.UiTestDrawer(uiLogOpen); System.Windows.Forms.Application.DoEvents(); }
                    if (uiDragPrev) { f.UiTestDragHover(); System.Windows.Forms.Application.DoEvents(); }
                    if (uiDrag != null) { f.UiTestDrag(uiDrag); System.Windows.Forms.Application.DoEvents(); }
                    if (uiDragCat != null)
                    {
                        string[] dc = uiDragCat.Split('>');
                        f.UiTestCatDrag(dc[0], dc.Length > 1 ? dc[1] : "");
                        System.Windows.Forms.Application.DoEvents();
                    }
                    if (uiDragReal != null)
                    {
                        string[] dc = uiDragReal.Split('>');
                        f.UiTestRealDrag(dc[0], dc.Length > 1 ? dc[1] : "");
                        System.Windows.Forms.Application.DoEvents();
                    }
                    if (mouseDrag != null)
                    {
                        string[] dc = mouseDrag.Split('>');
                        f.UiTestMouseDrag(dc[0], dc.Length > 1 ? dc[1] : "");
                        System.Windows.Forms.Application.DoEvents();
                    }
                    if (uiSetCat) { f.UiTestSetCat(); System.Windows.Forms.Application.DoEvents(); }
                    if (uiCatForm) { f.UiTestCatForm(); System.Windows.Forms.Application.DoEvents(); }
                    if (uiAddons) { f.UiTestAddonSwitch(); System.Windows.Forms.Application.DoEvents(); }
                    if (uiFix != null) { f.UiFixProfile(uiFix); System.Windows.Forms.Application.DoEvents(); }
                            if (uiSettings) { f.UiTestSettings(); System.Windows.Forms.Application.DoEvents(); }
                    if (uiTheme)
                    {
                        int bad = f.UiTestTheme();
                        System.Windows.Forms.Application.DoEvents();
                        Console.WriteLine("themeself failures = " + bad);
                        return bad == 0 ? 0 : 1;
                    }
                    if (uiWorkshop) { f.WorkshopShot(); System.Windows.Forms.Application.DoEvents(); return 0; }
                    if (langSelf) { f.LangSelf(); System.Windows.Forms.Application.DoEvents(); return 0; }
                    if (gallerySelf) { int badg = f.GallerySelf(); System.Windows.Forms.Application.DoEvents(); return badg == 0 ? 0 : 1; }
                    if (uiGallery) { f.GalleryShot(); System.Windows.Forms.Application.DoEvents(); return 0; }
                    if (uiFilter != null) { f.UiTestFilter(uiFilter); System.Windows.Forms.Application.DoEvents(); }
                    if (uiDeep != null) { f.UiTestFilter(uiDeep); System.Windows.Forms.Application.DoEvents(); f.UiTestDeep(); System.Windows.Forms.Application.DoEvents(); }
                    System.IO.File.WriteAllText(System.IO.Path.Combine(Core.LogDir, "uitest.txt"), f.LayoutDump(), new System.Text.UTF8Encoding(true));
                    // DrawToBitmap 画的是整窗口（含标题栏），按客户区尺寸截图会把底部 31px 裁掉
                    System.Drawing.Size full = f.Size;
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(full.Width, full.Height))
                    {
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, full.Width, full.Height));
                        bmp.Save(System.IO.Path.Combine(Core.LogDir, "uitest.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 切语言不重启进程：把主窗口关掉重建一遍。
            // 界面文案是在控件构造时定下来的，改语言只能重建；重建比让用户手动重开顺手得多。
            // （状态靠配置和 Core 的静态量带过去，用户看到的就是"刷"一下变语言。）
            bool again = false;
            do
            {
                again = false;
                using (MainForm f = new MainForm(gameRoot))
                {
                    Application.Run(f);
                    again = f.LangRestartRequested;
                }
                if (again)
                {
                    Lang.Load();          // 配置里已经是新语言了
                    Lang.EnsureBuilt();
                }
            } while (again);
            return 0;
        }
    }
}
