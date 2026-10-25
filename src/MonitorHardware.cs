using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenLight
{
    internal static class MonitorHardware
    {
        private static readonly Dictionary<int, string> Names = new Dictionary<int, string> {
            { 0x10, "硬件亮度" }, { 0x12, "对比度" }, { 0x62, "显示器音量" }, { 0x8d, "显示器静音" }, { 0x60, "输入源" }, { 0xd6, "电源模式" } };
        internal static T WithPhysical<T>(string screen, Func<IntPtr, T> operation)
        {
            T result = default(T); bool found = false; Exception error = null;
            Native.MonitorEnum callback = delegate(IntPtr monitor, IntPtr hdc, ref Native.Rect rect, IntPtr data)
            {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (!Native.GetMonitorInfo(monitor, ref info) || info.Device != screen) return true;
                try
                {
                    uint count;
                    if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count) || count != 1) throw new InvalidOperationException("无法唯一匹配物理显示器；复制显示或虚拟显示可能不支持硬件控制");
                    var physical = new Native.PhysicalMonitor[1];
                    if (!Native.GetPhysicalMonitorsFromHMONITOR(monitor, 1, physical)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try { result = operation(physical[0].Handle); found = true; }
                    finally { Native.DestroyPhysicalMonitor(physical[0].Handle); }
                }
                catch (Exception ex) { error = ex; }
                return false;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero); GC.KeepAlive(callback);
            if (error != null) throw error;
            if (!found) throw new InvalidOperationException("物理显示器不可用，请确认显示器连接并启用 DDC/CI");
            return result;
        }
        private static string Capabilities(IntPtr handle)
        {
            uint length;
            if (!GetCapabilitiesStringLength(handle, out length) || length < 1 || length > 65535) return "";
            var bytes = new StringBuilder((int)length);
            return CapabilitiesRequestAndCapabilitiesReply(handle, bytes, length) ? bytes.ToString().ToLowerInvariant() : "";
        }
        private static MonitorFeature Read(IntPtr handle, int code)
        {
            if (!Names.ContainsKey(code)) throw new InvalidOperationException("不允许写入此 VCP 功能");
            uint min = 0, current, max;
            bool ok;
            if (code == 0x10) ok = Native.GetMonitorBrightness(handle, out min, out current, out max);
            else if (code == 0x12) ok = GetMonitorContrast(handle, out min, out current, out max);
            else ok = Native.GetVCPFeatureAndVCPFeatureReply(handle, (byte)code, IntPtr.Zero, out current, out max);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (min > 65535 || max > 65535 || current > 65535) throw new InvalidOperationException("设备返回了异常参数范围");
            return new MonitorFeature { Code = code, Name = Names[code], Minimum = (int)min, Maximum = (int)max, Current = (int)current };
        }
        private static List<int> Values(string capabilities, int code)
        {
            var match = Regex.Match(capabilities, @"(?:\s|\()" + code.ToString("x") + @"\s*\(([^)]*)\)");
            if (!match.Success) return new List<int>();
            var result = new List<int>();
            foreach (string token in Regex.Split(match.Groups[1].Value.Trim(), @"\s+"))
            {
                int value;
                if (int.TryParse(token, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 65535) result.Add(value);
            }
            return result.Distinct().ToList();
        }
        internal static MonitorFeatures Probe(string screen)
        {
            return WithPhysical(screen, handle => {
                var result = new MonitorFeatures(); string caps = Capabilities(handle);
                foreach (int code in Names.Keys)
                {
                    try
                    {
                        var feature = Read(handle, code);
                        if (code == 0x60 || code == 0xd6 || code == 0x8d)
                        { feature.Values = Values(caps, code); if (feature.Values.Count == 0) continue; }
                        else if (feature.Maximum <= feature.Minimum) continue;
                        result.Items.Add(feature);
                    }
                    catch (Exception) { /* Unsupported functions stay absent, never guessed. */ }
                }
                return result;
            });
        }
        internal static string Write(string screen, int code, int value)
        {
            return WithPhysical(screen, handle => {
                var actual = Read(handle, code);
                bool discrete = code == 0x60 || code == 0xd6 || code == 0x8d;
                if (discrete ? !Values(Capabilities(handle), code).Contains(value) : value < actual.Minimum || value > actual.Maximum)
                    throw new InvalidOperationException("参数超出显示器已确认的能力范围");
                bool ok = code == 0x10 ? Native.SetMonitorBrightness(handle, (uint)value) : code == 0x12 ? SetMonitorContrast(handle, (uint)value) : Native.SetVCPFeature(handle, (byte)code, (uint)value);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (code == 0x60 || code == 0xd6) return "指令已提交；输入源或电源切换后的状态无法读回确认";
                ThreadWait();
                var readback = Read(handle, code);
                if (readback.Current != value) throw new InvalidOperationException("接口已接受指令，但读回 " + readback.Current + "，与目标不一致");
                return "硬件读回已验证：" + value;
            });
        }
        private static void ThreadWait() { System.Threading.Thread.Sleep(180); }
        [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetMonitorContrast(IntPtr monitor, out uint min, out uint current, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] private static extern bool SetMonitorContrast(IntPtr monitor, uint value);
        [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);
        [DllImport("dxva2.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, StringBuilder text, uint length);
    }
}
