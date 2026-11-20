using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("双人节拍")]
[assembly: AssemblyProduct("Duet Rhythm Plus")]
[assembly: AssemblyDescription("双人节拍 Windows 安装与启动")]
[assembly: AssemblyVersion("1.1.1.1")]
[assembly: AssemblyFileVersion("1.1.1.1")]

internal static class Launcher
{
    internal const string AppName = "双人节拍";
    internal const string Address = "http://127.0.0.1:43120";
    private static readonly Assembly Executable = Assembly.GetExecutingAssembly();
    private static readonly Version Version = Executable.GetName().Version;
    private static readonly string LocalAppData = UserDirectory("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);
    private static readonly string InstallDirectory = Path.Combine(LocalAppData, "Programs", "DuetRhythmPlus");
    private static readonly string InstalledLauncher = Path.Combine(InstallDirectory, "DuetRhythmPlus.exe");
    private static readonly string AppDirectory = Path.Combine(InstallDirectory, "app-" + Version);
    internal static readonly string DataDirectory = Path.Combine(LocalAppData, "DuetRhythmPlus", "Data");

    [STAThread]
    private static void Main(string[] args)
    {
        bool openBrowser = Array.IndexOf(args, "--no-browser") < 0;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (!IsInstalledExecutable() && InstalledVersion() > Version)
            {
                Process.Start(InstalledLauncher, openBrowser ? "" : "--no-browser");
                return;
            }

            string instance;
            using (var sha = SHA256.Create())
                instance = @"Local\DuetRhythmPlus-" + Convert.ToBase64String(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(InstallDirectory.ToUpperInvariant()))).Replace('/', '_');

            bool first;
            using (var mutex = new Mutex(true, instance, out first))
            using (var reopen = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "-open"))
            {
                if (!first)
                {
                    if (openBrowser) reopen.Set();
                    return;
                }

                using (var window = new LaunchWindow(openBrowser))
                {
                    var listener = ThreadPool.RegisterWaitForSingleObject(reopen,
                        (state, timedOut) => window.Post(window.OpenBrowser), null, Timeout.Infinite, false);
                    try { Application.Run(window); }
                    finally { listener.Unregister(null); }
                }
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static string Install()
    {
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(DataDirectory);
        if (!Directory.Exists(AppDirectory))
        {
            string staging = Path.Combine(InstallDirectory, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                using (var payload = Executable.GetManifestResourceStream("DuetRhythmPlus.Payload"))
                using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                    zip.ExtractToDirectory(staging);
                Directory.Move(staging, AppDirectory);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }

        if (!IsInstalledExecutable() && InstalledVersion() < Version)
        {
            string staging = Path.Combine(InstallDirectory, ".launcher-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(Executable.Location, staging);
                if (File.Exists(InstalledLauncher)) File.Replace(staging, InstalledLauncher, null);
                else File.Move(staging, InstalledLauncher);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }

        string startMenu = Path.Combine(UserDirectory("APPDATA", Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
        string shortcutFile = Path.Combine(startMenu, AppName + ".lnk");
        if (!File.Exists(shortcutFile) && File.Exists(InstalledLauncher))
        {
            Directory.CreateDirectory(startMenu);
            Type shellClass = Type.GetTypeFromProgID("WScript.Shell");
            if (shellClass != null)
            {
                try
                {
                    dynamic shell = Activator.CreateInstance(shellClass);
                    dynamic shortcut = shell.CreateShortcut(shortcutFile);
                    shortcut.TargetPath = InstalledLauncher;
                    shortcut.WorkingDirectory = InstallDirectory;
                    shortcut.Description = "打开双人节拍";
                    shortcut.Save();
                }
                catch (COMException) { /* The downloaded EXE can still launch the game. */ }
            }
        }
        return AppDirectory;
    }

    private static bool IsInstalledExecutable()
    {
        return String.Equals(Executable.Location, InstalledLauncher, StringComparison.OrdinalIgnoreCase);
    }

    private static Version InstalledVersion()
    {
        Version version;
        if (File.Exists(InstalledLauncher) && Version.TryParse(FileVersionInfo.GetVersionInfo(InstalledLauncher).FileVersion, out version))
            return version;
        return new Version(0, 0);
    }

    private static string UserDirectory(string variable, Environment.SpecialFolder folder)
    {
        string directory = Environment.GetEnvironmentVariable(variable);
        return String.IsNullOrWhiteSpace(directory) ? Environment.GetFolderPath(folder) : Path.GetFullPath(directory);
    }
}

internal sealed class LaunchWindow : Form
{
    private readonly Label status;
    private readonly Button browser;
    private readonly Button exit;
    private Process server;
    private bool starting = true;
    private bool ready;
    private string lastError;

    internal LaunchWindow(bool openBrowser)
    {
        Text = Launcher.AppName;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Microsoft YaHei UI", 10F);
        ClientSize = new Size(440, 174);

        status = new Label { Text = "正在准备游戏…", Dock = DockStyle.Fill, Padding = new Padding(4, 10, 4, 8) };
        browser = new Button { Text = "打开游戏", Enabled = false, AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
        browser.Click += (sender, args) => OpenBrowser();
        exit = new Button { Text = "停止并退出", Enabled = false, AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
        exit.Click += (sender, args) => Close();

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true };
        controls.Controls.Add(exit);
        controls.Controls.Add(browser);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(status, 0, 0);
        layout.Controls.Add(controls, 0, 1);
        Controls.Add(layout);
        AcceptButton = browser;
        Shown += async (sender, args) => await StartGame(openBrowser);
    }

    private async Task StartGame(bool openBrowser)
    {
        try
        {
            string directory = await Task.Run((Func<string>)Launcher.Install);
            status.Text = "正在启动游戏服务…";
            var started = new TaskCompletionSource<bool>();
            var config = new ProcessStartInfo(Path.Combine(directory, "runtime", "node.exe"), @"windows\desktop-server.mjs")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            config.EnvironmentVariables["DUET_DESKTOP_DATA"] = Launcher.DataDirectory;
            config.EnvironmentVariables.Remove("NODE_OPTIONS");
            config.EnvironmentVariables.Remove("NODE_PATH");
            server = new Process { StartInfo = config, EnableRaisingEvents = true };
            server.OutputDataReceived += (sender, args) => { if (args.Data == "READY") started.TrySetResult(true); };
            server.ErrorDataReceived += (sender, args) => { if (!String.IsNullOrWhiteSpace(args.Data)) lastError = args.Data; };
            server.Exited += (sender, args) =>
            {
                started.TrySetResult(false);
                Post(() => { if (ready) ShowError("游戏服务已停止。请退出后重新启动。"); });
            };
            server.Start();
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            Task finished = await Task.WhenAny(started.Task, Task.Delay(12000));
            if (finished != started.Task) throw new IOException("游戏服务启动超时。请退出后重试。");
            if (!await started.Task || server.HasExited)
            {
                if (server.HasExited) server.WaitForExit();
                if (lastError == "EADDRINUSE") throw new IOException("本地端口 43120 已被占用。请关闭占用它的程序，再启动双人节拍。");
                throw new IOException("游戏服务启动失败。" + (lastError == null ? "" : "\r\n" + lastError));
            }
            ready = true;
            browser.Enabled = true;
            status.Text = "游戏已启动。玩的时候请保留此窗口。\r\n" + Launcher.Address;
            if (openBrowser) OpenBrowser();
        }
        catch (Exception error)
        {
            StopGame();
            ShowError(error.Message);
        }
        finally
        {
            starting = false;
            exit.Enabled = true;
        }
    }

    internal void Post(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    internal void OpenBrowser()
    {
        if (!ready) return;
        try { Process.Start(new ProcessStartInfo(Launcher.Address) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        {
            status.Text = "请在浏览器中打开以下地址。\r\n" + Launcher.Address;
        }
    }

    private void ShowError(string message)
    {
        ready = false;
        status.Text = message;
        browser.Enabled = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        if (starting && args.CloseReason == CloseReason.UserClosing) args.Cancel = true;
        base.OnFormClosing(args);
        if (!args.Cancel) StopGame();
    }

    private void StopGame()
    {
        if (server == null) return;
        ready = false;
        try
        {
            if (!server.HasExited)
            {
                server.StandardInput.Close();
                if (!server.WaitForExit(2500)) server.Kill();
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        finally { server.Dispose(); server = null; }
    }
}
