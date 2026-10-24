using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenLight
{
    internal sealed class MainForm : Form
    {
        private readonly MonitorService service = new MonitorService();
        private readonly DimmingManager dimming = new DimmingManager();
        private readonly SemaphoreSlim io = new SemaphoreSlim(1, 1);
        private readonly Dictionary<MonitorCard, int> pending = new Dictionary<MonitorCard, int>();
        internal readonly List<MonitorCard> Cards = new List<MonitorCard>();
        private readonly Panel list;
        private readonly Label emptyState;
        private readonly UiButton refresh, reset, details, settingsButton;
        internal readonly ToggleSwitch LinkSwitch;
        private readonly SettingsStore settingsStore;
        internal AppSettings preferences;
        private readonly bool testing;
        private string hotkeyStatus;
        private bool linkedUpdate, firstDiscovery = true, settingsOpen;
        private int operationVersion;
        private readonly List<UiButton> presets = new List<UiButton>();
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer debounce;
        private readonly Icon appIcon;
        internal readonly float UiScale;
        private bool refreshing, writing, exiting, allowClose, refreshAgain, resourcesDisposed;
        internal string Status = "正在检测显示器…";
        internal bool Ready { get { return !refreshing && !writing && Cards.Count > 0; } }
        internal bool IsIdle { get { return !refreshing && !writing && pending.Count == 0; } }
        internal IntPtr[] DimmingHandles { get { return dimming.Handles; } }

        internal MainForm() : this(false) { }
        internal MainForm(bool testMode, bool discoverOnShow = true)
        {
            testing = testMode;
            settingsStore = new SettingsStore(testMode ? null : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml"));
            preferences = settingsStore.Load();
            Text = "屏幕亮度 · Screen Light"; BackColor = Theme.Background; ForeColor = Theme.Text; AutoScaleMode = AutoScaleMode.None;
            using (var graphics = CreateGraphics()) UiScale = graphics.DpiX / 96f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ClientSize = new Size(S(680), S(627)); MinimumSize = new Size(S(560), S(520)); StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; appIcon = CreateIcon(); Icon = appIcon;
            refresh = Button("重新检测", async delegate { await RefreshDevices(); });
            reset = Button("恢复画面", delegate { ResetDimming(); });
            details = Button("诊断详情", delegate { ShowDiagnostics(); });
            settingsButton = Button("设置", delegate { ShowSettings(); });
            settingsButton.Link = true; details.Link = true;
            details.MutedLink = true;
            LinkSwitch = new ToggleSwitch { Text = "滑块联动", UiScale = UiScale, BackColor = Theme.Background, Checked = preferences.Linked, AccessibleName = "所有屏幕滑块联动" };
            LinkSwitch.CheckedChanged += delegate { preferences.Linked = LinkSwitch.Checked; SaveSettings(); };
            Controls.Add(LinkSwitch);
            list = new Panel { AutoScroll = true, BackColor = Theme.Background }; Controls.Add(list);
            emptyState = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = Status, ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 14 * UiScale, FontStyle.Regular, GraphicsUnit.Pixel) };
            list.Controls.Add(emptyState);
            string[] names = { "夜间", "阅读", "工作", "明亮" }; int[] values = { 25, 50, 75, 100 };
            for (int i = 0; i < values.Length; i++)
            {
                int target = values[i]; var button = Button(names[i], delegate { SetAll(target); }); button.Subtitle = values[i] + "%"; button.Symbol = new string[] { "moon", "book", "work", "sun" }[i]; presets.Add(button);
            }
            var menu = new ContextMenuStrip(); menu.Items.Add("打开亮度面板", null, delegate { Restore(); });
            menu.Items.Add("设置 / 快捷键", null, delegate { ShowSettings(); });
            menu.Items.Add("恢复画面（清除软件调光）", null, delegate { ResetDimming(); }); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, async delegate { await ExitAsync(); });
            tray = new NotifyIcon { Icon = appIcon, Text = "Screen Light · 屏幕亮度", ContextMenuStrip = menu, Visible = !testMode };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Restore(); };
            debounce = new System.Windows.Forms.Timer { Interval = 90 };
            debounce.Tick += async delegate { debounce.Stop(); await ApplyPending(); };
            Shown += async delegate { ConfigureHotkeys(); if (discoverOnShow) await RefreshDevices(); };
            Resize += delegate { LayoutUi(); };
            FormClosing += async delegate(object sender, FormClosingEventArgs e)
            {
                if (allowClose) return;
                if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
                { exiting = true; pending.Clear(); debounce.Stop(); dimming.Dispose(); tray.Visible = false; allowClose = true; return; }
                e.Cancel = true;
                if (testMode) await ExitAsync(); else Hide();
            };
            LayoutUi(); UpdateButtons();
        }
        private int S(float value) { return (int)Math.Round(value * UiScale); }
        private UiButton Button(string text, Action click)
        {
            var button = new UiButton { Text = text, UiScale = UiScale, BackColor = Theme.Background };
            button.Click += delegate { click(); }; Controls.Add(button); return button;
        }
        private void LayoutUi()
        {
            if (list == null) return;
            float w = ClientSize.Width / UiScale, h = ClientSize.Height / UiScale;
            refresh.SetBounds(S(w - 122), S(31), S(90), S(36));
            LinkSwitch.SetBounds(S(w - 154), S(89), S(122), S(28));
            list.SetBounds(S(32), S(126), S(w - 64), S(h - 392));
            list.AutoScrollMinSize = new Size(0, S(Cards.Count * 253 - (Cards.Count > 0 ? 18 : 0)));
            int y = 0;
            foreach (var card in Cards) { card.SetBounds(0, y + list.AutoScrollPosition.Y, list.ClientSize.Width - (list.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0), S(235)); y += S(253); }
            float tileW = (w - 64 - 30) / 4;
            for (int i = 0; i < presets.Count; i++) presets[i].SetBounds(S(32 + i * (tileW + 10)), S(h - 208), S(tileW), S(67));
            reset.SetBounds(S(w - 132), S(h - 96), S(100), S(36));
            settingsButton.SetBounds(S(24), S(h - 96), S(50), S(36));
            details.SetBounds(S(80), S(h - 96), S(80), S(36));
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(UiScale, UiScale);
            float w = ClientSize.Width / UiScale, h = ClientSize.Height / UiScale;
            Theme.TextAt(g, "屏幕亮度", 24, Theme.Text, new RectangleF(32, 20, 240, 36), true);
            Theme.TextAt(g, "让每一块屏幕，都刚好舒适", 12, Theme.Muted, new RectangleF(32, 61, 350, 22));
            Theme.TextAt(g, "我的显示器 · " + Cards.Count, 13, Theme.Muted, new RectangleF(32, 91, 240, 23));
            Theme.TextAt(g, "亮度预设", 13, Theme.Text, new RectangleF(32, h - 240, 120, 24), true);
            Theme.TextAt(g, "应用到所有屏幕", 11, Theme.Muted, new RectangleF(w - 212, h - 240, 180, 24), false, StringAlignment.Far);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 32, h - 117, w - 32, h - 117);
            Theme.TextAt(g, "关闭后驻留托盘  ·  Ctrl + Alt + ↑ / ↓ 调节亮度", 11, Theme.Muted, new RectangleF(32, h - 40, w - 64, 24));
        }
        private void Restore() { if (exiting) return; Show(); WindowState = FormWindowState.Normal; Activate(); }
        private void Remember(MonitorDevice device)
        {
            device.Settings.LastValue = device.Value;
            device.Settings.HasValue = true;
        }
        private void SaveSettings()
        {
            bool saved = settingsStore.Save(preferences);
            if (!exiting) { settingsButton.Text = saved ? "设置" : "设置 !"; settingsButton.Invalidate(); }
            if (!saved && tray != null) tray.Text = "Screen Light · 设置未保存，请查看诊断详情";
        }
        private async void ShowSettings(MonitorCard selected = null)
        {
            if (writing || refreshing || exiting || settingsOpen) return;
            await ApplyPending();
            if (writing || refreshing || exiting) return;
            using (var dialog = new SettingsForm(preferences, Cards.Select(c => c.Device).ToArray(), settingsStore.Error ?? hotkeyStatus, selected == null ? -1 : Cards.IndexOf(selected)))
            {
                dialog.Icon = Icon;
                DialogResult result;
                settingsOpen = true;
                try { result = dialog.ShowDialog(this); }
                finally { settingsOpen = false; }
                if (result != DialogResult.OK) return;
                if (!settingsStore.Save(dialog.Value)) { MessageBox.Show(this, settingsStore.Error, "无法保存设置"); return; }
                preferences = dialog.Value;
                LinkSwitch.Checked = preferences.Linked;
                foreach (var card in Cards)
                {
                    var device = card.Device;
                    var profile = preferences.For(device);
                    bool changed = profile.Mode != device.Settings.Mode;
                    bool calibrationChanged = profile.Minimum != device.Settings.Minimum || profile.Maximum != device.Settings.Maximum || profile.Curve != device.Settings.Curve;
                    device.Settings = profile;
                    if (changed)
                    {
                        dimming.Remove(device.ScreenName);
                        device.UsingSoftware = profile.Mode == ControlMode.软件 || !device.Supported;
                        device.Value = device.UsingSoftware ? 100 : profile.Logical(device.HardwareValue);
                    }
                    else if (calibrationChanged && !device.UsingSoftware)
                    {
                        dimming.Remove(device.ScreenName); device.Value = profile.Logical(device.HardwareValue);
                    }
                    Remember(device);
                }
                SaveSettings(); ConfigureHotkeys(); await RefreshDevices();
            }
        }
        private void ConfigureHotkeys()
        {
            if (testing) return;
            for (int id = 1; id <= 5; id++) UnregisterHotKey(Handle, id);
            if (!preferences.Hotkeys) { hotkeyStatus = "全局快捷键已关闭。"; return; }
            var failed = new List<string>();
            Keys[] keys = { Keys.Up, Keys.Down, Keys.Up, Keys.Down, Keys.Home };
            for (int i = 0; i < keys.Length; i++)
            {
                uint modifiers = 3u | (i == 2 || i == 3 ? 4u : 0u);
                if (!RegisterHotKey(Handle, i + 1, modifiers, (uint)keys[i])) failed.Add("Ctrl+Alt+" + (i == 2 || i == 3 ? "Shift+" : "") + keys[i]);
            }
            hotkeyStatus = failed.Count == 0 ? "全局快捷键已启用；控制范围：" + preferences.Target : "快捷键被占用，未启用：" + string.Join("、", failed);
            if (failed.Count > 0) { tray.BalloonTipTitle = "Screen Light · 快捷键冲突"; tray.BalloonTipText = hotkeyStatus; tray.ShowBalloonTip(4000); }
        }
        internal void HandleHotkey(int id)
        {
            if (!preferences.Hotkeys || exiting || refreshing || id < 1 || id > 5) return;
            if (id == 5) { ResetDimming(); return; }
            if (settingsOpen) return;
            string screen = preferences.Target == HotkeyTarget.当前窗口所在屏幕 ? Screen.FromHandle(GetForegroundWindow()).DeviceName : Screen.FromPoint(Cursor.Position).DeviceName;
            int step = (id == 1 || id == 3 ? 1 : -1) * (id <= 2 ? 5 : 1);
            linkedUpdate = true;
            try
            {
                foreach (var card in Cards)
                    if (preferences.Target == HotkeyTarget.所有屏幕 || card.Device.ScreenName == screen)
                    {
                        card.Slider.Value += step;
                        pending[card] = card.Slider.Value;
                    }
            }
            finally { linkedUpdate = false; }
            debounce.Stop(); debounce.Start();
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x0312 && preferences != null && !exiting) HandleHotkey(message.WParam.ToInt32());
            if (message.Msg == 0x007E && list != null && !exiting && IsHandleCreated)
            {
                operationVersion++;
                // Remove obsolete overlay rectangles immediately when monitor topology changes.
                dimming.Dispose();
                BeginInvoke(new Action(async delegate { await RefreshDevices(); }));
            }
        }
        private void UpdateButtons()
        {
            emptyState.Visible = Cards.Count == 0; emptyState.Text = Status;
            refresh.Enabled = !refreshing && !writing && !exiting; reset.Enabled = !refreshing && !exiting;
            settingsButton.Enabled = !refreshing && !writing && !exiting;
            LinkSwitch.Enabled = !refreshing && !exiting && Cards.Count > 1;
            foreach (var button in presets) button.Enabled = !refreshing && !exiting && Cards.Count > 0;
            foreach (var card in Cards) { card.Mode.Enabled = !writing && !refreshing && !exiting; card.SettingsButton.Enabled = card.Mode.Enabled; }
        }
        internal async Task RefreshDevices()
        {
            if (writing) { refreshAgain = true; return; }
            if (refreshing || exiting) return;
            refreshing = true; pending.Clear(); debounce.Stop(); UpdateButtons(); list.Enabled = false; Status = "正在检测显示器…"; Invalidate();
            await io.WaitAsync();
            try
            {
                var previous = Cards.ToDictionary(c => c.Device.Settings.Id, c => new int[] { c.Device.Value, c.Device.UsingSoftware ? 1 : 0 });
                await Task.Run(() => service.Discover()); if (exiting) return;
                foreach (var card in Cards) card.Dispose(); Cards.Clear();
                dimming.Reconcile(service.Devices);
                foreach (var device in service.Devices)
                {
                    device.Settings = preferences.For(device);
                    if (!string.IsNullOrWhiteSpace(device.Settings.Name)) device.Name = device.Settings.Name;
                    device.UsingSoftware = device.UsingSoftware || device.Settings.Mode == ControlMode.软件;
                    device.Value = device.UsingSoftware ? 100 : device.Settings.Logical(device.HardwareValue);
                    int[] old;
                    if (previous.TryGetValue(device.Settings.Id, out old))
                    {
                        if (old[1] == 1) { dimming.Apply(device, old[0]); }
                        else if (device.Settings.Mode == ControlMode.混合 && device.HardwareValue == device.Settings.Hardware(old[0]))
                        {
                            dimming.Apply(device, device.Settings.Software(old[0])); device.UsingSoftware = false; device.Value = old[0];
                        }
                    }
                    var card = AttachDevice(device);
                    if (firstDiscovery && preferences.Restore && device.Settings.HasValue) pending[card] = device.Settings.LastValue;
                }
                Status = Cards.Count == 0 ? "未发现显示器，请连接后重新检测" : Cards.Count + " 块显示器已就绪";
                if (firstDiscovery)
                {
                    var area = Screen.FromControl(this).WorkingArea;
                    ClientSize = new Size(Math.Min(S(680), area.Width - S(32)), Math.Min(S(627 + Math.Max(0, Math.Min(3, Cards.Count) - 1) * 253), area.Height - S(72)));
                    Location = new Point(area.X + (area.Width - Width) / 2, area.Y + Math.Max(0, (area.Height - Height) / 2));
                }
                LayoutUi();
                firstDiscovery = false;
            }
            catch (Exception ex) { Status = "检测失败：" + ex.Message; }
            finally { io.Release(); refreshing = false; if (!exiting) { list.Enabled = true; UpdateButtons(); Invalidate(); } }
            if (pending.Count > 0 && !exiting) { debounce.Start(); }
        }
        internal MonitorCard AttachDevice(MonitorDevice device)
        {
            var card = new MonitorCard(device, UiScale);
            card.Slider.ValueChanged += delegate
            {
                if (card.Updating || refreshing || exiting) return;
                pending[card] = card.Slider.Value; debounce.Stop(); debounce.Start();
                if (preferences.Linked && !linkedUpdate)
                {
                    linkedUpdate = true;
                    try { foreach (var other in Cards) if (other != card) { other.Slider.Value = card.Slider.Value; pending[other] = other.Slider.Value; } }
                    finally { linkedUpdate = false; }
                }
                foreach (var button in presets) { button.Selected = false; button.Invalidate(); }
            };
            card.ModeRequested += ChangeMode;
            card.SettingsRequested += delegate(MonitorCard selected) { ShowSettings(selected); };
            card.MixedRequested += delegate(MonitorCard selected) { ChangeControlMode(selected, ControlMode.混合); };
            Cards.Add(card); list.Controls.Add(card); LayoutUi(); UpdateButtons(); return card;
        }
        internal void SetAll(int target)
        {
            if (refreshing || exiting) return;
            linkedUpdate = true;
            foreach (var card in Cards)
            {
                card.Slider.Value = target;
                // A repeated preset still verifies hardware; equality must not silently skip a write.
                pending[card] = card.Slider.Value;
            }
            linkedUpdate = false;
            foreach (var button in presets) { button.Selected = button.Subtitle == target + "%"; button.Invalidate(); }
            debounce.Stop(); debounce.Start();
        }
        private void ChangeMode(MonitorCard card, bool software)
        { ChangeControlMode(card, software ? ControlMode.软件 : ControlMode.自动); }
        private void ChangeControlMode(MonitorCard card, ControlMode mode)
        {
            if (writing || refreshing || exiting) return;
            pending.Remove(card); dimming.Remove(card.Device.ScreenName);
            card.Device.Settings.Mode = mode;
            card.Device.UsingSoftware = mode == ControlMode.软件 || !card.Device.Supported;
            card.Device.Value = card.Device.UsingSoftware ? 100 : card.Device.Settings.Logical(card.Device.HardwareValue);
            card.Sync(card.Device.UsingSoftware ? "软件调光已就绪" : "已切换为硬件优先", "", false);
            Remember(card.Device); SaveSettings();
        }
        internal async Task ApplyPending()
        {
            if (writing || refreshing || exiting || pending.Count == 0) return;
            int version = operationVersion;
            writing = true; UpdateButtons(); await io.WaitAsync();
            try
            {
                while (pending.Count > 0 && !exiting && version == operationVersion)
                {
                    var batch = pending.ToArray(); pending.Clear();
                    foreach (var item in batch)
                    {
                        if (exiting || version != operationVersion) break;
                        var card = item.Key; var device = card.Device;
                        device.LastRequestedValue = item.Value;
                        string fallback = null;
                        if (!device.UsingSoftware)
                        {
                            card.State = "正在核对硬件亮度…"; card.Invalidate();
                            try { await Task.Run(() => device.Set(device.Settings.Hardware(item.Value))); }
                            catch (Exception ex) { fallback = ex.Message; }
                        }
                        if (exiting) break;
                        if (version != operationVersion)
                        {
                            if (!device.UsingSoftware) device.Value = device.Settings.Logical(device.HardwareValue);
                            card.Sync("软件调光已清除", "", false); Remember(device); break;
                        }
                        if (device.UsingSoftware || fallback != null)
                        {
                            try
                            {
                                int target; if (!pending.TryGetValue(card, out target)) target = item.Value;
                                target = Math.Max(10, target);
                                device.LastRequestedValue = target;
                                bool done = await dimming.ApplySmooth(device, target, preferences.Smooth, true, target,
                                    () => exiting || version != operationVersion || (pending.ContainsKey(card) && pending[card] != target));
                                if (done && !pending.ContainsKey(card)) { card.Sync(fallback != null ? "硬件未响应，已切换软件调光" : "软件调光已生效", fallback ?? "", false); Remember(device); }
                            }
                            catch (Exception ex) { card.Sync("调光失败：" + ex.Message, ex.ToString(), true); }
                        }
                        else
                        {
                            try
                            {
                                int shade = device.Settings.Mode == ControlMode.混合 ? device.Settings.Software(item.Value) : 100;
                                bool done = await dimming.ApplySmooth(device, shade, preferences.Smooth, false, item.Value,
                                    () => exiting || version != operationVersion || pending.ContainsKey(card));
                                if (done && !pending.ContainsKey(card))
                                {
                                    card.Sync(shade < 100 ? "混合调光已生效 · 背光已验证" : "硬件亮度已验证 · " + device.Value + "%", device.LastChannel, false);
                                    Remember(device);
                                }
                            }
                            catch (Exception ex) { card.Sync("调光失败：" + ex.Message, ex.ToString(), true); }
                        }
                    }
                }
            }
            finally
            {
                io.Release(); writing = false; if (!exiting) UpdateButtons();
                SaveSettings();
            }
            if (refreshAgain && !exiting) { refreshAgain = false; await RefreshDevices(); }
            if (pending.Count > 0 && !exiting) debounce.Start();
        }
        internal void ResetDimming()
        {
            if (refreshing) return;
            operationVersion++;
            pending.Clear(); debounce.Stop(); dimming.Dispose();
            foreach (var card in Cards)
            {
                card.Device.Value = card.Device.UsingSoftware ? 100 : card.Device.Settings.Logical(card.Device.HardwareValue);
                card.Sync("已恢复画面 · 软件调光已清除", "", false); Remember(card.Device);
            }
            SaveSettings();
        }
        private void ShowDiagnostics()
        {
            string text = (hotkeyStatus ?? "") + Environment.NewLine + (settingsStore.Error ?? "") + Environment.NewLine + string.Join(Environment.NewLine, service.Diagnostics);
            foreach (var device in service.Devices) text += Environment.NewLine + Environment.NewLine + device.Name + Environment.NewLine + string.Join(Environment.NewLine, device.Attempts);
            using (var dialog = new DiagnosticsForm(Cards.ToArray(), text, UiScale)) { dialog.Icon = Icon; dialog.ShowDialog(this); }
        }
        internal async Task ExitAsync()
        {
            if (exiting) return;
            exiting = true; debounce.Stop(); pending.Clear(); dimming.Dispose(); Hide(); tray.Visible = false;
            await io.WaitAsync();
            try { await Task.Run(() => service.Dispose()); }
            finally { io.Release(); allowClose = true; Close(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed = true;
                dimming.Dispose(); if (debounce != null) debounce.Dispose();
                if (IsHandleCreated) for (int id = 1; id <= 5; id++) UnregisterHotKey(Handle, id);
                if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
                if (appIcon != null) appIcon.Dispose();
            }
            base.Dispose(disposing);
        }
        private static Icon CreateIcon()
        {
            using (var bitmap = new Bitmap(32, 32)) using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias; Theme.Box(graphics, new RectangleF(0, 0, 32, 32), 9, Theme.Accent, null); Theme.Sun(graphics, 16, 16, 10, Color.White);
                IntPtr handle = bitmap.GetHicon();
                try { using (var icon = System.Drawing.Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    }
}
