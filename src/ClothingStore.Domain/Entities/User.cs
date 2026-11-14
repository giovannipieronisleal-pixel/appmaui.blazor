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
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public bool IsActive { get; set; } = true;
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }
    public List<string> TwoFactorBackupCodes { get; set; } = [];
    public string? SecurityPinHash { get; set; }
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

    public User(string email, string passwordHash, Guid roleId, string? username = null)
    {
        SetEmail(email);
        SetUsername(!string.IsNullOrWhiteSpace(username) ? username : DeriveDefaultUsername(Email));
        SetPasswordHash(passwordHash);
        RoleId = roleId;
        IsActive = true;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public bool HasEmailAddress => !string.IsNullOrWhiteSpace(Email) && Email.Contains('@', StringComparison.Ordinal);

    public void SetUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new DomainException("Informe um nome de usuário válido.");

        var normalized = username.Trim().ToLowerInvariant();
        if (normalized.Length > 80)
            throw new DomainException("O nome de usuário deve conter no máximo 80 caracteres.");

        Username = normalized;
    }

    public void SetEmail(string emailOrLogin)
    {
        if (string.IsNullOrWhiteSpace(emailOrLogin))
            throw new DomainException("Informe um e-mail ou usuário válido.");

        Email = emailOrLogin.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(Username))
        {
            Username = DeriveDefaultUsername(Email);
        }
    }

    public static string DeriveDefaultUsername(string emailOrLogin)
    {
        var normalized = (emailOrLogin ?? string.Empty).Trim().ToLowerInvariant();
        var atIdx = normalized.IndexOf('@');
        return atIdx > 0 ? normalized[..atIdx] : normalized;
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
            // Invalida códigos temporários ativos para impedir reuso pós-desbloqueio em ataques de força bruta
            ResetTokenHash = null;
            ResetTokenExpiresAt = null;
            EmailLoginCodeHash = null;
            EmailLoginCodeExpiresAt = null;
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

    public void EnableTwoFactor(string base32Secret, List<string>? backupCodes = null)
    {
        if (string.IsNullOrWhiteSpace(base32Secret))
            throw new DomainException("A chave de autenticação em duas etapas é inválida.");

        TwoFactorEnabled = true;
        TwoFactorSecret = base32Secret.Trim().ToUpperInvariant();
        TwoFactorBackupCodes = backupCodes ?? [];
    }

    public void DisableTwoFactor()
    {
        TwoFactorEnabled = false;
        TwoFactorSecret = null;
        TwoFactorBackupCodes.Clear();
    }

    public bool VerifyTwoFactorCode(string code)
    {
        if (!TwoFactorEnabled || string.IsNullOrWhiteSpace(TwoFactorSecret) || string.IsNullOrWhiteSpace(code))
            return false;

        var clean = code.Trim();

        // 1. Testa código TOTP (Google Authenticator / Authy)
        if (Common.TwoFactorHelper.VerifyTotp(TwoFactorSecret, clean))
            return true;

        // 2. Testa código de emergência/backup
        var matchedBackup = TwoFactorBackupCodes.FirstOrDefault(b =>
            string.Equals(b.Replace("-", ""), clean.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

        if (matchedBackup is not null)
        {
            TwoFactorBackupCodes.Remove(matchedBackup);
            return true;
        }

        return false;
    }

    public void SetSecurityPin(string pinHash)
    {
        if (string.IsNullOrWhiteSpace(pinHash))
            throw new DomainException("O hash do PIN de segurança é obrigatório.");

        SecurityPinHash = pinHash;
    }

    public void RemoveSecurityPin()
    {
        SecurityPinHash = null;
    }

    private static bool FixedTimeStringEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
