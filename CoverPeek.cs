using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 候选区上方的封面预览条：左边 16:9 缩略图，右边 MOD 名 + 改动/体积/来源。
    /// 自绘而不是拼 PictureBox + 三个 Label —— 一个控件一次 SetBounds，排版跟着宽度走，
    /// 换主题也只是重画，不用同步一堆子控件的颜色。
    /// </summary>
    internal sealed class CoverPeek : Control
    {
        private Image img;
        private string title = "";
        private string meta = "";
        private string src = "";
        private string hint = "";
        private string info = "";        // 竖版才用：附加几行（改了什么 / 游戏里叫什么 / 开关状态）
        private bool owned;                 // img 是不是自己 new 出来的（要自己释放）

        public CoverPeek()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.SurfaceAlt;
        }

        /// <summary>显示某个 MOD 的封面。imgPath 为 null 就画占位。</summary>
        public void Show(string name, string metaText, string srcText, string imgPath, string hintText, string infoText)
        {
            Image loaded = null;
            try
            {
                if (!string.IsNullOrEmpty(imgPath) && System.IO.File.Exists(imgPath))
                {
                    // 从文件流读一份自己的副本：缓存文件随时可能被清理，别把句柄留在磁盘上
                    byte[] raw = System.IO.File.ReadAllBytes(imgPath);
                    using (System.IO.MemoryStream ms = new System.IO.MemoryStream(raw))
                        loaded = Image.FromStream(ms);
                }
            }
            catch { loaded = null; }

            Detach();
            img = loaded;
            owned = loaded != null;
            title = name ?? "";
            meta = metaText ?? "";
            src = srcText ?? "";
            hint = hintText ?? "";
            info = infoText ?? "";
            Invalidate();
        }

        /// <summary>
        /// 同一套排版，但图已经在内存里（工坊推荐区用：封面是网上下回来裁好的，没有磁盘路径）。
        /// 图归调用方所有（ImageList 还要继续用），这里不释放 —— owned=false。
        /// </summary>
        public void ShowImage(string name, string metaText, string srcText, Image image, string hintText, string infoText)
        {
            Detach();
            img = image;
            owned = false;
            title = name ?? "";
            meta = metaText ?? "";
            src = srcText ?? "";
            hint = hintText ?? "";
            info = infoText ?? "";
            Invalidate();
        }

        /// <summary>没有选中行时用：只给一句提示。</summary>
        public void Clear(string hintText)
        {
            Detach();
            title = "";
            meta = "";
            src = "";
            hint = hintText ?? "";
            info = "";
            Invalidate();
        }

        private void Detach()
        {
            if (owned && img != null) { try { img.Dispose(); } catch { } }
            img = null;
            owned = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Detach();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 竖着用（右侧详情栏）：图在上、字在下；横着用（候选区顶上那条）：图在左、字在右
            if (Height > Width * 8 / 10) { PaintTall(g); return; }

            Rectangle full = new Rectangle(0, 0, Width, Height);
            using (GraphicsPath bg = Round(full, Dpi.S(8)))
            {
                using (SolidBrush b = new SolidBrush(Theme.SurfaceAlt)) g.FillPath(b, bg);
                using (Pen p = new Pen(Theme.LineSoft)) g.DrawPath(p, bg);
            }

            int pad = Dpi.S(8);
            int th = Math.Max(Dpi.S(48), Height - pad * 2);
            int tw = th * 16 / 9;
            if (tw > Width / 2 - pad) { tw = Math.Max(Dpi.S(60), Width / 2 - pad); th = tw * 9 / 16; }
            Rectangle thumb = new Rectangle(pad, (Height - th) / 2, tw, th);

            using (GraphicsPath clip = Round(thumb, Dpi.S(6)))
            {
                GraphicsState st = g.Save();
                g.SetClip(clip);
                if (img != null)
                {
                    // cover：等比铺满，多出来的裁掉
                    float s = Math.Max((float)thumb.Width / img.Width, (float)thumb.Height / img.Height);
                    int dw = (int)Math.Ceiling(img.Width * s), dh = (int)Math.Ceiling(img.Height * s);
                    g.DrawImage(img, new Rectangle(thumb.X + (thumb.Width - dw) / 2,
                                                   thumb.Y + (thumb.Height - dh) / 2, dw, dh));
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Theme.IsDark
                               ? Color.FromArgb(46, 50, 58) : Color.FromArgb(232, 236, 242)))
                        g.FillRectangle(b, thumb);
                    string ch = "";
                    if (title.Length > 0 && hint.Length == 0) ch = title.Substring(0, 1);
                    if (ch.Length > 0)
                    {
                        using (Font f = new Font(Theme.UIBold.FontFamily, Math.Max(11f, th / 4f), FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(Theme.Ink3))
                        {
                            SizeF sz = g.MeasureString(ch, f);
                            g.DrawString(ch, f, b, thumb.X + (thumb.Width - sz.Width) / 2,
                                         thumb.Y + (thumb.Height - sz.Height) / 2);
                        }
                    }
                    string tag = hint.Length > 0 ? hint : "包内无图";
                    using (Font f = new Font(Theme.UISmall.FontFamily, 7.5f))
                    using (SolidBrush b = new SolidBrush(Theme.Ink3))
                    {
                        SizeF sz = g.MeasureString(tag, f);
                        g.DrawString(tag, f, b, thumb.X + (thumb.Width - sz.Width) / 2,
                                     thumb.Y + thumb.Height - sz.Height - Dpi.S(3));
                    }
                }
                g.Restore(st);
            }
            using (Pen p = new Pen(Theme.LineSoft)) { using (GraphicsPath cp = Round(thumb, Dpi.S(6))) g.DrawPath(p, cp); }

            // 右侧文字
            int tx = thumb.Right + Dpi.S(10);
            int tw2 = Width - tx - pad;
            if (tw2 < Dpi.S(40)) return;

            if (title.Length == 0)
            {
                TextRenderer.DrawText(g, hint, Theme.UI, new Rectangle(tx, 0, tw2, Height),
                    Theme.Ink3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }

            int line1 = Dpi.S(20), line2 = Dpi.S(18), line3 = Dpi.S(18);
            int top = (Height - (line1 + line2 + line3)) / 2;
            TextRenderer.DrawText(g, title, Theme.UIBold, new Rectangle(tx, top, tw2, line1),
                Theme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, meta, Theme.UISmall, new Rectangle(tx, top + line1, tw2, line2),
                Theme.Ink2, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // 来源徽标：有图才说得清这张图哪来的，没图就明说没有
            if (src.Length > 0)
            {
                bool has = img != null;
                Color ink = has ? Theme.Info : Theme.Ink3;
                Color bgb = has ? (Theme.IsDark ? Color.FromArgb(27, 42, 58) : Color.FromArgb(227, 240, 250))
                                : (Theme.IsDark ? Color.FromArgb(42, 46, 53) : Color.FromArgb(242, 243, 246));
                Font f = Theme.UISmall;
                Size sz = TextRenderer.MeasureText(g, src, f);
                Rectangle badge = new Rectangle(tx, top + line1 + line2 + Dpi.S(1),
                                                Math.Min(tw2, sz.Width + Dpi.S(6)), Dpi.S(16));
                using (GraphicsPath bp = Round(badge, Dpi.S(3)))
                {
                    using (SolidBrush b = new SolidBrush(bgb)) g.FillPath(b, bp);
                }
                TextRenderer.DrawText(g, src, f, badge, ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        /// <summary>竖版：16:9 大图在上，标题 / 类型·体积 / 来源 / 提示依次排在下面。</summary>
        private void PaintTall(Graphics g)
        {
            int pad = Dpi.S(10);
            Rectangle full = new Rectangle(0, 0, Width, Height);
            using (GraphicsPath bg = Round(full, Dpi.S(8)))
            {
                using (SolidBrush b = new SolidBrush(Theme.SurfaceAlt)) g.FillPath(b, bg);
                using (Pen p = new Pen(Theme.LineSoft)) g.DrawPath(p, bg);
            }

            int iw = Math.Max(Dpi.S(40), Width);
            int ih = iw * 9 / 16;
            // 图跟详情栏齐宽（2026-11-24 用户）：以前左右各留 10px、高度再卡 212，
            // 于是「栏比图宽、图比栏矮」，两边都留白。现在栏多宽图就多宽，
            // 只有栏特别矮的时候才按高度收窄（那会儿宽度优先会让正文没地方）。
            int maxH = Math.Min(Dpi.S(300), Math.Max(Dpi.S(60), Height * 45 / 100));
            if (ih > maxH) { ih = maxH; iw = ih * 16 / 9; }
            Rectangle thumb = new Rectangle((Width - iw) / 2, 0, iw, ih);

            using (GraphicsPath clip = Round(thumb, Dpi.S(6)))
            {
                GraphicsState st = g.Save();
                g.SetClip(clip);
                if (img != null)
                {
                    float s = Math.Max((float)thumb.Width / img.Width, (float)thumb.Height / img.Height);
                    int dw = (int)Math.Ceiling(img.Width * s), dh = (int)Math.Ceiling(img.Height * s);
                    g.DrawImage(img, new Rectangle(thumb.X + (thumb.Width - dw) / 2,
                                                   thumb.Y + (thumb.Height - dh) / 2, dw, dh));
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Theme.IsDark
                               ? Color.FromArgb(46, 50, 58) : Color.FromArgb(232, 236, 242)))
                        g.FillRectangle(b, thumb);
                    string tag = hint.Length > 0 ? hint : "包内无图";
                    using (Font f = new Font(Theme.UISmall.FontFamily, 8.5f))
                    using (SolidBrush b = new SolidBrush(Theme.Ink3))
                    {
                        SizeF sz = g.MeasureString(tag, f);
                        g.DrawString(tag, f, b, thumb.X + (thumb.Width - sz.Width) / 2,
                                     thumb.Y + (thumb.Height - sz.Height) / 2);
                    }
                }
                g.Restore(st);
            }
            using (Pen p = new Pen(Theme.LineSoft)) { using (GraphicsPath cp = Round(thumb, Dpi.S(6))) g.DrawPath(p, cp); }

            // 文字块也跟图对齐（以前文字内缩 10px、图内缩 10px，现在两个都贴栏边）
            int tx = 0;
            int tw = Width;
            int y = thumb.Bottom + Dpi.S(12);
            if (title.Length == 0)
            {
                TextRenderer.DrawText(g, hint, Theme.UI, new Rectangle(tx, y, tw, Height - y - pad),
                    Theme.Ink3, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }
            using (Font tf = new Font(Theme.UIBold.FontFamily, 11f, FontStyle.Bold))
                TextRenderer.DrawText(g, title, tf, new Rectangle(tx, y, tw, Dpi.S(44)),
                    Theme.Ink, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            y += Dpi.S(46);
            TextRenderer.DrawText(g, meta, Theme.UISmall, new Rectangle(tx, y, tw, Dpi.S(20)),
                Theme.Ink2, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y += Dpi.S(22);
            if (src.Length > 0)
            {
                bool has = img != null;
                Color ink = has ? Theme.Info : Theme.Ink3;
                Color bgb = has ? (Theme.IsDark ? Color.FromArgb(27, 42, 58) : Color.FromArgb(227, 240, 250))
                                : (Theme.IsDark ? Color.FromArgb(42, 46, 53) : Color.FromArgb(242, 243, 246));
                Font f = Theme.UISmall;
                Size sz = TextRenderer.MeasureText(g, src, f);
                Rectangle badge = new Rectangle(tx, y, Math.Min(tw, sz.Width + Dpi.S(6)), Dpi.S(18));
                using (GraphicsPath bp = Round(badge, Dpi.S(3)))
                {
                    using (SolidBrush b = new SolidBrush(bgb)) g.FillPath(b, bp);
                }
                TextRenderer.DrawText(g, src, f, badge, ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                y = badge.Bottom + Dpi.S(10);
            }
            if (info.Length > 0)
            {
                TextRenderer.DrawText(g, info, Theme.UISmall, new Rectangle(tx, y, tw, Height - y - pad),
                    Theme.Ink3, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }

        private static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
