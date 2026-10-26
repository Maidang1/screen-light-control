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
        private readonly ComboBox screens = Combo(), modes = Combo(), rotation = Combo(), features = Combo(), values = Combo(), scale = Combo(160),
            vendorControls = Combo(), filter = Combo(), iccAvailable = Combo(), gpuChoice = Combo(220);
        private readonly NumericUpDown x = Number(-32767, 32767), y = Number(-32767, 32767), raw = Number(0, 65535), sdr = Number(80, 480), vendorValue = Number(-1000, 1000),
            temperature = Number(1200, 10000, 100), gamma = Decimal(.3m, 3m), colorBrightness = Number(10, 100), red = Number(0, 100), green = Number(0, 100), blue = Number(0, 100);
        private readonly CheckBox primary = new CheckBox { Text = "设为主屏", AutoSize = true }, hdr = new CheckBox { Text = "HDR", AutoSize = true },
            wcg = new CheckBox { Text = "广色域 / 自动颜色管理", AutoSize = true }, colorEnabled = new CheckBox { Text = "启用此显示器的伽马颜色调整", AutoSize = true },
            iccAdvanced = new CheckBox { Text = "作为高级颜色配置关联", AutoSize = true };
        private readonly ListBox targets = List(), iccAssociated = List(), gpuApps = List();
        private readonly Label status = new Label { AutoSize = false, Height = 60, Dock = DockStyle.Bottom, Padding = new Padding(14), Text = "正在读取显示配置…" };
        private readonly Label hardwareStatus = Hint("硬件功能需要显示器和连接链路支持 DDC/CI。点击探测读取实际能力。"), screenInfo = Hint(""), adapterInfo = Hint(""),
            vendorStatus = Hint("读取 NVIDIA（NVAPI）或 AMD（ADL）驱动提供的颜色控制。"), colorStatus = Hint(""), iccStatus = Hint(""), nightStatus = Hint(""), gpuStatus = Hint("");
        private readonly FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 5, 10, 5) };
        private readonly Panel layoutPreview = new Panel { Width = 540, Height = 135, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly System.Windows.Forms.Timer countdown = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly AppSettings preferences;
        private readonly Action save;
        private readonly ColorManager colors;
        private HardwareConnection desktop;
        private DesktopState state = new DesktopState();
        private DesktopPlan draft;
        private DesktopScreen selected;
        private bool loading, busy, closing, closeRequested;
        private string token;
        private DateTime trialDeadline;
        private Button confirm, revert, hardwareApply;
        internal DisplayConsoleForm(bool readOnShow = true, AppSettings preferences = null, Action save = null, ColorManager colors = null)
        {
            this.preferences = preferences ?? new AppSettings(); this.save = save ?? delegate { }; this.colors = colors;
            Text = "Screen Light · 显示控制台"; StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Microsoft YaHei UI", 9); BackColor = Theme.Background;
            ClientSize = new Size(860, 680); MinimumSize = new Size(720, 580);
            Controls.Add(tabs); Controls.Add(actions); Controls.Add(status);

            var display = Page("显示与布局");
            Row(display, "当前显示器", screens);
            Row(display, "显卡与接口", screenInfo);
            Row(display, "布局预览", layoutPreview); layoutPreview.Paint += PaintLayout;
            Row(display, "分辨率与刷新率", modes);
            rotation.Items.AddRange(new object[] { "0° 横向", "90° 纵向", "180° 横向翻转", "270° 纵向翻转" });
            Row(display, "旋转", rotation); Row(display, "桌面坐标 X / Y", Pair(x, y)); Row(display, "", Pair(primary, hdr, wcg));
            Row(display, "缩放比例", Pair(scale, Hint("通过未公开的 Windows 缩放接口设置，与系统设置一致")));
            Row(display, "SDR 内容亮度（尼特）", Pair(sdr, Hint("仅 HDR 开启时生效；Windows 默认 80–480 尼特")));
            var screenActions = new FlowLayoutPanel { AutoSize = true };
            Add(screenActions, "暂存此屏设置", Stage); Add(screenActions, "从左到右排列", Arrange);
            Row(display, "编辑布局", screenActions);
            var projection = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            foreach (var option in new[] { new { Name = "仅电脑屏幕", Value = 1 }, new { Name = "复制", Value = 2 }, new { Name = "扩展", Value = 4 }, new { Name = "仅第二屏幕", Value = 8 } })
            { int target = option.Value; Add(projection, option.Name, () => Run(() => Apply(new DesktopPlan { Topology = target }))); }
            Row(display, "投影模式", projection);
            Row(display, "说明", Hint("先暂存每屏设置，再试用。X / Y 为桌面物理像素坐标，主屏在应用时归零。\n分辨率、刷新率、旋转、布局、HDR、广色域、缩放、SDR 亮度和投影模式均须在 20 秒内确认，否则恢复。\n刷新率保持原有的精确时序（如 59.94 Hz）。复制模式下请先切到扩展模式，再编辑各屏。"));

            var outputs = Page("显示器与显卡");
            Row(outputs, "显卡", adapterInfo);
            targets.Height = 160; Row(outputs, "所有已连接输出", targets);
            var targetActions = new FlowLayoutPanel { AutoSize = true };
            Add(targetActions, "启用所选显示器", () => ToggleTarget(true)); Add(targetActions, "停用所选显示器", () => ToggleTarget(false));
            Row(outputs, "启用 / 停用", targetActions);
            Row(outputs, "说明", Hint("列出每块显卡上所有已连接的显示器，包括 Windows 当前未驱动的输出。\n启用时在显示器所在显卡上分配空闲输出源；显卡输出源不足时需先停用同卡的其他显示器。\n显示器的物理连接决定由哪块显卡输出；应用渲染所用显卡在「显卡偏好」页设置。"));

            var hardware = Page("显示器硬件");
            var probeRow = new FlowLayoutPanel { AutoSize = true }; Add(probeRow, "探测所选显示器能力", Probe);
            Row(hardware, "所选屏幕", Hint("与「显示与布局」中选择的显示器一致。复制模式下按设备路径匹配物理显示器。"));
            Row(hardware, "能力探测", probeRow); Row(hardware, "可用功能", features); Row(hardware, "连续参数（原始值）", raw); Row(hardware, "离散参数", values);
            var writeRow = new FlowLayoutPanel { AutoSize = true }; hardwareApply = Add(writeRow, "应用硬件参数", WriteFeature); hardwareApply.Enabled = false; Row(hardware, "", writeRow); Row(hardware, "结果", hardwareStatus);
            var vendorRow = new FlowLayoutPanel { AutoSize = true }; Add(vendorRow, "读取显卡驱动颜色", VendorProbe); Add(vendorRow, "应用", VendorWrite); Add(vendorRow, "恢复驱动默认", VendorDefault);
            Row(hardware, "显卡驱动颜色", vendorControls); Row(hardware, "数值", vendorValue); Row(hardware, "", vendorRow); Row(hardware, "驱动结果", vendorStatus);
            Row(hardware, "范围", Hint("DDC/CI：亮度、对比度、RGB 增益与黑电平、锐度、色温预设、显示模式、音量、静音、输入源、电源与恢复默认值，只开放设备成功读出或能力字符串列出的项目。\n显卡驱动：NVIDIA 数字振动与色调（NVAPI），AMD 亮度、对比度、饱和度、色调与色温（ADL）。Intel 显卡暂未提供接口。\n输入源、电源与恢复出厂操作可能导致画面消失，需要确认。"));
            vendorControls.SelectedIndexChanged += delegate { var c = vendorControls.SelectedItem as VendorControl; if (c == null) return; vendorValue.Minimum = Math.Min(c.Minimum, c.Current); vendorValue.Maximum = Math.Max(c.Maximum, c.Current); vendorValue.Value = c.Current; };

            var color = Page("颜色与夜间");
            Row(color, "伽马颜色", colorEnabled);
            Row(color, "色温 (K)", Pair(temperature, Hint("6500K 为中性；数值越低越暖")));
            Row(color, "伽马 / 亮度 (%)", Pair(gamma, colorBrightness));
            Row(color, "红 / 绿 / 蓝增益 (%)", Pair(red, green, blue));
            var colorRow = new FlowLayoutPanel { AutoSize = true }; Add(colorRow, "应用到所选显示器", ApplyColor); Add(colorRow, "恢复中性", ResetColor);
            Add(colorRow, "解锁扩展伽马范围", UnlockGamma); Row(color, "", colorRow); Row(color, "结果", colorStatus);
            filter.Items.AddRange(ColorFilters.Names); filter.SelectedIndex = Math.Max(0, Math.Min(ColorFilters.Names.Length - 1, this.preferences.ColorFilter));
            filter.SelectedIndexChanged += delegate { if (loading) return; this.preferences.ColorFilter = filter.SelectedIndex; this.save(); if (this.colors != null) this.colors.ApplyFilter(filter.SelectedIndex); colorStatus.Text = "全屏颜色滤镜：" + filter.SelectedItem; };
            Row(color, "全屏颜色滤镜", filter);
            var nightRow = new FlowLayoutPanel { AutoSize = true }; Add(nightRow, "开启夜间模式", () => SetNight(true)); Add(nightRow, "关闭夜间模式", () => SetNight(false));
            Row(color, "Windows 夜间模式", nightRow); Row(color, "", nightStatus);
            iccAssociated.Height = 90; Row(color, "已关联 ICC 配置", iccAssociated); Row(color, "已安装 ICC 配置", iccAvailable);
            var iccRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            Add(iccRow, "读取关联", IccList); Add(iccRow, "关联并设为默认", IccAdd); Add(iccRow, "设为默认", IccDefault); Add(iccRow, "移除所选关联", IccRemove); Add(iccRow, "安装 ICC 文件…", IccInstall);
            Row(color, "", Pair(iccRow, iccAdvanced)); Row(color, "", iccStatus);
            Row(color, "说明", Hint("伽马调整按显示器设备分别写入，程序运行期间自动重新应用，退出时恢复中性。HDR 开启时 Windows 会忽略伽马曲线。\n默认只允许较小的伽马偏移；解锁扩展范围需要管理员确认，写入 GdiIcmGammaRange。\n夜间模式使用 Windows 未公开的设置数据，仅在格式可识别时修改。颜色滤镜作用于所有显示器。"));

            var gpu = Page("显卡偏好");
            gpuApps.Height = 180; Row(gpu, "应用", gpuApps);
            gpuChoice.Items.AddRange(GpuPreferences.Names); gpuChoice.SelectedIndex = 2;
            var gpuRow = new FlowLayoutPanel { AutoSize = true }; Add(gpuRow, "添加程序…", GpuAdd); Add(gpuRow, "修改所选", GpuChange); Add(gpuRow, "移除所选", GpuRemove);
            Row(gpu, "首选显卡", gpuChoice); Row(gpu, "", gpuRow); Row(gpu, "", gpuStatus);
            Row(gpu, "说明", Hint("与 Windows「设置 > 显示 > 显卡」相同的每应用首选项，写入当前用户的 DirectX 设置；应用重新启动后生效。"));

            var profiles = Page("配置方案");
            var profileButtons = new FlowLayoutPanel { AutoSize = true };
            Add(profileButtons, "导出当前暂存方案", Export); Add(profileButtons, "导入方案并试用", Import); Row(profiles, "显示方案", profileButtons);
            Row(profiles, "说明", Hint("方案保存分辨率、刷新率、旋转、坐标、主屏、HDR、广色域、缩放和 SDR 亮度；先按显示器设备路径匹配，找不到时按 EDID 序列号匹配（换接口或换显卡后仍可使用）。\n导入后先校验当前设备与支持的模式，再进入 20 秒试用。\n方案包含显示器设备路径和序列号，分享前请留意。不包含输入源、电源或自动执行项。"));

            var system = Page("Windows 与显卡能力");
            foreach (var link in new[] {
                new { Name = "缩放、文本大小、多屏与显示识别", Target = "ms-settings:display" },
                new { Name = "高级显示、位深、动态刷新率与适配器属性", Target = "ms-settings:display-advanced" },
                new { Name = "HDR、自动 HDR 与校准", Target = "ms-settings:display-hdr" },
                new { Name = "夜间模式计划与强度", Target = "ms-settings:nightlight" },
                new { Name = "硬件加速 GPU 计划与可变刷新率", Target = "ms-settings:display-advancedgraphics" },
                new { Name = "Windows 颜色滤镜", Target = "ms-settings:easeofaccess-colorfilter" },
                new { Name = "电源、休眠与关闭屏幕时间", Target = "ms-settings:powersleep" },
                new { Name = "无线投影与连接", Target = "ms-settings:project" } })
            { string target = link.Target; var row = new FlowLayoutPanel { AutoSize = true }; Add(row, link.Name, () => Open(target)); Row(system, "系统设置", row); }
            var colorPanel = new FlowLayoutPanel { AutoSize = true }; Add(colorPanel, "ICC 颜色配置与校准", () => Open(Path.Combine(Environment.SystemDirectory, "colorcpl.exe"))); Row(system, "颜色管理", colorPanel);
            var power = new FlowLayoutPanel { AutoSize = true }; Add(power, "立即关闭所有显示器", () => MonitorPower.Off(this)); Row(system, "显示器电源", power);
            Row(system, "能力边界", Hint("本程序使用 Windows 公开接口，并在需要时使用未公开的缩放、SDR 亮度和夜间模式接口；这些接口可能随 Windows 更新变化，失败时不会修改设置。\n位深与色彩编码可在「显示与布局」中读取；设置位深、色彩格式、超频、HDCP、固件由显卡控制面板或显示器决定。"));

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
            Shown += delegate { if (readOnShow) { Reload(); RefreshNight(); RefreshGpu(); } };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (closing) return; e.Cancel = true;
                if (busy) { closeRequested = true; status.Text = "正在结束设备操作，随后恢复未确认的配置并关闭…"; return; }
                Run(() => { if (desktop != null) Call("DESKTOP_REVERT"); token = null; }, true);
            };
            UpdateActions();
        }
        private static ComboBox Combo(int width = 420) { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width }; }
        private static ListBox List() { return new ListBox { Width = 580, IntegralHeight = false, HorizontalScrollbar = true }; }
        private static NumericUpDown Number(int min, int max, int step = 1) { return new NumericUpDown { Minimum = min, Maximum = max, Width = 110, Increment = step }; }
        private static NumericUpDown Decimal(decimal min, decimal max) { return new NumericUpDown { Minimum = min, Maximum = max, Width = 110, DecimalPlaces = 2, Increment = .05m, Value = 1 }; }
        private static Control Pair(params Control[] controls) { var panel = new FlowLayoutPanel { AutoSize = true }; panel.Controls.AddRange(controls); return panel; }
        private static Label Hint(string text) { return new Label { Text = text, AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(3, 8, 3, 8) }; }
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
        // Driver SDK and color-profile calls use a disposable worker so a crash or hang only ends that worker.
        private static string Isolated(string command, int timeout = 12000)
        {
            using (var worker = new HardwareConnection(new ProcessStartInfo(Application.ExecutablePath, "--hardware-worker " + Process.GetCurrentProcess().Id)))
                return worker.Request(command, timeout);
        }
        private static DisplayReply Extra(string action, string screen, string text = null, int kind = 0, int value = 0)
        {
            var command = new DisplayCommand { Action = action, Screen = screen, Text = text, Kind = kind, Value = value };
            return WireXml.Unpack<DisplayReply>(Isolated("DISPLAY_EXTRA " + WireXml.Pack(command)));
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
                targets.Items.Clear(); targets.Items.AddRange(state.Targets.ToArray());
                adapterInfo.Text = state.Adapters.Count == 0 ? "未读取到显卡信息" : string.Join("\n", state.Adapters.Select(a => a.ToString() + " · " + state.Targets.Count(t => t.AdapterId == a.Id) + " 个已连接输出"));
                status.Text = "已读取 " + state.Screens.Count + " 块屏幕、" + state.Adapters.Count + " 块显卡；暂存编辑后点击试用。";
            });
        }
        private void PaintLayout(object sender, PaintEventArgs e)
        {
            if (draft == null || draft.Screens.Count == 0) return;
            var bounds = draft.Screens;
            double left = bounds.Min(s => s.X), top = bounds.Min(s => s.Y);
            double width = Math.Max(1, bounds.Max(s => (double)s.X + s.Width) - left), height = Math.Max(1, bounds.Max(s => (double)s.Y + s.Height) - top);
            double scaleFactor = Math.Min((layoutPreview.Width - 20) / width, (layoutPreview.Height - 20) / height);
            for (int i = 0; i < bounds.Count; i++)
            {
                var screen = bounds[i]; var rect = new RectangleF((float)(10 + (screen.X - left) * scaleFactor), (float)(10 + (screen.Y - top) * scaleFactor), (float)(screen.Width * scaleFactor), (float)(screen.Height * scaleFactor));
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
            primary.Checked = edit.Primary; hdr.Checked = edit.HdrEnabled; hdr.Enabled = edit.HdrSupported; wcg.Checked = edit.WcgEnabled; wcg.Enabled = edit.WcgSupported;
            scale.Items.Clear(); foreach (int option in selected.ScaleOptions) scale.Items.Add(option + "%" + (option == selected.ScaleRecommended ? "（推荐）" : ""));
            scale.Enabled = selected.ScaleOptions.Count > 0; if (scale.Enabled) scale.SelectedIndex = Math.Max(0, selected.ScaleOptions.IndexOf(edit.Scale));
            sdr.Enabled = edit.HdrEnabled && edit.SdrWhiteLevel > 0; sdr.Value = Math.Max(sdr.Minimum, Math.Min(sdr.Maximum, edit.SdrWhiteLevel > 0 ? edit.SdrWhiteLevel : 80));
            screenInfo.Text = (selected.Adapter ?? "未知显卡") + (string.IsNullOrEmpty(selected.Connector) ? "" : " · " + selected.Connector) + " · 当前 " + selected.Width + " × " + selected.Height + " @ " + selected.ExactRefresh
                + (selected.NativeWidth > 0 ? " · 原生 " + selected.NativeWidth + " × " + selected.NativeHeight : "")
                + (selected.BitsPerColor > 0 ? "\n" + selected.BitsPerColor + " bpc · " + DesktopScreen.EncodingName(selected.ColorEncoding) : "")
                + (selected.Identity != null && selected.Identity.StartsWith("EDID:") ? " · 序列号已识别" : " · 无唯一序列号，按接口识别");
            features.Items.Clear(); raw.Enabled = false; values.Enabled = false; if (hardwareApply != null) hardwareApply.Enabled = false; hardwareStatus.Text = selected.Name + " · 尚未探测此屏幕硬件能力";
            vendorControls.Items.Clear(); iccAssociated.Items.Clear(); iccAvailable.Items.Clear();
            var profile = preferences.ColorFor(selected.Identity, false) ?? new ColorProfile();
            colorEnabled.Checked = profile.Enabled; temperature.Value = profile.Temperature; gamma.Value = (decimal)profile.Gamma; colorBrightness.Value = profile.Brightness;
            red.Value = profile.Red; green.Value = profile.Green; blue.Value = profile.Blue;
            colorStatus.Text = colors == null ? "颜色调整由主程序管理，当前未连接" : GammaRange.Unlocked ? "已解锁扩展伽马范围" : "使用 Windows 默认伽马范围";
            loading = false; layoutPreview.Invalidate();
        }
        private void Stage()
        {
            if (loading || selected == null || draft == null || busy) return;
            var mode = modes.SelectedItem as DesktopMode; if (mode == null) return;
            var edit = draft.Screens.Single(s => s.Id == selected.Id);
            bool swapped = rotation.SelectedIndex % 2 != selected.Rotation / 90 % 2;
            edit.Width = swapped ? mode.Height : mode.Width; edit.Height = swapped ? mode.Width : mode.Height; edit.Frequency = mode.Frequency;
            edit.Rotation = rotation.SelectedIndex * 90; edit.X = (int)x.Value; edit.Y = (int)y.Value; edit.HdrEnabled = hdr.Checked; edit.WcgEnabled = wcg.Checked;
            if (scale.Enabled && scale.SelectedIndex >= 0) edit.Scale = selected.ScaleOptions[scale.SelectedIndex];
            if (sdr.Enabled) edit.SdrWhiteLevel = (int)sdr.Value;
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
        private void ToggleTarget(bool enable)
        {
            var target = targets.SelectedItem as DesktopTarget; if (target == null) { status.Text = "请先选择显示器输出"; return; }
            Run(() => Apply(enable ? new DesktopPlan { EnableTarget = target.Id } : new DesktopPlan { DisableTarget = target.Id }));
        }
        private void Confirm() { if (token == null) return; string pending = token; Run(() => { Call("DESKTOP_CONFIRM " + pending); OnUi(() => { token = null; countdown.Stop(); }); LoadState(WireXml.Unpack<DesktopState>(Call("DESKTOP_READ"))); }); }
        private void Revert() { Run(() => { Call("DESKTOP_REVERT"); OnUi(() => { token = null; countdown.Stop(); }); LoadState(WireXml.Unpack<DesktopState>(Call("DESKTOP_READ"))); }); }
        private void Probe()
        {
            if (selected == null || token != null) return; string screen = selected.Device, monitor = selected.Id;
            Run(() => {
                var result = WireXml.Unpack<MonitorFeatures>(Isolated("FEATURE_PROBE " + HardwareWorker.Encode(screen) + " " + HardwareWorker.Encode(monitor)));
                OnUi(() => { features.Items.Clear(); features.Items.AddRange(result.Items.ToArray()); if (features.Items.Count > 0) features.SelectedIndex = 0; hardwareStatus.Text = result.Items.Count + " 项功能已成功读出；未列出的功能不可用。"; });
            });
        }
        private void SelectFeature()
        {
            var feature = features.SelectedItem as MonitorFeature;
            if (feature == null) { raw.Enabled = false; values.Enabled = false; if (hardwareApply != null) hardwareApply.Enabled = false; return; }
            hardwareApply.Enabled = true;
            raw.Minimum = 0; raw.Maximum = Math.Max(feature.Maximum, feature.Current); raw.Minimum = feature.Minimum; raw.Value = Math.Max(raw.Minimum, Math.Min(raw.Maximum, feature.Current));
            values.Items.Clear(); values.Items.AddRange(feature.Values.Select(v => (object)FeatureValueName(feature.Code, v)).ToArray());
            values.SelectedIndex = feature.Action ? 0 : feature.Values.IndexOf(feature.Current); raw.Enabled = feature.Values.Count == 0; values.Enabled = feature.Values.Count > 0;
        }
        internal static string FeatureValueName(int code, int value)
        {
            string name = null;
            if (code == 0x04 || code == 0x05 || code == 0x08) name = "执行";
            if (code == 0xd6) name = value == 1 ? "开机" : value == 2 ? "待机" : value == 3 ? "挂起" : value == 4 || value == 5 ? "关闭显示器" : null;
            if (code == 0x8d) name = value == 1 ? "静音" : value == 2 ? "取消静音" : null;
            if (code == 0x60) name = value == 0x11 ? "HDMI 1" : value == 0x12 ? "HDMI 2" : value == 0x0f ? "DisplayPort 1" : value == 0x10 ? "DisplayPort 2" : value == 1 ? "VGA 1" : value == 3 ? "DVI 1" : value == 0x1b ? "USB-C" : null;
            if (code == 0x14) name = value == 1 ? "sRGB" : value == 2 ? "原生" : value == 3 ? "4000K" : value == 4 ? "5000K" : value == 5 ? "6500K" : value == 6 ? "7500K" : value == 7 ? "8200K" : value == 8 ? "9300K" : value == 9 ? "10000K" : value == 0x0a ? "11500K" : value == 0x0b ? "用户 1" : value == 0x0c ? "用户 2" : value == 0x0d ? "用户 3" : null;
            if (code == 0xdc) name = value == 0 ? "标准" : value == 1 ? "办公" : value == 2 ? "影院" : value == 3 ? "游戏" : value == 4 ? "体育" : value == 5 ? "动态" : null;
            return (name == null ? "设备选项" : name) + " · 0x" + value.ToString("X2");
        }
        private void WriteFeature()
        {
            var feature = features.SelectedItem as MonitorFeature; if (feature == null || selected == null || token != null) return;
            if (feature.Values.Count > 0 && values.SelectedIndex < 0) { status.Text = "请先选择设备支持的参数"; return; }
            int value = feature.Values.Count > 0 ? feature.Values[values.SelectedIndex] : (int)raw.Value; string screen = selected.Device, monitor = selected.Id;
            if ((feature.Code == 0x60 || feature.Code == 0xd6) && MessageBox.Show(this, "切换输入源或电源可能让当前画面消失，需要用显示器实体按键恢复。继续？", "设备操作", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (feature.Action && MessageBox.Show(this, "「" + feature.Name + "」会把显示器相关设置恢复为出厂值，无法撤销。继续？", "设备操作", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            Run(() => {
                string reply = Isolated("FEATURE_WRITE " + HardwareWorker.Encode(screen) + " " + feature.Code + " " + value + " " + HardwareWorker.Encode(monitor));
                OnUi(() => { hardwareStatus.Text = Encoding.UTF8.GetString(Convert.FromBase64String(reply)); });
            });
        }
        private void VendorProbe()
        {
            if (selected == null) return; string screen = selected.Device;
            Run(() => { var reply = Extra("VENDOR_PROBE", screen); OnUi(() => { vendorControls.Items.Clear(); vendorControls.Items.AddRange(reply.Controls.ToArray()); if (vendorControls.Items.Count > 0) vendorControls.SelectedIndex = 0; vendorStatus.Text = reply.Message; }); });
        }
        private void VendorWrite(int? fixedValue)
        {
            var control = vendorControls.SelectedItem as VendorControl; if (control == null || selected == null) { vendorStatus.Text = "请先读取显卡驱动颜色"; return; }
            string screen = selected.Device; int value = fixedValue ?? (int)vendorValue.Value;
            Run(() => { var reply = Extra("VENDOR_SET", screen, null, control.Kind, value); OnUi(() => { control.Current = value; vendorValue.Value = Math.Max(vendorValue.Minimum, Math.Min(vendorValue.Maximum, value)); vendorStatus.Text = reply.Message; }); });
        }
        private void VendorWrite() { VendorWrite(null); }
        private void VendorDefault() { var control = vendorControls.SelectedItem as VendorControl; if (control != null) VendorWrite(control.Default); }
        private ColorProfile EditedColor()
        {
            return new ColorProfile { Key = selected.Identity, Enabled = colorEnabled.Checked, Temperature = (int)temperature.Value, Gamma = (double)gamma.Value,
                Brightness = (int)colorBrightness.Value, Red = (int)red.Value, Green = (int)green.Value, Blue = (int)blue.Value };
        }
        private void ApplyColor()
        {
            if (selected == null || selected.Identity == null) { colorStatus.Text = "无法识别所选显示器"; return; }
            var edited = EditedColor();
            try { edited.Validate(); } catch (InvalidDataException ex) { colorStatus.Text = ex.Message; return; }
            preferences.Colors.RemoveAll(c => c.Key == edited.Key); preferences.Colors.Add(edited); save();
            if (colors == null) { colorStatus.Text = "已保存；主程序运行时应用"; return; }
            colors.ApplyAll();
            colorStatus.Text = colors.Messages.Count > 0 ? string.Join("\n", colors.Messages) : edited.Enabled ? "伽马颜色已应用到 " + selected.Name : "已停用此显示器的伽马调整";
        }
        private void ResetColor()
        {
            if (selected == null) return; loading = true;
            temperature.Value = 6500; gamma.Value = 1; colorBrightness.Value = 100; red.Value = green.Value = blue.Value = 100; loading = false; ApplyColor();
        }
        private void UnlockGamma()
        {
            try { GammaRange.Unlock(); colorStatus.Text = "已解锁扩展伽马范围；注销或重启后对所有程序生效"; }
            catch (Exception ex) { colorStatus.Text = "未解锁：" + ex.Message; }
        }
        private void RefreshNight()
        {
            try { bool? value = NightLight.Read(); nightStatus.Text = value == null ? "夜间模式数据不可识别或尚未初始化" : value.Value ? "夜间模式：已开启" : "夜间模式：已关闭"; }
            catch (Exception ex) { nightStatus.Text = ex.Message; }
        }
        private void SetNight(bool enable)
        {
            try { NightLight.Set(enable); RefreshNight(); }
            catch (Exception ex) { nightStatus.Text = ex.Message; }
        }
        private void IccList()
        {
            if (selected == null) return; string screen = selected.Device;
            Run(() => {
                var reply = Extra("ICC_LIST", screen);
                OnUi(() => {
                    iccAssociated.Items.Clear(); iccAvailable.Items.Clear();
                    foreach (string item in reply.Items)
                        if (item.StartsWith("可用:")) iccAvailable.Items.Add(item.Substring(3));
                        else iccAssociated.Items.Add(item + (string.Equals(Path.GetFileName(item), Path.GetFileName(reply.Default ?? ""), StringComparison.OrdinalIgnoreCase) ? "（默认）" : ""));
                    if (iccAvailable.Items.Count > 0) iccAvailable.SelectedIndex = 0;
                    iccStatus.Text = iccAssociated.Items.Count + " 个关联配置；默认：" + (reply.Default ?? "系统默认");
                });
            });
        }
        private void IccChange(string action, string profile, int value = 0)
        {
            if (selected == null || string.IsNullOrEmpty(profile)) { iccStatus.Text = "请先选择配置文件"; return; }
            string screen = selected.Device; profile = Path.GetFileName(profile.Replace("（默认）", ""));
            Run(() => { var reply = Extra(action, screen, profile, 0, value); OnUi(() => iccStatus.Text = reply.Message); });
        }
        private void IccAdd() { IccChange("ICC_ADD", iccAvailable.SelectedItem as string, iccAdvanced.Checked ? 1 : 0); }
        private void IccDefault() { IccChange("ICC_DEFAULT", (iccAssociated.SelectedItem as string) ?? (iccAvailable.SelectedItem as string)); }
        private void IccRemove() { IccChange("ICC_REMOVE", iccAssociated.SelectedItem as string); }
        private void IccInstall()
        {
            using (var dialog = new OpenFileDialog { Filter = "ICC 颜色配置|*.icc;*.icm" })
                if (dialog.ShowDialog(this) == DialogResult.OK) { string path = dialog.FileName; Run(() => { var reply = Extra("ICC_INSTALL", null, path); OnUi(() => iccStatus.Text = reply.Message); }); }
        }
        private void RefreshGpu()
        {
            try { gpuApps.Items.Clear(); gpuApps.Items.AddRange(GpuPreferences.List().ToArray()); gpuStatus.Text = gpuApps.Items.Count + " 个程序设置了首选显卡"; }
            catch (Exception ex) { gpuStatus.Text = ex.Message; }
        }
        private void GpuAdd()
        {
            using (var dialog = new OpenFileDialog { Filter = "程序|*.exe" })
                if (dialog.ShowDialog(this) == DialogResult.OK) try { GpuPreferences.Set(dialog.FileName, gpuChoice.SelectedIndex); RefreshGpu(); } catch (Exception ex) { gpuStatus.Text = ex.Message; }
        }
        private void GpuChange()
        {
            var entry = gpuApps.SelectedItem as GpuPreferences.Entry; if (entry == null) { gpuStatus.Text = "请先选择程序"; return; }
            try { GpuPreferences.Set(entry.Application, gpuChoice.SelectedIndex); RefreshGpu(); } catch (Exception ex) { gpuStatus.Text = ex.Message; }
        }
        private void GpuRemove()
        {
            var entry = gpuApps.SelectedItem as GpuPreferences.Entry; if (entry == null) { gpuStatus.Text = "请先选择程序"; return; }
            try { GpuPreferences.Remove(entry.Application); RefreshGpu(); } catch (Exception ex) { gpuStatus.Text = ex.Message; }
        }
        private void Export()
        {
            Stage(); if (draft == null) return;
            using (var dialog = new SaveFileDialog { Filter = "Screen Light 显示方案|*.screenlight", FileName = "显示方案.screenlight" })
                if (dialog.ShowDialog(this) == DialogResult.OK) try { File.WriteAllText(dialog.FileName, WireXml.Pack(draft)); status.Text = "显示方案已导出"; } catch (Exception ex) { status.Text = ex.Message; }
        }
        // Device paths change when a monitor moves to another port or GPU; fall back to a unique EDID match.
        internal static void Resolve(DesktopPlan plan, DesktopState live)
        {
            foreach (var screen in plan.Screens)
            {
                var match = live.Screens.SingleOrDefault(s => s.Id == screen.Id);
                if (match == null && screen.Identity != null && screen.Identity.StartsWith("EDID:"))
                {
                    var candidates = live.Screens.Where(s => s.Identity == screen.Identity).ToArray();
                    if (candidates.Length == 1 && !plan.Screens.Any(other => other != screen && other.Id == candidates[0].Id)) match = candidates[0];
                }
                if (match != null) { screen.Id = match.Id; screen.Device = match.Device; }
            }
        }
        private void Import()
        {
            using (var dialog = new OpenFileDialog { Filter = "Screen Light 显示方案|*.screenlight" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try
                    {
                        if (new FileInfo(dialog.FileName).Length > 6 * 1024 * 1024) throw new InvalidDataException("方案文件过大");
                        var plan = WireXml.Unpack<DesktopPlan>(File.ReadAllText(dialog.FileName));
                        Resolve(plan, state);
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
