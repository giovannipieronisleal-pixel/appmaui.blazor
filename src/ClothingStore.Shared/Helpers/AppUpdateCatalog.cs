using System.Globalization;
using System.Text;
using System.Text.Json;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Helpers;

public sealed record AppUpdateReleaseInfo(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    string ReleaseTitle,
    string ReleaseNotes,
    string ReleasePageUrl,
    DateTimeOffset? PublishedAt,
    string? WindowsDownloadUrl,
    long? WindowsAssetSizeBytes,
    string? AndroidDownloadUrl,
    long? AndroidAssetSizeBytes,
    string? SelectedAssetName,
    string? SelectedDownloadUrl,
    long? SelectedAssetSizeBytes,
    string Source);

/// <summary>
/// Catálogo centralizado de versão do YouLink, parser de GitHub Releases e utilitários
/// puros para atualização direta dentro do aplicativo no Windows e Android.
/// </summary>
public static class AppUpdateCatalog
{
    public const string CurrentAppVersion = "1.0.15";
    public const string GitHubOwner = "giovannipieronisleal-pixel";
    public const string GitHubRepo = "yourlink";
    public const string GitHubLatestReleaseApiUrl =
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    public const string ReleasesPageUrl =
        $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public const string WindowsAssetName = "YouLink-Windows-x64.zip";
    public const string WindowsSetupAssetName = "YouLink-Setup-x64.exe";
    public const string AndroidAssetName = "YouLink-Android.apk";
    public const string WindowsLauncherName = "YouLink.exe";
    public const string WindowsExecutableName = "appmaui.blazor.exe";

    public static string NormalizeVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            return string.Empty;

        var trimmed = rawVersion.Trim().TrimStart('v', 'V');
        var separatorIndex = trimmed.IndexOfAny(['-', '+', ' ']);
        if (separatorIndex > 0)
        {
            trimmed = trimmed[..separatorIndex];
        }

        if (!Version.TryParse(trimmed, out var parsed))
            return trimmed;

        return parsed.Revision > 0
            ? parsed.ToString()
            : $"{parsed.Major}.{Math.Max(0, parsed.Minor)}.{Math.Max(0, parsed.Build)}";
    }

    private static Version NormalizeComparableVersion(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public static bool IsVersionNewer(string? candidateVersion, string? currentVersion)
    {
        var normalizedCandidate = NormalizeVersion(candidateVersion);
        var normalizedCurrent = NormalizeVersion(currentVersion);

        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedCurrent))
            return false;

        return Version.TryParse(normalizedCandidate, out var candidate)
            && Version.TryParse(normalizedCurrent, out var current)
            && NormalizeComparableVersion(candidate) > NormalizeComparableVersion(current);
    }

    public static bool IsWindowsSetupInstallation(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return false;

        try
        {
            var trimmed = baseDirectory.TrimEnd('\\', '/');
            if (File.Exists(Path.Combine(trimmed, "unins000.exe")))
                return true;

            var parent = Directory.GetParent(trimmed);
            return parent is not null && File.Exists(Path.Combine(parent.FullName, "unins000.exe"));
        }
        catch
        {
            return false;
        }
    }

    public static AppUpdateReleaseInfo? ParseGitHubReleaseJson(
        string json,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup = false)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
                return null;

            var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            var normalizedLatest = NormalizeVersion(tagName);
            if (string.IsNullOrWhiteSpace(normalizedLatest))
                return null;

            var normalizedCurrent = NormalizeVersion(currentVersion);
            if (string.IsNullOrWhiteSpace(normalizedCurrent))
                normalizedCurrent = CurrentAppVersion;

            var releaseName = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var releaseBody = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;

            DateTimeOffset? publishedAt = null;
            if (root.TryGetProperty("published_at", out var pubProp) &&
                pubProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(pubProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                publishedAt = parsedDate;
            }

            string? windowsSetupUrl = null;
            long? windowsSetupSize = null;
            string? windowsUrl = null;
            long? windowsSize = null;
            string? androidUrl = null;
            long? androidSize = null;

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var an) ? an.GetString() : null;
                    var downloadUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                    long? size = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var parsedSize)
                        ? parsedSize
                        : null;

                    if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
                        continue;

                    if (string.Equals(assetName, WindowsSetupAssetName, StringComparison.OrdinalIgnoreCase) ||
                        assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        windowsSetupUrl ??= downloadUrl;
                        windowsSetupSize ??= size;
                    }
                    else if (string.Equals(assetName, WindowsAssetName, StringComparison.OrdinalIgnoreCase) ||
                             assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        windowsUrl ??= downloadUrl;
                        windowsSize ??= size;
                    }
                    else if (string.Equals(assetName, AndroidAssetName, StringComparison.OrdinalIgnoreCase) ||
                             assetName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    {
                        androidUrl ??= downloadUrl;
                        androidSize ??= size;
                    }
                }
            }

            var (selectedName, selectedUrl, selectedSize) = SelectPlatformAsset(
                isWindows,
                isAndroid,
                preferWindowsSetup,
                windowsSetupUrl,
                windowsSetupSize,
                windowsUrl,
                windowsSize,
                androidUrl,
                androidSize);

            return new AppUpdateReleaseInfo(
                CurrentVersion: normalizedCurrent,
                LatestVersion: normalizedLatest,
                IsUpdateAvailable: IsVersionNewer(normalizedLatest, normalizedCurrent),
                ReleaseTitle: string.IsNullOrWhiteSpace(releaseName) ? $"YouLink v{normalizedLatest}" : releaseName.Trim(),
                ReleaseNotes: string.IsNullOrWhiteSpace(releaseBody)
                    ? "Melhorias de desempenho, sincronização online 24h e correções visuais."
                    : releaseBody.Trim(),
                ReleasePageUrl: string.IsNullOrWhiteSpace(htmlUrl) ? ReleasesPageUrl : htmlUrl.Trim(),
                PublishedAt: publishedAt,
                WindowsDownloadUrl: preferWindowsSetup ? (windowsSetupUrl ?? windowsUrl) : (windowsUrl ?? windowsSetupUrl),
                WindowsAssetSizeBytes: preferWindowsSetup ? (windowsSetupSize ?? windowsSize) : (windowsSize ?? windowsSetupSize),
                AndroidDownloadUrl: androidUrl,
                AndroidAssetSizeBytes: androidSize,
                SelectedAssetName: selectedName,
                SelectedDownloadUrl: selectedUrl,
                SelectedAssetSizeBytes: selectedSize,
                Source: "GitHubReleases");
        }
        catch
        {
            return null;
        }
    }

    public static AppUpdateReleaseInfo? ParseGitHubRedirectLocation(
        string? redirectLocationOrUrl,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup = false)
    {
        if (string.IsNullOrWhiteSpace(redirectLocationOrUrl))
            return null;

        var trimmed = redirectLocationOrUrl.Trim();
        var tagMarkerIndex = trimmed.LastIndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
        if (tagMarkerIndex < 0)
            return null;

        var rawTag = trimmed[(tagMarkerIndex + "/releases/tag/".Length)..];
        var queryOrFragment = rawTag.IndexOfAny(['?', '#', '/']);
        if (queryOrFragment >= 0)
        {
            rawTag = rawTag[..queryOrFragment];
        }

        var latestVersion = NormalizeVersion(Uri.UnescapeDataString(rawTag));
        if (string.IsNullOrWhiteSpace(latestVersion) || !Version.TryParse(latestVersion, out _))
            return null;

        var releasePageUrl = trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/tag/v{latestVersion}";

        return BuildDeterministicReleaseInfo(
            latestVersion: latestVersion,
            currentVersion: currentVersion,
            isWindows: isWindows,
            isAndroid: isAndroid,
            preferWindowsSetup: preferWindowsSetup,
            releaseNotes: "Nova versão oficial disponível no GitHub Releases do YouLink.",
            releasePageUrl: releasePageUrl,
            source: "GitHubRedirect");
    }

    public static AppUpdateReleaseInfo? MergeWithApiCompatibility(
        AppUpdateReleaseInfo? githubRelease,
        ApiCompatibilityStatusDto? apiCompat,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup = false)
    {
        var normalizedCurrent = NormalizeVersion(currentVersion);
        if (string.IsNullOrWhiteSpace(normalizedCurrent))
            normalizedCurrent = CurrentAppVersion;

        if (apiCompat is null || string.IsNullOrWhiteSpace(apiCompat.LatestAppVersion))
            return githubRelease;

        var apiLatest = NormalizeVersion(apiCompat.LatestAppVersion);
        if (githubRelease is not null && !IsVersionNewer(apiLatest, githubRelease.LatestVersion))
            return githubRelease;

        var releasePage = string.IsNullOrWhiteSpace(apiCompat.ReleaseUrl)
            ? ReleasesPageUrl
            : apiCompat.ReleaseUrl.Trim();

        return BuildDeterministicReleaseInfo(
            latestVersion: apiLatest,
            currentVersion: normalizedCurrent,
            isWindows: isWindows,
            isAndroid: isAndroid,
            preferWindowsSetup: preferWindowsSetup,
            releaseNotes: string.IsNullOrWhiteSpace(apiCompat.Message)
                ? "Nova atualização oficial disponível."
                : apiCompat.Message.Trim(),
            releasePageUrl: releasePage,
            source: "ApiCompatibility");
    }

    private static AppUpdateReleaseInfo BuildDeterministicReleaseInfo(
        string latestVersion,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup,
        string releaseNotes,
        string releasePageUrl,
        string source)
    {
        var normalizedLatest = NormalizeVersion(latestVersion);
        var normalizedCurrent = NormalizeVersion(currentVersion);
        if (string.IsNullOrWhiteSpace(normalizedCurrent))
            normalizedCurrent = CurrentAppVersion;

        var tagBase = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/download/v{normalizedLatest}";
        var windowsSetupUrl = $"{tagBase}/{WindowsSetupAssetName}";
        var windowsUrl = $"{tagBase}/{WindowsAssetName}";
        var androidUrl = $"{tagBase}/{AndroidAssetName}";
        var (selectedName, selectedUrl, selectedSize) = SelectPlatformAsset(
            isWindows,
            isAndroid,
            preferWindowsSetup,
            windowsSetupUrl,
            null,
            windowsUrl,
            null,
            androidUrl,
            null);

        return new AppUpdateReleaseInfo(
            CurrentVersion: normalizedCurrent,
            LatestVersion: normalizedLatest,
            IsUpdateAvailable: IsVersionNewer(normalizedLatest, normalizedCurrent),
            ReleaseTitle: $"YouLink v{normalizedLatest}",
            ReleaseNotes: releaseNotes,
            ReleasePageUrl: releasePageUrl,
            PublishedAt: null,
            WindowsDownloadUrl: preferWindowsSetup ? windowsSetupUrl : windowsUrl,
            WindowsAssetSizeBytes: null,
            AndroidDownloadUrl: androidUrl,
            AndroidAssetSizeBytes: null,
            SelectedAssetName: selectedName,
            SelectedDownloadUrl: selectedUrl,
            SelectedAssetSizeBytes: selectedSize,
            Source: source);
    }

    public static string FormatFileSize(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value <= 0)
            return "Tamanho automático";

        var mb = bytes.Value / (1024d * 1024d);
        if (mb >= 1d)
            return $"{mb.ToString("N1", CultureInfo.GetCultureInfo("pt-BR"))} MB";

        var kb = Math.Max(1d, bytes.Value / 1024d);
        return $"{kb.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} KB";
    }

    public static string? ResolveExtractedAppRoot(
        string extractedDirectory,
        string executableFileName = WindowsExecutableName)
    {
        if (string.IsNullOrWhiteSpace(extractedDirectory) || !Directory.Exists(extractedDirectory))
            return null;

        var directCandidate = Path.Combine(extractedDirectory, executableFileName);
        if (File.Exists(directCandidate))
            return extractedDirectory;

        var match = Directory
            .EnumerateFiles(extractedDirectory, executableFileName, SearchOption.AllDirectories)
            .FirstOrDefault();

        return match is null ? null : Path.GetDirectoryName(match);
    }

    public static bool IsDevelopmentBuildDirectory(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return false;

        var normalized = baseDirectory.Replace('/', '\\');
        return normalized.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildWindowsApplyUpdateScript(
        int targetProcessId,
        string sourceAppDir,
        string targetAppDir,
        string executableFileName,
        string logFilePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$targetPid = {targetProcessId}");
        sb.AppendLine($"$sourceDir = '{EscapePowerShellSingleQuoted(sourceAppDir.TrimEnd('\\', '/'))}'");
        sb.AppendLine($"$targetDir = '{EscapePowerShellSingleQuoted(targetAppDir.TrimEnd('\\', '/'))}'");
        sb.AppendLine($"$exeName = '{EscapePowerShellSingleQuoted(executableFileName)}'");
        sb.AppendLine($"$launcherName = '{EscapePowerShellSingleQuoted(WindowsLauncherName)}'");
        sb.AppendLine($"$logPath = '{EscapePowerShellSingleQuoted(logFilePath)}'");
        sb.AppendLine("function Write-Log([string]$msg) { Add-Content -Path $logPath -Value \"[$(Get-Date -Format 'HH:mm:ss')] $msg\" -Encoding UTF8 }");
        sb.AppendLine("Write-Log 'Iniciando atualização automática do YouLink Windows...'");
        sb.AppendLine("for ($i = 0; $i -lt 40; $i++) {");
        sb.AppendLine("    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue");
        sb.AppendLine("    if ($null -eq $proc) { break }");
        sb.AppendLine("    Start-Sleep -Milliseconds 350");
        sb.AppendLine("}");
        sb.AppendLine("$stillRunning = Get-Process -Id $targetPid -ErrorAction SilentlyContinue");
        sb.AppendLine("if ($null -ne $stillRunning) { try { Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue } catch {} Start-Sleep -Milliseconds 600 }");
        sb.AppendLine("$robocopyArgs = @($sourceDir, $targetDir, '/E', '/IS', '/IT', '/R:6', '/W:1', '/XD', 'App_Data', '/NP', '/NFL', '/NDL', '/NJH', '/NJS')");
        sb.AppendLine("& robocopy @robocopyArgs | Out-Null");
        sb.AppendLine("$rc = $LASTEXITCODE");
        sb.AppendLine("Write-Log \"Robocopy exit code: $rc\"");
        sb.AppendLine("if ($rc -ge 8) { throw \"Falha ao copiar arquivos da atualização (robocopy=$rc).\" }");
        sb.AppendLine("$sourceParent = Split-Path -Path $sourceDir -Parent");
        sb.AppendLine("$targetParent = Split-Path -Path $targetDir -Parent");
        sb.AppendLine("$launchExe = Join-Path $targetDir $exeName");
        sb.AppendLine("$launchWorkDir = $targetDir");
        sb.AppendLine("if ((Split-Path -Leaf $targetDir) -eq 'App' -and $targetParent -and $sourceParent) {");
        sb.AppendLine("    $srcLauncher = Join-Path $sourceParent $launcherName");
        sb.AppendLine("    if (Test-Path $srcLauncher) {");
        sb.AppendLine("        Copy-Item -Path $srcLauncher -Destination (Join-Path $targetParent $launcherName) -Force");
        sb.AppendLine("        $launchExe = Join-Path $targetParent $launcherName");
        sb.AppendLine("    }");
        sb.AppendLine("    $srcReadme = Join-Path $sourceParent 'LEIA-ME.txt'");
        sb.AppendLine("    if (Test-Path $srcReadme) { Copy-Item -Path $srcReadme -Destination (Join-Path $targetParent 'LEIA-ME.txt') -Force }");
        sb.AppendLine("}");
        sb.AppendLine("if (Test-Path $launchExe) {");
        sb.AppendLine("    Write-Log \"Reabrindo $launchExe\"");
        sb.AppendLine("    Start-Process -FilePath $launchExe -WorkingDirectory $launchWorkDir");
        sb.AppendLine("}");
        sb.AppendLine("try { Remove-Item -Path $sourceDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}");
        return sb.ToString();
    }

    public static string BuildWindowsInstallerUpdateScript(
        int targetProcessId,
        string setupExePath,
        string targetLauncherPath,
        string logFilePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$targetPid = {targetProcessId}");
        sb.AppendLine($"$setupExe = '{EscapePowerShellSingleQuoted(setupExePath)}'");
        sb.AppendLine($"$launchExe = '{EscapePowerShellSingleQuoted(targetLauncherPath)}'");
        sb.AppendLine($"$logPath = '{EscapePowerShellSingleQuoted(logFilePath)}'");
        sb.AppendLine("function Write-Log([string]$msg) { Add-Content -Path $logPath -Value \"[$(Get-Date -Format 'HH:mm:ss')] $msg\" -Encoding UTF8 }");
        sb.AppendLine("Write-Log 'Iniciando atualização pelo instalador YouLink-Setup-x64.exe...'");
        sb.AppendLine("for ($i = 0; $i -lt 40; $i++) {");
        sb.AppendLine("    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue");
        sb.AppendLine("    if ($null -eq $proc) { break }");
        sb.AppendLine("    Start-Sleep -Milliseconds 350");
        sb.AppendLine("}");
        sb.AppendLine("$stillRunning = Get-Process -Id $targetPid -ErrorAction SilentlyContinue");
        sb.AppendLine("if ($null -ne $stillRunning) { try { Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue } catch {} Start-Sleep -Milliseconds 600 }");
        sb.AppendLine("$procSetup = Start-Process -FilePath $setupExe -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS') -Wait -PassThru");
        sb.AppendLine("Write-Log \"Instalador finalizado com código: $($procSetup.ExitCode)\"");
        sb.AppendLine("if ($procSetup.ExitCode -ne 0) { throw \"Instalador retornou código $($procSetup.ExitCode).\" }");
        sb.AppendLine("if (Test-Path $launchExe) {");
        sb.AppendLine("    Start-Process -FilePath $launchExe -WorkingDirectory (Split-Path -Parent $launchExe)");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static (string? AssetName, string? DownloadUrl, long? SizeBytes) SelectPlatformAsset(
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup,
        string? windowsSetupUrl,
        long? windowsSetupSize,
        string? windowsUrl,
        long? windowsSize,
        string? androidUrl,
        long? androidSize)
    {
        if (isWindows)
        {
            if (preferWindowsSetup && !string.IsNullOrWhiteSpace(windowsSetupUrl))
                return (WindowsSetupAssetName, windowsSetupUrl, windowsSetupSize);

            if (!string.IsNullOrWhiteSpace(windowsUrl))
                return (WindowsAssetName, windowsUrl, windowsSize);

            if (!string.IsNullOrWhiteSpace(windowsSetupUrl))
                return (WindowsSetupAssetName, windowsSetupUrl, windowsSetupSize);
        }

        if (isAndroid && !string.IsNullOrWhiteSpace(androidUrl))
            return (AndroidAssetName, androidUrl, androidSize);

        return (null, null, null);
    }

    private static string EscapePowerShellSingleQuoted(string value) =>
        (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
}
