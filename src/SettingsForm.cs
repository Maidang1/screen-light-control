using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenLight
{
    internal sealed class ModeOption : RadioButton
    {
        internal float UiScale = 1;
        internal string Description;
        internal ModeOption() { AutoSize = false; Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(UiScale, UiScale);
            float w = Width / UiScale, h = Height / UiScale;
            Theme.Box(g, new RectangleF(.5f, .5f, w - 1, h - 1), 8, Checked ? Theme.Soft : Color.White, Checked ? Color.FromArgb(200, 224, 255) : Theme.Border);
            using (var pen = new Pen(Checked ? Theme.Accent : Color.FromArgb(190, 197, 208), Checked ? 5 : 1)) g.DrawEllipse(pen, 17, h / 2 - 7, 14, 14);
            Theme.TextAt(g, Text, 14, Theme.Text, new RectangleF(46, 10, w - 62, 23), true);
            Theme.TextAt(g, Description, 12, Theme.Muted, new RectangleF(46, 34, w - 62, 22));
            if (Focused && ShowFocusCues) using (var pen = new Pen(Theme.Accent)) g.DrawRectangle(pen, 4, 4, w - 8, h - 8);
        }
    }

    internal sealed class SettingsForm : Form
    {
        internal readonly AppSettings Value;
        internal readonly float UiScale;
        internal readonly ComboBox DeviceSelector, TargetSelector;
        internal readonly TextBox DeviceName;
        internal readonly NumericUpDown MinInput, MaxInput, CurveInput;
        internal readonly ToggleSwitch SmoothSwitch, LinkedSwitch, HotkeysSwitch, RestoreSwitch;
        internal readonly ModeOption[] Modes;
        private readonly MonitorDevice[] devices;
        private readonly Panel viewport;
        private readonly DesignPanel general, display;
        private readonly UiButton generalTab, displayTab, save, cancel;
        private readonly Label message;
        private readonly string status;
        private int currentDevice = -1;
        internal bool DisplayTabSelected;
        private bool loading;
        internal SettingsForm(AppSettings original, MonitorDevice[] devices, string status, int selectedDevice = -1)
        {
            Value = SettingsStore.Copy(original); this.devices = devices; this.status = status;
            Text = "Screen Light · 设置"; StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.None;
            using (var graphics = CreateGraphics()) UiScale = graphics.DpiX / 96f;
            BackColor = Theme.Background; Font = new Font("Microsoft YaHei UI", 12 * UiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(S(760), area.Width - S(40)), Math.Min(S(900), area.Height - S(90)));
            MinimumSize = new Size(S(560), S(480)); MaximizeBox = false;
            generalTab = Button("通用与快捷键", delegate { SelectTab(false); }, true);
            displayTab = Button("显示器设置", delegate { SelectTab(true); }, true);
            displayTab.Enabled = devices.Length > 0;
            generalTab.MutedLink = displayTab.MutedLink = true;
            save = Button("保存设置", delegate { if (TrySave()) { DialogResult = DialogResult.OK; Close(); } }, false); save.Primary = true;
            cancel = Button("取消", delegate { DialogResult = DialogResult.Cancel; Close(); }, false); cancel.DialogResult = DialogResult.Cancel; CancelButton = cancel;
            viewport = new Panel { AutoScroll = true, BackColor = Theme.Background }; Controls.Add(viewport);
            general = new DesignPanel { UiScale = UiScale, Height = S(800), PaintContent = PaintGeneral };
            display = new DesignPanel { UiScale = UiScale, Height = S(726), PaintContent = PaintDisplay };
            viewport.Controls.Add(general); viewport.Controls.Add(display);
            SmoothSwitch = Toggle(general, "平滑软件调光", Value.Smooth, delegate(bool v) { Value.Smooth = v; });
            LinkedSwitch = Toggle(general, "所有屏幕滑块联动", Value.Linked, delegate(bool v) { Value.Linked = v; });
            HotkeysSwitch = Toggle(general, "启用全局快捷键", Value.Hotkeys, delegate(bool v) { Value.Hotkeys = v; if (TargetSelector != null) TargetSelector.Enabled = v; general.Invalidate(); });
            RestoreSwitch = Toggle(general, "启动时恢复上次亮度", Value.Restore, delegate(bool v) { Value.Restore = v; });
            TargetSelector = Combo(general, "快捷键控制范围"); TargetSelector.Items.AddRange(new object[] { "鼠标所在屏幕", "当前窗口所在屏幕", "所有屏幕" }); TargetSelector.SelectedIndex = (int)Value.Target; TargetSelector.Enabled = Value.Hotkeys;
            TargetSelector.SelectedIndexChanged += delegate { Value.Target = (HotkeyTarget)TargetSelector.SelectedIndex; };
            DeviceSelector = Combo(display, "选择显示器");
            foreach (var device in devices) { Value.For(device); DeviceSelector.Items.Add(device.Name + (device.Primary ? " · 主显示器" : " · 扩展显示器")); }
            DeviceName = new TextBox { BorderStyle = BorderStyle.FixedSingle, MaxLength = 80, AccessibleName = "显示名称" }; display.Controls.Add(DeviceName);
            MinInput = Number(display, "最低硬件亮度", 0, 100, 0, 1);
            MaxInput = Number(display, "最高硬件亮度", 0, 100, 0, 1);
            CurveInput = Number(display, "亮度曲线", .5m, 2m, 2, .05m);
            Modes = new ModeOption[3];
            string[] titles = { "自动模式", "软件调光", "混合调光" };
            string[] descriptions = { "优先调节硬件背光，失败后自动使用软件调光。", "直接改变画面明暗，不改变显示器背光。", "先降低背光，低于 20% 后继续用软件压暗。" };
            for (int i = 0; i < Modes.Length; i++)
            {
                Modes[i] = new ModeOption { UiScale = UiScale, Text = titles[i], Description = descriptions[i], AccessibleName = titles[i] + "，" + descriptions[i], BackColor = Theme.Background };
                display.Controls.Add(Modes[i]); Modes[i].CheckedChanged += delegate { if (!loading) { UpdateCalibration(); display.Invalidate(); } };
            }
            DeviceSelector.SelectedIndexChanged += delegate { CommitDevice(); LoadDevice(DeviceSelector.SelectedIndex); };
            message = new Label { ForeColor = Theme.Red, BackColor = Theme.Background, AutoSize = false }; Controls.Add(message);
            if (!string.IsNullOrEmpty(status) && (status.Contains("被占用") || status.Contains("无法") || status.Contains("未保存"))) message.Text = status;
            if (devices.Length > 0) DeviceSelector.SelectedIndex = Math.Max(0, Math.Min(devices.Length - 1, selectedDevice));
            Resize += delegate { LayoutUi(); }; SelectTab(selectedDevice >= 0 && devices.Length > 0); LayoutUi();
        }
        private int S(float v) { return (int)Math.Round(v * UiScale); }
        private UiButton Button(string title, Action action, bool link)
        { var b = new UiButton { Text = title, UiScale = UiScale, Link = link, BackColor = Theme.Background }; b.Click += delegate { action(); }; Controls.Add(b); return b; }
        private ToggleSwitch Toggle(Control parent, string name, bool value, Action<bool> changed)
        { var t = new ToggleSwitch { UiScale = UiScale, BackColor = Theme.Background, Checked = value, AccessibleName = name }; parent.Controls.Add(t); t.CheckedChanged += delegate { changed(t.Checked); }; return t; }
        private ComboBox Combo(Control parent, string name)
        { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Standard, AccessibleName = name }; parent.Controls.Add(c); return c; }
        private NumericUpDown Number(Control parent, string name, decimal min, decimal max, int digits, decimal step)
        { var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = digits, Increment = step, BorderStyle = BorderStyle.FixedSingle, AccessibleName = name }; parent.Controls.Add(n); return n; }
        internal void SelectTab(bool isDisplay)
        {
            DisplayTabSelected = isDisplay && devices.Length > 0;
            if (general != null) { general.Visible = !DisplayTabSelected; display.Visible = DisplayTabSelected; viewport.AutoScrollPosition = Point.Empty; }
            generalTab.Selected = !DisplayTabSelected; displayTab.Selected = DisplayTabSelected;
            generalTab.Invalidate(); displayTab.Invalidate(); LayoutUi(); Invalidate();
        }
        private void LoadDevice(int index)
        {
            currentDevice = index; if (index < 0) return;
            loading = true; var p = Value.For(devices[index]);
            DeviceName.Text = p.Name ?? ""; MinInput.Value = p.Minimum; MaxInput.Value = p.Maximum; CurveInput.Value = (decimal)p.Curve;
            for (int i = 0; i < Modes.Length; i++) Modes[i].Checked = (int)p.Mode == i;
            loading = false; UpdateCalibration(); display.Invalidate();
        }
        private void UpdateCalibration()
        { bool enabled = !Modes[1].Checked; MinInput.Enabled = MaxInput.Enabled = CurveInput.Enabled = enabled; }
        private void CommitDevice()
        {
            if (loading || currentDevice < 0) return;
            ValidateChildren(); var p = Value.For(devices[currentDevice]); p.Name = DeviceName.Text.Trim();
            p.Minimum = (int)MinInput.Value; p.Maximum = (int)MaxInput.Value; p.Curve = (double)CurveInput.Value;
            for (int i = 0; i < Modes.Length; i++) if (Modes[i].Checked) p.Mode = (ControlMode)i;
        }
        internal bool TrySave()
        {
            CommitDevice();
            try { Value.Validate(); message.Text = ""; return true; }
            catch (Exception ex)
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    try { Value.For(devices[i]).Validate(); }
                    catch { DeviceSelector.SelectedIndex = i; SelectTab(true); viewport.ScrollControlIntoView(MinInput); MinInput.Focus(); break; }
                }
                message.Text = ex.Message; return false;
            }
        }
        private void Place(Control c, float x, float y, float w, float h) { c.SetBounds(S(x), S(y), S(w), S(h)); }
        private void LayoutUi()
        {
            if (viewport == null || message == null) return;
            float w = ClientSize.Width / UiScale, h = ClientSize.Height / UiScale;
            Place(generalTab, 24, 90, 126, 38); Place(displayTab, 165, 90, 110, 38);
            Place(viewport, 32, 150, w - 64, h - 254);
            viewport.AutoScrollMinSize = new Size(0, DisplayTabSelected ? display.Height : general.Height);
            int width = viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
            general.Width = display.Width = width;
            float inner = width / UiScale;
            foreach (var page in new DesignPanel[] { general, display }) if (page.Visible) page.Location = viewport.AutoScrollPosition;
            Place(SmoothSwitch, inner - 48, 37, 44, 30); Place(LinkedSwitch, inner - 48, 119, 44, 30);
            Place(HotkeysSwitch, inner - 48, 239, 44, 30); Place(RestoreSwitch, inner - 48, 599, 44, 30);
            Place(TargetSelector, inner - 238, 305, 234, 32);
            float half = (inner - 20) / 2;
            Place(DeviceSelector, 0, 26, half, 34); Place(DeviceName, half + 20, 26, half - 4, 32);
            for (int i = 0; i < Modes.Length; i++) Place(Modes[i], 0, 114 + i * 69, inner - 4, 64);
            Place(MinInput, 0, 424, half, 32); Place(MaxInput, half + 20, 424, half - 4, 32);
            Place(CurveInput, inner - 112, 482, 108, 32);
            Place(message, 32, h - 97, w - 64, 34);
            Place(cancel, w - 236, h - 54, 84, 36); Place(save, w - 140, h - 54, 108, 36);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; g.ScaleTransform(UiScale, UiScale); float w = ClientSize.Width / UiScale, h = ClientSize.Height / UiScale;
            Theme.TextAt(g, "设置", 24, Theme.Text, new RectangleF(32, 20, w - 64, 38), true);
            Theme.TextAt(g, DisplayTabSelected ? "按你的使用习惯，调整每一块屏幕。" : "让常用操作触手可及。", 12, Theme.Muted, new RectangleF(32, 63, w - 64, 23));
            using (var p = new Pen(Theme.Border)) { g.DrawLine(p, 32, 130, w - 32, 130); g.DrawLine(p, 32, h - 104, w - 32, h - 104); }
            using (var p = new Pen(Theme.Accent, 2)) g.DrawLine(p, DisplayTabSelected ? 174 : 32, 130, DisplayTabSelected ? 266 : 142, 130);
            Theme.TextAt(g, DisplayTabSelected ? "每屏设置独立保存" : "关闭窗口后驻留系统托盘", 11, Theme.Muted, new RectangleF(32, h - 52, w - 290, 32));
        }
        private void Title(Graphics g, string text, float y, float w) { Theme.TextAt(g, text, 14, Theme.Text, new RectangleF(0, y, w, 24), true); }
        private void RowText(Graphics g, string title, string desc, float y, float w)
        { Theme.TextAt(g, title, 14, Theme.Text, new RectangleF(0, y, w - 58, 25), true); Theme.TextAt(g, desc, 12, Theme.Muted, new RectangleF(0, y + 27, w - 58, 23)); }
        private void PaintGeneral(Graphics g, float w)
        {
            Title(g, "调光体验", 0, w); RowText(g, "平滑软件调光", "亮度柔和过渡，连续调节时跟随最新目标。", 30, w);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, 100, w - 4, 100);
            RowText(g, "所有屏幕滑块联动", "拖动任意滑块，将相同百分比应用到所有屏幕。", 112, w);
            Title(g, "键盘快捷键", 200, w); RowText(g, "启用全局快捷键", "关闭面板后仍可使用。", 232, w);
            Theme.TextAt(g, "控制范围", 13, Theme.Text, new RectangleF(0, 305, 150, 32));
            Theme.Box(g, new RectangleF(.5f, 354, w - 5, 156), 10, Color.White, Theme.Border);
            string[] labels = { "调节 5%", "精细调节 1%", "恢复画面" }, keys = { "Ctrl  +  Alt  +  ↑ / ↓", "Ctrl  +  Alt  +  Shift  +  ↑ / ↓", "Ctrl  +  Alt  +  Home" };
            for (int i = 0; i < 3; i++)
            {
                Theme.TextAt(g, labels[i], 13, Theme.Text, new RectangleF(16, 364 + 48 * i, w - 280, 32));
                Theme.Box(g, new RectangleF(w - 260, 365 + 48 * i, 238, 30), 5, Color.FromArgb(244, 245, 247), Theme.Border);
                Theme.TextAt(g, keys[i], 12, Theme.Text, new RectangleF(w - 258, 365 + 48 * i, 234, 30), false, StringAlignment.Center, "Segoe UI");
            }
            Theme.TextAt(g, "控制所有屏幕时，按各屏当前值分别增减亮度。", 11, Theme.Muted, new RectangleF(0, 523, w - 4, 24));
            Title(g, "启动与记忆", 560, w); RowText(g, "启动时恢复上次亮度", "启动时应用上次成功调节的亮度，默认关闭。", 592, w);
            Theme.Box(g, new RectangleF(0, 662, w - 4, 62), 8, Theme.Soft, null);
            Theme.TextAt(g, "每屏名称、调光模式和校准会自动记忆。", 12, Color.FromArgb(70, 97, 125), new RectangleF(14, 671, w - 32, 23));
            Theme.TextAt(g, "退出程序时，软件调光会清除。", 12, Color.FromArgb(70, 97, 125), new RectangleF(14, 696, w - 32, 23));
            if (!string.IsNullOrEmpty(status)) Theme.TextAt(g, status, 11, status.Contains("未") || status.Contains("无法") ? Theme.Amber : Theme.Muted, new RectangleF(0, 742, w - 4, 42));
        }
        private void PaintDisplay(Graphics g, float w)
        {
            float half = (w - 20) / 2;
            Theme.TextAt(g, "选择显示器", 12, Theme.Muted, new RectangleF(0, 0, half, 24));
            Theme.TextAt(g, "显示名称（留空使用设备名称）", 12, Theme.Muted, new RectangleF(half + 20, 0, half, 24));
            Title(g, "调光模式", 80, w); Title(g, "硬件亮度校准", 338, w);
            Theme.TextAt(g, Modes[1].Checked ? "软件模式不改变背光；硬件校准保留供其他模式使用。" : "设置实际背光的调节范围，让滑块更适合这块显示器。", 12, Theme.Muted, new RectangleF(0, 367, w - 4, 24));
            Theme.TextAt(g, "最低亮度 (%)", 12, Theme.Muted, new RectangleF(0, 398, half, 24));
            Theme.TextAt(g, "最高亮度 (%)", 12, Theme.Muted, new RectangleF(half + 20, 398, half, 24));
            Theme.TextAt(g, "亮度曲线", 14, Theme.Text, new RectangleF(0, 477, w - 130, 24), true);
            Theme.TextAt(g, "范围 0.5–2.0；1 为线性，越大则低亮度区域调节越细。", 12, Theme.Muted, new RectangleF(0, 505, w - 4, 24));
            Theme.Box(g, new RectangleF(0, 550, w - 4, 77), 8, Color.FromArgb(240, 243, 247), null);
            bool mixed = Modes[2].Checked;
            Theme.TextAt(g, mixed ? "当前混合模式" : Modes[1].Checked ? "当前软件模式" : "当前自动模式", 11, Theme.Muted, new RectangleF(14, 558, w - 32, 20));
            if (mixed)
            {
                float track = w - 32;
                Theme.Box(g, new RectangleF(14, 586, track * .2f, 6), 3, Color.FromArgb(152, 200, 255), null);
                Theme.Box(g, new RectangleF(17 + track * .2f, 586, track * .8f - 3, 6), 3, Theme.Accent, null);
                Theme.TextAt(g, "0–20% · 软件压暗", 11, Theme.Muted, new RectangleF(14, 601, w / 2, 21));
                Theme.TextAt(g, "20–100% · 硬件背光", 11, Theme.Muted, new RectangleF(w / 2, 601, w / 2 - 18, 21), false, StringAlignment.Far);
            }
            else Theme.TextAt(g, Modes[1].Checked ? "只改变画面明暗，最低保留 10% 亮度。" : "优先硬件调节并核对读回结果，失败后使用软件调光。", 12, Theme.Muted, new RectangleF(14, 588, w - 32, 28));
            Theme.TextAt(g, "保存设置不会主动改变硬件背光，校准用于下一次调节。", 11, Theme.Muted, new RectangleF(0, 645, w - 4, 28));
            if (currentDevice >= 0 && !devices[currentDevice].Supported) Theme.TextAt(g, "此屏幕暂未提供硬件接口，将使用软件调光。", 12, Theme.Amber, new RectangleF(0, 682, w - 4, 28));
        }
    }
}
