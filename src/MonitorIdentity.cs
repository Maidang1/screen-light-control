using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ScreenLight
{
    // Stable monitor identity independent of the GPU port. Device paths change when a cable
    // moves to another connector or GPU; the EDID manufacturer, product and serial do not.
    internal sealed class EdidInfo
    {
        internal string Manufacturer, Name, Serial;
        internal int Product;
        internal uint SerialNumber;
        internal int WidthCm, HeightCm;
        internal static EdidInfo Parse(byte[] edid)
        {
            if (edid == null || edid.Length < 128) return null;
            byte[] header = { 0, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0 };
            for (int i = 0; i < header.Length; i++) if (edid[i] != header[i]) return null;
            int vendor = (edid[8] << 8) | edid[9];
            var result = new EdidInfo
            {
                Manufacturer = new string(new[] { (char)('A' - 1 + ((vendor >> 10) & 31)), (char)('A' - 1 + ((vendor >> 5) & 31)), (char)('A' - 1 + (vendor & 31)) }),
                Product = edid[10] | (edid[11] << 8), SerialNumber = BitConverter.ToUInt32(edid, 12), WidthCm = edid[21], HeightCm = edid[22]
            };
            for (int offset = 54; offset <= 108; offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0) continue;
                string text = Encoding.ASCII.GetString(edid, offset + 5, 13).Split('\n')[0].Trim();
                if (edid[offset + 3] == 0xff && text.Length > 0) result.Serial = text;
                if (edid[offset + 3] == 0xfc && text.Length > 0) result.Name = text;
            }
            return result;
        }
        // Null when the panel reports no usable serial; identical models are then indistinguishable.
        internal string StableKey
        {
            get
            {
                string serial = !string.IsNullOrEmpty(Serial) && Serial.Trim('0', ' ').Length > 0 ? Serial
                    : SerialNumber != 0 && SerialNumber != 0x01010101 && SerialNumber != uint.MaxValue ? SerialNumber.ToString("X8") : null;
                return serial == null ? null : "EDID:" + Manufacturer + Product.ToString("X4") + ":" + serial;
            }
        }
    }

    internal static class MonitorIdentity
    {
        // Accepts \\?\DISPLAY#AOC2703#5&...#{guid} device interface paths and DISPLAY\AOC2703\5&..._0 WMI names.
        internal static byte[] ReadEdid(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;
            string id = BrightnessMath.DisplayId(devicePath);
            if (!id.StartsWith(@"DISPLAY\", StringComparison.OrdinalIgnoreCase) || id.Split('\\').Length != 3) return null;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id + @"\Device Parameters"))
                    return key == null ? null : key.GetValue("EDID") as byte[];
            }
            catch (Exception) { return null; }
        }
        internal static EdidInfo Edid(string devicePath) { return EdidInfo.Parse(ReadEdid(devicePath)); }
        internal static string Stable(string devicePath) { var edid = Edid(devicePath); return edid == null ? null : edid.StableKey; }
        internal static string Key(string devicePath)
        {
            return Stable(devicePath) ?? (string.IsNullOrEmpty(devicePath) ? null : "PATH:" + BrightnessMath.DisplayId(devicePath));
        }
        // Monitors with duplicated EDID serials must not share one profile; fall back to their paths.
        internal static void Disambiguate<T>(IList<T> items, Func<T, string> get, Action<T, string> clear)
        {
            foreach (var group in items.Where(i => get(i) != null && get(i).StartsWith("EDID:")).GroupBy(get).Where(g => g.Count() > 1).ToArray())
                foreach (var item in group) clear(item, null);
        }
        // Active monitor interface paths attached to a GDI source such as \\.\DISPLAY1, in driver order.
        internal static List<string> MonitorPaths(string gdiName)
        {
            var result = new List<string>();
            for (uint i = 0; i < 16; i++)
            {
                var display = new Native.DisplayDevice(); display.Size = Marshal.SizeOf(display);
                if (!Native.EnumDisplayDevices(gdiName, i, ref display, 1)) break;
                if ((display.StateFlags & 1) != 0 && !string.IsNullOrEmpty(display.DeviceId)) result.Add(display.DeviceId);
            }
            return result;
        }
    }
}
