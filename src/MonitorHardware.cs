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
            { 0x10, "硬件亮度" }, { 0x12, "对比度" }, { 0x62, "显示器音量" }, { 0x8d, "显示器静音" }, { 0x60, "输入源" }, { 0xd6, "电源模式" },
            { 0x14, "色温预设" }, { 0x16, "红色增益" }, { 0x18, "绿色增益" }, { 0x1a, "蓝色增益" }, { 0x6c, "红色黑电平" }, { 0x6e, "绿色黑电平" },
            { 0x70, "蓝色黑电平" }, { 0x87, "锐度" }, { 0xdc, "显示模式" }, { 0x04, "恢复出厂设置" }, { 0x05, "恢复亮度与对比度默认值" }, { 0x08, "恢复颜色默认值" } };
        private static readonly HashSet<int> Discrete = new HashSet<int> { 0x60, 0xd6, 0x8d, 0x14, 0xdc };
        // Write-only MCCS actions: listed in the capability string, never read back.
        private static readonly HashSet<int> Actions = new HashSet<int> { 0x04, 0x05, 0x08 };
        // monitorPath selects one monitor of a cloned HMONITOR by its device interface path.
        internal static T WithPhysical<T>(string screen, string monitorPath, Func<IntPtr, T> operation)
        {
            T result = default(T); bool found = false; Exception error = null;
            Native.MonitorEnum callback = delegate(IntPtr monitor, IntPtr hdc, ref Native.Rect rect, IntPtr data)
            {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (!Native.GetMonitorInfo(monitor, ref info) || info.Device != screen) return true;
                try
                {
                    uint count;
                    if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count) || count == 0 || count > 32) throw new InvalidOperationException("驱动未提供物理显示器；虚拟显示或扩展坞可能不支持硬件控制");
                    var physical = new Native.PhysicalMonitor[count];
                    if (!Native.GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try
                    {
                        int index = 0;
                        if (count > 1)
                        {
                            var paths = MonitorIdentity.MonitorPaths(screen);
                            index = monitorPath == null ? -1 : paths.FindIndex(p => BrightnessMath.DisplayId(p) == BrightnessMath.DisplayId(monitorPath));
                            if (paths.Count != count || index < 0) throw new InvalidOperationException("复制显示中的物理显示器无法与设备路径唯一对应，请先切换到扩展模式");
                        }
                        result = operation(physical[index].Handle); found = true;
                    }
                    finally { foreach (var item in physical) Native.DestroyPhysicalMonitor(item.Handle); }
                }
                catch (Exception ex) { error = ex; }
                return false;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero); GC.KeepAlive(callback);
            if (error != null) throw error;
            if (!found) throw new InvalidOperationException("物理显示器不可用，请确认显示器连接并启用 DDC/CI");
            return result;
        }
        // Top-level VCP codes of the "vcp(...)" capability section.
        internal static HashSet<int> Listed(string capabilities)
        {
            var result = new HashSet<int>();
            int start = capabilities.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return result;
            int depth = 1; var token = new StringBuilder();
            Action flush = () => { int code; if (depth == 1 && token.Length > 0 && int.TryParse(token.ToString(), System.Globalization.NumberStyles.HexNumber, null, out code)) result.Add(code); token.Clear(); };
            for (int i = start + 4; i < capabilities.Length && depth > 0; i++)
            {
                char c = capabilities[i];
                if (c == '(') { flush(); depth++; }
                else if (c == ')') { flush(); depth--; }
                else if (char.IsWhiteSpace(c)) flush();
                else if (depth == 1) token.Append(c);
            }
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
        internal static List<int> Values(string capabilities, int code)
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
            return Probe(screen, null);
        }
        internal static MonitorFeatures Probe(string screen, string monitorPath)
        {
            return WithPhysical(screen, monitorPath, handle => {
                var result = new MonitorFeatures(); string caps = Capabilities(handle); var listed = Listed(caps);
                foreach (int code in Names.Keys)
                {
                    try
                    {
                        if (Actions.Contains(code))
                        {
                            if (listed.Contains(code)) result.Items.Add(new MonitorFeature { Code = code, Name = Names[code], Action = true, Values = new List<int> { 1 } });
                            continue;
                        }
                        var feature = Read(handle, code);
                        if (Discrete.Contains(code))
                        { feature.Values = Values(caps, code); if (feature.Values.Count == 0) continue; }
                        else if (feature.Maximum <= feature.Minimum) continue;
                        result.Items.Add(feature);
                    }
                    catch (Exception) { /* Unsupported functions stay absent, never guessed. */ }
                }
                return result;
            });
        }
        internal static string Write(string screen, int code, int value) { return Write(screen, null, code, value); }
        internal static string Write(string screen, string monitorPath, int code, int value)
        {
            return WithPhysical(screen, monitorPath, handle => {
                if (Actions.Contains(code))
                {
                    if (value != 1 || !Listed(Capabilities(handle)).Contains(code)) throw new InvalidOperationException("显示器未声明此恢复操作");
                    if (!Native.SetVCPFeature(handle, (byte)code, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return "恢复指令已提交；显示器会自行重置相关设置";
                }
                if (!Names.ContainsKey(code)) throw new InvalidOperationException("不允许写入此 VCP 功能");
                var actual = Read(handle, code);
                bool discrete = Discrete.Contains(code);
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
