using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;

namespace ClothingStore.Application.Services;

/// <summary>
/// Integração direta com a API oficial do Mercado Pago (OAuth2 + POST/GET /v1/payments + assinatura HMAC de webhook).
/// </summary>
public sealed class MercadoPagoPixService : IMercadoPagoPixService
{
    private const string OAuthAuthorizeEndpoint = "https://auth.mercadopago.com/authorization";
    private const string OAuthTokenEndpoint = "https://api.mercadopago.com/oauth/token";
    private const string UsersMeEndpoint = "https://api.mercadopago.com/users/me";
    private const string PaymentsEndpoint = "https://api.mercadopago.com/v1/payments";
    private const string PaymentsSearchEndpoint = "https://api.mercadopago.com/v1/payments/search";
    private const string CheckoutPreferencesEndpoint = "https://api.mercadopago.com/checkout/preferences";
    private const string PreapprovalEndpoint = "https://api.mercadopago.com/preapproval";
    private const string AuthorizedPaymentsEndpoint = "https://api.mercadopago.com/authorized_payments";

    private readonly HttpClient _httpClient;
    private readonly Func<MercadoPagoAppConfig> _configProvider;

    public MercadoPagoPixService(
        HttpClient httpClient,
        Func<MercadoPagoAppConfig>? configProvider = null)
    {
        _httpClient = httpClient;
        _configProvider = configProvider ?? ReadFromEnvironment;
    }

    public static MercadoPagoAppConfig ReadFromEnvironment() => ReadFromConfig(null, null, null);

    public static MercadoPagoAppConfig ReadFromConfig(
        string? configuredClientId,
        string? configuredClientSecret,
        string? configuredWebhookSecret)
    {
        var clientId = Environment.GetEnvironmentVariable("MP_CLIENT_ID")
                       ?? Environment.GetEnvironmentVariable("MERCADOPAGO_CLIENT_ID")
                       ?? configuredClientId;
        var clientSecret = Environment.GetEnvironmentVariable("MP_CLIENT_SECRET")
                           ?? Environment.GetEnvironmentVariable("MERCADOPAGO_CLIENT_SECRET")
                           ?? configuredClientSecret;
        var webhookSecret = Environment.GetEnvironmentVariable("MP_WEBHOOK_SECRET")
                            ?? Environment.GetEnvironmentVariable("MERCADOPAGO_WEBHOOK_SECRET")
                            ?? configuredWebhookSecret;

        return new MercadoPagoAppConfig(
            string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim(),
            string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret.Trim(),
            string.IsNullOrWhiteSpace(webhookSecret) ? null : webhookSecret.Trim());
    }

    public static string BuildOAuthCallbackHtml(MercadoPagoOAuthCallbackResult result)
    {
        var title = result.Success ? "Mercado Pago conectado!" : "Não foi possível conectar";
        var badge = result.Success ? "✅ Conexão Concluída" : "⚠️ Atenção";
        var accent = result.Success ? "#22c55e" : "#ef4444";
        var safeMsg = System.Net.WebUtility.HtmlEncode(result.Message);
        var safeLabel = System.Net.WebUtility.HtmlEncode(result.AccountLabel);
        var extra = result.Success && !string.IsNullOrWhiteSpace(safeLabel)
            ? $"<p class='account'>Conta vinculada: <strong>{safeLabel}</strong></p>"
            : string.Empty;

        return $$"""
<!DOCTYPE html>
<html lang="pt-BR">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>{{title}} — YouLink</title>
  <style>
    body { margin:0; font-family:system-ui,-apple-system,sans-serif; background:#0f1117; color:#f3f4f6; display:flex; min-height:100vh; align-items:center; justify-content:center; padding:24px; }
    .card { max-width:440px; width:100%; background:#181b24; border:1px solid rgba(255,255,255,0.1); border-radius:16px; padding:28px; text-align:center; box-shadow:0 20px 50px rgba(0,0,0,0.5); }
    .badge { display:inline-block; padding:6px 14px; border-radius:999px; font-weight:700; font-size:13px; background:rgba(255,255,255,0.06); color:{{accent}}; border:1px solid {{accent}}; margin-bottom:16px; }
    h1 { margin:0 0 12px; font-size:22px; }
    p { margin:0 0 12px; line-height:1.5; color:#d1d5db; font-size:15px; }
    .account { background:rgba(212,175,55,0.1); border:1px solid rgba(212,175,55,0.35); border-radius:10px; padding:10px 14px; color:#f3e5ab; }
    .hint { font-size:13px; color:#9ca3af; margin-top:16px; }
  </style>
</head>
<body>
  <div class="card">
    <div class="badge">{{badge}}</div>
    <h1>{{title}}</h1>
    <p>{{safeMsg}}</p>
    {{extra}}
    <p class="hint">Você já pode fechar esta janela e voltar para o aplicativo <strong>YouLink</strong>.</p>
  </div>
</body>
</html>
""";
    }

    public static string BuildSubscriptionReturnHtml(
        bool isApproved,
        string? orderNumber,
        string planTitle,
        string message)
    {
        var title = isApproved ? "Pagamento Confirmado — Plano Liberado!" : "Pagamento em Processamento";
        var badge = isApproved ? "✅ Assinatura / Plano Ativo" : "⏳ Aguardando Confirmação";
        var accent = isApproved ? "#22c55e" : "#eab308";
        var safeOrder = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(orderNumber) ? "-" : orderNumber.Trim());
        var safePlan = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(planTitle) ? "Plano YouLink" : planTitle.Trim());
        var safeMsg = System.Net.WebUtility.HtmlEncode(message);

        return $$"""
<!DOCTYPE html>
<html lang="pt-BR">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>{{title}} — YouLink</title>
  <style>
    body { margin:0; font-family:system-ui,-apple-system,sans-serif; background:#0f1117; color:#f3f4f6; display:flex; min-height:100vh; align-items:center; justify-content:center; padding:24px; }
    .card { max-width:460px; width:100%; background:#181b24; border:1px solid rgba(255,255,255,0.1); border-radius:16px; padding:28px; text-align:center; box-shadow:0 20px 50px rgba(0,0,0,0.5); }
    .badge { display:inline-block; padding:6px 14px; border-radius:999px; font-weight:700; font-size:13px; background:rgba(255,255,255,0.06); color:{{accent}}; border:1px solid {{accent}}; margin-bottom:16px; }
    h1 { margin:0 0 12px; font-size:22px; }
    p { margin:0 0 12px; line-height:1.5; color:#d1d5db; font-size:15px; }
    .meta { background:rgba(212,175,55,0.1); border:1px solid rgba(212,175,55,0.35); border-radius:10px; padding:10px 14px; color:#f3e5ab; font-size:14px; }
    .hint { font-size:13px; color:#9ca3af; margin-top:16px; }
  </style>
</head>
<body>
  <div class="card">
    <div class="badge">{{badge}}</div>
    <h1>{{title}}</h1>
    <p>{{safeMsg}}</p>
    <p class="meta">Pedido: <strong>{{safeOrder}}</strong> · {{safePlan}}</p>
    <p class="hint">Volte para o aplicativo <strong>YouLink</strong> — ele detecta a confirmação em poucos segundos e libera o plano automaticamente.</p>
  </div>
</body>
</html>
""";
    }

    public MercadoPagoAppConfig GetConfiguredApp() => _configProvider();

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        var app = GetConfiguredApp();
        if (!app.IsOAuthConfigured || string.IsNullOrWhiteSpace(app.ClientId))
            throw new InvalidOperationException("O aplicativo OAuth do Mercado Pago não está configurado neste servidor.");

        return $"{OAuthAuthorizeEndpoint}" +
               $"?client_id={Uri.EscapeDataString(app.ClientId)}" +
               "&response_type=code" +
               "&platform_id=mp" +
               $"&state={Uri.EscapeDataString(state)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
    }

    public async Task<MercadoPagoOAuthTokenResult> ExchangeCodeForTokensAsync(
        string code,
        string redirectUri,
        CancellationToken ct = default)
    {
        var app = GetConfiguredApp();
        if (!app.IsOAuthConfigured)
        {
            return new MercadoPagoOAuthTokenResult(
                false, null, null, 0, null, null,
                "MP_CLIENT_ID e MP_CLIENT_SECRET não configurados no servidor.");
        }

        using var payload = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId!,
            ["client_secret"] = app.ClientSecret!,
            ["code"] = code.Trim(),
            ["redirect_uri"] = redirectUri
        });

        using var response = await _httpClient.PostAsync(OAuthTokenEndpoint, payload, ct);
        return await ParseOAuthTokenResponseAsync(response, ct);
    }

    public async Task<MercadoPagoOAuthTokenResult> RefreshAccessTokenAsync(
        string refreshToken,
        CancellationToken ct = default)
    {
        var app = GetConfiguredApp();
        if (!app.IsOAuthConfigured)
        {
            return new MercadoPagoOAuthTokenResult(
                false, null, null, 0, null, null,
                "MP_CLIENT_ID e MP_CLIENT_SECRET não configurados no servidor.");
        }

        using var payload = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId!,
            ["client_secret"] = app.ClientSecret!,
            ["refresh_token"] = refreshToken.Trim()
        });

        using var response = await _httpClient.PostAsync(OAuthTokenEndpoint, payload, ct);
        return await ParseOAuthTokenResponseAsync(response, ct);
    }

    public async Task<MercadoPagoPixPaymentResult> CreatePixPaymentAsync(
        string accessToken,
        decimal transactionAmount,
        string description,
        string orderNumber,
        string? customerName,
        string? payerEmail,
        string? notificationUrl,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new MercadoPagoPixPaymentResult(
                false, string.Empty, string.Empty, null, "error", null,
                "Access token da conta Mercado Pago ausente.");
        }

        var safeAmount = Math.Round(Math.Max(0.01m, transactionAmount), 2, MidpointRounding.AwayFromZero);
        var cleanEmail = IsValidEmailAddress(payerEmail)
            ? payerEmail!.Trim()
            : $"cliente.{SanitizeOrderSlug(orderNumber)}@cliente.youlink.com.br";

        var firstName = string.IsNullOrWhiteSpace(customerName)
            ? "Cliente"
            : customerName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Cliente";

        var body = new Dictionary<string, object?>
        {
            ["transaction_amount"] = safeAmount,
            ["description"] = string.IsNullOrWhiteSpace(description) ? $"Pedido {orderNumber}" : description.Trim(),
            ["payment_method_id"] = "pix",
            ["external_reference"] = orderNumber,
            ["payer"] = new Dictionary<string, object?>
            {
                ["email"] = cleanEmail,
                ["first_name"] = firstName
            }
        };

        if (IsHttpUrl(notificationUrl))
            body["notification_url"] = notificationUrl!.Trim();

        var effectiveIdempotency = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey.Trim();
        var (ok, raw, errMsg) = await PostAuthorizedJsonAsync(PaymentsEndpoint, accessToken, body, effectiveIdempotency, ct);
        if (!ok || string.IsNullOrWhiteSpace(raw))
        {
            return new MercadoPagoPixPaymentResult(
                false, string.Empty, string.Empty, null, "error", null, errMsg ?? "Resposta vazia do Mercado Pago.");
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var paymentId = root.TryGetProperty("id", out var idProp)
                ? idProp.ToString()
                : string.Empty;
            var status = root.TryGetProperty("status", out var stProp)
                ? stProp.GetString() ?? "pending"
                : "pending";
            var statusDetail = root.TryGetProperty("status_detail", out var sdProp)
                ? sdProp.GetString()
                : null;

            string? qrCode = null;
            string? qrCodeBase64 = null;
            if (root.TryGetProperty("point_of_interaction", out var poi) &&
                poi.TryGetProperty("transaction_data", out var txData))
            {
                if (txData.TryGetProperty("qr_code", out var qrProp))
                    qrCode = qrProp.GetString();
                if (txData.TryGetProperty("qr_code_base64", out var b64Prop))
                    qrCodeBase64 = b64Prop.GetString();
            }

            if (string.IsNullOrWhiteSpace(paymentId) || string.IsNullOrWhiteSpace(qrCode))
            {
                return new MercadoPagoPixPaymentResult(
                    false, paymentId, string.Empty, qrCodeBase64, status, statusDetail,
                    "Resposta do Mercado Pago não retornou código Pix QR Code.");
            }

            return new MercadoPagoPixPaymentResult(
                true, paymentId, qrCode, qrCodeBase64, status, statusDetail, null);
        }
        catch (Exception ex)
        {
            return new MercadoPagoPixPaymentResult(
                false, string.Empty, string.Empty, null, "error", null,
                $"Falha ao interpretar resposta do Mercado Pago: {ex.Message}");
        }
    }

    public async Task<MercadoPagoPaymentLookupResult?> GetPaymentStatusAsync(
        string accessToken,
        string paymentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(paymentId))
            return null;

        var raw = await GetAuthorizedRawAsync($"{PaymentsEndpoint}/{Uri.EscapeDataString(paymentId.Trim())}", accessToken, ct);
        if (raw is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ParsePaymentLookupElement(doc.RootElement, paymentId.Trim());
        }
        catch
        {
            return null;
        }
    }

    public async Task<MercadoPagoPaymentLookupResult?> SearchLatestPaymentByExternalReferenceAsync(
        string accessToken,
        string externalReference,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(externalReference))
            return null;

        var url = $"{PaymentsSearchEndpoint}?external_reference={Uri.EscapeDataString(externalReference.Trim())}&sort=date_created&criteria=desc";
        var raw = await GetAuthorizedRawAsync(url, accessToken, ct);
        if (raw is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                return null;
            }

            MercadoPagoPaymentLookupResult? latest = null;
            foreach (var item in results.EnumerateArray())
            {
                var parsed = ParsePaymentLookupElement(item, string.Empty);
                latest ??= parsed;
                if (parsed.IsApproved)
                    return parsed;
            }
            return latest;
        }
        catch
        {
            return null;
        }
    }

    public async Task<MercadoPagoCheckoutPreferenceResult> CreateCheckoutPreferenceAsync(
        string accessToken,
        decimal transactionAmount,
        string title,
        string orderNumber,
        string? payerName,
        string? payerEmail,
        string backUrlBase,
        string? notificationUrl,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new MercadoPagoCheckoutPreferenceResult(
                false, string.Empty, string.Empty, null,
                "Access token da plataforma no Mercado Pago não configurado.");
        }

        var safeAmount = Math.Round(Math.Max(0.01m, transactionAmount), 2, MidpointRounding.AwayFromZero);
        var encodedOrder = Uri.EscapeDataString(orderNumber);
        var cleanBackBase = string.IsNullOrWhiteSpace(backUrlBase)
            ? "https://youlink-loja.onrender.com/planos/retorno"
            : backUrlBase.Trim().TrimEnd('/');

        var body = new Dictionary<string, object?>
        {
            ["items"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = orderNumber,
                    ["title"] = string.IsNullOrWhiteSpace(title) ? $"Plano YouLink ({orderNumber})" : title.Trim(),
                    ["quantity"] = 1,
                    ["currency_id"] = "BRL",
                    ["unit_price"] = safeAmount
                }
            },
            ["external_reference"] = orderNumber,
            ["back_urls"] = new Dictionary<string, object?>
            {
                ["success"] = $"{cleanBackBase}?orderNumber={encodedOrder}&status=approved",
                ["pending"] = $"{cleanBackBase}?orderNumber={encodedOrder}&status=pending",
                ["failure"] = $"{cleanBackBase}?orderNumber={encodedOrder}&status=failure"
            },
            ["auto_return"] = "approved"
        };

        if (IsValidEmailAddress(payerEmail) || !string.IsNullOrWhiteSpace(payerName))
        {
            var payerObj = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(payerName))
                payerObj["name"] = payerName.Trim();
            if (IsValidEmailAddress(payerEmail))
                payerObj["email"] = payerEmail!.Trim();
            body["payer"] = payerObj;
        }

        if (IsHttpUrl(notificationUrl))
            body["notification_url"] = notificationUrl!.Trim();

        var (ok, raw, errMsg) = await PostAuthorizedJsonAsync(CheckoutPreferencesEndpoint, accessToken, body, idempotencyKey, ct);
        if (!ok)
        {
            return new MercadoPagoCheckoutPreferenceResult(false, string.Empty, string.Empty, null, errMsg);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw!);
            var root = doc.RootElement;
            var prefId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
            var initPoint = root.TryGetProperty("init_point", out var initProp) ? initProp.GetString() ?? string.Empty : string.Empty;
            var sandboxInit = root.TryGetProperty("sandbox_init_point", out var sbProp) ? sbProp.GetString() : null;
            var resolvedUrl = !string.IsNullOrWhiteSpace(initPoint) ? initPoint : (sandboxInit ?? string.Empty);

            if (string.IsNullOrWhiteSpace(prefId) || string.IsNullOrWhiteSpace(resolvedUrl))
            {
                return new MercadoPagoCheckoutPreferenceResult(false, prefId, string.Empty, sandboxInit,
                    "Resposta do Mercado Pago não retornou link de pagamento Checkout Pro.");
            }

            return new MercadoPagoCheckoutPreferenceResult(true, prefId, resolvedUrl, sandboxInit, null);
        }
        catch (Exception ex)
        {
            return new MercadoPagoCheckoutPreferenceResult(false, string.Empty, string.Empty, null,
                $"Falha ao interpretar preferência do Mercado Pago: {ex.Message}");
        }
    }

    public async Task<MercadoPagoSubscriptionResult> CreatePreapprovalSubscriptionAsync(
        string accessToken,
        decimal recurringAmount,
        int frequencyMonths,
        string reason,
        string orderNumber,
        string payerEmail,
        string backUrl,
        string? notificationUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new MercadoPagoSubscriptionResult(false, string.Empty, string.Empty, "error",
                "Access token da plataforma no Mercado Pago não configurado.");
        }

        if (!IsValidEmailAddress(payerEmail))
        {
            return new MercadoPagoSubscriptionResult(false, string.Empty, string.Empty, "error",
                "Para assinatura mensal automática no cartão, informe um e-mail válido no campo Usuário / Gmail de Acesso.");
        }

        var safeAmount = Math.Round(Math.Max(1.00m, recurringAmount), 2, MidpointRounding.AwayFromZero);
        var safeFreq = frequencyMonths switch { 3 => 3, 12 => 12, _ => 1 };
        var cleanBackUrl = IsHttpUrl(backUrl)
            ? backUrl.Trim()
            : $"https://youlink-loja.onrender.com/planos/retorno?orderNumber={Uri.EscapeDataString(orderNumber)}";

        var body = new Dictionary<string, object?>
        {
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? $"Assinatura YouLink ({orderNumber})" : reason.Trim(),
            ["external_reference"] = orderNumber,
            ["payer_email"] = payerEmail.Trim(),
            ["back_url"] = cleanBackUrl,
            ["auto_recurring"] = new Dictionary<string, object?>
            {
                ["frequency"] = safeFreq,
                ["frequency_type"] = "months",
                ["transaction_amount"] = safeAmount,
                ["currency_id"] = "BRL"
            },
            ["status"] = "pending"
        };

        if (IsHttpUrl(notificationUrl))
            body["notification_url"] = notificationUrl!.Trim();

        var (ok, raw, errMsg) = await PostAuthorizedJsonAsync(PreapprovalEndpoint, accessToken, body, idempotencyKey: null, ct);
        if (!ok)
        {
            return new MercadoPagoSubscriptionResult(false, string.Empty, string.Empty, "error", errMsg);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw!);
            var root = doc.RootElement;
            var subId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
            var initPoint = root.TryGetProperty("init_point", out var initProp) ? initProp.GetString() ?? string.Empty : string.Empty;
            var status = root.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "pending" : "pending";

            if (string.IsNullOrWhiteSpace(subId) || string.IsNullOrWhiteSpace(initPoint))
            {
                return new MercadoPagoSubscriptionResult(false, subId, string.Empty, status,
                    "Resposta do Mercado Pago não retornou link de assinatura (init_point).");
            }

            return new MercadoPagoSubscriptionResult(true, subId, initPoint, status, null);
        }
        catch (Exception ex)
        {
            return new MercadoPagoSubscriptionResult(false, string.Empty, string.Empty, "error",
                $"Falha ao interpretar assinatura do Mercado Pago: {ex.Message}");
        }
    }

    public async Task<MercadoPagoPreapprovalLookupResult?> GetPreapprovalStatusAsync(
        string accessToken,
        string preapprovalId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(preapprovalId))
            return null;

        var raw = await GetAuthorizedRawAsync($"{PreapprovalEndpoint}/{Uri.EscapeDataString(preapprovalId.Trim())}", accessToken, ct);
        if (raw is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? preapprovalId.Trim() : preapprovalId.Trim();
            var status = root.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "pending" : "pending";
            var extRef = root.TryGetProperty("external_reference", out var refProp) ? refProp.GetString() : null;
            var payerEmail = root.TryGetProperty("payer_email", out var emProp) ? emProp.GetString() : null;

            decimal? txAmount = null;
            if (root.TryGetProperty("auto_recurring", out var ar) &&
                ar.TryGetProperty("transaction_amount", out var amtProp) &&
                amtProp.TryGetDecimal(out var dec))
            {
                txAmount = dec;
            }

            DateTimeOffset? nextPaymentDate = null;
            if (root.TryGetProperty("next_payment_date", out var npd) &&
                npd.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(npd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDate))
            {
                nextPaymentDate = parsedDate;
            }

            return new MercadoPagoPreapprovalLookupResult(id, status, extRef, payerEmail, txAmount, nextPaymentDate);
        }
        catch
        {
            return null;
        }
    }

    public async Task<MercadoPagoAuthorizedPaymentLookupResult?> GetAuthorizedPaymentAsync(
        string accessToken,
        string authorizedPaymentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(authorizedPaymentId))
            return null;

        var raw = await GetAuthorizedRawAsync($"{AuthorizedPaymentsEndpoint}/{Uri.EscapeDataString(authorizedPaymentId.Trim())}", accessToken, ct);
        if (raw is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idProp) ? idProp.ToString() : authorizedPaymentId.Trim();
            var preapprovalId = root.TryGetProperty("preapproval_id", out var preProp) ? preProp.GetString() : null;
            var extRef = root.TryGetProperty("external_reference", out var refProp) ? refProp.GetString() : null;
            var status = root.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "pending" : "pending";

            string? paymentId = null;
            if (root.TryGetProperty("payment", out var payObj))
            {
                if (payObj.TryGetProperty("id", out var pidProp))
                    paymentId = pidProp.ToString();
                if (payObj.TryGetProperty("status", out var pstProp) && !string.IsNullOrWhiteSpace(pstProp.GetString()))
                    status = pstProp.GetString()!;
            }

            decimal? txAmount = null;
            if (root.TryGetProperty("transaction_amount", out var amtProp) && amtProp.TryGetDecimal(out var dec))
                txAmount = dec;

            return new MercadoPagoAuthorizedPaymentLookupResult(id, preapprovalId, paymentId, status, extRef, txAmount);
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Ok, string? RawBody, string? ErrorMessage)> PostAuthorizedJsonAsync(
        string url,
        string accessToken,
        object body,
        string? idempotencyKey,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey.Trim());

        using var response = await _httpClient.SendAsync(req, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var errMsg = ExtractErrorMessage(raw) ?? $"Mercado Pago retornou HTTP {(int)response.StatusCode}.";
            return (false, raw, errMsg);
        }

        return (true, raw, null);
    }

    private async Task<string?> GetAuthorizedRawAsync(
        string url,
        string accessToken,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

        using var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadAsStringAsync(ct);
    }

    private static MercadoPagoPaymentLookupResult ParsePaymentLookupElement(JsonElement root, string fallbackId)
    {
        var resolvedId = root.TryGetProperty("id", out var idProp)
            ? idProp.ToString()
            : fallbackId;
        var status = root.TryGetProperty("status", out var stProp)
            ? stProp.GetString() ?? "pending"
            : "pending";
        var statusDetail = root.TryGetProperty("status_detail", out var sdProp)
            ? sdProp.GetString()
            : null;
        var amount = root.TryGetProperty("transaction_amount", out var amtProp) && amtProp.TryGetDecimal(out var dec)
            ? dec
            : 0m;
        var extRef = root.TryGetProperty("external_reference", out var refProp)
            ? refProp.GetString()
            : null;

        DateTimeOffset? dateApproved = null;
        if (root.TryGetProperty("date_approved", out var dtProp) &&
            dtProp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(dtProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDate))
        {
            dateApproved = parsedDate;
        }

        return new MercadoPagoPaymentLookupResult(
            resolvedId, status, statusDetail, amount, extRef, dateApproved);
    }

    private static bool IsValidEmailAddress(string? email) =>
        !string.IsNullOrWhiteSpace(email) &&
        email.Contains('@', StringComparison.Ordinal) &&
        email.Contains('.', StringComparison.Ordinal);

    private static bool IsHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public bool VerifyWebhookSignature(
        string? webhookSecretOverride,
        string? xSignatureHeader,
        string? xRequestIdHeader,
        string? dataIdQueryParam)
    {
        var secret = string.IsNullOrWhiteSpace(webhookSecretOverride)
            ? GetConfiguredApp().WebhookSecret
            : webhookSecretOverride.Trim();

        // Quando sem segredo de webhook configurado, ainda validamos o pagamento direto via GET /v1/payments/{id}
        // autenticado no Mercado Pago antes de confirmar qualquer pedido.
        if (string.IsNullOrWhiteSpace(secret))
            return true;

        if (string.IsNullOrWhiteSpace(xSignatureHeader))
            return false;

        string? ts = null;
        string? v1 = null;
        foreach (var part in xSignatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0)
                continue;

            var key = part[..idx].Trim();
            var val = part[(idx + 1)..].Trim();
            if (string.Equals(key, "ts", StringComparison.OrdinalIgnoreCase))
                ts = val;
            else if (string.Equals(key, "v1", StringComparison.OrdinalIgnoreCase))
                v1 = val;
        }

        if (string.IsNullOrWhiteSpace(ts) || string.IsNullOrWhiteSpace(v1))
            return false;

        var manifestBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(dataIdQueryParam))
            manifestBuilder.Append("id:").Append(dataIdQueryParam.Trim().ToLowerInvariant()).Append(';');
        if (!string.IsNullOrWhiteSpace(xRequestIdHeader))
            manifestBuilder.Append("request-id:").Append(xRequestIdHeader.Trim()).Append(';');
        manifestBuilder.Append("ts:").Append(ts).Append(';');

        var expectedBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(manifestBuilder.ToString()));
        var expectedHex = Convert.ToHexString(expectedBytes).ToLowerInvariant();

        var providedBytes = Encoding.UTF8.GetBytes(v1.ToLowerInvariant());
        var expectedHexBytes = Encoding.UTF8.GetBytes(expectedHex);
        return providedBytes.Length == expectedHexBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedHexBytes);
    }

    private async Task<MercadoPagoOAuthTokenResult> ParseOAuthTokenResponseAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = ExtractErrorMessage(raw) ?? $"Mercado Pago OAuth retornou HTTP {(int)response.StatusCode}.";
            return new MercadoPagoOAuthTokenResult(false, null, null, 0, null, null, err);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var atProp) ? atProp.GetString() : null;
            var refreshToken = root.TryGetProperty("refresh_token", out var rtProp) ? rtProp.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var exProp) && exProp.TryGetInt32(out var secs)
                ? secs
                : 21600;
            var userId = root.TryGetProperty("user_id", out var uidProp) ? uidProp.ToString() : null;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new MercadoPagoOAuthTokenResult(false, null, refreshToken, 0, userId, null,
                    "Resposta OAuth não retornou access_token.");
            }

            var accountLabel = await TryFetchMerchantAccountLabelAsync(accessToken, ct)
                               ?? (string.IsNullOrWhiteSpace(userId) ? "Conta Mercado Pago" : $"Mercado Pago #{userId}");

            return new MercadoPagoOAuthTokenResult(
                true, accessToken, refreshToken, expiresIn, userId, accountLabel, null);
        }
        catch (Exception ex)
        {
            return new MercadoPagoOAuthTokenResult(false, null, null, 0, null, null,
                $"Erro ao processar token Mercado Pago: {ex.Message}");
        }
    }

    private async Task<string?> TryFetchMerchantAccountLabelAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsersMeEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return null;

            var raw = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var email = root.TryGetProperty("email", out var em) ? em.GetString() : null;
            var nickname = root.TryGetProperty("nickname", out var nn) ? nn.GetString() : null;
            var firstName = root.TryGetProperty("first_name", out var fn) ? fn.GetString() : null;
            var lastName = root.TryGetProperty("last_name", out var ln) ? ln.GetString() : null;
            var fullName = $"{firstName} {lastName}".Trim();
            var displayName = !string.IsNullOrWhiteSpace(fullName)
                ? fullName
                : string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();

            if (!string.IsNullOrWhiteSpace(email))
                return string.IsNullOrWhiteSpace(displayName) ? email.Trim() : $"{displayName} ({email.Trim()})";
            return displayName;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeOrderSlug(string orderNumber)
    {
        var chars = (orderNumber ?? "pedido").Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? "pedido" : new string(chars).ToLowerInvariant();
    }

    private static string? ExtractErrorMessage(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var msg) && !string.IsNullOrWhiteSpace(msg.GetString()))
                return msg.GetString();
            if (root.TryGetProperty("error_description", out var ed) && !string.IsNullOrWhiteSpace(ed.GetString()))
                return ed.GetString();
            if (root.TryGetProperty("error", out var er) && !string.IsNullOrWhiteSpace(er.GetString()))
                return er.GetString();
        }
        catch
        {
        }
        return rawJson.Length > 180 ? rawJson[..180] : rawJson;
    }
}
