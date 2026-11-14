using System.Text.RegularExpressions;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Configurações personalizadas da loja gerenciadas pelo proprietário no aplicativo .NET MAUI
/// e carregadas dinamicamente pelo site Blazor Web App.
/// </summary>
public sealed class StoreSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StoreName { get; set; } = "Minha Loja";
    public string StoreSlug { get; set; } = "minha-loja";
    public string WhatsappNumber { get; set; } = "5511900000000";
    public string StoreDescription { get; set; } = "Catálogo digital oficial da loja.";
    public string? LogoStoragePath { get; set; }
    public string? LogoPublicUrl { get; set; }
    public int DefaultLowStockThreshold { get; set; } = 3;
    public string? OnlineBaseUrl { get; set; }
    public bool AutoSyncOnlineOnSave { get; set; } = true;
    public DateTimeOffset? LastOnlineSyncAt { get; set; }
    public Guid? UpdatedByEmployeeId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Regras de Ciclo de Vida do Site (Mudança de Nome e 3 Semanas de Inatividade)
    public bool IsSiteActive { get; set; } = true;
    public string? DeactivationReason { get; set; }
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    public string? PublishedStoreName { get; set; }

    public const int MaxInactivityDays = 21; // 3 semanas (21 dias) sem movimentação

    // Planos Comerciais e Estúdio Visual Sob Medida (Versão Premium)
    public const string PlanTierBasic = "Basico";
    public const string PlanTierIntermediate = "Intermediario";
    public const string PlanTierPremium = "Premium";

    public string PlanTier { get; set; } = PlanTierBasic;
    public decimal BasicPlanMonthlyPrice { get; set; } = 49.90m;
    public decimal IntermediaryPlanMonthlyPrice { get; set; } = 89.90m;
    public decimal PremiumPlanMonthlyPrice { get; set; } = 129.90m;
    public string PicpayPixKey { get; set; } = "picpay.me/sua-conta";
    public DateTimeOffset? PlanActivatedAt { get; set; }
    public DateTimeOffset? PlanExpiresAt { get; set; }
    public string? ActiveGiftCardCode { get; set; }
    public StoreDesignConfig DesignConfig { get; set; } = StoreDesignConfig.CreateMinimalistaLuxo();

    // Vendas diretas no app apenas por Pix (exclusivo Meia Versão / Intermediário até Premium), distância e Correios
    // ManualSalesEnabled permite ativar/desativar movimentação e registro de vendas sem Pix (Dinheiro, Cartão, WhatsApp / Balcão).
    public bool DirectPixSalesEnabled { get; set; } = false;
    public bool ManualSalesEnabled { get; set; } = true;
    public string? StorePixKey { get; set; }
    public string? StorePixReceiverName { get; set; }
    public string? OriginCep { get; set; }
    public string? OriginCity { get; set; }
    public string? OriginState { get; set; }
    public string DeliveryScopeMode { get; set; } = "TodoBrasil";
    public int MaxDeliveryDistanceKm { get; set; } = 0;
    public int DefaultPackageWeightGrams { get; set; } = 450;

    // Modo "Meu Servidor Próprio" (BYO Server), Plano Cortesia sem Pagamento & Domínio
    public const string DomainModePlatformDefault = "PlataformaPadrao";
    public const string DomainModeFreeDomain = "DominioGratis";
    public const string DomainModePurchasedDomain = "DominioComprado";

    public string ActivePlanBillingKind { get; set; } = PlanActivationCode.BillingKindPaid;
    public string DomainAcquisitionMode { get; set; } = DomainModePlatformDefault;
    public string ServerHostingMode { get; set; } = "PlatformShared";
    public string OwnServerProvider { get; set; } = "GoogleCloudRun";
    public string? OwnServerSyncSecret { get; set; }
    public string? GoogleOAuthClientId { get; set; }
    public string? GoogleAccountEmail { get; set; }
    public string GoogleAccountTier { get; set; } = "None";
    public long? GoogleDriveQuotaBytes { get; set; }
    public string? GoogleHostedDomain { get; set; }
    public DateTimeOffset? GoogleAccountVerifiedAt { get; set; }

    public static string NormalizePlanTier(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return PlanTierBasic;

        var trimmed = candidate.Trim();
        if (string.Equals(trimmed, PlanTierPremium, StringComparison.OrdinalIgnoreCase))
            return PlanTierPremium;

        if (string.Equals(trimmed, PlanTierIntermediate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Intermediário", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "MeiaVersao", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Meia Versão", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Intermediary", StringComparison.OrdinalIgnoreCase))
        {
            return PlanTierIntermediate;
        }

        return PlanTierBasic;
    }

    public bool IsIntermediatePlan => string.Equals(PlanTier, PlanTierIntermediate, StringComparison.OrdinalIgnoreCase);
    public bool IsPremiumPlan => string.Equals(PlanTier, PlanTierPremium, StringComparison.OrdinalIgnoreCase);

    public bool IsLicenseActive(DateTimeOffset? now = null) =>
        !PlanExpiresAt.HasValue || (now ?? DateTimeOffset.UtcNow) < PlanExpiresAt.Value;

    public bool IsEffectiveIntermediatePlan(DateTimeOffset? now = null) =>
        IsIntermediatePlan && IsLicenseActive(now);

    public bool IsEffectivePremiumPlan(DateTimeOffset? now = null) =>
        IsPremiumPlan && IsLicenseActive(now);

    public string GetEffectivePlanTier(DateTimeOffset? now = null)
    {
        if (IsEffectivePremiumPlan(now))
            return PlanTierPremium;
        if (IsEffectiveIntermediatePlan(now))
            return PlanTierIntermediate;
        return PlanTierBasic;
    }

    public decimal GetActivePlanMonthlyPrice() => GetEffectivePlanTier() switch
    {
        PlanTierPremium => PremiumPlanMonthlyPrice,
        PlanTierIntermediate => IntermediaryPlanMonthlyPrice,
        _ => BasicPlanMonthlyPrice
    };

    /// <summary>
    /// Retorna o design efetivo exibido no catálogo público e no aplicativo:
    /// - Básico (ou licença expirada): Minimalista Luxo fixo (não configurável).
    /// - Intermediário (Meia Versão): exatamente uma das 5 versões pré-prontas oficiais (não configurável).
    /// - Premium: design 100% moldável pelo cliente.
    /// </summary>
    public StoreDesignConfig GetEffectivePublicDesign(DateTimeOffset? now = null)
    {
        return GetEffectivePlanTier(now) switch
        {
            PlanTierPremium => (DesignConfig ?? StoreDesignConfig.CreateMinimalistaLuxo()).CreateNormalizedCopy(),
            PlanTierIntermediate => StoreDesignConfig.CreatePresetByKey(DesignConfig?.PresetKey),
            _ => StoreDesignConfig.CreateMinimalistaLuxo()
        };
    }

    public void RedeemActivationCode(
        PlanActivationCode activationCode,
        Guid? employeeId = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(activationCode);
        var current = now ?? DateTimeOffset.UtcNow;

        if (activationCode.IsRevoked)
            throw new DomainException("Este código Gift Card foi revogado e não pode mais ser utilizado.");

        if (activationCode.IsRedeemed)
            throw new DomainException("Este código Gift Card já foi resgatado.");

        if (activationCode.CodeExpiresAt.HasValue && current >= activationCode.CodeExpiresAt.Value)
            throw new DomainException("Este código Gift Card expirou antes do resgate.");

        var normalizedTier = NormalizePlanTier(activationCode.TargetPlanTier);

        var isSameActiveTier = string.Equals(PlanTier, normalizedTier, StringComparison.OrdinalIgnoreCase)
            && PlanExpiresAt.HasValue
            && PlanExpiresAt.Value > current;

        DateTimeOffset? nextExpiry;
        if (!activationCode.DurationDays.HasValue)
        {
            nextExpiry = null; // Vitalício
        }
        else if (isSameActiveTier)
        {
            nextExpiry = PlanExpiresAt!.Value.AddDays(activationCode.DurationDays.Value);
        }
        else
        {
            nextExpiry = current.AddDays(activationCode.DurationDays.Value);
        }

        PlanTier = normalizedTier;
        PlanActivatedAt = current;
        PlanExpiresAt = nextExpiry;
        ActiveGiftCardCode = activationCode.Code;
        ActivePlanBillingKind = activationCode.IsGiftedUnpaid
            ? PlanActivationCode.BillingKindGiftedUnpaid
            : PlanActivationCode.BillingKindPaid;

        // Regra de hospedagem para planos ganhos sem pagamento: vira servidor na conta do administrador (PlatformShared),
        // mantendo liberdade para o lojista usar um domínio grátis ou comprar seu domínio próprio.
        if (activationCode.IsGiftedUnpaid)
        {
            ServerHostingMode = "PlatformShared";
            OwnServerSyncSecret = null;
        }

        if (!string.IsNullOrWhiteSpace(activationCode.SuggestedDomainName))
        {
            var suggested = activationCode.SuggestedDomainName.Trim();
            var normalizedSuggestedUrl = suggested.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                         suggested.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? suggested.TrimEnd('/')
                : $"https://{suggested.TrimEnd('/')}";

            OnlineBaseUrl = normalizedSuggestedUrl;
            DomainAcquisitionMode = InferDomainAcquisitionMode(normalizedSuggestedUrl);
        }

        IsSiteActive = true;
        DeactivationReason = null;
        PublishedStoreName = StoreName;
        LastActivityAt = current;
        UpdatedAt = current;
        UpdatedByEmployeeId = employeeId;

        activationCode.MarkRedeemed(StoreName, employeeId, nextExpiry, current);
    }

    public void UpdatePlanAndDesign(
        string? planTier,
        decimal? basicPlanMonthlyPrice = null,
        decimal? premiumPlanMonthlyPrice = null,
        string? picpayPixKey = null,
        StoreDesignConfig? designConfig = null,
        Guid? employeeId = null,
        decimal? intermediaryPlanMonthlyPrice = null)
    {
        if (!string.IsNullOrWhiteSpace(planTier))
        {
            PlanTier = NormalizePlanTier(planTier);
        }

        if (basicPlanMonthlyPrice.HasValue)
        {
            if (basicPlanMonthlyPrice.Value < 0m)
                throw new DomainException("O valor mensal do plano Básico não pode ser negativo.");
            BasicPlanMonthlyPrice = Math.Round(basicPlanMonthlyPrice.Value, 2, MidpointRounding.AwayFromZero);
        }

        if (intermediaryPlanMonthlyPrice.HasValue)
        {
            if (intermediaryPlanMonthlyPrice.Value < 0m)
                throw new DomainException("O valor mensal do plano Intermediário não pode ser negativo.");
            IntermediaryPlanMonthlyPrice = Math.Round(intermediaryPlanMonthlyPrice.Value, 2, MidpointRounding.AwayFromZero);
        }

        if (premiumPlanMonthlyPrice.HasValue)
        {
            if (premiumPlanMonthlyPrice.Value < 0m)
                throw new DomainException("O valor mensal do plano Premium não pode ser negativo.");
            PremiumPlanMonthlyPrice = Math.Round(premiumPlanMonthlyPrice.Value, 2, MidpointRounding.AwayFromZero);
        }

        if (!string.IsNullOrWhiteSpace(picpayPixKey))
        {
            PicpayPixKey = picpayPixKey.Trim();
        }

        if (designConfig is not null)
        {
            if (IsEffectiveIntermediatePlan())
            {
                // Na Meia Versão, preserva eventuais rascunhos Premium anteriores apenas se precisar?
                // Melhor: aplica diretamente o preset oficial escolhido dentre os 5 (não configurável).
                DesignConfig = StoreDesignConfig.CreatePresetByKey(designConfig.PresetKey);
            }
            else
            {
                DesignConfig = designConfig.CreateNormalizedCopy();
            }
        }

        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public StoreSettings()
    {
    }

    public StoreSettings(
        string storeName,
        string whatsappNumber,
        string storeDescription,
        string? logoStoragePath = null,
        string? logoPublicUrl = null,
        int defaultLowStockThreshold = 3,
        string? customSlug = null,
        string? onlineBaseUrl = null,
        bool autoSyncOnlineOnSave = true,
        DateTimeOffset? lastOnlineSyncAt = null)
    {
        UpdateSettings(storeName, whatsappNumber, storeDescription, defaultLowStockThreshold, null, customSlug, onlineBaseUrl, autoSyncOnlineOnSave, lastOnlineSyncAt);
        SetLogo(logoStoragePath, logoPublicUrl, null);
    }

    public void UpdateSettings(
        string storeName,
        string whatsappNumber,
        string storeDescription,
        int defaultLowStockThreshold = 5,
        Guid? employeeId = null,
        string? customSlug = null,
        string? onlineBaseUrl = null,
        bool autoSyncOnlineOnSave = true,
        DateTimeOffset? lastOnlineSyncAt = null)
    {
        if (string.IsNullOrWhiteSpace(storeName))
            throw new DomainException("O nome da loja é obrigatório.");

        if (storeName.Trim().Length > 100)
            throw new DomainException("O nome da loja deve conter no máximo 100 caracteres.");

        if (string.IsNullOrWhiteSpace(storeDescription))
            throw new DomainException("A descrição da loja é obrigatória.");

        if (storeDescription.Trim().Length > 1000)
            throw new DomainException("A descrição da loja deve conter no máximo 1000 caracteres.");

        if (defaultLowStockThreshold < 0)
            throw new DomainException("O limite padrão de estoque baixo não pode ser negativo.");

        var sanitizedNumber = SanitizeWhatsappNumber(whatsappNumber);
        var trimmedName = storeName.Trim();

        // REGRA DO USUÁRIO: Toda vez que o site mudar de nome, desativa o site do usuário!
        if (!string.IsNullOrWhiteSpace(PublishedStoreName) &&
            !string.Equals(PublishedStoreName, trimmedName, StringComparison.OrdinalIgnoreCase))
        {
            IsSiteActive = false;
            DeactivationReason = $"O site foi desativado automaticamente porque o nome da loja mudou de '{PublishedStoreName}' para '{trimmedName}'. Reative o site no aplicativo para colocá-lo no ar novamente.";
        }
        else if (string.IsNullOrWhiteSpace(PublishedStoreName))
        {
            PublishedStoreName = trimmedName;
        }

        StoreName = trimmedName;
        StoreSlug = Category.GenerateSlug(string.IsNullOrWhiteSpace(customSlug) ? StoreName : customSlug);
        WhatsappNumber = sanitizedNumber;
        StoreDescription = storeDescription.Trim();
        DefaultLowStockThreshold = defaultLowStockThreshold;
        OnlineBaseUrl = string.IsNullOrWhiteSpace(onlineBaseUrl) ? null : onlineBaseUrl.Trim().TrimEnd('/');
        AutoSyncOnlineOnSave = autoSyncOnlineOnSave;
        if (lastOnlineSyncAt.HasValue)
        {
            LastOnlineSyncAt = lastOnlineSyncAt.Value;
        }
        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
        LastActivityAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Verifica a regra de 3 semanas (21 dias) sem movimentação no app ou site.
    /// Se o período foi ultrapassado, desativa o site automaticamente.
    /// </summary>
    public bool CheckAndEnforceInactivity(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        if (IsSiteActive && (current - LastActivityAt).TotalDays >= MaxInactivityDays)
        {
            IsSiteActive = false;
            DeactivationReason = "O site foi desativado automaticamente após 3 semanas (21 dias) sem movimentação no aplicativo ou no catálogo.";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Registra movimentação no aplicativo ou catálogo (vendas, estoque, produtos, login).
    /// </summary>
    public void RecordActivity()
    {
        LastActivityAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Reativa e publica o site do usuário.
    /// </summary>
    public void ActivateSite(Guid? employeeId = null)
    {
        IsSiteActive = true;
        DeactivationReason = null;
        PublishedStoreName = StoreName;
        LastActivityAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedByEmployeeId = employeeId;
    }

    /// <summary>
    /// Desativa o site do usuário com um motivo específico.
    /// </summary>
    public void DeactivateSite(string reason, Guid? employeeId = null)
    {
        IsSiteActive = false;
        DeactivationReason = reason;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedByEmployeeId = employeeId;
    }

    public void SetLogo(string? storagePath, string? publicUrl, Guid? employeeId)
    {
        LogoStoragePath = string.IsNullOrWhiteSpace(storagePath) ? null : storagePath.Trim();
        LogoPublicUrl = string.IsNullOrWhiteSpace(publicUrl) ? null : publicUrl.Trim();
        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static string SanitizeWhatsappNumber(string rawNumber)
    {
        if (string.IsNullOrWhiteSpace(rawNumber))
            throw new DomainException("O número de WhatsApp é obrigatório.");

        var digitsOnly = Regex.Replace(rawNumber, @"\D", "");

        // Se o usuário informar apenas DDD + Telefone (10 ou 11 dígitos, ex.: 11999990000 ou 55991234567 do RS),
        // adiciona o DDI do Brasil (55) automaticamente
        if (digitsOnly.Length == 10 || digitsOnly.Length == 11)
        {
            digitsOnly = "55" + digitsOnly;
        }

        if (digitsOnly.Length < 10 || digitsOnly.Length > 15)
            throw new DomainException("Informe o número de WhatsApp completo com código do país (DDI) e DDD (ex.: 5511999999999).");

        return digitsOnly;
    }

    /// <summary>
    /// Gera o link público da loja com o nome/slug da loja para uso em QR Code fixo.
    /// Se OnlineBaseUrl estiver configurado (ex.: Google Cloud Run), utiliza o link online 24h automaticamente.
    /// </summary>
    public string BuildStoreCatalogUrl(string? baseSiteUrl = null)
    {
        var targetBase = !string.IsNullOrWhiteSpace(OnlineBaseUrl)
            ? OnlineBaseUrl.TrimEnd('/')
            : (string.IsNullOrWhiteSpace(baseSiteUrl) ? "http://localhost:5190" : baseSiteUrl.TrimEnd('/'));

        var slug = string.IsNullOrWhiteSpace(StoreSlug) ? Category.GenerateSlug(StoreName) : StoreSlug;

        var uriText = targetBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      targetBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? targetBase
            : $"https://{targetBase}";

        if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (host is "localhost" or "127.0.0.1" || host.EndsWith(".run.app", StringComparison.OrdinalIgnoreCase))
            {
                return $"{targetBase}/loja/{slug}";
            }

            // Para domínio próprio oficial do lojista (ex.: https://minhalojadigital.com):
            return targetBase;
        }

        return $"{targetBase}/loja/{slug}";
    }

    /// <summary>
    /// Gera o link oficial do WhatsApp no formato https://wa.me/NUMERO?text=MENSAGEM
    /// com a mensagem devidamente codificada para consulta de um produto do catálogo.
    /// </summary>
    public string BuildWhatsAppConsultationUrl(string productName, string? color = null, string? size = null)
    {
        var message = BuildWhatsAppConsultationMessage(productName, color, size);
        var encodedMessage = Uri.EscapeDataString(message);
        return $"https://wa.me/{WhatsappNumber}?text={encodedMessage}";
    }

    public static string BuildWhatsAppConsultationMessage(string productName, string? color = null, string? size = null)
    {
        var cleanProduct = string.IsNullOrWhiteSpace(productName) ? "Produto" : productName.Trim();

        if (!string.IsNullOrWhiteSpace(color) && !string.IsNullOrWhiteSpace(size))
        {
            return $"Olá! Tenho interesse no produto {cleanProduct}, na cor {color.Trim().ToLowerInvariant()} e tamanho {size.Trim().ToUpperInvariant()}. Gostaria de saber mais informações.";
        }

        if (!string.IsNullOrWhiteSpace(color))
        {
            return $"Olá! Tenho interesse no produto {cleanProduct}, na cor {color.Trim().ToLowerInvariant()}. Gostaria de saber mais informações.";
        }

        if (!string.IsNullOrWhiteSpace(size))
        {
            return $"Olá! Tenho interesse no produto {cleanProduct}, no tamanho {size.Trim().ToUpperInvariant()}. Gostaria de saber mais informações.";
        }

        return $"Olá! Tenho interesse no produto {cleanProduct}. Gostaria de saber mais informações.";
    }

    public bool CanUseDirectPixSales(DateTimeOffset? now = null) =>
        IsEffectiveIntermediatePlan(now) || IsEffectivePremiumPlan(now);

    public bool CanViewSalesAnalytics(DateTimeOffset? now = null) =>
        IsEffectiveIntermediatePlan(now) || IsEffectivePremiumPlan(now);

    public bool CanRecordNonPixSales(DateTimeOffset? now = null) =>
        CanViewSalesAnalytics(now) && ManualSalesEnabled;

    public void SetManualSalesEnabled(bool enabled, Guid? employeeId = null)
    {
        ManualSalesEnabled = enabled;
        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool IsDirectPixCheckoutReady(DateTimeOffset? now = null) =>
        CanUseDirectPixSales(now) &&
        DirectPixSalesEnabled &&
        !string.IsNullOrWhiteSpace(StorePixKey) &&
        Regex.Replace(OriginCep ?? string.Empty, @"\D", "").Length == 8;

    public void ConfigurePixAndShipping(
        bool directPixSalesEnabled,
        string? storePixKey,
        string? storePixReceiverName,
        string? originCep,
        string? originCity,
        string? originState,
        string? deliveryScopeMode,
        int maxDeliveryDistanceKm,
        int defaultPackageWeightGrams,
        Guid? employeeId = null,
        DateTimeOffset? now = null)
    {
        var eligible = CanUseDirectPixSales(now);
        if (directPixSalesEnabled && !eligible)
        {
            throw new DomainException(
                "A venda direta no app por Pix está disponível apenas da Meia Versão (Intermediário) até a Premium. Atualize seu plano em Planos para ativar.");
        }

        var normalizedPixKey = string.IsNullOrWhiteSpace(storePixKey) ? null : storePixKey.Trim();
        if (directPixSalesEnabled && (string.IsNullOrWhiteSpace(normalizedPixKey) || normalizedPixKey.Length < 5))
        {
            throw new DomainException(
                "Para ativar as vendas por Pix no app, informe uma Chave Pix válida (CPF, E-mail, Celular ou Chave Aleatória).");
        }

        string? formattedOriginCep = null;
        var resolvedOriginCity = string.IsNullOrWhiteSpace(originCity) ? null : originCity.Trim();
        var resolvedOriginState = string.IsNullOrWhiteSpace(originState) ? null : originState.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(originCep))
        {
            var cepDigits = Regex.Replace(originCep, @"\D", "");
            if (cepDigits.Length != 8)
                throw new DomainException("Informe um CEP de origem válido com 8 dígitos (ex.: 01310-100).");

            formattedOriginCep = $"{cepDigits[..5]}-{cepDigits[5..]}";
        }
        else if (directPixSalesEnabled)
        {
            throw new DomainException(
                "Para ativar vendas por Pix no app, informe o seu CEP de origem para calcular a distância de entrega e o frete dos Correios.");
        }

        if (!string.IsNullOrWhiteSpace(resolvedOriginState) && resolvedOriginState.Length != 2)
            throw new DomainException("A UF do vendedor deve conter exatamente 2 letras (ex.: SP, RJ, MG).");

        var scopeToken = (deliveryScopeMode ?? string.Empty).Trim();
        var scope = scopeToken switch
        {
            var s when string.Equals(s, "MesmoEstado", StringComparison.OrdinalIgnoreCase) => "MesmoEstado",
            var s when string.Equals(s, "MesmaCidade", StringComparison.OrdinalIgnoreCase) => "MesmaCidade",
            var s when string.Equals(s, "RaioKm", StringComparison.OrdinalIgnoreCase) => "RaioKm",
            _ => "TodoBrasil"
        };

        if (maxDeliveryDistanceKm < 0 || maxDeliveryDistanceKm > 5000)
            throw new DomainException("A distância máxima de entrega deve estar entre 0 e 5.000 km.");

        if (scope == "RaioKm" && maxDeliveryDistanceKm <= 0)
            throw new DomainException("No modo 'Por Raio em KM', informe a distância máxima em quilômetros (ex.: 50 km).");

        if (defaultPackageWeightGrams < 100 || defaultPackageWeightGrams > 30000)
            throw new DomainException("O peso padrão do pacote deve estar entre 100 g e 30.000 g (30 kg).");

        DirectPixSalesEnabled = eligible && directPixSalesEnabled;
        StorePixKey = normalizedPixKey;
        StorePixReceiverName = string.IsNullOrWhiteSpace(storePixReceiverName)
            ? null
            : storePixReceiverName.Trim();
        OriginCep = formattedOriginCep;
        OriginCity = resolvedOriginCity;
        OriginState = resolvedOriginState;
        DeliveryScopeMode = scope;
        MaxDeliveryDistanceKm = maxDeliveryDistanceKm;
        DefaultPackageWeightGrams = defaultPackageWeightGrams;

        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool QualifiesForDedicatedServerBenefit() =>
        string.Equals(GoogleAccountTier, "GoogleOneProOuSuperior", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(GoogleAccountTier, "GoogleWorkspace", StringComparison.OrdinalIgnoreCase);

    public void ConfigureServerHosting(
        string? hostingMode,
        string? provider,
        string? syncSecret,
        bool clearSyncSecret,
        string? googleOAuthClientId,
        Guid? employeeId = null)
    {
        var normalizedMode = string.Equals((hostingMode ?? string.Empty).Trim(), "DedicatedOwnServer", StringComparison.OrdinalIgnoreCase)
            ? "DedicatedOwnServer"
            : "PlatformShared";

        var cleanProvider = (provider ?? string.Empty).Trim();
        var normalizedProvider = cleanProvider switch
        {
            var p when string.Equals(p, "Render", StringComparison.OrdinalIgnoreCase) => "Render",
            var p when string.Equals(p, "FlyIo", StringComparison.OrdinalIgnoreCase) => "FlyIo",
            var p when string.Equals(p, "VPSPropria", StringComparison.OrdinalIgnoreCase) => "VPSPropria",
            var p when string.Equals(p, "Outro", StringComparison.OrdinalIgnoreCase) => "Outro",
            _ => "GoogleCloudRun"
        };

        if (normalizedMode == "DedicatedOwnServer" && string.IsNullOrWhiteSpace(OnlineBaseUrl))
        {
            throw new DomainException(
                "No modo 'Meu Servidor Próprio', informe a URL HTTPS do seu servidor (ex.: https://minhaloja.run.app ou Render/Fly.io).");
        }

        ServerHostingMode = normalizedMode;
        OwnServerProvider = normalizedProvider;

        if (clearSyncSecret)
        {
            OwnServerSyncSecret = null;
        }
        else if (!string.IsNullOrWhiteSpace(syncSecret))
        {
            OwnServerSyncSecret = syncSecret.Trim();
        }

        if (googleOAuthClientId is not null)
        {
            GoogleOAuthClientId = string.IsNullOrWhiteSpace(googleOAuthClientId)
                ? null
                : googleOAuthClientId.Trim();
        }

        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetGoogleAccountVerification(
        string email,
        string tier,
        long? driveQuotaBytes,
        string? hostedDomain,
        DateTimeOffset verifiedAt,
        string? googleOAuthClientId = null,
        Guid? employeeId = null)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new DomainException("E-mail Google verificado não pode ser vazio.");

        GoogleAccountEmail = email.Trim();
        GoogleAccountTier = string.IsNullOrWhiteSpace(tier) ? "None" : tier.Trim();
        GoogleDriveQuotaBytes = driveQuotaBytes;
        GoogleHostedDomain = string.IsNullOrWhiteSpace(hostedDomain) ? null : hostedDomain.Trim();
        GoogleAccountVerifiedAt = verifiedAt;

        if (!string.IsNullOrWhiteSpace(googleOAuthClientId))
        {
            GoogleOAuthClientId = googleOAuthClientId.Trim();
        }

        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ClearGoogleAccountVerification(Guid? employeeId = null)
    {
        GoogleAccountEmail = null;
        GoogleAccountTier = "None";
        GoogleDriveQuotaBytes = null;
        GoogleHostedDomain = null;
        GoogleAccountVerifiedAt = null;
        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool IsCurrentPlanGiftedUnpaid(DateTimeOffset? now = null) =>
        IsLicenseActive(now) &&
        string.Equals(ActivePlanBillingKind, PlanActivationCode.BillingKindGiftedUnpaid, StringComparison.OrdinalIgnoreCase);

    public bool UsesOwnerSharedServer =>
        !string.Equals(ServerHostingMode, "DedicatedOwnServer", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeDomainAcquisitionMode(string? candidate)
    {
        var raw = (candidate ?? string.Empty).Trim();
        if (string.Equals(raw, DomainModeFreeDomain, StringComparison.OrdinalIgnoreCase))
            return DomainModeFreeDomain;
        if (string.Equals(raw, DomainModePurchasedDomain, StringComparison.OrdinalIgnoreCase))
            return DomainModePurchasedDomain;
        return DomainModePlatformDefault;
    }

    public static string InferDomainAcquisitionMode(string? urlOrDomain)
    {
        if (string.IsNullOrWhiteSpace(urlOrDomain))
            return DomainModePlatformDefault;

        var raw = urlOrDomain.Trim().ToLowerInvariant();
        if (raw.Contains("localhost", StringComparison.Ordinal) ||
            raw.Contains("127.0.0.1", StringComparison.Ordinal) ||
            raw.Contains("minhaloja.com", StringComparison.Ordinal) ||
            raw.Contains("://lojas.", StringComparison.Ordinal))
        {
            return DomainModePlatformDefault;
        }

        if (raw.Contains(".duckdns.org", StringComparison.Ordinal) ||
            raw.Contains(".dpdns.org", StringComparison.Ordinal) ||
            raw.Contains(".pages.dev", StringComparison.Ordinal) ||
            raw.Contains(".workers.dev", StringComparison.Ordinal) ||
            raw.Contains(".onrender.com", StringComparison.Ordinal) ||
            raw.Contains(".run.app", StringComparison.Ordinal) ||
            raw.Contains(".fly.dev", StringComparison.Ordinal) ||
            raw.Contains(".github.io", StringComparison.Ordinal))
        {
            return DomainModeFreeDomain;
        }

        return DomainModePurchasedDomain;
    }

    public void ConfigureDomainChoice(
        string? domainAcquisitionMode,
        string? onlineBaseUrl = null,
        Guid? employeeId = null)
    {
        if (!string.IsNullOrWhiteSpace(onlineBaseUrl))
        {
            var trimmed = onlineBaseUrl.Trim().TrimEnd('/');
            OnlineBaseUrl = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"https://{trimmed}";
        }

        DomainAcquisitionMode = string.IsNullOrWhiteSpace(domainAcquisitionMode)
            ? InferDomainAcquisitionMode(OnlineBaseUrl)
            : NormalizeDomainAcquisitionMode(domainAcquisitionMode);

        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Configuração visual moldável do aplicativo e do catálogo público (desbloqueada no Plano Premium).
/// </summary>
public sealed partial class StoreDesignConfig
{
    public string PresetKey { get; set; } = "minimalista-luxo";
    public string BackgroundColor { get; set; } = "#FAF8F5";
    public string SurfaceColor { get; set; } = "#FFFFFF";
    public string SurfaceSubtleColor { get; set; } = "#F3EFE8";
    public string PrimaryColor { get; set; } = "#18181B";
    public string PrimaryTextColor { get; set; } = "#FAF8F5";
    public string AccentColor { get; set; } = "#B69158";
    public string TextColor { get; set; } = "#18181B";
    public string MutedTextColor { get; set; } = "#57534E";
    public string BorderColor { get; set; } = "#E7E2DA";
    public string SidebarColor { get; set; } = "#141416";
    public string DisplayFont { get; set; } = "Playfair Display";
    public string BodyFont { get; set; } = "Plus Jakarta Sans";
    public string CornerRadiusMode { get; set; } = "ROUND_FOUR";
    public string DensityMode { get; set; } = "balanced";
    public string HeroLayout { get; set; } = "split";
    public int GridColumns { get; set; } = 3;
    public bool ShowLookbookBadge { get; set; } = true;
    public string? HeroEyebrow { get; set; }
    public string? HeroHeadline { get; set; }

    public static StoreDesignConfig CreateMinimalistaLuxo() => new();

    /// <summary>
    /// Retorna uma das 5 versões pré-prontas oficiais do sistema (usadas no Plano Intermediário / Meia Versão).
    /// </summary>
    public static StoreDesignConfig CreatePresetByKey(string? presetKey)
    {
        var key = (presetKey ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "editorial-vogue" => new StoreDesignConfig
            {
                PresetKey = "editorial-vogue",
                BackgroundColor = "#F7F5F2",
                SurfaceColor = "#FFFFFF",
                SurfaceSubtleColor = "#EFECE6",
                PrimaryColor = "#0F0F10",
                PrimaryTextColor = "#FFFFFF",
                AccentColor = "#9A7B4F",
                TextColor = "#0F0F10",
                MutedTextColor = "#525252",
                BorderColor = "#DEDAD2",
                SidebarColor = "#0F0F10",
                DisplayFont = "Bodoni Moda",
                BodyFont = "Inter",
                CornerRadiusMode = "SHARP",
                DensityMode = "airy",
                HeroLayout = "centered",
                GridColumns = 3,
                ShowLookbookBadge = true
            },
            "boutique-rose" => new StoreDesignConfig
            {
                PresetKey = "boutique-rose",
                BackgroundColor = "#FDF8F6",
                SurfaceColor = "#FFFFFF",
                SurfaceSubtleColor = "#F7ECE8",
                PrimaryColor = "#3D2329",
                PrimaryTextColor = "#FDF8F6",
                AccentColor = "#C4847A",
                TextColor = "#2E1A1E",
                MutedTextColor = "#7A5B61",
                BorderColor = "#EBDAD5",
                SidebarColor = "#2B181C",
                DisplayFont = "Cormorant Garamond",
                BodyFont = "DM Sans",
                CornerRadiusMode = "ROUND_TWELVE",
                DensityMode = "airy",
                HeroLayout = "split",
                GridColumns = 3,
                ShowLookbookBadge = true
            },
            "urban-sartorial" => new StoreDesignConfig
            {
                PresetKey = "urban-sartorial",
                BackgroundColor = "#F4F4F5",
                SurfaceColor = "#FFFFFF",
                SurfaceSubtleColor = "#E4E4E7",
                PrimaryColor = "#09090B",
                PrimaryTextColor = "#FAFAFA",
                AccentColor = "#52525B",
                TextColor = "#09090B",
                MutedTextColor = "#52525B",
                BorderColor = "#D4D4D8",
                SidebarColor = "#09090B",
                DisplayFont = "Space Grotesk",
                BodyFont = "Inter",
                CornerRadiusMode = "ROUND_FOUR",
                DensityMode = "compact",
                HeroLayout = "split",
                GridColumns = 4,
                ShowLookbookBadge = true
            },
            "noir-noturno" => new StoreDesignConfig
            {
                PresetKey = "noir-noturno",
                BackgroundColor = "#0E0E10",
                SurfaceColor = "#18181B",
                SurfaceSubtleColor = "#232327",
                PrimaryColor = "#E5C99B",
                PrimaryTextColor = "#111113",
                AccentColor = "#C8A267",
                TextColor = "#F5F2EB",
                MutedTextColor = "#A8A29E",
                BorderColor = "#2C2C32",
                SidebarColor = "#09090B",
                DisplayFont = "Playfair Display",
                BodyFont = "Plus Jakarta Sans",
                CornerRadiusMode = "ROUND_FOUR",
                DensityMode = "balanced",
                HeroLayout = "banner",
                GridColumns = 3,
                ShowLookbookBadge = true
            },
            _ => CreateMinimalistaLuxo()
        };
    }

    public StoreDesignConfig CreateNormalizedCopy()
    {
        return new StoreDesignConfig
        {
            PresetKey = NormalizeToken(PresetKey, "minimalista-luxo"),
            BackgroundColor = NormalizeHex(BackgroundColor, "#FAF8F5"),
            SurfaceColor = NormalizeHex(SurfaceColor, "#FFFFFF"),
            SurfaceSubtleColor = NormalizeHex(SurfaceSubtleColor, "#F3EFE8"),
            PrimaryColor = NormalizeHex(PrimaryColor, "#18181B"),
            PrimaryTextColor = NormalizeHex(PrimaryTextColor, "#FAF8F5"),
            AccentColor = NormalizeHex(AccentColor, "#B69158"),
            TextColor = NormalizeHex(TextColor, "#18181B"),
            MutedTextColor = NormalizeHex(MutedTextColor, "#57534E"),
            BorderColor = NormalizeHex(BorderColor, "#E7E2DA"),
            SidebarColor = NormalizeHex(SidebarColor, "#141416"),
            DisplayFont = NormalizeFont(DisplayFont, AllowedDisplayFonts, "Playfair Display"),
            BodyFont = NormalizeFont(BodyFont, AllowedBodyFonts, "Plus Jakarta Sans"),
            CornerRadiusMode = NormalizeOneOf(CornerRadiusMode, ["SHARP", "ROUND_FOUR", "ROUND_TWELVE"], "ROUND_FOUR"),
            DensityMode = NormalizeOneOf(DensityMode, ["compact", "balanced", "airy"], "balanced"),
            HeroLayout = NormalizeOneOf(HeroLayout, ["split", "centered", "banner"], "split"),
            GridColumns = Math.Clamp(GridColumns, 2, 4),
            ShowLookbookBadge = ShowLookbookBadge,
            HeroEyebrow = string.IsNullOrWhiteSpace(HeroEyebrow) ? null : HeroEyebrow.Trim(),
            HeroHeadline = string.IsNullOrWhiteSpace(HeroHeadline) ? null : HeroHeadline.Trim()
        };
    }

    private static readonly string[] AllowedDisplayFonts =
    [
        "Playfair Display",
        "Cormorant Garamond",
        "Bodoni Moda",
        "Montserrat",
        "Space Grotesk"
    ];

    private static readonly string[] AllowedBodyFonts =
    [
        "Plus Jakarta Sans",
        "Inter",
        "DM Sans",
        "Montserrat",
        "Work Sans"
    ];

    private static readonly Regex HexColorRegex = MyHexRegex();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex MyHexRegex();

    private static string NormalizeHex(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;
        var trimmed = candidate.Trim();
        return HexColorRegex.IsMatch(trimmed) ? trimmed.ToUpperInvariant() : fallback;
    }

    private static string NormalizeFont(string? candidate, string[] allowed, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;
        var trimmed = candidate.Trim();
        return allowed.FirstOrDefault(f => string.Equals(f, trimmed, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }

    private static string NormalizeOneOf(string? candidate, string[] allowed, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;
        var trimmed = candidate.Trim();
        return allowed.FirstOrDefault(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }

    private static string NormalizeToken(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;
        return candidate.Trim().ToLowerInvariant();
    }
}
