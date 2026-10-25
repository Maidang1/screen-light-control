using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenLight
{
    internal sealed class DisplayConsoleForm : Form
    {
        private readonly ComboBox screens = Combo(), modes = Combo(), rotation = Combo(), features = Combo(), values = Combo();
        private readonly NumericUpDown x = Number(-32767, 32767), y = Number(-32767, 32767), raw = Number(0, 65535);
        private readonly CheckBox primary = new CheckBox { Text = "设为主屏", AutoSize = true }, hdr = new CheckBox { Text = "HDR / 高级颜色", AutoSize = true };
        private readonly Label status = new Label { AutoSize = false, Height = 60, Dock = DockStyle.Bottom, Padding = new Padding(14), Text = "正在读取显示配置…" };
        private readonly Label hardwareStatus = new Label { AutoSize = true, MaximumSize = new Size(640, 0), Text = "硬件功能需要显示器和连接链路支持 DDC/CI。点击探测读取实际能力。" };
        private readonly FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 5, 10, 5) };
        private readonly Panel layoutPreview = new Panel { Width = 540, Height = 135, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly System.Windows.Forms.Timer countdown = new System.Windows.Forms.Timer { Interval = 250 };
        private HardwareConnection desktop;
        private DesktopState state = new DesktopState();
        private DesktopPlan draft;
        private DesktopScreen selected;
        private bool loading, busy, closing, closeRequested;
        private string token;
        private DateTime trialDeadline;
        private Button confirm, revert, hardwareApply;
        internal DisplayConsoleForm(bool readOnShow = true)
        {
            Text = "Screen Light · 显示控制台"; StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Microsoft YaHei UI", 9); BackColor = Theme.Background;
            ClientSize = new Size(820, 650); MinimumSize = new Size(700, 560);
            Controls.Add(tabs); Controls.Add(actions); Controls.Add(status);
            var display = Page("显示与布局");
            Row(display, "当前显示器", screens);
            Row(display, "布局预览", layoutPreview); layoutPreview.Paint += PaintLayout;
            Row(display, "分辨率与刷新率", modes);
            rotation.Items.AddRange(new object[] { "0° 横向", "90° 纵向", "180° 横向翻转", "270° 纵向翻转" });
            Row(display, "旋转", rotation); Row(display, "桌面坐标 X / Y", Pair(x, y)); Row(display, "", Pair(primary, hdr));
            var screenActions = new FlowLayoutPanel { AutoSize = true };
            Add(screenActions, "暂存此屏设置", Stage); Add(screenActions, "从左到右排列", Arrange);
            Row(display, "编辑布局", screenActions);
            var projection = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            foreach (var option in new[] { new { Name = "仅电脑屏幕", Value = 1 }, new { Name = "复制", Value = 2 }, new { Name = "扩展", Value = 4 }, new { Name = "仅第二屏幕", Value = 8 } })
            { int target = option.Value; Add(projection, option.Name, () => Run(() => Apply(new DesktopPlan { Topology = target }))); }
            Row(display, "投影模式", projection);
            Row(display, "说明", Hint("先暂存每屏设置，再试用。X / Y 为桌面物理像素坐标，主屏在应用时归零。\n分辨率、刷新率、旋转、布局、HDR 和投影模式均须在 20 秒内确认，否则恢复。\n复制模式下请先切到扩展模式，再编辑各屏。休眠、插拔或驱动限制可能使方案失效。"));
            var hardware = Page("显示器硬件");
            var probeRow = new FlowLayoutPanel { AutoSize = true }; Add(probeRow, "探测所选显示器能力", Probe);
            Row(hardware, "所选屏幕", Hint("与「显示与布局」中选择的显示器一致。软件调光继续由主面板管理。"));
            Row(hardware, "能力探测", probeRow); Row(hardware, "可用功能", features); Row(hardware, "连续参数（原始值）", raw); Row(hardware, "离散参数", values);
            var writeRow = new FlowLayoutPanel { AutoSize = true }; hardwareApply = Add(writeRow, "应用硬件参数", WriteFeature); hardwareApply.Enabled = false; Row(hardware, "", writeRow); Row(hardware, "结果", hardwareStatus);
            Row(hardware, "范围", Hint("只开放设备成功读出的亮度、对比度、显示器音量、静音、输入源与电源。\n输入源、静音、电源的选项来自设备能力字符串，不猜测支持情况。\n输入源和电源操作可能导致画面消失，不会自动写入方案或启动恢复。"));
            var profiles = Page("配置方案");
            var profileButtons = new FlowLayoutPanel { AutoSize = true };
            Add(profileButtons, "导出当前暂存方案", Export); Add(profileButtons, "导入方案并试用", Import); Row(profiles, "显示方案", profileButtons);
            Row(profiles, "说明", Hint("方案保存分辨率、刷新率、旋转、坐标、主屏与 HDR；按显示器设备路径匹配。\n导入后先校验当前设备与支持的模式，再进入 20 秒试用。\n方案包含显示器设备路径，用于设备匹配；分享前请留意这一信息。\n不包含输入源、电源或自动执行项。"));
            var system = Page("Windows 与显卡能力");
            foreach (var link in new[] {
                new { Name = "缩放、文本大小、多屏与显示识别", Target = "ms-settings:display" },
                new { Name = "高级显示、位深、动态刷新率与适配器属性", Target = "ms-settings:display-advanced" },
                new { Name = "HDR、自动 HDR 与校准", Target = "ms-settings:display-hdr" },
                new { Name = "夜间模式与计划", Target = "ms-settings:nightlight" },
                new { Name = "应用 GPU 首选项、硬件加速与可变刷新率", Target = "ms-settings:display-advancedgraphics" },
                new { Name = "颜色滤镜与辅助功能", Target = "ms-settings:easeofaccess-colorfilter" },
                new { Name = "电源、休眠与关闭屏幕时间", Target = "ms-settings:powersleep" },
                new { Name = "无线投影与连接", Target = "ms-settings:project" } })
            { string target = link.Target; var row = new FlowLayoutPanel { AutoSize = true }; Add(row, link.Name, () => Open(target)); Row(system, "系统设置", row); }
            var color = new FlowLayoutPanel { AutoSize = true }; Add(color, "ICC 颜色配置与校准", () => Open(Path.Combine(Environment.SystemDirectory, "colorcpl.exe"))); Row(system, "颜色管理", color);
            Row(system, "能力边界", Hint("Windows 未提供稳定公共接口或由驱动掌管的选项在系统中设置。\nNVIDIA / AMD / Intel 专属功能、HDCP、超频、固件与物理链路带宽由设备决定。\n此程序不安装驱动，不使用未公开接口，不承诺突破显示器或 Windows 的限制。"));
            Add(actions, "重新读取", Reload); Add(actions, "试用暂存配置", () => { Stage(); if (draft != null) Run(() => Apply(draft)); });
            confirm = Add(actions, "保留配置", Confirm); revert = Add(actions, "立即恢复", Revert); Add(actions, "关闭", Close);
            screens.SelectedIndexChanged += delegate { SelectScreen(); };
            features.SelectedIndexChanged += delegate { SelectFeature(); };
            countdown.Tick += delegate {
                if (token == null) return;
                int left = Math.Max(0, (int)Math.Ceiling((trialDeadline - DateTime.UtcNow).TotalSeconds));
                status.Text = "试用配置 · " + left + " 秒内点击「保留配置」，否则由独立工作进程恢复";
                if (left == 0 && !busy) Revert();
            };
            Shown += delegate { if (readOnShow) Reload(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (closing) return; e.Cancel = true;
                if (busy) { closeRequested = true; status.Text = "正在结束设备操作，随后恢复未确认的配置并关闭…"; return; }
                Run(() => { if (desktop != null) Call("DESKTOP_REVERT"); token = null; }, true);
            };
            UpdateActions();
        }
        private static ComboBox Combo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 }; }
        private static NumericUpDown Number(int min, int max) { return new NumericUpDown { Minimum = min, Maximum = max, Width = 150 }; }
        private static Control Pair(params Control[] controls) { var panel = new FlowLayoutPanel { AutoSize = true }; panel.Controls.AddRange(controls); return panel; }
        private static Label Hint(string text) { return new Label { Text = text, AutoSize = true, MaximumSize = new Size(580, 0), Margin = new Padding(3, 8, 3, 8) }; }
        private TableLayoutPanel Page(string title)
        {
            var page = new TabPage(title) { AutoScroll = true, BackColor = Theme.Background, Padding = new Padding(12) };
            var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); page.Controls.Add(table); tabs.TabPages.Add(page); return table;
        }
        private static void Row(TableLayoutPanel table, string title, Control control)
        {
            int row = table.RowCount++; table.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(3, 10, 3, 10) }, 0, row);
            control.Margin = new Padding(3, 5, 3, 5); table.Controls.Add(control, 1, row);
        }
        private static Button Add(Control parent, string title, Action action)
        { var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(86, 30) }; button.Click += delegate { action(); }; parent.Controls.Add(button); return button; }
        private string Call(string command)
        {
            if (desktop == null) desktop = new HardwareConnection(new ProcessStartInfo(Application.ExecutablePath, "--hardware-worker " + Process.GetCurrentProcess().Id), true);
            return desktop.Request(command, 15000);
        }
        private async void Run(Action action, bool closeAfter = false)
        {
            if (busy) return; busy = true; UpdateActions();
            try { await Task.Run(action); if (!IsDisposed && token == null) status.Text = "操作完成"; }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    status.Text = ex.Message + (token == null ? "" : "；未确认配置将由工作进程恢复");
                    if (closeAfter) MessageBox.Show(this, "自动恢复未获确认：" + ex.Message + "。请在 Windows 显示设置中检查配置。", "显示配置恢复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                busy = false;
                if (closeAfter) { closing = true; Close(); }
                else if (!IsDisposed) { UpdateActions(); if (closeRequested) { closeRequested = false; Close(); } }
            }
        }
        private void OnUi(Action action) { if (!IsDisposed) { if (InvokeRequired) Invoke(action); else action(); } }
        private void Reload() { Run(() => LoadState(WireXml.Unpack<DesktopState>(Call("DESKTOP_READ")))); }
        internal void LoadState(DesktopState value)
        {
            OnUi(() => {
                state = value; draft = new DesktopPlan { Screens = WireXml.Unpack<DesktopState>(WireXml.Pack(value)).Screens };
                screens.Items.Clear(); screens.Items.AddRange(state.Screens.ToArray()); if (screens.Items.Count > 0) screens.SelectedIndex = 0;
                status.Text = "已读取 " + state.Screens.Count + " 块屏幕；暂存编辑后点击试用。";
            });
        }
        private void PaintLayout(object sender, PaintEventArgs e)
        {
            if (draft == null || draft.Screens.Count == 0) return;
            var bounds = draft.Screens;
            double left = bounds.Min(s => s.X), top = bounds.Min(s => s.Y);
            double width = Math.Max(1, bounds.Max(s => (double)s.X + s.Width) - left), height = Math.Max(1, bounds.Max(s => (double)s.Y + s.Height) - top);
            double scale = Math.Min((layoutPreview.Width - 20) / width, (layoutPreview.Height - 20) / height);
            for (int i = 0; i < bounds.Count; i++)
            {
                var screen = bounds[i]; var rect = new RectangleF((float)(10 + (screen.X - left) * scale), (float)(10 + (screen.Y - top) * scale), (float)(screen.Width * scale), (float)(screen.Height * scale));
                using (var brush = new SolidBrush(selected != null && screen.Id == selected.Id ? Theme.Soft : Theme.Background)) e.Graphics.FillRectangle(brush, rect);
                using (var pen = new Pen(Theme.Accent)) e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                    e.Graphics.DrawString((i + 1) + (screen.Primary ? " · 主屏" : "") + "\n" + screen.Width + " × " + screen.Height, Font, Brushes.Black, rect, format);
            }
        }
        private void SelectScreen()
        {
            selected = screens.SelectedItem as DesktopScreen; if (selected == null) return;
            var edit = draft.Screens.Single(s => s.Id == selected.Id); loading = true;
            modes.Items.Clear(); modes.Items.AddRange(selected.Modes.OrderByDescending(m => m.Width).ThenByDescending(m => m.Frequency).ToArray());
            bool swapped = edit.Rotation % 180 != selected.Rotation % 180;
            modes.SelectedItem = selected.Modes.FirstOrDefault(m => m.Width == (swapped ? edit.Height : edit.Width) && m.Height == (swapped ? edit.Width : edit.Height) && m.Frequency == edit.Frequency);
            if (modes.SelectedIndex < 0 && modes.Items.Count > 0) modes.SelectedIndex = 0;
            rotation.SelectedIndex = edit.Rotation / 90; x.Value = Math.Max(x.Minimum, Math.Min(x.Maximum, edit.X)); y.Value = Math.Max(y.Minimum, Math.Min(y.Maximum, edit.Y));
            primary.Checked = edit.Primary; hdr.Checked = edit.HdrEnabled; hdr.Enabled = edit.HdrSupported;
            features.Items.Clear(); raw.Enabled = false; values.Enabled = false; if (hardwareApply != null) hardwareApply.Enabled = false; hardwareStatus.Text = selected.Name + " · 尚未探测此屏幕硬件能力"; loading = false; layoutPreview.Invalidate();
        }
        private void Stage()
        {
            if (loading || selected == null || draft == null || busy) return;
            var mode = modes.SelectedItem as DesktopMode; if (mode == null) return;
            var edit = draft.Screens.Single(s => s.Id == selected.Id);
            bool swapped = rotation.SelectedIndex % 2 != selected.Rotation / 90 % 2;
            edit.Width = swapped ? mode.Height : mode.Width; edit.Height = swapped ? mode.Width : mode.Height; edit.Frequency = mode.Frequency;
            edit.Rotation = rotation.SelectedIndex * 90; edit.X = (int)x.Value; edit.Y = (int)y.Value; edit.HdrEnabled = hdr.Checked;
            if (primary.Checked) foreach (var screen in draft.Screens) screen.Primary = screen.Id == edit.Id;
            layoutPreview.Invalidate();
            status.Text = "已暂存 " + selected.Name + "。可继续设置其他屏幕，然后试用整个配置。";
        }
        private void Arrange()
        {
            Stage(); if (draft == null) return; int left = 0;
            foreach (var screen in draft.Screens.OrderByDescending(s => s.Primary)) { screen.X = left; screen.Y = 0; left += screen.Width; }
            SelectScreen(); status.Text = "已按主屏开始从左到右排列；点击「试用暂存配置」应用。";
        }
        private void Apply(DesktopPlan value)
        {
            var safe = WireXml.Unpack<DesktopPlan>(WireXml.Pack(value)); safe.Validate(state);
            string[] pending = Call("DESKTOP_APPLY " + WireXml.Pack(safe)).Split('|');
            if (pending.Length != 2) throw new InvalidDataException("显示试用回复无效，请等待自动恢复");
            var deadline = new DateTime(long.Parse(pending[1]), DateTimeKind.Utc);
            OnUi(() => { token = pending[0]; trialDeadline = deadline; countdown.Start(); UpdateActions(); });
        }
        private void Confirm() { if (token == null) return; string pending = token; Run(() => { Call("DESKTOP_CONFIRM " + pending); OnUi(() => { token = null; countdown.Stop(); }); LoadState(WireXml.Unpack<DesktopState>(Call("DESKTOP_READ"))); }); }
        private void Revert() { Run(() => { Call("DESKTOP_REVERT"); OnUi(() => { token = null; countdown.Stop(); }); LoadState(WireXml.Unpack<DesktopState>(Call("DESKTOP_READ"))); }); }
        private void Probe()
        {
            if (selected == null || token != null) return; string screen = selected.Device;
            Run(() => {
                using (var worker = new HardwareConnection(new ProcessStartInfo(Application.ExecutablePath, "--hardware-worker " + Process.GetCurrentProcess().Id)))
                {
                    var result = WireXml.Unpack<MonitorFeatures>(worker.Request("FEATURE_PROBE " + Convert.ToBase64String(Encoding.UTF8.GetBytes(screen)), 12000));
                    OnUi(() => { features.Items.Clear(); features.Items.AddRange(result.Items.ToArray()); if (features.Items.Count > 0) features.SelectedIndex = 0; hardwareStatus.Text = result.Items.Count + " 项功能已成功读出；未列出的功能不可用。"; });
                }
            });
        }
        private void SelectFeature()
        {
            var feature = features.SelectedItem as MonitorFeature;
            if (feature == null) { raw.Enabled = false; values.Enabled = false; if (hardwareApply != null) hardwareApply.Enabled = false; return; }
            hardwareApply.Enabled = true;
            raw.Minimum = 0; raw.Maximum = Math.Max(feature.Maximum, feature.Current); raw.Minimum = feature.Minimum; raw.Value = Math.Max(raw.Minimum, Math.Min(raw.Maximum, feature.Current));
            values.Items.Clear(); values.Items.AddRange(feature.Values.Select(v => (object)FeatureValueName(feature.Code, v)).ToArray());
            values.SelectedIndex = feature.Values.IndexOf(feature.Current); raw.Enabled = feature.Values.Count == 0; values.Enabled = feature.Values.Count > 0;
        }
        private static string FeatureValueName(int code, int value)
        {
            string name = null;
            if (code == 0xd6) name = value == 1 ? "开机" : value == 2 ? "待机" : value == 3 ? "挂起" : value == 4 || value == 5 ? "关闭显示器" : null;
            if (code == 0x8d) name = value == 1 ? "静音" : value == 2 ? "取消静音" : null;
            if (code == 0x60) name = value == 0x11 ? "HDMI 1" : value == 0x12 ? "HDMI 2" : value == 0x0f ? "DisplayPort 1" : value == 0x10 ? "DisplayPort 2" : value == 1 ? "VGA 1" : value == 3 ? "DVI 1" : null;
            return (name == null ? "设备选项" : name) + " · 0x" + value.ToString("X2");
        }
        private void WriteFeature()
        {
            var feature = features.SelectedItem as MonitorFeature; if (feature == null || selected == null || token != null) return;
            if (feature.Values.Count > 0 && values.SelectedIndex < 0) { status.Text = "请先选择设备支持的参数"; return; }
            int value = feature.Values.Count > 0 ? feature.Values[values.SelectedIndex] : (int)raw.Value; string screen = selected.Device;
            if ((feature.Code == 0x60 || feature.Code == 0xd6) && MessageBox.Show(this, "切换输入源或电源可能让当前画面消失，需要用显示器实体按键恢复。继续？", "设备操作", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            Run(() => {
                using (var worker = new HardwareConnection(new ProcessStartInfo(Application.ExecutablePath, "--hardware-worker " + Process.GetCurrentProcess().Id)))
                {
                    string reply = worker.Request("FEATURE_WRITE " + Convert.ToBase64String(Encoding.UTF8.GetBytes(screen)) + " " + feature.Code + " " + value, 12000);
                    OnUi(() => { hardwareStatus.Text = Encoding.UTF8.GetString(Convert.FromBase64String(reply)); });
                }
            });
        }
        private void Export()
        {
            Stage(); if (draft == null) return;
            using (var dialog = new SaveFileDialog { Filter = "Screen Light 显示方案|*.screenlight", FileName = "显示方案.screenlight" })
                if (dialog.ShowDialog(this) == DialogResult.OK) try { File.WriteAllText(dialog.FileName, WireXml.Pack(draft)); status.Text = "显示方案已导出"; } catch (Exception ex) { status.Text = ex.Message; }
        }
        private void Import()
        {
            using (var dialog = new OpenFileDialog { Filter = "Screen Light 显示方案|*.screenlight" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try
                    {
                        if (new FileInfo(dialog.FileName).Length > 6 * 1024 * 1024) throw new InvalidDataException("方案文件过大");
                        var plan = WireXml.Unpack<DesktopPlan>(File.ReadAllText(dialog.FileName));
                        // Resolve GDI names again: device paths stay stable while DISPLAY numbers may change.
                        foreach (var screen in plan.Screens) { var live = state.Screens.SingleOrDefault(s => s.Id == screen.Id); if (live != null) screen.Device = live.Device; }
                        Run(() => Apply(plan));
                    }
                    catch (Exception ex) { status.Text = "方案无法读取：" + ex.Message; }
        }
        private void UpdateActions()
        {
            tabs.Enabled = !busy && token == null; actions.Enabled = !busy;
            if (confirm != null) confirm.Enabled = token != null; if (revert != null) revert.Enabled = token != null;
            // During a trial only confirmation, recovery and closing are available.
            foreach (Control button in actions.Controls) if (button != confirm && button != revert && button.Text != "关闭") button.Enabled = token == null && !busy;
        }
        private void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception ex) { status.Text = "无法打开设置：" + ex.Message; } }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { countdown.Dispose(); if (desktop != null) desktop.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
