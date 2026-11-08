namespace ClothingStore.Application.Interfaces;

/// <summary>
/// Serviço de envio de e-mails transacionais (código de entrada por e-mail e recuperação de acesso).
/// </summary>
public interface IEmailService
{
    Task SendSecurityCodeEmailAsync(
        string recipientEmail,
        string subject,
        string securityCode,
        int validityMinutes,
        CancellationToken ct = default);
}

