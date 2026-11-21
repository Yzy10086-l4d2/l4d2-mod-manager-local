using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 「来源」总开关：Steam（自带，要 key）+ 配置文件里那些外加来源，一列勾选。
    /// 顺序就是优先级：推荐区从上往下试，第一个有结果的生效。
    /// </summary>
    internal sealed class SourcesForm : Form
    {
        private readonly CheckBox chkSteam = new CheckBox();
        private readonly TextBox txtKey = new TextBox();
        private readonly CheckedListBox list = new CheckedListBox();
        private readonly Label lblPath = new Label();

        public SourcesForm()
        {
            Text = Lang.S("MOD 来源");
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(500, 452);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            Label title = new Label();
            title.Text = Lang.S("启用哪些来源");
            title.SetBounds(18, 12, 300, 22);
            title.Font = Theme.RowTitle;
            title.ForeColor = Theme.Ink;
            title.BackColor = Color.Transparent;
            Controls.Add(title);

            Label hint = new Label();
            hint.Text = Lang.S("推荐区按这里的顺序挨个试，第一个有结果的生效。");
            hint.SetBounds(18, 34, 440, 16);
            hint.Font = Theme.UISmall;
            hint.ForeColor = Theme.Ink3;
            hint.BackColor = Color.Transparent;
            Controls.Add(hint);

            chkSteam.Text = Lang.S("Steam 创意工坊（官方，唯一能搜全站 + 一键装的来源）");
            chkSteam.SetBounds(18, 58, 450, 24);
            chkSteam.Font = Theme.UI;
            chkSteam.Checked = Core.Cfg.SteamEnabled;
            Controls.Add(chkSteam);

            Label keyNote = new Label();
            keyNote.Text = Lang.S("Steam API key（填了走 api.steampowered.com；留空就抓网页）");
            keyNote.SetBounds(18, 86, 440, 16);
            keyNote.Font = Theme.UISmall;
            keyNote.ForeColor = Theme.Ink3;
            keyNote.BackColor = Color.Transparent;
            Controls.Add(keyNote);

            txtKey.SetBounds(18, 106, 344, 26);
            txtKey.BorderStyle = BorderStyle.FixedSingle;
            txtKey.Font = Theme.UI;
            txtKey.Text = Core.Cfg.WsApiKey ?? "";
            Theme.Dress(txtKey);
            Controls.Add(txtKey);

            FlatButton help = new FlatButton();
            help.Text = Lang.S("怎么拿 key");
            help.Soft = true;
            help.SetBounds(370, 106, 112, 26);
            help.Click += delegate { ShowKeyHelp(); };
            Controls.Add(help);

            Label other = new Label();
            other.Text = Lang.S("其它来源（配置文件里加的）");
            other.SetBounds(18, 148, 440, 16);
            other.Font = Theme.UISmall;
            other.ForeColor = Theme.Ink3;
            other.BackColor = Color.Transparent;
            Controls.Add(other);

            list.SetBounds(18, 168, 464, 176);
            list.Font = Theme.UI;
            list.CheckOnClick = true;
            Theme.Dress(list);
            Controls.Add(list);

            lblPath.SetBounds(18, 352, 464, 16);
            lblPath.Font = Theme.UISmall;
            lblPath.ForeColor = Theme.Ink3;
            lblPath.AutoEllipsis = true;
            Controls.Add(lblPath);

            FlatButton open = new FlatButton();
            open.Text = Lang.S("打开来源文件");
            open.Soft = true;
            open.SetBounds(18, 380, 116, 30);
            open.Click += delegate { OpenFile(); };
            Controls.Add(open);

            FlatButton reload = new FlatButton();
            reload.Text = Lang.S("重新读取");
            reload.Soft = true;
            reload.SetBounds(142, 380, 92, 30);
            reload.Click += delegate { Fill(); };
            Controls.Add(reload);

            FlatButton ok = new FlatButton();
            ok.Text = Lang.S("保存");
            ok.Primary = true;
            ok.SetBounds(396, 380, 86, 30);
            ok.Click += delegate { Save(); };
            Controls.Add(ok);

            FlatButton cancel = new FlatButton();
            cancel.Text = Lang.S("取消");
            cancel.Plain = true;
            cancel.SetBounds(308, 380, 80, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            Fill();
        }

        private void Fill()
        {
            Sources.Load(true);
            list.Items.Clear();
            lock (Sources.All)
            {
                foreach (SourceDef d in Sources.All)
                    list.Items.Add(new Row(d), d.Enabled);
            }
            lblPath.Text = Sources.FilePath;
            chkSteam.Checked = Core.Cfg.SteamEnabled;
            txtKey.Text = Core.Cfg.WsApiKey ?? "";
        }

        private void ShowKeyHelp()
        {
            MessageBox.Show(this,
                Lang.S("key 在 Steam 官网页上拿，拿到后粘到左边输入框，点「保存」。") + "\r\n\r\n" +
                Lang.S("1. 打开 steamcommunity.com/dev/apikey（网页打不开时，用 Steam 客户端内置浏览器，或手机流量）") + "\r\n" +
                Lang.S("2. 登录 Steam，域名随便填（例如 l4d2local）") + "\r\n" +
                Lang.S("3. 复制那串 32 位字符，粘进来，保存") + "\r\n\r\n" +
                Lang.S("key 只存在你自己电脑的配置文件里，不上传任何地方。"),
                Lang.S("怎么拿 Steam API key"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenFile()
        {
            try
            {
                Sources.EnsureDefault();
                System.Diagnostics.Process.Start("notepad.exe", "\"" + Sources.FilePath + "\"");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        }

        private void Save()
        {
            try
            {
                Core.Cfg.SteamEnabled = chkSteam.Checked;
                Core.Cfg.WsApiKey = txtKey.Text.Trim();
                Core.SaveConfig();
                WsNet.Retry();

                List<string> lines = new List<string>(File.ReadAllLines(Sources.FilePath, Encoding.UTF8));
                Dictionary<string, bool> want = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < list.Items.Count; i++)
                {
                    Row r = list.Items[i] as Row;
                    if (r != null) want[r.Def.Id] = list.GetItemChecked(i);
                }
                string section = "";
                for (int i = 0; i < lines.Count; i++)
                {
                    string s = lines[i].Trim();
                    if (s.StartsWith("[") && s.EndsWith("]")) { section = s.Substring(1, s.Length - 2).Trim(); continue; }
                    if (section.Length == 0 || !want.ContainsKey(section)) continue;
                    if (s.StartsWith("enabled=", StringComparison.OrdinalIgnoreCase))
                        lines[i] = "enabled=" + (want[section] ? "1" : "0");
                }
                File.WriteAllLines(Sources.FilePath, lines.ToArray(), Encoding.UTF8);
                Sources.Load(true);
                Core.Log("INFO", "设置：Steam 来源" + (Core.Cfg.SteamEnabled ? "开" : "关")
                    + "，API key" + (Core.Cfg.WsApiKey.Length > 0 ? "已填" : "空"));
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        }

        private sealed class Row
        {
            public SourceDef Def;
            public Row(SourceDef d) { Def = d; }
            public override string ToString()
            {
                string kind = Def.Kind == "json" ? Lang.S("接口") : Lang.S("网页");
                return Def.Name + "　·　" + kind;
            }
        }
    }
}
