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
            bool manualUpdate = args.Contains("--manual-update", StringComparer.OrdinalIgnoreCase);
            int waitPid = ParseWaitPid(args);
            if (!args.Contains("--skip-update", StringComparer.OrdinalIgnoreCase))
            {
                UpdateResult update = await UpdateService.CheckAndOfferAsync(baseDirectory, manualUpdate, waitPid);
                if (update == UpdateResult.Installing)
                    return;
            }

            string? installedVersion = args.FirstOrDefault(argument =>
                argument.StartsWith("--updated=", StringComparison.OrdinalIgnoreCase));
            if (installedVersion is not null)
            {
                MessageBox.Show(
                    $"Atualizacao concluida. Versao {installedVersion[10..]} instalada.",
                    "XMEye Cloud Tester", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                if (!IsInternalArgument(argument))
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

    private static int ParseWaitPid(string[] args)
    {
        string? argument = args.FirstOrDefault(value =>
            value.StartsWith("--wait-pid=", StringComparison.OrdinalIgnoreCase));
        return argument is not null && int.TryParse(argument[11..], out int pid) ? pid : 0;
    }

    private static bool IsInternalArgument(string argument) =>
        argument.Equals("--skip-update", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--manual-update", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--wait-pid=", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--updated=", StringComparison.OrdinalIgnoreCase);
}
