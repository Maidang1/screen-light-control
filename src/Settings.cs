using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using System.Globalization;

namespace ScreenLight
{
    public enum ControlMode { 自动, 软件, 混合 }
    public enum HotkeyTarget { 鼠标所在屏幕, 当前窗口所在屏幕, 所有屏幕 }
    public sealed class ChineseBooleanConverter : BooleanConverter
    {
        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        { return destinationType == typeof(string) && value is bool ? ((bool)value ? "开启" : "关闭") : base.ConvertTo(context, culture, value, destinationType); }
        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        { if (value is string) { if ((string)value == "开启") return true; if ((string)value == "关闭") return false; } return base.ConvertFrom(context, culture, value); }
    }

    public sealed class DisplaySettings
    {
        [Browsable(false)] public string Id { get; set; }
        // EDID manufacturer/product/serial; survives moving the cable to another port or GPU.
        [Browsable(false)] public string Identity { get; set; }
        [DisplayName("显示器名称")] public string Name { get; set; }
        [DisplayName("调光模式"), Description("混合模式：滑块低于 20% 时，在最低硬件背光上继续软件调暗。")] public ControlMode Mode { get; set; }
        [DisplayName("最低硬件亮度 (%)")] public int Minimum { get; set; }
        [DisplayName("最高硬件亮度 (%)")] public int Maximum { get; set; }
        [DisplayName("亮度曲线"), Description("范围 0.5–2.0；1 为线性，大于 1 时低亮度区域调节更细。")] public double Curve { get; set; }
        [Browsable(false)] public int LastValue { get; set; }
        [Browsable(false)] public bool HasValue { get; set; }
        public DisplaySettings() { Maximum = 100; Curve = 1; LastValue = 100; }
        internal void Validate()
        {
            if (Minimum < 0 || Maximum > 100 || Minimum >= Maximum || double.IsNaN(Curve) || Curve < .5 || Curve > 2 || !Enum.IsDefined(typeof(ControlMode), Mode))
                throw new InvalidDataException("每屏设置需满足：0 ≤ 最低亮度 < 最高亮度 ≤ 100，曲线为 0.5–2.0。");
            LastValue = BrightnessMath.Clamp(LastValue);
        }
        internal int Hardware(int logical)
        {
            double level = BrightnessMath.Clamp(logical) / 100.0;
            if (Mode == ControlMode.混合) level = Math.Max(0, (level - .2) / .8);
            return (int)Math.Round(Minimum + (Maximum - Minimum) * Math.Pow(level, Curve));
        }
        internal int Logical(int hardware)
        {
            double level = Math.Pow(Math.Max(0, Math.Min(1, (hardware - Minimum) / (double)(Maximum - Minimum))), 1 / Curve);
            if (Mode == ControlMode.混合) level = .2 + .8 * level;
            int lower = BrightnessMath.Clamp((int)Math.Floor(100 * level));
            int upper = BrightnessMath.Clamp((int)Math.Ceiling(100 * level));
            return Math.Abs(Hardware(lower) - hardware) <= Math.Abs(Hardware(upper) - hardware) ? lower : upper;
        }
        internal int Software(int logical) { return Mode == ControlMode.混合 ? Math.Max(10, Math.Min(100, logical * 5)) : Math.Max(10, BrightnessMath.Clamp(logical)); }
    }

    // Per-monitor gamma-ramp color adjustment, keyed by MonitorIdentity.Key.
    public sealed class ColorProfile
    {
        public string Key { get; set; }
        public bool Enabled { get; set; }
        public int Temperature { get; set; }
        public double Gamma { get; set; }
        public int Brightness { get; set; }
        public int Red { get; set; }
        public int Green { get; set; }
        public int Blue { get; set; }
        public ColorProfile() { Temperature = 6500; Gamma = 1; Brightness = 100; Red = Green = Blue = 100; }
        internal bool IsNeutral { get { return Temperature == 6500 && Math.Abs(Gamma - 1) < .001 && Brightness == 100 && Red == 100 && Green == 100 && Blue == 100; } }
        internal void Validate()
        {
            if (Temperature < 1200 || Temperature > 10000 || double.IsNaN(Gamma) || Gamma < .3 || Gamma > 3 || Brightness < 10 || Brightness > 100
                || Red < 0 || Red > 100 || Green < 0 || Green > 100 || Blue < 0 || Blue > 100)
                throw new InvalidDataException("颜色设置需满足：色温 1200–10000K，伽马 0.3–3.0，亮度 10–100%，RGB 增益 0–100%。");
        }
    }

    public sealed class AppSettings
    {
        [TypeConverter(typeof(ChineseBooleanConverter)), DisplayName("启用全局快捷键"), Description("Ctrl+Alt+↑/↓ 调节 5%；加 Shift 调节 1%；Ctrl+Alt+Home 恢复软件调光。")] public bool Hotkeys { get; set; }
        [DisplayName("快捷键控制范围")] public HotkeyTarget Target { get; set; }
        [TypeConverter(typeof(ChineseBooleanConverter)), DisplayName("所有屏幕滑块联动")] public bool Linked { get; set; }
        [TypeConverter(typeof(ChineseBooleanConverter)), DisplayName("平滑软件调光")] public bool Smooth { get; set; }
        [TypeConverter(typeof(ChineseBooleanConverter)), DisplayName("启动时恢复上次亮度"), Description("默认关闭。开启后，启动时会主动调节屏幕亮度。")] public bool Restore { get; set; }
        [Browsable(false)] public List<DisplaySettings> Displays { get; set; }
        [Browsable(false)] public List<ColorProfile> Colors { get; set; }
        // 0 = off; otherwise a ColorFilters preset applied through the Magnification API.
        [Browsable(false)] public int ColorFilter { get; set; }
        public AppSettings() { Hotkeys = true; Smooth = true; Displays = new List<DisplaySettings>(); Colors = new List<ColorProfile>(); }
        internal ColorProfile ColorFor(string identity, bool create)
        {
            var entry = Colors.FirstOrDefault(c => c.Key == identity);
            if (entry == null && create && identity != null) { entry = new ColorProfile { Key = identity }; Colors.Add(entry); }
            return entry;
        }
        internal DisplaySettings For(MonitorDevice device)
        {
            string id = string.IsNullOrEmpty(device.Instance) ? device.ScreenName + "|" + device.Name : device.Instance;
            string identity = device.Identity != null && device.Identity.StartsWith("EDID:") ? device.Identity : null;
            DisplaySettings entry = identity == null ? null : Displays.FirstOrDefault(p => p.Identity == identity);
            if (entry == null)
            {
                entry = Displays.FirstOrDefault(p => p.Id == id);
                // The same port now hosts a different monitor: do not inherit its calibration.
                if (entry != null && identity != null && entry.Identity != null && entry.Identity != identity) entry = null;
            }
            if (entry == null) { entry = new DisplaySettings { Id = id }; Displays.Add(entry); }
            if (identity != null) { entry.Identity = identity; entry.Id = id; }
            return entry;
        }
        internal void Validate()
        {
            if (!Enum.IsDefined(typeof(HotkeyTarget), Target)) throw new InvalidDataException("快捷键范围无效。");
            if (Displays == null) Displays = new List<DisplaySettings>();
            foreach (var entry in Displays) { if (entry == null) throw new InvalidDataException("显示器设置无效。"); entry.Validate(); }
            if (Colors == null) Colors = new List<ColorProfile>();
            foreach (var entry in Colors) { if (entry == null || string.IsNullOrEmpty(entry.Key)) throw new InvalidDataException("颜色设置无效。"); entry.Validate(); }
            if (ColorFilter < 0 || ColorFilter >= ColorFilters.Names.Length) throw new InvalidDataException("颜色滤镜无效。");
        }
    }

    internal sealed class SettingsStore
    {
        internal readonly string Path;
        internal string Error;
        internal SettingsStore(string path) { Path = path; }
        internal AppSettings Load()
        {
            if (Path == null || !File.Exists(Path)) return new AppSettings();
            try
            {
                using (var reader = XmlReader.Create(Path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                { var value = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(reader); value.Validate(); return value; }
            }
            catch (Exception ex) { Error = "无法读取设置，已使用默认值：" + ex.Message; return new AppSettings(); }
        }
        internal bool Save(AppSettings value)
        {
            if (Path == null) return true;
            try
            {
                value.Validate(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                string temporary = Path + ".tmp";
                using (var stream = File.Create(temporary)) new XmlSerializer(typeof(AppSettings)).Serialize(stream, value);
                if (File.Exists(Path)) File.Replace(temporary, Path, null); else File.Move(temporary, Path);
                Error = null; return true;
            }
            catch (Exception ex) { Error = "设置未保存：" + ex.Message; return false; }
        }
        internal static AppSettings Copy(AppSettings value)
        {
            var serializer = new XmlSerializer(typeof(AppSettings));
            using (var stream = new MemoryStream()) { serializer.Serialize(stream, value); stream.Position = 0; return (AppSettings)serializer.Deserialize(stream); }
        }
    }
}
