using System.ComponentModel.DataAnnotations;

namespace ClothingStore.Shared.DTOs;

public sealed record LoginRequest(
    [Required(ErrorMessage = "O e-mail é obrigatório.")]
    [EmailAddress(ErrorMessage = "E-mail inválido.")]
    string Email,

    [Required(ErrorMessage = "A senha é obrigatória.")]
    string Password);

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    EmployeeProfileDto Employee);

public sealed record ForgotPasswordRequest(
    [Required(ErrorMessage = "O e-mail é obrigatório.")]
    [EmailAddress(ErrorMessage = "E-mail inválido.")]
    string Email);

public sealed record ForgotPasswordResponse(
    string Message,
    string? DemoResetCode = null);

public sealed record ResetPasswordRequest(
    [Required] string Email,
    [Required] string ResetToken,
    [Required, MinLength(6, ErrorMessage = "A nova senha deve ter no mínimo 6 caracteres.")]
    string NewPassword);

public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(6, ErrorMessage = "A nova senha deve ter no mínimo 6 caracteres.")]
    string NewPassword);

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
    bool IsActive)
{
    public bool IsAdmin => string.Equals(RoleName, "Admin", StringComparison.OrdinalIgnoreCase);
}

