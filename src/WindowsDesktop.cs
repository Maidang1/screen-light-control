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
        private sealed class Entry { internal int Path, Source; internal string Id, Name, Device; internal bool Hdr, HdrSupported, Wcg, WcgSupported; internal int Bpc, Encoding = -1; }
        internal const int PathSize = 72, ModeSize = 64;
        private const uint AllPaths = 1, ActivePaths = 2;
        // DPI scale steps used by Windows' undocumented relative scaling query.
        internal static readonly int[] ScaleSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };
        internal static Snapshot Capture() { return Capture(ActivePaths); }
        private static Snapshot Capture(uint flags)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                uint pc, mc; Check(GetDisplayConfigBufferSizes(flags, out pc, out mc), "读取显示缓冲区");
                if (pc > 8192 || mc > 8192) throw new InvalidOperationException("驱动返回了异常的显示配置数量");
                var result = new Snapshot { Paths = new byte[pc * PathSize], Modes = new byte[mc * ModeSize], PathCount = pc, ModeCount = mc };
                int code = QueryDisplayConfig(flags, ref result.PathCount, result.Paths, ref result.ModeCount, result.Modes, IntPtr.Zero);
                if (code == 122) continue;
                Check(code, "读取显示配置"); return result;
            }
            throw new InvalidOperationException("显示设备正在变化，请稍后重新检测");
        }
        private static void Check(int code, string action) { if (code != 0) throw new InvalidOperationException(action + "失败，Windows 错误码 " + code); }
        private static byte[] Info(byte[] paths, int offset, bool target, int type, int size)
        {
            var bytes = Header(paths, offset, target, type, size);
            Check(DisplayConfigGetDeviceInfo(bytes), "读取设备信息"); return bytes;
        }
        private static byte[] Header(byte[] paths, int offset, bool target, int type, int size)
        {
            var bytes = new byte[size]; Put(bytes, 0, type); Put(bytes, 4, size);
            Array.Copy(paths, offset + (target ? 20 : 0), bytes, 8, 12); return bytes;
        }
        private static string Text(byte[] bytes, int offset, int length) { return Encoding.Unicode.GetString(bytes, offset, length).Split('\0')[0]; }
        private static int Get(byte[] bytes, int offset) { return BitConverter.ToInt32(bytes, offset); }
        private static void Put(byte[] bytes, int offset, int value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static string Luid(byte[] bytes, int offset) { return Get(bytes, offset + 4).ToString("X8") + ":" + Get(bytes, offset).ToString("X8"); }
        internal static string ConnectorName(int technology)
        {
            switch (technology)
            {
                case 0: return "VGA"; case 1: return "S-Video"; case 2: return "复合视频"; case 3: return "分量视频"; case 4: return "DVI";
                case 5: return "HDMI"; case 6: return "LVDS"; case 8: return "D-Jpn"; case 9: return "SDI"; case 10: return "DisplayPort";
                case 11: return "内置 DisplayPort"; case 12: return "UDI"; case 13: return "内置 UDI"; case 14: return "SDTV"; case 15: return "Miracast";
                case 16: return "间接有线（USB / 扩展坞）"; case 17: return "间接虚拟显示"; case 18: return "DisplayPort（USB 隧道）";
                case unchecked((int)0x80000000): return "内置屏幕";
                default: return "其他接口 " + technology;
            }
        }
        // Reads HDR / WCG state; Windows 11 24H2 exposes the split HDR and WCG states (type 15).
        private static void ReadColor(byte[] paths, int p, Entry entry)
        {
            try
            {
                var info = Info(paths, p, true, 15, 36); int v = Get(info, 20); entry.HdrSupported = (v & 16) != 0; entry.Hdr = (v & 32) != 0;
                entry.WcgSupported = (v & 64) != 0; entry.Wcg = (v & 128) != 0; entry.Encoding = Get(info, 24); entry.Bpc = Get(info, 28);
                return;
            }
            catch (InvalidOperationException) { }
            try
            {
                var info = Info(paths, p, true, 9, 32); int color = Get(info, 20);
                entry.HdrSupported = (color & 1) != 0; entry.Hdr = (color & 2) != 0; entry.Encoding = Get(info, 24); entry.Bpc = Get(info, 28);
            }
            catch (InvalidOperationException) { }
        }
        private static List<Entry> Entries(Snapshot snapshot)
        {
            var entries = new List<Entry>();
            for (int i = 0; i < snapshot.PathCount; i++)
            {
                int p = i * PathSize, source = Get(snapshot.Paths, p + 12);
                if (source < 0 || source >= snapshot.ModeCount) throw new InvalidDataException("活动屏幕缺少源模式");
                byte[] target = Info(snapshot.Paths, p, true, 2, 420);
                var entry = new Entry { Path = p, Source = source * ModeSize, Id = Text(target, 164, 256), Name = Text(target, 36, 128), Device = Text(Info(snapshot.Paths, p, false, 1, 84), 20, 64) };
                if (string.IsNullOrEmpty(entry.Id)) throw new InvalidDataException("显示器没有稳定设备路径");
                ReadColor(snapshot.Paths, p, entry);
                entries.Add(entry);
            }
            return entries;
        }
        // Adapter LUIDs are only valid during this boot; names are resolved for display only.
        private static DesktopAdapter Adapter(Dictionary<string, DesktopAdapter> cache, byte[] paths, int offset)
        {
            string id = Luid(paths, offset); DesktopAdapter adapter;
            if (cache.TryGetValue(id, out adapter)) return adapter;
            adapter = new DesktopAdapter { Id = id, Name = "显卡 " + id, Vendor = "未知" };
            try
            {
                var bytes = new byte[276]; Put(bytes, 0, 4); Put(bytes, 4, 276); Array.Copy(paths, offset, bytes, 8, 8);
                Check(DisplayConfigGetDeviceInfo(bytes), "读取显卡名称"); adapter.Path = Text(bytes, 20, 256);
                string upper = adapter.Path.ToUpperInvariant();
                for (uint i = 0; i < 64; i++)
                {
                    var display = new Native.DisplayDevice(); display.Size = Marshal.SizeOf(display);
                    if (!Native.EnumDisplayDevices(null, i, ref display, 0)) break;
                    if (!string.IsNullOrEmpty(display.DeviceId) && upper.Contains(display.DeviceId.ToUpperInvariant().Replace('\\', '#'))) { adapter.Name = display.DeviceString; break; }
                }
                adapter.Vendor = VendorName(upper);
            }
            catch (InvalidOperationException) { }
            cache[id] = adapter; return adapter;
        }
        internal static string VendorName(string path)
        {
            path = (path ?? "").ToUpperInvariant();
            if (path.Contains("VEN_10DE")) return "NVIDIA"; if (path.Contains("VEN_1002") || path.Contains("VEN_1022")) return "AMD";
            if (path.Contains("VEN_8086")) return "Intel"; if (path.Contains("VEN_1414")) return "Microsoft"; if (path.Contains("QCOM")) return "Qualcomm";
            return "其他";
        }
        internal static DesktopState Read(Snapshot snapshot = null)
        {
            if (snapshot == null) snapshot = Capture(); var state = new DesktopState();
            var adapters = new Dictionary<string, DesktopAdapter>();
            foreach (var entry in Entries(snapshot))
            {
                int s = entry.Source, p = entry.Path, denominator = Get(snapshot.Paths, p + 52);
                var adapter = Adapter(adapters, snapshot.Paths, p + 20);
                var screen = new DesktopScreen { Id = entry.Id, Device = entry.Device, Name = entry.Name.Length == 0 ? "显示器" : entry.Name,
                    Width = Get(snapshot.Modes, s + 16), Height = Get(snapshot.Modes, s + 20), X = Get(snapshot.Modes, s + 28), Y = Get(snapshot.Modes, s + 32),
                    RefreshNumerator = Get(snapshot.Paths, p + 48), RefreshDenominator = denominator,
                    Frequency = denominator == 0 ? 60 : (int)Math.Round(Get(snapshot.Paths, p + 48) / (double)denominator), Rotation = (Get(snapshot.Paths, p + 40) - 1) * 90,
                    HdrSupported = entry.HdrSupported, HdrEnabled = entry.Hdr, WcgSupported = entry.WcgSupported, WcgEnabled = entry.Wcg,
                    BitsPerColor = entry.Bpc, ColorEncoding = entry.Encoding, Adapter = adapter.Name, AdapterId = adapter.Id,
                    Connector = ConnectorName(Get(snapshot.Paths, p + 36)), Identity = MonitorIdentity.Key(entry.Id) };
                screen.Primary = screen.X == 0 && screen.Y == 0;
                // DEVMODE reports 59.94 Hz as 59; keep that convention so the current mode matches the list.
                var current = new DevMode { Size = 220 };
                if (EnumDisplaySettingsEx(entry.Device, -1, ref current, 0) && current.Frequency > 1) screen.Frequency = current.Frequency;
                try { var native = Info(snapshot.Paths, p, true, 3, 80); screen.NativeWidth = Get(native, 20); screen.NativeHeight = Get(native, 24); }
                catch (InvalidOperationException) { }
                if (entry.Hdr) try { screen.SdrWhiteLevel = (int)Math.Round(Get(Info(snapshot.Paths, p, true, 11, 24), 20) * 80 / 1000.0); } catch (InvalidOperationException) { }
                ReadScale(snapshot.Paths, p, screen);
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
            MonitorIdentity.Disambiguate(state.Screens, x => x.Identity, (x, v) => x.Identity = "PATH:" + BrightnessMath.DisplayId(x.Id));
            try { ReadTargets(state, adapters); }
            catch (Exception) { foreach (var screen in state.Screens) state.Targets.Add(new DesktopTarget { Id = screen.Id, Name = screen.Name, Identity = screen.Identity, Adapter = screen.Adapter, AdapterId = screen.AdapterId, Connector = screen.Connector, Device = screen.Device, Active = true }); }
            state.Adapters.AddRange(adapters.Values);
            return state;
        }
        private static void ReadScale(byte[] paths, int p, DesktopScreen screen)
        {
            try
            {
                var info = Info(paths, p, false, -3, 32);
                int min = Get(info, 20), cur = Get(info, 24), max = Get(info, 28), recommended = Math.Abs(min);
                cur = Math.Max(min, Math.Min(max, cur));
                if (recommended + max >= ScaleSteps.Length || recommended + min < 0) return;
                screen.ScaleRecommended = ScaleSteps[recommended]; screen.Scale = ScaleSteps[recommended + cur];
                for (int i = recommended + min; i <= recommended + max; i++) screen.ScaleOptions.Add(ScaleSteps[i]);
            }
            catch (InvalidOperationException) { }
        }
        private static void ReadTargets(DesktopState state, Dictionary<string, DesktopAdapter> adapters)
        {
            var all = Capture(AllPaths); var seen = new HashSet<string>();
            for (int i = 0; i < all.PathCount; i++)
            {
                int p = i * PathSize;
                if (Get(all.Paths, p + 60) == 0) continue;
                string key = Luid(all.Paths, p + 20) + "/" + Get(all.Paths, p + 28);
                // QDC_ALL_PATHS lists each output once per possible source; activity comes from the active screens.
                if (!seen.Add(key)) continue;
                byte[] name;
                try { name = Info(all.Paths, p, true, 2, 420); } catch (InvalidOperationException) { continue; }
                string id = Text(name, 164, 256); if (string.IsNullOrEmpty(id)) continue;
                var adapter = Adapter(adapters, all.Paths, p + 20);
                var live = state.Screens.FirstOrDefault(s => s.Id == id);
                state.Targets.Add(new DesktopTarget { Id = id, Name = Text(name, 36, 128).Length == 0 ? "显示器" : Text(name, 36, 128), Identity = MonitorIdentity.Key(id),
                    Adapter = adapter.Name, AdapterId = adapter.Id, Connector = ConnectorName(Get(all.Paths, p + 36)), Active = live != null, Device = live == null ? "" : live.Device });
            }
        }
        internal static void Apply(DesktopPlan plan)
        {
            var current = Read(); plan.Validate(current);
            if (plan.EnableTarget != null || plan.DisableTarget != null) { SetTarget(plan.EnableTarget ?? plan.DisableTarget, plan.EnableTarget != null); return; }
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
                var previous = current.Screens.Single(s => s.Id == screen.Id);
                if (entry == null) throw new InvalidOperationException("显示器已断开");
                Put(snapshot.Modes, entry.Source + 16, screen.Width); Put(snapshot.Modes, entry.Source + 20, screen.Height);
                Put(snapshot.Modes, entry.Source + 28, screen.X - primary.X); Put(snapshot.Modes, entry.Source + 32, screen.Y - primary.Y);
                Put(snapshot.Paths, entry.Path + 40, screen.Rotation / 90 + 1);
                bool sameSize = screen.Rotation % 180 == previous.Rotation % 180 ? screen.Width == previous.Width && screen.Height == previous.Height : screen.Width == previous.Height && screen.Height == previous.Width;
                if (sameSize && screen.Frequency == previous.Frequency) continue;
                // Keep the exact fractional timing (e.g. 60000/1001) when the integer rate is unchanged.
                bool keepRate = screen.Frequency == previous.Frequency && previous.RefreshDenominator > 0;
                Put(snapshot.Paths, entry.Path + 32, -1);
                Put(snapshot.Paths, entry.Path + 48, keepRate ? previous.RefreshNumerator : screen.Frequency * 1000);
                Put(snapshot.Paths, entry.Path + 52, keepRate ? previous.RefreshDenominator : 1000);
            }
            Check(SetDisplayConfig(snapshot.PathCount, snapshot.Paths, snapshot.ModeCount, snapshot.Modes, 0x20 | 0x40 | 0x400), "验证显示模式");
            Check(SetDisplayConfig(snapshot.PathCount, snapshot.Paths, snapshot.ModeCount, snapshot.Modes, 0x20 | 0x80 | 0x400), "应用显示模式");
            foreach (var screen in plan.Screens)
            {
                var previous = current.Screens.Single(s => s.Id == screen.Id);
                if (screen.HdrEnabled != previous.HdrEnabled) SetHdr(screen.Id, screen.HdrEnabled);
                if (screen.WcgEnabled != previous.WcgEnabled) SetWcg(screen.Id, screen.WcgEnabled);
                if (screen.HdrEnabled && screen.SdrWhiteLevel != previous.SdrWhiteLevel && screen.SdrWhiteLevel > 0) SetSdrWhiteLevel(screen.Id, screen.SdrWhiteLevel);
                if (screen.Scale != previous.Scale && screen.Scale > 0) SetScale(screen.Id, screen.Scale);
            }
        }
        // Activates an inactive output on a free source of its own GPU, or removes an active output.
        private static void SetTarget(string id, bool enable)
        {
            var all = Capture(AllPaths); var keep = new List<int>(); int add = -1;
            var targets = new Dictionary<int, string>();
            Func<int, string> targetId = p => { string v; if (!targets.TryGetValue(p, out v)) { try { v = Text(Info(all.Paths, p, true, 2, 420), 164, 256); } catch (InvalidOperationException) { v = ""; } targets[p] = v; } return v; };
            var usedSources = new HashSet<string>();
            for (int i = 0; i < all.PathCount; i++)
            {
                int p = i * PathSize; if ((Get(all.Paths, p + 68) & 1) == 0) continue;
                if (!enable && targetId(p) == id) continue;
                keep.Add(p); usedSources.Add(Luid(all.Paths, p) + "/" + Get(all.Paths, p + 8));
            }
            if (keep.Count == 0) throw new InvalidOperationException("至少需要保留一块启用的显示器");
            if (enable)
            {
                for (int i = 0; i < all.PathCount && add < 0; i++)
                {
                    int p = i * PathSize;
                    if ((Get(all.Paths, p + 68) & 1) != 0 || Get(all.Paths, p + 60) == 0 || usedSources.Contains(Luid(all.Paths, p) + "/" + Get(all.Paths, p + 8))) continue;
                    if (targetId(p) == id) add = p;
                }
                if (add < 0) throw new InvalidOperationException("该显卡没有空闲的显示输出源，请先停用同一显卡上的其他显示器");
            }
            var paths = new byte[(keep.Count + (add >= 0 ? 1 : 0)) * PathSize];
            for (int i = 0; i < keep.Count; i++) Array.Copy(all.Paths, keep[i], paths, i * PathSize, PathSize);
            if (add >= 0)
            {
                int q = keep.Count * PathSize; Array.Copy(all.Paths, add, paths, q, PathSize);
                Put(paths, q + 12, -1); Put(paths, q + 32, -1); Put(paths, q + 68, 1);
            }
            uint count = (uint)(paths.Length / PathSize);
            Check(SetDisplayConfig(count, paths, all.ModeCount, all.Modes, 0x20 | 0x40 | 0x400), enable ? "验证启用显示器" : "验证停用显示器");
            Check(SetDisplayConfig(count, paths, all.ModeCount, all.Modes, 0x20 | 0x80 | 0x400), enable ? "启用显示器" : "停用显示器");
        }
        private static Entry Find(Snapshot snapshot, string id, Func<Entry, bool> supported, string message)
        {
            var entry = Entries(snapshot).FirstOrDefault(e => e.Id == id);
            if (entry == null || !supported(entry)) throw new InvalidOperationException(message);
            return entry;
        }
        internal static void SetHdr(string id, bool enabled)
        {
            var snapshot = Capture(); var entry = Find(snapshot, id, e => e.HdrSupported, "屏幕已断开或未开放 HDR 控制");
            // Windows 11 24H2 separates HDR from WCG; older builds use the advanced color switch.
            var bytes = Header(snapshot.Paths, entry.Path, true, 16, 24); Put(bytes, 20, enabled ? 1 : 0);
            if (DisplayConfigSetDeviceInfo(bytes) == 0) return;
            bytes = Header(snapshot.Paths, entry.Path, true, 10, 24); Put(bytes, 20, enabled ? 1 : 0);
            Check(DisplayConfigSetDeviceInfo(bytes), "设置 HDR");
        }
        internal static void SetWcg(string id, bool enabled)
        {
            var snapshot = Capture(); var entry = Find(snapshot, id, e => e.WcgSupported, "屏幕已断开或未开放广色域控制");
            var bytes = Header(snapshot.Paths, entry.Path, true, 17, 24); Put(bytes, 20, enabled ? 1 : 0);
            Check(DisplayConfigSetDeviceInfo(bytes), "设置广色域（自动颜色管理）");
        }
        // Undocumented DISPLAYCONFIG_DEVICE_INFO_TYPE 0xFFFFFFEE used by the HDR "SDR content brightness" slider.
        internal static void SetSdrWhiteLevel(string id, int nits)
        {
            if (nits < 80 || nits > 480) throw new InvalidOperationException("SDR 内容亮度范围为 80–480 尼特");
            var snapshot = Capture(); var entry = Find(snapshot, id, e => e.Hdr, "SDR 内容亮度只在 HDR 开启时可用");
            var bytes = Header(snapshot.Paths, entry.Path, true, unchecked((int)0xFFFFFFEE), 28);
            Put(bytes, 20, nits * 1000 / 80); bytes[24] = 1;
            Check(DisplayConfigSetDeviceInfo(bytes), "设置 SDR 内容亮度");
        }
        // Undocumented DISPLAYCONFIG_DEVICE_INFO_TYPE -4: scale relative to the recommended value.
        internal static void SetScale(string id, int percent)
        {
            var snapshot = Capture(); var entry = Find(snapshot, id, e => true, "屏幕已断开");
            var info = Info(snapshot.Paths, entry.Path, false, -3, 32);
            int recommended = Math.Abs(Get(info, 20)), index = Array.IndexOf(ScaleSteps, percent);
            if (index < 0 || index - recommended < Get(info, 20) || index - recommended > Get(info, 28)) throw new InvalidOperationException("显示器不支持 " + percent + "% 缩放");
            var bytes = Header(snapshot.Paths, entry.Path, false, -4, 24); Put(bytes, 20, index - recommended);
            Check(DisplayConfigSetDeviceInfo(bytes), "设置缩放比例");
        }
        internal static void Restore(Snapshot original, DesktopState colors)
        {
            var errors = new List<string>();
            try
            {
                int code = SetDisplayConfig(original.PathCount, original.Paths, original.ModeCount, original.Modes, 0x20 | 0x80);
                if (code != 0) code = SetDisplayConfig(original.PathCount, original.Paths, original.ModeCount, original.Modes, 0x20 | 0x80 | 0x400);
                Check(code, "恢复显示配置");
            }
            catch (Exception ex) { errors.Add(ex.Message); }
            // A disconnected screen must not prevent recovery of the remaining screens' colors.
            DesktopState now = null;
            try { now = Read(); } catch (Exception ex) { errors.Add(ex.Message); }
            foreach (var screen in colors.Screens)
            {
                var live = now == null ? null : now.Screens.FirstOrDefault(s => s.Id == screen.Id);
                if (live == null) continue;
                if (screen.HdrSupported && live.HdrEnabled != screen.HdrEnabled) try { SetHdr(screen.Id, screen.HdrEnabled); } catch (Exception ex) { errors.Add(ex.Message); }
                if (screen.WcgSupported && live.WcgEnabled != screen.WcgEnabled) try { SetWcg(screen.Id, screen.WcgEnabled); } catch (Exception ex) { errors.Add(ex.Message); }
                if (screen.HdrEnabled && screen.SdrWhiteLevel >= 80 && live.SdrWhiteLevel != screen.SdrWhiteLevel) try { SetSdrWhiteLevel(screen.Id, screen.SdrWhiteLevel); } catch (Exception ex) { errors.Add(ex.Message); }
                if (screen.Scale > 0 && live.Scale != screen.Scale) try { SetScale(screen.Id, screen.Scale); } catch (Exception ex) { errors.Add(ex.Message); }
            }
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
        }
        // Adapter LUID and source ID of a GDI device name, as required by the color-profile API.
        internal static void SourceOf(string device, out long adapter, out uint source)
        {
            var snapshot = Capture();
            for (int i = 0; i < snapshot.PathCount; i++)
            {
                int p = i * PathSize;
                if (!string.Equals(Text(Info(snapshot.Paths, p, false, 1, 84), 20, 64), device, StringComparison.OrdinalIgnoreCase)) continue;
                adapter = BitConverter.ToInt64(snapshot.Paths, p); source = (uint)Get(snapshot.Paths, p + 8); return;
            }
            throw new InvalidOperationException("屏幕已断开");
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
