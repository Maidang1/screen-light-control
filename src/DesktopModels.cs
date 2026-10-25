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
        public string Id, Name, Device;
        public int Width, Height, Frequency, X, Y, Rotation;
        public bool Primary, HdrSupported, HdrEnabled;
        public List<DesktopMode> Modes = new List<DesktopMode>();
        public override string ToString() { return Name + (Primary ? " · 主屏" : "") + " · " + Device; }
    }
    public sealed class DesktopState
    {
        public List<DesktopScreen> Screens = new List<DesktopScreen>();
    }
    public sealed class DesktopPlan
    {
        // 0 = explicit screen modes, 1/2/4/8 = internal/clone/extend/external.
        public int Topology;
        public List<DesktopScreen> Screens = new List<DesktopScreen>();
        internal void Validate(DesktopState state)
        {
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
