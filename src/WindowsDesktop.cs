using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScreenLight
{
    internal static class WindowsDesktop
    {
        internal sealed class Snapshot { internal byte[] Paths, Modes; internal uint PathCount, ModeCount; }
        private sealed class Entry { internal int Path, Source; internal string Id, Name, Device; internal bool Hdr, HdrSupported; }
        internal static Snapshot Capture()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                uint pc, mc; Check(GetDisplayConfigBufferSizes(2, out pc, out mc), "读取显示缓冲区");
                if (pc > 128 || mc > 1024) throw new InvalidOperationException("驱动返回了异常的显示配置数量");
                var result = new Snapshot { Paths = new byte[pc * 72], Modes = new byte[mc * 64], PathCount = pc, ModeCount = mc };
                int code = QueryDisplayConfig(2, ref result.PathCount, result.Paths, ref result.ModeCount, result.Modes, IntPtr.Zero);
                if (code == 122) continue;
                Check(code, "读取显示配置"); return result;
            }
            throw new InvalidOperationException("显示设备正在变化，请稍后重新检测");
        }
        private static void Check(int code, string action) { if (code != 0) throw new InvalidOperationException(action + "失败，Windows 错误码 " + code); }
        private static byte[] Info(byte[] paths, int offset, bool target, int type, int size)
        {
            var bytes = new byte[size]; Put(bytes, 0, type); Put(bytes, 4, size);
            Array.Copy(paths, offset + (target ? 20 : 0), bytes, 8, 12);
            Check(DisplayConfigGetDeviceInfo(bytes), "读取设备信息"); return bytes;
        }
        private static string Text(byte[] bytes, int offset, int length) { return Encoding.Unicode.GetString(bytes, offset, length).Split('\0')[0]; }
        private static int Get(byte[] bytes, int offset) { return BitConverter.ToInt32(bytes, offset); }
        private static void Put(byte[] bytes, int offset, int value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static List<Entry> Entries(Snapshot snapshot)
        {
            var entries = new List<Entry>();
            for (int i = 0; i < snapshot.PathCount; i++)
            {
                int p = i * 72, source = Get(snapshot.Paths, p + 12);
                if (source < 0 || source >= snapshot.ModeCount) throw new InvalidDataException("活动屏幕缺少源模式");
                byte[] target = Info(snapshot.Paths, p, true, 2, 420);
                var entry = new Entry { Path = p, Source = source * 64, Id = Text(target, 164, 256), Name = Text(target, 36, 128), Device = Text(Info(snapshot.Paths, p, false, 1, 84), 20, 64) };
                if (string.IsNullOrEmpty(entry.Id)) throw new InvalidDataException("显示器没有稳定设备路径");
                try { int color = Get(Info(snapshot.Paths, p, true, 9, 32), 20); entry.HdrSupported = (color & 1) != 0; entry.Hdr = (color & 2) != 0; }
                catch (InvalidOperationException) { }
                entries.Add(entry);
            }
            return entries;
        }
        internal static DesktopState Read(Snapshot snapshot = null)
        {
            if (snapshot == null) snapshot = Capture(); var state = new DesktopState();
            foreach (var entry in Entries(snapshot))
            {
                int s = entry.Source, p = entry.Path, denominator = Get(snapshot.Paths, p + 52);
                var screen = new DesktopScreen { Id = entry.Id, Device = entry.Device, Name = entry.Name.Length == 0 ? "显示器" : entry.Name,
                    Width = Get(snapshot.Modes, s + 16), Height = Get(snapshot.Modes, s + 20), X = Get(snapshot.Modes, s + 28), Y = Get(snapshot.Modes, s + 32),
                    Frequency = denominator == 0 ? 60 : (int)Math.Round(Get(snapshot.Paths, p + 48) / (double)denominator), Rotation = (Get(snapshot.Paths, p + 40) - 1) * 90,
                    HdrSupported = entry.HdrSupported, HdrEnabled = entry.Hdr };
                screen.Primary = screen.X == 0 && screen.Y == 0;
                var found = new HashSet<string>();
                for (int i = 0; i < 4096; i++)
                {
                    var mode = new DevMode { Size = 220 };
                    if (!EnumDisplaySettingsEx(entry.Device, i, ref mode, 0)) break;
                    if (mode.Bits < 24 || (mode.Flags & 2) != 0 || mode.Width < 1 || mode.Height < 1 || mode.Frequency < 2) continue;
                    string key = mode.Width + "/" + mode.Height + "/" + mode.Frequency;
                    if (found.Add(key)) screen.Modes.Add(new DesktopMode { Width = mode.Width, Height = mode.Height, Frequency = mode.Frequency });
                }
                if (!screen.Modes.Any(m => m.Width == screen.Width && m.Height == screen.Height && m.Frequency == screen.Frequency))
                    screen.Modes.Add(new DesktopMode { Width = screen.Width, Height = screen.Height, Frequency = screen.Frequency });
                state.Screens.Add(screen);
            }
            return state;
        }
        internal static void Apply(DesktopPlan plan)
        {
            var current = Read(); plan.Validate(current);
            if (plan.Topology != 0)
            {
                Check(SetDisplayConfig(0, null, 0, null, (uint)plan.Topology | 0x40), "验证投影模式");
                Check(SetDisplayConfig(0, null, 0, null, (uint)plan.Topology | 0x80), "切换投影模式"); return;
            }
            var snapshot = Capture(); var entries = Entries(snapshot);
            if (entries.Select(e => e.Device).Distinct().Count() != entries.Count) throw new InvalidOperationException("复制模式下请先切换到扩展模式，再修改逐屏配置");
            var primary = plan.Screens.Single(s => s.Primary);
            foreach (var screen in plan.Screens)
            {
                var entry = entries.SingleOrDefault(e => e.Id == screen.Id);
                if (entry == null) throw new InvalidOperationException("显示器已断开");
                Put(snapshot.Modes, entry.Source + 16, screen.Width); Put(snapshot.Modes, entry.Source + 20, screen.Height);
                Put(snapshot.Modes, entry.Source + 28, screen.X - primary.X); Put(snapshot.Modes, entry.Source + 32, screen.Y - primary.Y);
                Put(snapshot.Paths, entry.Path + 40, screen.Rotation / 90 + 1);
                // Let Windows choose a matching target timing; source modes are supplied explicitly.
                Put(snapshot.Paths, entry.Path + 32, -1); Put(snapshot.Paths, entry.Path + 48, screen.Frequency * 1000); Put(snapshot.Paths, entry.Path + 52, 1000);
            }
            Check(SetDisplayConfig(snapshot.PathCount, snapshot.Paths, snapshot.ModeCount, snapshot.Modes, 0x20 | 0x40 | 0x400), "验证显示模式");
            Check(SetDisplayConfig(snapshot.PathCount, snapshot.Paths, snapshot.ModeCount, snapshot.Modes, 0x20 | 0x80 | 0x400), "应用显示模式");
            foreach (var screen in plan.Screens)
            {
                var previous = current.Screens.Single(s => s.Id == screen.Id);
                if (screen.HdrEnabled != previous.HdrEnabled) SetHdr(screen.Id, screen.HdrEnabled);
            }
        }
        internal static void SetHdr(string id, bool enabled)
        {
            var snapshot = Capture(); var entry = Entries(snapshot).SingleOrDefault(e => e.Id == id);
            if (entry == null || !entry.HdrSupported) throw new InvalidOperationException("屏幕已断开或未开放 HDR 控制");
            var bytes = new byte[24]; Put(bytes, 0, 10); Put(bytes, 4, 24);
            Array.Copy(snapshot.Paths, entry.Path + 20, bytes, 8, 12); Put(bytes, 20, enabled ? 1 : 0);
            Check(DisplayConfigSetDeviceInfo(bytes), "设置 HDR");
        }
        internal static void Restore(Snapshot original, DesktopState colors)
        {
            var errors = new List<string>();
            try { Check(SetDisplayConfig(original.PathCount, original.Paths, original.ModeCount, original.Modes, 0x20 | 0x80), "恢复显示配置"); }
            catch (Exception ex) { errors.Add(ex.Message); }
            // A disconnected screen must not prevent recovery of the remaining screens' colors.
            foreach (var screen in colors.Screens.Where(s => s.HdrSupported))
                try { SetHdr(screen.Id, screen.HdrEnabled); } catch (Exception ex) { errors.Add(ex.Message); }
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
        }
        internal static void Persist()
        {
            var snapshot = Capture(); Check(SetDisplayConfig(snapshot.PathCount, snapshot.Paths, snapshot.ModeCount, snapshot.Modes, 0x20 | 0x80 | 0x200), "保存显示配置");
        }
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 220)]
        internal struct DevMode
        {
            [FieldOffset(0), MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
            [FieldOffset(68)] internal ushort Size;
            [FieldOffset(168)] internal int Bits;
            [FieldOffset(172)] internal int Width;
            [FieldOffset(176)] internal int Height;
            [FieldOffset(180)] internal int Flags;
            [FieldOffset(184)] internal int Frequency;
        }
        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [In, Out] byte[] pathArray, ref uint modes, [In, Out] byte[] modeArray, IntPtr topology);
        [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint paths, byte[] pathArray, uint modes, byte[] modeArray, uint flags);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] info);
        [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(byte[] info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsEx(string device, int number, ref DevMode mode, uint flags);
    }

    internal sealed class DesktopNativeSession : IDisposable
    {
        private readonly object gate = new object();
        private readonly DesktopTrial trial;
        private WindowsDesktop.Snapshot original;
        private DesktopState colors;
        private readonly Timer timer;
        internal DesktopNativeSession()
        {
            trial = new DesktopTrial(() => WindowsDesktop.Restore(original, colors), WindowsDesktop.Persist, () => DateTime.UtcNow);
            timer = new Timer(Tick, null, 500, Timeout.Infinite);
        }
        private void Tick(object ignored)
        {
            try { lock (gate) trial.Tick(); }
            catch { /* Keep the snapshot pending for retry after transient driver errors. */ }
            finally { try { timer.Change(500, Timeout.Infinite); } catch (ObjectDisposedException) { } }
        }
        internal string Request(string command, string payload)
        {
            if (command == "DESKTOP_READ") return WireXml.Pack(WindowsDesktop.Read());
            if (command == "DESKTOP_APPLY")
            {
                var plan = WireXml.Unpack<DesktopPlan>(payload); plan.Validate(WindowsDesktop.Read());
                string token;
                lock (gate)
                {
                    if (trial.Token != null) throw new InvalidOperationException("请先确认或恢复上一次配置");
                    original = WindowsDesktop.Capture(); colors = WindowsDesktop.Read(original); token = trial.Begin();
                }
                try
                {
                    WindowsDesktop.Apply(plan);
                    lock (gate) if (trial.Token == null) { WindowsDesktop.Restore(original, colors); throw new InvalidOperationException("配置耗时超过试用期限，已恢复"); }
                    return token + "|" + trial.Deadline.Ticks;
                }
                catch { lock (gate) trial.Revert(); throw; }
            }
            lock (gate)
            {
                if (command == "DESKTOP_CONFIRM") trial.Confirm(payload);
                else if (command == "DESKTOP_REVERT") trial.Revert();
                else throw new InvalidOperationException("未知显示命令");
            }
            return "OK";
        }
        public void Dispose()
        {
            timer.Dispose();
            Exception failure = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { lock (gate) trial.Revert(); return; }
                catch (Exception ex) { failure = ex; Thread.Sleep(500); }
            }
            throw new InvalidOperationException("显示配置恢复未获确认，请检查 Windows 显示设置", failure);
        }
    }
}
