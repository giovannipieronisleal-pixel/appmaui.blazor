using System.Security.Cryptography;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Common;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Serviço de autenticação segura por e-mail (senha, 2FA ou OTP),
/// criação de contas, gestão de perfil, PIN numérico e segurança em dois fatores (TOTP).
/// </summary>
public sealed class AuthService
{
    private readonly IClothingStoreRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IEmailService? _emailService;

    public AuthService(
        IClothingStoreRepository repository,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IEmailService? emailService = null)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _emailService = emailService;
    }

    public async Task<AuthSetupStatusDto> GetSetupStatusAsync(CancellationToken ct = default)
    {
        var employees = await _repository.GetEmployeesAsync(ct);
        return new AuthSetupStatusDto(RequiresInitialSetup: employees.Count == 0);
    }

    public async Task<LoginResponse> RegisterUserAccountAsync(
        RegisterUserAccountRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new DomainException("Informe seu nome completo.");

        if (string.IsNullOrWhiteSpace(request.Email))
            throw new DomainException("Informe um e-mail ou login de acesso válido.");

        ValidatePasswordStrength(request.Password);

        var existingUser = await _repository.GetUserByEmailAsync(request.Email, ct);
        if (existingUser is not null)
        {
            throw new DomainException("Este e-mail/login já está cadastrado no sistema.");
        }

        var existingEmployees = await _repository.GetEmployeesAsync(ct);
        var isFirstAccount = existingEmployees.Count == 0;
        var roleName = isFirstAccount ? RoleNames.Admin : RoleNames.Employee;

        var role = await _repository.GetRoleByNameAsync(roleName, ct)
            ?? await _repository.GetRoleByNameAsync(RoleNames.Admin, ct)
            ?? throw new DomainException("Perfil de acesso não encontrado.");

        var passwordHash = _passwordHasher.HashPassword(request.Password);
        var user = new User(request.Email, passwordHash, role.Id)
        {
            Role = role,
            LastLoginAt = DateTimeOffset.UtcNow
        };

        var employee = new Employee(
            user.Id,
            request.FullName.Trim(),
            isFirstAccount ? "Proprietário(a) / Administrador(a)" : "Equipe",
            request.Phone?.Trim(),
            canManageProducts: isFirstAccount,
            canManageStock: true)
        {
            User = user
        };

        await _repository.AddEmployeeWithUserAsync(user, employee, ct);

        var (token, expiresAt) = _jwtTokenService.GenerateToken(user, employee, role.Name);
        return new LoginResponse(token, expiresAt, MapToProfileDto(employee, user, role.Name));
    }

    public async Task<LoginResponse> RegisterInitialAdminByEmailAsync(
        RegisterInitialAdminRequest request,
        CancellationToken ct = default)
    {
        var existingEmployees = await _repository.GetEmployeesAsync(ct);
        if (existingEmployees.Count > 0)
        {
            throw new DomainException("O administrador inicial já foi configurado. Entre com seu e-mail ou peça acesso ao proprietário.");
        }

        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new DomainException("Informe seu nome completo.");

        if (string.IsNullOrWhiteSpace(request.Email))
            throw new DomainException("Informe um login de acesso válido.");

        if (string.IsNullOrWhiteSpace(request.StoreName))
            throw new DomainException("Informe o nome da sua loja.");

        ValidatePasswordStrength(request.Password);

        var adminRole = await _repository.GetRoleByNameAsync(RoleNames.Admin, ct)
            ?? throw new DomainException("Perfil de Administrador não encontrado.");

        var passwordHash = _passwordHasher.HashPassword(request.Password);
        var user = new User(request.Email, passwordHash, adminRole.Id)
        {
            Role = adminRole,
            LastLoginAt = DateTimeOffset.UtcNow
        };

        var employee = new Employee(
            user.Id,
            request.FullName.Trim(),
            "Proprietário(a) / Administrador(a)",
            request.WhatsappNumber?.Trim(),
            canManageProducts: true,
            canManageStock: true)
        {
            User = user
        };

        await _repository.AddEmployeeWithUserAsync(user, employee, ct);

        var storeSettings = await _repository.GetStoreSettingsAsync(ct);
        var whatsappToSave = string.IsNullOrWhiteSpace(request.WhatsappNumber)
            ? (string.IsNullOrWhiteSpace(storeSettings.WhatsappNumber) ? "5511900000000" : storeSettings.WhatsappNumber)
            : request.WhatsappNumber;

        storeSettings.UpdateSettings(
            request.StoreName.Trim(),
            whatsappToSave,
            string.IsNullOrWhiteSpace(storeSettings.StoreDescription) ? "Catálogo digital oficial da loja." : storeSettings.StoreDescription,
            storeSettings.DefaultLowStockThreshold,
            employee.Id,
            Category.GenerateSlug(request.StoreName));

        await _repository.UpdateStoreSettingsAsync(storeSettings, ct);

        var (token, expiresAt) = _jwtTokenService.GenerateToken(user, employee, RoleNames.Admin);
        return new LoginResponse(token, expiresAt, MapToProfileDto(employee, user, RoleNames.Admin));
    }

    public async Task<LoginChallengeResponse> LoginWithChallengeAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            throw new DomainException("Informe seu e-mail e sua senha para entrar.");

        var nowUtc = DateTimeOffset.UtcNow;
        var user = await _repository.GetUserByEmailAsync(request.Email, ct);

        if (user is null)
        {
            _ = _passwordHasher.VerifyPassword(request.Password, _passwordHasher.HashPassword("DummyTimingProtection@123"));
            throw new DomainException("Credenciais inválidas. Verifique seu e-mail e senha.");
        }

        if (user.IsLockedOut(nowUtc))
        {
            throw new DomainException("Conta temporariamente bloqueada por excesso de tentativas incorretas. Aguarde 15 minutos ou redefina sua senha.");
        }

        if (!user.IsActive)
        {
            throw new DomainException("Credenciais inválidas ou conta desativada.");
        }

        if (!_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            user.RecordFailedAccessAttempt(nowUtc);
            var empForUpdate = await _repository.GetEmployeeByUserIdAsync(user.Id, ct);
            if (empForUpdate is not null)
            {
                await _repository.UpdateEmployeeAsync(empForUpdate, user, ct);
            }
            throw new DomainException("Credenciais inválidas. Verifique seu e-mail e senha.");
        }

        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct)
            ?? throw new DomainException("Credenciais inválidas.");

        if (!employee.IsActive)
            throw new DomainException("Credenciais inválidas ou conta desativada.");

        // Se a conta possuir 2FA ativado, exige o código de segundo fator
        if (user.TwoFactorEnabled)
        {
            return new LoginChallengeResponse(
                RequiresTwoFactor: true,
                TempSessionEmail: user.Email,
                AuthResult: null);
        }

        user.ResetFailedAccessAttempts();
        user.LastLoginAt = nowUtc;
        var roleName = user.Role?.Name ?? RoleNames.Employee;
        await _repository.UpdateEmployeeAsync(employee, user, ct);

        var (token, expiresAt) = _jwtTokenService.GenerateToken(user, employee, roleName);
        var loginResp = new LoginResponse(token, expiresAt, MapToProfileDto(employee, user, roleName));
        return new LoginChallengeResponse(RequiresTwoFactor: false, TempSessionEmail: null, AuthResult: loginResp);
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var challenge = await LoginWithChallengeAsync(request, ct);
        if (challenge.RequiresTwoFactor)
        {
            throw new DomainException("Esta conta possui autenticação em duas etapas (2FA) ativada. Informe o código de 6 dígitos.");
        }

        return challenge.AuthResult!;
    }

    public async Task<LoginResponse> VerifyTwoFactorLoginAsync(
        VerifyTwoFactorLoginRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code))
            throw new DomainException("Informe o código de 6 dígitos.");

        var nowUtc = DateTimeOffset.UtcNow;
        var user = await _repository.GetUserByEmailAsync(request.Email, ct)
            ?? throw new DomainException("Código de verificação inválido.");

        if (user.IsLockedOut(nowUtc))
            throw new DomainException("Conta temporariamente bloqueada por excesso de tentativas incorretas. Aguarde 15 minutos.");

        if (!user.VerifyTwoFactorCode(request.Code))
        {
            user.RecordFailedAccessAttempt(nowUtc);
            var empForUpdate = await _repository.GetEmployeeByUserIdAsync(user.Id, ct);
            if (empForUpdate is not null)
            {
                await _repository.UpdateEmployeeAsync(empForUpdate, user, ct);
            }
            throw new DomainException("Código de autenticação em duas etapas inválido ou expirado.");
        }

        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        user.ResetFailedAccessAttempts();
        user.LastLoginAt = nowUtc;
        var roleName = user.Role?.Name ?? RoleNames.Employee;
        await _repository.UpdateEmployeeAsync(employee, user, ct);

        var (token, expiresAt) = _jwtTokenService.GenerateToken(user, employee, roleName);
        return new LoginResponse(token, expiresAt, MapToProfileDto(employee, user, roleName));
    }

    public async Task<EmployeeProfileDto> UpdateUserProfileAsync(
        Guid userId,
        UpdateUserProfileRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new DomainException("Informe seu nome completo.");

        if (string.IsNullOrWhiteSpace(request.Email))
            throw new DomainException("Informe um e-mail válido.");

        var user = await _repository.GetUserByIdAsync(userId, ct)
            ?? throw new DomainException("Usuário não encontrado.");

        var employee = await _repository.GetEmployeeByUserIdAsync(userId, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        user.SetEmail(request.Email);
        employee.FullName = request.FullName.Trim();
        employee.Phone = request.Phone?.Trim();
        employee.UpdatedAt = DateTimeOffset.UtcNow;

        await _repository.UpdateEmployeeAsync(employee, user, ct);
        var roleName = user.Role?.Name ?? RoleNames.Employee;
        return MapToProfileDto(employee, user, roleName);
    }

    public async Task SetSecurityPinAsync(
        Guid userId,
        SetSecurityPinRequest request,
        CancellationToken ct = default)
    {
        var user = await _repository.GetUserByIdAsync(userId, ct)
            ?? throw new DomainException("Usuário não encontrado.");

        if (!_passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
            throw new DomainException("A senha atual informada está incorreta.");

        if (string.IsNullOrWhiteSpace(request.Pin) || request.Pin.Length != 4 || !request.Pin.All(char.IsDigit))
            throw new DomainException("O PIN de segurança deve conter exatamente 4 números.");

        var pinHash = _passwordHasher.HashPassword(request.Pin);
        user.SetSecurityPin(pinHash);

        var employee = await _repository.GetEmployeeByUserIdAsync(userId, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        await _repository.UpdateEmployeeAsync(employee, user, ct);
    }

    public async Task RemoveSecurityPinAsync(
        Guid userId,
        RemoveSecurityPinRequest request,
        CancellationToken ct = default)
    {
        var user = await _repository.GetUserByIdAsync(userId, ct)
            ?? throw new DomainException("Usuário não encontrado.");

        if (!_passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
            throw new DomainException("A senha atual informada está incorreta.");

        user.RemoveSecurityPin();

        var employee = await _repository.GetEmployeeByUserIdAsync(userId, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        await _repository.UpdateEmployeeAsync(employee, user, ct);
    }

    public async Task<TwoFactorSetupDto> GenerateTwoFactorSetupAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var user = await _repository.GetUserByIdAsync(userId, ct)
            ?? throw new DomainException("Usuário não encontrado.");

        var secret = TwoFactorHelper.GenerateBase32Secret(10);
        var backupCodes = TwoFactorHelper.GenerateBackupCodes(4);
        var otpUri = TwoFactorHelper.BuildOtpAuthUri(secret, user.Email, "Flavia Roupas");

        return new TwoFactorSetupDto(secret, otpUri, backupCodes);
    }

    public async Task<EmployeeProfileDto> EnableTwoFactorAsync(
        Guid userId,
        EnableTwoFactorRequest request,
        CancellationToken ct = default)
    {
        var user = await _repository.GetUserByIdAsync(userId, ct)
            ?? throw new DomainException("Usuário não encontrado.");

        var isValid = TwoFactorHelper.VerifyTotp(request.SecretKey, request.VerificationCode);
        if (!isValid)
            throw new DomainException("Código de 6 dígitos inválido. Verifique o aplicativo autenticador ou digite novamente.");

        user.EnableTwoFactor(request.SecretKey, request.BackupCodes?.ToList());

        var employee = await _repository.GetEmployeeByUserIdAsync(userId, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        await _repository.UpdateEmployeeAsync(employee, user, ct);
        var roleName = user.Role?.Name ?? RoleNames.Employee;
        return MapToProfileDto(employee, user, roleName);
    }

    public async Task<EmployeeProfileDto> DisableTwoFactorAsync(
        Guid userId,
        DisableTwoFactorRequest request,
        CancellationToken ct = default)
    {
        var user = await _repository.GetUserByIdAsync(userId, ct)
            ?? throw new DomainException("Usuário não encontrado.");

        if (!_passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
            throw new DomainException("A senha atual informada está incorreta.");

        user.DisableTwoFactor();

        var employee = await _repository.GetEmployeeByUserIdAsync(userId, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        await _repository.UpdateEmployeeAsync(employee, user, ct);
        var roleName = user.Role?.Name ?? RoleNames.Employee;
        return MapToProfileDto(employee, user, roleName);
    }

    public async Task<RequestEmailLoginCodeResponse> RequestEmailLoginCodeAsync(
        RequestEmailLoginCodeRequest request,
        CancellationToken ct = default)
    {
        const string genericMessage = "Se o e-mail informado estiver cadastrado e ativo, enviamos um código de entrada de 6 dígitos (válido por 10 minutos).";

        if (string.IsNullOrWhiteSpace(request.Email))
            throw new DomainException("Informe seu login ou e-mail.");

        var nowUtc = DateTimeOffset.UtcNow;
        var user = await _repository.GetUserByEmailAsync(request.Email, ct);
        if (user is null || !user.IsActive || user.IsLockedOut(nowUtc))
        {
            return new RequestEmailLoginCodeResponse(genericMessage);
        }

        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct);
        if (employee is null || !employee.IsActive)
        {
            return new RequestEmailLoginCodeResponse(genericMessage);
        }

        var rawCode = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        var codeHash = _passwordHasher.HashToken(rawCode);
        user.GenerateEmailLoginCode(codeHash, TimeSpan.FromMinutes(10));
        await _repository.UpdateEmployeeAsync(employee, user, ct);

        if (_emailService is not null)
        {
            await _emailService.SendSecurityCodeEmailAsync(
                user.Email,
                "Seu código de entrada no Sistema da Loja",
                rawCode,
                validityMinutes: 10,
                ct);
        }

        return new RequestEmailLoginCodeResponse(genericMessage);
    }

    public async Task<LoginResponse> LoginWithEmailCodeAsync(
        VerifyEmailLoginCodeRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code))
            throw new DomainException("Informe seu e-mail e o código de 6 dígitos enviado.");

        var nowUtc = DateTimeOffset.UtcNow;
        var user = await _repository.GetUserByEmailAsync(request.Email, ct)
            ?? throw new DomainException("Código de acesso inválido ou expirado.");

        if (user.IsLockedOut(nowUtc))
            throw new DomainException("Conta temporariamente bloqueada por excesso de tentativas incorretas. Aguarde 15 minutos.");

        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct)
            ?? throw new DomainException("Código de acesso inválido ou expirado.");

        var codeHash = _passwordHasher.HashToken(request.Code.Trim());
        if (!user.IsEmailLoginCodeValid(codeHash, nowUtc) || !employee.IsActive)
        {
            user.RecordFailedAccessAttempt(nowUtc);
            await _repository.UpdateEmployeeAsync(employee, user, ct);
            throw new DomainException("Código de acesso inválido ou expirado.");
        }

        user.ClearEmailLoginCode();
        user.ResetFailedAccessAttempts();
        user.LastLoginAt = nowUtc;
        var roleName = user.Role?.Name ?? RoleNames.Employee;
        await _repository.UpdateEmployeeAsync(employee, user, ct);

        var (token, expiresAt) = _jwtTokenService.GenerateToken(user, employee, roleName);
        return new LoginResponse(token, expiresAt, MapToProfileDto(employee, user, roleName));
    }

    public async Task<ForgotPasswordResponse> RequestPasswordResetAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        const string genericSafeResponse = "Se o e-mail informado estiver ativo no sistema, enviamos as instruções com o código de recuperação (válido por 30 minutos).";

        if (string.IsNullOrWhiteSpace(request.Email))
            throw new DomainException("Informe seu login ou e-mail.");

        var user = await _repository.GetUserByEmailAsync(request.Email, ct);
        if (user is null || !user.IsActive)
        {
            return new ForgotPasswordResponse(genericSafeResponse);
        }

        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct);
        if (employee is null || !employee.IsActive)
        {
            return new ForgotPasswordResponse(genericSafeResponse);
        }

        var rawCode = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        var tokenHash = _passwordHasher.HashToken(rawCode);
        user.GeneratePasswordResetToken(tokenHash, TimeSpan.FromMinutes(30));
        await _repository.UpdateEmployeeAsync(employee, user, ct);

        if (_emailService is not null)
        {
            await _emailService.SendSecurityCodeEmailAsync(
                user.Email,
                "Código de recuperação de senha",
                rawCode,
                validityMinutes: 30,
                ct);
        }

        return new ForgotPasswordResponse(genericSafeResponse);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        ValidatePasswordStrength(request.NewPassword);

        var nowUtc = DateTimeOffset.UtcNow;
        var user = await _repository.GetUserByEmailAsync(request.Email, ct)
            ?? throw new DomainException("Código de recuperação inválido ou expirado.");

        if (user.IsLockedOut(nowUtc))
            throw new DomainException("Conta temporariamente bloqueada. Aguarde 15 minutos antes de tentar novamente.");

        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct)
            ?? throw new DomainException("Código de recuperação inválido ou expirado.");

        var tokenHash = _passwordHasher.HashToken(request.ResetToken.Trim());
        if (!user.IsResetTokenValid(tokenHash, nowUtc))
        {
            user.RecordFailedAccessAttempt(nowUtc);
            await _repository.UpdateEmployeeAsync(employee, user, ct);
            throw new DomainException("Código de recuperação inválido ou expirado.");
        }

        user.SetPasswordHash(_passwordHasher.HashPassword(request.NewPassword));
        await _repository.UpdateEmployeeAsync(employee, user, ct);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        ValidatePasswordStrength(request.NewPassword);

        var user = await _repository.GetUserByIdAsync(userId, ct)
            ?? throw new DomainException("Usuário não encontrado.");

        if (!_passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
            throw new DomainException("A senha atual informada está incorreta.");

        user.SetPasswordHash(_passwordHasher.HashPassword(request.NewPassword));
        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        await _repository.UpdateEmployeeAsync(employee, user, ct);
    }

    public async Task<EmployeeProfileDto> GetCurrentProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _repository.GetUserByIdAsync(userId, ct)
            ?? throw new DomainException("Usuário não encontrado.");

        var employee = await _repository.GetEmployeeByUserIdAsync(userId, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        if (!user.IsActive || !employee.IsActive)
            throw new DomainException("Conta de funcionário desativada.");

        var roleName = user.Role?.Name ?? RoleNames.Employee;
        return MapToProfileDto(employee, user, roleName);
    }

    public static void ValidatePasswordStrength(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            throw new DomainException("A senha deve conter no mínimo 4 caracteres.");
    }

    public static EmployeeProfileDto MapToProfileDto(Employee employee, User user, string roleName) =>
        new(
            employee.Id,
            user.Id,
            employee.FullName,
            user.Email,
            roleName,
            employee.JobTitle,
            employee.Phone,
            employee.HasProductManagementPermission(roleName),
            employee.HasStockManagementPermission(roleName),
            employee.IsActive && user.IsActive,
            user.TwoFactorEnabled,
            !string.IsNullOrWhiteSpace(user.SecurityPinHash));
}
