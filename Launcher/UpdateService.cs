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
    Current,
    Installing
}

internal static class UpdateService
{
    private const string RepositoryOwner = "rottagio";
    private const string RepositoryName = "xmeye-cloud-tester";
    private const string UpdateAssetName = "XMEyeCloudTester-update.zip";

    private static readonly HttpClient Http = CreateClient();

    internal static async Task<UpdateResult> CheckAndInstallAsync(
        string installDirectory, int waitPid = 0)
    {
        if (RepositoryOwner.StartsWith("__", StringComparison.Ordinal))
            return UpdateResult.None;

        try
        {
            ReleaseInfo? release = await GetLatestReleaseAsync();
            Version current = GetInstalledVersion(installDirectory);
            if (release is null || release.Version <= current)
                return release is null ? UpdateResult.None : UpdateResult.Current;

            string workDirectory = Path.Combine(
                Path.GetTempPath(), "XMEyeCloudTester-Update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            string zipPath = Path.Combine(workDirectory, UpdateAssetName);
            string stagingDirectory = Path.Combine(workDirectory, "staging");
            Directory.CreateDirectory(stagingDirectory);

            await DownloadAsync(release.DownloadUrl, zipPath);
            VerifyDigest(zipPath, release.Digest);
            ExtractSafely(zipPath, stagingDirectory);
            ValidateUpdate(stagingDirectory, release.Version);
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

    private static Version GetInstalledVersion(string installDirectory)
    {
        Version launcher = Normalize(Assembly.GetExecutingAssembly().GetName().Version);
        string appPath = Path.Combine(installDirectory, "XMEyeCloudTester.App.dll");
        if (!File.Exists(appPath))
            return new Version(0, 0, 0);
        Version app = Normalize(AssemblyName.GetAssemblyName(appPath).Version);
        // Uma instalação parcial deve ser reparada mesmo quando somente o
        // lançador já possui a versão da release.
        return launcher <= app ? launcher : app;
    }

    private static Version Normalize(Version? version) => new(
        version?.Major ?? 0, version?.Minor ?? 0, Math.Max(0, version?.Build ?? 0));

    private static void ValidateUpdate(string stagingDirectory, Version releaseVersion)
    {
        string[] requiredFiles =
        [
            "XMEyeCloudTester.exe",
            "XMEyeCloudTester.dll",
            "XMEyeCloudTester.App.exe",
            "XMEyeCloudTester.App.dll",
            "XMEyeCloudTester.App.deps.json",
            "QRCoder.dll",
            "XMEyeBridge.dll",
            "CloudServer"
        ];
        if (requiredFiles.Any(file => !File.Exists(Path.Combine(stagingDirectory, file))))
            throw new InvalidDataException("O pacote nao contem todos os modulos obrigatorios do aplicativo.");

        Version expected = Normalize(releaseVersion);
        Version launcher = Normalize(AssemblyName.GetAssemblyName(
            Path.Combine(stagingDirectory, "XMEyeCloudTester.dll")).Version);
        Version app = Normalize(AssemblyName.GetAssemblyName(
            Path.Combine(stagingDirectory, "XMEyeCloudTester.App.dll")).Version);
        if (launcher != expected || app != expected)
            throw new InvalidDataException(
                $"Versoes inconsistentes no pacote: release {expected}, atualizador {launcher}, aplicativo {app}.");
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

$backup = Join-Path (Split-Path -Parent $Source) 'backup'
$backupReady = $false
$createdTargets = [Collections.Generic.List[string]]::new()
try {
    Write-UpdateLog "Inicio da instalacao $Version. Destino: $Destination"
    Wait-Process -Id $LauncherProcessId -ErrorAction SilentlyContinue
    if ($AppProcessId -gt 0) {
        Wait-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    }

    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $deadline = (Get-Date).AddSeconds(45)
    do {
        $runningApps = @(Get-Process -Name 'XMEyeCloudTester.App' -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase) }
            catch { $false }
        })
        if ($runningApps.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    if ($runningApps.Count -gt 0) {
        throw 'O aplicativo ainda esta encerrando. Tente atualizar novamente em alguns segundos.'
    }

    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
        if ($_.Name.StartsWith('!')) { return }
        $relative = $_.FullName.Substring($sourceRoot.Length)
        $target = Join-Path $Destination $relative
        if (Test-Path -LiteralPath $target) {
            $backupTarget = Join-Path $backup $relative
            $backupParent = Split-Path -Parent $backupTarget
            New-Item -ItemType Directory -Path $backupParent -Force | Out-Null
            [IO.File]::Copy($target, $backupTarget, $true)
        } else {
            $createdTargets.Add($target)
        }
    }
    $backupReady = $true

    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
        if ($_.Name.StartsWith('!')) { return }
        $relative = $_.FullName.Substring($sourceRoot.Length)
        $target = Join-Path $Destination $relative
        $parent = Split-Path -Parent $target
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        [IO.File]::Copy($_.FullName, $target, $true)
        $sourceHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) { throw "A verificacao do arquivo $relative falhou." }
        Write-UpdateLog "Arquivo confirmado: $relative ($targetHash)"
    }

    $launcherVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Destination 'XMEyeCloudTester.dll')).Version.ToString(3)
    $appVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Destination 'XMEyeCloudTester.App.dll')).Version.ToString(3)
    if ($launcherVersion -ne $Version -or $appVersion -ne $Version) {
        throw "Versoes finais inconsistentes: $launcherVersion / $appVersion; esperado $Version."
    }
    Write-UpdateLog "Instalacao $Version concluida e verificada."
    Start-Process -FilePath (Join-Path $Destination 'XMEyeCloudTester.exe') -ArgumentList '--skip-update' -WorkingDirectory $Destination
}
catch {
    $failure = $_.Exception.Message
    Write-UpdateLog "ERRO: $failure"
    if ($backupReady) {
        Get-ChildItem -LiteralPath $backup -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            $backupRoot = [IO.Path]::GetFullPath($backup).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            $relative = $_.FullName.Substring($backupRoot.Length)
            $target = Join-Path $Destination $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            [IO.File]::Copy($_.FullName, $target, $true)
        }
        foreach ($target in $createdTargets) {
            Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        }
        Write-UpdateLog 'Instalacao anterior restaurada.'
    }
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        "A atualizacao falhou e a versao anterior foi preservada. O detalhe foi salvo em:`n$logPath`n`n$failure",
        'Atualizacao do XMEye Cloud Tester', 'OK', 'Error') | Out-Null
    $launcherDll = Join-Path $Destination 'XMEyeCloudTester.dll'
    $appDll = Join-Path $Destination 'XMEyeCloudTester.App.dll'
    if ((Test-Path -LiteralPath $launcherDll) -and (Test-Path -LiteralPath $appDll)) {
        $launcherVersion = [Reflection.AssemblyName]::GetAssemblyName($launcherDll).Version.ToString(3)
        $appVersion = [Reflection.AssemblyName]::GetAssemblyName($appDll).Version.ToString(3)
        if ($launcherVersion -eq $appVersion) {
            Start-Process -FilePath (Join-Path $Destination 'XMEyeCloudTester.exe') -ArgumentList '--skip-update' -WorkingDirectory $Destination
        }
    }
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
