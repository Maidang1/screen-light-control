using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenLight
{
    public sealed class DisplayCommand
    {
        public string Action, Screen, Text;
        public int Kind, Value;
    }
    public sealed class VendorControl
    {
        public string Vendor, Name;
        public int Kind, Minimum, Maximum, Current, Default;
        public override string ToString() { return Vendor + " · " + Name; }
    }
    public sealed class DisplayReply
    {
        public string Message, Default;
        public List<string> Items = new List<string>();
        public List<VendorControl> Controls = new List<VendorControl>();
    }

    // Runs inside the hardware worker: driver SDKs and color-management calls may block or crash,
    // which must not take the UI process down with them.
    internal static class DisplayExtras
    {
        internal static DisplayReply Execute(DisplayCommand command)
        {
            switch (command.Action)
            {
                case "ICC_LIST": return IccList(command.Screen);
                case "ICC_ADD": return IccChange(command.Screen, command.Text, true, command.Value != 0);
                case "ICC_REMOVE": return IccChange(command.Screen, command.Text, false, false);
                case "ICC_DEFAULT": return IccDefault(command.Screen, command.Text);
                case "ICC_INSTALL": return IccInstall(command.Text);
                case "VENDOR_PROBE": return VendorColor.Probe(command.Screen);
                case "VENDOR_SET": return VendorColor.Set(command.Screen, command.Kind, command.Value);
                default: throw new InvalidOperationException("未知显示命令");
            }
        }

        // ---- ICC profiles (mscms, Windows 10 2004+ display association API) ----
        [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; }
        private const int UserScope = 1;
        private static Luid Source(string screen, out uint source)
        {
            long adapter; WindowsDesktop.SourceOf(screen, out adapter, out source);
            return new Luid { Low = (uint)(adapter & 0xffffffff), High = (int)(adapter >> 32) };
        }
        private static void Hr(int code, string action) { if (code < 0) throw new InvalidOperationException(action + "失败，HRESULT 0x" + code.ToString("X8")); }
        internal static string ColorDirectory()
        {
            var builder = new StringBuilder(260); uint size = 520;
            return GetColorDirectory(null, builder, ref size) ? builder.ToString() : Path.Combine(Environment.SystemDirectory, @"spool\drivers\color");
        }
        private static DisplayReply IccList(string screen)
        {
            uint source; var luid = Source(screen, out source); var reply = new DisplayReply();
            try
            {
                IntPtr list; uint count;
                Hr(ColorProfileGetDisplayList(UserScope, luid, source, out list, out count), "读取颜色配置关联");
                try { for (int i = 0; i < count && i < 256; i++) reply.Items.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size))); }
                finally { if (list != IntPtr.Zero) LocalFree(list); }
                IntPtr name;
                if (ColorProfileGetDisplayDefault(UserScope, luid, source, 0, 4, out name) >= 0 && name != IntPtr.Zero)
                { reply.Default = Marshal.PtrToStringUni(name); LocalFree(name); }
            }
            catch (EntryPointNotFoundException) { throw new InvalidOperationException("此 Windows 版本未提供按显示器关联颜色配置的接口，请使用颜色管理面板"); }
            try { reply.Items.AddRange(Directory.GetFiles(ColorDirectory()).Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)).Select(f => "可用:" + Path.GetFileName(f))); }
            catch (Exception) { }
            reply.Message = "已读取关联配置";
            return reply;
        }
        private static DisplayReply IccChange(string screen, string profile, bool add, bool advanced)
        {
            uint source; var luid = Source(screen, out source);
            try
            {
                if (add) Hr(ColorProfileAddDisplayAssociation(UserScope, profile, luid, source, true, advanced), "关联颜色配置");
                else Hr(ColorProfileRemoveDisplayAssociation(UserScope, profile, luid, source, false), "移除颜色配置");
            }
            catch (EntryPointNotFoundException) { throw new InvalidOperationException("此 Windows 版本未提供按显示器关联颜色配置的接口"); }
            return new DisplayReply { Message = add ? "已关联并设为默认：" + profile : "已移除关联：" + profile };
        }
        private static DisplayReply IccDefault(string screen, string profile)
        {
            uint source; var luid = Source(screen, out source);
            try { Hr(ColorProfileSetDisplayDefaultAssociation(UserScope, profile, 0, 4, luid, source), "设置默认颜色配置"); }
            catch (EntryPointNotFoundException) { throw new InvalidOperationException("此 Windows 版本未提供按显示器关联颜色配置的接口"); }
            return new DisplayReply { Message = "已设为默认：" + profile };
        }
        private static DisplayReply IccInstall(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到颜色配置文件", path);
            if (!InstallColorProfile(null, path)) throw new InvalidOperationException("安装颜色配置失败（通常需要管理员权限）：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return new DisplayReply { Message = "已安装：" + Path.GetFileName(path) };
        }
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileGetDisplayList(int scope, Luid adapter, uint source, out IntPtr list, out uint count);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileGetDisplayDefault(int scope, Luid adapter, uint source, int type, int subtype, out IntPtr name);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileAddDisplayAssociation(int scope, string profile, Luid adapter, uint source, bool setAsDefault, bool advancedColor);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileRemoveDisplayAssociation(int scope, string profile, Luid adapter, uint source, bool advancedColor);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string profile, int type, int subtype, Luid adapter, uint source);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "InstallColorProfileW")] private static extern bool InstallColorProfile(string machine, string profile);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, EntryPoint = "GetColorDirectoryW")] private static extern bool GetColorDirectory(string machine, StringBuilder buffer, ref uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }

    // Driver-level color controls. NVIDIA: digital vibrance and hue through NVAPI.
    // AMD: brightness, contrast, saturation, hue and temperature through ADL.
    internal static class VendorColor
    {
        internal const int NvVibrance = 1, NvHue = 2, AmdBrightness = 11, AmdContrast = 12, AmdSaturation = 13, AmdHue = 14, AmdTemperature = 15;
        private static readonly Dictionary<int, int> AmdTypes = new Dictionary<int, int> { { AmdBrightness, 1 }, { AmdContrast, 2 }, { AmdSaturation, 4 }, { AmdHue, 8 }, { AmdTemperature, 16 } };
        private static readonly Dictionary<int, string> AmdNames = new Dictionary<int, string> { { AmdBrightness, "驱动亮度" }, { AmdContrast, "驱动对比度" }, { AmdSaturation, "饱和度" }, { AmdHue, "色调" }, { AmdTemperature, "色温" } };
        internal static DisplayReply Probe(string screen)
        {
            var reply = new DisplayReply(); var notes = new List<string>();
            try { NvApi.Probe(screen, reply.Controls); } catch (Exception ex) { notes.Add("NVIDIA：" + ex.Message); }
            try { Adl.Probe(screen, reply.Controls); } catch (Exception ex) { notes.Add("AMD：" + ex.Message); }
            reply.Message = reply.Controls.Count > 0 ? "已读取 " + reply.Controls.Count + " 项显卡驱动颜色控制" : "此屏幕所在显卡未提供可用的驱动颜色接口（" + string.Join("；", notes) + "）。Intel 显卡请使用 Intel Graphics Command Center。";
            return reply;
        }
        internal static DisplayReply Set(string screen, int kind, int value)
        {
            int readback = kind < 10 ? NvApi.Set(screen, kind, value) : Adl.Set(screen, AmdTypes[kind], value);
            if (readback != value) throw new InvalidOperationException("驱动已接受指令，但读回 " + readback + "，与目标不一致");
            return new DisplayReply { Message = "驱动读回已验证：" + value };
        }
        private static T Function<T>(IntPtr library, string name) where T : class
        {
            IntPtr address = GetProcAddress(library, name);
            return address == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
        private static IntPtr Load(params string[] names)
        {
            foreach (string name in names) { IntPtr library = LoadLibrary(name); if (library != IntPtr.Zero) return library; }
            throw new InvalidOperationException("未安装对应显卡驱动");
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr library, string name);

        private static class NvApi
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterface(uint id);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumDisplay(int index, out IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DisplayName(IntPtr handle, StringBuilder name);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDvc(IntPtr handle, int output, ref DvcInfo info);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetLevel(IntPtr handle, int output, int level);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetHue(IntPtr handle, int output, ref HueInfo info);
            [StructLayout(LayoutKind.Sequential)] private struct DvcInfo { internal uint Version; internal int Current, Minimum, Maximum; }
            [StructLayout(LayoutKind.Sequential)] private struct HueInfo { internal uint Version; internal int Current, Default; }
            private static QueryInterface query;
            private static T Get<T>(uint id) where T : class
            {
                if (query == null)
                {
                    IntPtr library = Load(IntPtr.Size == 8 ? "nvapi64.dll" : "nvapi.dll");
                    query = Function<QueryInterface>(library, "nvapi_QueryInterface");
                    if (query == null) throw new InvalidOperationException("NVAPI 不可用");
                    var init = Get<Initialize>(0x0150E828); if (init == null || init() != 0) { query = null; throw new InvalidOperationException("NVAPI 初始化失败"); }
                }
                IntPtr address = query(id);
                return address == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
            }
            private static IntPtr Handle(string screen)
            {
                var enumerate = Get<EnumDisplay>(0x9ABDD40D); var name = Get<DisplayName>(0x22A78B05);
                if (enumerate == null || name == null) throw new InvalidOperationException("NVAPI 缺少显示器枚举接口");
                IntPtr handle;
                for (int i = 0; i < 32 && enumerate(i, out handle) == 0; i++)
                {
                    var text = new StringBuilder(64);
                    if (name(handle, text) == 0 && string.Equals(text.ToString(), screen, StringComparison.OrdinalIgnoreCase)) return handle;
                }
                throw new InvalidOperationException("此屏幕不由 NVIDIA 显卡输出");
            }
            internal static void Probe(string screen, List<VendorControl> controls)
            {
                IntPtr handle = Handle(screen);
                var getDvc = Get<GetDvc>(0x4085DE45); var dvc = new DvcInfo { Version = 16 | 0x10000 };
                if (getDvc != null && getDvc(handle, 0, ref dvc) == 0)
                    controls.Add(new VendorControl { Vendor = "NVIDIA", Name = "数字振动（饱和度）", Kind = NvVibrance, Minimum = dvc.Minimum, Maximum = dvc.Maximum, Current = dvc.Current, Default = dvc.Minimum + (dvc.Maximum - dvc.Minimum) / 2 });
                var getHue = Get<GetHue>(0x95B64341); var hue = new HueInfo { Version = 12 | 0x10000 };
                if (getHue != null && getHue(handle, 0, ref hue) == 0)
                    controls.Add(new VendorControl { Vendor = "NVIDIA", Name = "色调角度", Kind = NvHue, Minimum = 0, Maximum = 359, Current = hue.Current, Default = hue.Default });
            }
            internal static int Set(string screen, int kind, int value)
            {
                IntPtr handle = Handle(screen);
                var set = Get<SetLevel>(kind == NvVibrance ? 0x172409B4u : 0xF5A0F22Cu);
                if (set == null || set(handle, 0, value) != 0) throw new InvalidOperationException("NVIDIA 驱动拒绝设置");
                var list = new List<VendorControl>(); Probe(screen, list);
                var control = list.FirstOrDefault(c => c.Kind == kind); if (control == null) throw new InvalidOperationException("无法读回驱动设置");
                return control.Current;
            }
        }

        private static class Adl
        {
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr Malloc(int size);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create(Malloc malloc, int connected, out IntPtr context);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Destroy(IntPtr context);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AdapterCount(IntPtr context, out int count);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AdapterInfoGet(IntPtr context, IntPtr info, int size);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DisplayInfoGet(IntPtr context, int adapter, out int count, out IntPtr info, int force);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ColorGet(IntPtr context, int adapter, int display, int type, out int current, out int fallback, out int minimum, out int maximum, out int step);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ColorSet(IntPtr context, int adapter, int display, int type, int value);
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            internal struct AdapterInfo
            {
                internal int Size, Index; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Udid;
                internal int Bus, Device, Function, Vendor;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string DisplayName;
                internal int Present, Exist;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string DriverPath;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string DriverPathExt;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Pnp;
                internal int OsDisplayIndex;
            }
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            internal struct DisplayInfo
            {
                internal int LogicalIndex, PhysicalIndex, LogicalAdapter, PhysicalAdapter, Controller;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Manufacturer;
                internal int Type, OutputType, Connector, Mask, Value;
            }
            private static readonly Malloc allocate = size => Marshal.AllocHGlobal(size);
            private static IntPtr library;
            private static T F<T>(string name) where T : class
            {
                if (library == IntPtr.Zero) library = Load("atiadlxx.dll", "atiadlxy.dll");
                var result = Function<T>(library, name); if (result == null) throw new InvalidOperationException("ADL 缺少 " + name);
                return result;
            }
            // Returns (adapter index, display index) of the display that drives the GDI source.
            private static T With<T>(string screen, Func<IntPtr, int, int, T> action)
            {
                IntPtr context;
                if (F<Create>("ADL2_Main_Control_Create")(allocate, 1, out context) != 0) throw new InvalidOperationException("ADL 初始化失败");
                try
                {
                    int count; if (F<AdapterCount>("ADL2_Adapter_NumberOfAdapters_Get")(context, out count) != 0 || count <= 0 || count > 256) throw new InvalidOperationException("ADL 未找到显卡");
                    int size = Marshal.SizeOf(typeof(AdapterInfo)); IntPtr buffer = Marshal.AllocHGlobal(size * count);
                    var adapters = new List<AdapterInfo>();
                    try
                    {
                        for (int i = 0; i < size * count; i++) Marshal.WriteByte(buffer, i, 0);
                        if (F<AdapterInfoGet>("ADL2_Adapter_AdapterInfo_Get")(context, buffer, size * count) != 0) throw new InvalidOperationException("ADL 无法读取显卡信息");
                        for (int i = 0; i < count; i++) adapters.Add((AdapterInfo)Marshal.PtrToStructure(new IntPtr(buffer.ToInt64() + i * size), typeof(AdapterInfo)));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                    foreach (var adapter in adapters.Where(a => a.Present != 0 && string.Equals(a.DisplayName, screen, StringComparison.OrdinalIgnoreCase)))
                    {
                        int displays; IntPtr info;
                        if (F<DisplayInfoGet>("ADL2_Display_DisplayInfo_Get")(context, adapter.Index, out displays, out info, 0) != 0 || info == IntPtr.Zero) continue;
                        try
                        {
                            int itemSize = Marshal.SizeOf(typeof(DisplayInfo));
                            for (int i = 0; i < displays && i < 64; i++)
                            {
                                var display = (DisplayInfo)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + i * itemSize), typeof(DisplayInfo));
                                if ((display.Value & 3) == 3 && display.LogicalAdapter == adapter.Index) return action(context, adapter.Index, display.LogicalIndex);
                            }
                        }
                        finally { Marshal.FreeHGlobal(info); }
                    }
                    throw new InvalidOperationException("此屏幕不由 AMD 显卡输出");
                }
                finally { F<Destroy>("ADL2_Main_Control_Destroy")(context); }
            }
            internal static void Probe(string screen, List<VendorControl> controls)
            {
                With(screen, (context, adapter, display) =>
                {
                    foreach (var kind in AmdTypes)
                    {
                        int current, fallback, minimum, maximum, step;
                        if (F<ColorGet>("ADL2_Display_Color_Get")(context, adapter, display, kind.Value, out current, out fallback, out minimum, out maximum, out step) == 0 && maximum > minimum)
                            controls.Add(new VendorControl { Vendor = "AMD", Name = AmdNames[kind.Key], Kind = kind.Key, Minimum = minimum, Maximum = maximum, Current = current, Default = fallback });
                    }
                    return true;
                });
            }
            internal static int Set(string screen, int type, int value)
            {
                return With(screen, (context, adapter, display) =>
                {
                    int current, fallback, minimum, maximum, step;
                    if (F<ColorGet>("ADL2_Display_Color_Get")(context, adapter, display, type, out current, out fallback, out minimum, out maximum, out step) != 0) throw new InvalidOperationException("AMD 驱动未提供此项");
                    if (value < minimum || value > maximum) throw new InvalidOperationException("参数超出驱动报告的范围 " + minimum + "–" + maximum);
                    if (F<ColorSet>("ADL2_Display_Color_Set")(context, adapter, display, type, value) != 0) throw new InvalidOperationException("AMD 驱动拒绝设置");
                    F<ColorGet>("ADL2_Display_Color_Get")(context, adapter, display, type, out current, out fallback, out minimum, out maximum, out step);
                    return current;
                });
            }
        }
    }
}
