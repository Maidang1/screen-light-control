using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenLight
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--diagnose")
            {
                try
                {
                    using (var service = new MonitorService())
                    {
                        service.Discover();
                        using (var writer = new StreamWriter(args[1], false, System.Text.Encoding.UTF8))
                        {
                            writer.WriteLine("Screen Light v3 | Read-only diagnostics | " + DateTime.Now.ToString("s"));
                            foreach (var device in service.Devices) writer.WriteLine(device.Name + " | " + device.ScreenName + " | " + device.Value + "% | channels=" + device.Channels.Count);
                            foreach (var message in service.Diagnostics) writer.WriteLine(message);
                        }
                    }
                }
                catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\ScreenLight.Desktop", out created))
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (!created)
                {
                    IntPtr window = FindWindow(null, "屏幕亮度 · Screen Light");
                    if (window != IntPtr.Zero) { ShowWindow(window, 9); SetForegroundWindow(window); }
                    else MessageBox.Show("程序正在启动，请稍候点击托盘中的紫色太阳图标。", "屏幕亮度");
                    return;
                }
                Application.Run(new MainForm());
            }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    }
}
