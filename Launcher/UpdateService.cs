using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

namespace XMEyeCloudTester.Launcher;

internal enum UpdateResult
{
    None,
    Installing
}

internal static class UpdateService
{
    private const string RepositoryOwner = "rottagio";
    private const string RepositoryName = "xmeye-cloud-tester";
    private const string UpdateAssetName = "XMEyeCloudTester-update.zip";

    private static readonly HttpClient Http = CreateClient();

    internal static async Task<UpdateResult> CheckAndOfferAsync(string installDirectory)
    {
        if (RepositoryOwner.StartsWith("__", StringComparison.Ordinal))
            return UpdateResult.None;

        try
        {
            ReleaseInfo? release = await GetLatestReleaseAsync();
            Version current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            if (release is null || release.Version <= current)
                return UpdateResult.None;

            DialogResult answer = MessageBox.Show(
                $"A versao {release.Version} esta disponivel.\n\n" +
                "Deseja baixar e instalar agora? O aplicativo sera reaberto automaticamente.",
                "Atualizacao do XMEye Cloud Tester",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (answer != DialogResult.Yes)
                return UpdateResult.None;

            string workDirectory = Path.Combine(
                Path.GetTempPath(), "XMEyeCloudTester-Update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            string zipPath = Path.Combine(workDirectory, UpdateAssetName);
            string stagingDirectory = Path.Combine(workDirectory, "staging");
            Directory.CreateDirectory(stagingDirectory);

            await DownloadAsync(release.DownloadUrl, zipPath);
            VerifyDigest(zipPath, release.Digest);
            ExtractSafely(zipPath, stagingDirectory);
            ValidateUpdate(stagingDirectory);
            StartInstallerScript(workDirectory, stagingDirectory, installDirectory);
            return UpdateResult.Installing;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Nao foi possivel verificar ou instalar a atualizacao. " +
                "O aplicativo sera aberto normalmente.\n\n" + ex.Message,
                "Atualizacao do XMEye Cloud Tester",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return UpdateResult.None;
        }
    }

    private static async Task<ReleaseInfo?> GetLatestReleaseAsync()
    {
        string url = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
        using HttpResponseMessage response = await Http.GetAsync(url);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument json = await JsonDocument.ParseAsync(stream);
        string tag = json.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? version))
            return null;

        foreach (JsonElement asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), UpdateAssetName,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            string downloadUrl = asset.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidDataException("A release nao contem uma URL de download.");
            string? digest = asset.TryGetProperty("digest", out JsonElement digestElement)
                ? digestElement.GetString()
                : null;
            return new ReleaseInfo(version, downloadUrl, digest);
        }

        return null;
    }

    private static async Task DownloadAsync(string url, string destination)
    {
        using HttpResponseMessage response = await Http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync();
        await using FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target);
    }

    private static void VerifyDigest(string filePath, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return;
        string[] parts = digest.Split(':', 2);
        if (parts.Length != 2 || !parts[0].Equals("sha256", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Formato de assinatura da atualizacao desconhecido.");

        using FileStream stream = File.OpenRead(filePath);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(parts[1], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A assinatura SHA-256 da atualizacao nao confere.");
    }

    private static void ExtractSafely(string zipPath, string destination)
    {
        string destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O pacote de atualizacao contem um caminho invalido.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void ValidateUpdate(string stagingDirectory)
    {
        string launcher = Path.Combine(stagingDirectory, "XMEyeCloudTester.dll");
        string application = Path.Combine(stagingDirectory, "XMEyeCloudTester.App.dll");
        if (!File.Exists(launcher) || !File.Exists(application))
            throw new InvalidDataException("O pacote nao contem os dois modulos obrigatorios do aplicativo.");
    }

    private static void StartInstallerScript(
        string workDirectory, string stagingDirectory, string installDirectory)
    {
        string scriptPath = Path.Combine(workDirectory, "install-update.ps1");
        File.WriteAllText(scriptPath, """
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
$sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\\') + '\\'
Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($sourceRoot.Length)
    $target = Join-Path $Destination $relative
    $parent = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
}
Start-Process -FilePath (Join-Path $Destination 'XMEyeCloudTester.exe') -ArgumentList '--skip-update' -WorkingDirectory $Destination
Remove-Item -LiteralPath $Source -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
""");

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-Source");
        startInfo.ArgumentList.Add(stagingDirectory);
        startInfo.ArgumentList.Add("-Destination");
        startInfo.ArgumentList.Add(Path.GetFullPath(installDirectory));
        Process.Start(startInfo);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("XMEyeCloudTester", "0.8.1"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private sealed record ReleaseInfo(Version Version, string DownloadUrl, string? Digest);
}
