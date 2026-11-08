using System.Security.Claims;
using System.Text.Encodings.Web;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ClothingStore.Api.Security;

/// <summary>
/// Handler de autenticação JWT que valida a assinatura do token e verifica em tempo real no banco
/// se o funcionário continua ativo e quais são suas permissões vigentes.
/// </summary>
public sealed class JwtBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "JwtBearer";

    private readonly IJwtTokenService _jwtTokenService;
    private readonly IClothingStoreRepository _repository;

    public JwtBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IJwtTokenService jwtTokenService,
        IClothingStoreRepository repository)
        : base(options, logger, encoder)
    {
        _jwtTokenService = jwtTokenService;
        _repository = repository;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        var authHeader = authHeaderValues.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var principal = _jwtTokenService.ValidateToken(token);
        if (principal is null)
        {
            return AuthenticateResult.Fail("Token JWT inválido ou expirado.");
        }

        var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
        {
            return AuthenticateResult.Fail("Identificador de usuário ausente no token.");
        }

        var user = await _repository.GetUserByIdAsync(userId, Context.RequestAborted);
        var employee = await _repository.GetEmployeeByUserIdAsync(userId, Context.RequestAborted);

        if (user is null || employee is null || !user.IsActive || !employee.IsActive)
        {
            return AuthenticateResult.Fail("Conta de funcionário desativada ou inexistente.");
        }

        var roleName = user.Role?.Name ?? RoleNames.Employee;

        // Reconstitui as claims com as permissões atuais do banco (garantindo revogação imediata)
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("employee_id", employee.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, employee.FullName),
            new(ClaimTypes.Role, roleName),
            new("can_manage_products", employee.HasProductManagementPermission(roleName) ? "true" : "false"),
            new("can_manage_stock", employee.HasStockManagementPermission(roleName) ? "true" : "false")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}

