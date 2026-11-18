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
        private bool owned;                 // img 是不是自己 new 出来的（要自己释放）

        public CoverPeek()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.SurfaceAlt;
        }

        /// <summary>显示某个 MOD 的封面。imgPath 为 null 就画占位。</summary>
        public void Show(string name, string metaText, string srcText, string imgPath, string hintText)
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
