using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Helpers;

public sealed record ExistingPlanPromptState(
    bool ShouldPrompt,
    string CurrentPlanTier,
    string CurrentPlanTitle,
    string SelectedPlanTitle,
    bool IsLifetime,
    bool IsSameTier,
    bool IsDowngrade,
    DateTimeOffset? ExpiresAt,
    string Headline,
    string Message,
    string ConfirmActionLabel);

/// <summary>
/// Avalia se a loja atual já possui um plano ativo equivalente/superior (ex.: Premium ou Vitalício)
/// antes de gerar um novo pedido de assinatura no checkout.
/// </summary>
public static class PlanCheckoutGuardHelper
{
    public static int GetTierRank(string? tier)
    {
        if (string.Equals(tier, StoreDesignPresets.PlanPremium, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (string.Equals(tier, StoreDesignPresets.PlanIntermediate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tier, "Intermediário", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tier, "MeiaVersao", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tier, "Meia Versão", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 1;
    }

    public static string GetPlanTitle(string? tier) => GetTierRank(tier) switch
    {
        3 => "Plano Premium Sob Medida",
        2 => "Plano Intermediário (Meia Versão)",
        _ => "Plano Básico"
    };

    public static ExistingPlanPromptState EvaluatePurchasePrompt(
        AdminStoreSettingsDto? settings,
        string? selectedTier,
        string? targetStoreName,
        DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var currentTier = NormalizeTier(settings?.PlanTier);
        var targetTier = NormalizeTier(selectedTier);
        var currentRank = GetTierRank(currentTier);
        var targetRank = GetTierRank(targetTier);
        var currentTitle = GetPlanTitle(currentTier);
        var targetTitle = GetPlanTitle(targetTier);

        if (settings is null)
        {
            return new(false, currentTier, currentTitle, targetTitle, false, false, false, null, string.Empty, string.Empty, string.Empty);
        }

        // Permite contratar sem aviso quando o usuário está comprando explicitamente para outra loja
        var enteredStore = (targetStoreName ?? string.Empty).Trim();
        var ownStore = (settings.StoreName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(enteredStore) &&
            !string.IsNullOrWhiteSpace(ownStore) &&
            !string.Equals(enteredStore, ownStore, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, currentTier, currentTitle, targetTitle, false, false, false, settings.PlanExpiresAt, string.Empty, string.Empty, string.Empty);
        }

        var isExpired = settings.PlanExpiresAt.HasValue && currentTime >= settings.PlanExpiresAt.Value;
        if (isExpired)
        {
            return new(false, currentTier, currentTitle, targetTitle, false, false, false, settings.PlanExpiresAt, string.Empty, string.Empty, string.Empty);
        }

        var hasPaidOrActivatedPlan =
            currentRank > 1 ||
            settings.PlanActivatedAt.HasValue ||
            settings.PlanExpiresAt.HasValue ||
            !string.IsNullOrWhiteSpace(settings.ActiveGiftCardCode);

        if (!hasPaidOrActivatedPlan)
        {
            return new(false, currentTier, currentTitle, targetTitle, false, false, false, settings.PlanExpiresAt, string.Empty, string.Empty, string.Empty);
        }

        var isLifetime = !settings.PlanExpiresAt.HasValue;
        var isSameTier = currentRank == targetRank;
        var isDowngrade = targetRank < currentRank;

        var shouldPrompt = isLifetime || isSameTier || isDowngrade;
        if (!shouldPrompt)
        {
            return new(false, currentTier, currentTitle, targetTitle, isLifetime, isSameTier, isDowngrade, settings.PlanExpiresAt, string.Empty, string.Empty, string.Empty);
        }

        var expiryDesc = isLifetime
            ? "Vitalício (sem expiração)"
            : $"ativo até {settings.PlanExpiresAt!.Value.ToLocalTime():dd/MM/yyyy}";

        string headline;
        string message;
        string confirmLabel;

        if (currentRank == 3 && isSameTier && isLifetime)
        {
            headline = "👑 Você já possui o Plano Premium Vitalício!";
            message = $"A loja \"{ownStore}\" já está no {currentTitle} ({expiryDesc}). Não é necessário comprar o mesmo plano novamente.";
            confirmLabel = "💳 Gerar Pedido Mesmo Assim";
        }
        else if (isSameTier)
        {
            headline = $"✨ Você já possui o {currentTitle} ativo";
            message = $"A loja \"{ownStore}\" já está com o {currentTitle} ({expiryDesc}). Deseja renovar/estender o prazo ou mudar de plano?";
            confirmLabel = "🔄 Sim, Renovar / Gerar Pedido Pix";
        }
        else
        {
            headline = $"⚠️ Mudar do {currentTitle} para o {targetTitle}?";
            message = $"Sua loja \"{ownStore}\" já possui o {currentTitle} ({expiryDesc}). Se continuar, o nível ativo do plano será alterado para {targetTitle}.";
            confirmLabel = $"🔄 Confirmar Troca para {targetTitle}";
        }

        return new(true, currentTier, currentTitle, targetTitle, isLifetime, isSameTier, isDowngrade, settings.PlanExpiresAt, headline, message, confirmLabel);
    }

    private static string NormalizeTier(string? tier) => GetTierRank(tier) switch
    {
        3 => StoreDesignPresets.PlanPremium,
        2 => StoreDesignPresets.PlanIntermediate,
        _ => StoreDesignPresets.PlanBasic
    };
}
