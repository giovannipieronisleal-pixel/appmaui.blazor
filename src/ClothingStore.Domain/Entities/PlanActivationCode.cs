using System.Security.Cryptography;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Código Gift Card / cupom de ativação de versão e validade do plano da loja.
/// </summary>
public sealed class PlanActivationCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string TargetPlanTier { get; set; } = StoreSettings.PlanTierPremium;
    public int? DurationDays { get; set; }
    public string? TargetStoreName { get; set; }
    public string? Notes { get; set; }
    public Guid? CreatedByEmployeeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CodeExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public string? RedeemedByStoreName { get; set; }
    public Guid? RedeemedByEmployeeId { get; set; }
    public DateTimeOffset? AppliedPlanExpiresAt { get; set; }

    public bool IsRedeemed => RedeemedAt.HasValue;
    public bool IsLifetime => !DurationDays.HasValue;

    public bool CanRedeem(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        return !IsRevoked && !IsRedeemed && (!CodeExpiresAt.HasValue || current < CodeExpiresAt.Value);
    }

    public string GetDurationLabel()
    {
        if (!DurationDays.HasValue)
            return "Vitalício";

        return DurationDays.Value switch
        {
            7 => "7 dias",
            15 => "15 dias",
            30 => "30 dias (1 mês)",
            90 => "90 dias (3 meses)",
            180 => "180 dias (6 meses)",
            365 => "365 dias (1 ano)",
            _ => $"{DurationDays.Value} dia(s)"
        };
    }

    public static PlanActivationCode Create(
        string targetPlanTier,
        int? durationDays,
        string? customCode = null,
        string? targetStoreName = null,
        string? notes = null,
        Guid? createdByEmployeeId = null,
        DateTimeOffset? codeExpiresAt = null)
    {
        var normalizedTier = StoreSettings.NormalizePlanTier(targetPlanTier);

        if (durationDays.HasValue && (durationDays.Value < 1 || durationDays.Value > 36500))
            throw new DomainException("A duração do Gift Card deve estar entre 1 e 36.500 dias (ou deixe em branco para Vitalício).");

        var normalizedCode = string.IsNullOrWhiteSpace(customCode)
            ? GenerateFormattedCode(normalizedTier, durationDays)
            : customCode.Trim().ToUpperInvariant().Replace(" ", "-");

        if (normalizedCode.Length < 4 || normalizedCode.Length > 64)
            throw new DomainException("O código Gift Card deve conter entre 4 e 64 caracteres.");

        return new PlanActivationCode
        {
            Code = normalizedCode,
            TargetPlanTier = normalizedTier,
            DurationDays = durationDays,
            TargetStoreName = string.IsNullOrWhiteSpace(targetStoreName) ? null : targetStoreName.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedByEmployeeId = createdByEmployeeId,
            CreatedAt = DateTimeOffset.UtcNow,
            CodeExpiresAt = codeExpiresAt
        };
    }

    public static string GenerateFormattedCode(string targetPlanTier, int? durationDays)
    {
        var tier = StoreSettings.NormalizePlanTier(targetPlanTier);
        var tierPrefix = tier switch
        {
            StoreSettings.PlanTierPremium => "PREM",
            StoreSettings.PlanTierIntermediate => "INTR",
            _ => "BASC"
        };
        var durationToken = durationDays.HasValue ? $"{durationDays.Value}D" : "VIT";
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        Span<char> suffix1 = stackalloc char[4];
        Span<char> suffix2 = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            suffix1[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            suffix2[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return $"LUXO-{tierPrefix}-{durationToken}-{new string(suffix1)}{new string(suffix2)}";
    }

    public void MarkRedeemed(
        string storeName,
        Guid? employeeId,
        DateTimeOffset? appliedPlanExpiresAt,
        DateTimeOffset? now = null)
    {
        if (IsRevoked)
            throw new DomainException("Este código Gift Card foi revogado pelo administrador.");
        if (IsRedeemed)
            throw new DomainException("Este código Gift Card já foi resgatado anteriormente.");

        RedeemedAt = now ?? DateTimeOffset.UtcNow;
        RedeemedByStoreName = string.IsNullOrWhiteSpace(storeName) ? "Loja" : storeName.Trim();
        RedeemedByEmployeeId = employeeId;
        AppliedPlanExpiresAt = appliedPlanExpiresAt;
    }

    public void Revoke()
    {
        if (IsRedeemed)
            throw new DomainException("Não é possível revogar um Gift Card que já foi resgatado.");

        IsRevoked = true;
    }
}
