using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenLight
{
    internal sealed class FakeChannel : IBrightnessChannel
    {
        public string Name { get; set; }
        internal int Current = 70, Requested, Reads, SettleAfter = 1;
        internal bool Ignore, FailWrite;
        public int Read() { Reads++; if (!Ignore && Reads >= SettleAfter) Current = Requested; return Current; }
        public void Write(int value) { if (FailWrite) throw new InvalidOperationException("simulated disconnect"); Requested = value; Reads = 0; }
    }
    internal static class Tests
    {
        private static int checks;
        private static void Equal<T>(T expected, T actual, string description)
        { checks++; if (!object.Equals(expected, actual)) throw new Exception(description + ": expected " + expected + ", got " + actual); }
        private static MonitorDevice Device(params IBrightnessChannel[] channels)
        {
            var device = new MonitorDevice { Name = "AOC U27N3G6R4B", ScreenName = @"\\.\DISPLAY1", Bounds = new Rectangle(0, 0, 3840, 2160), Primary = true, HardwareValue = 70, Value = 70, Wait = delegate { } };
            device.Channels.AddRange(channels); return device;
        }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Equal(20u, BrightnessMath.ToRaw(0, 20, 220), "nonzero minimum");
                Equal(120u, BrightnessMath.ToRaw(50, 20, 220), "scaled midpoint");
                Equal(220u, BrightnessMath.ToRaw(150, 20, 220), "clamped maximum");
                Equal(0, BrightnessMath.ToPercent(0, 20, 220), "raw underflow");
                Equal(100, BrightnessMath.ToPercent(uint.MaxValue, 20, 220), "raw overflow");
                for (int percent = 0; percent <= 100; percent++) Equal(percent, BrightnessMath.ToPercent(BrightnessMath.ToRaw(percent, 100, 1100), 100, 1100), "roundtrip " + percent);
                Equal(BrightnessMath.DisplayId(@"DISPLAY\AOC2703\5&1cac716b&0&UID4356_0"), BrightnessMath.DisplayId(@"\\?\DISPLAY#AOC2703#5&1cac716b&0&UID4356#{guid}"), "match real WMI and device IDs");
                Equal(@"DISPLAY\ABC\UID_A", BrightnessMath.DisplayId(@"DISPLAY\ABC\UID_A"), "preserve ID suffix");
                Equal(IntPtr.Size == 8 ? 264 : 260, Marshal.SizeOf(typeof(Native.PhysicalMonitor)), "physical ABI");
                Equal(840, Marshal.SizeOf(typeof(Native.DisplayDevice)), "display ABI");
                Equal(104, Marshal.SizeOf(typeof(Native.MonitorInfo)), "monitor ABI");
                var ignored = new FakeChannel { Name = "ignores writes", Ignore = true };
                using (var device = Device(ignored))
                {
                    bool failed = false; try { device.Set(65); } catch (InvalidOperationException) { failed = true; }
                    Equal(true, failed, "API success without brightness change is rejected");
                    Equal(70, device.Value, "never display requested value as confirmed");
                    Equal(3, ignored.Reads, "bounded readback retries");
                    failed = false; try { device.Set(69); } catch (InvalidOperationException) { failed = true; }
                    Equal(true, failed, "one-point no-op must not pass verification");
                }
                var delayed = new FakeChannel { Name = "slow monitor", SettleAfter = 3 };
                using (var device = Device(delayed)) { device.Set(65); Equal(65, device.Value, "allow slow hardware readback"); Equal(3, delayed.Reads, "wait for convergence"); }
                var good = new FakeChannel { Name = "fallback channel" };
                using (var device = Device(new FakeChannel { Name = "broken", FailWrite = true }, good))
                { device.Set(55); Equal(55, device.Value, "second hardware transport works"); Equal("fallback channel", device.LastChannel, "record working transport"); }
                using (var device = Device(new FakeChannel { Name = "no-op", Ignore = true }, new FakeChannel { Name = "working" }))
                { device.Set(42); Equal(42, device.Value, "fallback after accepted but ignored write"); }
                Equal(.35, DimOverlay.OpacityFor(65), "software luminance"); Equal(.9, DimOverlay.OpacityFor(0), "never make screen fully black"); Equal(0.0, DimOverlay.OpacityFor(100), "clear dimming");
                Equal(0x080800A0, DimOverlay.OverlayStyles, "layered click-through no-activate tool window");
                using (var slider = new BrightnessSlider())
                {
                    int changes = 0; slider.ValueChanged += delegate { changes++; };
                    slider.Value = 80; slider.Value = 80; slider.Value = 300;
                    Equal(100, slider.Value, "slider clamps"); Equal(2, changes, "coalescing identical slider values");
                    slider.Minimum = 10; slider.Value = 0; Equal(10, slider.Value, "software minimum");
                }
                string output = args.Length > 0 ? args[0] : "test-output"; Directory.CreateDirectory(output);
                SettingsTests(output);
                SettingsUiTests(output);
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var asyncChecks = ControlTests();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!asyncChecks.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                Equal(true, asyncChecks.IsCompleted, "async controls finish without deadlock");
                asyncChecks.GetAwaiter().GetResult();
                foreach (float scale in new float[] { 1, 1.5f, 2.5f })
                using (var device = Device(new FakeChannel { Name = "test" }))
                using (var card = new MonitorCard(device, scale))
                {
                    card.CreateControl();
                    using (var bitmap = new Bitmap(card.Width, card.Height)) { card.DrawToBitmap(bitmap, card.ClientRectangle); bitmap.Save(Path.Combine(output, "card-" + (int)(scale * 100) + ".png")); }
                    Equal(true, card.Slider.Bottom < card.Height, "slider fits at DPI " + scale);
                    card.Slider.Value = 80; card.Sync("failed", "", true); Equal(70, card.Slider.Value, "restore confirmed state after failure");
                }
                Console.WriteLine("PASS: " + checks + " regression checks.");
                if (args.Contains("--integration")) return Integration(output);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void SettingsTests(string output)
        {
            var profile = new DisplaySettings { Mode = ControlMode.混合, Minimum = 10, Maximum = 90 };
            Equal(10, profile.Hardware(0), "hybrid minimum backlight");
            Equal(10, profile.Hardware(20), "hybrid transition backlight");
            Equal(90, profile.Hardware(100), "calibrated maximum");
            Equal(50, profile.Software(10), "hybrid shade below transition");
            Equal(100, profile.Software(20), "no shade at transition");
            Equal(10, profile.Software(0), "hybrid safe software floor");
            foreach (double curve in new double[] { .5, 1, 2 })
            {
                profile.Curve = curve;
                int last = -1;
                for (int level = 0; level <= 100; level++)
                { int mapped = profile.Hardware(level); Equal(true, mapped >= last && mapped >= 10 && mapped <= 90, "monotonic calibrated mapping"); last = mapped; }
                for (int level = 20; level <= 100; level++)
                { int hardware = profile.Hardware(level); Equal(hardware, profile.Hardware(profile.Logical(hardware)), "representable hardware survives inverse calibration"); }
            }
            var settings = new AppSettings { Target = HotkeyTarget.所有屏幕, Linked = true, Restore = true };
            using (var device = Device())
            {
                device.Instance = "DISPLAY\\TEST\\unique";
                var entry = settings.For(device); entry.Name = "中文显示器"; entry.Mode = ControlMode.混合; entry.HasValue = true; entry.LastValue = 15;
                device.ScreenName = "renumbered"; Equal(entry, settings.For(device), "device instance survives display renumbering");
                var store = new SettingsStore(Path.GetFullPath(Path.Combine(output, "settings-test.xml")));
                bool saved = store.Save(settings); Equal(true, saved, "save settings: " + store.Error);
                var restored = store.Load(); Equal("中文显示器", restored.For(device).Name, "unicode profile roundtrip");
                Equal(15, restored.For(device).LastValue, "remember brightness"); Equal(true, restored.Restore, "remember explicit restore option");
                Equal(ControlMode.混合, restored.For(device).Mode, "remember mode");
                var copy = SettingsStore.Copy(restored); copy.Displays[0].Minimum = 100;
                Equal(0, restored.Displays[0].Minimum, "cancel settings leaves original untouched");
                Equal(false, store.Save(copy), "reject invalid calibration"); Equal(0, store.Load().Displays[0].Minimum, "invalid save preserves previous file");
                File.WriteAllText(store.Path, "<broken>"); Equal(false, store.Load().Restore, "corrupt settings defaults to no brightness writes");
                Equal(true, store.Error != null, "report malformed settings");
                using (var dialog = new SettingsForm(settings, new MonitorDevice[] { device }, null))
                {
                    dialog.Show(); Application.DoEvents();
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "settings-window.png")); }
                    dialog.Hide();
                }
            }
        }
        private static void Capture(Form form, string path)
        {
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(path); }
        }
        private static void SettingsUiTests(string output)
        {
            using (var first = Device(new FakeChannel { Name = "DDC/CI" }))
            using (var second = Device(new FakeChannel { Name = "Windows Monitor API" }))
            {
                first.Instance = "monitor-one"; second.Instance = "monitor-two"; second.Name = "扩展显示器"; second.ScreenName = "DISPLAY2"; second.Primary = false;
                var prefs = new AppSettings(); prefs.For(second).Mode = ControlMode.混合;
                using (var dialog = new SettingsForm(prefs, new MonitorDevice[] { first, second }, "快捷键已启用", 1))
                {
                    dialog.Show(); Application.DoEvents();
                    Equal(true, dialog.DisplayTabSelected, "card settings opens display tab");
                    Equal(1, dialog.DeviceSelector.SelectedIndex, "card settings selects the matching display");
                    Equal(true, dialog.Modes[2].Checked, "load saved mixed mode");
                    Capture(dialog, Path.Combine(output, "v3-display-settings.png"));
                    dialog.DeviceName.Text = "阅读屏"; dialog.MinInput.Value = 15; dialog.MaxInput.Value = 85; dialog.CurveInput.Value = 1.3m;
                    dialog.DeviceSelector.SelectedIndex = 0;
                    Equal("阅读屏", dialog.Value.For(second).Name, "switching displays commits draft name");
                    Equal(15, dialog.Value.For(second).Minimum, "switching displays commits calibration");
                    Equal(null, prefs.For(second).Name, "original preferences remain untouched");
                    dialog.Modes[1].Checked = true;
                    Equal(false, dialog.MinInput.Enabled, "software mode disables hardware calibration");
                    dialog.Modes[0].Checked = true;
                    Equal(true, dialog.MinInput.Enabled, "automatic mode re-enables calibration");
                    dialog.MinInput.Value = 90; dialog.MaxInput.Value = 30;
                    Equal(false, dialog.TrySave(), "invalid range blocked by save action");
                    dialog.MinInput.Value = 0; dialog.MaxInput.Value = 100;
                    dialog.SelectTab(false); dialog.SmoothSwitch.Checked = false; dialog.LinkedSwitch.Checked = true;
                    dialog.HotkeysSwitch.Checked = false;
                    Equal(false, dialog.TargetSelector.Enabled, "disabling hotkeys disables target selection");
                    dialog.HotkeysSwitch.Checked = true; dialog.TargetSelector.SelectedIndex = 2; dialog.RestoreSwitch.Checked = false;
                    Equal(true, dialog.TrySave(), "valid settings accepted");
                    Equal(false, dialog.Value.Smooth, "smooth toggle updates draft"); Equal(true, dialog.Value.Linked, "link toggle updates draft");
                    Equal(HotkeyTarget.所有屏幕, dialog.Value.Target, "target selection updates draft");
                    Equal(1.3, dialog.Value.For(second).Curve, "unselected display keeps edited curve");
                    Equal(true, prefs.Smooth, "cancel does not alter original global settings");
                    Capture(dialog, Path.Combine(output, "v3-general-settings.png"));
                    dialog.ClientSize = new Size((int)(560 * dialog.UiScale), (int)(480 * dialog.UiScale));
                    dialog.SelectTab(true); Capture(dialog, Path.Combine(output, "v3-settings-small.png"));
                    foreach (Control control in dialog.Controls.OfType<UiButton>()) Equal(true, dialog.ClientRectangle.Contains(control.Bounds), "fixed settings action remains visible at minimum size");
                    dialog.Hide();
                }
                using (var empty = new SettingsForm(prefs, new MonitorDevice[0], null))
                { empty.SelectTab(true); Equal(false, empty.DisplayTabSelected, "no-monitor settings stay on general tab"); Equal(true, empty.TrySave(), "general settings can save without monitors"); }
                first.UsingSoftware = true; first.Value = 65; first.LastRequestedValue = 65;
                second.Settings.Mode = ControlMode.混合; second.Value = 15;
                using (var form = new MainForm(true, false))
                {
                    var card = form.AttachDevice(first); card.Sync("硬件未响应，已切换软件调光", "requested=65, readback=70", false);
                    Equal(false, form.LinkSwitch.Enabled, "single-screen link control disabled");
                    form.Show(); Capture(form, Path.Combine(output, "v3-single-window.png"));
                    form.AttachDevice(second).Sync("混合调光已生效 · 背光已验证", "", false);
                    form.ClientSize = new Size((int)(680 * form.UiScale), (int)(880 * form.UiScale));
                    Equal(true, form.LinkSwitch.Enabled, "multi-screen link control enabled");
                    form.LinkSwitch.Checked = true; Equal(true, form.preferences.Linked, "main link control saves preference");
                    Capture(form, Path.Combine(output, "v3-multi-window.png"));
                    Equal(true, form.Cards[0].SettingsButton.Visible, "per-screen settings entry visible");
                    using (var diagnostics = new DiagnosticsForm(form.Cards.ToArray(), "DDC/CI: requested=65, readback=70 (mismatch)", form.UiScale))
                    { diagnostics.Show(); Capture(diagnostics, Path.Combine(output, "v3-diagnostics.png")); diagnostics.Hide(); }
                    form.Hide();
                }
            }
        }
        private static async Task ControlTests()
        {
            using (var form = new MainForm(true))
            using (var hardware = Device(new FakeChannel { Name = "verified" }))
            using (var software = Device())
            {
                hardware.Bounds = software.Bounds = new Rectangle(-12000, -12000, 30, 30);
                hardware.ScreenName = "FAKE1"; software.ScreenName = "FAKE2";
                hardware.Settings.Mode = ControlMode.混合; software.UsingSoftware = true;
                var first = form.AttachDevice(hardware); var second = form.AttachDevice(software);
                form.preferences.Smooth = false;
                form.SetAll(10); await form.ApplyPending();
                Equal(0, hardware.HardwareValue, "hybrid verifies physical minimum");
                Equal(10, hardware.Value, "hybrid keeps logical slider value"); Equal(false, hardware.UsingSoftware, "hybrid does not become software-only");
                form.SetAll(60); await form.ApplyPending();
                Equal(50, hardware.HardwareValue, "hybrid hardware range mapping"); Equal(60, first.Slider.Value, "show logical not physical percentage");
                form.preferences.Linked = true; first.Slider.Value = 35;
                Equal(35, second.Slider.Value, "linked slider queues other monitor"); await form.ApplyPending();
                form.preferences.Target = HotkeyTarget.所有屏幕;
                form.HandleHotkey(1); await form.ApplyPending(); Equal(40, hardware.Value, "global coarse hotkey");
                form.HandleHotkey(4); await form.ApplyPending(); Equal(39, software.Value, "global fine hotkey");
                form.preferences.Smooth = true;
                form.SetAll(15); var transition = form.ApplyPending();
                await Task.Delay(30); form.HandleHotkey(5); await transition;
                Equal(0, form.DimmingHandles.Length, "restore cancels running fade and clears every overlay"); Equal(100, software.Value, "restore cancels queued software target");
                form.SetAll(25); transition = form.ApplyPending();
                await Task.Delay(30); form.SetAll(80); await transition;
                Equal(80, hardware.Value, "latest target wins hardware queue"); Equal(80, software.Value, "latest target wins software fade");
                Equal(true, hardware.Settings.HasValue, "successful changes remembered");
                Equal(80, hardware.Settings.LastValue, "remember logical hybrid value");
                form.ResetDimming();
                Equal(hardware.Settings.Logical(hardware.HardwareValue), hardware.Value, "reset mixed shade reflects unchanged backlight");
                form.SetAll(20); transition = form.ApplyPending(); await Task.Delay(30);
                await form.ExitAsync(); await transition;
                Equal(0, form.DimmingHandles.Length, "exit cannot resurrect overlays");
            }
            using (var form = new MainForm(true))
            using (var failed = Device(new FakeChannel { Name = "ignores writes", Ignore = true }))
            {
                failed.Bounds = new Rectangle(-12000, -12000, 30, 30); failed.Settings.Mode = ControlMode.混合;
                form.AttachDevice(failed); form.preferences.Smooth = false; form.SetAll(15); await form.ApplyPending();
                Equal(true, failed.UsingSoftware, "hybrid still falls back when hardware ignores writes");
                Equal(70, failed.HardwareValue, "fallback retains actual backlight"); Equal(15, failed.Value, "fallback uses requested software percentage");
                form.ResetDimming(); await form.ExitAsync();
            }
        }
        private static int Integration(string output)
        {
            int result = 0;
            var form = new MainForm(true);
            var white = new Form { Text = "Dimming verification patch", FormBorderStyle = FormBorderStyle.None, BackColor = Color.White, StartPosition = FormStartPosition.Manual, AutoScaleMode = AutoScaleMode.None, Bounds = new Rectangle(20, 20, 140, 140), ShowInTaskbar = false, TopMost = true };
            form.Shown += async delegate
            {
                int[] originals = null;
                try
                {
                    for (int i = 0; i < 150 && !form.Ready; i++) await Task.Delay(100);
                    Equal(true, form.Ready, "real monitor discovery completes");
                    originals = form.Cards.Select(c => c.Device.HardwareValue).ToArray();
                    white.Show(); await Task.Delay(250);
                    var point = new Point(80, 80); Color before = Pixel(point);
                    var card = form.Cards.First(c => c.Device.Primary);
                    int target = originals[form.Cards.IndexOf(card)] >= 20 ? originals[form.Cards.IndexOf(card)] - 5 : 30;
                    // Exercise actual Windows mouse messages -> slider -> debounce -> hardware -> fallback.
                    int x = (int)Math.Round(13 * form.UiScale + (card.Slider.Width - 26 * form.UiScale) * target / 100.0), y = card.Slider.Height / 2;
                    SendMessage(card.Slider.Handle, 0x0201, new IntPtr(1), new IntPtr((y << 16) | x));
                    SendMessage(card.Slider.Handle, 0x0202, IntPtr.Zero, new IntPtr((y << 16) | x));
                    IntPtr foreground = GetForegroundWindow();
                    for (int i = 0; i < 150 && !form.IsIdle; i++) await Task.Delay(100);
                    Equal(true, form.IsIdle, "slider operation completes");
                    await Task.Delay(300);
                    Color dimmed = Pixel(point);
                    if (card.Device.UsingSoftware)
                    {
                        foreach (Form window in Application.OpenForms) Console.WriteLine(window.Text + " | " + window.Handle + " | " + window.Bounds);
                        Console.WriteLine("Hit=" + WindowFromPoint(point) + "; FG=" + GetForegroundWindow() + "; pixel=" + before.R + " -> " + dimmed.R);
                        Equal(true, dimmed.R < before.R - 20, "compositor pixels visibly darken");
                        Equal(white.Handle, WindowFromPoint(point), "dimming layer does not intercept mouse input");
                        Equal(true, form.DimmingHandles.Length > 0, "native dimming layer exists");
                        Console.WriteLine("Overlay handles: " + string.Join(",", form.DimmingHandles.Select(handle => handle.ToString())));
                        Equal(false, form.DimmingHandles.Contains(GetForegroundWindow()), "dimming layer never owns foreground focus");
                        Console.WriteLine("Foreground handles: " + foreground + " -> " + GetForegroundWindow());
                        Console.WriteLine("SOFTWARE FALLBACK VERIFIED: pixel " + before.R + " -> " + dimmed.R + "; slider=" + card.Slider.Value + "%");
                    }
                    else Console.WriteLine("HARDWARE VERIFIED: " + card.Device.Value + "%");
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(output, "full-window.png")); }
                    form.ResetDimming(); await Task.Delay(200);
                    Equal(before.R, Pixel(point).R, "restore removes software dimming");
                    Console.WriteLine("RESTORE VERIFIED; total checks=" + checks);
                }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally
                {
                    form.ResetDimming(); white.Close(); white.Dispose();
                }
                if (originals != null)
                {
                    for (int i = 0; i < form.Cards.Count; i++)
                    {
                        var device = form.Cards[i].Device; int original = originals[i];
                        if (device.HardwareValue != original)
                            try { await Task.Run(() => device.Set(original)); } catch (Exception ex) { result = 1; Console.Error.WriteLine("Restore hardware: " + ex.Message); }
                    }
                }
                await form.ExitAsync();
            };
            Application.Run(form); return result;
        }
        private static Color Pixel(Point point)
        {
            using (var bitmap = new Bitmap(1, 1)) using (var graphics = Graphics.FromImage(bitmap))
            { graphics.CopyFromScreen(point, Point.Empty, new Size(1, 1)); return bitmap.GetPixel(0, 0); }
        }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    }
}
