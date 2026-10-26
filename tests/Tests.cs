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
                if (HardwareWorker.TryRun(args)) return 0;
                if (args.Length == 2 && args[0] == "--test-no-connect") System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                if (args.Length == 2 && args[0] == "--test-hardware-worker") return TestHardwareWorker(args[1]);
                TransportTests();
                ExtendedControlTests();
                if (args.Contains("--transport-only")) { Console.WriteLine("PASS: " + checks + " control and transport checks."); return 0; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Contains("--tray-ui-only")) { TrayTests(); Console.WriteLine("PASS: tray window lifecycle; " + checks + " checks"); return 0; }
                if (args.Contains("--console-ui-only")) { ConsoleUiTest("/tmp"); return 0; }
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
                ConsoleUiTest(output);
                TrayTests();
                SettingsTests(output);
                SettingsUiTests(output);
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var asyncChecks = AllControlTests();
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
        private static int TestHardwareWorker(string pipeName)
        {
            using (var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut))
            {
                pipe.Connect(3000);
                using (var input = new StreamReader(pipe))
                using (var output = new StreamWriter(pipe) { AutoFlush = true })
                {
                    string command; bool hangWrites = false; string recoveryFile = null;
                    while ((command = input.ReadLine()) != null)
                    {
                        if (command.StartsWith("RECOVERY ")) recoveryFile = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(command.Substring(9)));
                        if (command == "ARM") hangWrites = true;
                        if (hangWrites && command.StartsWith("WRITE ")) System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                        if (command == "HANG") System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                        if (command == "EXIT") return 0;
                        if (command == "ERROR") output.WriteLine("ERROR " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("simulated API failure")));
                        else if (command == "PID") output.WriteLine(System.Diagnostics.Process.GetCurrentProcess().Id);
                        else if (command.StartsWith("DISCOVER "))
                        {
                            string screen = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(command.Substring(9)));
                            if (screen == "bad") System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                            using (var service = new NativeMonitorService())
                            {
                                var device = Device(new FakeChannel { Name = "test hardware" });
                                device.ScreenName = screen; device.Instance = screen;
                                service.Devices.Add(device);
                                output.WriteLine(HardwareWorker.Snapshot(service));
                            }
                        }
                        else if (command.StartsWith("READ ")) output.WriteLine("65");
                        else output.WriteLine("OK");

                    }
                    if (recoveryFile != null) File.WriteAllText(recoveryFile, "restored");
                    return 0;
                }
            }
        }
        private static System.Diagnostics.ProcessStartInfo WorkerStart(string arguments = "--test-hardware-worker")
        {
            string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (Type.GetType("Mono.Runtime") != null)
                return new System.Diagnostics.ProcessStartInfo(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,
                    "\"" + executable + "\" " + arguments);
            return new System.Diagnostics.ProcessStartInfo(executable, arguments);
        }
        private static void TransportTests()
        {
            string recoveryFile = Path.Combine(Path.GetTempPath(), "screen-light-recovery-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                using (var connection = new HardwareConnection(WorkerStart(), true))
                {
                    int pid = int.Parse(connection.Request("PID", 3000));
                    connection.Request("RECOVERY " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(recoveryFile)), 3000);
                    using (var child = System.Diagnostics.Process.GetProcessById(pid))
                    {
                        connection.Dispose();
                        bool exited = child.WaitForExit(3000);
                        if (!exited) child.Kill();
                        Equal(true, exited, "trial worker is allowed to finish recovery after disconnection");
                        Equal("restored", File.ReadAllText(recoveryFile), "trial connection disposal preserves recovery responsibility");
                    }
                }
            }
            finally { if (File.Exists(recoveryFile)) File.Delete(recoveryFile); }
            using (var service = new MonitorService(() => new HardwareConnection(WorkerStart()),
                () => new[] { new MonitorDevice { ScreenName = "bad" }, new MonitorDevice { ScreenName = "good" } }, 3000))
            {
                service.Discover();
                Equal(2, service.Devices.Count, "discovery retains both screens after one worker hangs");
                Equal(true, service.Devices[0].UsingSoftware, "hung screen uses logical screen fallback");
                Equal(true, service.Devices[1].Supported, "healthy screen retains hardware channels");
                service.Devices[1].Wait = delegate { };
                service.Devices[1].Set(65);
                Equal(65, service.Devices[1].HardwareValue, "healthy screen remains controllable after another worker times out");
            }
            using (var stuck = new HardwareConnection(WorkerStart()))
            using (var healthy = new HardwareConnection(WorkerStart()))
            {
                Equal("OK", healthy.Request("PING", 3000), "second screen worker started");
                bool failed = false;
                try { stuck.Request("HANG", 200); } catch (TimeoutException) { failed = true; }
                Equal(true, failed, "one screen worker times out");
                Equal(65, new RemoteChannel(healthy, 0, 0, "healthy screen").Read(), "one screen timeout leaves another screen worker alive");
            }
            using (var connection = new HardwareConnection(WorkerStart("--hardware-worker " + System.Diagnostics.Process.GetCurrentProcess().Id)))
            {
                for (int i = 0; i < 2; i++)
                {
                    bool failed = false;
                    try { connection.Request("INVALID", 3000); } catch (InvalidOperationException) { failed = true; }
                    Equal(true, failed, "real worker dispatch and parent watchdog stay alive");
                }
            }
            using (var connection = new HardwareConnection(WorkerStart("--test-no-connect")))
            {
                bool failed = false; var watch = System.Diagnostics.Stopwatch.StartNew();
                try { connection.Request("PING", 200); } catch (TimeoutException) { failed = true; }
                Equal(true, failed, "worker startup also has a deadline");
                Equal(true, watch.ElapsedMilliseconds < 5000, "startup timeout returns promptly");
            }
            using (var connection = new HardwareConnection(WorkerStart()))
            {
                Equal("OK", connection.Request("PING", 3000), "worker responds");
                bool failed = false;
                try { connection.Request("ERROR", 3000); } catch (InvalidOperationException) { failed = true; }
                Equal(true, failed, "API failure propagated");
                Equal("OK", connection.Request("PING", 3000), "API failure permits another channel");
                var channel = new RemoteChannel(connection, 0, 0, "test proxy");
                channel.Write(65); Equal(65, channel.Read(), "remote read and write protocol");
                int pid = int.Parse(connection.Request("PID", 3000));
                using (var child = System.Diagnostics.Process.GetProcessById(pid))
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew(); failed = false;
                    try { connection.Request("HANG", 200); } catch (TimeoutException) { failed = true; }
                    Equal(true, failed, "hung hardware has a deadline");
                    Equal(true, watch.ElapsedMilliseconds < 5000, "timeout returns promptly");
                    Equal(true, child.WaitForExit(3000), "hung worker is terminated");
                }
                failed = false;
                try { connection.Request("PING", 3000); } catch (IOException) { failed = true; }
                Equal(true, failed, "timed out worker is never reused");
                connection.Dispose(); // cleanup must be idempotent after timeout
            }
            using (var connection = new HardwareConnection(WorkerStart()))
            {
                bool failed = false;
                try { connection.Request("EXIT", 3000); } catch (IOException) { failed = true; }
                Equal(true, failed, "unexpected worker exit cannot hang caller");
            }
            using (var connection = new HardwareConnection(WorkerStart()))
                Equal("OK", connection.Request("PING", 3000), "fresh worker recovers after failure");
            using (var connection = new HardwareConnection(WorkerStart()))
            using (var device = Device(new RemoteChannel(connection, 0, 0, "hung channel", 200), new RemoteChannel(connection, 0, 1, "second channel", 200)))
            {
                connection.Request("ARM", 3000);
                bool failed = false; var watch = System.Diagnostics.Stopwatch.StartNew();
                try { device.Set(45); } catch (InvalidOperationException) { failed = true; }
                Equal(true, failed, "hung write reaches existing software fallback path");
                Equal(70, device.HardwareValue, "timed out write is never reported as verified");
                Equal(2, device.Attempts.Count, "remaining channel fails safely after timeout");
                Equal(true, watch.ElapsedMilliseconds < 5000, "channel retries cannot keep lock forever");
            }
        }
        private static DesktopState SampleDesktop()
        {
            var result = new DesktopState();
            for (int i = 0; i < 2; i++) result.Screens.Add(new DesktopScreen { Id = "stable-" + i, Name = "测试显示器 " + i, Device = "DISPLAY" + i,
                Width = 1920, Height = 1080, Frequency = 60, X = i * 1920, Primary = i == 0,
                Modes = new System.Collections.Generic.List<DesktopMode> { new DesktopMode { Width = 1920, Height = 1080, Frequency = 60 } } });
            return result;
        }
        private static void ExtendedControlTests()
        {
            Equal(220, Marshal.SizeOf(typeof(WindowsDesktop.DevMode)), "DEVMODE ABI for mode enumeration");
            var state = SampleDesktop();
            Func<DesktopPlan> plan = () => new DesktopPlan { Screens = WireXml.Unpack<DesktopState>(WireXml.Pack(state)).Screens };
            plan().Validate(state); Equal(2, plan().Screens.Count, "display plan roundtrip");
            Action<DesktopPlan, string> reject = (value, name) => {
                bool failed = false; try { value.Validate(state); } catch (InvalidDataException) { failed = true; }
                Equal(true, failed, name);
            };
            var edit = plan(); edit.Screens[0].Primary = false; reject(edit, "missing primary rejected");
            edit = plan(); edit.Screens[1].Primary = true; reject(edit, "multiple primary rejected");
            edit = plan(); edit.Screens[1].Id = "missing"; reject(edit, "disconnected profile rejected");
            edit = plan(); edit.Screens[1].Id = edit.Screens[0].Id; reject(edit, "duplicate monitor rejected");
            edit = plan(); edit.Screens[1].X = 100; reject(edit, "overlapping layout rejected");
            edit = plan(); edit.Screens[1].X = 5000; reject(edit, "unreachable desktop island rejected");
            edit = plan(); edit.Screens[0].Frequency = 144; reject(edit, "unsupported refresh rejected");
            edit = plan(); edit.Screens[0].HdrEnabled = true; reject(edit, "unavailable HDR rejected");
            edit = plan(); edit.Screens[0].Rotation = 45; reject(edit, "arbitrary rotation rejected");
            edit = plan(); edit.Screens[0].X = int.MinValue; reject(edit, "coordinate overflow rejected");
            edit = plan(); edit.Topology = 2; reject(edit, "mixed topology and screen edits rejected");
            reject(new DesktopPlan { Topology = 3 }, "invalid projection rejected");
            foreach (int topology in new[] { 1, 2, 4, 8 }) { new DesktopPlan { Topology = topology }.Validate(state); Equal(true, true, "standard projection accepted"); }
            edit = plan(); edit.Screens[1].Rotation = 90; edit.Screens[1].Width = 1080; edit.Screens[1].Height = 1920;
            edit.Validate(state); Equal(90, edit.Screens[1].Rotation, "rotation validates against native orientation modes");
            var now = new DateTime(2026, 1, 1); int restores = 0, saves = 0;
            var trial = new DesktopTrial(() => restores++, () => saves++, () => now);
            string token = trial.Begin(); now = now.AddSeconds(19); trial.Tick(); Equal(0, restores, "trial lasts until deadline");
            now = now.AddSeconds(1); trial.Tick(); Equal(1, restores, "deadline restores original snapshot");
            trial.Tick(); Equal(1, restores, "recovery is idempotent");
            bool failedToken = false; try { trial.Confirm(token); } catch (InvalidOperationException) { failedToken = true; }
            Equal(true, failedToken, "late confirmation cannot persist reverted settings");
            token = trial.Begin(); trial.Confirm(token); Equal(1, saves, "confirmation persists current configuration");
            now = now.AddMinutes(1); trial.Tick(); Equal(1, restores, "confirmed settings do not revert");
            token = trial.Begin(); now = now.AddSeconds(20);
            failedToken = false; try { trial.Confirm(token); } catch (InvalidOperationException) { failedToken = true; }
            Equal(true, failedToken, "deadline checked during confirmation even before timer ticks");
            Equal(2, restores, "late confirmation restores immediately");
            trial.Begin(); trial.Revert(); Equal(3, restores, "disconnect or explicit cancel restores pending trial");
            int attempts = 0;
            var retry = new DesktopTrial(() => { if (++attempts == 1) throw new IOException("device not ready"); }, () => { }, () => now);
            retry.Begin(); now = now.AddSeconds(21);
            try { retry.Tick(); } catch (IOException) { }
            Equal(true, retry.Token != null, "failed recovery keeps snapshot pending"); retry.Tick(); Equal(2, attempts, "recovery retries after transient driver error");
            var badSave = new DesktopTrial(() => { }, () => { throw new IOException("save failed"); }, () => now);
            token = badSave.Begin(); try { badSave.Confirm(token); } catch (IOException) { }
            Equal(true, badSave.Token != null, "persist failure keeps rollback armed");
            NewCapabilityTests(state);
            bool xmlRejected = false;
            try { WireXml.Unpack<DesktopPlan>(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///nonexistent'>]><DesktopPlan>&e;</DesktopPlan>"))); }
            catch (InvalidOperationException) { xmlRejected = true; }
            Equal(true, xmlRejected, "profile parser prohibits external entities");
        }
        private static void NewCapabilityTests(DesktopState state)
        {
            // EDID identity: manufacturer "AOC", product 0x2703, serial string descriptor.
            var edid = new byte[128]; new byte[] { 0, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0 }.CopyTo(edid, 0);
            edid[8] = 0x05; edid[9] = 0xe3; edid[10] = 0x03; edid[11] = 0x27;
            edid[57] = 0xff; System.Text.Encoding.ASCII.GetBytes("ABC123\n").CopyTo(edid, 59);
            var info = EdidInfo.Parse(edid);
            Equal("AOC", info.Manufacturer, "EDID manufacturer"); Equal("EDID:AOC2703:ABC123", info.StableKey, "EDID stable key");
            edid[57] = 0; Equal(null, EdidInfo.Parse(edid).StableKey, "no serial means no stable identity");
            var items = new System.Collections.Generic.List<string[]> { new[] { "EDID:X:1" }, new[] { "EDID:X:1" }, new[] { "EDID:Y:2" } };
            MonitorIdentity.Disambiguate(items, i => i[0], (i, v) => i[0] = v);
            Equal(null, items[0][0], "duplicated serials are not trusted"); Equal("EDID:Y:2", items[2][0], "unique serial kept");
            // Settings follow the monitor across ports by EDID, but not a different monitor on the same port.
            var prefs = new AppSettings();
            using (var device = Device())
            {
                device.Instance = "DISPLAY\\AOC\\PORT1"; device.Identity = "EDID:AOC2703:ABC123";
                var entry = prefs.For(device); entry.Minimum = 20;
                device.Instance = "DISPLAY\\AOC\\PORT2"; Equal(entry, prefs.For(device), "profile follows monitor to another GPU port");
                device.Identity = "EDID:AOC2703:OTHER"; Equal(false, prefs.For(device) == entry, "other monitor on same port gets its own profile");
            }
            // Plan import resolves a moved monitor by EDID.
            var live = SampleDesktop(); live.Screens[1].Identity = "EDID:Z:9"; live.Screens[1].Id = "new-path";
            var imported = new DesktopPlan { Screens = WireXml.Unpack<DesktopState>(WireXml.Pack(SampleDesktop())).Screens };
            imported.Screens[1].Identity = "EDID:Z:9";
            DisplayConsoleForm.Resolve(imported, live); Equal("new-path", imported.Screens[1].Id, "plan follows monitor by EDID");
            // Target enable / disable validation.
            var withTargets = SampleDesktop();
            withTargets.Targets.Add(new DesktopTarget { Id = "stable-0", Active = true }); withTargets.Targets.Add(new DesktopTarget { Id = "off", Active = false });
            new DesktopPlan { EnableTarget = "off" }.Validate(withTargets); Equal(true, true, "enable inactive output");
            Action<DesktopPlan, string> rejectTarget = (value, name) => { bool failed = false; try { value.Validate(withTargets); } catch (InvalidDataException) { failed = true; } Equal(true, failed, name); };
            rejectTarget(new DesktopPlan { DisableTarget = "stable-0" }, "cannot disable the last active output");
            rejectTarget(new DesktopPlan { EnableTarget = "stable-0" }, "cannot enable active output");
            rejectTarget(new DesktopPlan { EnableTarget = "off", Topology = 4 }, "target switch is standalone");
            // Scale / SDR / WCG validation.
            var edit = new DesktopPlan { Screens = WireXml.Unpack<DesktopState>(WireXml.Pack(state)).Screens };
            edit.Screens[0].Scale = 150; bool rejected = false; try { edit.Validate(state); } catch (InvalidDataException) { rejected = true; }
            Equal(true, rejected, "unsupported scale rejected");
            edit = new DesktopPlan { Screens = WireXml.Unpack<DesktopState>(WireXml.Pack(state)).Screens };
            edit.Screens[0].SdrWhiteLevel = 200; rejected = false; try { edit.Validate(state); } catch (InvalidDataException) { rejected = true; }
            Equal(true, rejected, "SDR brightness requires HDR");
            Equal("59.94 Hz", new DesktopScreen { RefreshNumerator = 60000, RefreshDenominator = 1001 }.ExactRefresh, "fractional refresh kept");
            // Gamma ramps.
            var identity = GammaMath.Identity(); Equal((ushort)0, identity[0], "identity black"); Equal((ushort)65535, identity[255], "identity white");
            Equal(identity[128], identity[256 + 128], "identity is channel neutral");
            var warm = GammaMath.Ramp(new ColorProfile { Temperature = 3000 });
            Equal(true, warm[512 + 255] < warm[255], "warm temperature reduces blue");
            // DDC capability parsing.
            var listed = MonitorHardware.Listed("(prot(monitor)vcp(02 04 05 08 10 12 14(05 08 0B) 60(0F 11) d6(01 04)))");
            Equal(true, listed.Contains(0x04) && listed.Contains(0x14) && listed.Contains(0xd6), "VCP codes listed");
            Equal(false, listed.Contains(0x0b), "nested values are not codes");
            Equal(3, MonitorHardware.Values("vcp(14(05 08 0B))", 0x14).Count, "discrete values parsed");
            // Night light blob (known layout) toggles symmetrically.
            var blob = new byte[] { 0x43, 0x42, 1, 0, 0x0a, 2, 1, 0, 0x2a, 6, 1, 2, 3, 4, 5, 0x2a, 0x2b, 0x0e, 0x13, 0x43, 0x42, 1, 0, 0xc2, 0x0a, 0, 0xca, 0x14, 0x0e, 0x15, 0, 0xca, 0x1e, 0x0e, 7, 0, 0xcf, 0x28, 0, 0, 0, 0, 0, 0 };
            Equal(false, NightLight.Parse(blob), "night light off");
            var on = NightLight.Toggle(blob, true); Equal(true, NightLight.Parse(on), "night light on"); Equal(blob.Length + 2, on.Length, "enabled flag inserted");
            Equal(false, NightLight.Parse(NightLight.Toggle(on, false)), "night light off again");
            rejected = false; try { NightLight.Toggle(new byte[40], true); } catch (InvalidOperationException) { rejected = true; }
            Equal(true, rejected, "unknown night light layout is never modified");
            Equal(2, GpuPreferences.ParsePreference("SwapEffectUpgradeEnable=1;GpuPreference=2;"), "GPU preference parsed");
            Equal("GpuPreference=1;SwapEffectUpgradeEnable=1;", GpuPreferences.Merge("GpuPreference=2;SwapEffectUpgradeEnable=1;", 1), "other DirectX options preserved");
            Equal(25, ColorFilters.Matrix(3).Length, "color filter matrix size");
        }
        private static void ConsoleUiTest(string output)
        {
            using (var form = new DisplayConsoleForm(false))
            {
                form.LoadState(SampleDesktop()); form.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(form.Location, Point.Empty, form.Size);
                    bitmap.Save(Path.Combine(output, "screen-light-console.png"));
                }
                Equal(true, form.Controls.OfType<TabControl>().Single().TabPages.Count == 7, "display console exposes all capability groups");
                form.Close();
                for (int i = 0; i < 100 && !form.IsDisposed; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                Equal(true, form.IsDisposed, "console can close without touching display hardware");
                Console.WriteLine("PASS: display console renders and closes without native display writes; " + checks + " checks");
            }
        }
        private static void TrayTests()
        {
            using (var form = new MainForm(true, false, true))
            {
                form.Show(); Application.DoEvents();
                Equal(false, form.Visible, "tray launch never displays the main window");
                Equal(true, form.IsHandleCreated, "tray launch keeps a hidden window for hotkeys");
                IntPtr originalWindow = form.Handle;
                form.Restore(); Application.DoEvents();
                Equal(true, form.Visible, "tray activation opens hidden panel");
                Equal(originalWindow, form.Handle, "opening tray panel preserves hotkey window");
                if (Type.GetType("Mono.Runtime") == null)
                {
                    form.WindowState = FormWindowState.Minimized; Application.DoEvents();
                    Equal(false, form.Visible, "minimize hides panel into tray");
                    Equal(originalWindow, form.Handle, "minimize preserves hotkey registrations");
                }
                else form.Hide(); // Xvfb without a window manager cannot generate Windows minimize events.
                form.Restore(); Application.DoEvents();
                Equal(FormWindowState.Normal, form.WindowState, "tray activation restores normal state before showing");
                Equal(true, form.Visible, "minimized panel can reopen from tray");
                var exit = form.ExitAsync();
                for (int i = 0; i < 200 && !exit.IsCompleted; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                Equal(true, exit.IsCompleted, "tray exit completes cleanup"); exit.GetAwaiter().GetResult();
                Equal(true, form.IsDisposed, "tray exit disposes main window");
            }
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
        private static async Task AllControlTests()
        { await ControlTests(); await HungHardwareUiTest(); }
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
        private static async Task HungHardwareUiTest()
        {
            using (var connection = new HardwareConnection(WorkerStart()))
            using (var form = new MainForm(true, false))
            using (var device = Device(new RemoteChannel(connection, 0, 0, "hung driver", 1500)))
            using (var healthy = Device(new FakeChannel { Name = "independent healthy driver" }))
            {
                connection.Request("ARM", 3000);
                device.Bounds = new Rectangle(-12000, -12000, 30, 30);
                healthy.ScreenName = "healthy-screen"; healthy.Bounds = new Rectangle(-12100, -12000, 30, 30);
                form.AttachDevice(device); form.AttachDevice(healthy); form.preferences.Smooth = false;
                form.SetAll(45); var work = form.ApplyPending();
                for (int i = 0; i < 20 && healthy.HardwareValue != 45; i++) await Task.Delay(20);
                Equal(45, healthy.HardwareValue, "healthy screen writes while another screen is still blocked");
                Equal(false, work.IsCompleted, "healthy write does not wait for the hung screen deadline");
                await work;
                Equal(true, form.IsIdle, "UI releases I/O lock after hung driver");
                Equal(true, device.UsingSoftware, "hung driver falls back to software dimming");
                Equal(45, device.Value, "software target applied after hardware timeout");
                form.ResetDimming(); await form.ExitAsync();
                Equal(true, form.IsDisposed, "exit completes after driver timeout");
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
