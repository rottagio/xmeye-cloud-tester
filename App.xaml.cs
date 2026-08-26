using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace XMEyeCloudTester;

public partial class App : System.Windows.Application
{
    private const string AppUserModelId = "RottaGio.XMEyeCloudTester.Monitor.V2";
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

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId, uint flags, IntPtr item1, IntPtr item2);

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterWindowsApplicationIdentity();
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

    private static void RegisterWindowsApplicationIdentity()
    {
        try
        {
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                return;
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\AppUserModelId\{AppUserModelId}", writable: true);
            key.SetValue("DisplayName", "iCSee/XMEye Monitor", RegistryValueKind.String);
            key.SetValue("IconUri", executable + ",0", RegistryValueKind.String);
            key.SetValue("IconBackgroundColor", "0", RegistryValueKind.String);
            // SHCNE_ASSOCCHANGED: pede ao Explorer para reler a identidade e o
            // ícone sem apagar todo o cache nem reiniciar a barra de tarefas.
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // A janela ainda aplicará diretamente o ícone embutido no executável.
        }
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
