using System.Security.Cryptography;
using System.Text;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace ClothingStore.Application.Services;

/// <summary>
/// Orquestra os pedidos de assinatura/upgrade do aplicativo (Básico, Meia Versão e Premium):
/// libera a chave Pix do dono somente após o clique de compra e permite aprovação no Admin com entrega automática.
/// </summary>
public sealed class PlatformSubscriptionService
{
    private readonly IClothingStoreRepository _repository;

    public PlatformSubscriptionService(IClothingStoreRepository repository)
    {
        _repository = repository;
    }

    public async Task<PlatformSubscriptionOrderDto> CreatePlanOrderAsync(
        CreatePlatformPlanOrderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _repository.GetStoreSettingsAsync(ct);
        var pixKey = (settings.PicpayPixKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(pixKey))
        {
            throw new DomainException("A chave Pix oficial de recebimento da plataforma ainda não foi configurada no Admin.");
        }

        var normalizedTier = StoreSettings.NormalizePlanTier(request.PlanTier);
        var durationMonths = request.DurationMonths switch
        {
            3 => 3,
            12 => 12,
            _ => 1
        };

        var expectedMonthlyBase = normalizedTier switch
        {
            StoreSettings.PlanTierPremium => settings.PremiumPlanMonthlyPrice,
            StoreSettings.PlanTierIntermediate => settings.IntermediaryPlanMonthlyPrice,
            _ => settings.BasicPlanMonthlyPrice
        };

        if (request.UseOwnDedicatedServer)
            expectedMonthlyBase = Math.Max(19.90m, expectedMonthlyBase - 10.00m);

        var effectiveAmount = request.Amount > 0m
            ? Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero)
            : Math.Round(expectedMonthlyBase * durationMonths, 2, MidpointRounding.AwayFromZero);

        if (effectiveAmount <= 0m)
            throw new DomainException("O valor da contratação do plano deve ser maior que zero.");

        var storeName = string.IsNullOrWhiteSpace(request.StoreName)
            ? settings.StoreName
            : request.StoreName.Trim();
        var storeSlug = string.IsNullOrWhiteSpace(request.StoreSlug)
            ? Category.GenerateSlug(storeName)
            : request.StoreSlug.Trim();
        var desiredDomain = string.IsNullOrWhiteSpace(request.DesiredDomain)
            ? (settings.OnlineBaseUrl ?? $"https://lojas.{storeSlug}.com")
            : request.DesiredDomain.Trim();

        var orderNumber = $"YLK-{DateTimeOffset.UtcNow:yyyyMMdd}-{RandomNumberGenerator.GetInt32(1000, 9999)}";
        var brCode = PixPayloadBuilder.IsValidPixKey(pixKey)
            ? PixPayloadBuilder.BuildBrCode(
                pixKey,
                "YouLink",
                settings.OriginCity ?? "Brasil",
                effectiveAmount,
                orderNumber)
            : string.Empty;

        var order = PlatformSubscriptionOrder.CreatePending(
            orderNumber,
            normalizedTier,
            durationMonths,
            effectiveAmount,
            storeName,
            storeSlug,
            desiredDomain,
            request.UseOwnDedicatedServer,
            request.BuyerUserId,
            request.BuyerUsername,
            request.BuyerEmail,
            request.BuyerFullName,
            pixKey,
            brCode);

        await _repository.SavePlatformSubscriptionOrderAsync(order, ct);

        return MapToDto(
            order,
            "Pedido registrado! Chave Pix liberada abaixo. Após enviar o Pix, sua versão paga será ativada automaticamente neste aparelho.");
    }

    public async Task<PlatformPlanOrderStatusDto> GetPlanOrderStatusAsync(
        string orderNumber,
        string claimToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNumber) || string.IsNullOrWhiteSpace(claimToken))
            throw new DomainException("Número do pedido e token de acompanhamento são obrigatórios.");

        var order = await _repository.GetPlatformSubscriptionOrderByNumberAsync(orderNumber, ct)
            ?? throw new DomainException($"Pedido '{orderNumber.Trim()}' não encontrado.");

        var expectedBytes = Encoding.UTF8.GetBytes(order.ClaimToken);
        var providedBytes = Encoding.UTF8.GetBytes(claimToken.Trim());
        if (expectedBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            throw new DomainException("Token de acompanhamento inválido para este pedido.");
        }

        var isApproved = string.Equals(order.Status, PlatformSubscriptionOrder.StatusPaid, StringComparison.OrdinalIgnoreCase);
        var message = order.Status switch
        {
            PlatformSubscriptionOrder.StatusPaid => $"Pagamento confirmado! Plano {order.PlanTier} liberado automaticamente.",
            PlatformSubscriptionOrder.StatusCancelled => "Este pedido foi cancelado pelo administrador.",
            _ => "Aguardando confirmação do Pix pelo administrador para liberar automaticamente."
        };

        return new PlatformPlanOrderStatusDto(
            order.OrderNumber,
            order.Status,
            isApproved,
            order.PlanTier,
            order.DurationDays,
            order.AppliedPlanExpiresAt,
            order.IssuedGiftCardCode,
            order.ApprovedAt,
            message,
            order.DesiredDomain);
    }

    public async Task<IReadOnlyList<PlatformSubscriptionOrderDto>> GetAllOrdersAsync(CancellationToken ct = default)
    {
        var orders = await _repository.GetPlatformSubscriptionOrdersAsync(ct);
        return orders.Select(o => MapToDto(o, ResolveDefaultMessage(o))).ToList();
    }

    public async Task<PlatformSubscriptionOrderDto> ApprovePlanOrderAsync(
        Guid orderId,
        string? adminName = null,
        Guid? adminEmployeeId = null,
        CancellationToken ct = default)
    {
        var order = await _repository.GetPlatformSubscriptionOrderByIdAsync(orderId, ct)
            ?? throw new DomainException("Pedido de plano não encontrado.");

        if (string.Equals(order.Status, PlatformSubscriptionOrder.StatusPaid, StringComparison.OrdinalIgnoreCase))
        {
            return MapToDto(order, "Pagamento já confirmado anteriormente.");
        }

        if (string.Equals(order.Status, PlatformSubscriptionOrder.StatusCancelled, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("Este pedido foi cancelado e não pode ser aprovado.");
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(Math.Max(1, order.DurationDays));

        var auditCode = PlanActivationCode.Create(
            order.PlanTier,
            order.DurationDays,
            customCode: null,
            targetStoreName: order.StoreName,
            notes: $"Compra automática aprovada — Pedido {order.OrderNumber} ({order.BuyerUsername})",
            createdByEmployeeId: adminEmployeeId,
            codeExpiresAt: null,
            isGiftedUnpaid: false,
            suggestedDomainName: order.DesiredDomain);

        auditCode.MarkRedeemed(order.StoreName, order.BuyerUserId ?? adminEmployeeId, expiresAt, now);
        await _repository.SavePlanActivationCodeAsync(auditCode, ct);

        order.Approve(auditCode.Code, expiresAt, adminName, now);
        await _repository.SavePlatformSubscriptionOrderAsync(order, ct);

        var settings = await _repository.GetStoreSettingsAsync(ct);
        if (string.IsNullOrWhiteSpace(order.StoreName) ||
            string.Equals(settings.StoreName, order.StoreName, StringComparison.OrdinalIgnoreCase))
        {
            settings.UpdatePlanAndDesign(order.PlanTier);
            settings.PlanActivatedAt = now;
            settings.PlanExpiresAt = expiresAt;
            settings.ActivePlanBillingKind = PlanActivationCode.BillingKindPaid;
            settings.ActiveGiftCardCode = auditCode.Code;
            if (order.UseOwnDedicatedServer)
            {
                settings.ServerHostingMode = "DedicatedOwnServer";
            }

            if (!string.IsNullOrWhiteSpace(order.DesiredDomain) &&
                (order.DesiredDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 order.DesiredDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    settings.ConfigureDomainChoice(null, order.DesiredDomain, order.BuyerUserId ?? adminEmployeeId);
                }
                catch
                {
                }
            }

            settings.ActivateSite(adminEmployeeId);
            await _repository.UpdateStoreSettingsAsync(settings, ct);
        }

        return MapToDto(order, $"Pix confirmado! Plano {order.PlanTier} liberado automaticamente para '{order.StoreName}'.");
    }

    public async Task<PlatformSubscriptionOrderDto> CancelPlanOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await _repository.GetPlatformSubscriptionOrderByIdAsync(orderId, ct)
            ?? throw new DomainException("Pedido de plano não encontrado.");

        order.Cancel();
        await _repository.SavePlatformSubscriptionOrderAsync(order, ct);

        return MapToDto(order, "Pedido cancelado.");
    }

    private static string ResolveDefaultMessage(PlatformSubscriptionOrder order) =>
        order.Status switch
        {
            PlatformSubscriptionOrder.StatusPaid => $"Pago — liberado em {order.ApprovedAt?.ToLocalTime():dd/MM HH:mm}.",
            PlatformSubscriptionOrder.StatusCancelled => "Pedido cancelado.",
            _ => "Aguardando confirmação do Pix."
        };

    private static PlatformSubscriptionOrderDto MapToDto(PlatformSubscriptionOrder order, string message) =>
        new(
            order.Id,
            order.OrderNumber,
            order.ClaimToken,
            order.PlanTier,
            order.DurationMonths,
            order.DurationDays,
            order.Amount,
            order.StoreName,
            order.StoreSlug,
            order.DesiredDomain,
            order.UseOwnDedicatedServer,
            order.BuyerUserId,
            order.BuyerUsername,
            order.BuyerEmail,
            order.BuyerFullName,
            order.PixKeyUsed,
            order.PixBrCode,
            order.Status,
            order.IssuedGiftCardCode,
            order.AppliedPlanExpiresAt,
            order.CreatedAt,
            order.ApprovedAt,
            order.ApprovedByAdminName,
            message);
}
