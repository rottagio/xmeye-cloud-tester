using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace XMEyeCloudTester;

public partial class App : System.Windows.Application
{
    private const string AppUserModelId = "RottaGio.XMEyeCloudTester.Monitor";
    private Mutex? singleInstanceMutex;
    private bool restartAfterLogout;

    internal static string? AccountLogoutCleanupMessage { get; private set; }

    static App()
    {
        string baseDirectory = AppContext.BaseDirectory;
        Directory.SetCurrentDirectory(baseDirectory);
        SetDllDirectory(baseDirectory);
        string plugins = Path.Combine(baseDirectory, "plugins");
        Environment.SetEnvironmentVariable("QT_PLUGIN_PATH", plugins + ";" + baseDirectory);
        Environment.SetEnvironmentVariable(
            "QT_QPA_PLATFORM_PLUGIN_PATH", Path.Combine(plugins, "platforms"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Give the monitor its own stable Windows identity. Without an explicit
        // identity, the taskbar can retain the launcher's old cached icon even
        // after both executables have been replaced by the updater.
        _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        singleInstanceMutex = new Mutex(
            initiallyOwned: true, "Local\\XMEyeCloudTester.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "O XMEye Cloud já está aberto. Use a janela existente para evitar que duas sessões disputem as câmeras.",
                "XMEye Cloud", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AccountLogoutCleanupMessage = CloudSessionStore.CompletePendingLogout();
        base.OnStartup(e);
    }

    internal void RestartAfterAccountLogout()
    {
        restartAfterLogout = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        bool shouldRestart = restartAfterLogout;
        try { singleInstanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { }
        singleInstanceMutex?.Dispose();
        singleInstanceMutex = null;

        base.OnExit(e);

        if (!shouldRestart)
            return;

        string? executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
    }
}
