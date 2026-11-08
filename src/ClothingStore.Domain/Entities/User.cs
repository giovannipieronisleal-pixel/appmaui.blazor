using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Conta de autenticação de usuário vinculada a um funcionário.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ResetTokenHash { get; set; }
    public DateTimeOffset? ResetTokenExpiresAt { get; set; }
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

    public void SetEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new DomainException("Informe um endereço de e-mail válido.");

        Email = email.Trim().ToLowerInvariant();
    }

    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new DomainException("O hash da senha não pode ser vazio.");

        PasswordHash = passwordHash;
        ResetTokenHash = null;
        ResetTokenExpiresAt = null;
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
        return IsActive
            && !string.IsNullOrWhiteSpace(ResetTokenHash)
            && ResetTokenExpiresAt.HasValue
            && ResetTokenExpiresAt.Value >= nowUtc
            && string.Equals(ResetTokenHash, tokenHash, StringComparison.Ordinal);
    }
}
