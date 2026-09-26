using FieldReport.Api.Auth;
using FieldReport.Api.Contracts;
using FieldReport.Api.Data;
using FieldReport.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FieldReport.Api.Controllers;

/// <summary>Logowanie technika (F2).</summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimiting.AuthPolicy)]
[Produces("application/json")]
public sealed class AuthController(TokenService tokens, DataStore store, IOptionsMonitor<AuthOptions> options) : ControllerBase
{
    /// <summary>Logowanie: dowolny poprawny e-mail + PIN 1234.</summary>
    /// <remarks>
    /// Każdy e-mail to osobny technik (osobne zgłoszenia i urządzenia). Imię technika jest wyprowadzane z e-maila
    /// (`jan.kowalski@firma.pl` → „Jan Kowalski”). Błędny e-mail lub PIN → 401 „Nieprawidłowy e-mail lub PIN”.
    ///
    ///     POST /api/auth/login
    ///     { "email": "jan.kowalski@firma.pl", "pin": "1234" }
    /// </remarks>
    /// <response code="200">Zalogowano — tokeny i imię technika.</response>
    /// <response code="401">Nieprawidłowy e-mail lub PIN.</response>
    /// <response code="429">Za dużo prób — spróbuj za chwilę.</response>
    [HttpPost("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public ActionResult<TokenResponse> Login([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] LoginRequest? request)
    {
        var email = request?.Email?.Trim();
        if (!Technicians.IsValidEmail(email) || request?.Pin?.Trim() != options.CurrentValue.Pin)
        {
            return Problem(title: "Nieprawidłowy e-mail lub PIN", detail: "Podaj poprawny adres e-mail i PIN 1234.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var name = store.GetTechnicianName(email!) ?? Technicians.NameFromEmail(email!);
        store.EnsureTechnician(email!, name);
        return tokens.Issue(email!, name);
    }

    /// <summary>Odświeżenie tokenu: refresh token → nowa para tokenów.</summary>
    /// <remarks>
    /// Refresh token jest jednorazowy (rotacja): po użyciu zapisz nowy z odpowiedzi. Wygasły, użyty
    /// lub nieznany token → 401 (aplikacja przechodzi do logowania).
    /// </remarks>
    /// <response code="200">Nowe tokeny.</response>
    /// <response code="401">Refresh token nieprawidłowy lub wygasły.</response>
    [HttpPost("refresh")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public ActionResult<TokenResponse> Refresh([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshRequest? request)
    {
        var email = string.IsNullOrWhiteSpace(request?.RefreshToken) ? null : store.ConsumeRefreshToken(request.RefreshToken.Trim());
        if (email is null)
        {
            return Problem(title: "Sesja wygasła", detail: "Refresh token jest nieprawidłowy, wygasł lub został już użyty. Zaloguj się ponownie.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var name = store.GetTechnicianName(email) ?? Technicians.NameFromEmail(email);
        return tokens.Issue(email, name);
    }
}
