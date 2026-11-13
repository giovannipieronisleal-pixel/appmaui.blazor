using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace appmaui.blazor.Services;

/// <summary>
/// Serviço de sincronização e publicação 24h online do catálogo no Google Cloud Run,
/// servidor próprio (Cloud Run / Render / Fly.io) e verificação OAuth de conta Google.
/// </summary>
public sealed class GoogleCloudSyncService
{
    private readonly IClothingStoreRepository _repository;
    private readonly AdminApiClient _adminApiClient;
    private readonly HttpClient _httpClient;
    private static readonly HttpClient ExternalHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public event Action? OnSyncStatusChanged;

    public bool IsSyncing { get; private set; }
    public string? LastSyncError { get; private set; }
    public DateTimeOffset? LastSyncTime { get; private set; }
    public bool? IsCloudOnline { get; private set; }

    public GoogleCloudSyncService(
        IClothingStoreRepository repository,
        AdminApiClient adminApiClient,
        HttpClient httpClient)
    {
        _repository = repository;
        _adminApiClient = adminApiClient;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Testa se o site hospedado no Google Cloud está ativo e respondendo 24h por dia.
    /// </summary>
    public async Task<bool> CheckCloudOnlineStatusAsync(string? targetUrl = null, CancellationToken ct = default)
    {
        try
        {
            var settings = await _repository.GetStoreSettingsAsync(ct);
            var url = string.IsNullOrWhiteSpace(targetUrl) ? settings.OnlineBaseUrl : targetUrl;

            if (string.IsNullOrWhiteSpace(url))
            {
                IsCloudOnline = null;
                OnSyncStatusChanged?.Invoke();
                return false;
            }

            var cleanUrl = url.TrimEnd('/');
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            // Testa endpoint de status ou health check do Google Cloud Run / servidor próprio
            var checkUrl = $"{cleanUrl}/api/cloud/status";
            var resp = await ExternalHttpClient.GetAsync(checkUrl, cts.Token);
            var isOk = resp.IsSuccessStatusCode;

            if (!isOk)
            {
                // Fallback para rota pública da loja
                var publicUrl = $"{cleanUrl}/api/public/store-info";
                var resp2 = await ExternalHttpClient.GetAsync(publicUrl, cts.Token);
                isOk = resp2.IsSuccessStatusCode;
            }

            IsCloudOnline = isOk;
            OnSyncStatusChanged?.Invoke();
            return isOk;
        }
        catch
        {
            IsCloudOnline = false;
            OnSyncStatusChanged?.Invoke();
            return false;
        }
    }

    public async Task<OwnServerHealthCheckResult> TestOwnServerConnectionAsync(
        string? targetUrl,
        CancellationToken ct = default)
    {
        var raw = (targetUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(raw) ||
            (!raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
             !raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            return new OwnServerHealthCheckResult(
                false,
                raw,
                null,
                0,
                "Informe uma URL válida começando com https:// (ex.: https://sualoja.run.app ou onrender.com).");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var statusResp = await ExternalHttpClient.GetAsync($"{raw}/api/cloud/status", cts.Token);
            if (statusResp.IsSuccessStatusCode)
            {
                sw.Stop();
                var json = await statusResp.Content.ReadAsStringAsync(cts.Token);
                string? detectedStore = null;
                var requiresSyncKey = false;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("storeName", out var sn))
                        detectedStore = sn.GetString();
                    if (doc.RootElement.TryGetProperty("requiresSyncKey", out var rsk) &&
                        rsk.ValueKind == JsonValueKind.True)
                        requiresSyncKey = true;
                }
                catch
                {
                }

                IsCloudOnline = true;
                OnSyncStatusChanged?.Invoke();

                var keyText = requiresSyncKey ? " • protegido com Sync Key" : string.Empty;
                var label = string.IsNullOrWhiteSpace(detectedStore) ? "Servidor YouLink" : $"Loja \"{detectedStore}\"";
                return new OwnServerHealthCheckResult(
                    true,
                    raw,
                    detectedStore,
                    sw.ElapsedMilliseconds,
                    $"Online em {sw.ElapsedMilliseconds} ms — {label} respondeu normalmente{keyText}!");
            }

            using var fallbackResp = await ExternalHttpClient.GetAsync($"{raw}/api/public/store-info", cts.Token);
            sw.Stop();
            if (fallbackResp.IsSuccessStatusCode)
            {
                IsCloudOnline = true;
                OnSyncStatusChanged?.Invoke();
                return new OwnServerHealthCheckResult(
                    true,
                    raw,
                    null,
                    sw.ElapsedMilliseconds,
                    $"Online em {sw.ElapsedMilliseconds} ms (via /api/public/store-info)!");
            }

            IsCloudOnline = false;
            OnSyncStatusChanged?.Invoke();
            return new OwnServerHealthCheckResult(
                false,
                raw,
                null,
                sw.ElapsedMilliseconds,
                $"Servidor respondeu HTTP {(int)statusResp.StatusCode}. Verifique se a API do YouLink está rodando nessa URL.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            IsCloudOnline = false;
            OnSyncStatusChanged?.Invoke();
            return new OwnServerHealthCheckResult(
                false,
                raw,
                null,
                sw.ElapsedMilliseconds,
                $"Não foi possível conectar ao servidor: {ex.Message}");
        }
    }

    public async Task<GoogleAccountTierVerificationResult> VerifyGoogleAccountByTokenAsync(
        string accessToken,
        string? googleOAuthClientId = null,
        CancellationToken ct = default)
    {
        var result = await GoogleAccountTierVerifier.VerifyWithAccessTokenAsync(
            accessToken,
            ExternalHttpClient,
            ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Email))
            return result;

        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.SetGoogleAccountVerification(
            result.Email,
            result.DetectedTier,
            result.DriveQuotaBytes,
            result.HostedDomain,
            result.VerifiedAt ?? DateTimeOffset.UtcNow,
            googleOAuthClientId);

        await _repository.UpdateStoreSettingsAsync(settings, ct);
        OnSyncStatusChanged?.Invoke();
        return result;
    }

    public async Task<GoogleAccountTierVerificationResult> VerifyGoogleAccountViaBrowserOAuthAsync(
        string googleOAuthClientId,
        CancellationToken ct = default)
    {
        var clientId = (googleOAuthClientId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new GoogleAccountTierVerificationResult(
                false, null, null, null, null, null,
                GoogleAccountTierVerifier.TierNone, false, false, "Client ID ausente",
                "Informe o Google OAuth Client ID (Desktop/Aplicativo Web) ou cole um Access Token do Google OAuth Playground.",
                null);
        }

        if (!HttpListener.IsSupported)
        {
            return new GoogleAccountTierVerificationResult(
                false, null, null, null, null, null,
                GoogleAccountTierVerifier.TierNone, false, false, "Não suportado no dispositivo",
                "Login via servidor local não suportado nesta plataforma. Use a opção de colar o Access Token Google.",
                null);
        }

        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var redirectUri = $"http://127.0.0.1:{port}/callback/";
            var pkce = GoogleAccountTierVerifier.CreatePkcePair();
            var authUrl = GoogleAccountTierVerifier.BuildAuthorizationUrl(
                clientId,
                redirectUri,
                pkce.CodeChallenge,
                pkce.State);

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            await Launcher.Default.OpenAsync(new Uri(authUrl));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (completed != contextTask)
            {
                return new GoogleAccountTierVerificationResult(
                    false, null, null, null, null, null,
                    GoogleAccountTierVerifier.TierNone, false, false, "Tempo esgotado",
                    "Tempo limite de 2 minutos aguardando login Google no navegador. Tente novamente.",
                    null);
            }

            var context = await contextTask;
            var query = context.Request.QueryString;
            var error = query["error"];
            var code = query["code"];
            var returnedState = query["state"];

            var html = string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code)
                ? "<html><body style='font-family:sans-serif;text-align:center;padding:48px'><h2>&#10004; Conta Google conectada ao YouLink!</h2><p>Você já pode fechar esta aba e voltar para o aplicativo.</p></body></html>"
                : "<html><body style='font-family:sans-serif;text-align:center;padding:48px'><h2>Autorização Google cancelada</h2><p>Feche esta aba e tente novamente no YouLink.</p></body></html>";

            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, ct);
            context.Response.OutputStream.Close();

            if (!string.IsNullOrWhiteSpace(error))
            {
                return new GoogleAccountTierVerificationResult(
                    false, null, null, null, null, null,
                    GoogleAccountTierVerifier.TierNone, false, false, "Acesso negado",
                    $"O Google retornou erro de autorização: {error}",
                    null);
            }

            if (string.IsNullOrWhiteSpace(code) || !string.Equals(returnedState, pkce.State, StringComparison.Ordinal))
            {
                return new GoogleAccountTierVerificationResult(
                    false, null, null, null, null, null,
                    GoogleAccountTierVerifier.TierNone, false, false, "Falha de segurança state",
                    "Retorno OAuth inválido ou state divergente. Tente novamente.",
                    null);
            }

            var accessToken = await GoogleAccountTierVerifier.ExchangeCodeForAccessTokenAsync(
                clientId,
                code,
                pkce.CodeVerifier,
                redirectUri,
                ExternalHttpClient,
                ct: ct);

            return await VerifyGoogleAccountByTokenAsync(accessToken, clientId, ct);
        }
        catch (Exception ex)
        {
            return new GoogleAccountTierVerificationResult(
                false, null, null, null, null, null,
                GoogleAccountTierVerifier.TierNone, false, false, "Erro OAuth",
                $"Erro no fluxo OAuth Google: {ex.Message}",
                null);
        }
    }

    public async Task<AdminStoreSettingsDto> ClearGoogleAccountVerificationAsync(CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.ClearGoogleAccountVerification();
        await _repository.UpdateStoreSettingsAsync(settings, ct);
        OnSyncStatusChanged?.Invoke();
        return EmployeeAndSettingsService.MapToAdminSettingsDto(settings);
    }

    /// <summary>
    /// Consulta se o domínio desejado para o site está disponível no Google Cloud.
    /// </summary>
    public async Task<CheckDomainAvailabilityResponse> CheckDomainAvailabilityAsync(
        string rawDomain,
        CancellationToken ct = default)
    {
        return await GoogleCloudDomainChecker.EvaluateDomainAsync(
            rawDomain,
            ExternalHttpClient,
            async domain =>
            {
                var settings = await _repository.GetStoreSettingsAsync(ct);
                return string.Equals(settings.OnlineBaseUrl, $"https://{domain}", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(settings.StoreSlug, domain, StringComparison.OrdinalIgnoreCase);
            },
            ct);
    }

    public async Task<FreeDomainSearchResponse> SearchFreeDomainsAsync(
        string? storeNameOrKeyword,
        string? ownerCustomRootDomain = null,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var query = string.IsNullOrWhiteSpace(storeNameOrKeyword) ? settings.StoreName : storeNameOrKeyword;

        return await FreeDomainFinder.SearchAvailableFreeDomainsAsync(
            query,
            ExternalHttpClient,
            async domain =>
            {
                var current = await _repository.GetStoreSettingsAsync(ct);
                return string.Equals(current.OnlineBaseUrl, $"https://{domain}", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(current.StoreSlug, domain, StringComparison.OrdinalIgnoreCase);
            },
            ownerCustomRootDomain,
            ct);
    }

    public Task<(bool Success, string Message)> UpdateDuckDnsAsync(
        string subdomainOrDomain,
        string token,
        string? ipv4 = null,
        CancellationToken ct = default) =>
        FreeDomainFinder.UpdateDuckDnsIpAsync(subdomainOrDomain, token, ipv4, ExternalHttpClient, ct);

    /// <summary>
    /// Sincroniza o catálogo local com o site 24h no Google Cloud.
    /// </summary>
    public async Task<(bool Success, string Message)> SyncCatalogToCloudAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            IsSyncing = true;
            LastSyncError = null;
            OnSyncStatusChanged?.Invoke();

            var settings = await _repository.GetStoreSettingsAsync(ct);
            var categories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
            var products = await _repository.GetProductsAsync(includeArchived: false, ct);

            if (string.IsNullOrWhiteSpace(settings.OnlineBaseUrl))
            {
                return (false, "URL do Google Cloud não configurada. Informe o link do site nas configurações da loja.");
            }

            var payload = new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = settings.StoreName,
                    StoreSlug = settings.StoreSlug,
                    WhatsappNumber = settings.WhatsappNumber,
                    StoreDescription = settings.StoreDescription,
                    LogoPublicUrl = settings.LogoPublicUrl,
                    DefaultLowStockThreshold = settings.DefaultLowStockThreshold,
                    OnlineBaseUrl = settings.OnlineBaseUrl,
                    AutoSyncOnlineOnSave = settings.AutoSyncOnlineOnSave,
                    LastOnlineSyncAt = DateTimeOffset.UtcNow,
                    PlanTier = settings.PlanTier,
                    BasicPlanMonthlyPrice = settings.BasicPlanMonthlyPrice,
                    IntermediaryPlanMonthlyPrice = settings.IntermediaryPlanMonthlyPrice,
                    PremiumPlanMonthlyPrice = settings.PremiumPlanMonthlyPrice,
                    PicpayPixKey = settings.PicpayPixKey,
                    Design = StoreDesignMapper.ToDto(settings.GetEffectivePublicDesign()),
                    DirectPixSalesEnabled = settings.DirectPixSalesEnabled,
                    StorePixKey = settings.StorePixKey,
                    StorePixReceiverName = settings.StorePixReceiverName,
                    OriginCep = settings.OriginCep,
                    OriginCity = settings.OriginCity,
                    OriginState = settings.OriginState,
                    DeliveryScopeMode = settings.DeliveryScopeMode,
                    MaxDeliveryDistanceKm = settings.MaxDeliveryDistanceKm,
                    DefaultPackageWeightGrams = settings.DefaultPackageWeightGrams,
                    ServerHostingMode = settings.ServerHostingMode,
                    OwnServerProvider = settings.OwnServerProvider
                },
                Categories = categories.Select(c => new SaveCategoryRequest
                {
                    Name = c.Name,
                    Description = c.Description,
                    DisplayOrder = c.DisplayOrder,
                    IsActive = c.IsActive
                }).ToList(),
                Products = products.Select(p => new SaveProductRequest
                {
                    CategoryId = p.CategoryId,
                    Name = p.Name,
                    Description = p.Description,
                    BasePrice = p.BasePrice,
                    IsVisibleOnSite = p.IsVisibleOnSite
                }).ToList()
            };

            var cloudEndpoint = $"{settings.OnlineBaseUrl.TrimEnd('/')}/api/cloud/sync-state";

            using var request = new HttpRequestMessage(HttpMethod.Post, cloudEndpoint)
            {
                Content = JsonContent.Create(payload)
            };

            if (!string.IsNullOrWhiteSpace(settings.OwnServerSyncSecret))
            {
                request.Headers.TryAddWithoutValidation("X-YouLink-Sync-Key", settings.OwnServerSyncSecret.Trim());
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var response = await ExternalHttpClient.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                settings.LastOnlineSyncAt = DateTimeOffset.UtcNow;
                await _repository.UpdateStoreSettingsAsync(settings, ct);

                LastSyncTime = settings.LastOnlineSyncAt;
                IsCloudOnline = true;
                OnSyncStatusChanged?.Invoke();
                return (true, $"Catálogo online atualizado com sucesso no Google Cloud! ({products.Count} produtos e {categories.Count} categorias sincronizados).");
            }

            var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
            LastSyncError = $"Falha ao sincronizar com Google Cloud: HTTP {(int)response.StatusCode}";
            OnSyncStatusChanged?.Invoke();
            return (false, LastSyncError);
        }
        catch (Exception ex)
        {
            LastSyncError = $"Erro de conexão com o site no Google Cloud: {ex.Message}";
            IsCloudOnline = false;
            OnSyncStatusChanged?.Invoke();
            return (false, LastSyncError);
        }
        finally
        {
            IsSyncing = false;
            _syncLock.Release();
            OnSyncStatusChanged?.Invoke();
        }
    }

    /// <summary>
    /// Dispara a sincronização automática em background sem bloquear a interface de usuário.
    /// </summary>
    public void TriggerAutoSyncIfEnabled()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var settings = await _repository.GetStoreSettingsAsync();
                if (settings.AutoSyncOnlineOnSave && !string.IsNullOrWhiteSpace(settings.OnlineBaseUrl))
                {
                    await SyncCatalogToCloudAsync();
                }
            }
            catch
            {
            }
        });
    }
}
