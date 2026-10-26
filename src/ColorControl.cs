using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenLight
{
    internal static class GammaMath
    {
        // Tanner Helland's black-body approximation, normalised so 6500K is neutral.
        internal static double[] Kelvin(int kelvin)
        {
            Func<int, double[]> raw = k =>
            {
                double t = k / 100.0;
                double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
                double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
                double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
                return new[] { Math.Max(0, Math.Min(255, r)), Math.Max(0, Math.Min(255, g)), Math.Max(0, Math.Min(255, b)) };
            };
            double[] value = raw(kelvin), neutral = raw(6500);
            return new[] { Math.Min(1, value[0] / neutral[0]), Math.Min(1, value[1] / neutral[1]), Math.Min(1, value[2] / neutral[2]) };
        }
        // 3 x 256 WORD entries in the layout SetDeviceGammaRamp expects (all red, all green, all blue).
        internal static ushort[] Ramp(ColorProfile profile)
        {
            var ramp = new ushort[768];
            double[] white = Kelvin(profile.Temperature), gain = { profile.Red / 100.0, profile.Green / 100.0, profile.Blue / 100.0 };
            for (int channel = 0; channel < 3; channel++)
                for (int i = 0; i < 256; i++)
                {
                    double level = Math.Pow(i / 255.0, 1 / profile.Gamma) * white[channel] * gain[channel] * profile.Brightness / 100.0;
                    ramp[channel * 256 + i] = (ushort)Math.Round(Math.Max(0, Math.Min(1, level)) * 65535);
                }
            return ramp;
        }
        internal static ushort[] Identity() { return Ramp(new ColorProfile()); }
    }

    // Gamma ramps are per GDI source and are reset by Windows on mode changes, driver resets
    // and by other color tools, so the manager re-applies them and restores identity on exit.
    internal sealed class ColorManager : IDisposable
    {
        private readonly Func<AppSettings> settings;
        private readonly Dictionary<string, ushort[]> applied = new Dictionary<string, ushort[]>(StringComparer.OrdinalIgnoreCase);
        private bool magnifier;
        internal readonly List<string> Messages = new List<string>();
        internal Func<IEnumerable<KeyValuePair<string, List<string>>>> Screens = () =>
            System.Windows.Forms.Screen.AllScreens.Select(s => new KeyValuePair<string, List<string>>(s.DeviceName, MonitorIdentity.MonitorPaths(s.DeviceName).Select(MonitorIdentity.Key).ToList()));
        internal ColorManager(Func<AppSettings> settings) { this.settings = settings; }
        internal bool Active { get { return applied.Count > 0 || magnifier; } }
        internal void ApplyAll()
        {
            Messages.Clear(); var prefs = settings();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var screen in Screens())
            {
                seen.Add(screen.Key);
                var profile = screen.Value.Select(k => prefs.ColorFor(k, false)).FirstOrDefault(p => p != null && p.Enabled && !p.IsNeutral);
                if (profile == null) { Reset(screen.Key); continue; }
                string error = Apply(screen.Key, GammaMath.Ramp(profile));
                if (error != null) Messages.Add(screen.Key + "：" + error);
            }
            foreach (string device in applied.Keys.Where(k => !seen.Contains(k)).ToArray()) applied.Remove(device);
            ApplyFilter(prefs.ColorFilter);
        }
        internal string Apply(string device, ushort[] ramp)
        {
            IntPtr dc = CreateDC(device, null, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return "无法打开显示设备上下文";
            try
            {
                if (!SetDeviceGammaRamp(dc, ramp))
                    return "Windows 拒绝了此伽马曲线（超出默认允许范围、HDR 已开启或驱动不支持）。可在颜色页解锁扩展伽马范围。";
                applied[device] = ramp; return null;
            }
            finally { DeleteDC(dc); }
        }
        internal void Reset(string device)
        {
            if (!applied.ContainsKey(device)) return;
            applied.Remove(device);
            IntPtr dc = CreateDC(device, null, null, IntPtr.Zero);
            if (dc != IntPtr.Zero) try { SetDeviceGammaRamp(dc, GammaMath.Identity()); } finally { DeleteDC(dc); }
        }
        // Called periodically: another program or a display reset may have replaced the ramp.
        internal void Reassert()
        {
            foreach (var entry in applied.ToArray())
            {
                IntPtr dc = CreateDC(entry.Key, null, null, IntPtr.Zero);
                if (dc == IntPtr.Zero) continue;
                try
                {
                    var current = new ushort[768];
                    if (GetDeviceGammaRamp(dc, current) && !current.Where((v, i) => Math.Abs(v - entry.Value[i]) > 512).Any()) continue;
                    SetDeviceGammaRamp(dc, entry.Value);
                }
                finally { DeleteDC(dc); }
            }
        }
        internal void ApplyFilter(int index)
        {
            if (index == 0 && !magnifier) return;
            try
            {
                if (!magnifier) { if (!MagInitialize()) throw new InvalidOperationException("放大镜接口初始化失败"); magnifier = true; }
                var effect = ColorFilters.Matrix(index);
                if (!MagSetFullscreenColorEffect(effect)) throw new InvalidOperationException("Windows 拒绝了全屏颜色滤镜");
                if (index == 0) { MagUninitialize(); magnifier = false; }
            }
            catch (DllNotFoundException ex) { Messages.Add("颜色滤镜：" + ex.Message); }
            catch (EntryPointNotFoundException ex) { Messages.Add("颜色滤镜：" + ex.Message); }
            catch (InvalidOperationException ex) { Messages.Add("颜色滤镜：" + ex.Message); }
        }
        public void Dispose()
        {
            foreach (string device in applied.Keys.ToArray()) Reset(device);
            if (magnifier) { try { MagSetFullscreenColorEffect(ColorFilters.Matrix(0)); MagUninitialize(); } catch (Exception) { } magnifier = false; }
        }
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string output, IntPtr init);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
        [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
        [DllImport("magnification.dll")] private static extern bool MagInitialize();
        [DllImport("magnification.dll")] private static extern bool MagUninitialize();
        [DllImport("magnification.dll")] private static extern bool MagSetFullscreenColorEffect(float[] effect);
    }

    // Full-screen 5x5 color matrices (row vector convention) for the Magnification API.
    internal static class ColorFilters
    {
        internal static readonly string[] Names = { "关闭", "灰度", "反色", "灰度反色", "复古暖色", "夜视红色", "红绿色弱增强", "降低蓝光" };
        internal static float[] Matrix(int index)
        {
            float[] m;
            switch (index)
            {
                case 1: m = new float[] { .3f, .3f, .3f, 0, 0, .59f, .59f, .59f, 0, 0, .11f, .11f, .11f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 }; break;
                case 2: m = new float[] { -1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 }; break;
                case 3: m = new float[] { -.3f, -.3f, -.3f, 0, 0, -.59f, -.59f, -.59f, 0, 0, -.11f, -.11f, -.11f, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 }; break;
                case 4: m = new float[] { .393f, .349f, .272f, 0, 0, .769f, .686f, .534f, 0, 0, .189f, .168f, .131f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 }; break;
                case 5: m = new float[] { .3f, 0, 0, 0, 0, .59f, 0, 0, 0, 0, .11f, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 }; break;
                // Shifts red/green differences into blue so they remain distinguishable.
                case 6: m = new float[] { 1, 0, -.7f, 0, 0, 0, 1, .7f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 }; break;
                case 7: m = new float[] { 1, 0, 0, 0, 0, 0, .9f, 0, 0, 0, 0, 0, .6f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 }; break;
                default: m = new float[] { 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 }; break;
            }
            return m;
        }
    }

    // Windows Night Light lives in an undocumented CloudStore blob. Only the known layout is
    // edited: inner length byte 0x13 (off) / 0x15 (on) and the "02 01" enabled field.
    internal static class NightLight
    {
        internal const string StateKey = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";
        internal static bool? Parse(byte[] data)
        {
            if (data == null || data.Length < 27 || data[0] != 0x43 || data[1] != 0x42 || data[2] != 1 || data[3] != 0) return null;
            if (data[19] != 0x43 || data[20] != 0x42 || data[21] != 1 || data[22] != 0 || data.Length < 19 + data[18]) return null;
            if (data[18] == 0x15 && data[23] == 2 && data[24] == 1) return true;
            if (data[18] == 0x13) return false;
            return null;
        }
        internal static byte[] Toggle(byte[] data, bool enable)
        {
            bool? current = Parse(data);
            if (current == null) throw new InvalidOperationException("夜间模式数据格式未知，未作修改");
            if (current.Value == enable) return data;
            var result = new List<byte>(data.Take(23));
            if (enable) { result.AddRange(new byte[] { 2, 1 }); result.AddRange(data.Skip(23)); }
            else result.AddRange(data.Skip(25));
            result[18] = (byte)(enable ? 0x15 : 0x13);
            // Bump the varint timestamp so the settings service notices the change.
            for (int i = 10; i < 15; i++) if (result[i] != 0xff) { result[i]++; break; }
            return result.ToArray();
        }
        internal static bool? Read()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(StateKey)) return key == null ? null : Parse(key.GetValue("Data") as byte[]);
        }
        internal static void Set(bool enable)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(StateKey, true))
            {
                if (key == null) throw new InvalidOperationException("尚未初始化夜间模式，请先在 Windows 设置中打开一次夜间模式页面");
                var original = key.GetValue("Data") as byte[];
                key.SetValue("Data", Toggle(original, enable), RegistryValueKind.Binary);
                if (Parse(key.GetValue("Data") as byte[]) != enable) { key.SetValue("Data", original, RegistryValueKind.Binary); throw new InvalidOperationException("夜间模式写入未生效，已恢复原数据"); }
            }
        }
    }

    internal static class GammaRange
    {
        private const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM";
        internal static bool Unlocked
        {
            get { try { using (var key = Registry.LocalMachine.OpenSubKey(Key)) return key != null && Convert.ToInt32(key.GetValue("GdiIcmGammaRange", 0)) >= 256; } catch (Exception) { return false; } }
        }
        // HKLM needs elevation; reg.exe runs through the UAC prompt instead of elevating this app.
        internal static void Unlock()
        {
            var start = new ProcessStartInfo("reg.exe", "add \"HKLM\\" + Key + "\" /v GdiIcmGammaRange /t REG_DWORD /d 256 /f") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using (var process = Process.Start(start)) { process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException("注册表写入失败：" + process.ExitCode); }
        }
    }

    // Settings > Graphics stores per-app GPU choices in this per-user key.
    internal static class GpuPreferences
    {
        private const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
        internal static readonly string[] Names = { "由 Windows 决定", "节能（集成显卡）", "高性能（独立显卡）" };
        internal sealed class Entry
        {
            internal string Application; internal int Preference;
            public override string ToString() { return Names[Preference] + " · " + Application; }
        }
        internal static int ParsePreference(string data)
        {
            foreach (string part in (data ?? "").Split(';'))
            {
                var pair = part.Split('='); int value;
                if (pair.Length == 2 && pair[0].Trim() == "GpuPreference" && int.TryParse(pair[1], out value) && value >= 0 && value <= 2) return value;
            }
            return 0;
        }
        internal static string Merge(string data, int preference)
        {
            var parts = (data ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Where(p => !p.Trim().StartsWith("GpuPreference=")).ToList();
            parts.Insert(0, "GpuPreference=" + preference);
            return string.Join(";", parts) + ";";
        }
        internal static List<Entry> List()
        {
            var result = new List<Entry>();
            using (var key = Registry.CurrentUser.OpenSubKey(Key))
                if (key != null)
                    foreach (string name in key.GetValueNames().Where(n => n != "DirectXUserGlobalSettings"))
                        result.Add(new Entry { Application = name, Preference = ParsePreference(key.GetValue(name) as string) });
            return result;
        }
        internal static void Set(string application, int preference)
        {
            if (preference < 0 || preference > 2) throw new ArgumentOutOfRangeException("preference");
            using (var key = Registry.CurrentUser.CreateSubKey(Key)) key.SetValue(application, Merge(key.GetValue(application) as string, preference), RegistryValueKind.String);
        }
        internal static void Remove(string application) { using (var key = Registry.CurrentUser.OpenSubKey(Key, true)) if (key != null) key.DeleteValue(application, false); }
    }

    internal static class MonitorPower
    {
        // DefWindowProc handles SC_MONITORPOWER for our own window; no broadcast to other apps.
        internal static void Off(IWin32Window window) { SendMessage(window.Handle, 0x0112, new IntPtr(0xF170), new IntPtr(2)); }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    }
}
