using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ScreenLight
{
    internal sealed class DiagnosticsForm : Form
    {
        internal DiagnosticsForm(MonitorCard[] cards, string log, float scale)
        {
            Text = "Screen Light · 诊断详情"; AutoScaleMode = AutoScaleMode.None; BackColor = Theme.Background; StartPosition = FormStartPosition.CenterParent;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size((int)(680 * scale), Math.Min((int)(750 * scale), area.Height - (int)(90 * scale)));
            MinimumSize = new Size((int)(520 * scale), (int)(420 * scale));
            var viewport = new Panel { AutoScroll = true, BackColor = Theme.Background };
            var body = new DesignPanel { UiScale = scale };
            var details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = log, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel), AccessibleName = "原始诊断日志" };
            var close = new UiButton { Text = "关闭", UiScale = scale, BackColor = Theme.Background, DialogResult = DialogResult.Cancel };
            close.Click += delegate { Close(); }; CancelButton = close;
            var snapshots = cards.Select(c => new string[] { c.Device.Name, c.State,
                c.Device.LastRequestedValue.HasValue ? c.Device.LastRequestedValue + "%" : "尚未调节",
                c.Device.Supported ? c.Device.HardwareValue + "%" : "硬件接口不可用",
                string.Join(" → ", c.Device.Channels.Select(ch => ch.Name)),
                (c.Device.UsingSoftware ? "软件调光" : c.Device.Settings.Mode == ControlMode.混合 ? "混合调光" : "硬件优先") + " · " + c.Device.Value + "%" }).ToArray();
            body.PaintContent = delegate(Graphics g, float w)
            {
                for (int i = 0; i < snapshots.Length; i++)
                {
                    float y = i * 218; var data = snapshots[i];
                    Theme.Box(g, new RectangleF(.5f, y + .5f, w - 5, 200), 10, Color.White, Theme.Border);
                    Theme.TextAt(g, data[0], 14, Theme.Text, new RectangleF(18, y + 12, w - 36, 28), true);
                    Theme.TextAt(g, data[1], 11, data[1].Contains("未响应") ? Theme.Amber : Theme.Accent, new RectangleF(18, y + 42, w - 36, 22));
                    string[] labels = { "请求亮度", "硬件读回", "可用通道", "当前方式" };
                    for (int j = 0; j < 4; j++)
                    {
                        Theme.TextAt(g, labels[j], 12, Theme.Muted, new RectangleF(18, y + 72 + j * 28, 90, 24));
                        Theme.TextAt(g, data[j + 2], 12, Theme.Text, new RectangleF(112, y + 72 + j * 28, w - 134, 24), false, StringAlignment.Far);
                    }
                }
                Theme.TextAt(g, "原始日志", 13, Theme.Text, new RectangleF(0, snapshots.Length * 218, w, 26), true);
            };
            body.Controls.Add(details); viewport.Controls.Add(body); Controls.Add(viewport); Controls.Add(close);
            Paint += delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.ScaleTransform(scale, scale); float w = ClientSize.Width / scale;
                Theme.TextAt(e.Graphics, "诊断详情", 24, Theme.Text, new RectangleF(32, 20, w - 64, 38), true);
                Theme.TextAt(e.Graphics, "接口返回成功不等于背光已改变，以读回结果为准。", 12, Theme.Muted, new RectangleF(32, 65, w - 64, 24));
            };
            Action layout = delegate
            {
                viewport.SetBounds((int)(32 * scale), (int)(110 * scale), ClientSize.Width - (int)(64 * scale), ClientSize.Height - (int)(180 * scale));
                body.Size = new Size(viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth, (int)((snapshots.Length * 218 + 232) * scale));
                details.SetBounds(0, (int)((snapshots.Length * 218 + 36) * scale), body.Width - (int)(4 * scale), (int)(180 * scale));
                close.SetBounds(ClientSize.Width - (int)(120 * scale), ClientSize.Height - (int)(52 * scale), (int)(88 * scale), (int)(36 * scale));
            };
            Resize += delegate { layout(); }; layout();
        }
    }
}
