using System.Security.Cryptography;
using System.Text;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Conta de autenticação de usuário vinculada a um funcionário.
/// Inclui proteção contra força bruta (lockout temporário), recuperação de senha e entrada por código no e-mail (OTP).
/// </summary>
public sealed class User
{
    public const int MaxFailedAttemptsBeforeLockout = 5;
    public static readonly TimeSpan DefaultLockoutDuration = TimeSpan.FromMinutes(15);

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ResetTokenHash { get; set; }
    public DateTimeOffset? ResetTokenExpiresAt { get; set; }
    public string? EmailLoginCodeHash { get; set; }
    public DateTimeOffset? EmailLoginCodeExpiresAt { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTimeOffset? LockoutEndAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User()
    {
    }

    public User(string email, string passwordHash, Guid roleId)
    {
        SetEmail(email);
        SetPasswordHash(passwordHash);
        RoleId = roleId;
        IsActive = true;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void SetEmail(string emailOrLogin)
    {
        if (string.IsNullOrWhiteSpace(emailOrLogin))
            throw new DomainException("Informe um login ou usuário válido.");

        Email = emailOrLogin.Trim().ToLowerInvariant();
    }

    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new DomainException("O hash da senha não pode ser vazio.");

        PasswordHash = passwordHash;
        ResetTokenHash = null;
        ResetTokenExpiresAt = null;
        FailedLoginAttempts = 0;
        LockoutEndAt = null;
    }

    public bool IsLockedOut(DateTimeOffset nowUtc) =>
        LockoutEndAt.HasValue && LockoutEndAt.Value > nowUtc;

    public void RecordFailedAccessAttempt(DateTimeOffset nowUtc)
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= MaxFailedAttemptsBeforeLockout)
        {
            LockoutEndAt = nowUtc.Add(DefaultLockoutDuration);
        }
    }

    public void ResetFailedAccessAttempts()
    {
        FailedLoginAttempts = 0;
        LockoutEndAt = null;
    }

    public void GeneratePasswordResetToken(string tokenHash, TimeSpan validity)
    {
        if (!IsActive)
            throw new DomainException("Não é possível recuperar senha de uma conta desativada.");

        ResetTokenHash = tokenHash;
        ResetTokenExpiresAt = DateTimeOffset.UtcNow.Add(validity);
    }

    public bool IsResetTokenValid(string tokenHash, DateTimeOffset nowUtc)
    {
        if (!IsActive ||
            IsLockedOut(nowUtc) ||
            string.IsNullOrWhiteSpace(ResetTokenHash) ||
            string.IsNullOrWhiteSpace(tokenHash) ||
            !ResetTokenExpiresAt.HasValue ||
            ResetTokenExpiresAt.Value < nowUtc)
        {
            return false;
        }

        return FixedTimeStringEquals(ResetTokenHash, tokenHash);
    }

    public void GenerateEmailLoginCode(string codeHash, TimeSpan validity)
    {
        if (!IsActive)
            throw new DomainException("Esta conta está desativada.");

        EmailLoginCodeHash = codeHash;
        EmailLoginCodeExpiresAt = DateTimeOffset.UtcNow.Add(validity);
    }

    public bool IsEmailLoginCodeValid(string codeHash, DateTimeOffset nowUtc)
    {
        if (!IsActive ||
            IsLockedOut(nowUtc) ||
            string.IsNullOrWhiteSpace(EmailLoginCodeHash) ||
            string.IsNullOrWhiteSpace(codeHash) ||
            !EmailLoginCodeExpiresAt.HasValue ||
            EmailLoginCodeExpiresAt.Value < nowUtc)
        {
            return false;
        }

        return FixedTimeStringEquals(EmailLoginCodeHash, codeHash);
    }

    public void ClearEmailLoginCode()
    {
        EmailLoginCodeHash = null;
        EmailLoginCodeExpiresAt = null;
    }

    private static bool FixedTimeStringEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
