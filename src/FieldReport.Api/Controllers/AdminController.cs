using System.Reflection;
using FieldReport.Api.Contracts;
using FieldReport.Api.Data;
using FieldReport.Api.Infrastructure;
using FieldReport.Api.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FieldReport.Api.Controllers;

/// <summary>Administracja.</summary>
[ApiController]
[Route("api/admin")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class AdminController(DataStore store, IOptionsMonitor<AdminOptions> options) : ControllerBase
{
    /// <summary>Reset danych (wszystkich albo jednego technika). Wymaga nagłówka <c>X-Admin-Key</c>.</summary>
    /// <remarks>
    /// Usuwa zgłoszenia, zdjęcia, urządzenia i sesje (refresh tokeny). Klucz: `Admin:Key` w konfiguracji
    /// albo zmienna środowiskowa `Admin__Key`. Bez skonfigurowanego klucza reset jest wyłączony (403).
    /// </remarks>
    /// <param name="adminKey">Klucz administratora.</param>
    /// <param name="technicianEmail" example="jan.kowalski@firma.pl">Opcjonalnie: tylko ten technik.</param>
    /// <response code="200">Wyczyszczono.</response>
    /// <response code="401">Brak lub zły klucz.</response>
    /// <response code="403">Reset wyłączony (brak Admin:Key na serwerze).</response>
    [HttpPost("reset")]
    [ProducesResponseType<ResetResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public ActionResult<ResetResult> Reset([FromHeader(Name = "X-Admin-Key")] string? adminKey, [FromQuery] string? technicianEmail)
    {
        var expected = options.CurrentValue.Key;
        if (string.IsNullOrEmpty(expected))
            return Problem(title: "Reset wyłączony", detail: "Na serwerze nie ustawiono Admin:Key.", statusCode: StatusCodes.Status403Forbidden);
        if (!Secrets.FixedTimeEquals(adminKey, expected))
            return Problem(title: "Brak dostępu", detail: "Nieprawidłowy lub brakujący nagłówek X-Admin-Key.", statusCode: StatusCodes.Status401Unauthorized);

        var email = string.IsNullOrWhiteSpace(technicianEmail) ? null : technicianEmail;
        var (reports, technicians) = store.Reset(email);
        return new ResetResult
        {
            RemovedReports = reports,
            RemovedTechnicians = technicians,
            Message = email is null ? "Usunięto wszystkie dane." : $"Usunięto dane technika {DataStore.Normalize(email)}."
        };
    }
}

/// <summary>Stan usługi.</summary>
[ApiController]
[Route("api/health")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class HealthController(IPushSender push, IOptionsMonitor<AuthOptions> auth, IHostEnvironment env, TimeProvider time) : ControllerBase
{
    public static readonly string Version =
        typeof(HealthController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    /// <summary>Wersja, czas serwera i czy Firebase jest skonfigurowany.</summary>
    /// <response code="200">Usługa działa.</response>
    [HttpGet]
    [HttpHead] // monitory dostępności i `curl -I`
    [ProducesResponseType<HealthDto>(StatusCodes.Status200OK)]
    public ActionResult<HealthDto> Get() => new HealthDto
    {
        Status = "ok",
        Version = Version,
        ServerTime = time.GetUtcNow(),
        FirebaseConfigured = push.IsConfigured,
        FirebaseStatus = push.StatusDescription,
        PathBase = Request.PathBase.Value ?? "",
        AccessTokenLifetimeSeconds = auth.CurrentValue.AccessTokenLifetimeSeconds,
        Environment = env.EnvironmentName
    };
}
