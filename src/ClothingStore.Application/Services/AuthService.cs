using System.Security.Cryptography;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Serviço de autenticação, recuperação de acesso e gerenciamento de sessão de funcionários.
/// </summary>
public sealed class AuthService
{
    private readonly IClothingStoreRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;

    public AuthService(
        IClothingStoreRepository repository,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            throw new DomainException("Informe o e-mail e a senha para entrar.");

        var user = await _repository.GetUserByEmailAsync(request.Email, ct)
            ?? throw new DomainException("E-mail ou senha inválidos.");

        if (!user.IsActive)
            throw new DomainException("Esta conta de funcionário está desativada. Contate o administrador.");

        if (!_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
            throw new DomainException("E-mail ou senha inválidos.");

        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct)
            ?? throw new DomainException("Perfil de funcionário não encontrado para este usuário.");

        if (!employee.IsActive)
            throw new DomainException("O perfil deste funcionário está inativo.");

        var roleName = user.Role?.Name ?? RoleNames.Employee;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _repository.UpdateEmployeeAsync(employee, user, ct);

        var (token, expiresAt) = _jwtTokenService.GenerateToken(user, employee, roleName);
        return new LoginResponse(token, expiresAt, MapToProfileDto(employee, user, roleName));
    }

    public async Task<ForgotPasswordResponse> RequestPasswordResetAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var user = await _repository.GetUserByEmailAsync(request.Email, ct);
        if (user is null || !user.IsActive)
        {
            return new ForgotPasswordResponse(
                "Se o e-mail informado estiver ativo no sistema, um código de recuperação foi gerado.");
        }

        var rawCode = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        var tokenHash = _passwordHasher.HashToken(rawCode);
        user.GeneratePasswordResetToken(tokenHash, TimeSpan.FromMinutes(30));

        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct);
        if (employee is not null)
        {
            await _repository.UpdateEmployeeAsync(employee, user, ct);
        }

        return new ForgotPasswordResponse(
            "Código de recuperação gerado com validade de 30 minutos.",
            rawCode);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var user = await _repository.GetUserByEmailAsync(request.Email, ct)
            ?? throw new DomainException("Solicitação de redefinição inválida.");

        var tokenHash = _passwordHasher.HashToken(request.ResetToken);
        if (!user.IsResetTokenValid(tokenHash, DateTimeOffset.UtcNow))
            throw new DomainException("Código de recuperação inválido ou expirado.");

        user.SetPasswordHash(_passwordHasher.HashPassword(request.NewPassword));
        var employee = await _repository.GetEmployeeByUserIdAsync(user.Id, ct)
            ?? throw new DomainException("Funcionário não encontrado.");

        await _repository.UpdateEmployeeAsync(employee, user, ct);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
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
            employee.IsActive && user.IsActive);
}
