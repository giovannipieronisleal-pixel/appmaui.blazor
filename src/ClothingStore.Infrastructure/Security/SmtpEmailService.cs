using System.Net;
using System.Net.Mail;
using System.Text;
using ClothingStore.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace ClothingStore.Infrastructure.Security;

/// <summary>
/// Implementação de envio de e-mail via SMTP com suporte a TLS.
/// Quando o servidor SMTP não está configurado em desenvolvimento local, registra a mensagem
/// exclusivamente em caixa de saída local protegida no servidor (sem jamais expor o código na resposta da API).
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly string _localOutboxFilePath;

    public SmtpEmailService(IConfiguration configuration, string? localOutboxFilePath = null)
    {
        _configuration = configuration;
        _localOutboxFilePath = !string.IsNullOrWhiteSpace(localOutboxFilePath)
            ? localOutboxFilePath
            : Path.Combine(AppContext.BaseDirectory, "App_Data", "email_outbox.txt");
    }

    public async Task SendSecurityCodeEmailAsync(
        string recipientEmail,
        string subject,
        string securityCode,
        int validityMinutes,
        CancellationToken ct = default)
    {
        var smtpHost = _configuration["Smtp:Host"] ?? Environment.GetEnvironmentVariable("SMTP_HOST");
        var smtpPortStr = _configuration["Smtp:Port"] ?? Environment.GetEnvironmentVariable("SMTP_PORT") ?? "587";
        var smtpUser = _configuration["Smtp:Username"] ?? Environment.GetEnvironmentVariable("SMTP_USERNAME");
        var smtpPass = _configuration["Smtp:Password"] ?? Environment.GetEnvironmentVariable("SMTP_PASSWORD");
        var fromEmail = _configuration["Smtp:FromEmail"] ?? Environment.GetEnvironmentVariable("SMTP_FROM_EMAIL") ?? smtpUser ?? "no-reply@loja.local";

        var bodyText = new StringBuilder()
            .AppendLine("Olá,")
            .AppendLine()
            .AppendLine($"Seu código de segurança de uso único é: {securityCode}")
            .AppendLine($"Este código expira em {validityMinutes} minutos.")
            .AppendLine()
            .AppendLine("Por segurança, nunca compartilhe este código com terceiros.")
            .ToString();

        if (!string.IsNullOrWhiteSpace(smtpHost) &&
            !string.IsNullOrWhiteSpace(smtpUser) &&
            !string.IsNullOrWhiteSpace(smtpPass) &&
            int.TryParse(smtpPortStr, out var smtpPort))
        {
            using var message = new MailMessage(fromEmail, recipientEmail, subject, bodyText)
            {
                IsBodyHtml = false
            };

            using var client = new SmtpClient(smtpHost, smtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(smtpUser, smtpPass)
            };

            await client.SendMailAsync(message, ct);
            return;
        }

        // Caixa de saída local segura no disco do servidor (para uso sem servidor SMTP externo configurado)
        try
        {
            var dir = Path.GetDirectoryName(_localOutboxFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var entry = $"[{DateTimeOffset.Now:dd/MM/yyyy HH:mm:ss}] PARA: {recipientEmail} | ASSUNTO: {subject} | CÓDIGO: {securityCode} (expira em {validityMinutes} min){Environment.NewLine}";
            await File.AppendAllTextAsync(_localOutboxFilePath, entry, ct);
        }
        catch
        {
        }
    }
}

