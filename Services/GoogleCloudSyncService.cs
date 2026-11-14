using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
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
    private const string CentralUrlPreferenceKey = "youlink_central_catalog_url_v1";
    private const string PublishTokenPreferencePrefix = "youlink_central_publish_token_v1_";
    public const string HostingModePlatformShared = "PlatformShared";

    private readonly IClothingStoreRepository _repository;
    private readonly AdminApiClient _adminApiClient;
    private readonly HttpClient _httpClient;
    private readonly OrderAndShippingService? _orderAndShippingService;
    private readonly HttpClient _externalHttpClient;
    private static readonly HttpClient ExternalHttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public event Action? OnSyncStatusChanged;

    public bool IsSyncing { get; private set; }
    public string? LastSyncError { get; private set; }
    public DateTimeOffset? LastSyncTime { get; private set; }
    public bool? IsCloudOnline { get; private set; }
    public DateTimeOffset? LastOrdersSyncTime { get; private set; }

    public GoogleCloudSyncService(
        IClothingStoreRepository repository,
        AdminApiClient adminApiClient,
        HttpClient httpClient,
        OrderAndShippingService? orderAndShippingService = null,
        HttpClient? externalHttpClient = null)
    {
        _repository = repository;
        _adminApiClient = adminApiClient;
        _httpClient = httpClient;
        _orderAndShippingService = orderAndShippingService;
        _externalHttpClient = externalHttpClient ?? ExternalHttpClient;
    }

    public static string? GetCachedCentralCatalogUrl()
    {
        try
        {
            var value = Preferences.Default.Get(CentralUrlPreferenceKey, string.Empty);
            var normalized = NormalizePublicServerUrl(value);
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveCachedCentralCatalogUrl(string? url)
    {
        try
        {
            var normalized = NormalizePublicServerUrl(url);
            if (string.IsNullOrWhiteSpace(normalized))
                Preferences.Default.Remove(CentralUrlPreferenceKey);
            else
                Preferences.Default.Set(CentralUrlPreferenceKey, normalized);
        }
        catch
        {
        }
    }

    private static string? GetCachedPublishToken(string? storeSlug)
    {
        if (string.IsNullOrWhiteSpace(storeSlug))
            return null;

        try
        {
            var key = $"{PublishTokenPreferencePrefix}{storeSlug.Trim().ToLowerInvariant()}";
            var value = Preferences.Default.Get(key, string.Empty);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCachedPublishToken(string? storeSlug, string? token)
    {
        if (string.IsNullOrWhiteSpace(storeSlug) || string.IsNullOrWhiteSpace(token))
            return;

        try
        {
            var key = $"{PublishTokenPreferencePrefix}{storeSlug.Trim().ToLowerInvariant()}";
            Preferences.Default.Set(key, token.Trim());
        }
        catch
        {
        }
    }

    public static string ResolveEffectiveStoreSlug(StoreSettings settings) =>
        string.IsNullOrWhiteSpace(settings.StoreSlug)
            ? WhatsAppLinkBuilder.GenerateStoreSlug(settings.StoreName)
            : WhatsAppLinkBuilder.GenerateStoreSlug(settings.StoreSlug);

    public static string ResolveCentralServerBaseUrl(StoreSettings settings, string? explicitUrl = null)
    {
        var explicitNormalized = NormalizePublicServerUrl(explicitUrl);
        if (!string.IsNullOrWhiteSpace(explicitNormalized))
            return explicitNormalized;

        var settingsCentral = NormalizePublicServerUrl(settings.CentralCatalogBaseUrl);
        if (!string.IsNullOrWhiteSpace(settingsCentral))
            return settingsCentral;

        var cachedCentral = GetCachedCentralCatalogUrl();
        if (!string.IsNullOrWhiteSpace(cachedCentral))
            return cachedCentral;

        var isSharedMode = string.Equals(
            settings.ServerHostingMode,
            HostingModePlatformShared,
            StringComparison.OrdinalIgnoreCase);

        if (isSharedMode || string.IsNullOrWhiteSpace(settings.OwnServerSyncSecret))
        {
            var onlineNormalized = NormalizePublicServerUrl(settings.OnlineBaseUrl);
            if (!string.IsNullOrWhiteSpace(onlineNormalized))
                return onlineNormalized;
        }

        return string.Empty;
    }

    private static string? ResolvePublishToken(StoreSettings settings, string effectiveSlug) =>
        !string.IsNullOrWhiteSpace(settings.CentralPublishToken)
            ? settings.CentralPublishToken.Trim()
            : GetCachedPublishToken(effectiveSlug);

    private static string BuildCloudStatusUrl(string cleanBaseUrl, string? storeSlug) =>
        string.IsNullOrWhiteSpace(storeSlug)
            ? $"{cleanBaseUrl}/api/cloud/status"
            : $"{cleanBaseUrl}/api/cloud/status?storeSlug={Uri.EscapeDataString(storeSlug)}";

    /// <summary>
    /// Testa se o site hospedado na nuvem está ativo e respondendo 24h por dia.
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

            var cleanUrl = NormalizePublicServerUrl(url);
            if (string.IsNullOrWhiteSpace(cleanUrl))
            {
                IsCloudOnline = false;
                OnSyncStatusChanged?.Invoke();
                return false;
            }

            var slug = ResolveEffectiveStoreSlug(settings);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            var checkUrl = BuildCloudStatusUrl(cleanUrl, slug);
            var resp = await _externalHttpClient.GetAsync(checkUrl, cts.Token);
            var isOk = resp.IsSuccessStatusCode;

            if (!isOk)
            {
                var publicUrl = string.IsNullOrWhiteSpace(slug)
                    ? $"{cleanUrl}/api/public/store-info"
                    : $"{cleanUrl}/api/public/store-info?storeSlug={Uri.EscapeDataString(slug)}";
                var resp2 = await _externalHttpClient.GetAsync(publicUrl, cts.Token);
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

    public static string NormalizeSyncSecretInput(string? rawSecret) =>
        WhatsAppLinkBuilder.NormalizeSyncSecretInput(rawSecret);

    public static string NormalizePublicServerUrl(string? rawUrl) =>
        WhatsAppLinkBuilder.NormalizePublicServerUrl(rawUrl);

    public Task<RenderServiceProvisionResult> ProvisionRenderServiceAsync(
        string? rawApiKey,
        string? storeNameOrSlug,
        string? existingSyncSecret = null,
        CancellationToken ct = default) =>
        RenderProvisioningHelper.DiscoverOrCreateYouLinkServiceAsync(
            rawApiKey,
            storeNameOrSlug,
            existingSyncSecret,
            ExternalHttpClient,
            ct);

    public async Task<OwnServerHealthCheckResult> TestOwnServerConnectionAsync(
        string? targetUrl,
        CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        var raw = NormalizePublicServerUrl(targetUrl);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new OwnServerHealthCheckResult(
                false,
                (targetUrl ?? string.Empty).Trim(),
                null,
                0,
                "Informe uma URL válida do seu site (ex.: https://sualoja.onrender.com).");
        }

        const int maxAttempts = 3;
        var sw = Stopwatch.StartNew();
        Exception? lastException = null;
        HttpStatusCode? lastStatusCode = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(45));

                var statusUrl = BuildCloudStatusUrl(raw, null);
                using var statusResp = await _externalHttpClient.GetAsync(statusUrl, cts.Token);
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

                lastStatusCode = statusResp.StatusCode;
                if (IsTransientWakeupStatus(statusResp.StatusCode) && attempt < maxAttempts)
                {
                    progress?.Report($"⏳ Acordando seu site gratuito no Render (tentativa {attempt + 1}/{maxAttempts})...");
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
                    continue;
                }

                using var fallbackResp = await _externalHttpClient.GetAsync($"{raw}/api/public/store-info", cts.Token);
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
                        $"Online em {sw.ElapsedMilliseconds} ms!");
                }

                IsCloudOnline = false;
                OnSyncStatusChanged?.Invoke();
                return new OwnServerHealthCheckResult(
                    false,
                    raw,
                    null,
                    sw.ElapsedMilliseconds,
                    $"Servidor respondeu HTTP {(int)statusResp.StatusCode}. Confira se o deploy no Render já terminou e ficou 'Live'.");
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested && IsTransientWakeupException(ex))
            {
                lastException = ex;
                progress?.Report($"⏳ O servidor gratuito ainda está iniciando no Render (tentativa {attempt + 1}/{maxAttempts})...");
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        sw.Stop();
        IsCloudOnline = false;
        OnSyncStatusChanged?.Invoke();

        var reason = lastStatusCode.HasValue
            ? $"HTTP {(int)lastStatusCode.Value}"
            : lastException is OperationCanceledException or TimeoutException
                ? "tempo limite de conexão esgotado — verifique se o endereço está correto e se o servidor está ativo"
                : lastException?.Message ?? "sem resposta";

        return new OwnServerHealthCheckResult(
            false,
            raw,
            null,
            sw.ElapsedMilliseconds,
            $"Não foi possível conectar ao servidor ({reason}).");
    }

    private static bool IsTransientWakeupStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or (HttpStatusCode)429;

    private static bool IsTransientWakeupException(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or TimeoutException;

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

    public static CloudCatalogSyncPayload BuildCatalogSyncPayload(
        StoreSettings settings,
        IReadOnlyList<Category> categories,
        IReadOnlyList<Product> products)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(products);

        var categoriesById = categories.ToDictionary(c => c.Id, c => c);

        return new CloudCatalogSyncPayload
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
                IsSiteActive = settings.IsSiteActive,
                DeactivationReason = settings.DeactivationReason,
                LastOnlineSyncAt = DateTimeOffset.UtcNow,
                PlanTier = settings.PlanTier,
                BasicPlanMonthlyPrice = settings.BasicPlanMonthlyPrice,
                IntermediaryPlanMonthlyPrice = settings.IntermediaryPlanMonthlyPrice,
                PremiumPlanMonthlyPrice = settings.PremiumPlanMonthlyPrice,
                PicpayPixKey = settings.PicpayPixKey,
                Design = StoreDesignMapper.ToDto(settings.GetEffectivePublicDesign()),
                DirectPixSalesEnabled = settings.DirectPixSalesEnabled,
                PixGatewayMode = settings.PixGatewayMode,
                StorePixKey = settings.StorePixKey,
                StorePixReceiverName = settings.StorePixReceiverName,
                OriginCep = settings.OriginCep,
                OriginCity = settings.OriginCity,
                OriginState = settings.OriginState,
                DeliveryScopeMode = settings.DeliveryScopeMode,
                MaxDeliveryDistanceKm = settings.MaxDeliveryDistanceKm,
                DefaultPackageWeightGrams = settings.DefaultPackageWeightGrams,
                ServerHostingMode = settings.ServerHostingMode,
                OwnServerProvider = settings.OwnServerProvider,
                CentralCatalogBaseUrl = settings.CentralCatalogBaseUrl
            },
            Categories = categories.Select(c => new SaveCategoryRequest
            {
                Name = c.Name,
                Description = c.Description,
                DisplayOrder = c.DisplayOrder,
                IsActive = c.IsActive
            }).ToList(),
            Products = products.Select(p =>
            {
                categoriesById.TryGetValue(p.CategoryId, out var cat);
                return new SaveProductRequest
                {
                    CategoryId = p.CategoryId,
                    CategoryName = cat?.Name ?? p.Category?.Name,
                    Name = p.Name,
                    Description = p.Description,
                    BasePrice = p.BasePrice,
                    IsVisibleOnSite = p.IsVisibleOnSite
                };
            }).ToList(),
            CatalogSnapshot = products.Select(p =>
            {
                categoriesById.TryGetValue(p.CategoryId, out var cat);
                return new CloudProductSyncDto
                {
                    SourceId = p.Id,
                    CategoryName = cat?.Name ?? p.Category?.Name ?? "Geral",
                    CategorySlug = cat?.Slug ?? p.Category?.Slug,
                    Name = p.Name,
                    Slug = p.Slug,
                    Description = p.Description,
                    BasePrice = p.BasePrice,
                    IsVisibleOnSite = p.IsVisibleOnSite,
                    Variants = p.Variants
                        .Where(v => v.IsActive)
                        .Select(v => new CloudProductVariantSyncDto
                        {
                            Color = v.Color,
                            ColorHex = v.ColorHex,
                            Size = v.Size,
                            Sku = v.Sku,
                            StockQuantity = v.StockQuantity,
                            LowStockThreshold = v.LowStockThreshold,
                            IsActive = v.IsActive
                        })
                        .ToList(),
                    Images = p.Images
                        .OrderBy(i => i.DisplayOrder)
                        .Select(i => new CloudProductImageSyncDto
                        {
                            PublicUrl = i.PublicUrl,
                            StoragePath = i.StoragePath,
                            AltText = i.AltText,
                            DisplayOrder = i.DisplayOrder,
                            IsPrimary = i.IsPrimary
                        })
                        .ToList()
                };
            }).ToList()
        };
    }

    /// <summary>
    /// Armazena a URL do servidor central recebida automaticamente do YouLink Admin.
    /// </summary>
    public async Task SyncCentralServerUrlFromCatalogAsync(
        string? centralCatalogServerUrl,
        CancellationToken ct = default)
    {
        var normalized = NormalizePublicServerUrl(centralCatalogServerUrl);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        SaveCachedCentralCatalogUrl(normalized);
        var settings = await _repository.GetStoreSettingsAsync(ct);

        if (string.IsNullOrWhiteSpace(settings.CentralCatalogBaseUrl) ||
            (string.Equals(settings.ServerHostingMode, HostingModePlatformShared, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(settings.CentralCatalogBaseUrl, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            settings.ConfigureCentralPublishing(normalized, settings.CentralPublishToken, preserveExistingToken: true);
            await _repository.UpdateStoreSettingsAsync(settings, ct);
            OnSyncStatusChanged?.Invoke();
        }
    }

    /// <summary>
    /// Publica e sincroniza a loja no Servidor Central em 1 clique, sem exigir Render ou senhas técnicas.
    /// </summary>
    public async Task<(bool Success, string PublicStoreUrl, string Message)> PublishToCentralServerAsync(
        string? centralServerUrl = null,
        string? ownerEmailOverride = null,
        CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            IsSyncing = true;
            LastSyncError = null;
            OnSyncStatusChanged?.Invoke();

            var settings = await _repository.GetStoreSettingsAsync(ct);
            var targetBase = ResolveCentralServerBaseUrl(settings, centralServerUrl);
            if (string.IsNullOrWhiteSpace(targetBase))
            {
                const string missingMsg = "Informe o endereço do Servidor Central YouLink (ou conecte ao YouLink Admin para recebê-lo automaticamente).";
                LastSyncError = missingMsg;
                OnSyncStatusChanged?.Invoke();
                return (false, string.Empty, missingMsg);
            }

            if (string.IsNullOrWhiteSpace(settings.StoreSlug))
            {
                settings.StoreSlug = WhatsAppLinkBuilder.GenerateStoreSlug(settings.StoreName);
            }

            SaveCachedCentralCatalogUrl(targetBase);
            settings.ConfigureCentralPublishing(targetBase, settings.CentralPublishToken, preserveExistingToken: true);
            await _repository.UpdateStoreSettingsAsync(settings, ct);

            return await PublishStoreToCentralCoreAsync(
                settings,
                targetBase,
                ownerEmailOverride,
                ct,
                progress);
        }
        catch (Exception ex)
        {
            LastSyncError = $"Erro ao publicar no servidor central: {ex.Message}";
            IsCloudOnline = false;
            OnSyncStatusChanged?.Invoke();
            return (false, string.Empty, LastSyncError);
        }
        finally
        {
            IsSyncing = false;
            _syncLock.Release();
            OnSyncStatusChanged?.Invoke();
        }
    }

    /// <summary>
    /// Sincroniza o catálogo local com o site 24h (Servidor Central 1-clique ou servidor próprio Render).
    /// </summary>
    public async Task<(bool Success, string Message)> SyncCatalogToCloudAsync(
        CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            IsSyncing = true;
            LastSyncError = null;
            OnSyncStatusChanged?.Invoke();

            var settings = await _repository.GetStoreSettingsAsync(ct);
            var cleanSecret = NormalizeSyncSecretInput(settings.OwnServerSyncSecret);
            var slug = ResolveEffectiveStoreSlug(settings);
            var publishToken = ResolvePublishToken(settings, slug);
            var isSharedMode = string.Equals(
                settings.ServerHostingMode,
                HostingModePlatformShared,
                StringComparison.OrdinalIgnoreCase);

            var centralBase = ResolveCentralServerBaseUrl(settings);
            var shouldUseCentralPublish =
                isSharedMode ||
                !string.IsNullOrWhiteSpace(publishToken) ||
                (string.IsNullOrWhiteSpace(cleanSecret) && !string.IsNullOrWhiteSpace(centralBase));

            if (shouldUseCentralPublish && !string.IsNullOrWhiteSpace(centralBase))
            {
                var centralRes = await PublishStoreToCentralCoreAsync(
                    settings,
                    centralBase,
                    ownerEmailOverride: null,
                    ct,
                    progress);
                return (centralRes.Success, centralRes.Message);
            }

            var normalizedUrl = NormalizePublicServerUrl(settings.OnlineBaseUrl);
            if (string.IsNullOrWhiteSpace(normalizedUrl))
            {
                return (false, "URL do site não configurada. Publique em 1 clique no Servidor Central ou cole a URL gerada no Render.");
            }

            return await SyncOwnServerCoreAsync(settings, normalizedUrl, cleanSecret, ct, progress);
        }
        catch (Exception ex)
        {
            LastSyncError = $"Erro de conexão com o servidor online: {ex.Message}";
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
    /// Busca pedidos e confirmações automáticas de Pix realizados na vitrine online e aplica no estoque local.
    /// </summary>
    public async Task<(bool Success, int UpdatedOrdersCount, string Message)> PullOrdersFromCloudAsync(
        Guid actingEmployeeId = default,
        CancellationToken ct = default)
    {
        try
        {
            var settings = await _repository.GetStoreSettingsAsync(ct);
            return await PullOrdersFromCloudCoreAsync(settings, actingEmployeeId, ct);
        }
        catch (Exception ex)
        {
            return (false, 0, $"Não foi possível buscar pedidos online: {ex.Message}");
        }
    }

    /// <summary>
    /// Consulta no servidor se a conta Mercado Pago da loja está conectada para recebimento Pix automático.
    /// </summary>
    public async Task<MercadoPagoStatusDto> GetMercadoPagoStatusFromServerAsync(CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var baseUrl = ResolveStoreControlBaseUrl(settings);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new MercadoPagoStatusDto(
                IsAppConfiguredOnServer: false,
                IsStoreConnected: settings.HasMercadoPagoConnected,
                PixGatewayMode: settings.PixGatewayMode,
                AccountLabel: settings.MercadoPagoAccountLabel,
                UserId: settings.MercadoPagoUserId,
                ConnectedAt: settings.MercadoPagoConnectedAt,
                Message: "Publique sua loja online primeiro para verificar a conexão com o Mercado Pago.");
        }

        var slug = ResolveEffectiveStoreSlug(settings);
        var url = $"{baseUrl}/api/payments/mercadopago/status?storeSlug={Uri.EscapeDataString(slug)}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));

        using var resp = await _externalHttpClient.GetAsync(url, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            return new MercadoPagoStatusDto(
                IsAppConfiguredOnServer: false,
                IsStoreConnected: settings.HasMercadoPagoConnected,
                PixGatewayMode: settings.PixGatewayMode,
                AccountLabel: settings.MercadoPagoAccountLabel,
                UserId: settings.MercadoPagoUserId,
                ConnectedAt: settings.MercadoPagoConnectedAt,
                Message: $"Servidor respondeu HTTP {(int)resp.StatusCode} ao consultar o Mercado Pago.");
        }

        var status = await resp.Content.ReadFromJsonAsync<MercadoPagoStatusDto>(cancellationToken: cts.Token);
        if (status is null)
        {
            return new MercadoPagoStatusDto(
                false,
                settings.HasMercadoPagoConnected,
                settings.PixGatewayMode,
                settings.MercadoPagoAccountLabel,
                settings.MercadoPagoUserId,
                settings.MercadoPagoConnectedAt,
                "Resposta inválida do servidor ao consultar Mercado Pago.");
        }

        var modeChanged = !string.Equals(settings.PixGatewayMode, status.PixGatewayMode, StringComparison.OrdinalIgnoreCase);
        var connChanged = settings.HasMercadoPagoConnected != status.IsStoreConnected ||
                          !string.Equals(settings.MercadoPagoUserId, status.UserId, StringComparison.Ordinal);

        if (connChanged || modeChanged)
        {
            settings.ConfigureMercadoPagoConnection(
                status.IsStoreConnected,
                status.UserId,
                status.AccountLabel,
                status.ConnectedAt,
                status.PixGatewayMode);
            await _repository.UpdateStoreSettingsAsync(settings, ct);
            OnSyncStatusChanged?.Invoke();
        }

        return status;
    }

    /// <summary>
    /// Inicia a conexão OAuth em 1 clique do Mercado Pago no servidor online e retorna a URL para abrir no navegador.
    /// </summary>
    public async Task<MercadoPagoOAuthStartResponse> StartMercadoPagoOAuthOnServerAsync(
        string? ownerEmailOverride = null,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var baseUrl = ResolveStoreControlBaseUrl(settings);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new MercadoPagoOAuthStartResponse(
                false,
                null,
                null,
                "Configure ou publique a loja em um Servidor Central primeiro antes de conectar o Mercado Pago.");
        }

        var slug = ResolveEffectiveStoreSlug(settings);
        var publishToken = ResolvePublishToken(settings, slug);
        var ownerEmail = await ResolveOwnerEmailAsync(settings, ownerEmailOverride, ct);
        var url = $"{baseUrl}/api/payments/mercadopago/oauth/start?storeSlug={Uri.EscapeDataString(slug)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        ApplyStoreControlHeaders(request, settings, slug, publishToken, ownerEmail, includeSyncSecretIfByo: true);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await _externalHttpClient.SendAsync(request, cts.Token);
        var raw = await resp.Content.ReadAsStringAsync(cts.Token);

        if (!resp.IsSuccessStatusCode)
        {
            return new MercadoPagoOAuthStartResponse(false, null, null, FormatSyncHttpError((int)resp.StatusCode, raw));
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var isConfigured = root.TryGetProperty("isConfigured", out var ic) && ic.ValueKind == JsonValueKind.True;
        var authUrl = root.TryGetProperty("authorizationUrl", out var au) ? au.GetString() : null;
        var stateToken = root.TryGetProperty("stateToken", out var st) ? st.GetString() : null;
        var issuedToken = root.TryGetProperty("publishToken", out var pt) ? pt.GetString() : null;
        var msg = root.TryGetProperty("message", out var mg) ? mg.GetString() : null;

        if (!string.IsNullOrWhiteSpace(issuedToken))
        {
            SaveCachedPublishToken(slug, issuedToken);
            settings.ConfigureCentralPublishing(baseUrl, issuedToken);
            await _repository.UpdateStoreSettingsAsync(settings, ct);
        }

        return new MercadoPagoOAuthStartResponse(
            isConfigured,
            authUrl,
            stateToken,
            msg ?? (isConfigured ? "Autorização iniciada." : "Aplicativo Mercado Pago não configurado no servidor."));
    }

    /// <summary>
    /// Desconecta a conta Mercado Pago desta loja e volta para Pix Direto gratuito.
    /// </summary>
    public async Task<(bool Success, string Message)> DisconnectMercadoPagoOnServerAsync(CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var baseUrl = ResolveStoreControlBaseUrl(settings);
        var slug = ResolveEffectiveStoreSlug(settings);

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            var publishToken = ResolvePublishToken(settings, slug);
            var ownerEmail = await ResolveOwnerEmailAsync(settings, null, ct);
            var url = $"{baseUrl}/api/payments/mercadopago/disconnect?storeSlug={Uri.EscapeDataString(slug)}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            ApplyStoreControlHeaders(request, settings, slug, publishToken, ownerEmail, includeSyncSecretIfByo: true);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            using var resp = await _externalHttpClient.SendAsync(request, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                return (false, FormatSyncHttpError((int)resp.StatusCode, body));
            }
        }

        settings.ConfigureMercadoPagoConnection(false, null, null, null, StoreSettings.PixGatewayDirect);
        await _repository.UpdateStoreSettingsAsync(settings, ct);
        OnSyncStatusChanged?.Invoke();
        return (true, "Mercado Pago desconectado. Sua loja voltou para o Pix Direto Gratuito (Taxa 0%).");
    }

    private async Task<(bool Success, string PublicStoreUrl, string Message)> PublishStoreToCentralCoreAsync(
        StoreSettings settings,
        string targetBase,
        string? ownerEmailOverride,
        CancellationToken ct,
        IProgress<string>? progress)
    {
        // Puxa pedidos pagos na vitrine antes de enviar o catálogo para não sobrescrever baixas de estoque
        await PullOrdersFromCloudCoreAsync(settings, Guid.Empty, ct);

        settings = await _repository.GetStoreSettingsAsync(ct);
        var categories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);

        var slug = ResolveEffectiveStoreSlug(settings);
        var publishToken = ResolvePublishToken(settings, slug);
        var ownerEmail = await ResolveOwnerEmailAsync(settings, ownerEmailOverride, ct);

        settings.OnlineBaseUrl = targetBase;
        settings.ServerHostingMode = HostingModePlatformShared;
        settings.ConfigureCentralPublishing(targetBase, publishToken, preserveExistingToken: true);

        var payload = BuildCatalogSyncPayload(settings, categories, products);
        var endpoint = $"{targetBase}/api/cloud/publish-store?storeSlug={Uri.EscapeDataString(slug)}";
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            ApplyStoreControlHeaders(request, settings, slug, publishToken, ownerEmail, includeSyncSecretIfByo: false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));

            try
            {
                var response = await _externalHttpClient.SendAsync(request, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<CloudStorePublishResponse>(cancellationToken: cts.Token);
                    var effectiveSlug = string.IsNullOrWhiteSpace(result?.StoreSlug) ? slug : result.StoreSlug.Trim();
                    var effectiveToken = !string.IsNullOrWhiteSpace(result?.PublishToken)
                        ? result.PublishToken!.Trim()
                        : publishToken;

                    if (!string.IsNullOrWhiteSpace(effectiveToken))
                    {
                        SaveCachedPublishToken(effectiveSlug, effectiveToken);
                    }

                    settings.StoreSlug = effectiveSlug;
                    settings.OnlineBaseUrl = targetBase;
                    settings.ServerHostingMode = HostingModePlatformShared;
                    settings.AutoSyncOnlineOnSave = true;
                    settings.IsSiteActive = result?.IsSiteActive ?? true;
                    settings.LastOnlineSyncAt = result?.SyncedAt ?? DateTimeOffset.UtcNow;
                    settings.ConfigureCentralPublishing(targetBase, effectiveToken, preserveExistingToken: true);
                    await _repository.UpdateStoreSettingsAsync(settings, ct);

                    LastSyncTime = settings.LastOnlineSyncAt;
                    IsCloudOnline = true;
                    OnSyncStatusChanged?.Invoke();

                    var publicUrl = !string.IsNullOrWhiteSpace(result?.PublicStoreUrl)
                        ? result.PublicStoreUrl
                        : WhatsAppLinkBuilder.BuildStoreCatalogUrl(targetBase, effectiveSlug);

                    return (
                        true,
                        publicUrl,
                        $"Site da loja publicado em 1 clique! ({products.Count} produtos e {categories.Count} categorias no ar)");
                }

                if (response.StatusCode == HttpStatusCode.NotFound &&
                    !string.IsNullOrWhiteSpace(NormalizeSyncSecretInput(settings.OwnServerSyncSecret)))
                {
                    var legacyRes = await SyncOwnServerCoreAsync(
                        settings,
                        targetBase,
                        NormalizeSyncSecretInput(settings.OwnServerSyncSecret),
                        ct,
                        progress);
                    var fallbackUrl = WhatsAppLinkBuilder.BuildStoreCatalogUrl(targetBase, slug);
                    return (legacyRes.Success, fallbackUrl, legacyRes.Message);
                }

                if (IsTransientWakeupStatus(response.StatusCode) && attempt < maxAttempts)
                {
                    progress?.Report($"⏳ Acordando o Servidor Central (tentativa {attempt + 1}/{maxAttempts})...");
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
                    continue;
                }

                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                LastSyncError = FormatSyncHttpError((int)response.StatusCode, errorBody);
                OnSyncStatusChanged?.Invoke();
                return (false, string.Empty, LastSyncError);
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested && IsTransientWakeupException(ex))
            {
                progress?.Report($"⏳ Aguardando resposta do servidor online (tentativa {attempt + 1}/{maxAttempts})...");
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }

        LastSyncError = "O servidor demorou para responder. Aguarde alguns segundos e clique novamente.";
        OnSyncStatusChanged?.Invoke();
        return (false, string.Empty, LastSyncError);
    }

    private async Task<(bool Success, string Message)> SyncOwnServerCoreAsync(
        StoreSettings settings,
        string normalizedUrl,
        string cleanSecret,
        CancellationToken ct,
        IProgress<string>? progress)
    {
        await PullOrdersFromCloudCoreAsync(settings, Guid.Empty, ct);
        settings = await _repository.GetStoreSettingsAsync(ct);

        var categories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);
        var payload = BuildCatalogSyncPayload(settings, categories, products);
        var cloudEndpoint = $"{normalizedUrl}/api/cloud/sync-state";
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, cloudEndpoint)
            {
                Content = JsonContent.Create(payload)
            };

            if (!string.IsNullOrWhiteSpace(cleanSecret))
            {
                request.Headers.TryAddWithoutValidation("X-YouLink-Sync-Key", cleanSecret);
            }

            var slug = ResolveEffectiveStoreSlug(settings);
            if (!string.IsNullOrWhiteSpace(slug))
            {
                request.Headers.TryAddWithoutValidation("X-YouLink-Store-Slug", slug);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));

            try
            {
                var response = await _externalHttpClient.SendAsync(request, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    settings.LastOnlineSyncAt = DateTimeOffset.UtcNow;
                    await _repository.UpdateStoreSettingsAsync(settings, ct);

                    LastSyncTime = settings.LastOnlineSyncAt;
                    IsCloudOnline = true;
                    OnSyncStatusChanged?.Invoke();
                    return (true, $"Catálogo online atualizado com sucesso! ({products.Count} produtos e {categories.Count} categorias sincronizados).");
                }

                if (IsTransientWakeupStatus(response.StatusCode) && attempt < maxAttempts)
                {
                    progress?.Report($"⏳ Finalizando ativação no Render (tentativa {attempt + 1}/{maxAttempts})...");
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
                    continue;
                }

                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                LastSyncError = FormatSyncHttpError((int)response.StatusCode, errorBody);
                OnSyncStatusChanged?.Invoke();
                return (false, LastSyncError);
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested && IsTransientWakeupException(ex))
            {
                progress?.Report($"⏳ Aguardando resposta do Render (tentativa {attempt + 1}/{maxAttempts})...");
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }

        LastSyncError = "O servidor gratuito demorou para responder. Aguarde alguns segundos e clique novamente.";
        OnSyncStatusChanged?.Invoke();
        return (false, LastSyncError);
    }

    private async Task<(bool Success, int UpdatedOrdersCount, string Message)> PullOrdersFromCloudCoreAsync(
        StoreSettings settings,
        Guid actingEmployeeId,
        CancellationToken ct)
    {
        var baseUrl = ResolveStoreControlBaseUrl(settings);
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return (false, 0, "Servidor online não configurado para buscar pedidos.");
        }

        var slug = ResolveEffectiveStoreSlug(settings);
        var publishToken = ResolvePublishToken(settings, slug);
        var cleanSecret = NormalizeSyncSecretInput(settings.OwnServerSyncSecret);
        if (string.IsNullOrWhiteSpace(publishToken) && string.IsNullOrWhiteSpace(cleanSecret))
        {
            return (false, 0, "Loja ainda não sincronizada para leitura remota de pedidos.");
        }

        var ownerEmail = await ResolveOwnerEmailAsync(settings, null, ct);
        var endpoint = $"{baseUrl}/api/cloud/orders?storeSlug={Uri.EscapeDataString(slug)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyStoreControlHeaders(request, settings, slug, publishToken, ownerEmail, includeSyncSecretIfByo: true);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await _externalHttpClient.SendAsync(request, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            return (false, 0, $"HTTP {(int)resp.StatusCode} ao buscar pedidos online.");
        }

        var remote = await resp.Content.ReadFromJsonAsync<CloudSyncOrdersResponse>(cancellationToken: cts.Token);
        if (remote is null)
            return (false, 0, "Resposta vazia ao buscar pedidos online.");

        var updated = 0;
        if (_orderAndShippingService is not null && remote.Orders.Count > 0)
        {
            updated = await _orderAndShippingService.MergeRemoteOrdersAsync(remote.Orders, actingEmployeeId, ct);
        }

        LastOrdersSyncTime = DateTimeOffset.UtcNow;
        if (updated > 0)
        {
            OnSyncStatusChanged?.Invoke();
        }

        return (true, updated, $"Pedidos atualizados ({remote.Orders.Count} no servidor, {updated} atualizações locais).");
    }

    private string ResolveStoreControlBaseUrl(StoreSettings settings)
    {
        var online = NormalizePublicServerUrl(settings.OnlineBaseUrl);
        if (!string.IsNullOrWhiteSpace(online))
            return online;

        return ResolveCentralServerBaseUrl(settings);
    }

    private async Task<string?> ResolveOwnerEmailAsync(
        StoreSettings settings,
        string? explicitEmail,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(explicitEmail))
            return explicitEmail.Trim();

        if (!string.IsNullOrWhiteSpace(settings.GoogleAccountEmail))
            return settings.GoogleAccountEmail.Trim();

        try
        {
            var employees = await _repository.GetEmployeesAsync(ct);
            var admin = employees.FirstOrDefault(e =>
                            e.IsActive &&
                            string.Equals(e.User?.Role?.Name, RoleNames.Admin, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(e.User?.Email))
                        ?? employees.FirstOrDefault(e => e.IsActive && !string.IsNullOrWhiteSpace(e.User?.Email));
            return admin?.User?.Email?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyStoreControlHeaders(
        HttpRequestMessage request,
        StoreSettings settings,
        string slug,
        string? publishToken,
        string? ownerEmail,
        bool includeSyncSecretIfByo)
    {
        if (!string.IsNullOrWhiteSpace(slug))
            request.Headers.TryAddWithoutValidation("X-YouLink-Store-Slug", slug);

        if (!string.IsNullOrWhiteSpace(publishToken))
            request.Headers.TryAddWithoutValidation("X-YouLink-Publish-Token", publishToken.Trim());

        if (!string.IsNullOrWhiteSpace(ownerEmail))
            request.Headers.TryAddWithoutValidation("X-YouLink-Owner-Email", ownerEmail.Trim());

        var isSharedMode = string.Equals(
            settings.ServerHostingMode,
            HostingModePlatformShared,
            StringComparison.OrdinalIgnoreCase);

        if (includeSyncSecretIfByo && !isSharedMode)
        {
            var cleanSecret = NormalizeSyncSecretInput(settings.OwnServerSyncSecret);
            if (!string.IsNullOrWhiteSpace(cleanSecret))
            {
                request.Headers.TryAddWithoutValidation("X-YouLink-Sync-Key", cleanSecret);
            }
        }
    }

    private static string FormatSyncHttpError(int statusCode, string? responseBody)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(err.GetString()))
                {
                    return $"Falha na operação online (HTTP {statusCode}): {err.GetString()}";
                }
            }
            catch
            {
            }
        }

        if (statusCode is 401 or 403)
        {
            return "Chave YOULINK_SYNC_SECRET inválida ou ausente (HTTP 401). Copie exatamente o valor da variável YOULINK_SYNC_SECRET no painel do Render.";
        }

        return $"Falha ao sincronizar com o servidor online: HTTP {statusCode}";
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

    /// <summary>
    /// Se o servidor gratuito na nuvem reiniciou após hibernação e acordou vazio,
    /// reenvia o catálogo automaticamente para manter o link oficial sempre atualizado.
    /// </summary>
    public async Task EnsureCloudMirrorPopulatedAsync(CancellationToken ct = default)
    {
        try
        {
            var settings = await _repository.GetStoreSettingsAsync(ct);
            if (!settings.AutoSyncOnlineOnSave)
                return;

            var normalizedUrl = NormalizePublicServerUrl(settings.OnlineBaseUrl);
            if (string.IsNullOrWhiteSpace(normalizedUrl) ||
                normalizedUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                normalizedUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var slug = ResolveEffectiveStoreSlug(settings);
            var statusUrl = BuildCloudStatusUrl(normalizedUrl, slug);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            using var resp = await _externalHttpClient.GetAsync(statusUrl, cts.Token);
            if (!resp.IsSuccessStatusCode)
                return;

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);

            var remoteStoreName = doc.RootElement.TryGetProperty("storeName", out var sn) ? sn.GetString() : null;
            var remoteProducts = doc.RootElement.TryGetProperty("productsCount", out var pc) && pc.TryGetInt32(out var pCount)
                ? pCount
                : -1;

            var localProducts = await _repository.GetProductsAsync(includeArchived: false, ct);
            var nameMismatch = !string.Equals(remoteStoreName?.Trim(), settings.StoreName?.Trim(), StringComparison.OrdinalIgnoreCase);
            var emptyRemoteNeedsSync = remoteProducts == 0 && (localProducts.Count > 0 || nameMismatch);

            if (emptyRemoteNeedsSync)
            {
                await SyncCatalogToCloudAsync(ct);
            }
        }
        catch
        {
        }
    }
}
