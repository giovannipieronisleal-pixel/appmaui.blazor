using System.Security.Cryptography;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Pedido de compra ou renovação de plano pago (Básico, Meia Versão ou Premium) enviado pelo usuário do YouLink
/// para confirmação Pix no YouLink Admin e entrega automática do plano no aparelho do comprador.
/// </summary>
public sealed class PlatformSubscriptionOrder
{
    public const string StatusPending = "Pendente";
    public const string StatusPaid = "Pago";
    public const string StatusCancelled = "Cancelado";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNumber { get; set; } = string.Empty;
    public string ClaimToken { get; set; } = string.Empty;

    public string PlanTier { get; set; } = StoreSettings.PlanTierIntermediate;
    public int DurationMonths { get; set; } = 1;
    public int DurationDays { get; set; } = 30;
    public decimal Amount { get; set; }

    public string StoreName { get; set; } = string.Empty;
    public string StoreSlug { get; set; } = string.Empty;
    public string DesiredDomain { get; set; } = string.Empty;
    public bool UseOwnDedicatedServer { get; set; }

    public Guid? BuyerUserId { get; set; }
    public string BuyerUsername { get; set; } = string.Empty;
    public string BuyerEmail { get; set; } = string.Empty;
    public string BuyerFullName { get; set; } = string.Empty;

    public string PixKeyUsed { get; set; } = string.Empty;
    public string PixBrCode { get; set; } = string.Empty;

    public string Status { get; set; } = StatusPending;
    public string? IssuedGiftCardCode { get; set; }
    public DateTimeOffset? AppliedPlanExpiresAt { get; set; }
    public string? ApprovedByAdminName { get; set; }
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    public PlatformSubscriptionOrder()
    {
    }

    public static PlatformSubscriptionOrder CreatePending(
        string orderNumber,
        string planTier,
        int durationMonths,
        decimal amount,
        string storeName,
        string storeSlug,
        string desiredDomain,
        bool useOwnDedicatedServer,
        Guid? buyerUserId,
        string? buyerUsername,
        string? buyerEmail,
        string? buyerFullName,
        string pixKeyUsed,
        string pixBrCode,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
            throw new DomainException("Número do pedido de plano é obrigatório.");

        var normalizedMonths = durationMonths switch
        {
            3 => 3,
            12 => 12,
            _ => 1
        };

        var durationDays = normalizedMonths switch
        {
            3 => 90,
            12 => 365,
            _ => 30
        };

        if (amount < 0m)
            throw new DomainException("O valor do pedido não pode ser negativo.");

        return new PlatformSubscriptionOrder
        {
            Id = Guid.NewGuid(),
            OrderNumber = orderNumber.Trim().ToUpperInvariant(),
            ClaimToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            PlanTier = StoreSettings.NormalizePlanTier(planTier),
            DurationMonths = normalizedMonths,
            DurationDays = durationDays,
            Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero),
            StoreName = string.IsNullOrWhiteSpace(storeName) ? "Minha Loja" : storeName.Trim(),
            StoreSlug = string.IsNullOrWhiteSpace(storeSlug) ? "minha-loja" : storeSlug.Trim(),
            DesiredDomain = (desiredDomain ?? string.Empty).Trim(),
            UseOwnDedicatedServer = useOwnDedicatedServer,
            BuyerUserId = buyerUserId,
            BuyerUsername = (buyerUsername ?? string.Empty).Trim(),
            BuyerEmail = (buyerEmail ?? string.Empty).Trim(),
            BuyerFullName = string.IsNullOrWhiteSpace(buyerFullName)
                ? (buyerUsername ?? "Cliente YouLink").Trim()
                : buyerFullName.Trim(),
            PixKeyUsed = (pixKeyUsed ?? string.Empty).Trim(),
            PixBrCode = (pixBrCode ?? string.Empty).Trim(),
            Status = StatusPending,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public void Approve(
        string issuedGiftCardCode,
        DateTimeOffset? appliedPlanExpiresAt,
        string? adminName = null,
        DateTimeOffset? now = null)
    {
        if (Status == StatusCancelled)
            throw new DomainException("Este pedido já foi cancelado e não pode mais ser aprovado.");

        var ts = now ?? DateTimeOffset.UtcNow;
        Status = StatusPaid;
        IssuedGiftCardCode = string.IsNullOrWhiteSpace(issuedGiftCardCode) ? IssuedGiftCardCode : issuedGiftCardCode.Trim().ToUpperInvariant();
        AppliedPlanExpiresAt = appliedPlanExpiresAt ?? ts.AddDays(DurationDays);
        ApprovedByAdminName = string.IsNullOrWhiteSpace(adminName) ? "Administrador" : adminName.Trim();
        ApprovedAt ??= ts;
    }

    public void Cancel(string? reason = null, DateTimeOffset? now = null)
    {
        if (Status == StatusPaid)
            throw new DomainException("Este pedido já foi confirmado e liberado.");

        Status = StatusCancelled;
        CancelledAt = now ?? DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Notes = reason.Trim();
        }
    }
}
