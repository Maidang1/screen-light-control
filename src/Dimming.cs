using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace ScreenLight
{
    internal sealed class DimOverlay : Form
    {
        internal int Brightness = 100;
        private IntPtr previousForeground;
        internal static double OpacityFor(int percent) { return (100 - Math.Max(10, BrightnessMath.Clamp(percent))) / 100.0; }
        internal const int OverlayStyles = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;
        internal DimOverlay()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            BackColor = Color.Black; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            Text = "Screen Light dimming layer";
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.ExStyle |= OverlayStyles; return parameters; }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            // The layer is sized in physical pixels for its own monitor. Crossing a monitor with a
            // different DPI must not let WinForms rescale it (WM_DPICHANGED / WM_GETDPISCALEDSIZE).
            if (message.Msg == 0x02E0 || message.Msg == 0x02E4) { message.Result = IntPtr.Zero; return; }
            if ((message.Msg == 0x0006 && (message.WParam.ToInt64() & 0xffff) != 0) || message.Msg == 0x0007)
            {
                // Some desktop hooks activate newly created top-level windows explicitly.
                // A dimming layer must never keep keyboard focus, even in that case.
                if (previousForeground != IntPtr.Zero && IsWindow(previousForeground)) SetForegroundWindow(previousForeground);
                message.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref message);
        }
        internal void Apply(Rectangle bounds, int percent)
        {
            IntPtr foreground = GetForegroundWindow();
            if (!IsHandleCreated || foreground != Handle) previousForeground = foreground;
            Brightness = Math.Max(10, BrightnessMath.Clamp(percent)); Bounds = bounds;
            if (Brightness == 100) { if (IsHandleCreated) ShowWindow(Handle, 0); return; }
            // Show the native window directly: Form.Show/Opacity can activate or recreate
            // a layered form on some desktop configurations despite ShowWithoutActivation.
            if (!SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(OpacityFor(Brightness) * 255), 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            SetWindowPos(Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040);
            if (GetForegroundWindow() == Handle && previousForeground != IntPtr.Zero) SetForegroundWindow(previousForeground);
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }
    internal sealed class DimmingManager : IDisposable
    {
        private readonly Dictionary<string, DimOverlay> overlays = new Dictionary<string, DimOverlay>();
        private int generation;
        internal IntPtr[] Handles { get { return overlays.Values.Where(layer => layer.IsHandleCreated).Select(layer => layer.Handle).ToArray(); } }
        internal int Apply(MonitorDevice device, int percent)
        {
            if (device.Bounds.Width <= 0 || device.Bounds.Height <= 0 || string.IsNullOrEmpty(device.ScreenName))
                throw new InvalidOperationException("无法确定屏幕位置，请重新检测");
            DimOverlay overlay;
            if (!overlays.TryGetValue(device.ScreenName, out overlay))
            { overlay = new DimOverlay(); overlays.Add(device.ScreenName, overlay); }
            overlay.Apply(device.Bounds, percent); device.UsingSoftware = true; device.Value = overlay.Brightness;
            return device.Value;
        }
        internal async Task<bool> ApplySmooth(MonitorDevice device, int percent, bool smooth, bool software, int logical, Func<bool> superseded)
        {
            int version = generation;
            DimOverlay layer;
            int start = overlays.TryGetValue(device.ScreenName, out layer) ? layer.Brightness : 100;
            int end = Math.Max(10, BrightnessMath.Clamp(percent));
            int steps = smooth ? 12 : 1;
            for (int i = 1; i <= steps; i++)
            {
                if (version != generation || superseded()) return false;
                double t = i / (double)steps;
                int value = (int)Math.Round(start + (end - start) * (t * t * (3 - 2 * t)));
                Apply(device, value);
                device.UsingSoftware = software;
                device.Value = logical;
                if (i < steps) await Task.Delay(16);
            }
            return true;
        }
        internal void Remove(string screen)
        {
            generation++;
            DimOverlay overlay;
            if (overlays.TryGetValue(screen, out overlay)) { overlays.Remove(screen); overlay.Close(); overlay.Dispose(); }
        }
        internal void Reconcile(IEnumerable<MonitorDevice> devices)
        {
            var active = devices.ToArray();
            foreach (string key in overlays.Keys.ToArray()) if (!active.Any(d => d.ScreenName == key)) Remove(key);
            foreach (var device in active)
            {
                DimOverlay overlay;
                if (overlays.TryGetValue(device.ScreenName, out overlay)) overlay.Apply(device.Bounds, overlay.Brightness);
            }
        }
        public void Dispose() { generation++; foreach (var key in overlays.Keys.ToArray()) Remove(key); }
    }
}
