using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace XMEyeCloudTester;

internal static class PreviewResourceLogger
{
    private const long MaximumLogBytes = 2L * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XMEyeCloudAccountTester",
        "preview-resources.log");

    internal static void Write(
        string moment, int device, int window, int attempt, int? startResult = null)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            string line = string.Join(';',
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                $"momento={moment}",
                $"dispositivo={device}",
                $"janela={window}",
                $"tentativa={attempt}",
                $"retorno={(startResult?.ToString(CultureInfo.InvariantCulture) ?? "-")}",
                $"workingSet={process.WorkingSet64}",
                $"memoriaPrivada={process.PrivateMemorySize64}",
                $"threads={process.Threads.Count}",
                $"handles={process.HandleCount}") + Environment.NewLine;
            byte[] data = Utf8.GetBytes(line);

            lock (Sync)
            {
                string? directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                using var stream = new FileStream(
                    LogPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (stream.Length + data.Length > MaximumLogBytes)
                    stream.SetLength(0);
                stream.Position = stream.Length;
                stream.Write(data);
            }
        }
        catch
        {
            // Diagnóstico nunca pode interferir na recuperação do vídeo.
        }
    }
}
