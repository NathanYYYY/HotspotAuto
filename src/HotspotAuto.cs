using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyTitle("移动热点自动开启")]
[assembly: AssemblyDescription("开机自动开启并保持 Windows 移动热点")]

namespace HotspotAuto
{
    internal static class Program
    {
        public const string AppName = "HotspotAuto";
        public const string TaskName = "HotspotAuto";
        public const string Version = "1.0.0";
        private static readonly object LogSync = new object();
        private static Mutex _keepAliveMutex;

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName); }
        }

        public static string LogFile
        {
            get { return Path.Combine(DataDir, "hotspot.log"); }
        }

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName); }
        }

        public static string InstalledExe
        {
            get { return Path.Combine(InstallDir, AppName + ".exe"); }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "gui";
            switch (mode)
            {
                case "run":
                    return RunStartupMonitor();
                case "on":
                    return CommandOn();
                case "off":
                    return CommandOff();
                case "status":
                    return CommandStatus();
                case "install":
                    return Setup.Install(args);
                case "uninstall":
                    return Setup.Uninstall(args);
                case "help":
                case "-h":
                case "--help":
                case "/?":
                    PrintUsage();
                    return 0;
                case "version":
                case "--version":
                    ConsoleOut(AppName + " " + Version);
                    return 0;
                default:
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm());
                    return 0;
            }
        }

        // ---------------------------------------------------------------- logging

        private static void PrintUsage()
        {
            ConsoleOut(AppName + " " + Version);
            ConsoleOut("用法:");
            ConsoleOut("  HotspotAuto.exe              打开图形界面");
            ConsoleOut("  HotspotAuto.exe run          启动时监控（供计划任务调用）");
            ConsoleOut("  HotspotAuto.exe on           立即开启热点");
            ConsoleOut("  HotspotAuto.exe off          立即关闭热点");
            ConsoleOut("  HotspotAuto.exe status       查看状态");
            ConsoleOut("  HotspotAuto.exe install      安装开机自启（自动提权）");
            ConsoleOut("  HotspotAuto.exe uninstall    卸载开机自启（自动提权）");
        }

        public static void Log(string format, params object[] args)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string message = args != null && args.Length > 0 ? string.Format(format, args) : format;
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " [" + Process.GetCurrentProcess().SessionId + "] " + message;
                lock (LogSync)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(LogFile);
                        if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                        {
                            try { File.Copy(LogFile, LogFile + ".1", true); } catch { }
                            try { File.Delete(LogFile); } catch { }
                        }
                    }
                    catch { }
                    File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- helpers

        public static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static int RunTool(string file, string arguments, bool quiet)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (!quiet || p.ExitCode != 0)
                    {
                        Log("tool: {0} {1} -> {2} {3} {4}", file, arguments, p.ExitCode, stdout.Trim(), stderr.Trim());
                    }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Log("tool error: {0} {1}: {2}", file, arguments, ex.Message);
                return -1;
            }
        }

        public static int Elevate(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, arguments);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return 1;
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Win32Exception)
            {
                return 2; // cancelled by the user
            }
            catch (Exception ex)
            {
                Log("elevate failed: " + ex.Message);
                return 1;
            }
        }

        public static bool IsInstalled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\HotspotAuto"))
                {
                    if (k == null) return false;
                    string dir = k.GetValue("InstallDir") as string;
                    if (string.IsNullOrEmpty(dir)) return false;
                    return File.Exists(Path.Combine(dir, AppName + ".exe"));
                }
            }
            catch { return false; }
        }

        public static void StopRunningKeepAlive()
        {
            try
            {
                string pidFile = Path.Combine(DataDir, "keepalive.pid");
                if (!File.Exists(pidFile)) return;
                int pid;
                if (int.TryParse(File.ReadAllText(pidFile).Trim(), out pid)
                    && pid != Process.GetCurrentProcess().Id)
                {
                    try
                    {
                        using (Process p = Process.GetProcessById(pid))
                        {
                            if (string.Equals(p.ProcessName, AppName, StringComparison.OrdinalIgnoreCase))
                            {
                                p.Kill();
                                p.WaitForExit(3000);
                                Log("stopped monitor pid=" + pid);
                            }
                        }
                    }
                    catch { }
                }
                try { File.Delete(pidFile); } catch { }
            }
            catch { }
        }

        // ---------------------------------------------------------------- WinRT

        private static bool IsUsable(ConnectionProfile profile)
        {
            if (profile == null) return false;
            try
            {
                return profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch { return false; }
        }

        public static ConnectionProfile GetInternetProfile()
        {
            try
            {
                ConnectionProfile p = NetworkInformation.GetInternetConnectionProfile();
                if (IsUsable(p)) return p;
                IReadOnlyList<ConnectionProfile> all = NetworkInformation.GetConnectionProfiles();
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (IsUsable(all[i])) return all[i];
                    }
                }
            }
            catch { }
            return null;
        }

        public static T Await<T>(IAsyncOperation<T> operation)
        {
            while (operation.Status == AsyncStatus.Started) Thread.Sleep(100);
            if (operation.Status == AsyncStatus.Error) throw operation.ErrorCode;
            if (operation.Status == AsyncStatus.Canceled) throw new OperationCanceledException("WinRT operation canceled");
            return operation.GetResults();
        }

        public static bool TryEnsureOn(out string detail)
        {
            detail = null;
            ConnectionProfile profile = GetInternetProfile();
            if (profile == null)
            {
                detail = "没有可用的 Internet 连接";
                return false;
            }
            NetworkOperatorTetheringManager manager =
                NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            TetheringOperationalState state = manager.TetheringOperationalState;
            if (state == TetheringOperationalState.On)
            {
                detail = "已经开启";
                return true;
            }
            if (state == TetheringOperationalState.InTransition) Thread.Sleep(1500);
            NetworkOperatorTetheringOperationResult result = Await(manager.StartTetheringAsync());
            detail = result.Status.ToString();
            if (!string.IsNullOrEmpty(result.AdditionalErrorMessage))
            {
                detail += " (" + result.AdditionalErrorMessage + ")";
            }
            return result.Status == TetheringOperationStatus.Success
                || result.Status == TetheringOperationStatus.AlreadyOn;
        }

        public static bool TryEnsureOff(out string detail)
        {
            detail = null;
            ConnectionProfile profile = GetInternetProfile();
            if (profile == null)
            {
                detail = "没有可用的 Internet 连接";
                return false;
            }
            NetworkOperatorTetheringManager manager =
                NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (manager.TetheringOperationalState == TetheringOperationalState.Off)
            {
                detail = "已经关闭";
                return true;
            }
            NetworkOperatorTetheringOperationResult result = Await(manager.StopTetheringAsync());
            detail = result.Status.ToString();
            if (!string.IsNullOrEmpty(result.AdditionalErrorMessage))
            {
                detail += " (" + result.AdditionalErrorMessage + ")";
            }
            return result.Status == TetheringOperationStatus.Success;
        }

        public sealed class StatusInfo
        {
            public bool Available;
            public string StateText = "未知";
            public string Ssid;
            public int Clients;
            public string Upstream;
            public string Error;
        }

        public static StatusInfo GetStatusInfo()
        {
            StatusInfo info = new StatusInfo();
            try
            {
                ConnectionProfile profile = GetInternetProfile();
                ConnectionProfile any = profile;
                if (any == null)
                {
                    IReadOnlyList<ConnectionProfile> all = NetworkInformation.GetConnectionProfiles();
                    if (all != null && all.Count > 0) any = all[0];
                }
                info.Upstream = profile != null ? profile.ProfileName : null;
                if (any == null)
                {
                    info.Error = "没有网络连接";
                    return info;
                }
                NetworkOperatorTetheringManager manager =
                    NetworkOperatorTetheringManager.CreateFromConnectionProfile(any);
                info.Available = true;
                switch (manager.TetheringOperationalState)
                {
                    case TetheringOperationalState.On: info.StateText = "已开启"; break;
                    case TetheringOperationalState.Off: info.StateText = "已关闭"; break;
                    case TetheringOperationalState.InTransition: info.StateText = "切换中"; break;
                    default: info.StateText = "未知"; break;
                }
                try
                {
                    NetworkOperatorTetheringAccessPointConfiguration cfg = manager.GetCurrentAccessPointConfiguration();
                    info.Ssid = cfg.Ssid;
                }
                catch { }
                try { info.Clients = (int)manager.ClientCount; } catch { }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        // ---------------------------------------------------------------- modes

        private static int RunStartupMonitor()
        {
            bool created = false;
            try
            {
                _keepAliveMutex = new Mutex(true, @"Global\HotspotAutoKeepAlive", out created);
                if (!created && !_keepAliveMutex.WaitOne(0))
                {
                    Log("another monitor instance is already running, exit");
                    return 0;
                }
            }
            catch (UnauthorizedAccessException)
            {
                Log("mutex access denied, continuing without single-instance guard");
            }

            string pidFile = Path.Combine(DataDir, "keepalive.pid");
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(pidFile, Process.GetCurrentProcess().Id.ToString(), Encoding.ASCII);
            }
            catch { }

            Log("monitor started: user={0}, session={1}",
                WindowsIdentity.GetCurrent().Name, Process.GetCurrentProcess().SessionId);

            DateTime started = DateTime.Now;
            DateTime? onAt = null;
            int failures = 0;
            DateTime lastFailureLog = DateTime.MinValue;

            try
            {
                while (true)
                {
                    string detail = null;
                    bool ok = false;
                    try
                    {
                        ok = TryEnsureOn(out detail);
                    }
                    catch (Exception ex)
                    {
                        detail = ex.GetType().Name + ": " + ex.Message;
                    }

                    DateTime now = DateTime.Now;
                    if (ok)
                    {
                        if (onAt == null)
                        {
                            onAt = now;
                            Log("hotspot is on ({0}); monitoring for 60s", detail);
                        }
                        else if (failures > 0)
                        {
                            Log("hotspot is on again ({0})", detail);
                        }
                        failures = 0;
                    }
                    else
                    {
                        failures++;
                        if (failures == 1 || (now - lastFailureLog).TotalMinutes >= 5)
                        {
                            Log("hotspot ensure failed (attempt {0}): {1}", failures, detail);
                            lastFailureLog = now;
                        }
                    }

                    if (onAt != null && (now - onAt.Value).TotalSeconds >= 60)
                    {
                        Log("monitor window finished (60s); hotspot stays on; exiting");
                        break;
                    }
                    if (onAt == null && (now - started).TotalSeconds >= 120)
                    {
                        Log("no hotspot after 120s; giving up and exiting");
                        break;
                    }

                    int sleepMs = 10000;
                    if (onAt != null)
                    {
                        int remaining = 60000 - (int)(now - onAt.Value).TotalMilliseconds;
                        if (remaining < sleepMs) sleepMs = remaining;
                    }
                    else
                    {
                        int remaining = 120000 - (int)(now - started).TotalMilliseconds;
                        if (remaining < sleepMs) sleepMs = remaining;
                    }
                    if (sleepMs < 1000) sleepMs = 1000;
                    Thread.Sleep(sleepMs);
                }
            }
            finally
            {
                try { File.Delete(pidFile); } catch { }
                try { if (_keepAliveMutex != null) _keepAliveMutex.ReleaseMutex(); } catch { }
            }

            return 0;
        }

        private static int CommandOn()
        {
            try
            {
                string detail;
                bool ok = TryEnsureOn(out detail);
                Log("manual on: {0} ({1})", ok ? "ok" : "failed", detail);
                ConsoleOut(ok ? "热点已开启" : "开启失败：" + detail);
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Log("manual on error: " + ex);
                ConsoleOut("开启失败：" + ex.Message);
                return 1;
            }
        }

        private static int CommandOff()
        {
            try
            {
                string detail;
                bool ok = TryEnsureOff(out detail);
                Log("manual off: {0} ({1})", ok ? "ok" : "failed", detail);
                ConsoleOut(ok ? "热点已关闭" : "关闭失败：" + detail);
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Log("manual off error: " + ex);
                ConsoleOut("关闭失败：" + ex.Message);
                return 1;
            }
        }

        private static int CommandStatus()
        {
            StatusInfo s = GetStatusInfo();
            string text = s.Error != null
                ? "状态：读取失败（" + s.Error + "）"
                : string.Format("状态：{0}；热点：{1}；已连接：{2} 台；共享网络：{3}",
                    s.StateText, string.IsNullOrEmpty(s.Ssid) ? "-" : s.Ssid, s.Clients,
                    s.Upstream ?? "未连接");
            Log("status: " + text);
            ConsoleOut(text);
            return s.Error == null ? 0 : 1;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        private static void ConsoleOut(string text)
        {
            try
            {
                IntPtr handle = GetStdHandle(-11);
                IntPtr invalid = new IntPtr(-1);
                if (handle == IntPtr.Zero || handle == invalid)
                {
                    AttachConsole(-1);
                    handle = GetStdHandle(-11);
                }
                if (handle != IntPtr.Zero && handle != invalid)
                {
                    using (StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                    {
                        writer.AutoFlush = true;
                        writer.WriteLine(text);
                    }
                }
            }
            catch { }
        }
    }

    // -------------------------------------------------------------------- setup

    internal static class Setup
    {
        public static int Install(string[] args)
        {
            if (!Program.IsAdmin()) return Program.Elevate("install" + (HasQuiet(args) ? " --quiet" : ""));
            try
            {
                Program.Log("install: begin");
                Program.RunTool("schtasks.exe", "/end /tn \"" + Program.TaskName + "\"", true);
                Program.StopRunningKeepAlive();
                Thread.Sleep(700);

                Directory.CreateDirectory(Program.InstallDir);
                string self = Application.ExecutablePath;
                if (!string.Equals(self, Program.InstalledExe, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(self, Program.InstalledExe, true);
                }

                string xmlPath = Path.Combine(Path.GetTempPath(), "HotspotAuto.task.xml");
                File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
                int rc = Program.RunTool("schtasks.exe",
                    "/create /tn \"" + Program.TaskName + "\" /xml \"" + xmlPath + "\" /f", false);
                try { File.Delete(xmlPath); } catch { }
                if (rc != 0)
                {
                    Program.Log("install: schtasks create failed, rc=" + rc);
                    if (!HasQuiet(args))
                    {
                        MessageBox.Show("创建开机任务失败（代码 " + rc + "）。\n请查看日志：" + Program.LogFile,
                            "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return rc;
                }

                using (RegistryKey run = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    run.SetValue(Program.AppName, "\"" + Program.InstalledExe + "\" run --logon");
                }
                using (RegistryKey marker = Registry.CurrentUser.CreateSubKey(@"Software\HotspotAuto"))
                {
                    marker.SetValue("InstallDir", Program.InstallDir);
                    marker.SetValue("Version", Program.Version);
                }

                Program.RunTool("schtasks.exe", "/run /tn \"" + Program.TaskName + "\"", true);
                Program.Log("install: done");
                if (!HasQuiet(args))
                {
                    MessageBox.Show("安装完成。\n\n以后每次开机都会自动开启移动热点，登录前（锁屏界面）也可以使用。",
                        "移动热点自动开启", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Program.Log("install error: " + ex);
                if (!HasQuiet(args))
                {
                    MessageBox.Show("安装失败：" + ex.Message, "安装失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 1;
            }
        }

        public static int Uninstall(string[] args)
        {
            if (!Program.IsAdmin()) return Program.Elevate("uninstall" + (HasQuiet(args) ? " --quiet" : ""));
            try
            {
                Program.Log("uninstall: begin");
                Program.RunTool("schtasks.exe", "/end /tn \"" + Program.TaskName + "\"", true);
                Program.RunTool("schtasks.exe", "/delete /tn \"" + Program.TaskName + "\" /f", true);
                Program.StopRunningKeepAlive();
                Thread.Sleep(500);

                try
                {
                    using (RegistryKey run = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (run != null) run.DeleteValue(Program.AppName, false);
                    }
                }
                catch { }
                try
                {
                    using (RegistryKey marker = Registry.CurrentUser.OpenSubKey(@"Software\HotspotAuto", true))
                    {
                        if (marker != null) marker.DeleteValue("InstallDir", false);
                    }
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\HotspotAuto", false);
                }
                catch { }

                string cmd = "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + Program.InstallDir + "\""
                    + " & rmdir /s /q \"" + Program.DataDir + "\"";
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", cmd);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);

                Program.Log("uninstall: done");
                if (!HasQuiet(args))
                {
                    MessageBox.Show("已卸载。\n\n如果当前程序正在从安装目录运行，窗口关闭后安装目录会被自动删除。",
                        "移动热点自动开启", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Program.Log("uninstall error: " + ex);
                if (!HasQuiet(args))
                {
                    MessageBox.Show("卸载失败：" + ex.Message, "卸载失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 1;
            }
        }

        private static bool HasQuiet(string[] args)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--quiet", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string JoinArgs(string[] args)
        {
            if (args == null || args.Length == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(args[i]);
            }
            return sb.ToString();
        }

        private static string BuildTaskXml()
        {
            string exe = Program.InstalledExe;
            return @"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.3"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>HotspotAuto</Author>
    <Description>开机自动开启并保持 Windows 移动热点（以 SYSTEM 运行，登录前即可用）</Description>
    <Version>1.0</Version>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
      <Delay>PT20S</Delay>
    </BootTrigger>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id=""0"" Path=""System""&gt;&lt;Select Path=""System""&gt;*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
      <Delay>PT20S</Delay>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>" + exe + @"</Command>
      <Arguments>run</Arguments>
    </Exec>
  </Actions>
</Task>";
        }
    }

    // --------------------------------------------------------------------- GUI

    internal sealed class MainForm : Form
    {
        private readonly Label _lblState;
        private readonly Label _lblSsid;
        private readonly Label _lblClients;
        private readonly Label _lblUpstream;
        private readonly Label _lblAuto;
        private readonly Button _btnInstall;
        private readonly System.Windows.Forms.Timer _timer;
        private bool _installed;
        private bool _busy;

        public MainForm()
        {
            Text = "移动热点自动开启";
            ClientSize = new Size(480, 386);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = new Label();
            title.Text = "移动热点自动开启";
            title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(24, 16);
            Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "开机自动开启 · 锁屏可用 · 1 分钟后自动退出";
            subtitle.ForeColor = Color.Gray;
            subtitle.AutoSize = true;
            subtitle.Location = new Point(26, 54);
            Controls.Add(subtitle);

            Panel line = new Panel();
            line.BackColor = Color.FromArgb(225, 225, 225);
            line.Location = new Point(26, 80);
            line.Size = new Size(428, 1);
            Controls.Add(line);

            _lblState = new Label();
            _lblState.Text = "状态：读取中…";
            _lblState.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            _lblState.AutoSize = true;
            _lblState.Location = new Point(24, 94);
            Controls.Add(_lblState);

            _lblSsid = new Label();
            _lblSsid.Text = "热点名称：—";
            _lblSsid.AutoSize = true;
            _lblSsid.Location = new Point(26, 128);
            Controls.Add(_lblSsid);

            _lblClients = new Label();
            _lblClients.Text = "已连接设备：—";
            _lblClients.AutoSize = true;
            _lblClients.Location = new Point(26, 152);
            Controls.Add(_lblClients);

            _lblUpstream = new Label();
            _lblUpstream.Text = "共享网络：—";
            _lblUpstream.AutoSize = true;
            _lblUpstream.Location = new Point(26, 176);
            Controls.Add(_lblUpstream);

            _lblAuto = new Label();
            _lblAuto.Text = "开机自启：未安装";
            _lblAuto.AutoSize = true;
            _lblAuto.Location = new Point(26, 200);
            Controls.Add(_lblAuto);

            Button btnOn = new Button();
            btnOn.Text = "立即开启";
            btnOn.Location = new Point(26, 232);
            btnOn.Size = new Size(110, 36);
            btnOn.Click += OnTurnOn;
            Controls.Add(btnOn);

            Button btnSettings = new Button();
            btnSettings.Text = "打开系统热点设置";
            btnSettings.Location = new Point(146, 232);
            btnSettings.Size = new Size(168, 36);
            btnSettings.Click += OnOpenSettings;
            Controls.Add(btnSettings);

            Button btnRefresh = new Button();
            btnRefresh.Text = "刷新";
            btnRefresh.Location = new Point(324, 232);
            btnRefresh.Size = new Size(110, 36);
            btnRefresh.Click += delegate { RefreshStatus(); };
            Controls.Add(btnRefresh);

            _btnInstall = new Button();
            _btnInstall.Text = "安装开机自启";
            _btnInstall.Location = new Point(26, 282);
            _btnInstall.Size = new Size(200, 40);
            _btnInstall.Click += OnInstallClick;
            Controls.Add(_btnInstall);

            Label hint = new Label();
            hint.Text = "安装后每次开机自动开启，检查 1 分钟后退出，热点保持开启。\n日志：" + Program.LogFile;
            hint.ForeColor = Color.Gray;
            hint.Font = new Font("Microsoft YaHei UI", 8F);
            hint.Location = new Point(26, 334);
            hint.Size = new Size(430, 44);
            Controls.Add(hint);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 3000;
            _timer.Tick += delegate { RefreshStatus(); };
            _timer.Start();

            Shown += delegate
            {
                _installed = Program.IsInstalled();
                UpdateInstallButton();
                RefreshStatus();
            };
        }

        private void UpdateInstallButton()
        {
            _btnInstall.Text = _installed ? "卸载开机自启" : "安装开机自启";
            _btnInstall.ForeColor = _installed ? Color.Firebrick : Color.Black;
        }

        private void RefreshStatus()
        {
            if (_busy) return;
            try
            {
                Program.StatusInfo s = Program.GetStatusInfo();
                if (s.Error != null && !s.Available)
                {
                    _lblState.Text = "状态：未连接网络";
                    _lblState.ForeColor = Color.DimGray;
                    _lblSsid.Text = "热点名称：—";
                    _lblClients.Text = "已连接设备：—";
                    _lblUpstream.Text = "共享网络：" + (s.Error ?? "—");
                }
                else
                {
                    _lblState.Text = "状态：" + s.StateText;
                    _lblState.ForeColor = s.StateText == "已开启" ? Color.SeaGreen : Color.DimGray;
                    _lblSsid.Text = "热点名称：" + (string.IsNullOrEmpty(s.Ssid) ? "—" : s.Ssid);
                    _lblClients.Text = "已连接设备：" + s.Clients + " 台";
                    _lblUpstream.Text = "共享网络：" + (string.IsNullOrEmpty(s.Upstream) ? "—" : s.Upstream);
                }

                bool installed = Program.IsInstalled();
                if (installed != _installed)
                {
                    _installed = installed;
                    UpdateInstallButton();
                }
                _lblAuto.Text = "开机自启：" + (_installed ? "已安装" : "未安装");
                _lblAuto.ForeColor = _installed ? Color.SeaGreen : Color.DimGray;
            }
            catch (Exception ex)
            {
                _lblState.Text = "状态：读取失败";
                _lblState.ForeColor = Color.Firebrick;
                Program.Log("gui refresh error: " + ex.Message);
            }
        }

        private void OnTurnOn(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string detail;
                bool ok;
                try { ok = Program.TryEnsureOn(out detail); }
                catch (Exception ex) { ok = false; detail = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _busy = false;
                        if (!ok)
                        {
                            MessageBox.Show("开启失败：" + detail, "提示",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        RefreshStatus();
                    });
                }
                catch { }
            });
        }

        private void OnOpenSettings(object sender, EventArgs e)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("ms-settings:network-mobilehotspot");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开系统设置：" + ex.Message, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnInstallClick(object sender, EventArgs e)
        {
            if (_busy) return;
            if (_installed)
            {
                if (MessageBox.Show("确定要卸载开机自启吗？", "卸载",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
            }

            _busy = true;
            int rc = 1;
            try
            {
                string mode = _installed ? "uninstall --quiet" : "install --quiet";
                rc = ElevateAndWait(mode);
            }
            finally
            {
                _busy = false;
            }

            if (rc == 0)
            {
                MessageBox.Show(_installed ? "已卸载。" : "安装完成。\n\n以后每次开机都会自动开启移动热点，登录前（锁屏界面）也可以使用。",
                    "移动热点自动开启", MessageBoxButtons.OK, MessageBoxIcon.Information);
                bool wasInstalled = _installed;
                _installed = Program.IsInstalled();
                UpdateInstallButton();
                RefreshStatus();
                if (wasInstalled && !_installed && IsRunningFromInstallDir())
                {
                    Close();
                }
            }
            else if (rc != 2)
            {
                MessageBox.Show("操作失败（代码 " + rc + "）。\n请查看日志：" + Program.LogFile,
                    "移动热点自动开启", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool IsRunningFromInstallDir()
        {
            try
            {
                string exe = Application.ExecutablePath;
                return exe.StartsWith(Program.InstallDir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private int ElevateAndWait(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, arguments);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return 1;
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Win32Exception)
            {
                return 2;
            }
            catch (Exception ex)
            {
                Program.Log("gui elevate error: " + ex.Message);
                return 1;
            }
        }
    }
}
