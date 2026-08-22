using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace XMEyeCloudTester;

public partial class App : System.Windows.Application
{
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
}
