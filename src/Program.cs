using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AgentSleep
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                int exitCode;
                if (RecoveryCommands.TryRun(args, out exitCode))
                {
                    if (args[0] == "--restore")
                    {
                        if (args.Length != 1) Report("Usage: AgentSleep.exe --restore");
                        else if (exitCode == 0) Report("No display rollback remains pending.");
                        else if (exitCode == 3) Report("Windows restored a usable fallback. The original display layout is still pending.");
                        else Report("The saved display layout could not be verified. Retry from the unlocked local Windows desktop.");
                    }
                    return exitCode;
                }
                if (args.Length != 0)
                {
                    using (var output = new StringWriter())
                    {
                        if (args[0] == "--dry-run")
                            exitCode = TrayDryRun.Run(args.Skip(1).ToArray(), output);
                        else
                        {
                            output.WriteLine("Usage: AgentSleep.exe [--dry-run [--target TARGET-ID] | --restore]");
                            exitCode = 2;
                        }
                        Report(output.ToString());
                        return exitCode;
                    }
                }

                using (var instance = new Mutex(false, @"Local\AgentSleep.Tray"))
                {
                    try { if (!instance.WaitOne(0)) return 0; }
                    catch (AbandonedMutexException) { }
                    try
                    {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        using (var tray = new TrayContext(new NativeDisplays())) Application.Run(tray);
                    }
                    finally { instance.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception error)
            {
                Report("AgentSleep could not start. " + error.Message);
                return 2;
            }
        }

        private static void Report(string text)
        {
            IntPtr output = GetStdHandle(-11);
            if ((output != IntPtr.Zero && output != new IntPtr(-1)) || AttachConsole(-1)) Console.WriteLine(text.TrimEnd());
            else MessageBox.Show(text.TrimEnd(), "AgentSleep", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    }
}
