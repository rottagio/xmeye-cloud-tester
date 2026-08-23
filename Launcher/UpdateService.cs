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

    internal static async Task<UpdateResult> CheckAndOfferAsync(
        string installDirectory, bool manualCheck = false, int waitPid = 0)
    {
        if (RepositoryOwner.StartsWith("__", StringComparison.Ordinal))
            return UpdateResult.None;

        try
        {
            ReleaseInfo? release = await GetLatestReleaseAsync();
            Version current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            if (release is null || release.Version <= current)
            {
                if (manualCheck)
                {
                    MessageBox.Show(
                        $"Voce ja esta usando a versao mais recente ({current.Major}.{current.Minor}.{current.Build}).",
                        "Atualizacao do XMEye Cloud Tester",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return UpdateResult.None;
            }

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
            StartInstallerScript(
                workDirectory, stagingDirectory, installDirectory, waitPid, release.Version);
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
        string[] requiredFiles =
        [
            "XMEyeCloudTester.dll",
            "XMEyeCloudTester.App.dll",
            "XMEyeCloudTester.App.deps.json",
            "QRCoder.dll",
            "XMEyeBridge.dll",
            "CloudServer"
        ];
        if (requiredFiles.Any(file => !File.Exists(Path.Combine(stagingDirectory, file))))
            throw new InvalidDataException("O pacote nao contem todos os modulos obrigatorios do aplicativo.");
    }

    private static void StartInstallerScript(
        string workDirectory, string stagingDirectory, string installDirectory,
        int waitPid, Version version)
    {
        string scriptPath = Path.Combine(workDirectory, "install-update.ps1");
        File.WriteAllText(scriptPath, """
param(
    [Parameter(Mandatory=$true)][int]$LauncherProcessId,
    [int]$AppProcessId = 0,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$logDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'XMEyeCloudAccountTester'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory 'update.log'
function Write-UpdateLog([string]$Message) {
    Add-Content -LiteralPath $logPath -Value ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message)
}

try {
    Write-UpdateLog "Inicio da instalacao $Version. Destino: $Destination"
    Wait-Process -Id $LauncherProcessId -ErrorAction SilentlyContinue
    if ($AppProcessId -gt 0) {
        Wait-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500

    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\\') + '\\'
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length)
        $target = Join-Path $Destination $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        [IO.File]::Copy($_.FullName, $target, $true)
        $sourceHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) {
            throw "A verificacao do arquivo $relative falhou."
        }
        Write-UpdateLog "Arquivo confirmado: $relative ($targetHash)"
    }

    Write-UpdateLog "Instalacao $Version concluida."
    Start-Process -FilePath (Join-Path $Destination 'XMEyeCloudTester.exe') -ArgumentList @('--skip-update', "--updated=$Version") -WorkingDirectory $Destination
    Remove-Item -LiteralPath $Source -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
catch {
    Write-UpdateLog "ERRO: $($_.Exception.Message)"
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        "A atualizacao falhou. O detalhe foi salvo em:`n$logPath`n`n$($_.Exception.Message)",
        'Atualizacao do XMEye Cloud Tester', 'OK', 'Error') | Out-Null
    Start-Process -FilePath (Join-Path $Destination 'XMEyeCloudTester.exe') -ArgumentList '--skip-update' -WorkingDirectory $Destination
    exit 1
}
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
        startInfo.ArgumentList.Add("-LauncherProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-AppProcessId");
        startInfo.ArgumentList.Add(waitPid.ToString());
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add($"{version.Major}.{version.Minor}.{version.Build}");
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
            new ProductInfoHeaderValue("XMEyeCloudTester", "0.9.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private sealed record ReleaseInfo(Version Version, string DownloadUrl, string? Digest);
}
