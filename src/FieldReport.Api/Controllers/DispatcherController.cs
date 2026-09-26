using FieldReport.Api.Auth;
using FieldReport.Api.Contracts;
using FieldReport.Api.Data;
using FieldReport.Api.Infrastructure;
using FieldReport.Api.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FieldReport.Api.Controllers;

/// <summary>Symulacja dyspozytora (poza aplikacją — używana ze Swaggera).</summary>
[ApiController]
[Route("api/dispatcher")]
[AllowAnonymous]
[EnableRateLimiting(RateLimiting.DispatcherPolicy)]
[Produces("application/json")]
public sealed class DispatcherController(DataStore store, PushService push, IOptionsMonitor<DispatcherOptions> options) : ControllerBase
{
    private const string KeyHeader = "X-Dispatcher-Key";

    /// <summary>Nowe zgłoszenie przypisane technikowi + push <c>report_assigned</c>.</summary>
    /// <remarks>
    /// `technicianEmail` pusty = zgłoszenie dla **każdego** technika, który się kiedykolwiek zalogował (każdy dostaje
    /// osobny rekord). Push: notification „Nowe zgłoszenie” / tytuł, data `{ "type": "report_assigned", "reportId": "…" }`,
    /// kanał Android `reports`. Bez skonfigurowanego Firebase zgłoszenie powstaje, a `push.skipped = true`
    /// (w `push.targetDevices` — ilu urządzeniom by wysłano).
    ///
    ///     POST /api/dispatcher/reports
    ///     { "technicianEmail": "jan.kowalski@firma.pl", "title": "Awaria pompy", "description": "Budynek B", "priority": "High" }
    /// </remarks>
    /// <param name="request">Dane zgłoszenia.</param>
    /// <param name="dispatcherKey">Wymagany tylko, gdy serwer ma ustawiony <c>Dispatcher:Key</c>.</param>
    /// <param name="ct">Anulowanie.</param>
    /// <response code="200">Utworzone zgłoszenia i wynik wysyłki push.</response>
    /// <response code="400">Błędne dane lub brak techników.</response>
    /// <response code="401">Wymagany/niepoprawny X-Dispatcher-Key.</response>
    [HttpPost("reports")]
    [ProducesResponseType<DispatcherResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DispatcherResult>> CreateReport(
        [FromBody] DispatcherCreateReportRequest request,
        [FromHeader(Name = KeyHeader)] string? dispatcherKey,
        CancellationToken ct)
    {
        if (CheckKey(dispatcherKey) is { } denied) return denied;

        List<string> emails;
        if (string.IsNullOrWhiteSpace(request.TechnicianEmail))
        {
            emails = store.GetTechnicians().Select(t => t.Email).ToList();
            if (emails.Count == 0)
                return Problem(title: "Brak techników", detail: "Nikt się jeszcze nie zalogował — podaj technicianEmail albo najpierw zaloguj się w aplikacji.",
                    statusCode: StatusCodes.Status400BadRequest);
        }
        else
        {
            if (!Technicians.IsValidEmail(request.TechnicianEmail))
                return Problem(title: "Niepoprawny e-mail", detail: "technicianEmail musi być poprawnym adresem e-mail (albo pusty = wszyscy).",
                    statusCode: StatusCodes.Status400BadRequest);
            emails = [DataStore.Normalize(request.TechnicianEmail)];
        }

        var result = new DispatcherResult();
        var targets = new List<(string, PushMessage)>();
        foreach (var email in emails)
        {
            var name = store.GetTechnicianName(email) ?? Technicians.NameFromEmail(email);
            var report = store.CreateByDispatcher(email, name, request.Title, request.Description, request.Priority);
            if (report is null) continue; // limit zgłoszeń technika
            result.Reports.Add(new DispatcherReportDto { TechnicianEmail = email, Report = report.ToDto(Request) });
            targets.Add((email, new PushMessage(PushTypes.ReportAssigned, report.Id, "Nowe zgłoszenie", report.Title)));
        }

        result.Push = await push.SendAsync(PushTypes.ReportAssigned, targets, ct);
        return result;
    }

    /// <summary>Zmiana statusu zgłoszenia + push <c>report_status_changed</c>.</summary>
    /// <remarks>
    /// Dyspozytor może ustawić dowolny status (serwer celowo nie pilnuje kolejności — aplikacja musi obsłużyć „cofnięty” status). Push: „Dyspozytor zmienił status” / tytuł,
    /// data `{ "type": "report_status_changed", "reportId": "…" }`. Identyfikator weź z aplikacji albo z `GET /api/dispatcher/reports`.
    ///
    ///     PATCH /api/dispatcher/reports/3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f/status
    ///     { "status": "InProgress" }
    /// </remarks>
    /// <param name="id" example="3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f">Identyfikator zgłoszenia.</param>
    /// <param name="request">Nowy status.</param>
    /// <param name="technicianEmail" example="jan.kowalski@firma.pl">Technik — wymagany tylko, gdy to samo id ma kilku techników (inaczej 409).</param>
    /// <param name="dispatcherKey">Wymagany tylko, gdy serwer ma ustawiony <c>Dispatcher:Key</c>.</param>
    /// <param name="ct">Anulowanie.</param>
    /// <response code="200">Zmienione zgłoszenie i wynik wysyłki push.</response>
    /// <response code="404">Brak zgłoszenia.</response>
    /// <response code="409">To samo id ma kilku techników — podaj technicianEmail.</response>
    [HttpPatch("reports/{id:guid}/status")]
    [ProducesResponseType<DispatcherResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DispatcherResult>> ChangeStatus(
        Guid id,
        [FromBody] DispatcherStatusRequest request,
        [FromQuery] string? technicianEmail,
        [FromHeader(Name = KeyHeader)] string? dispatcherKey,
        CancellationToken ct)
    {
        if (CheckKey(dispatcherKey) is { } denied) return denied;

        var matches = store.FindById(id);
        if (!string.IsNullOrWhiteSpace(technicianEmail))
            matches = matches.Where(m => m.Email == DataStore.Normalize(technicianEmail)).ToList();
        if (matches.Count == 0)
            return Problem(title: "Nie znaleziono", detail: "Brak zgłoszenia o tym id (u wskazanego technika).", statusCode: StatusCodes.Status404NotFound);
        if (matches.Count > 1)
            return Problem(title: "Niejednoznaczne id",
                detail: $"To id ma {matches.Count} techników — podaj ?technicianEmail=… (GET /api/dispatcher/reports pokaże, kto).",
                statusCode: StatusCodes.Status409Conflict);

        var result = new DispatcherResult();
        var targets = new List<(string, PushMessage)>();
        foreach (var (email, _) in matches)
        {
            var updated = store.SetStatus(email, id, request.Status!.Value);
            if (updated is null) continue;
            result.Reports.Add(new DispatcherReportDto { TechnicianEmail = email, Report = updated.ToDto(Request) });
            targets.Add((email, new PushMessage(PushTypes.ReportStatusChanged, id, "Dyspozytor zmienił status", updated.Title)));
        }

        result.Push = await push.SendAsync(PushTypes.ReportStatusChanged, targets, ct);
        return result;
    }

    /// <summary>Podgląd zgłoszeń (wszystkich albo jednego technika) — np. żeby znaleźć id do zmiany statusu.</summary>
    /// <param name="technicianEmail" example="jan.kowalski@firma.pl">E-mail technika; puste = wszyscy.</param>
    /// <param name="dispatcherKey">Wymagany tylko, gdy serwer ma ustawiony <c>Dispatcher:Key</c>.</param>
    [HttpGet("reports")]
    [ProducesResponseType<List<DispatcherReportDto>>(StatusCodes.Status200OK)]
    public ActionResult<List<DispatcherReportDto>> ListReports(
        [FromQuery] string? technicianEmail,
        [FromHeader(Name = KeyHeader)] string? dispatcherKey)
    {
        if (CheckKey(dispatcherKey) is { } denied) return denied;
        return store.GetAllReports(string.IsNullOrWhiteSpace(technicianEmail) ? null : technicianEmail)
            .Select(x => new DispatcherReportDto { TechnicianEmail = x.Email, Report = x.Report.ToDto(Request) })
            .ToList();
    }

    /// <summary>Technicy i ich zarejestrowane urządzenia (tokeny skrócone) — kontrola przed wysyłką push.</summary>
    /// <param name="dispatcherKey">Wymagany tylko, gdy serwer ma ustawiony <c>Dispatcher:Key</c>.</param>
    [HttpGet("technicians")]
    [ProducesResponseType<List<DispatcherTechnicianDto>>(StatusCodes.Status200OK)]
    public ActionResult<List<DispatcherTechnicianDto>> ListTechnicians([FromHeader(Name = KeyHeader)] string? dispatcherKey)
    {
        if (CheckKey(dispatcherKey) is { } denied) return denied;
        return store.GetTechnicians().Select(t => new DispatcherTechnicianDto
        {
            Email = t.Email,
            Name = t.Name,
            ReportCount = t.ReportCount,
            Devices = t.Devices.Select(d => new DeviceDto { Token = PushService.Short(d.Token), Platform = d.Platform, RegisteredAt = d.RegisteredAt }).ToList()
        }).ToList();
    }

    private ObjectResult? CheckKey(string? provided)
    {
        var expected = options.CurrentValue.Key;
        if (string.IsNullOrEmpty(expected) || Secrets.FixedTimeEquals(provided, expected)) return null;
        return Problem(title: "Brak dostępu", detail: $"Nagłówek {KeyHeader} jest wymagany na tym serwerze.", statusCode: StatusCodes.Status401Unauthorized);
    }
}
