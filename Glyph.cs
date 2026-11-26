using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Installer
{
    /// <summary>
    /// MOD 分类的单色剪影图标。
    ///
    /// 照 L4D2 HUD 那套做法：只有轮廓、不用颜色，所以浅色深色都能直接套同一个形状，
    /// 颜色由调用方传。全部在 16x16 的网格里搭路径，画的时候缩放到 14~15px。
    ///
    /// 小尺寸下的取舍：凡是 1px 以下的细节都会被抗锯齿磨成灰雾，所以这里一律只保留
    /// 「一眼能读出来的大块」——枪就是横机匣 + 明显下垂的弹匣/握把，斧就是竖柄 + 大三角。
    /// 细枝末节（准星、扳机护圈、护木纹路）全部砍掉，宁可少画也不要糊。
    /// </summary>
    internal static class Glyph
    {
        public const int Grid = 16;

        /// <summary>大类 → 图标。树的第一层。</summary>
        public static string ForCat(string cat)
        {
            switch (cat)
            {
                case "枪械": return "gun";
                case "近战": return "melee";
                case "投掷物": return "bottle";
                case "弹药升级": return "ammo";
                case "人物": return "person";
                // 特感以前落到 default 的 misc（叠片杂物），八只感染者共用一个杂物图标；
                // 小分类 "claw" 早就有，大类这里补上，两层才对得上。
                // 2026-11-21 大类改名「僵尸」，下面分「小僵尸」「特感」；老名字继续接。
                case "僵尸": case "特感": return "claw";
                case "药品": return "medkit";
                case "地图":
                case "三方图": return "map";
                case "喷漆": return "spray";
                case "我的分支": return "branch";
                case "未归类": return "unknown";
                default: return "misc";
            }
        }

        /// <summary>小分类 → 图标。认不出来的退回大类那一个，保证每行都有东西。</summary>
        public static string ForSub(string sub, string cat)
        {
            switch (sub)
            {
                case "步枪": return "rifle";
                case "冲锋枪": return "smg";
                case "霰弹枪": return "shotgun";
                case "狙击枪": return "sniper";
                case "手枪": return "pistol";
                case "特殊武器": return "launcher";
                case "近战": return "melee";
                case "投掷物": return "bottle";
                case "弹药升级": return "ammo";
                case "其他枪械": return "gun";
                case "一代": return "survivor1";
                case "二代": return "survivor2";
                case "特感": return "claw";
                case "小僵尸": case "普通感染者": return "horde";
                case "非普通感染者": return "horde";
                case "八人包": return "person";
                case "治疗与复活": return "medkit";
                case "暂时增益": return "syringe";
                case "其他药品": return "pills";
                // 杂项下面这三个是新加的「整套替换」栏：界面 / 特效 / 音效。
                // 和老的 "特效杂项"（目录名带「杂项」的零散包）不是一回事，别混。
                case "UI 界面": return "ui";
                case "特效": return "fx";
                case "音效": return "sound";
                default: return ForCat(cat);
            }
        }

        /// <summary>
        /// 在 rect 里画一个图标。颜色通常给 Theme.Ink2。
        ///
        /// 两套画法：树里的大类 / 小类图标走「线型」（细描边、圆头圆角，颜色靠抗锯齿自己变淡），
        /// 顶栏那几个 ui- 开头的工具图标还是原来的实心剪影 —— 它们只有 15px、又贴在两条图标按钮中间，
        /// 线型在那个尺寸上会散。key 的前缀决定用哪套，调用方不用改。
        /// </summary>
        public static void Draw(Graphics g, string key, Rectangle rect, Color color)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            bool solid = key.StartsWith("ui-", StringComparison.Ordinal);
            using (GraphicsPath p = solid ? Build(key) : BuildLine(key))
            {
                if (p == null) return;
                GraphicsState st = g.Save();
                try
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TranslateTransform(rect.X, rect.Y);
                    g.ScaleTransform(rect.Width / (float)Grid, rect.Height / (float)Grid);
                    if (solid)
                    {
                        using (SolidBrush b = new SolidBrush(color)) g.FillPath(b, p);
                    }
                    else
                    {
                        // 线宽按 16 格网格给：18px 树里约 1.3px，32px 样张约 2.3px，跟着尺寸缩放不糊。
                        using (Pen pen = new Pen(color, LineUnit))
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            pen.LineJoin = LineJoin.Round;
                            // 「未归类」画虚线框：一眼就知道这行没归位，不只是个方框。
                            if (key == "unknown")
                            {
                                pen.DashStyle = DashStyle.Custom;
                                pen.DashPattern = new float[] { 1.7f, 1.5f };
                            }
                            g.DrawPath(pen, p);
                        }
                    }
                }
                finally { g.Restore(st); }
            }
        }

        // ---------------------------------------------------------------- 线型图标（树里用）
        //
        // 和下面的实心剪影是同一批 key，换成「细线 + 圆头圆角」的画法：
        // 16 格网格里一件图标只留 2~5 笔，线宽恒为 1.15 格（18px 下约 1.3px），
        // 靠形状 + 留白认类别，不靠面积。小件（弹匣、握把）宽了才画闭合框，
        // 窄的就一根线 —— 1px 的框在 18px 下只会糊成一坨灰。

        /// <summary>线宽（单位是 16 格网格；18px 下约 1.3px，32px 下约 2.3px）。</summary>
        private const float LineUnit = 1.25f;

        /// <summary>开折线：不闭合，两端是圆头。</summary>
        private static void Open(GraphicsPath p, params PointF[] pts) { p.AddLines(pts); }

        /// <summary>闭合轮廓。</summary>
        private static void Loop(GraphicsPath p, params PointF[] pts) { p.AddPolygon(pts); }

        /// <summary>圆圈。</summary>
        private static void Ring(GraphicsPath p, float cx, float cy, float r) { p.AddEllipse(cx - r, cy - r, r * 2f, r * 2f); }

        /// <summary>一段直线。</summary>
        private static void Seg(GraphicsPath p, float x1, float y1, float x2, float y2) { p.AddLine(x1, y1, x2, y2); }

        /// <summary>圆角矩形轮廓。</summary>
        private static void Box(GraphicsPath p, float x, float y, float w, float h, float r)
        {
            if (r <= 0f) { p.AddRectangle(new RectangleF(x, y, w, h)); return; }
            float d = r * 2f;
            p.AddArc(x, y, d, d, 180f, 90f);
            p.AddArc(x + w - d, y, d, d, 270f, 90f);
            p.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
            p.AddArc(x, y + h - d, d, d, 90f, 90f);
            p.CloseFigure();
        }

        private static GraphicsPath BuildLine(string key)
        {
            GraphicsPath p = new GraphicsPath();
            switch (key)
            {
                // ---- 大类
                case "gun":
                    GunLine(p);
                    return p;

                case "person":
                    PersonLine(p, 8.0f, 4.4f, 2.6f, 4.8f, 8.4f, 14.2f);
                    return p;

                // 普通感染者：一大一小两个人影叠着 = 一群，和「特感」的三道爪痕分开。
                case "horde":
                    PersonLine(p, 6.2f, 5.2f, 2.1f, 2.2f, 9.0f, 14.2f);
                    Ring(p, 12.0f, 6.8f, 1.5f);
                    Open(p, new PointF(9.8f, 14.2f), new PointF(10.6f, 10.4f), new PointF(13.4f, 10.4f), new PointF(14.2f, 14.2f));
                    return p;

                case "medkit":
                    Box(p, 1.8f, 5.8f, 12.4f, 8.4f, 1.2f);                       // 箱体
                    Open(p, new PointF(6.2f, 5.8f), new PointF(6.2f, 3.6f), new PointF(9.8f, 3.6f), new PointF(9.8f, 5.8f));  // 提手
                    Seg(p, 8.0f, 7.6f, 8.0f, 12.4f);                            // 十字
                    Seg(p, 5.6f, 10.0f, 10.4f, 10.0f);
                    return p;

                case "map":
                    Loop(p, new PointF(1.4f, 4.6f), new PointF(5.0f, 3.0f), new PointF(5.0f, 11.8f), new PointF(1.4f, 13.4f));
                    Loop(p, new PointF(5.6f, 3.0f), new PointF(10.4f, 4.6f), new PointF(10.4f, 13.4f), new PointF(5.6f, 11.8f));
                    Loop(p, new PointF(11.0f, 4.6f), new PointF(14.6f, 3.0f), new PointF(14.6f, 11.8f), new PointF(11.0f, 13.4f));
                    return p;

                case "spray":
                    Box(p, 3.6f, 6.2f, 8.4f, 8.2f, 1.4f);                       // 罐体
                    Box(p, 6.2f, 3.6f, 3.4f, 2.6f, 0.6f);                       // 罐盖
                    Seg(p, 5.4f, 9.4f, 10.2f, 9.4f);                            // 标签线
                    Ring(p, 12.4f, 2.6f, 0.8f);                                 // 雾点
                    Ring(p, 13.6f, 4.4f, 0.8f);
                    return p;

                case "branch":
                    Seg(p, 8.0f, 13.8f, 8.0f, 6.6f);                            // 主干
                    Seg(p, 8.0f, 8.8f, 4.4f, 5.6f);                             // 左枝
                    Seg(p, 8.0f, 7.8f, 11.6f, 4.8f);                            // 右枝
                    Ring(p, 3.2f, 4.4f, 1.5f);
                    Ring(p, 12.4f, 3.6f, 1.5f);
                    Ring(p, 8.0f, 2.6f, 1.5f);
                    Seg(p, 5.4f, 14.2f, 10.6f, 14.2f);                          // 底座
                    return p;

                case "unknown":
                    Box(p, 2.0f, 2.0f, 12.0f, 12.0f, 2.4f);                     // 虚线框（Draw 里给这个 key 换虚线）
                    Ring(p, 8.0f, 8.0f, 1.3f);
                    return p;

                // ---- 枪械小类
                // 枪族在小尺寸下只靠「长短 + 一个特征」区分：描边一多就糊成一团。
                case "rifle":
                    GunLine(p);
                    Box(p, 4.8f, 2.6f, 4.8f, 2.4f, 0.9f);                       // 瞄准镜
                    return p;

                case "smg":
                    Box(p, 2.6f, 6.2f, 7.4f, 3.0f, 1.0f);                       // 短机匣
                    Seg(p, 10.0f, 7.7f, 12.4f, 7.7f);                           // 短管
                    Seg(p, 5.4f, 9.2f, 4.8f, 13.4f);                            // 握把
                    return p;

                case "shotgun":
                    Seg(p, 1.6f, 6.2f, 14.2f, 6.2f);                            // 上管
                    Seg(p, 1.6f, 8.6f, 14.2f, 8.6f);                            // 下管
                    Seg(p, 1.6f, 6.2f, 1.6f, 8.6f);                             // 托尾
                    Box(p, 5.2f, 8.6f, 4.6f, 2.4f, 0.8f);                       // 泵动护木
                    return p;

                case "sniper":
                    Box(p, 2.6f, 6.4f, 11.8f, 2.6f, 1.0f);                      // 机匣 + 长管
                    Box(p, 5.0f, 2.8f, 5.0f, 2.2f, 0.9f);                       // 瞄准镜
                    Seg(p, 7.4f, 9.0f, 6.6f, 13.2f);                            // 握把
                    return p;

                case "pistol":
                    Box(p, 2.8f, 5.4f, 9.0f, 2.8f, 0.9f);                       // 套筒
                    Seg(p, 9.0f, 8.2f, 6.4f, 13.6f);                            // 握把
                    Seg(p, 11.8f, 6.8f, 14.2f, 6.8f);                           // 枪口
                    return p;

                case "launcher":
                    Box(p, 1.8f, 5.6f, 9.0f, 4.4f, 1.4f);                       // 粗管
                    Loop(p, new PointF(10.8f, 4.6f), new PointF(14.6f, 5.8f), new PointF(14.6f, 9.8f), new PointF(10.8f, 11.0f));  // 喇叭口
                    Seg(p, 5.4f, 10.0f, 4.8f, 13.4f);                           // 握把
                    return p;

                case "melee":
                    Seg(p, 9.4f, 3.0f, 6.2f, 13.6f);                            // 斜柄（穿出斧头一截，才不像旗子）
                    Loop(p, new PointF(8.8f, 2.2f), new PointF(13.8f, 3.4f),
                              new PointF(13.0f, 6.8f), new PointF(8.4f, 5.8f));  // 斧头：刃口外鼓
                    return p;

                case "bottle":
                    Loop(p, new PointF(4.8f, 8.2f), new PointF(11.2f, 8.2f), new PointF(10.4f, 14.4f), new PointF(5.6f, 14.4f));  // 瓶身
                    Seg(p, 6.9f, 8.2f, 6.9f, 5.0f);                             // 瓶颈
                    Seg(p, 9.1f, 8.2f, 9.1f, 5.0f);
                    Seg(p, 6.9f, 5.0f, 9.1f, 5.0f);
                    Loop(p, new PointF(8.0f, 0.8f), new PointF(9.6f, 4.0f), new PointF(6.4f, 4.0f));   // 火苗
                    return p;

                case "ammo":
                    Loop(p, new PointF(5.0f, 1.6f), new PointF(6.6f, 5.4f), new PointF(6.6f, 14.2f), new PointF(3.4f, 14.2f), new PointF(3.4f, 5.4f));
                    Loop(p, new PointF(11.0f, 1.6f), new PointF(12.6f, 5.4f), new PointF(12.6f, 14.2f), new PointF(9.4f, 14.2f), new PointF(9.4f, 5.4f));
                    return p;

                // ---- 人物小类
                case "survivor1":
                    PersonLine(p, 6.6f, 4.0f, 2.1f, 2.6f, 8.6f, 14.2f);
                    Ring(p, 12.6f, 8.0f, 1.3f);                                 // 一代：右侧一颗点
                    return p;

                case "survivor2":
                    PersonLine(p, 6.6f, 4.0f, 2.1f, 2.6f, 8.6f, 14.2f);
                    Ring(p, 12.6f, 5.6f, 1.3f);                                 // 二代：两颗点
                    Ring(p, 12.6f, 10.2f, 1.3f);
                    return p;

                case "claw":
                    Seg(p, 2.4f, 3.4f, 10.8f, 13.0f);                           // 三道爪痕
                    Seg(p, 6.6f, 2.0f, 14.6f, 9.8f);
                    Seg(p, 1.2f, 7.4f, 8.0f, 14.6f);
                    return p;

                // ---- 药品小类
                case "syringe":
                    Box(p, 4.6f, 5.2f, 6.2f, 3.4f, 0.8f);                       // 药筒
                    Seg(p, 2.6f, 5.2f, 4.6f, 5.2f);                             // 活塞
                    Seg(p, 2.6f, 8.6f, 4.6f, 8.6f);
                    Seg(p, 2.6f, 5.2f, 2.6f, 8.6f);
                    Seg(p, 1.2f, 6.9f, 2.6f, 6.9f);                             // 推杆
                    Seg(p, 10.8f, 6.9f, 14.8f, 6.9f);                           // 针
                    Seg(p, 6.6f, 5.2f, 6.6f, 8.6f);                             // 刻度
                    return p;

                case "pills":
                    // 横着的胶囊 + 中间一道斜分界。别画成「圆 + 斜线」—— 那是禁止符号。
                    p.AddEllipse(2.2f, 5.4f, 11.6f, 5.2f);
                    Seg(p, 5.9f, 5.9f, 10.1f, 10.1f);
                    return p;

                // ---- 杂项 / 整套替换三栏
                case "ui":
                    Box(p, 1.6f, 2.2f, 12.8f, 8.6f, 1.2f);                      // 显示器
                    Seg(p, 8.0f, 10.8f, 8.0f, 13.2f);                           // 支架
                    Seg(p, 4.8f, 13.6f, 11.2f, 13.6f);                          // 底座
                    return p;

                case "fx":
                    Loop(p, Star(5.4f, 5.8f, 4.4f, 1.5f));                      // 一大一小两颗火花
                    Loop(p, Star(12.0f, 11.8f, 3.0f, 1.0f));
                    return p;

                case "sound":
                    Loop(p, new PointF(1.4f, 6.2f), new PointF(4.4f, 6.2f), new PointF(7.6f, 3.0f), new PointF(7.6f, 13.0f), new PointF(4.4f, 9.8f), new PointF(1.4f, 9.8f));  // 喇叭
                    p.AddArc(7.6f - 4.3f, 8.0f - 4.3f, 8.6f, 8.6f, -50f, 100f);     // 两声波
                    p.AddArc(7.6f - 6.6f, 8.0f - 6.6f, 13.2f, 13.2f, -46f, 92f);
                    return p;

                case "misc":
                    Box(p, 1.8f, 6.6f, 12.4f, 7.6f, 1.2f);                      // 工具箱
                    Open(p, new PointF(5.8f, 6.6f), new PointF(5.8f, 3.6f), new PointF(10.2f, 3.6f), new PointF(10.2f, 6.6f));  // 提手
                    Seg(p, 8.0f, 10.6f, 8.0f, 12.2f);                           // 搭扣
                    return p;

                default:
                    p.Dispose();
                    return null;
            }
        }

        /// <summary>线型步枪：一根横的机匣+枪管 + 一根下垂的握把，只留两笔。</summary>
        private static void GunLine(GraphicsPath p)
        {
            Box(p, 1.8f, 5.8f, 11.8f, 3.0f, 1.0f);          // 机匣 + 枪管
            Seg(p, 6.2f, 8.8f, 5.4f, 13.2f);                // 握把
        }

        /// <summary>
        /// 线型半身人形：头 + 肩线。头半径 / 肩宽端点都给参数，共用给「人物 / 小僵尸 / 一代 / 二代」。
        /// </summary>
        private static void PersonLine(GraphicsPath p, float hx, float hy, float hr, float halfW, float sy, float bottom)
        {
            Ring(p, hx, hy, hr);
            Open(p, new PointF(hx - halfW, bottom), new PointF(hx - halfW + 1.6f, sy),
                     new PointF(hx + halfW - 1.6f, sy), new PointF(hx + halfW, bottom));
        }

        // ---------------------------------------------------------------- 形状

        /// <summary>
        /// 要镂空的地方（医疗包的十字、药瓶刻度、杂项的叠片）用 Alternate；
        /// 其余一律 Winding，部件之间重叠一点不会被误挖出洞。
        /// </summary>
        private static GraphicsPath New(FillMode mode)
        {
            GraphicsPath p = new GraphicsPath();
            p.FillMode = mode;
            return p;
        }

        private static GraphicsPath Build(string key)
        {
            GraphicsPath p = New(FillMode.Winding);
            switch (key)
            {
                // ---- 大类
                case "gun":
                    Gun(p);
                    return p;

                case "person":
                    Person(p, 1f);
                    return p;

                // 普通感染者：一大一小两个人影叠着 = 一群，跟「特感」的爪子区分开。
                // 17px 下只敢用两个实心块，三个头会糊成一团。
                case "horde":
                    Person(p, 0.66f);
                    p.AddEllipse(9.2f, 6.6f, 4.4f, 4.4f);
                    p.AddPolygon(new PointF[]
                    {
                        new PointF(7.8f, 15.0f), new PointF(15.2f, 15.0f),
                        new PointF(13.8f, 11.4f), new PointF(9.6f, 11.4f)
                    });
                    return p;

                case "medkit":
                    p.Dispose();
                    p = New(FillMode.Alternate);
                    Medkit(p);
                    return p;

                case "map":
                    p.AddPolygon(new PointF[] { new PointF(1.4f, 4.2f), new PointF(5.1f, 2.6f), new PointF(5.1f, 11.4f), new PointF(1.4f, 13.0f) });
                    p.AddPolygon(new PointF[] { new PointF(5.7f, 2.6f), new PointF(10.3f, 4.2f), new PointF(10.3f, 13.0f), new PointF(5.7f, 11.4f) });
                    p.AddPolygon(new PointF[] { new PointF(10.9f, 4.2f), new PointF(14.6f, 2.6f), new PointF(14.6f, 11.4f), new PointF(10.9f, 13.0f) });
                    return p;

                case "spray":
                    p.AddRectangle(new RectangleF(3.0f, 4.8f, 10.0f, 9.6f));
                    p.AddPolygon(new PointF[] { new PointF(3.0f, 4.8f), new PointF(13.0f, 4.8f), new PointF(11.6f, 2.6f), new PointF(4.4f, 2.6f) });
                    p.AddRectangle(new RectangleF(6.2f, 0.8f, 3.2f, 2.2f));
                    p.AddRectangle(new RectangleF(9.2f, 1.2f, 2.8f, 1.2f));
                    return p;

                case "branch":
                    p.AddRectangle(new RectangleF(7.1f, 6.2f, 1.8f, 8.0f));
                    p.AddPolygon(new PointF[] { new PointF(7.1f, 7.8f), new PointF(7.1f, 10.0f), new PointF(2.4f, 6.2f), new PointF(3.4f, 4.6f) });
                    p.AddPolygon(new PointF[] { new PointF(8.9f, 7.8f), new PointF(8.9f, 10.0f), new PointF(13.6f, 6.2f), new PointF(12.6f, 4.6f) });
                    p.AddEllipse(1.2f, 3.0f, 3.4f, 3.4f);
                    p.AddEllipse(11.4f, 3.0f, 3.4f, 3.4f);
                    p.AddEllipse(6.3f, 0.8f, 3.4f, 3.4f);
                    p.AddRectangle(new RectangleF(6.1f, 13.8f, 3.8f, 1.6f));
                    return p;

                case "unknown":
                    p.AddRectangle(new RectangleF(1.4f, 1.4f, 13.2f, 1.6f));
                    p.AddRectangle(new RectangleF(1.4f, 13.0f, 13.2f, 1.6f));
                    p.AddRectangle(new RectangleF(1.4f, 1.4f, 1.6f, 13.2f));
                    p.AddRectangle(new RectangleF(13.0f, 1.4f, 1.6f, 13.2f));
                    p.AddRectangle(new RectangleF(6.9f, 6.9f, 2.2f, 2.2f));
                    return p;

                // ---- 顶栏工具图标（2026-11-24 设计稿 v1）：和分类图标同一套实心剪影
                case "ui-bolt":          // 启动项
                    p.AddPolygon(new PointF[] {
                        new PointF(9.8f, 0.8f), new PointF(3.6f, 9.4f), new PointF(7.5f, 9.4f),
                        new PointF(6.4f, 15.2f), new PointF(12.6f, 6.6f), new PointF(8.7f, 6.6f) });
                    return p;

                case "ui-shop":          // 工坊：下箭头 + 托盘
                    p.AddPolygon(new PointF[] {
                        new PointF(6.5f, 1.6f), new PointF(9.5f, 1.6f), new PointF(9.5f, 6.8f),
                        new PointF(12.2f, 6.8f), new PointF(8.0f, 11.4f), new PointF(3.8f, 6.8f),
                        new PointF(6.5f, 6.8f) });
                    p.AddRectangle(new RectangleF(2.2f, 12.4f, 11.6f, 2.0f));
                    return p;

                case "ui-grid":          // 总览：四宫格
                    p.AddRectangle(new RectangleF(2.0f, 2.0f, 5.3f, 5.3f));
                    p.AddRectangle(new RectangleF(8.7f, 2.0f, 5.3f, 5.3f));
                    p.AddRectangle(new RectangleF(2.0f, 8.7f, 5.3f, 5.3f));
                    p.AddRectangle(new RectangleF(8.7f, 8.7f, 5.3f, 5.3f));
                    return p;

                case "ui-tune":          // 设置：两条滑轨 + 两个把手（三条会读成"菜单"，这里只要两粗一细）
                    p.AddRectangle(new RectangleF(2.2f, 4.4f, 11.6f, 2.0f));
                    p.AddRectangle(new RectangleF(2.2f, 9.6f, 11.6f, 2.0f));
                    p.AddRectangle(new RectangleF(3.6f, 2.8f, 3.4f, 5.2f));
                    p.AddRectangle(new RectangleF(9.0f, 8.0f, 3.4f, 5.2f));
                    return p;

                case "ui-refresh":       // 刷新：圆环 + 缺口箭头（Alternate 挖空）
                    p.Dispose();
                    p = New(FillMode.Alternate);
                    p.AddEllipse(2.6f, 2.6f, 10.8f, 10.8f);
                    p.AddEllipse(5.0f, 5.0f, 6.0f, 6.0f);
                    p.AddPolygon(new PointF[] {
                        new PointF(10.2f, 0.6f), new PointF(14.4f, 4.8f), new PointF(10.2f, 4.8f) });
                    return p;

                case "misc":
                    // 工具箱：箱体 + 提手（提手用 Alternate 挖空）+ 中间搭扣。
                    // 原来画的是两个叠着的方块，17px 下看不出是什么，用户也点出来「没含义」。
                    p.Dispose();
                    p = New(FillMode.Alternate);
                    p.AddRectangle(new RectangleF(1.6f, 6.2f, 12.8f, 8.0f));   // 箱体
                    p.AddRectangle(new RectangleF(5.0f, 2.2f, 6.0f, 4.2f));    // 提手外框
                    p.AddRectangle(new RectangleF(6.6f, 3.4f, 2.8f, 3.0f));    // 提手内空
                    p.AddRectangle(new RectangleF(6.8f, 8.2f, 2.4f, 2.2f));    // 搭扣（挖空）
                    return p;

                // ---- 枪械小类
                case "rifle":
                    Gun(p);
                    p.AddRectangle(new RectangleF(4.0f, 2.6f, 5.8f, 2.1f));
                    p.AddRectangle(new RectangleF(5.2f, 4.6f, 1.3f, 1.6f));
                    p.AddRectangle(new RectangleF(7.4f, 4.6f, 1.3f, 1.6f));
                    return p;

                case "smg":
                    p.AddRectangle(new RectangleF(2.4f, 6.0f, 7.8f, 3.2f));
                    p.AddRectangle(new RectangleF(10.0f, 6.9f, 4.0f, 1.5f));
                    p.AddRectangle(new RectangleF(4.0f, 8.9f, 2.8f, 5.4f));
                    p.AddPolygon(new PointF[] { new PointF(7.8f, 8.9f), new PointF(9.8f, 8.9f), new PointF(9.0f, 12.6f), new PointF(7.6f, 12.6f) });
                    p.AddRectangle(new RectangleF(0.9f, 6.6f, 1.8f, 2.6f));
                    return p;

                case "shotgun":
                    p.AddRectangle(new RectangleF(0.8f, 5.4f, 14.4f, 2.8f));
                    p.AddRectangle(new RectangleF(5.4f, 8.2f, 5.8f, 3.2f));
                    p.AddRectangle(new RectangleF(1.4f, 8.2f, 4.2f, 2.4f));
                    p.AddPolygon(new PointF[] { new PointF(0.8f, 5.4f), new PointF(0.0f, 11.2f), new PointF(2.4f, 11.2f), new PointF(3.2f, 5.4f) });
                    return p;

                case "sniper":
                    p.AddRectangle(new RectangleF(0.5f, 6.9f, 15.0f, 1.5f));
                    p.AddRectangle(new RectangleF(4.6f, 2.2f, 6.8f, 2.4f));
                    p.AddRectangle(new RectangleF(5.9f, 4.5f, 1.3f, 1.7f));
                    p.AddRectangle(new RectangleF(9.5f, 4.5f, 1.3f, 1.7f));
                    p.AddPolygon(new PointF[] { new PointF(0.5f, 6.9f), new PointF(0.0f, 11.2f), new PointF(2.2f, 11.2f), new PointF(2.7f, 6.9f) });
                    p.AddPolygon(new PointF[] { new PointF(7.3f, 8.2f), new PointF(9.2f, 8.2f), new PointF(8.7f, 12.2f), new PointF(7.1f, 12.2f) });
                    return p;

                case "pistol":
                    p.AddRectangle(new RectangleF(2.8f, 4.6f, 9.4f, 3.0f));
                    p.AddRectangle(new RectangleF(12.0f, 5.6f, 2.9f, 1.5f));
                    p.AddPolygon(new PointF[] { new PointF(4.2f, 7.4f), new PointF(8.2f, 7.4f), new PointF(7.5f, 14.2f), new PointF(4.6f, 14.2f) });
                    return p;

                case "launcher":
                    p.AddRectangle(new RectangleF(1.4f, 6.0f, 10.0f, 4.2f));
                    p.AddPolygon(new PointF[] { new PointF(11.2f, 5.2f), new PointF(14.8f, 6.6f), new PointF(14.8f, 9.6f), new PointF(11.2f, 11.0f) });
                    p.AddPolygon(new PointF[] { new PointF(5.6f, 10.0f), new PointF(8.2f, 10.0f), new PointF(7.8f, 13.8f), new PointF(5.8f, 13.8f) });
                    p.AddRectangle(new RectangleF(0.2f, 6.4f, 1.6f, 3.4f));
                    return p;

                case "melee":
                    p.AddRectangle(new RectangleF(6.6f, 4.6f, 1.9f, 10.6f));
                    p.AddPolygon(new PointF[] { new PointF(8.5f, 5.0f), new PointF(14.7f, 4.0f), new PointF(14.7f, 8.4f), new PointF(8.5f, 9.2f) });
                    return p;

                case "bottle":
                    p.AddPolygon(new PointF[] { new PointF(4.8f, 8.0f), new PointF(11.2f, 8.0f), new PointF(10.4f, 14.4f), new PointF(5.6f, 14.4f) });
                    p.AddRectangle(new RectangleF(6.9f, 4.8f, 2.2f, 3.4f));
                    p.AddPolygon(new PointF[] { new PointF(8.0f, 0.6f), new PointF(10.2f, 4.4f), new PointF(5.8f, 4.4f) });
                    return p;

                case "ammo":
                    // 两发竖着的子弹：尖头 + 直筒。和横放的手枪剪影不会混。
                    p.AddPolygon(Bullet(3.6f));
                    p.AddPolygon(Bullet(9.0f));
                    return p;

                // ---- 人物小类
                case "survivor1":
                    Person(p, 0.8f);
                    p.AddRectangle(new RectangleF(10.6f, 6.2f, 2.4f, 2.4f));
                    return p;

                case "survivor2":
                    Person(p, 0.8f);
                    p.AddRectangle(new RectangleF(10.6f, 5.0f, 2.4f, 2.4f));
                    p.AddRectangle(new RectangleF(10.6f, 9.2f, 2.4f, 2.4f));
                    return p;

                case "claw":
                    p.AddPolygon(new PointF[] { new PointF(1.8f, 3.6f), new PointF(4.0f, 2.2f), new PointF(12.8f, 12.2f), new PointF(10.6f, 13.6f) });
                    p.AddPolygon(new PointF[] { new PointF(6.2f, 2.0f), new PointF(8.4f, 1.2f), new PointF(15.6f, 10.4f), new PointF(13.4f, 11.2f) });
                    p.AddPolygon(new PointF[] { new PointF(0.2f, 7.2f), new PointF(2.0f, 5.8f), new PointF(9.0f, 14.4f), new PointF(7.2f, 15.2f) });
                    return p;

                // ---- 药品小类
                case "syringe":
                    p.AddRectangle(new RectangleF(4.2f, 5.0f, 7.2f, 3.4f));
                    p.AddRectangle(new RectangleF(2.4f, 5.6f, 2.2f, 2.2f));
                    p.AddRectangle(new RectangleF(0.6f, 4.6f, 1.9f, 1.0f));
                    p.AddRectangle(new RectangleF(0.6f, 7.8f, 1.9f, 1.0f));
                    p.AddRectangle(new RectangleF(1.9f, 4.6f, 1.0f, 4.2f));
                    p.AddRectangle(new RectangleF(11.2f, 6.0f, 3.6f, 1.4f));
                    p.AddPolygon(new PointF[] { new PointF(14.6f, 5.7f), new PointF(15.8f, 6.7f), new PointF(14.6f, 7.7f) });
                    return p;

                case "pills":
                    p.Dispose();
                    p = New(FillMode.Alternate);
                    p.AddPolygon(new PointF[] { new PointF(4.2f, 6.2f), new PointF(11.8f, 6.2f), new PointF(10.8f, 14.4f), new PointF(5.2f, 14.4f) });
                    p.AddRectangle(new RectangleF(5.4f, 3.0f, 5.2f, 3.0f));
                    p.AddRectangle(new RectangleF(6.2f, 8.2f, 3.6f, 1.2f));
                    p.AddRectangle(new RectangleF(6.4f, 10.8f, 3.2f, 1.2f));
                    return p;

                // ---- 杂项 / 整套替换三栏
                case "ui":
                    // 显示器：外壳挖空出屏幕，下面一截支架 + 底座。
                    // 挖空用 Alternate，和医疗包/药瓶一个路子。
                    p.Dispose();
                    p = New(FillMode.Alternate);
                    p.AddRectangle(new RectangleF(1.2f, 2.0f, 13.6f, 9.0f));
                    p.AddRectangle(new RectangleF(2.9f, 3.5f, 10.2f, 6.0f));
                    p.AddRectangle(new RectangleF(6.8f, 11.0f, 2.4f, 2.4f));
                    p.AddRectangle(new RectangleF(4.0f, 13.4f, 8.0f, 1.8f));
                    return p;

                case "fx":
                    // 一大一小两颗四角星 = 火花。星形在 17px 下比爆炸云更清楚：
                    // 云的轮廓全是圆弧，缩小后就是一坨圆点。
                    p.AddPolygon(Star(5.2f, 5.6f, 4.6f, 1.5f));
                    p.AddPolygon(Star(11.8f, 11.6f, 2.9f, 0.95f));
                    return p;

                case "sound":
                    // 喇叭 + 两道向右张开的斜条（拼成一个 ">"）当声波。
                    // 试过用弧线，1.5px 宽的弧在 17px 下会被抗锯齿磨成灰雾，斜条反而干净。
                    p.AddRectangle(new RectangleF(1.2f, 6.0f, 2.8f, 4.0f));
                    p.AddPolygon(new PointF[]
                    {
                        new PointF(4.0f, 6.4f), new PointF(7.8f, 3.0f),
                        new PointF(7.8f, 13.0f), new PointF(4.0f, 9.6f)
                    });
                    p.AddPolygon(new PointF[]
                    {
                        new PointF(9.0f, 5.0f), new PointF(10.3f, 4.0f),
                        new PointF(13.4f, 8.0f), new PointF(12.1f, 9.0f)
                    });
                    p.AddPolygon(new PointF[]
                    {
                        new PointF(9.0f, 11.0f), new PointF(12.1f, 7.0f),
                        new PointF(13.4f, 8.0f), new PointF(10.3f, 12.0f)
                    });
                    return p;

                default:
                    p.Dispose();
                    return null;
            }
        }

        /// <summary>共用件：横放的步枪剪影（托 + 机匣 + 管 + 下垂的弹匣与握把）。</summary>
        private static void Gun(GraphicsPath p)
        {
            p.AddPolygon(new PointF[] { new PointF(1.2f, 5.8f), new PointF(0.2f, 10.0f), new PointF(2.2f, 10.0f), new PointF(3.0f, 5.8f) });
            p.AddRectangle(new RectangleF(1.2f, 5.8f, 9.8f, 3.6f));
            p.AddRectangle(new RectangleF(10.8f, 6.7f, 4.4f, 1.6f));
            p.AddPolygon(new PointF[] { new PointF(4.4f, 9.2f), new PointF(7.4f, 9.2f), new PointF(6.9f, 13.8f), new PointF(4.8f, 13.8f) });
            p.AddPolygon(new PointF[] { new PointF(8.8f, 9.2f), new PointF(10.6f, 9.2f), new PointF(10.0f, 13.0f), new PointF(8.6f, 13.0f) });
        }

        /// <summary>共用件：四角星（外半径 / 内半径），火花用。第一个尖朝上。</summary>
        private static PointF[] Star(float cx, float cy, float outer, float inner)
        {
            PointF[] pts = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 4;
                float r = (i % 2 == 0) ? outer : inner;
                pts[i] = new PointF(cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r));
            }
            return pts;
        }

        /// <summary>共用件：一发竖着的子弹（尖头 + 直筒）。x 是左边缘，宽 2.8。</summary>
        private static PointF[] Bullet(float x)
        {
            return new PointF[]
            {
                new PointF(x + 1.4f, 1.2f),
                new PointF(x + 2.8f, 5.2f),
                new PointF(x + 2.8f, 14.4f),
                new PointF(x, 14.4f),
                new PointF(x, 5.2f)
            };
        }

        /// <summary>共用件：半身人形。scale 给「一代/二代」缩小用，右边腾地方放竖条。</summary>
        private static void Person(GraphicsPath p, float scale)
        {
            p.AddEllipse(5.4f * scale, 1.4f * scale, 5.2f * scale, 5.2f * scale);
            p.AddRectangle(new RectangleF(7.0f * scale, 6.0f * scale, 2.0f * scale, 2.4f * scale));
            p.AddPolygon(new PointF[]
            {
                new PointF(2.4f * scale, 14.4f * scale),
                new PointF(13.6f * scale, 14.4f * scale),
                new PointF(11.6f * scale, 7.4f * scale),
                new PointF(4.4f * scale, 7.4f * scale)
            });
        }

        /// <summary>医疗包：箱体挖出十字，提手是个环。必须 Alternate 填充。</summary>
        private static void Medkit(GraphicsPath p)
        {
            p.AddRectangle(new RectangleF(1.2f, 5.4f, 13.6f, 8.8f));
            p.AddRectangle(new RectangleF(5.8f, 2.8f, 4.4f, 2.8f));
            p.AddRectangle(new RectangleF(6.8f, 3.7f, 2.4f, 1.2f));
            p.AddRectangle(new RectangleF(6.9f, 7.2f, 2.2f, 5.4f));
            p.AddRectangle(new RectangleF(5.3f, 8.5f, 5.4f, 2.2f));
        }
    }
}
