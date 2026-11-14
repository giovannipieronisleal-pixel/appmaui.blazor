using System.Security.Claims;
using ClothingStore.Domain.Entities;

namespace ClothingStore.Application.Interfaces;

public interface IJwtTokenService
{
    (string Token, DateTimeOffset ExpiresAt) GenerateToken(User user, Employee employee, string roleName);
    ClaimsPrincipal? ValidateToken(string token);
    string GenerateTwoFactorChallengeToken(Guid userId, string email);
    bool ValidateTwoFactorChallengeToken(string? token, Guid expectedUserId);
}

