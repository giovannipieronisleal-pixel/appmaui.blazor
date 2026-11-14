using System.Text.Json;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace appmaui.blazor.Services;

/// <summary>
/// Gateway administrativo para o aplicativo .NET MAUI.
/// Gerencia a sessão autenticada no SecureStorage do dispositivo, comunica-se com a ASP.NET Core Web API
/// e faz fallback instantâneo (sem bloqueio por timeout) para o banco de dados compartilhado quando a API não está rodando separadamente.
/// </summary>
public sealed class MauiAdminGateway
{
    private const string SessionStorageKey = "clothingstore_auth_session_v1";
    private const string ServerUrlPreferenceKey = "youlink_api_base_url_v1";
    private const string PendingAccountSyncPreferenceKey = "youlink_pending_account_sync_v1";
    private const string PendingPlanOrderPreferenceKey = "youlink_pending_plan_order_v1";
    private static string ResolveCurrentClientAppVersion()
    {
        try
        {
            var version = AppInfo.Current.VersionString;
            var normalized = AppUpdateCatalog.NormalizeVersion(version);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }
        catch
        {
        }

        return AppUpdateCatalog.CurrentAppVersion;
    }

    private static readonly HttpClient FallbackDnsHttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly AdminApiClient _apiClient;
    private readonly AuthService _authService;
    private readonly ProductManagementService _productService;
    private readonly StockManagementService _stockService;
    private readonly EmployeeAndSettingsService _employeeAndSettingsService;
    private readonly GoogleCloudSyncService? _cloudSyncService;
    private readonly OrderAndShippingService? _orderAndShippingService;
    private readonly PlatformSubscriptionService? _subscriptionService;

    private DateTimeOffset _skipRemoteApiUntil = DateTimeOffset.MinValue;

    public EmployeeProfileDto? CurrentEmployee { get; private set; }
    public string? AccessToken { get; private set; }
    public bool IsAuthenticated => CurrentEmployee is not null && CurrentEmployee.IsActive;
    public bool IsAdmin => CurrentEmployee?.IsAdmin ?? false;
    public bool CanManageProducts => IsAdmin || (CurrentEmployee?.CanManageProducts ?? false);
    public bool CanManageStock => IsAdmin || (CurrentEmployee?.CanManageStock ?? false);
    public bool HasCustomServerUrl { get; private set; }
    public bool LastAccountActionWasLocalOnly { get; private set; }
    public int LastSyncedAccountsCount { get; private set; }
    public string? LastSyncedAccountsMessage { get; private set; }
    public bool IsRemoteApiOnline => DateTimeOffset.UtcNow >= _skipRemoteApiUntil;
    public string CurrentServerUrl => _apiClient.CurrentBaseAddress ?? string.Empty;
    public ApiCompatibilityStatusDto? LatestCompatibilityStatus { get; private set; }

    public event Action? AuthStateChanged;
    public event Action<AdminStoreSettingsDto>? PlanEntitlementActivated;

    public MauiAdminGateway(
        AdminApiClient apiClient,
        AuthService authService,
        ProductManagementService productService,
        StockManagementService stockService,
        EmployeeAndSettingsService employeeAndSettingsService,
        GoogleCloudSyncService? cloudSyncService = null,
        OrderAndShippingService? orderAndShippingService = null,
        PlatformSubscriptionService? subscriptionService = null)
    {
        _apiClient = apiClient;
        _authService = authService;
        _productService = productService;
        _stockService = stockService;
        _employeeAndSettingsService = employeeAndSettingsService;
        _cloudSyncService = cloudSyncService;
        _orderAndShippingService = orderAndShippingService;
        _subscriptionService = subscriptionService;

        LoadSavedServerUrl();
    }

    private void LoadSavedServerUrl()
    {
        try
        {
            var savedUrl = Preferences.Default.Get(ServerUrlPreferenceKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(savedUrl) && _apiClient.UpdateBaseAddress(savedUrl))
            {
                HasCustomServerUrl = true;
            }
        }
        catch
        {
        }
    }

    public async Task<(bool Reachable, string NormalizedUrl, string? StoreName, string? ErrorMessage)> ConfigureServerUrlAsync(
        string? rawUrl,
        CancellationToken ct = default)
    {
        _skipRemoteApiUntil = DateTimeOffset.MinValue;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            _apiClient.UpdateBaseAddress(null);
            HasCustomServerUrl = false;
            try
            {
                Preferences.Default.Remove(ServerUrlPreferenceKey);
            }
            catch
            {
            }

            var defaultTest = await _apiClient.TestServerConnectionAsync(null, ct);
            if (!defaultTest.Reachable)
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            else
                await TrySyncLocalAccountsQuietlyAsync(ct);

            AuthStateChanged?.Invoke();
            return defaultTest;
        }

        var probe = await _apiClient.TestServerConnectionAsync(rawUrl, ct);
        if (_apiClient.UpdateBaseAddress(probe.NormalizedUrl))
        {
            HasCustomServerUrl = true;
            try
            {
                Preferences.Default.Set(ServerUrlPreferenceKey, probe.NormalizedUrl);
            }
            catch
            {
            }
        }

        if (!probe.Reachable)
        {
            _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(60);
        }
        else
        {
            await TrySyncLocalAccountsQuietlyAsync(ct);
        }

        AuthStateChanged?.Invoke();
        return probe;
    }

    private static bool GetPendingAccountSync()
    {
        try
        {
            return Preferences.Default.Get(PendingAccountSyncPreferenceKey, false);
        }
        catch
        {
            return false;
        }
    }

    private static void SetPendingAccountSync(bool pending)
    {
        try
        {
            if (pending)
                Preferences.Default.Set(PendingAccountSyncPreferenceKey, true);
            else
                Preferences.Default.Remove(PendingAccountSyncPreferenceKey);
        }
        catch
        {
        }
    }

    private async Task<SyncLocalAccountsResultDto?> TrySyncLocalAccountsQuietlyAsync(CancellationToken ct = default)
    {
        try
        {
            return await PushLocalAccountsToServerAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Envia todas as contas locais deste aparelho para o servidor central / Receptor LAN do YouLink Admin.
    /// Idempotente: contas já existentes no destino são preservadas sem alteração.
    /// </summary>
    public async Task<SyncLocalAccountsResultDto> PushLocalAccountsToServerAsync(CancellationToken ct = default)
    {
        var employees = await _employeeAndSettingsService.GetEmployeesAsync(ct);
        var adminStore = await _employeeAndSettingsService.GetAdminStoreSettingsAsync(ct);
        var storeName = string.IsNullOrWhiteSpace(adminStore.StoreName) ? null : adminStore.StoreName.Trim();

        var items = employees
            .Select(e => new SyncLocalAccountItemDto(
                FullName: e.FullName,
                Username: e.Username,
                Email: e.Email,
                Phone: e.Phone,
                IsActive: e.IsActive,
                StoreName: storeName))
            .Take(PlatformAccountSyncProcessor.MaxBatchSize)
            .ToList();

        if (items.Count == 0 && CurrentEmployee is not null)
        {
            items.Add(new SyncLocalAccountItemDto(
                FullName: CurrentEmployee.FullName,
                Username: CurrentEmployee.Username,
                Email: CurrentEmployee.Email,
                Phone: CurrentEmployee.Phone,
                IsActive: CurrentEmployee.IsActive,
                StoreName: storeName));
        }

        if (items.Count == 0)
        {
            LastSyncedAccountsCount = 0;
            LastSyncedAccountsMessage = "Nenhuma conta local encontrada neste dispositivo para enviar.";
            SetPendingAccountSync(false);
            return new SyncLocalAccountsResultDto(0, 0, LastSyncedAccountsMessage);
        }

        var result = await _apiClient.SyncLocalAccountsAsync(new SyncLocalAccountsRequest(items), ct: ct);
        LastSyncedAccountsCount = result.ImportedCount;
        LastSyncedAccountsMessage = result.Message;
        SetPendingAccountSync(false);
        return result;
    }

    public async Task<ApiCompatibilityStatusDto?> RefreshCompatibilityAsync(CancellationToken ct = default)
    {
        if (!ShouldTryRemoteApi)
            return LatestCompatibilityStatus;

        try
        {
            var status = await _apiClient.GetCompatibilityStatusAsync(ResolveCurrentClientAppVersion(), ct);
            if (status is not null)
            {
                LatestCompatibilityStatus = status;
                AuthStateChanged?.Invoke();
            }
            return status;
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldTryRemoteApi => DateTimeOffset.UtcNow >= _skipRemoteApiUntil;

    private static bool IsConnectionOrTimeoutError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or TimeoutException;

    private async Task<T> ExecuteWithFallbackAsync<T>(
        Func<Task<T>> remoteCall,
        Func<Task<T>> localFallback)
    {
        if (ShouldTryRemoteApi)
        {
            try
            {
                return await remoteCall();
            }
            catch (Exception ex) when (IsConnectionOrTimeoutError(ex))
            {
                // Marca a API remota como offline por 60 segundos para evitar esperas de timeout a cada clique
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            }
        }

        return await localFallback();
    }

    private async Task ExecuteWithFallbackAsync(
        Func<Task> remoteCall,
        Func<Task> localFallback)
    {
        if (ShouldTryRemoteApi)
        {
            try
            {
                await remoteCall();
                return;
            }
            catch (Exception ex) when (IsConnectionOrTimeoutError(ex))
            {
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            }
        }

        await localFallback();
    }

    private static readonly Guid LegacyDefaultAdminUserId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    private static bool IsLegacyDefaultAdminProfile(EmployeeProfileDto? profile) =>
        profile is not null &&
        (profile.UserId == LegacyDefaultAdminUserId ||
         string.Equals(profile.Email, "admin", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(profile.Username, "admin", StringComparison.OrdinalIgnoreCase));

    public async Task TryRestoreSessionAsync()
    {
        if (IsAuthenticated)
            return;

        try
        {
            var rawJson = await SecureStorage.Default.GetAsync(SessionStorageKey);
            if (string.IsNullOrWhiteSpace(rawJson))
                rawJson = Preferences.Default.Get(SessionStorageKey + "_fallback", string.Empty);
            if (string.IsNullOrWhiteSpace(rawJson))
                return;

            var saved = JsonSerializer.Deserialize<LoginResponse>(rawJson);
            if (saved is null || saved.ExpiresAt <= DateTimeOffset.UtcNow || IsLegacyDefaultAdminProfile(saved.Employee))
            {
                ClearSavedSession();
                return;
            }

            _apiClient.SetSession(saved.AccessToken, saved.Employee);
            var freshProfile = await ExecuteWithFallbackAsync(
                () => _apiClient.RefreshProfileAsync(),
                async () =>
                {
                    try
                    {
                        return await _authService.GetCurrentProfileAsync(saved.Employee.UserId);
                    }
                    catch
                    {
                        return saved.Employee;
                    }
                });

            if (IsLegacyDefaultAdminProfile(freshProfile))
            {
                Logout();
                return;
            }

            AccessToken = saved.AccessToken;
            CurrentEmployee = freshProfile;
            _apiClient.SetSession(saved.AccessToken, freshProfile);
            AuthStateChanged?.Invoke();

            if (GetPendingAccountSync())
            {
                _ = TrySyncLocalAccountsQuietlyAsync();
            }
        }
        catch
        {
        }
    }

    public Task<AuthSetupStatusDto> GetSetupStatusAsync() =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetSetupStatusAsync(),
            () => _authService.GetSetupStatusAsync());

    public async Task<LoginResponse> RegisterUserAccountAsync(RegisterUserAccountRequest request)
    {
        var usedFallback = false;
        var response = await ExecuteWithFallbackAsync(
            async () =>
            {
                var remoteResp = await _apiClient.RegisterUserAccountAsync(request);
                LastAccountActionWasLocalOnly = false;
                return remoteResp;
            },
            async () =>
            {
                usedFallback = true;
                var localResp = await _authService.RegisterUserAccountAsync(request);
                _apiClient.SetSession(localResp.AccessToken, localResp.Employee);
                return localResp;
            });

        if (usedFallback)
        {
            LastAccountActionWasLocalOnly = true;
            SetPendingAccountSync(true);
        }
        else if (GetPendingAccountSync())
        {
            _ = TrySyncLocalAccountsQuietlyAsync();
        }

        await PersistSessionAsync(response);
        return response;
    }

    public async Task<LoginResponse> RegisterInitialAdminByEmailAsync(RegisterInitialAdminRequest request)
    {
        var response = await ExecuteWithFallbackAsync(
            () => _apiClient.RegisterInitialAdminByEmailAsync(request),
            async () =>
            {
                var localResp = await _authService.RegisterInitialAdminByEmailAsync(request);
                _apiClient.SetSession(localResp.AccessToken, localResp.Employee);
                return localResp;
            });

        await PersistSessionAsync(response);
        return response;
    }

    public async Task<LoginChallengeResponse> LoginWithChallengeAsync(LoginRequest request, bool rememberMe = true)
    {
        var usedFallback = false;
        var challenge = await ExecuteWithFallbackAsync(
            async () =>
            {
                var remoteChallenge = await _apiClient.LoginWithChallengeAsync(request);
                LastAccountActionWasLocalOnly = false;
                return remoteChallenge;
            },
            async () =>
            {
                usedFallback = true;
                var localChallenge = await _authService.LoginWithChallengeAsync(request);
                if (!localChallenge.RequiresTwoFactor && localChallenge.AuthResult is not null)
                {
                    _apiClient.SetSession(localChallenge.AuthResult.AccessToken, localChallenge.AuthResult.Employee);
                }
                return localChallenge;
            });

        if (usedFallback)
            LastAccountActionWasLocalOnly = true;

        if (!challenge.RequiresTwoFactor && challenge.AuthResult is not null)
        {
            await PersistSessionAsync(challenge.AuthResult, rememberMe);
        }
        return challenge;
    }

    public async Task<LoginResponse> VerifyTwoFactorLoginAsync(VerifyTwoFactorLoginRequest request, bool rememberMe = true)
    {
        var usedFallback = false;
        var response = await ExecuteWithFallbackAsync(
            async () =>
            {
                var remoteResp = await _apiClient.VerifyTwoFactorLoginAsync(request);
                LastAccountActionWasLocalOnly = false;
                return remoteResp;
            },
            async () =>
            {
                usedFallback = true;
                var localResp = await _authService.VerifyTwoFactorLoginAsync(request);
                _apiClient.SetSession(localResp.AccessToken, localResp.Employee);
                return localResp;
            });

        if (usedFallback)
            LastAccountActionWasLocalOnly = true;

        await PersistSessionAsync(response, rememberMe);
        return response;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, bool rememberMe = true)
    {
        var response = await ExecuteWithFallbackAsync(
            () => _apiClient.LoginAsync(request),
            async () =>
            {
                var localResp = await _authService.LoginAsync(request);
                _apiClient.SetSession(localResp.AccessToken, localResp.Employee);
                return localResp;
            });

        await PersistSessionAsync(response, rememberMe);
        return response;
    }

    public Task<RequestEmailLoginCodeResponse> RequestEmailLoginCodeAsync(RequestEmailLoginCodeRequest request) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.RequestEmailLoginCodeAsync(request),
            () => _authService.RequestEmailLoginCodeAsync(request));

    public async Task<LoginResponse> LoginWithEmailCodeAsync(VerifyEmailLoginCodeRequest request, bool rememberMe = true)
    {
        var response = await ExecuteWithFallbackAsync(
            () => _apiClient.LoginWithEmailCodeAsync(request),
            async () =>
            {
                var localResp = await _authService.LoginWithEmailCodeAsync(request);
                _apiClient.SetSession(localResp.AccessToken, localResp.Employee);
                return localResp;
            });

        await PersistSessionAsync(response, rememberMe);
        return response;
    }

    private async Task PersistSessionAsync(LoginResponse response, bool rememberMe = true)
    {
        AccessToken = response.AccessToken;
        CurrentEmployee = response.Employee;

        try
        {
            if (rememberMe)
            {
                var serialized = JsonSerializer.Serialize(response);
                try
                {
                    await SecureStorage.Default.SetAsync(SessionStorageKey, serialized);
                }
                catch
                {
                }
                Preferences.Default.Set(SessionStorageKey + "_fallback", serialized);
            }
            else
            {
                ClearSavedSession();
            }
        }
        catch
        {
        }

        AuthStateChanged?.Invoke();
    }

    private static void ClearSavedSession()
    {
        try
        {
            try
            {
                SecureStorage.Default.Remove(SessionStorageKey);
            }
            catch
            {
            }
            Preferences.Default.Remove(SessionStorageKey + "_fallback");
        }
        catch
        {
        }
    }

    public void Logout()
    {
        AccessToken = null;
        CurrentEmployee = null;
        _apiClient.ClearSession();
        ClearSavedSession();
        AuthStateChanged?.Invoke();
    }

    public Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.ForgotPasswordAsync(request),
            () => _authService.RequestPasswordResetAsync(request));

    public Task ResetPasswordAsync(ResetPasswordRequest request) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.ResetPasswordAsync(request),
            () => _authService.ResetPasswordAsync(request));

    public async Task ChangePasswordAsync(ChangePasswordRequest request)
    {
        EnsureAuthenticated();
        await _authService.ChangePasswordAsync(CurrentEmployee!.UserId, request);
    }

    public async Task<EmployeeProfileDto> UpdateUserProfileAsync(UpdateUserProfileRequest request)
    {
        EnsureAuthenticated();
        var updated = await _authService.UpdateUserProfileAsync(CurrentEmployee!.UserId, request);
        CurrentEmployee = updated;
        _apiClient.SetSession(AccessToken ?? string.Empty, updated);
        AuthStateChanged?.Invoke();
        return updated;
    }

    public async Task SetSecurityPinAsync(SetSecurityPinRequest request)
    {
        EnsureAuthenticated();
        await _authService.SetSecurityPinAsync(CurrentEmployee!.UserId, request);
        CurrentEmployee = CurrentEmployee with { HasSecurityPin = true };
        _apiClient.SetSession(AccessToken ?? string.Empty, CurrentEmployee);
        AuthStateChanged?.Invoke();
    }

    public async Task RemoveSecurityPinAsync(RemoveSecurityPinRequest request)
    {
        EnsureAuthenticated();
        await _authService.RemoveSecurityPinAsync(CurrentEmployee!.UserId, request);
        CurrentEmployee = CurrentEmployee with { HasSecurityPin = false };
        _apiClient.SetSession(AccessToken ?? string.Empty, CurrentEmployee);
        AuthStateChanged?.Invoke();
    }

    public Task<TwoFactorSetupDto> GenerateTwoFactorSetupAsync()
    {
        EnsureAuthenticated();
        return _authService.GenerateTwoFactorSetupAsync(CurrentEmployee!.UserId);
    }

    public async Task<EmployeeProfileDto> EnableTwoFactorAsync(EnableTwoFactorRequest request)
    {
        EnsureAuthenticated();
        var updated = await _authService.EnableTwoFactorAsync(CurrentEmployee!.UserId, request);
        CurrentEmployee = updated;
        _apiClient.SetSession(AccessToken ?? string.Empty, updated);
        AuthStateChanged?.Invoke();
        return updated;
    }

    public async Task<EmployeeProfileDto> DisableTwoFactorAsync(DisableTwoFactorRequest request)
    {
        EnsureAuthenticated();
        var updated = await _authService.DisableTwoFactorAsync(CurrentEmployee!.UserId, request);
        CurrentEmployee = updated;
        _apiClient.SetSession(AccessToken ?? string.Empty, updated);
        AuthStateChanged?.Invoke();
        return updated;
    }

    // Dashboard
    public Task<DashboardSummaryDto> GetDashboardSummaryAsync()
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetDashboardSummaryAsync(),
            () => _stockService.GetDashboardSummaryAsync());
    }

    // Categorias & Produtos
    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync()
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetCategoriesAsync(),
            () => _productService.GetAllCategoriesAsync());
    }

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request)
    {
        EnsureProductPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.CreateCategoryAsync(request),
            () => _productService.CreateCategoryAsync(request));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid categoryId, SaveCategoryRequest request)
    {
        EnsureProductPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.UpdateCategoryAsync(categoryId, request),
            () => _productService.UpdateCategoryAsync(categoryId, request));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public Task<IReadOnlyList<ProductDetailDto>> GetProductsAsync(
        string? search = null,
        Guid? categoryId = null,
        bool includeArchived = false)
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetProductsAsync(search, categoryId, includeArchived),
            () => _productService.GetAdminProductsAsync(search, categoryId, includeArchived));
    }

    public Task<ProductDetailDto> GetProductByIdAsync(Guid productId)
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetProductByIdAsync(productId),
            () => _productService.GetProductByIdAsync(productId));
    }

    public async Task<ProductDetailDto> CreateProductAsync(SaveProductRequest request)
    {
        EnsureProductPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.CreateProductAsync(request),
            () => _productService.CreateProductAsync(request, CurrentEmployee?.EmployeeId));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public async Task<ProductDetailDto> UpdateProductAsync(Guid productId, SaveProductRequest request)
    {
        EnsureProductPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.UpdateProductAsync(productId, request),
            () => _productService.UpdateProductAsync(productId, request));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public async Task<ProductDetailDto> ToggleProductVisibilityAsync(Guid productId, bool isVisibleOnSite)
    {
        EnsureProductPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.ToggleProductVisibilityAsync(productId, isVisibleOnSite),
            () => _productService.ToggleVisibilityAsync(productId, isVisibleOnSite));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public async Task DeleteOrArchiveProductAsync(Guid productId)
    {
        EnsureProductPermission();
        await ExecuteWithFallbackAsync(
            () => _apiClient.DeleteOrArchiveProductAsync(productId),
            () => _productService.DeleteOrArchiveProductAsync(productId));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
    }

    // Imagens do Produto
    public Task<ProductImageDto> AddProductImageByUrlAsync(Guid productId, AddProductImageByUrlRequest request)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.AddProductImageByUrlAsync(productId, request),
            () => _productService.AddProductImageByUrlAsync(productId, request));
    }

    public Task<ProductImageDto> UploadProductImageFileAsync(
        Guid productId,
        string fileName,
        Stream fileStream,
        string contentType,
        string? altText,
        bool isPrimary)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.UploadProductImageFileAsync(productId, fileName, fileStream, contentType, altText, isPrimary),
            () =>
            {
                if (fileStream.CanSeek)
                    fileStream.Position = 0;
                return _productService.UploadProductImageAsync(productId, fileName, fileStream, contentType, altText, isPrimary);
            });
    }

    public Task SetPrimaryProductImageAsync(Guid productId, Guid imageId)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.SetPrimaryProductImageAsync(productId, imageId),
            () => _productService.SetPrimaryProductImageAsync(productId, imageId));
    }

    public Task DeleteProductImageAsync(Guid productId, Guid imageId)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.DeleteProductImageAsync(productId, imageId),
            () => _productService.DeleteProductImageAsync(productId, imageId));
    }

    // Variantes
    public async Task<ProductVariantDto> AddVariantAsync(Guid productId, SaveProductVariantRequest request)
    {
        EnsureProductPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.AddVariantAsync(productId, request),
            () => _productService.AddVariantAsync(productId, CurrentEmployee!.EmployeeId, request));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public async Task<ProductVariantDto> UpdateVariantAsync(Guid variantId, SaveProductVariantRequest request)
    {
        EnsureProductPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.UpdateVariantAsync(variantId, request),
            () => _productService.UpdateVariantAsync(variantId, request, CurrentEmployee?.EmployeeId));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public async Task DeleteVariantAsync(Guid variantId)
    {
        EnsureProductPermission();
        await ExecuteWithFallbackAsync(
            () => _apiClient.DeleteVariantAsync(variantId),
            () => _productService.DeleteVariantAsync(variantId));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
    }

    // Controle de Estoque e Movimentações
    public async Task<StockMovementDto> RegisterStockMovementAsync(CreateStockMovementRequest request)
    {
        EnsureStockPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.RegisterStockMovementAsync(request),
            () => _stockService.RegisterMovementAsync(CurrentEmployee!.EmployeeId, request));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public Task<IReadOnlyList<StockMovementDto>> GetStockMovementsAsync(
        Guid? productId = null,
        Guid? variantId = null,
        int limit = 100)
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetStockMovementsAsync(productId, variantId, limit),
            () => _stockService.GetMovementsAsync(productId, variantId, null, limit));
    }

    // Funcionários (AdminOnly)
    public Task<IReadOnlyList<EmployeeProfileDto>> GetEmployeesAsync()
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetEmployeesAsync(),
            () => _employeeAndSettingsService.GetEmployeesAsync());
    }

    public async Task<EmployeeProfileDto> CreateEmployeeAsync(CreateEmployeeRequest request)
    {
        EnsureAdminPermission();

        if (ShouldTryRemoteApi)
        {
            try
            {
                var remote = await _apiClient.CreateEmployeeAsync(request);
                LastAccountActionWasLocalOnly = false;
                return remote;
            }
            catch (Exception ex) when (IsConnectionOrTimeoutError(ex))
            {
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            }
            catch (InvalidOperationException) when (HasCustomServerUrl)
            {
                // Sessão iniciada offline no celular antes de conectar o Servidor Central:
                // cria localmente e sincroniza a conta para o Receptor LAN / YouLink Admin.
            }
        }

        var local = await _employeeAndSettingsService.CreateEmployeeAsync(request);
        LastAccountActionWasLocalOnly = true;
        SetPendingAccountSync(true);
        var synced = await TrySyncLocalAccountsQuietlyAsync();
        if (synced is not null && synced.ImportedCount > 0)
        {
            LastAccountActionWasLocalOnly = false;
        }
        return local;
    }

    public Task<EmployeeProfileDto> UpdateEmployeeAsync(Guid employeeId, UpdateEmployeeRequest request)
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.UpdateEmployeeAsync(employeeId, request),
            () => _employeeAndSettingsService.UpdateEmployeeAsync(employeeId, request));
    }

    public Task<EmployeeProfileDto> SetEmployeeActiveStatusAsync(Guid employeeId, bool isActive)
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.SetEmployeeActiveStatusAsync(employeeId, isActive),
            () => _employeeAndSettingsService.SetEmployeeActiveStatusAsync(employeeId, isActive));
    }

    // Configurações da Loja (AdminOnly)
    public Task<AdminStoreSettingsDto> GetStoreSettingsAsync()
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetStoreSettingsAsync(),
            () => _employeeAndSettingsService.GetAdminStoreSettingsAsync());
    }

    /// <summary>
    /// Retorna configurações da loja incluindo a chave Pix/preços dos planos para qualquer usuário autenticado,
    /// sem sobrescrever com placeholder quando não for administrador.
    /// </summary>
    public async Task<AdminStoreSettingsDto> GetPlanCatalogSettingsAsync()
    {
        EnsureAuthenticated();
        if (IsAdmin)
        {
            return await GetStoreSettingsAsync();
        }

        return await _employeeAndSettingsService.GetAdminStoreSettingsAsync();
    }

    public async Task<AdminStoreSettingsDto> UpdateStoreSettingsAsync(
        UpdateStoreSettingsRequest request,
        bool suppressAutoSync = false)
    {
        EnsureAdminPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.UpdateStoreSettingsAsync(request),
            () => _employeeAndSettingsService.UpdateStoreSettingsAsync(CurrentEmployee!.EmployeeId, request));
        if (!suppressAutoSync)
        {
            _cloudSyncService?.TriggerAutoSyncIfEnabled();
        }
        return result;
    }

    public async Task<AdminStoreSettingsDto> ActivateSiteAsync(bool suppressAutoSync = false)
    {
        EnsureAdminPermission();
        var result = await _employeeAndSettingsService.ActivateStoreSiteAsync(CurrentEmployee?.EmployeeId);
        if (!suppressAutoSync)
        {
            _cloudSyncService?.TriggerAutoSyncIfEnabled();
        }
        return result;
    }

    public async Task<AdminStoreSettingsDto> DeactivateSiteAsync(string reason)
    {
        EnsureAdminPermission();
        var result = await _employeeAndSettingsService.DeactivateStoreSiteAsync(CurrentEmployee?.EmployeeId, reason);
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public Task<AdminStoreSettingsDto> UploadStoreLogoAsync(string fileName, Stream fileStream, string contentType)
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.UploadStoreLogoAsync(fileName, fileStream, contentType),
            () =>
            {
                if (fileStream.CanSeek)
                    fileStream.Position = 0;
                return _employeeAndSettingsService.UploadStoreLogoAsync(CurrentEmployee!.EmployeeId, fileName, fileStream, contentType);
            });
    }

    public Task<AdminStoreSettingsDto> RemoveStoreLogoAsync()
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.RemoveStoreLogoAsync(),
            () => _employeeAndSettingsService.RemoveStoreLogoAsync(CurrentEmployee!.EmployeeId));
    }

    public async Task<RedeemActivationCodeResponse> RedeemActivationCodeAsync(RedeemActivationCodeRequest request)
    {
        EnsureAdminPermission();
        var response = await ExecuteWithFallbackAsync(
            () => _apiClient.RedeemActivationCodeAsync(request),
            () => _employeeAndSettingsService.RedeemActivationCodeAsync(request, CurrentEmployee?.EmployeeId));

        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return response;
    }

    public Task<CheckDomainAvailabilityResponse> CheckDomainAvailabilityAsync(string domain, CancellationToken ct = default)
    {
        if (_cloudSyncService is not null)
        {
            return _cloudSyncService.CheckDomainAvailabilityAsync(domain, ct);
        }

        return ExecuteWithFallbackAsync(
            () => _apiClient.CheckDomainAvailabilityAsync(domain, ct),
            () => Task.FromResult(new CheckDomainAvailabilityResponse(
                domain,
                true,
                "Available",
                $"Domínio '{domain}' verificado para publicação no Google Cloud.",
                $"https://{domain.Trim().TrimEnd('/')}",
                [])));
    }

    public Task<FreeDomainSearchResponse> SearchFreeDomainsAsync(string? storeNameOrKeyword, CancellationToken ct = default)
    {
        if (_cloudSyncService is not null)
            return _cloudSyncService.SearchFreeDomainsAsync(storeNameOrKeyword, ct: ct);

        return FreeDomainFinder.SearchAvailableFreeDomainsAsync(storeNameOrKeyword, FallbackDnsHttpClient, ct: ct);
    }

    public Task<(bool Success, string Message)> UpdateDuckDnsAsync(string domainOrSubdomain, string token, string? ipv4 = null, CancellationToken ct = default)
    {
        if (_cloudSyncService is not null)
            return _cloudSyncService.UpdateDuckDnsAsync(domainOrSubdomain, token, ipv4, ct);

        return FreeDomainFinder.UpdateDuckDnsIpAsync(domainOrSubdomain, token, ipv4, FallbackDnsHttpClient, ct);
    }

    public Task<OwnServerHealthCheckResult> TestOwnServerConnectionAsync(
        string? targetUrl,
        CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        EnsureAdminPermission();
        if (_cloudSyncService is not null)
            return _cloudSyncService.TestOwnServerConnectionAsync(targetUrl, ct, progress);

        return Task.FromResult(new OwnServerHealthCheckResult(
            false,
            targetUrl ?? string.Empty,
            null,
            0,
            "Serviço de nuvem indisponível para teste neste ambiente."));
    }

    public Task<GoogleAccountTierVerificationResult> VerifyGoogleAccountByTokenAsync(
        string accessToken,
        string? googleOAuthClientId = null,
        CancellationToken ct = default)
    {
        EnsureAdminPermission();
        if (_cloudSyncService is not null)
            return _cloudSyncService.VerifyGoogleAccountByTokenAsync(accessToken, googleOAuthClientId, ct);

        return _employeeAndSettingsService.VerifyAndSaveGoogleAccountAsync(
            CurrentEmployee?.EmployeeId,
            accessToken,
            googleOAuthClientId,
            FallbackDnsHttpClient,
            ct);
    }

    public Task<GoogleAccountTierVerificationResult> VerifyGoogleAccountViaBrowserOAuthAsync(
        string googleOAuthClientId,
        CancellationToken ct = default)
    {
        EnsureAdminPermission();
        if (_cloudSyncService is not null)
            return _cloudSyncService.VerifyGoogleAccountViaBrowserOAuthAsync(googleOAuthClientId, ct);

        return Task.FromResult(new GoogleAccountTierVerificationResult(
            false, null, null, null, null, null,
            GoogleAccountTierVerifier.TierNone, false, false, "Indisponível",
            "Login via navegador Google só está disponível no app Windows/Mobile. Cole o Access Token Google manualmente.",
            null));
    }

    public Task<AdminStoreSettingsDto> ClearGoogleAccountVerificationAsync(CancellationToken ct = default)
    {
        EnsureAdminPermission();
        if (_cloudSyncService is not null)
            return _cloudSyncService.ClearGoogleAccountVerificationAsync(ct);

        return _employeeAndSettingsService.ClearGoogleAccountVerificationAsync(CurrentEmployee?.EmployeeId, ct);
    }

    public async Task<CepAddressLookupDto> LookupCepAsync(string cep, CancellationToken ct = default)
    {
        if (_orderAndShippingService is null)
            return ClothingStore.Shared.Helpers.BrazilShippingCalculator.ResolveRegionFromCep(cep);

        return await _orderAndShippingService.LookupCepAsync(cep, ct);
    }

    public async Task<CorreiosQuoteDto> SimulateCorreiosAsync(
        string? originCep,
        string destCep,
        int weightGrams = 450,
        int lengthCm = 24,
        int widthCm = 18,
        int heightCm = 8,
        CancellationToken ct = default)
    {
        EnsureAuthenticated();
        return await ExecuteWithFallbackAsync(
            () => _apiClient.SimulateCorreiosAsync(originCep, destCep, weightGrams, lengthCm, widthCm, heightCm, ct),
            () => _orderAndShippingService is not null
                ? _orderAndShippingService.SimulateCorreiosForSellerAsync(originCep, destCep, weightGrams, lengthCm, widthCm, heightCm, ct)
                : throw new DomainException("Serviço de cálculo de Correios indisponível."));
    }

    public Task<IReadOnlyList<StoreOrderDto>> GetOrdersAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetOrdersAsync(ct),
            () => _orderAndShippingService is not null
                ? _orderAndShippingService.GetOrdersAsync(ct)
                : Task.FromResult<IReadOnlyList<StoreOrderDto>>([]));
    }

    public async Task<StoreOrderDto> ConfirmOrderPixAsync(Guid orderId, CancellationToken ct = default)
    {
        EnsureStockPermission();
        var updated = await ExecuteWithFallbackAsync(
            () => _apiClient.ConfirmOrderPixAsync(orderId, ct),
            () => _orderAndShippingService is not null
                ? _orderAndShippingService.ConfirmPixPaymentAsync(orderId, CurrentEmployee!.EmployeeId, ct)
                : throw new DomainException("Serviço de pedidos indisponível."));

        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return updated;
    }

    public async Task<StoreOrderDto> UpdateOrderFulfillmentAsync(Guid orderId, UpdateOrderFulfillmentRequest request, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var updated = await ExecuteWithFallbackAsync(
            () => _apiClient.UpdateOrderFulfillmentAsync(orderId, request, ct),
            () => _orderAndShippingService is not null
                ? _orderAndShippingService.UpdateOrderFulfillmentAsync(orderId, request, ct)
                : throw new DomainException("Serviço de pedidos indisponível."));

        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return updated;
    }

    public async Task<StoreOrderDto> CancelOrderAsync(Guid orderId, string? reason = null, CancellationToken ct = default)
    {
        EnsureStockPermission();
        var updated = await ExecuteWithFallbackAsync(
            () => _apiClient.CancelOrderAsync(orderId, reason, ct),
            () => _orderAndShippingService is not null
                ? _orderAndShippingService.CancelOrderAsync(orderId, CurrentEmployee!.EmployeeId, reason, ct)
                : throw new DomainException("Serviço de pedidos indisponível."));

        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return updated;
    }

    public async Task<SalesReportDto> GetSalesReportAsync(string period = "30d", CancellationToken ct = default)
    {
        EnsureAuthenticated();
        return await ExecuteWithFallbackAsync(
            () => _apiClient.GetSalesReportAsync(period, ct),
            () => _orderAndShippingService is not null
                ? _orderAndShippingService.GetSalesReportAsync(period, ct)
                : throw new DomainException("Serviço de vendas e pedidos indisponível."));
    }

    public async Task<StoreOrderDto> RegisterManualSaleAsync(RegisterManualSaleRequest request, CancellationToken ct = default)
    {
        EnsureStockPermission();
        var created = await ExecuteWithFallbackAsync(
            () => _apiClient.RegisterManualSaleAsync(request, ct),
            () => _orderAndShippingService is not null
                ? _orderAndShippingService.RegisterManualSaleAsync(CurrentEmployee!.EmployeeId, request, ct)
                : throw new DomainException("Serviço de vendas e pedidos indisponível."));

        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return created;
    }

    public async Task<AdminStoreSettingsDto> SetManualSalesEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        EnsureAdminPermission();
        var current = await GetStoreSettingsAsync();
        var request = new UpdateStoreSettingsRequest
        {
            StoreName = current.StoreName,
            StoreSlug = current.StoreSlug,
            WhatsappNumber = current.WhatsappNumber,
            StoreDescription = current.StoreDescription,
            DefaultLowStockThreshold = current.DefaultLowStockThreshold,
            OnlineBaseUrl = current.OnlineBaseUrl,
            AutoSyncOnlineOnSave = current.AutoSyncOnlineOnSave,
            ManualSalesEnabled = enabled
        };

        return await UpdateStoreSettingsAsync(request);
    }

    private sealed record PendingPlanOrderTicket(
        Guid Id,
        string OrderNumber,
        string ClaimToken,
        string PlanTier,
        int DurationMonths,
        int DurationDays,
        decimal Amount,
        string StoreName,
        string StoreSlug,
        string DesiredDomain,
        string PixKeyUsed,
        string PixBrCode,
        DateTimeOffset CreatedAt);

    public PlatformSubscriptionOrderDto? GetPendingPlanOrder()
    {
        var ticket = GetPendingPlanOrderTicket();
        if (ticket is null)
            return null;

        return new PlatformSubscriptionOrderDto(
            ticket.Id,
            ticket.OrderNumber,
            ticket.ClaimToken,
            ticket.PlanTier,
            ticket.DurationMonths,
            ticket.DurationDays,
            ticket.Amount,
            ticket.StoreName,
            ticket.StoreSlug,
            ticket.DesiredDomain,
            false,
            CurrentEmployee?.UserId,
            CurrentEmployee?.Username ?? string.Empty,
            CurrentEmployee?.Email ?? string.Empty,
            CurrentEmployee?.FullName ?? string.Empty,
            ticket.PixKeyUsed,
            ticket.PixBrCode,
            "Pendente",
            null,
            null,
            ticket.CreatedAt,
            null,
            null,
            "Pedido aguardando confirmação do Pix pelo administrador.");
    }

    public void ClearPendingPlanOrder()
    {
        try
        {
            Preferences.Default.Remove(PendingPlanOrderPreferenceKey);
        }
        catch
        {
        }
    }

    private PendingPlanOrderTicket? GetPendingPlanOrderTicket()
    {
        try
        {
            var json = Preferences.Default.Get(PendingPlanOrderPreferenceKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            return JsonSerializer.Deserialize<PendingPlanOrderTicket>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void SavePendingPlanOrderTicket(PlatformSubscriptionOrderDto order)
    {
        try
        {
            var ticket = new PendingPlanOrderTicket(
                order.Id,
                order.OrderNumber,
                order.ClaimToken,
                order.PlanTier,
                order.DurationMonths,
                order.DurationDays,
                order.Amount,
                order.StoreName,
                order.StoreSlug,
                order.DesiredDomain,
                order.PixKeyUsed,
                order.PixBrCode,
                order.CreatedAt);
            Preferences.Default.Set(PendingPlanOrderPreferenceKey, JsonSerializer.Serialize(ticket));
        }
        catch
        {
        }
    }

    public async Task<PlatformSubscriptionOrderDto> CreatePlatformPlanPurchaseOrderAsync(
        CreatePlatformPlanOrderRequest request,
        CancellationToken ct = default)
    {
        EnsureAdminPermission();

        PlatformSubscriptionOrderDto created;
        if (HasCustomServerUrl || ShouldTryRemoteApi)
        {
            try
            {
                created = await _apiClient.CreatePlatformPlanOrderAsync(request, ct);
                SavePendingPlanOrderTicket(created);
                return created;
            }
            catch (Exception ex) when (IsConnectionOrTimeoutError(ex))
            {
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            }
        }

        if (_subscriptionService is null)
            throw new DomainException("Serviço de pedidos de plano indisponível.");

        created = await _subscriptionService.CreatePlanOrderAsync(request, ct);
        SavePendingPlanOrderTicket(created);
        return created;
    }

    public async Task<PlatformPlanOrderStatusDto?> CheckAndApplyPendingPlanOrderAsync(
        string? orderNumber = null,
        string? claimToken = null,
        CancellationToken ct = default)
    {
        var ticket = GetPendingPlanOrderTicket();
        var effectiveOrderNumber = string.IsNullOrWhiteSpace(orderNumber) ? ticket?.OrderNumber : orderNumber.Trim();
        var effectiveClaimToken = string.IsNullOrWhiteSpace(claimToken) ? ticket?.ClaimToken : claimToken.Trim();

        if (string.IsNullOrWhiteSpace(effectiveOrderNumber) || string.IsNullOrWhiteSpace(effectiveClaimToken))
            return null;

        PlatformPlanOrderStatusDto? status = null;
        if (HasCustomServerUrl || ShouldTryRemoteApi)
        {
            try
            {
                status = await _apiClient.GetPlatformPlanOrderStatusAsync(effectiveOrderNumber, effectiveClaimToken, ct);
            }
            catch (Exception ex) when (IsConnectionOrTimeoutError(ex))
            {
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            }
        }

        if (status is null && _subscriptionService is not null)
        {
            status = await _subscriptionService.GetPlanOrderStatusAsync(effectiveOrderNumber, effectiveClaimToken, ct);
        }

        if (status is null)
            return null;

        if (status.IsApproved)
        {
            var activated = await _employeeAndSettingsService.ApplyApprovedRemotePlanAsync(
                status,
                CurrentEmployee?.EmployeeId,
                ct);

            ClearPendingPlanOrder();
            _cloudSyncService?.TriggerAutoSyncIfEnabled();
            PlanEntitlementActivated?.Invoke(activated);
            AuthStateChanged?.Invoke();
        }
        else if (string.Equals(status.Status, "Cancelado", StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingPlanOrder();
        }

        return status;
    }

    public async Task<PlatformPlanOrderStatusDto?> TryAutoDeliverPendingPlanAsync(CancellationToken ct = default)
    {
        try
        {
            return await CheckAndApplyPendingPlanOrderAsync(ct: ct);
        }
        catch
        {
            return null;
        }
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new DomainException("Sessão não autenticada. Faça login para continuar.");
    }

    private void EnsureProductPermission()
    {
        EnsureAuthenticated();
        if (!CanManageProducts)
            throw new DomainException("Seu perfil não possui permissão para cadastrar ou alterar produtos.");
    }

    private void EnsureStockPermission()
    {
        EnsureAuthenticated();
        if (!CanManageStock)
            throw new DomainException("Seu perfil não possui permissão para movimentar o estoque.");
    }

    private void EnsureAdminPermission()
    {
        EnsureAuthenticated();
        if (!IsAdmin)
            throw new DomainException("Acesso restrito ao perfil Administrador / Proprietário.");
    }
}
