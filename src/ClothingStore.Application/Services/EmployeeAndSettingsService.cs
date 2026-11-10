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

    public EmployeeAndSettingsService(
        IClothingStoreRepository repository,
        IPasswordHasher passwordHasher,
        IImageStorageService imageStorage)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _imageStorage = imageStorage;
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
        AuthService.ValidatePasswordStrength(request.InitialPassword);

        var roleName = string.Equals(request.RoleName, RoleNames.Admin, StringComparison.OrdinalIgnoreCase)
            ? RoleNames.Admin
            : RoleNames.Employee;

        var role = await _repository.GetRoleByNameAsync(roleName, ct)
            ?? throw new DomainException($"Perfil '{roleName}' não encontrado.");

        // O e-mail não é obrigatório: usa o Login (Username), ou o E-mail (se informado), ou gera automaticamente a partir do Nome
        var loginIdentifier = !string.IsNullOrWhiteSpace(request.Username)
            ? request.Username.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(request.Email)
                ? request.Email.Trim().ToLowerInvariant()
                : Category.GenerateSlug(request.FullName).Replace('-', '.');

        if (string.IsNullOrWhiteSpace(loginIdentifier))
        {
            loginIdentifier = $"func.{Guid.NewGuid().ToString("N")[..6]}";
        }

        // Caso o login gerado automaticamente já exista e o usuário não digitou um login específico, adiciona sufixo numérico
        if (string.IsNullOrWhiteSpace(request.Username) && string.IsNullOrWhiteSpace(request.Email))
        {
            var baseLogin = loginIdentifier;
            int suffix = 2;
            while (await _repository.GetUserByEmailAsync(loginIdentifier, ct) is not null)
            {
                loginIdentifier = $"{baseLogin}{suffix}";
                suffix++;
            }
        }

        var passwordHash = _passwordHasher.HashPassword(request.InitialPassword);
        var user = new User(loginIdentifier, passwordHash, role.Id)
        {
            Role = role
        };

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
            request.OnlineBaseUrl,
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

    private static AdminStoreSettingsDto MapToAdminSettingsDto(StoreSettings s) =>
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
            s.LastOnlineSyncAt);
}

