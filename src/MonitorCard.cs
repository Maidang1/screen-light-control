using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenLight
{
    internal sealed class MonitorCard : Panel
    {
        internal readonly MonitorDevice Device;
        internal readonly BrightnessSlider Slider;
        internal readonly UiButton Mode, SettingsButton;
        internal float UiScale;
        internal bool Updating;
        internal string State, Detail;
        internal Color StateColor = Theme.Muted;
        private bool failed;
        private readonly ToolTip tooltip = new ToolTip();
        internal event Action<MonitorCard, bool> ModeRequested;
        internal event Action<MonitorCard> MixedRequested, SettingsRequested;
        internal MonitorCard(MonitorDevice device, float scale)
        {
            Device = device; UiScale = scale; BackColor = Theme.Background;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Slider = new BrightnessSlider { UiScale = scale, Minimum = device.UsingSoftware ? 10 : 0, Value = device.Value };
            Mode = new UiButton { UiScale = scale, BackColor = Color.White, Quiet = true, AccessibleName = "选择调光模式" };
            SettingsButton = new UiButton { UiScale = scale, BackColor = Color.White, Link = true, Symbol = "settings", AccessibleName = "设置 " + device.Name };
            Controls.Add(Slider); Controls.Add(Mode); Controls.Add(SettingsButton);
            SettingsButton.Click += delegate { if (SettingsRequested != null) SettingsRequested(this); };
            tooltip.SetToolTip(SettingsButton, "此显示器的模式、名称与校准");
            Mode.Click += delegate
            {
                var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 10), BackColor = Color.White, ShowImageMargin = false, ShowCheckMargin = true };
                var auto = menu.Items.Add("自动模式  ·  优先硬件，失败后自动回退", null, delegate { if (ModeRequested != null) ModeRequested(this, false); }) as ToolStripMenuItem;
                var software = menu.Items.Add("软件调光  ·  直接改变画面明暗", null, delegate { if (ModeRequested != null) ModeRequested(this, true); }) as ToolStripMenuItem;
                var mixed = menu.Items.Add("混合调光  ·  背光最低后继续软件调暗", null, delegate { if (MixedRequested != null) MixedRequested(this); }) as ToolStripMenuItem;
                auto.Checked = !device.UsingSoftware && device.Settings.Mode == ControlMode.自动;
                software.Checked = device.UsingSoftware; mixed.Checked = !device.UsingSoftware && device.Settings.Mode == ControlMode.混合;
                menu.Closed += delegate { menu.Dispose(); }; menu.Show(Mode, new Point(0, Mode.Height));
            };
            Slider.ValueChanged += delegate { if (!Updating) { State = "正在调节…"; Detail = ""; failed = false; StateColor = Theme.Accent; } Invalidate(); };
            Height = (int)(235 * scale); Width = (int)(616 * scale);
            Resize += delegate { LayoutChildren(); }; LayoutChildren();
            Sync(device.UsingSoftware ? "软件调光已就绪" : "已读取当前亮度", "", false);
        }
        private void LayoutChildren()
        {
            Slider.SetBounds((int)(8 * UiScale), (int)(137 * UiScale), Width - (int)(16 * UiScale), (int)(34 * UiScale));
            Mode.SetBounds(Width - (int)(124 * UiScale), (int)(85 * UiScale), (int)(104 * UiScale), (int)(32 * UiScale));
            SettingsButton.SetBounds(Width - (int)(52 * UiScale), (int)(16 * UiScale), (int)(32 * UiScale), (int)(32 * UiScale));
        }
        internal void Sync(string state, string detail, bool error)
        {
            Updating = true; Slider.Minimum = Device.UsingSoftware ? 10 : 0; Slider.Value = Device.Value; Updating = false;
            Mode.Text = Device.UsingSoftware ? "软件调光  ▾" : Device.Settings.Mode == ControlMode.混合 ? "混合调光  ▾" : "自动模式  ▾";
            Mode.Invalidate(); State = state; Detail = detail; failed = error;
            StateColor = error ? Theme.Red : state.Contains("未响应") ? Theme.Amber : Device.UsingSoftware ? Theme.Accent : Theme.Green;
            tooltip.SetToolTip(this, state + (string.IsNullOrEmpty(detail) ? "" : "\n" + detail));
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(UiScale, UiScale);
            float w = Width / UiScale;
            Theme.Box(g, new RectangleF(.5f, .5f, w - 1, 234), 12, Color.White, Theme.Border);
            Theme.Symbol(g, "monitor", 30, 34, Theme.Text);
            Theme.TextAt(g, Device.Name, 14, Theme.Text, new RectangleF(50, 17, w - 112, 25), true);
            Theme.TextAt(g, Device.Bounds.Width + " × " + Device.Bounds.Height + (Device.Primary ? " · 主显示器" : " · 扩展显示器"), 11, Theme.Muted, new RectangleF(50, 41, w - 112, 20));
            string value = Slider.Value.ToString();
            Theme.TextAt(g, value, 44, Theme.Text, new RectangleF(17, 74, 110, 56), false, StringAlignment.Near, "Segoe UI");
            float percentX = value.Length == 3 ? 93 : value.Length == 2 ? 70 : 46;
            Theme.TextAt(g, "%", 18, Theme.Muted, new RectangleF(percentX, 97, 30, 26), false, StringAlignment.Near, "Segoe UI");
            Theme.TextAt(g, Device.UsingSoftware ? "10%" : "0%", 10, Theme.Muted, new RectangleF(20, 174, 50, 20));
            Theme.TextAt(g, "100%", 10, Theme.Muted, new RectangleF(w - 70, 174, 50, 20), false, StringAlignment.Far);
            string badge = failed ? "调光失败" : State.Contains("未响应") ? "已自动回退" : State.Contains("核对") ? "正在核对…" : State.Contains("正在调节") ? "正在调节…" : State.Contains("已恢复") || State.Contains("已清除") ? "已恢复画面" : Device.UsingSoftware ? "软件调光" : State.Contains("已验证") ? "背光已验证" : "硬件优先";
            Color ink = failed ? Theme.Red : badge == "已自动回退" ? Theme.Amber : badge.Contains("正在") || Device.UsingSoftware ? Theme.Accent : Theme.Green;
            Color bg = failed ? Color.FromArgb(255, 240, 240) : badge == "已自动回退" ? Theme.AmberSoft : Device.UsingSoftware || badge.Contains("正在") ? Theme.Soft : Color.FromArgb(234, 246, 240);
            Theme.Box(g, new RectangleF(20, 201, 102, 23), 5, bg, null);
            Theme.TextAt(g, badge, 11, ink, new RectangleF(26, 201, 90, 23), false, StringAlignment.Center);
            string hint = Device.UsingSoftware ? "只改变画面，不改变背光" : Device.Settings.Mode == ControlMode.混合 && Device.Value < 20 ? "已进入低亮度调光" : "硬件调节 · 读回验证";
            Theme.TextAt(g, hint, 11, Theme.Muted, new RectangleF(133, 201, w - 153, 23), false, StringAlignment.Far);
            AccessibleName = Device.Name + "，" + Slider.Value + "%，" + State;
        }
        protected override void Dispose(bool disposing) { if (disposing) tooltip.Dispose(); base.Dispose(disposing); }
    }
}
