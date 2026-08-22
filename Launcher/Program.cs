using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace XMEyeCloudTester.Launcher;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            if (!args.Contains("--skip-update", StringComparer.OrdinalIgnoreCase))
            {
                UpdateResult update = await UpdateService.CheckAndOfferAsync(baseDirectory);
                if (update == UpdateResult.Installing)
                    return;
            }

            string application = Path.Combine(baseDirectory, "XMEyeCloudTester.App.exe");
            if (!File.Exists(application))
                throw new FileNotFoundException("A interface principal não foi encontrada.", application);

            string plugins = Path.Combine(baseDirectory, "plugins");
            var startInfo = new ProcessStartInfo(application)
            {
                UseShellExecute = false,
                WorkingDirectory = baseDirectory
            };
            startInfo.Environment["QT_PLUGIN_PATH"] = plugins + ";" + baseDirectory;
            startInfo.Environment["QT_QPA_PLATFORM_PLUGIN_PATH"] = Path.Combine(plugins, "platforms");
            foreach (string argument in args)
            {
                if (!argument.Equals("--skip-update", StringComparison.OrdinalIgnoreCase))
                    startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Não foi possível iniciar o XMEye Cloud Tester.\n\n" + ex.Message,
                "XMEye Cloud Tester", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
