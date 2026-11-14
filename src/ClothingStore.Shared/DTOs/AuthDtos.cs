using System.ComponentModel.DataAnnotations;

namespace ClothingStore.Shared.DTOs;

public sealed record AuthSetupStatusDto(
    bool RequiresInitialSetup);

public sealed record RegisterInitialAdminRequest(
    [Required(ErrorMessage = "Informe seu nome completo.")]
    string FullName,

    [Required(ErrorMessage = "Informe seu Gmail ou e-mail.")]
    string Email,

    [Required(ErrorMessage = "Informe uma senha.")]
    [MinLength(4, ErrorMessage = "A senha deve ter no mínimo 4 caracteres.")]
    string Password,

    [Required(ErrorMessage = "Informe o nome da sua loja.")]
    string StoreName,

    string? WhatsappNumber = null,
    string? Username = null);

public sealed record RegisterUserAccountRequest(
    [Required(ErrorMessage = "Informe seu nome completo.")]
    string FullName,

    [Required(ErrorMessage = "Informe seu Gmail ou e-mail.")]
    string Email,

    [Required(ErrorMessage = "Informe uma senha.")]
    [MinLength(4, ErrorMessage = "A senha deve ter no mínimo 4 caracteres.")]
    string Password,

    string? Phone = null,
    string? Username = null);

public sealed record LoginRequest(
    [Required(ErrorMessage = "O login é obrigatório.")]
    string Email,

    [Required(ErrorMessage = "A senha é obrigatória.")]
    string Password);

public sealed record LoginChallengeResponse(
    bool RequiresTwoFactor,
    string? TempSessionEmail,
    LoginResponse? AuthResult,
    string? ChallengeToken = null);

public sealed record VerifyTwoFactorLoginRequest(
    [Required(ErrorMessage = "O e-mail é obrigatório.")]
    string Email,

    [Required(ErrorMessage = "O código de 6 dígitos é obrigatório.")]
    string Code,

    string? ChallengeToken = null);

public sealed record RequestEmailLoginCodeRequest(
    [Required(ErrorMessage = "O login ou e-mail é obrigatório.")]
    string Email);

public sealed record RequestEmailLoginCodeResponse(
    string Message);

public sealed record VerifyEmailLoginCodeRequest(
    [Required(ErrorMessage = "O login ou e-mail é obrigatório.")]
    string Email,

    [Required(ErrorMessage = "O código de 6 dígitos é obrigatório.")]
    string Code);

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    EmployeeProfileDto Employee);

public sealed record ForgotPasswordRequest(
    [Required(ErrorMessage = "Informe o login ou e-mail.")]
    string Email);

public sealed record ForgotPasswordResponse(
    string Message);

public sealed record ResetPasswordRequest(
    [Required] string Email,
    [Required] string ResetToken,
    [Required, MinLength(4, ErrorMessage = "A nova senha deve ter no mínimo 4 caracteres.")]
    string NewPassword);

public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(4, ErrorMessage = "A nova senha deve ter no mínimo 4 caracteres.")]
    string NewPassword);

public sealed record UpdateUserProfileRequest(
    [Required(ErrorMessage = "Informe o nome completo.")]
    string FullName,

    [Required(ErrorMessage = "Informe o e-mail.")]
    string Email,

    string? Phone = null,
    string? Username = null);

public sealed record SetSecurityPinRequest(
    [Required(ErrorMessage = "Informe sua senha atual.")]
    string CurrentPassword,

    [Required(ErrorMessage = "Informe um PIN numérico de 4 dígitos.")]
    [RegularExpression(@"^\d{4}$", ErrorMessage = "O PIN deve conter exatamente 4 números.")]
    string Pin);

public sealed record RemoveSecurityPinRequest(
    [Required(ErrorMessage = "Informe sua senha atual.")]
    string CurrentPassword);

public sealed record TwoFactorSetupDto(
    string SecretKey,
    string OtpAuthUri,
    IReadOnlyList<string> BackupCodes);

public sealed record EnableTwoFactorRequest(
    [Required(ErrorMessage = "A chave secreta é obrigatória.")]
    string SecretKey,

    [Required(ErrorMessage = "Informe o código de 6 dígitos do aplicativo autenticador.")]
    string VerificationCode,

    IReadOnlyList<string>? BackupCodes = null);

public sealed record DisableTwoFactorRequest(
    [Required(ErrorMessage = "Informe sua senha atual para desativar a autenticação de 2 fatores.")]
    string CurrentPassword);

public sealed record EmployeeProfileDto(
    Guid EmployeeId,
    Guid UserId,
    string FullName,
    string Email,
    string RoleName,
    string? JobTitle,
    string? Phone,
    bool CanManageProducts,
    bool CanManageStock,
    bool IsActive,
    bool HasTwoFactorEnabled = false,
    bool HasSecurityPin = false,
    string? Username = null)
{
    public bool IsAdmin => string.Equals(RoleName, "Admin", StringComparison.OrdinalIgnoreCase);
    public string EffectiveUsername => !string.IsNullOrWhiteSpace(Username) ? Username : Email;
}

public sealed record ApiCompatibilityStatusDto(
    string ApiVersion,
    string MinSupportedAppVersion,
    string LatestAppVersion,
    bool UpdateRecommended,
    string? ClientAppVersion,
    string ReleaseUrl,
    string Message);

public sealed record PlatformAccountSyncDto(
    Guid UserId,
    Guid EmployeeId,
    string FullName,
    string Username,
    string? Email,
    string RoleName,
    string? JobTitle,
    string? Phone,
    bool IsActive,
    bool TwoFactorEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    string? StoreName,
    string? StoreSlug,
    string? OnlineBaseUrl,
    string PlanTier,
    DateTimeOffset? PlanExpiresAt);

public sealed record PlatformAccountsSyncEnvelopeDto(
    string ServerVersion,
    DateTimeOffset ServerTimeUtc,
    string StoreName,
    string PlanTier,
    DateTimeOffset? PlanExpiresAt,
    string? OnlineBaseUrl,
    IReadOnlyList<PlatformAccountSyncDto> Accounts);

public sealed record RemoteApplyVipRequest(
    string PlanTier,
    int? DurationDays = 30,
    bool ExtendExistingDuration = false,
    bool ActivateSite = true,
    string? StoreName = null,
    string? TargetUsername = null,
    string? TargetEmail = null);

public sealed record RemoteToggleUserActiveResponse(
    Guid UserId,
    Guid EmployeeId,
    bool IsActive,
    string Message);
