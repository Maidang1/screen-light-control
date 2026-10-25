using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenLight
{
    internal static class BrightnessMath
    {
        internal static int Clamp(int value) { return Math.Max(0, Math.Min(100, value)); }
        internal static uint ToRaw(int percent, uint min, uint max)
        {
            if (max <= min) throw new ArgumentException("无效的亮度范围");
            return min + (uint)Math.Round((max - min) * (Clamp(percent) / 100.0));
        }
        internal static int ToPercent(uint value, uint min, uint max)
        {
            if (max <= min) throw new ArgumentException("无效的亮度范围");
            value = Math.Max(min, Math.Min(max, value));
            return Clamp((int)Math.Round(((double)value - min) * 100 / (max - min)));
        }
        internal static string DisplayId(string value)
        {
            string id = value.Replace(@"\\?\", "").Replace('#', '\\').ToUpperInvariant();
            int guid = id.IndexOf(@"\{"); if (guid >= 0) id = id.Substring(0, guid);
            int suffix = id.LastIndexOf('_'), number;
            if (suffix > 0 && int.TryParse(id.Substring(suffix + 1), out number)) id = id.Substring(0, suffix);
            return id;
        }
    }
    internal interface IBrightnessChannel
    {
        string Name { get; }
        int Read();
        void Write(int percent);
    }
    internal sealed class PhysicalChannel : IBrightnessChannel
    {
        private readonly IntPtr handle;
        private readonly bool vcp;
        private uint minimum, maximum;
        public string Name { get { return vcp ? "DDC/CI · VCP 0x10" : "Windows Monitor API"; } }
        internal PhysicalChannel(IntPtr handle, bool vcp) { this.handle = handle; this.vcp = vcp; }
        public int Read()
        {
            uint current; bool ok;
            if (vcp) { minimum = 0; ok = Native.GetVCPFeatureAndVCPFeatureReply(handle, 0x10, IntPtr.Zero, out current, out maximum); }
            else ok = Native.GetMonitorBrightness(handle, out minimum, out current, out maximum);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            return BrightnessMath.ToPercent(current, minimum, maximum);
        }
        public void Write(int percent)
        {
            uint raw = BrightnessMath.ToRaw(percent, minimum, maximum);
            bool ok = vcp ? Native.SetVCPFeature(handle, 0x10, raw) : Native.SetMonitorBrightness(handle, raw);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    internal sealed class WmiChannel : IBrightnessChannel
    {
        private readonly string methodPath, readPath;
        public string Name { get { return "Windows 系统亮度"; } }
        internal WmiChannel(string methodPath, string readPath) { this.methodPath = methodPath; this.readPath = readPath; }
        public int Read()
        {
            using (var monitor = new ManagementObject(@"root\wmi", readPath, null)) { monitor.Get(); return Convert.ToInt32(monitor["CurrentBrightness"]); }
        }
        public void Write(int percent)
        {
            using (var method = new ManagementObject(@"root\wmi", methodPath, null))
            {
                object result = method.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)BrightnessMath.Clamp(percent) });
                if (result == null || Convert.ToUInt32(result) != 0) throw new InvalidOperationException("系统拒绝设置亮度：" + result);
            }
        }
    }
    internal sealed class MonitorDevice : IDisposable
    {
        internal string Name, ScreenName, Instance, Error, LastChannel;
        internal Rectangle Bounds;
        internal bool Primary, UsingSoftware;
        internal DisplaySettings Settings = new DisplaySettings();
        internal int Value = 100, HardwareValue = 100;
        internal int? LastRequestedValue;
        internal readonly List<IBrightnessChannel> Channels = new List<IBrightnessChannel>();
        internal IntPtr Handle;
        internal bool HasPhysicalHandle;
        internal bool Supported { get { return Channels.Count > 0; } }
        internal Action<int> Wait = Thread.Sleep;
        internal readonly List<string> Attempts = new List<string>();
        internal void Set(int percent)
        {
            percent = BrightnessMath.Clamp(percent); Attempts.Clear();
            foreach (var channel in Channels)
            {
                try
                {
                    channel.Write(percent);
                    int actual = HardwareValue;
                    foreach (int delay in new int[] { 120, 240, 480 })
                    {
                        Wait(delay); actual = channel.Read(); HardwareValue = actual;
                        if (actual == percent)
                        {
                            Value = actual; LastChannel = channel.Name; Error = null;
                            Attempts.Add(channel.Name + ": requested=" + percent + ", readback=" + actual + " (verified)"); return;
                        }
                    }
                    Attempts.Add(channel.Name + ": requested=" + percent + ", readback=" + actual + " (mismatch)");
                }
                catch (Exception ex) { Attempts.Add(channel.Name + ": " + ex.Message); }
            }
            Value = HardwareValue;
            Error = Channels.Count == 0 ? "当前屏幕未提供硬件亮度接口" : "硬件未响应亮度设置（读回 " + HardwareValue + "%）";
            throw new InvalidOperationException(Error);
        }
        public void Dispose()
        {
            Channels.Clear();
            if (HasPhysicalHandle) { Native.DestroyPhysicalMonitor(Handle); HasPhysicalHandle = false; Handle = IntPtr.Zero; }
        }
    }
    internal sealed class NativeMonitorService : IDisposable
    {
        internal readonly List<MonitorDevice> Devices = new List<MonitorDevice>();
        internal readonly List<string> Diagnostics = new List<string>();
        internal void Discover(string screenName = null)
        {
            Dispose(); Diagnostics.Clear();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wmiDevices = new Dictionary<string, MonitorDevice>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorID WHERE Active = TRUE"))
                using (var results = search.Get())
                    foreach (ManagementObject item in results) using (item)
                    {
                        var bytes = item["UserFriendlyName"] as ushort[];
                        string name = bytes == null ? "" : new string(bytes.Where(b => b != 0).Select(b => (char)b).ToArray());
                        string id = BrightnessMath.DisplayId(Convert.ToString(item["InstanceName"]));
                        if (name.Length > 0) names[id] = (id.StartsWith(@"DISPLAY\AOC") ? "AOC " : "") + name;
                    }
            }
            catch (Exception ex) { Diagnostics.Add("显示器名称：" + ex.Message); }
            try
            {
                var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"))
                using (var results = search.Get())
                    foreach (ManagementObject item in results) using (item) paths[Convert.ToString(item["InstanceName"])] = item.Path.RelativePath;
                using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE"))
                using (var results = search.Get())
                    foreach (ManagementObject item in results) using (item)
                    {
                        string instance = Convert.ToString(item["InstanceName"]), path;
                        if (!paths.TryGetValue(instance, out path)) continue;
                        var device = new MonitorDevice { Instance = instance, Value = Convert.ToInt32(item["CurrentBrightness"]) };
                        device.HardwareValue = device.Value; device.Channels.Add(new WmiChannel(path, item.Path.RelativePath));
                        wmiDevices[BrightnessMath.DisplayId(instance)] = device;
                    }
            }
            catch (Exception ex) { Diagnostics.Add("系统亮度：" + ex.Message); }
            Native.MonitorEnum callback = delegate(IntPtr monitor, IntPtr hdc, ref Native.Rect rect, IntPtr data)
            {
                try
                {
                    var info = new Native.MonitorInfo(); info.Size = Marshal.SizeOf(info);
                    if (!Native.GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    // Probe only this worker's logical screen; never touch another screen's DDC handle.
                    if (screenName != null && !string.Equals(screenName, info.Device, StringComparison.OrdinalIgnoreCase)) return true;
                    var display = new Native.DisplayDevice(); display.Size = Marshal.SizeOf(display);
                    string id = Native.EnumDisplayDevices(info.Device, 0, ref display, 1) ? BrightnessMath.DisplayId(display.DeviceId) : "";
                    string friendly; names.TryGetValue(id, out friendly);
                    MonitorDevice internalDevice;
                    if (wmiDevices.TryGetValue(id, out internalDevice))
                    { Describe(internalDevice, info, friendly ?? "内置屏幕"); Devices.Add(internalDevice); return true; }
                    uint count;
                    if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count) || count == 0)
                    {
                        var device = new MonitorDevice { Instance = id, Error = "驱动未提供硬件亮度接口", UsingSoftware = true };
                        Describe(device, info, friendly ?? "显示器"); Devices.Add(device); return true;
                    }
                    if (count > 32) throw new InvalidOperationException("驱动返回了异常的物理显示器数量");
                    var physical = new Native.PhysicalMonitor[count];
                    if (!Native.GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    for (int i = 0; i < physical.Length; i++)
                    {
                        var device = new MonitorDevice { Instance = (string.IsNullOrEmpty(id) ? info.Device : id) + "|physical:" + i, Handle = physical[i].Handle, HasPhysicalHandle = true };
                        Devices.Add(device);
                        Describe(device, info, friendly ?? (physical[i].Description == "Generic PnP Monitor" ? "外接显示器" : physical[i].Description));
                        foreach (bool vcp in new bool[] { true, false })
                        {
                            var channel = new PhysicalChannel(device.Handle, vcp);
                            try
                            {
                                int actual = channel.Read(); if (device.Channels.Count == 0) device.Value = device.HardwareValue = actual;
                                device.Channels.Add(channel); Diagnostics.Add(device.Name + " / " + channel.Name + ": " + actual + "%");
                            }
                            catch (Exception ex) { Diagnostics.Add(device.Name + " / " + channel.Name + ": " + ex.Message); }
                        }
                        if (!device.Supported) { device.UsingSoftware = true; device.Error = "未检测到可用的硬件亮度接口"; }
                    }
                }
                catch (Exception ex) { Diagnostics.Add("显示器检测：" + ex.Message); }
                return true;
            };
            if (!Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) Diagnostics.Add("Windows 显示器枚举失败");
            GC.KeepAlive(callback);
            foreach (var device in wmiDevices.Values) if (!Devices.Contains(device)) device.Dispose();
        }
        private static void Describe(MonitorDevice device, Native.MonitorInfo info, string name)
        {
            device.Name = name; device.ScreenName = info.Device; device.Primary = (info.Flags & 1) != 0;
            device.Bounds = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
        }
        public void Dispose() { foreach (var device in Devices) device.Dispose(); Devices.Clear(); }
    }
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MonitorInfo { internal int Size; internal Rect Monitor, Work; internal uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DisplayDevice
        {
            internal int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceString;
            internal uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceKey;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PhysicalMonitor { internal IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description; }
        internal delegate bool MonitorEnum(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice display, uint flags);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool SetMonitorBrightness(IntPtr monitor, uint value);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
        [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool DestroyPhysicalMonitor(IntPtr monitor);
    }
}
