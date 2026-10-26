using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace ScreenLight
{
    public sealed class DesktopMode
    {
        public int Width, Height, Frequency;
        public override string ToString() { return Width + " × " + Height + " · " + Frequency + " Hz"; }
    }
    public sealed class DesktopScreen
    {
        public string Id, Name, Device, Identity, Adapter, AdapterId, Connector;
        public int Width, Height, Frequency, X, Y, Rotation;
        // Exact path timing (59.94 Hz is 60000/1001); Frequency follows the integer DEVMODE convention.
        public int RefreshNumerator, RefreshDenominator, NativeWidth, NativeHeight, BitsPerColor, ColorEncoding;
        public bool Primary, HdrSupported, HdrEnabled, WcgSupported, WcgEnabled;
        // SDR content brightness in HDR mode (nits, 80-480); 0 when unknown.
        public int SdrWhiteLevel;
        // Per-monitor DPI scaling in percent; empty options when the driver does not report them.
        public int Scale, ScaleRecommended;
        public List<int> ScaleOptions = new List<int>();
        public List<DesktopMode> Modes = new List<DesktopMode>();
        internal string ExactRefresh
        {
            get
            {
                if (RefreshDenominator <= 0 || RefreshNumerator <= 0) return Frequency + " Hz";
                return (RefreshNumerator / (double)RefreshDenominator).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " Hz";
            }
        }
        internal static string EncodingName(int value)
        {
            return value == 0 ? "RGB" : value == 1 ? "YCbCr 4:4:4" : value == 2 ? "YCbCr 4:2:2" : value == 3 ? "YCbCr 4:2:0" : value == 4 ? "Intensity" : "未知";
        }
        public override string ToString() { return Name + (Primary ? " · 主屏" : "") + " · " + Device + (string.IsNullOrEmpty(Adapter) ? "" : " · " + Adapter); }
    }
    // Every connected monitor output, including outputs Windows currently does not drive.
    public sealed class DesktopTarget
    {
        public string Id, Name, Identity, Adapter, AdapterId, Connector, Device;
        public bool Active;
        public override string ToString() { return (Active ? "● " : "○ ") + Name + " · " + Connector + " · " + Adapter + (Active ? " · " + Device : " · 未启用"); }
    }
    public sealed class DesktopAdapter
    {
        public string Id, Name, Vendor, Path;
        public override string ToString() { return Name + " · " + Vendor + " · LUID " + Id; }
    }
    public sealed class DesktopState
    {
        public List<DesktopScreen> Screens = new List<DesktopScreen>();
        public List<DesktopTarget> Targets = new List<DesktopTarget>();
        public List<DesktopAdapter> Adapters = new List<DesktopAdapter>();
    }
    public sealed class DesktopPlan
    {
        // 0 = explicit screen modes, 1/2/4/8 = internal/clone/extend/external.
        public int Topology;
        // Enabling or disabling a single output is a standalone operation like a projection switch.
        public string EnableTarget, DisableTarget;
        public List<DesktopScreen> Screens = new List<DesktopScreen>();
        internal void Validate(DesktopState state)
        {
            if (EnableTarget != null || DisableTarget != null)
            {
                if (Topology != 0 || Screens.Count != 0 || (EnableTarget != null && DisableTarget != null)) throw new InvalidDataException("启用或停用显示器不能与其他配置混合提交");
                var target = state.Targets.FirstOrDefault(t => t.Id == (EnableTarget ?? DisableTarget));
                if (target == null) throw new InvalidDataException("显示器输出已断开");
                if (EnableTarget != null && target.Active) throw new InvalidDataException("该显示器已经启用");
                if (DisableTarget != null && !target.Active) throw new InvalidDataException("该显示器尚未启用");
                if (DisableTarget != null && state.Targets.Count(t => t.Active) < 2) throw new InvalidDataException("至少需要保留一块启用的显示器");
                return;
            }
            if (Topology != 0)
            {
                if (!new[] { 1, 2, 4, 8 }.Contains(Topology) || Screens.Count != 0) throw new InvalidDataException("投影模式不能与逐屏配置混合提交");
                return;
            }
            if (Screens.Count != state.Screens.Count || Screens.Count == 0 || Screens.Select(s => s.Id).Distinct().Count() != Screens.Count)
                throw new InvalidDataException("显示器已变化，请重新检测，或选择适用于当前屏幕的方案");
            if (Screens.Count(s => s.Primary) != 1) throw new InvalidDataException("必须指定且只能指定一块主屏");
            foreach (var screen in Screens)
            {
                var current = state.Screens.SingleOrDefault(s => s.Id == screen.Id);
                if (current == null || screen.Device != current.Device) throw new InvalidDataException("方案中的显示器已断开");
                if (!new[] { 0, 90, 180, 270 }.Contains(screen.Rotation) || Math.Abs((long)screen.X) > 32767 || Math.Abs((long)screen.Y) > 32767)
                    throw new InvalidDataException("旋转或屏幕坐标无效");
                // EnumDisplaySettings returns modes in the current orientation.
                bool rotated = screen.Rotation % 180 != current.Rotation % 180;
                if (!current.Modes.Any(m => m.Width == (rotated ? screen.Height : screen.Width) && m.Height == (rotated ? screen.Width : screen.Height) && m.Frequency == screen.Frequency))
                    throw new InvalidDataException(current.Name + " 不支持所选分辨率与刷新率");
                if (!current.HdrSupported && screen.HdrEnabled != current.HdrEnabled) throw new InvalidDataException("当前屏幕未开放 HDR 控制");
                if (!current.WcgSupported && screen.WcgEnabled != current.WcgEnabled) throw new InvalidDataException("当前屏幕未开放广色域控制");
                if (screen.Scale != current.Scale && !current.ScaleOptions.Contains(screen.Scale)) throw new InvalidDataException(current.Name + " 不支持所选缩放比例");
                if (screen.SdrWhiteLevel != current.SdrWhiteLevel && (!screen.HdrEnabled || screen.SdrWhiteLevel < 80 || screen.SdrWhiteLevel > 480))
                    throw new InvalidDataException("SDR 内容亮度只能在 HDR 开启时设置为 80–480 尼特");
            }
            for (int i = 0; i < Screens.Count; i++) for (int j = i + 1; j < Screens.Count; j++)
            {
                var a = Screens[i]; var b = Screens[j];
                if (a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height)
                    throw new InvalidDataException("扩展桌面的屏幕区域不能重叠；复制显示请使用投影模式");
            }
            // Every screen must connect to the desktop; corner contact alone is insufficient.
            var connected = new HashSet<string> { Screens[0].Id };
            bool added;
            do
            {
                added = false;
                foreach (var a in Screens.Where(s => connected.Contains(s.Id)).ToArray())
                foreach (var b in Screens.Where(s => !connected.Contains(s.Id)))
                    if (((a.X + a.Width == b.X || b.X + b.Width == a.X) && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height) ||
                        ((a.Y + a.Height == b.Y || b.Y + b.Height == a.Y) && a.X < b.X + b.Width && b.X < a.X + a.Width))
                    { connected.Add(b.Id); added = true; }
            } while (added);
            if (connected.Count != Screens.Count) throw new InvalidDataException("屏幕之间必须有相接的边，避免鼠标无法跨屏");
        }
    }
    public sealed class MonitorFeature
    {
        public int Code, Minimum, Maximum, Current;
        public string Name;
        public bool Action;
        public List<int> Values = new List<int>();
        public override string ToString() { return Name; }
    }
    public sealed class MonitorFeatures { public List<MonitorFeature> Items = new List<MonitorFeature>(); }
    internal static class WireXml
    {
        internal static string Pack<T>(T value)
        {
            using (var stream = new MemoryStream())
            { new XmlSerializer(typeof(T)).Serialize(stream, value); return Convert.ToBase64String(stream.ToArray()); }
        }
        internal static T Unpack<T>(string value)
        {
            byte[] data = Convert.FromBase64String(value);
            if (data.Length > 4 * 1024 * 1024) throw new InvalidDataException("配置数据过大");
            using (var stream = new MemoryStream(data))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
        }
    }
    // A dedicated watchdog process owns temporary desktop configurations, not the brightness worker.
    // The transaction can be tested without touching a real display.
    internal sealed class DesktopTrial
    {
        private readonly Action restore, persist;
        private readonly Func<DateTime> now;
        private DateTime deadline;
        internal string Token { get; private set; }
        internal DateTime Deadline { get { return deadline; } }
        internal DesktopTrial(Action restore, Action persist, Func<DateTime> now)
        { this.restore = restore; this.persist = persist; this.now = now; }
        internal string Begin()
        {
            if (Token != null) throw new InvalidOperationException("请先确认或恢复上一次配置");
            Token = Guid.NewGuid().ToString("N"); deadline = now().AddSeconds(20); return Token;
        }
        internal void Confirm(string token)
        {
            if (Token == null || Token != token) throw new InvalidOperationException("确认已过期，请重新读取屏幕配置");
            if (now() >= deadline) { Revert(); throw new InvalidOperationException("试用时间已到，配置已恢复"); }
            persist(); Token = null;
        }
        internal void Revert() { if (Token == null) return; restore(); Token = null; }
        internal void Tick() { if (Token != null && now() >= deadline) Revert(); }
    }
}
