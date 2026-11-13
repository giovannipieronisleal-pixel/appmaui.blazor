using System.Net.Http.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace appmaui.blazor.Services;

/// <summary>
/// Serviço de sincronização e publicação 24h online do catálogo no Google Cloud Run.
/// Executa sincronização sob demanda (botão "Criar/Atualizar Site") e automática toda vez que itens são salvos.
/// </summary>
public sealed class GoogleCloudSyncService
{
    private readonly IClothingStoreRepository _repository;
    private readonly AdminApiClient _adminApiClient;
    private readonly HttpClient _httpClient;
    private static readonly HttpClient PublicDnsClient = new() { Timeout = TimeSpan.FromSeconds(6) };
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

            // Testa endpoint de status ou health check do Google Cloud Run
            var checkUrl = $"{cleanUrl}/api/cloud/status";
            var resp = await _httpClient.GetAsync(checkUrl, cts.Token);
            var isOk = resp.IsSuccessStatusCode;

            if (!isOk)
            {
                // Fallback para rota pública da loja
                var publicUrl = $"{cleanUrl}/api/public/store-info";
                var resp2 = await _httpClient.GetAsync(publicUrl, cts.Token);
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

    /// <summary>
    /// Consulta se o domínio desejado para o site está disponível no Google Cloud.
    /// </summary>
    public async Task<CheckDomainAvailabilityResponse> CheckDomainAvailabilityAsync(
        string rawDomain,
        CancellationToken ct = default)
    {
        return await GoogleCloudDomainChecker.EvaluateDomainAsync(
            rawDomain,
            PublicDnsClient,
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
            PublicDnsClient,
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
        FreeDomainFinder.UpdateDuckDnsIpAsync(subdomainOrDomain, token, ipv4, PublicDnsClient, ct);

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
                    DefaultPackageWeightGrams = settings.DefaultPackageWeightGrams
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

            // Se houver token na sessão administrativa, inclui
            if (!string.IsNullOrWhiteSpace(_adminApiClient.CurrentEmployee?.Email))
            {
                // Token ou cabeçalho de sincronização
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var response = await _httpClient.SendAsync(request, cts.Token);
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
