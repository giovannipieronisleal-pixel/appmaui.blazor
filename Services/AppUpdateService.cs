using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace appmaui.blazor.Services;

/// <summary>
/// Verifica atualizações oficiais no GitHub Releases / API v1, exibe o popup no aplicativo
/// e executa o download e instalação direta pelo Windows ou Android.
/// </summary>
public sealed class AppUpdateService
{
    private const string IgnoredVersionPreferenceKey = "youlink_ignored_update_version_v1";
    private const long MinimumValidPackageBytes = 100 * 1024;

    private static readonly HttpClient DownloadHttpClient = CreateUpdateHttpClient();
    private readonly MauiAdminGateway _gateway;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private DateTimeOffset _lastCheckedAt = DateTimeOffset.MinValue;
    private bool _sessionPopupShown;

    public event Action? StateChanged;

    public AppUpdateReleaseInfo? AvailableRelease { get; private set; }
    public bool IsPopupOpen { get; private set; }
    public bool IsChecking { get; private set; }
    public bool IsDownloading { get; private set; }
    public bool IsApplyingUpdate { get; private set; }
    public int DownloadProgressPercent { get; private set; }
    public long DownloadedBytes { get; private set; }
    public long TotalBytes { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? DownloadedPackagePath { get; private set; }
    public bool WaitingAndroidInstallPermission { get; private set; }
    public bool CanApplyDirectlyInApp => OperatingSystem.IsWindows() || OperatingSystem.IsAndroid();

    public string CurrentVersion => ResolveCurrentVersion();

    public AppUpdateService(MauiAdminGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<AppUpdateReleaseInfo?> CheckForUpdatesAsync(
        bool manualTrigger = false,
        CancellationToken ct = default)
    {
        if (!manualTrigger &&
            AvailableRelease is not null &&
            DateTimeOffset.UtcNow - _lastCheckedAt < TimeSpan.FromMinutes(15))
        {
            if (ShouldAutoPrompt(AvailableRelease) && !_sessionPopupShown)
            {
                _sessionPopupShown = true;
                IsPopupOpen = true;
                NotifyChanged();
            }
            return AvailableRelease;
        }

        await _checkLock.WaitAsync(ct);
        try
        {
            if (!manualTrigger &&
                AvailableRelease is not null &&
                DateTimeOffset.UtcNow - _lastCheckedAt < TimeSpan.FromMinutes(15))
            {
                return AvailableRelease;
            }

            IsChecking = true;
            ErrorMessage = null;
            if (manualTrigger)
            {
                StatusMessage = "Procurando nova versão do YouLink...";
            }
            NotifyChanged();

            var currentVersion = ResolveCurrentVersion();
            var isWindows = OperatingSystem.IsWindows();
            var isAndroid = OperatingSystem.IsAndroid();

            AppUpdateReleaseInfo? githubRelease = null;
            string? githubError = null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(12));
                using var resp = await DownloadHttpClient.GetAsync(AppUpdateCatalog.GitHubLatestReleaseApiUrl, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(cts.Token);
                    githubRelease = AppUpdateCatalog.ParseGitHubReleaseJson(json, currentVersion, isWindows, isAndroid);
                }
                else
                {
                    githubError = $"GitHub retornou HTTP {(int)resp.StatusCode}.";
                }
            }
            catch (Exception ex)
            {
                githubError = ex.Message;
            }

            ApiCompatibilityStatusDto? apiCompat = null;
            try
            {
                apiCompat = await _gateway.RefreshCompatibilityAsync(ct);
            }
            catch
            {
            }

            var resolved = AppUpdateCatalog.MergeWithApiCompatibility(
                githubRelease,
                apiCompat,
                currentVersion,
                isWindows,
                isAndroid);

            if (resolved is not null)
            {
                AvailableRelease = resolved;
                _lastCheckedAt = DateTimeOffset.UtcNow;

                if (resolved.IsUpdateAvailable)
                {
                    StatusMessage = $"Nova versão v{resolved.LatestVersion} pronta para atualização.";
                    if (manualTrigger || (ShouldAutoPrompt(resolved) && !_sessionPopupShown))
                    {
                        _sessionPopupShown = true;
                        IsPopupOpen = true;
                    }
                }
                else if (manualTrigger)
                {
                    StatusMessage = $"✓ Você já está usando a versão mais recente (v{currentVersion}).";
                }
            }
            else if (manualTrigger)
            {
                ErrorMessage = $"Não foi possível verificar atualizações agora ({githubError ?? "sem resposta da internet"}).";
            }

            return AvailableRelease;
        }
        finally
        {
            IsChecking = false;
            NotifyChanged();
            _checkLock.Release();
        }
    }

    public void OpenPopup()
    {
        ErrorMessage = null;
        IsPopupOpen = true;
        NotifyChanged();
    }

    public void DismissPopupForNow()
    {
        if (IsDownloading || IsApplyingUpdate)
            return;

        IsPopupOpen = false;
        NotifyChanged();
    }

    public void IgnoreCurrentAvailableVersion()
    {
        if (IsDownloading || IsApplyingUpdate)
            return;

        if (AvailableRelease is not null && !string.IsNullOrWhiteSpace(AvailableRelease.LatestVersion))
        {
            try
            {
                Preferences.Default.Set(IgnoredVersionPreferenceKey, AvailableRelease.LatestVersion);
            }
            catch
            {
            }
        }

        IsPopupOpen = false;
        NotifyChanged();
    }

    public async Task DownloadAndInstallAsync(CancellationToken ct = default)
    {
        if (IsDownloading || IsApplyingUpdate)
            return;

        var release = AvailableRelease;
        if (release is null)
        {
            await CheckForUpdatesAsync(manualTrigger: true, ct);
            release = AvailableRelease;
        }

        if (release is null || !release.IsUpdateAvailable)
        {
            StatusMessage = $"✓ Você já está na versão mais recente (v{CurrentVersion}).";
            NotifyChanged();
            return;
        }

        if (OperatingSystem.IsAndroid() &&
            WaitingAndroidInstallPermission &&
            !string.IsNullOrWhiteSpace(DownloadedPackagePath) &&
            File.Exists(DownloadedPackagePath))
        {
            await LaunchAndroidInstallerAsync(DownloadedPackagePath, release.LatestVersion);
            return;
        }

        if (string.IsNullOrWhiteSpace(release.SelectedDownloadUrl) ||
            string.IsNullOrWhiteSpace(release.SelectedAssetName))
        {
            await OpenReleaseFallbackAsync();
            return;
        }

        IsDownloading = true;
        IsApplyingUpdate = false;
        WaitingAndroidInstallPermission = false;
        ErrorMessage = null;
        DownloadProgressPercent = 0;
        DownloadedBytes = 0;
        TotalBytes = release.SelectedAssetSizeBytes ?? 0;
        StatusMessage = $"Baixando {release.SelectedAssetName} (v{release.LatestVersion})...";
        NotifyChanged();

        try
        {
            var updateDir = OperatingSystem.IsAndroid()
                ? Path.Combine(FileSystem.CacheDirectory, "updates")
                : Path.Combine(Path.GetTempPath(), "YouLinkUpdater", release.LatestVersion);

            Directory.CreateDirectory(updateDir);
            var targetFile = Path.Combine(updateDir, release.SelectedAssetName);

            await DownloadFileWithProgressAsync(release.SelectedDownloadUrl, targetFile, ct);
            ValidateDownloadedArchive(targetFile);
            DownloadedPackagePath = targetFile;

            IsDownloading = false;
            IsApplyingUpdate = true;
            DownloadProgressPercent = 100;
            NotifyChanged();

            if (OperatingSystem.IsWindows())
            {
                await ApplyWindowsUpdateAsync(targetFile, updateDir, release.LatestVersion, ct);
            }
            else if (OperatingSystem.IsAndroid())
            {
                await LaunchAndroidInstallerAsync(targetFile, release.LatestVersion);
            }
            else
            {
                await OpenReleaseFallbackAsync();
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Download da atualização cancelado.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Falha ao atualizar pelo app: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            IsApplyingUpdate = false;
            NotifyChanged();
        }
    }

    public async Task OpenReleaseFallbackAsync()
    {
        var url = AvailableRelease?.SelectedDownloadUrl ?? AvailableRelease?.ReleasePageUrl ?? AppUpdateCatalog.ReleasesPageUrl;
        try
        {
            await Launcher.Default.OpenAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Não foi possível abrir o navegador: {ex.Message}";
            NotifyChanged();
        }
    }

    private async Task DownloadFileWithProgressAsync(string downloadUrl, string destinationPath, CancellationToken ct)
    {
        using var response = await DownloadHttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? TotalBytes;
        if (total > 0)
            TotalBytes = total;

        var tempFile = destinationPath + ".part";
        await using (var remoteStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            var lastReportedPct = -1;
            var lastNotifiedAt = DateTimeOffset.UtcNow;

            while (true)
            {
                var read = await remoteStream.ReadAsync(buffer, ct);
                if (read <= 0)
                    break;

                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                DownloadedBytes = readTotal;

                var pct = TotalBytes > 0
                    ? Math.Clamp((int)Math.Round(readTotal * 100d / TotalBytes), 1, 99)
                    : 0;

                var now = DateTimeOffset.UtcNow;
                if (pct != lastReportedPct || (now - lastNotifiedAt).TotalMilliseconds >= 250)
                {
                    lastReportedPct = pct;
                    lastNotifiedAt = now;
                    DownloadProgressPercent = pct;
                    StatusMessage = TotalBytes > 0
                        ? $"Baixando atualização... {pct}% ({AppUpdateCatalog.FormatFileSize(readTotal)} de {AppUpdateCatalog.FormatFileSize(TotalBytes)})"
                        : $"Baixando atualização... {AppUpdateCatalog.FormatFileSize(readTotal)}";
                    NotifyChanged();
                }
            }
        }

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        File.Move(tempFile, destinationPath);
    }

    private static void ValidateDownloadedArchive(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length < MinimumValidPackageBytes)
        {
            throw new InvalidOperationException("O arquivo baixado está incompleto ou inválido.");
        }

        Span<byte> header = stackalloc byte[2];
        using var fs = File.OpenRead(filePath);
        if (fs.Read(header) < 2 || header[0] != (byte)'P' || header[1] != (byte)'K')
        {
            throw new InvalidOperationException("O pacote baixado não parece ser um instalador válido (.zip/.apk).");
        }
    }

    private async Task ApplyWindowsUpdateAsync(
        string zipPath,
        string updateDir,
        string targetVersion,
        CancellationToken ct)
    {
        StatusMessage = "Extraindo nova versão...";
        NotifyChanged();

        var stagingDir = Path.Combine(updateDir, "staging");
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);
        var sourceAppDir = AppUpdateCatalog.ResolveExtractedAppRoot(stagingDir);
        if (string.IsNullOrWhiteSpace(sourceAppDir))
        {
            throw new InvalidOperationException("Executável do YouLink não encontrado dentro do pacote Windows.");
        }

        var currentAppDir = AppContext.BaseDirectory;
        if (AppUpdateCatalog.IsDevelopmentBuildDirectory(currentAppDir))
        {
            StatusMessage = $"✓ Pacote v{targetVersion} baixado e validado em: {zipPath} (pasta de desenvolvimento preservada).";
            TryOpenWindowsFolder(updateDir);
            NotifyChanged();
            return;
        }

        var scriptPath = Path.Combine(updateDir, "apply-youlink-update.ps1");
        var logPath = Path.Combine(updateDir, "update.log");
        var script = AppUpdateCatalog.BuildWindowsApplyUpdateScript(
            Environment.ProcessId,
            sourceAppDir,
            currentAppDir,
            AppUpdateCatalog.WindowsExecutableName,
            logPath);
        await File.WriteAllTextAsync(scriptPath, script, ct);

        var needsElevation = !TryProbeDirectoryWritable(currentAppDir);
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (needsElevation)
        {
            psi.Verb = "runas";
        }

        Process.Start(psi);
        StatusMessage = $"Aplicando atualização v{targetVersion} e reiniciando o YouLink...";
        NotifyChanged();

        await Task.Delay(500, ct);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                Application.Current?.Quit();
            }
            catch
            {
                Environment.Exit(0);
            }
        });
    }

    private async Task LaunchAndroidInstallerAsync(string apkPath, string targetVersion)
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(26) &&
            !(Platform.AppContext.PackageManager?.CanRequestPackageInstalls() ?? true))
        {
            WaitingAndroidInstallPermission = true;
            StatusMessage = "Permita 'Instalar apps desconhecidos' para o YouLink nas Configurações do Android e depois toque em 'Instalar APK Baixado'.";
            NotifyChanged();

            var packageUri = global::Android.Net.Uri.Parse($"package:{Platform.AppContext.PackageName}");
            var permissionIntent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionManageUnknownAppSources,
                packageUri);
            permissionIntent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            Platform.AppContext.StartActivity(permissionIntent);
            return;
        }
#endif

        WaitingAndroidInstallPermission = false;
        StatusMessage = $"Abrindo instalador do Android para o YouLink v{targetVersion}...";
        NotifyChanged();

        var opened = await Launcher.Default.OpenAsync(new OpenFileRequest
        {
            Title = $"Instalar atualização YouLink v{targetVersion}",
            File = new ReadOnlyFile(apkPath, "application/vnd.android.package-archive")
        });

        if (!opened)
        {
            ErrorMessage = "O Android não conseguiu abrir o pacote automaticamente. Toque em 'Abrir no GitHub' para baixar manualmente.";
        }
        else
        {
            StatusMessage = $"✓ Instalador do Android aberto! Confirme a instalação para atualizar para a v{targetVersion}.";
        }
    }

    private bool ShouldAutoPrompt(AppUpdateReleaseInfo release)
    {
        if (!release.IsUpdateAvailable)
            return false;

        try
        {
            var ignoredVersion = Preferences.Default.Get(IgnoredVersionPreferenceKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(ignoredVersion) &&
                string.Equals(ignoredVersion.Trim(), release.LatestVersion, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
        }

        return true;
    }

    private static string ResolveCurrentVersion()
    {
        try
        {
            var v = AppInfo.Current.VersionString;
            var normalized = AppUpdateCatalog.NormalizeVersion(v);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }
        catch
        {
        }

        return AppUpdateCatalog.CurrentAppVersion;
    }

    private static bool TryProbeDirectoryWritable(string dirPath)
    {
        try
        {
            var probe = Path.Combine(dirPath, $".youlink-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryOpenWindowsFolder(string dirPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() && Directory.Exists(dirPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dirPath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch
        {
        }
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YouLink-App", AppUpdateCatalog.CurrentAppVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private void NotifyChanged() => StateChanged?.Invoke();
}
