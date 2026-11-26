using System;
using System.Drawing;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 方案总览的独立窗口：以前是盖在主界面上的整块看板，用户反馈「打开就糊在原窗口上」。
    /// 这里只换一个容器，画布还是同一个 OverviewCanvas（数据、交互都不变）。
    /// </summary>
    internal sealed class OverviewForm : Form
    {
        private readonly OverviewCanvas canvas;

        public OverviewForm(MainForm owner)
        {
            Text = Lang.S("方案总览");
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(1120, 720);
            MinimumSize = Dpi.Sz(880, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            Font = Theme.UI;
            ShowInTaskbar = true;
            KeyPreview = true;
            Theme.DressForm(this);
            Icon = Theme.WindowIcon("grid");   // 任务栏单独一个图标（四宫格），跟工坊/下载区分开

            canvas = new OverviewCanvas(owner);
            canvas.Dock = DockStyle.Fill;
            Controls.Add(canvas);
            Shown += delegate { try { canvas.Rebuild(); } catch (Exception ex) { Core.Log("WARN", "总览窗口重建失败: " + ex.Message); } };
        }

        internal void RefreshPicks()
        {
            try { canvas.RefreshPicks(); } catch { }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        internal void TestEscape()
        {
            OnKeyDown(new KeyEventArgs(Keys.Escape));
        }
    }
}