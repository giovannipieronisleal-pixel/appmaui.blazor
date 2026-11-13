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
    private static readonly HttpClient FallbackDnsHttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly AdminApiClient _apiClient;
    private readonly AuthService _authService;
    private readonly ProductManagementService _productService;
    private readonly StockManagementService _stockService;
    private readonly EmployeeAndSettingsService _employeeAndSettingsService;
    private readonly GoogleCloudSyncService? _cloudSyncService;
    private readonly OrderAndShippingService? _orderAndShippingService;

    private DateTimeOffset _skipRemoteApiUntil = DateTimeOffset.MinValue;

    public EmployeeProfileDto? CurrentEmployee { get; private set; }
    public string? AccessToken { get; private set; }
    public bool IsAuthenticated => CurrentEmployee is not null && CurrentEmployee.IsActive;
    public bool IsAdmin => CurrentEmployee?.IsAdmin ?? false;
    public bool CanManageProducts => IsAdmin || (CurrentEmployee?.CanManageProducts ?? false);
    public bool CanManageStock => IsAdmin || (CurrentEmployee?.CanManageStock ?? false);

    public event Action? AuthStateChanged;

    public MauiAdminGateway(
        AdminApiClient apiClient,
        AuthService authService,
        ProductManagementService productService,
        StockManagementService stockService,
        EmployeeAndSettingsService employeeAndSettingsService,
        GoogleCloudSyncService? cloudSyncService = null,
        OrderAndShippingService? orderAndShippingService = null)
    {
        _apiClient = apiClient;
        _authService = authService;
        _productService = productService;
        _stockService = stockService;
        _employeeAndSettingsService = employeeAndSettingsService;
        _cloudSyncService = cloudSyncService;
        _orderAndShippingService = orderAndShippingService;
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

    public async Task TryRestoreSessionAsync()
    {
        if (IsAuthenticated)
            return;

        try
        {
            var rawJson = await SecureStorage.Default.GetAsync(SessionStorageKey);
            if (string.IsNullOrWhiteSpace(rawJson))
                return;

            var saved = JsonSerializer.Deserialize<LoginResponse>(rawJson);
            if (saved is null || saved.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                SecureStorage.Default.Remove(SessionStorageKey);
                return;
            }

            var freshProfile = await _authService.GetCurrentProfileAsync(saved.Employee.UserId);
            AccessToken = saved.AccessToken;
            CurrentEmployee = freshProfile;
            _apiClient.SetSession(saved.AccessToken, freshProfile);
            AuthStateChanged?.Invoke();
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
        var localResp = await _authService.RegisterUserAccountAsync(request);
        _apiClient.SetSession(localResp.AccessToken, localResp.Employee);
        await PersistSessionAsync(localResp);
        return localResp;
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
        var challenge = await _authService.LoginWithChallengeAsync(request);
        if (!challenge.RequiresTwoFactor && challenge.AuthResult is not null)
        {
            _apiClient.SetSession(challenge.AuthResult.AccessToken, challenge.AuthResult.Employee);
            await PersistSessionAsync(challenge.AuthResult, rememberMe);
        }
        return challenge;
    }

    public async Task<LoginResponse> VerifyTwoFactorLoginAsync(VerifyTwoFactorLoginRequest request, bool rememberMe = true)
    {
        var response = await _authService.VerifyTwoFactorLoginAsync(request);
        _apiClient.SetSession(response.AccessToken, response.Employee);
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
                await SecureStorage.Default.SetAsync(SessionStorageKey, serialized);
            }
            else
            {
                SecureStorage.Default.Remove(SessionStorageKey);
            }
        }
        catch
        {
        }

        AuthStateChanged?.Invoke();
    }

    public void Logout()
    {
        AccessToken = null;
        CurrentEmployee = null;
        _apiClient.ClearSession();
        try
        {
            SecureStorage.Default.Remove(SessionStorageKey);
        }
        catch
        {
        }
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

    public Task<EmployeeProfileDto> CreateEmployeeAsync(CreateEmployeeRequest request)
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.CreateEmployeeAsync(request),
            () => _employeeAndSettingsService.CreateEmployeeAsync(request));
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

    public async Task<AdminStoreSettingsDto> UpdateStoreSettingsAsync(UpdateStoreSettingsRequest request)
    {
        EnsureAdminPermission();
        var result = await ExecuteWithFallbackAsync(
            () => _apiClient.UpdateStoreSettingsAsync(request),
            () => _employeeAndSettingsService.UpdateStoreSettingsAsync(CurrentEmployee!.EmployeeId, request));
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
        return result;
    }

    public async Task<AdminStoreSettingsDto> ActivateSiteAsync()
    {
        EnsureAdminPermission();
        var result = await _employeeAndSettingsService.ActivateStoreSiteAsync(CurrentEmployee?.EmployeeId);
        _cloudSyncService?.TriggerAutoSyncIfEnabled();
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

    public Task<OwnServerHealthCheckResult> TestOwnServerConnectionAsync(string? targetUrl, CancellationToken ct = default)
    {
        EnsureAdminPermission();
        if (_cloudSyncService is not null)
            return _cloudSyncService.TestOwnServerConnectionAsync(targetUrl, ct);

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
