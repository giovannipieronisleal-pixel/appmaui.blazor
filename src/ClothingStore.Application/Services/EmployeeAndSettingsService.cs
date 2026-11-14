using System.Security.Cryptography;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Serviço administrativo exclusivo para Proprietário/Admin:
/// gestão de funcionários, permissões e seção "Configurações da loja" (Nome, WhatsApp, Descrição e Logotipo).
/// </summary>
public sealed class EmployeeAndSettingsService
{
    private readonly IClothingStoreRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IImageStorageService _imageStorage;
    private readonly IEmailService? _emailService;

    public EmployeeAndSettingsService(
        IClothingStoreRepository repository,
        IPasswordHasher passwordHasher,
        IImageStorageService imageStorage,
        IEmailService? emailService = null)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _imageStorage = imageStorage;
        _emailService = emailService;
    }

    public async Task<IReadOnlyList<EmployeeProfileDto>> GetEmployeesAsync(CancellationToken ct = default)
    {
        var employees = await _repository.GetEmployeesAsync(ct);
        var result = new List<EmployeeProfileDto>(employees.Count);

        foreach (var emp in employees)
        {
            var user = emp.User ?? await _repository.GetUserByIdAsync(emp.UserId, ct);
            if (user is not null)
            {
                var roleName = user.Role?.Name ?? RoleNames.Employee;
                result.Add(AuthService.MapToProfileDto(emp, user, roleName));
            }
        }

        return result;
    }

    public async Task<EmployeeProfileDto> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken ct = default)
    {
        var hasLegacyPassword = !string.IsNullOrWhiteSpace(request.InitialPassword);
        if (hasLegacyPassword)
        {
            AuthService.ValidatePasswordStrength(request.InitialPassword);
        }
        else if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@', StringComparison.Ordinal))
        {
            throw new DomainException("Informe o Gmail/e-mail do funcionário para que apenas ele crie a própria senha por e-mail.");
        }

        var roleName = string.Equals(request.RoleName, RoleNames.Admin, StringComparison.OrdinalIgnoreCase)
            ? RoleNames.Admin
            : RoleNames.Employee;

        var role = await _repository.GetRoleByNameAsync(roleName, ct)
            ?? throw new DomainException($"Perfil '{roleName}' não encontrado.");

        var explicitEmail = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
        var explicitUsername = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim().ToLowerInvariant();

        // Mantém compatibilidade com cadastro sem e-mail apenas quando chamado por fluxo legado com senha inicial
        var loginIdentifier = explicitEmail
            ?? explicitUsername
            ?? Category.GenerateSlug(request.FullName).Replace('-', '.');

        if (string.IsNullOrWhiteSpace(loginIdentifier))
        {
            loginIdentifier = $"func.{Guid.NewGuid().ToString("N")[..6]}";
        }

        if (explicitEmail is null && explicitUsername is null)
        {
            var baseLogin = loginIdentifier;
            int suffix = 2;
            while (await _repository.GetUserByEmailAsync(loginIdentifier, ct) is not null)
            {
                loginIdentifier = $"{baseLogin}{suffix}";
                suffix++;
            }
        }
        else
        {
            if (explicitEmail is not null && await _repository.GetUserByEmailAsync(explicitEmail, ct) is not null)
            {
                throw new DomainException("Este Gmail/e-mail já está cadastrado para outro usuário.");
            }

            if (explicitUsername is not null && await _repository.GetUserByEmailAsync(explicitUsername, ct) is not null)
            {
                throw new DomainException("Este nome de usuário já está cadastrado no sistema.");
            }
        }

        var effectiveUsername = explicitUsername ?? User.DeriveDefaultUsername(loginIdentifier);
        var secretToHash = hasLegacyPassword
            ? request.InitialPassword!
            : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var passwordHash = _passwordHasher.HashPassword(secretToHash);
        var user = new User(loginIdentifier, passwordHash, role.Id, effectiveUsername)
        {
            Role = role
        };

        string? firstAccessSetupCode = null;
        if (!hasLegacyPassword)
        {
            firstAccessSetupCode = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
            var tokenHash = _passwordHasher.HashToken(firstAccessSetupCode);
            user.GeneratePasswordResetToken(tokenHash, TimeSpan.FromHours(24));
        }

        var employee = new Employee(
            user.Id,
            request.FullName,
            request.JobTitle,
            request.Phone,
            canManageProducts: roleName == RoleNames.Admin || request.CanManageProducts,
            canManageStock: roleName == RoleNames.Admin || request.CanManageStock)
        {
            User = user
        };

        await _repository.AddEmployeeWithUserAsync(user, employee, ct);

        if (firstAccessSetupCode is not null && _emailService is not null)
        {
            await _emailService.SendSecurityCodeEmailAsync(
                user.Email,
                "Código para você definir sua senha de acesso",
                firstAccessSetupCode,
                validityMinutes: 1440,
                ct);
        }

        return AuthService.MapToProfileDto(employee, user, role.Name);
    }

    public async Task<EmployeeProfileDto> UpdateEmployeeAsync(
        Guid employeeId,
        UpdateEmployeeRequest request,
        CancellationToken ct = default)
    {
        var employee = await _repository.GetEmployeeByIdAsync(employeeId, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        var user = employee.User ?? await _repository.GetUserByIdAsync(employee.UserId, ct)
            ?? throw new DomainException("Conta de usuário do funcionário não encontrada.");

        var roleName = string.Equals(request.RoleName, RoleNames.Admin, StringComparison.OrdinalIgnoreCase)
            ? RoleNames.Admin
            : RoleNames.Employee;

        var role = await _repository.GetRoleByNameAsync(roleName, ct)
            ?? throw new DomainException($"Perfil '{roleName}' não encontrado.");

        user.RoleId = role.Id;
        user.Role = role;

        employee.FullName = request.FullName.Trim();
        employee.JobTitle = request.JobTitle?.Trim();
        employee.Phone = request.Phone?.Trim();
        employee.CanManageProducts = roleName == RoleNames.Admin || request.CanManageProducts;
        employee.CanManageStock = roleName == RoleNames.Admin || request.CanManageStock;
        employee.SetActiveStatus(request.IsActive);

        await _repository.UpdateEmployeeAsync(employee, user, ct);
        return AuthService.MapToProfileDto(employee, user, role.Name);
    }

    public async Task<EmployeeProfileDto> SetEmployeeActiveStatusAsync(
        Guid employeeId,
        bool isActive,
        CancellationToken ct = default)
    {
        var employee = await _repository.GetEmployeeByIdAsync(employeeId, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        var user = employee.User ?? await _repository.GetUserByIdAsync(employee.UserId, ct)
            ?? throw new DomainException("Conta de usuário não encontrada.");

        employee.SetActiveStatus(isActive);
        await _repository.UpdateEmployeeAsync(employee, user, ct);

        var roleName = user.Role?.Name ?? RoleNames.Employee;
        return AuthService.MapToProfileDto(employee, user, roleName);
    }

    // =========================================================================
    // CONFIGURAÇÕES PERSONALIZADAS DA LOJA (Nome, WhatsApp, Descrição, Logotipo)
    // =========================================================================
    public async Task<AdminStoreSettingsDto> GetAdminStoreSettingsAsync(CancellationToken ct = default)
    {
        var s = await _repository.GetStoreSettingsAsync(ct);
        return MapToAdminSettingsDto(s);
    }

    public async Task<AdminStoreSettingsDto> UpdateStoreSettingsAsync(
        Guid? employeeId,
        UpdateStoreSettingsRequest request,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.UpdateSettings(
            request.StoreName,
            request.WhatsappNumber,
            request.StoreDescription,
            request.DefaultLowStockThreshold,
            employeeId,
            request.StoreSlug,
            request.OnlineBaseUrl ?? settings.OnlineBaseUrl,
            request.AutoSyncOnlineOnSave,
            request.LastOnlineSyncAt);

        if (request.LogoPublicUrl is not null)
        {
            var trimmedLogo = request.LogoPublicUrl.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLogo))
            {
                settings.SetLogo(null, null, employeeId);
            }
            else if (!string.Equals(trimmedLogo, settings.LogoPublicUrl, StringComparison.Ordinal))
            {
                settings.SetLogo(trimmedLogo, trimmedLogo, employeeId);
            }
        }

        if (request.PlanTier is not null ||
            request.BasicPlanMonthlyPrice.HasValue ||
            request.IntermediaryPlanMonthlyPrice.HasValue ||
            request.PremiumPlanMonthlyPrice.HasValue ||
            request.PicpayPixKey is not null ||
            request.Design is not null)
        {
            settings.UpdatePlanAndDesign(
                request.PlanTier,
                request.BasicPlanMonthlyPrice,
                request.PremiumPlanMonthlyPrice,
                request.PicpayPixKey,
                request.Design is not null ? StoreDesignMapper.ToDomain(request.Design) : null,
                employeeId,
                request.IntermediaryPlanMonthlyPrice);
        }

        if (request.DirectPixSalesEnabled.HasValue ||
            request.PixGatewayMode is not null ||
            request.StorePixKey is not null ||
            request.StorePixReceiverName is not null ||
            request.OriginCep is not null ||
            request.OriginCity is not null ||
            request.OriginState is not null ||
            request.DeliveryScopeMode is not null ||
            request.MaxDeliveryDistanceKm.HasValue ||
            request.DefaultPackageWeightGrams.HasValue)
        {
            var effectiveEnabled = request.DirectPixSalesEnabled ?? settings.DirectPixSalesEnabled;
            var effectiveGatewayMode = request.PixGatewayMode is not null
                ? StoreSettings.NormalizePixGatewayMode(request.PixGatewayMode)
                : settings.PixGatewayMode;
            var usingAutomaticMercadoPago =
                string.Equals(effectiveGatewayMode, StoreSettings.PixGatewayMercadoPago, StringComparison.OrdinalIgnoreCase) &&
                settings.HasMercadoPagoConnected;

            var rawKey = request.StorePixKey ?? settings.StorePixKey;
            var normalizedKey = string.IsNullOrWhiteSpace(rawKey)
                ? null
                : ClothingStore.Shared.Helpers.PixPayloadBuilder.NormalizePixKey(rawKey);

            if (effectiveEnabled &&
                !usingAutomaticMercadoPago &&
                !ClothingStore.Shared.Helpers.PixPayloadBuilder.IsValidPixKey(normalizedKey))
            {
                throw new DomainException(
                    "Para ativar as vendas por Pix no app, informe uma Chave Pix válida (CPF, E-mail, Celular ou Chave Aleatória) ou conecte o Mercado Pago.");
            }

            var effOriginCep = request.OriginCep ?? settings.OriginCep;
            var effOriginCity = request.OriginCity ?? settings.OriginCity;
            var effOriginState = request.OriginState ?? settings.OriginState;

            if (!string.IsNullOrWhiteSpace(effOriginCep))
            {
                if (!ClothingStore.Shared.Helpers.BrazilShippingCalculator.TrySanitizeCep(effOriginCep, out var digits))
                    throw new DomainException("Informe um CEP de origem válido com 8 dígitos (ex.: 01310-100).");

                effOriginCep = ClothingStore.Shared.Helpers.BrazilShippingCalculator.FormatCep(digits);
                var region = ClothingStore.Shared.Helpers.BrazilShippingCalculator.ResolveRegionFromCep(digits);
                if (string.IsNullOrWhiteSpace(effOriginCity))
                    effOriginCity = region.City;
                if (string.IsNullOrWhiteSpace(effOriginState))
                    effOriginState = region.State;
            }

            settings.ConfigurePixAndShipping(
                effectiveEnabled,
                normalizedKey,
                request.StorePixReceiverName ?? settings.StorePixReceiverName,
                effOriginCep,
                effOriginCity,
                effOriginState,
                request.DeliveryScopeMode ?? settings.DeliveryScopeMode,
                request.MaxDeliveryDistanceKm ?? settings.MaxDeliveryDistanceKm,
                request.DefaultPackageWeightGrams ?? settings.DefaultPackageWeightGrams,
                employeeId,
                pixGatewayMode: effectiveGatewayMode);
        }

        if (request.CentralCatalogBaseUrl is not null)
        {
            settings.ConfigureCentralPublishing(
                request.CentralCatalogBaseUrl,
                preserveExistingToken: true,
                employeeId: employeeId);
        }

        if (request.ServerHostingMode is not null ||
            request.OwnServerProvider is not null ||
            request.OwnServerSyncSecret is not null ||
            request.ClearOwnServerSyncSecret ||
            request.GoogleOAuthClientId is not null)
        {
            settings.ConfigureServerHosting(
                request.ServerHostingMode ?? settings.ServerHostingMode,
                request.OwnServerProvider ?? settings.OwnServerProvider,
                request.OwnServerSyncSecret,
                request.ClearOwnServerSyncSecret,
                request.GoogleOAuthClientId,
                employeeId);
        }

        if (request.DomainAcquisitionMode is not null || request.OnlineBaseUrl is not null)
        {
            settings.ConfigureDomainChoice(
                request.DomainAcquisitionMode,
                request.OnlineBaseUrl,
                employeeId);
        }

        if (request.ManualSalesEnabled.HasValue)
        {
            settings.SetManualSalesEnabled(request.ManualSalesEnabled.Value, employeeId);
        }

        await _repository.UpdateStoreSettingsAsync(settings, ct);
        return MapToAdminSettingsDto(settings);
    }

    public async Task<GoogleAccountTierVerificationResult> VerifyAndSaveGoogleAccountAsync(
        Guid? employeeId,
        string accessToken,
        string? googleOAuthClientId,
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        var result = await ClothingStore.Shared.Helpers.GoogleAccountTierVerifier
            .VerifyWithAccessTokenAsync(accessToken, httpClient, ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Email))
        {
            return result;
        }

        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.SetGoogleAccountVerification(
            result.Email,
            result.DetectedTier,
            result.DriveQuotaBytes,
            result.HostedDomain,
            result.VerifiedAt ?? DateTimeOffset.UtcNow,
            googleOAuthClientId,
            employeeId);

        await _repository.UpdateStoreSettingsAsync(settings, ct);
        return result;
    }

    public async Task<AdminStoreSettingsDto> SaveGoogleAccountVerificationResultAsync(
        Guid? employeeId,
        GoogleAccountTierVerificationResult verification,
        string? googleOAuthClientId = null,
        CancellationToken ct = default)
    {
        if (!verification.Success || string.IsNullOrWhiteSpace(verification.Email))
            throw new DomainException(verification.Message);

        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.SetGoogleAccountVerification(
            verification.Email,
            verification.DetectedTier,
            verification.DriveQuotaBytes,
            verification.HostedDomain,
            verification.VerifiedAt ?? DateTimeOffset.UtcNow,
            googleOAuthClientId,
            employeeId);

        await _repository.UpdateStoreSettingsAsync(settings, ct);
        return MapToAdminSettingsDto(settings);
    }

    public async Task<AdminStoreSettingsDto> ClearGoogleAccountVerificationAsync(
        Guid? employeeId,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.ClearGoogleAccountVerification(employeeId);
        await _repository.UpdateStoreSettingsAsync(settings, ct);
        return MapToAdminSettingsDto(settings);
    }

    public async Task<AdminStoreSettingsDto> UploadStoreLogoAsync(
        Guid? employeeId,
        string fileName,
        Stream contentStream,
        string contentType,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var previousPath = settings.LogoStoragePath;

        var (storagePath, publicUrl) = await _imageStorage.UploadImageAsync(
            "store-logo",
            fileName,
            contentStream,
            contentType,
            ct);

        settings.SetLogo(storagePath, publicUrl, employeeId);
        await _repository.UpdateStoreSettingsAsync(settings, ct);

        if (!string.IsNullOrWhiteSpace(previousPath))
        {
            await _imageStorage.DeleteImageAsync(previousPath, ct);
        }

        return MapToAdminSettingsDto(settings);
    }

    public async Task<AdminStoreSettingsDto> RemoveStoreLogoAsync(Guid? employeeId, CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var previousPath = settings.LogoStoragePath;

        settings.SetLogo(null, null, employeeId);
        await _repository.UpdateStoreSettingsAsync(settings, ct);

        if (!string.IsNullOrWhiteSpace(previousPath))
        {
            await _imageStorage.DeleteImageAsync(previousPath, ct);
        }

        return MapToAdminSettingsDto(settings);
    }

    public async Task<AdminStoreSettingsDto> ActivateStoreSiteAsync(Guid? employeeId, CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.ActivateSite(employeeId);
        await _repository.UpdateStoreSettingsAsync(settings, ct);
        return MapToAdminSettingsDto(settings);
    }

    public async Task<AdminStoreSettingsDto> DeactivateStoreSiteAsync(Guid? employeeId, string reason, CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.DeactivateSite(reason, employeeId);
        await _repository.UpdateStoreSettingsAsync(settings, ct);
        return MapToAdminSettingsDto(settings);
    }

    public async Task<RedeemActivationCodeResponse> RedeemActivationCodeAsync(
        RedeemActivationCodeRequest request,
        Guid? employeeId = null,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Code))
            throw new DomainException("Digite o código do Gift Card para resgatar seu plano.");

        var normalizedCode = request.Code.Trim().ToUpperInvariant();
        var code = await _repository.GetPlanActivationCodeByCodeAsync(normalizedCode, ct)
            ?? throw new DomainException($"Código Gift Card '{normalizedCode}' não encontrado.");

        var settings = await _repository.GetStoreSettingsAsync(ct);

        if (!string.IsNullOrWhiteSpace(code.TargetStoreName) &&
            !string.Equals(code.TargetStoreName, settings.StoreName, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"Este Gift Card foi emitido exclusivamente para a loja '{code.TargetStoreName}'.");
        }

        settings.RedeemActivationCode(code, employeeId);
        await _repository.UpdateStoreSettingsAsync(settings, ct);
        await _repository.SavePlanActivationCodeAsync(code, ct);

        var message = BuildGiftCardActivationSuccessMessage(code.Code, settings, code.IsGiftedUnpaid);
        var dto = MapToAdminSettingsDto(settings);
        return new RedeemActivationCodeResponse(
            code.Code,
            settings.PlanTier,
            code.DurationDays,
            settings.PlanExpiresAt,
            message,
            dto);
    }

    /// <summary>
    /// Valida e marca um Gift Card como resgatado pela loja destinatária sem sobrescrever
    /// as configurações locais da loja do administrador na central.
    /// </summary>
    public async Task<PlatformGiftCardRedeemResponse> RedeemGiftCardForRemoteStoreAsync(
        PlatformGiftCardRedeemRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Code))
            throw new DomainException("Digite o código do Gift Card para resgatar seu plano.");

        var normalizedCode = request.Code.Trim().ToUpperInvariant();
        var code = await _repository.GetPlanActivationCodeByCodeAsync(normalizedCode, ct)
            ?? throw new DomainException($"Código Gift Card '{normalizedCode}' não encontrado.");

        if (code.IsRevoked)
            throw new DomainException("Este código Gift Card está pausado/revogado pelo administrador.");

        if (code.IsRedeemed)
        {
            var usedBy = string.IsNullOrWhiteSpace(code.RedeemedByStoreName)
                ? string.Empty
                : $" por '{code.RedeemedByStoreName}'";
            throw new DomainException($"Este código Gift Card já foi resgatado{usedBy}.");
        }

        var now = DateTimeOffset.UtcNow;
        if (code.CodeExpiresAt.HasValue && now >= code.CodeExpiresAt.Value)
            throw new DomainException("Este código Gift Card expirou antes do resgate.");

        var redeemingStoreName = string.IsNullOrWhiteSpace(request.StoreName)
            ? "Loja Parceira"
            : request.StoreName.Trim();

        if (!string.IsNullOrWhiteSpace(code.TargetStoreName) &&
            !string.Equals(code.TargetStoreName, redeemingStoreName, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"Este Gift Card foi emitido exclusivamente para a loja '{code.TargetStoreName}'.");
        }

        DateTimeOffset? appliedExpiry = code.DurationDays.HasValue
            ? now.AddDays(code.DurationDays.Value)
            : null;

        code.MarkRedeemed(redeemingStoreName, request.EmployeeId, appliedExpiry, now);
        await _repository.SavePlanActivationCodeAsync(code, ct);

        return new PlatformGiftCardRedeemResponse(
            code.Code,
            StoreSettings.NormalizePlanTier(code.TargetPlanTier),
            code.DurationDays,
            appliedExpiry,
            code.IsGiftedUnpaid,
            code.SuggestedDomainName,
            redeemingStoreName,
            $"Gift Card {code.Code} validado para a loja '{redeemingStoreName}'.");
    }

    /// <summary>
    /// Aplica localmente no app da loja destinatária um Gift Card validado remotamente pela central do Admin.
    /// </summary>
    public async Task<RedeemActivationCodeResponse> ApplyRedeemedRemoteGiftCardAsync(
        PlatformGiftCardRedeemResponse grant,
        Guid? employeeId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var normalizedCode = grant.Code.Trim().ToUpperInvariant();
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var normalizedTier = StoreSettings.NormalizePlanTier(grant.TargetPlanTier);

        if (!string.Equals(settings.ActiveGiftCardCode, normalizedCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settings.PlanTier, normalizedTier, StringComparison.OrdinalIgnoreCase) ||
            !settings.IsLicenseActive())
        {
            var current = DateTimeOffset.UtcNow;
            var isSameActiveTier = string.Equals(settings.PlanTier, normalizedTier, StringComparison.OrdinalIgnoreCase)
                && settings.PlanExpiresAt.HasValue
                && settings.PlanExpiresAt.Value > current;

            DateTimeOffset? nextExpiry;
            if (!grant.DurationDays.HasValue)
            {
                nextExpiry = null;
            }
            else if (isSameActiveTier)
            {
                nextExpiry = settings.PlanExpiresAt!.Value.AddDays(grant.DurationDays.Value);
            }
            else
            {
                nextExpiry = grant.PlanExpiresAt ?? current.AddDays(grant.DurationDays.Value);
            }

            settings.UpdatePlanAndDesign(normalizedTier);
            settings.PlanActivatedAt = current;
            settings.PlanExpiresAt = nextExpiry;
            settings.ActiveGiftCardCode = normalizedCode;
            settings.ActivePlanBillingKind = grant.IsGiftedUnpaid
                ? PlanActivationCode.BillingKindGiftedUnpaid
                : PlanActivationCode.BillingKindPaid;

            if (grant.IsGiftedUnpaid)
            {
                settings.ServerHostingMode = "PlatformShared";
                settings.OwnServerSyncSecret = null;
            }

            if (!string.IsNullOrWhiteSpace(grant.SuggestedDomainName))
            {
                var suggested = grant.SuggestedDomainName.Trim();
                var normalizedSuggestedUrl = suggested.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                             suggested.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? suggested.TrimEnd('/')
                    : $"https://{suggested.TrimEnd('/')}";

                settings.OnlineBaseUrl = normalizedSuggestedUrl;
                settings.DomainAcquisitionMode = StoreSettings.InferDomainAcquisitionMode(normalizedSuggestedUrl);
            }

            settings.ActivateSite(employeeId);
            await _repository.UpdateStoreSettingsAsync(settings, ct);
        }

        var localRecord = await _repository.GetPlanActivationCodeByCodeAsync(normalizedCode, ct);
        if (localRecord is null)
        {
            var mirror = PlanActivationCode.Create(
                normalizedTier,
                grant.DurationDays,
                customCode: normalizedCode,
                targetStoreName: null,
                notes: $"Gift Card ativado via central para {settings.StoreName}",
                createdByEmployeeId: employeeId,
                isGiftedUnpaid: grant.IsGiftedUnpaid,
                suggestedDomainName: grant.SuggestedDomainName);
            mirror.MarkRedeemed(settings.StoreName, employeeId, settings.PlanExpiresAt);
            await _repository.SavePlanActivationCodeAsync(mirror, ct);
        }

        var message = BuildGiftCardActivationSuccessMessage(normalizedCode, settings, grant.IsGiftedUnpaid);
        var dto = MapToAdminSettingsDto(settings);
        return new RedeemActivationCodeResponse(
            normalizedCode,
            settings.PlanTier,
            grant.DurationDays,
            settings.PlanExpiresAt,
            message,
            dto);
    }

    public async Task<IReadOnlyList<PlatformGiftCardSyncDto>> GetActivationCodesForSyncAsync(CancellationToken ct = default)
    {
        var codes = await _repository.GetPlanActivationCodesAsync(ct);
        return codes.Select(MapToSyncDto).ToList();
    }

    public async Task<IReadOnlyList<PlatformGiftCardSyncDto>> SyncPlatformGiftCardsAsync(
        IReadOnlyList<PlatformGiftCardSyncDto>? incoming,
        CancellationToken ct = default)
    {
        if (incoming is not null)
        {
            foreach (var dto in incoming)
            {
                if (string.IsNullOrWhiteSpace(dto.Code))
                    continue;

                var normalizedCode = dto.Code.Trim().ToUpperInvariant();
                var existing = await _repository.GetPlanActivationCodeByCodeAsync(normalizedCode, ct);
                if (existing is null)
                {
                    var created = new PlanActivationCode
                    {
                        Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
                        Code = normalizedCode,
                        TargetPlanTier = StoreSettings.NormalizePlanTier(dto.TargetPlanTier),
                        DurationDays = dto.DurationDays,
                        TargetStoreName = string.IsNullOrWhiteSpace(dto.TargetStoreName) ? null : dto.TargetStoreName.Trim(),
                        Notes = dto.Notes,
                        PlanBillingKind = string.Equals(dto.PlanBillingKind, PlanActivationCode.BillingKindGiftedUnpaid, StringComparison.OrdinalIgnoreCase)
                            ? PlanActivationCode.BillingKindGiftedUnpaid
                            : PlanActivationCode.BillingKindPaid,
                        SuggestedDomainName = dto.SuggestedDomainName,
                        CreatedAt = dto.CreatedAt == default ? DateTimeOffset.UtcNow : dto.CreatedAt,
                        CodeExpiresAt = dto.CodeExpiresAt,
                        IsRevoked = dto.IsRevoked,
                        RedeemedAt = dto.RedeemedAt,
                        RedeemedByStoreName = dto.RedeemedByStoreName,
                        RedeemedByEmployeeId = dto.RedeemedByEmployeeId,
                        AppliedPlanExpiresAt = dto.AppliedPlanExpiresAt,
                        StatusUpdatedAt = dto.StatusUpdatedAt
                    };
                    await _repository.SavePlanActivationCodeAsync(created, ct);
                    continue;
                }

                var changed = false;
                if (dto.RedeemedAt.HasValue && !existing.IsRedeemed)
                {
                    existing.IsRevoked = false;
                    existing.MarkRedeemed(
                        dto.RedeemedByStoreName ?? "Loja",
                        dto.RedeemedByEmployeeId,
                        dto.AppliedPlanExpiresAt,
                        dto.RedeemedAt);
                    changed = true;
                }
                else if (!existing.IsRedeemed && !dto.RedeemedAt.HasValue)
                {
                    var incomingUpdated = dto.StatusUpdatedAt ?? dto.CreatedAt;
                    var existingUpdated = existing.StatusUpdatedAt ?? existing.CreatedAt;
                    if (incomingUpdated > existingUpdated && existing.IsRevoked != dto.IsRevoked)
                    {
                        existing.IsRevoked = dto.IsRevoked;
                        existing.StatusUpdatedAt = incomingUpdated;
                        changed = true;
                    }
                }

                if (changed)
                {
                    await _repository.SavePlanActivationCodeAsync(existing, ct);
                }
            }
        }

        var all = await _repository.GetPlanActivationCodesAsync(ct);
        return all.Select(MapToSyncDto).ToList();
    }

    private static string BuildGiftCardActivationSuccessMessage(string code, StoreSettings settings, bool isGiftedUnpaid)
    {
        var expiryMessage = settings.PlanExpiresAt.HasValue
            ? $"válido até {settings.PlanExpiresAt.Value.ToLocalTime():dd/MM/yyyy}"
            : "com acesso Vitalício";

        var message = settings.GetEffectivePlanTier() switch
        {
            StoreSettings.PlanTierPremium => $"Gift Card {code} resgatado! Plano Premium Moldável ativado ({expiryMessage}).",
            StoreSettings.PlanTierIntermediate => $"Gift Card {code} resgatado! Plano Intermediário (5 versões pré-prontas) ativado ({expiryMessage}).",
            _ => $"Gift Card {code} resgatado! Plano Básico Minimalista Luxo ativado ({expiryMessage})."
        };

        if (isGiftedUnpaid)
        {
            message += " Como o plano foi liberado sem pagamento (cortesia), seu servidor fica hospedado na conta YouLink (Servidor Compartilhado) e você pode escolher um domínio grátis ou comprar um domínio próprio!";
        }

        return message;
    }

    private static PlatformGiftCardSyncDto MapToSyncDto(PlanActivationCode c) =>
        new(
            c.Id,
            c.Code,
            c.TargetPlanTier,
            c.DurationDays,
            c.TargetStoreName,
            c.Notes,
            c.PlanBillingKind,
            c.SuggestedDomainName,
            c.CreatedAt,
            c.CodeExpiresAt,
            c.IsRevoked,
            c.RedeemedAt,
            c.RedeemedByStoreName,
            c.RedeemedByEmployeeId,
            c.AppliedPlanExpiresAt,
            c.StatusUpdatedAt);

    public static AdminStoreSettingsDto MapToAdminSettingsDto(StoreSettings s) =>
        new(
            s.Id,
            s.StoreName,
            string.IsNullOrWhiteSpace(s.StoreSlug) ? Category.GenerateSlug(s.StoreName) : s.StoreSlug,
            s.WhatsappNumber,
            s.StoreDescription,
            s.LogoStoragePath,
            s.LogoPublicUrl,
            s.DefaultLowStockThreshold,
            s.UpdatedAt,
            s.OnlineBaseUrl,
            s.AutoSyncOnlineOnSave,
            s.LastOnlineSyncAt,
            s.IsSiteActive,
            s.DeactivationReason,
            s.LastActivityAt,
            s.PublishedStoreName,
            s.PlanTier,
            s.BasicPlanMonthlyPrice,
            s.PremiumPlanMonthlyPrice,
            s.PicpayPixKey,
            StoreDesignMapper.ToDto(s.IsEffectiveIntermediatePlan() ? s.GetEffectivePublicDesign() : s.DesignConfig),
            s.PlanActivatedAt,
            s.PlanExpiresAt,
            s.ActiveGiftCardCode,
            s.IntermediaryPlanMonthlyPrice,
            s.CanUseDirectPixSales() && s.DirectPixSalesEnabled,
            s.IsDirectPixCheckoutReady(),
            s.StorePixKey,
            s.StorePixReceiverName,
            s.OriginCep,
            s.OriginCity,
            s.OriginState,
            s.DeliveryScopeMode,
            s.MaxDeliveryDistanceKm,
            s.DefaultPackageWeightGrams,
            s.ServerHostingMode,
            s.OwnServerProvider,
            !string.IsNullOrWhiteSpace(s.OwnServerSyncSecret),
            s.GoogleOAuthClientId,
            s.GoogleAccountEmail,
            s.GoogleAccountTier,
            s.GoogleDriveQuotaBytes,
            s.GoogleHostedDomain,
            s.GoogleAccountVerifiedAt,
            s.QualifiesForDedicatedServerBenefit(),
            s.ActivePlanBillingKind,
            s.DomainAcquisitionMode,
            s.ManualSalesEnabled,
            s.PixGatewayMode,
            s.CentralCatalogBaseUrl,
            !string.IsNullOrWhiteSpace(s.CentralPublishToken),
            s.HasMercadoPagoConnected,
            s.MercadoPagoAccountLabel,
            s.MercadoPagoUserId,
            s.MercadoPagoConnectedAt);

    public async Task<AdminStoreSettingsDto> SyncOwnerPricingAsync(
        UpdateOwnerPricingSyncRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pixKey = (request.PicpayPixKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(pixKey))
            throw new DomainException("Informe a Chave Pix oficial para sincronizar os preços da plataforma.");

        if (request.BasicPlanMonthlyPrice <= 0m ||
            request.IntermediaryPlanMonthlyPrice <= 0m ||
            request.PremiumPlanMonthlyPrice <= 0m)
        {
            throw new DomainException("Todos os valores mensais dos planos devem ser maiores que zero.");
        }

        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.PicpayPixKey = pixKey;
        settings.BasicPlanMonthlyPrice = Math.Round(request.BasicPlanMonthlyPrice, 2, MidpointRounding.AwayFromZero);
        settings.IntermediaryPlanMonthlyPrice = Math.Round(request.IntermediaryPlanMonthlyPrice, 2, MidpointRounding.AwayFromZero);
        settings.PremiumPlanMonthlyPrice = Math.Round(request.PremiumPlanMonthlyPrice, 2, MidpointRounding.AwayFromZero);
        if (request.CentralCatalogServerUrl is not null)
        {
            settings.CentralCatalogBaseUrl = string.IsNullOrWhiteSpace(request.CentralCatalogServerUrl)
                ? null
                : request.CentralCatalogServerUrl.Trim().TrimEnd('/');
        }
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        await _repository.UpdateStoreSettingsAsync(settings, ct);
        return MapToAdminSettingsDto(settings);
    }

    public async Task<AdminStoreSettingsDto> ApplyApprovedRemotePlanAsync(
        PlatformPlanOrderStatusDto status,
        Guid? employeeId = null,
        CancellationToken ct = default)
    {
        if (status is null || !status.IsApproved)
            throw new DomainException("Este pedido ainda não foi confirmado pelo administrador.");

        var settings = await _repository.GetStoreSettingsAsync(ct);
        var targetTier = StoreSettings.NormalizePlanTier(status.PlanTier);
        var normalizedGiftCode = string.IsNullOrWhiteSpace(status.IssuedGiftCardCode)
            ? settings.ActiveGiftCardCode
            : status.IssuedGiftCardCode.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(normalizedGiftCode) &&
            string.Equals(settings.ActiveGiftCardCode, normalizedGiftCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(settings.PlanTier, targetTier, StringComparison.OrdinalIgnoreCase) &&
            settings.IsLicenseActive())
        {
            return MapToAdminSettingsDto(settings);
        }

        var now = DateTimeOffset.UtcNow;
        var nextExpiry = status.AppliedPlanExpiresAt ?? now.AddDays(Math.Max(1, status.DurationDays));

        settings.UpdatePlanAndDesign(targetTier);
        settings.PlanActivatedAt = status.ApprovedAt ?? now;
        settings.PlanExpiresAt = nextExpiry;
        settings.ActivePlanBillingKind = PlanActivationCode.BillingKindPaid;
        settings.ActiveGiftCardCode = normalizedGiftCode;

        if (!string.IsNullOrWhiteSpace(status.DesiredDomain) &&
            (status.DesiredDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             status.DesiredDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                settings.ConfigureDomainChoice(null, status.DesiredDomain, employeeId);
            }
            catch
            {
            }
        }

        settings.ActivateSite(employeeId);
        await _repository.UpdateStoreSettingsAsync(settings, ct);
        return MapToAdminSettingsDto(settings);
    }
}

