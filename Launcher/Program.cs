using System.Diagnostics;
using System.IO;
using System.Reflection;
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
            int waitPid = ParseWaitPid(args);
            bool manualUpdate = args.Contains("--manual-update", StringComparer.OrdinalIgnoreCase);
            if (!args.Contains("--skip-update", StringComparer.OrdinalIgnoreCase))
            {
                UpdateResult update = await UpdateService.CheckAndInstallAsync(baseDirectory, waitPid);
                if (update == UpdateResult.Installing)
                    return;
                if (manualUpdate && update == UpdateResult.Current)
                    MessageBox.Show(
                        "Você já está usando a versão mais recente.",
                        "Atualização do XMEye Cloud Tester",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            string application = Path.Combine(baseDirectory, "XMEyeCloudTester.App.exe");
            if (!File.Exists(application))
                throw new FileNotFoundException("A interface principal não foi encontrada.", application);
            EnsureVersionParity(baseDirectory);

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

    private static void EnsureVersionParity(string baseDirectory)
    {
        Version launcher = Normalize(Assembly.GetExecutingAssembly().GetName().Version);
        string appAssembly = Path.Combine(baseDirectory, "XMEyeCloudTester.App.dll");
        if (!File.Exists(appAssembly))
            throw new FileNotFoundException("O módulo principal não foi encontrado.", appAssembly);
        Version app = Normalize(AssemblyName.GetAssemblyName(appAssembly).Version);
        if (launcher != app)
            throw new InvalidDataException(
                $"A instalação está incompleta (atualizador {launcher}, aplicativo {app}). " +
                "Use o botão Atualizar para reinstalar o pacote completo.");
    }

    private static Version Normalize(Version? version) => new(
        version?.Major ?? 0, version?.Minor ?? 0, Math.Max(0, version?.Build ?? 0));
}
