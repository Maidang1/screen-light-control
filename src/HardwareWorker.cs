using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenLight
{
    // Native monitor APIs cannot safely be cancelled with CancellationToken or Thread.Abort.
    // Keep every WMI/DXVA call, including handle destruction, outside the UI process.
    internal sealed class HardwareConnection : IDisposable
    {
        private Process process;
        private bool failed;
        private readonly bool preserveWorker;
        private readonly NamedPipeServerStream pipe;
        private StreamReader input;
        private StreamWriter output;
        internal HardwareConnection(ProcessStartInfo start, bool preserveWorker = false)
        {
            this.preserveWorker = preserveWorker;
            start.UseShellExecute = false; start.CreateNoWindow = true;
            string pipeName = "ScreenLight-" + Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            start.Arguments += " " + pipeName;
            try { process = Process.Start(start); }
            catch { pipe.Dispose(); throw; }
        }
        internal string Request(string command, int timeout)
        {
            if (failed || process == null) throw new IOException("硬件进程已停用，请重新检测后重试");
            try
            {
                // Include pipe writes in the deadline: a stuck worker may stop draining the pipe.
                var response = Task.Run(() =>
                {
                    if (input == null)
                    {
                        pipe.WaitForConnection();
                        input = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                        output = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                    }
                    output.WriteLine(command);
                    string line = input.ReadLine();
                    if (line == null) throw new IOException("硬件进程意外退出");
                    if (line.StartsWith("ERROR ")) throw new InvalidOperationException(Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(6))));
                    return line;
                });
                // Observe failures even if the native operation finishes after our deadline.
                response.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                if (!response.Wait(timeout)) throw new TimeoutException("硬件调用超时，已停用硬件通道并回退软件调光");
                return response.GetAwaiter().GetResult();
            }
            catch (AggregateException ex)
            {
                // A normal API error can still fall back to another hardware channel.
                var error = ex.InnerException;
                if (!(error is InvalidOperationException)) Dispose();
                throw error;
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            failed = true;
            var child = process; process = null;
            if (child == null) return;
            // Brightness workers can be killed. Desktop trial workers keep their watchdog
            // alive until they restore the snapshot after the pipe disconnects.
            try { if (!preserveWorker && !child.HasExited) child.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            pipe.Dispose(); child.Dispose();
        }
    }

    internal sealed class RemoteChannel : IBrightnessChannel
    {
        private readonly HardwareConnection connection;
        private readonly string address;
        private readonly int timeout;
        public string Name { get; private set; }
        internal RemoteChannel(HardwareConnection connection, int device, int channel, string name, int timeout = 3000)
        { this.connection = connection; address = device + " " + channel; Name = name; this.timeout = timeout; }
        public int Read() { return int.Parse(connection.Request("READ " + address, timeout), System.Globalization.CultureInfo.InvariantCulture); }
        public void Write(int value) { connection.Request("WRITE " + address + " " + value, timeout); }
    }

    internal sealed class MonitorService : IDisposable
    {
        internal readonly List<MonitorDevice> Devices = new List<MonitorDevice>();
        internal readonly List<string> Diagnostics = new List<string>();
        private readonly List<HardwareConnection> connections = new List<HardwareConnection>();
        private readonly Func<HardwareConnection> connect;
        private readonly Func<MonitorDevice[]> screens;
        private readonly int discoveryTimeout;
        internal MonitorService(Func<HardwareConnection> connect = null, Func<MonitorDevice[]> screens = null, int discoveryTimeout = 8000)
        {
            this.connect = connect ?? (() => new HardwareConnection(new ProcessStartInfo(Application.ExecutablePath,
                "--hardware-worker " + Process.GetCurrentProcess().Id)));
            this.screens = screens ?? (() => Screen.AllScreens.Select(screen => new MonitorDevice {
                Name = "显示器", ScreenName = screen.DeviceName, Instance = screen.DeviceName,
                Bounds = screen.Bounds, Primary = screen.Primary, UsingSoftware = true }).ToArray());
            this.discoveryTimeout = discoveryTimeout;
        }
        private sealed class ProbeResult
        {
            internal HardwareConnection Connection;
            internal readonly List<MonitorDevice> Devices = new List<MonitorDevice>();
            internal readonly List<string> Diagnostics = new List<string>();
        }
        internal void Discover()
        {
            Dispose(); Diagnostics.Clear();
            // A stalled screen must not consume the deadline or disable channels of other screens.
            // Dedicated coordinators avoid blocking the thread pool needed by pipe requests.
            var probes = screens().Select(screen => Task.Factory.StartNew(() => DiscoverScreen(screen),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(probes);
            foreach (var probe in probes)
            {
                var result = probe.Result;
                if (result.Connection != null) connections.Add(result.Connection);
                Devices.AddRange(result.Devices); Diagnostics.AddRange(result.Diagnostics);
            }
        }
        private ProbeResult DiscoverScreen(MonitorDevice screen)
        {
            var result = new ProbeResult();
            try
            {
                result.Connection = connect();
                string reply = result.Connection.Request("DISCOVER " + Convert.ToBase64String(Encoding.UTF8.GetBytes(screen.ScreenName)), discoveryTimeout);
                using (var stream = new MemoryStream(Convert.FromBase64String(reply)))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    int count = reader.ReadInt32();
                    if (count < 1 || count > 32) throw new IOException("未找到匹配的物理显示器");
                    for (int i = 0; i < count; i++)
                    {
                        var device = new MonitorDevice();
                        result.Devices.Add(device);
                        device.Name = reader.ReadString(); device.ScreenName = reader.ReadString(); device.Instance = reader.ReadString();
                        if (device.ScreenName != screen.ScreenName) throw new IOException("显示器拓扑已变化，请重新检测");
                        device.Bounds = new Rectangle(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                        device.Primary = reader.ReadBoolean(); device.Value = device.HardwareValue = reader.ReadInt32();
                        device.Error = reader.ReadString(); device.UsingSoftware = reader.ReadBoolean();
                        device.Identity = reader.ReadString(); if (device.Identity.Length == 0) device.Identity = null;
                        int channels = reader.ReadInt32();
                        for (int c = 0; c < channels; c++) device.Channels.Add(new RemoteChannel(result.Connection, i, c, reader.ReadString()));
                    }
                    int messages = reader.ReadInt32();
                    for (int i = 0; i < messages; i++) result.Diagnostics.Add(reader.ReadString());
                }
            }
            catch (Exception ex)
            {
                if (result.Connection != null) { result.Connection.Dispose(); result.Connection = null; }
                foreach (var device in result.Devices) device.Dispose(); result.Devices.Clear();
                result.Diagnostics.Add(screen.ScreenName + " 硬件检测失败，使用软件调光：" + ex.Message);
                screen.UsingSoftware = true; screen.Error = ex.Message; result.Devices.Add(screen);
            }
            return result;
        }
        public void Dispose()
        {
            foreach (var connection in connections) connection.Dispose(); connections.Clear();
            foreach (var device in Devices) device.Dispose(); Devices.Clear();
        }
    }

    internal static class HardwareWorker
    {
        internal static bool TryRun(string[] args)
        {
            if (args.Length != 3 || args[0] != "--hardware-worker") return false;
            int parentId;
            if (!int.TryParse(args[1], out parentId)) return true;
            using (var desktop = new DesktopNativeSession())
            {
                // If the UI crashes/exits during a stuck driver call, do not leave an orphan worker.
                var watchdog = new Thread(() =>
                {
                    try { using (var parent = Process.GetProcessById(parentId)) while (!parent.HasExited) Thread.Sleep(500); }
                    catch (ArgumentException) { }
                    try { desktop.Dispose(); } finally { Environment.Exit(0); }
                });
                watchdog.IsBackground = true; watchdog.Start();
                // Named pipes also work for GUI-subsystem executables with no console handles.
                using (var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut))
                {
                    pipe.Connect(8000);
                    using (var input = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    using (var output = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                    using (var service = new NativeMonitorService())
                    {
                        string command;
                        while ((command = input.ReadLine()) != null)
                        {
                            try
                            {
                                string[] parts = command.Split(' ');
                                string reply;
                                if (parts[0].StartsWith("DESKTOP_")) reply = desktop.Request(parts[0], parts.Length > 1 ? parts[1] : "");
                                else if (parts[0] == "FEATURE_PROBE") reply = WireXml.Pack(MonitorHardware.Probe(Decode(parts, 1), Decode(parts, 2)));
                                else if (parts[0] == "FEATURE_WRITE") reply = Convert.ToBase64String(Encoding.UTF8.GetBytes(MonitorHardware.Write(Decode(parts, 1), Decode(parts, 4), int.Parse(parts[2]), int.Parse(parts[3]))));
                                else if (parts[0] == "DISPLAY_EXTRA") reply = WireXml.Pack(DisplayExtras.Execute(WireXml.Unpack<DisplayCommand>(parts[1])));
                                else if (parts[0] == "DISCOVER")
                                {
                                    if (parts.Length != 2) throw new InvalidOperationException("Missing display target");
                                    service.Discover(Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
                                    reply = Snapshot(service);
                                }
                                else
                                {
                                    var channel = service.Devices[int.Parse(parts[1])].Channels[int.Parse(parts[2])];
                                    if (parts[0] == "READ") reply = channel.Read().ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    else if (parts[0] == "WRITE") { channel.Write(int.Parse(parts[3])); reply = "OK"; }
                                    else throw new InvalidOperationException("Unknown hardware command");
                                }
                                output.WriteLine(reply);
                            }
                            catch (Exception ex) { output.WriteLine("ERROR " + Convert.ToBase64String(Encoding.UTF8.GetBytes(ex.Message))); }
                        }
                    }
                }
            }
            return true;
        }
        // Optional base64 UTF-8 argument; absent or "-" means null.
        private static string Decode(string[] parts, int index)
        {
            return parts.Length <= index || parts[index] == "-" ? null : Encoding.UTF8.GetString(Convert.FromBase64String(parts[index]));
        }
        internal static string Encode(string value) { return value == null ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
        internal static string Snapshot(NativeMonitorService service)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(service.Devices.Count);
                    foreach (var device in service.Devices)
                    {
                        writer.Write(device.Name ?? ""); writer.Write(device.ScreenName ?? ""); writer.Write(device.Instance ?? "");
                        writer.Write(device.Bounds.X); writer.Write(device.Bounds.Y); writer.Write(device.Bounds.Width); writer.Write(device.Bounds.Height);
                        writer.Write(device.Primary); writer.Write(device.Value); writer.Write(device.Error ?? ""); writer.Write(device.UsingSoftware); writer.Write(device.Identity ?? "");
                        writer.Write(device.Channels.Count); foreach (var channel in device.Channels) writer.Write(channel.Name);
                    }
                    writer.Write(service.Diagnostics.Count); foreach (var message in service.Diagnostics) writer.Write(message);
                }
                return Convert.ToBase64String(stream.ToArray());
            }
        }
    }
}
