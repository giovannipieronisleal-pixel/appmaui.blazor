namespace ClothingStore.Application.Interfaces;

/// <summary>
/// Serviço criptográfico para hash e verificação de senhas e tokens de recuperação.
/// </summary>
public interface IPasswordHasher
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string passwordHash);
    string HashToken(string rawToken);
}

