using FieldReport.Api.Auth;
using FieldReport.Api.Contracts;
using FieldReport.Api.Data;
using FieldReport.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace FieldReport.Api.Controllers;

/// <summary>Zgłoszenia zalogowanego technika (F6).</summary>
[ApiController]
[Route("api/reports")]
[Authorize]
[Produces("application/json")]
public sealed class ReportsController(DataStore store, PhotoUrlSigner photoUrls, IOptions<StorageOptions> storage) : ControllerBase
{
    /// <summary>Wszystkie zgłoszenia zalogowanego technika, od ostatnio zmienionego.</summary>
    /// <response code="200">Lista zgłoszeń (może być pusta).</response>
    [HttpGet]
    [ProducesResponseType<List<ReportDto>>(StatusCodes.Status200OK)]
    public ActionResult<List<ReportDto>> GetAll() =>
        store.GetReports(User.Email()).Select(r => r.ToDto(Request)).ToList();

    /// <summary>Szczegóły zgłoszenia.</summary>
    /// <param name="id" example="3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f">Identyfikator zgłoszenia.</param>
    /// <response code="200">Zgłoszenie.</response>
    /// <response code="404">Brak zgłoszenia (u tego technika).</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public ActionResult<ReportDto> Get(Guid id) =>
        store.GetReport(User.Email(), id) is { } r ? r.ToDto(Request) : NotFoundProblem();

    /// <summary>Utworzenie lub aktualizacja zgłoszenia (idempotentny upsert, <c>id</c> od aplikacji).</summary>
    /// <remarks>
    /// Ponowienie tego samego `PUT` nie tworzy duplikatu — ten sam `id` zawsze nadpisuje ten sam rekord.
    /// `id` w treści musi być równe `id` z adresu (albo puste). Serwer ustawia `updatedAt`, `hasPhoto` i `photoUrl`;
    /// `createdAt` jest brany z pierwszego zapisu. Tytuł jest przycinany (Trim) przed walidacją 3–80 znaków.
    /// Status: serwer NIE pilnuje kolejności przejść — zapisuje stan z urządzenia (reguła „tylko do przodu” jest w aplikacji).
    ///
    ///     PUT /api/reports/3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f
    ///     { "id": "3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f", "title": "Awaria pompy", "description": "Nie startuje",
    ///       "priority": "High", "status": "New", "createdAt": "2026-09-26T08:15:00+00:00" }
    /// </remarks>
    /// <response code="201">Utworzono nowe zgłoszenie.</response>
    /// <response code="200">Zaktualizowano istniejące zgłoszenie.</response>
    /// <response code="400">Błędne dane (walidacja, niezgodne id).</response>
    /// <response code="409">Nowe zgłoszenie przekroczyłoby limit zgłoszeń na technika (`Storage:MaxReportsPerTechnician`, domyślnie 500). Aktualizacja istniejącego nie zwraca 409.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<ReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ReportDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public ActionResult<ReportDto> Upsert(Guid id, [FromBody] ReportDto report)
    {
        if (id == Guid.Empty)
            return Problem(title: "Niepoprawny identyfikator", detail: "id nie może być pustym Guid.", statusCode: StatusCodes.Status400BadRequest);
        if (report.Id != Guid.Empty && report.Id != id)
            return Problem(title: "Niezgodny identyfikator", detail: "id w treści musi być równe id w adresie.", statusCode: StatusCodes.Status400BadRequest);

        var (outcome, stored) = store.Upsert(User.Email(), User.Name(), id, report);
        return outcome switch
        {
            UpsertOutcome.Created => CreatedAtAction(nameof(Get), new { id }, stored!.ToDto(Request)),
            UpsertOutcome.Updated => Ok(stored!.ToDto(Request)),
            _ => Problem(title: "Limit zgłoszeń", detail: $"Osiągnięto limit {storage.Value.MaxReportsPerTechnician} zgłoszeń na technika.",
                statusCode: StatusCodes.Status409Conflict)
        };
    }

    /// <summary>Wysłanie zdjęcia do zgłoszenia (multipart/form-data, pole <c>photo</c>).</summary>
    /// <remarks>
    /// Zgłoszenie musi już istnieć (najpierw `PUT /api/reports/{id}`). Obsługiwane formaty: JPEG, PNG, WebP, HEIC
    /// (rozpoznawane po zawartości). Limit rozmiaru: `Storage:MaxPhotoBytes` (domyślnie 5 MB). Ponowne wysłanie nadpisuje zdjęcie.
    ///
    ///     curl -X PUT -H "Authorization: Bearer $TOKEN" -F "photo=@usterka.jpg" http://localhost:5080/api/reports/{id}/photo
    /// </remarks>
    /// <param name="id" example="3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f">Identyfikator zgłoszenia.</param>
    /// <param name="photo">Plik zdjęcia.</param>
    /// <param name="ct">Anulowanie.</param>
    /// <response code="200">Zapisano — zgłoszenie z <c>hasPhoto = true</c>.</response>
    /// <response code="400">Brak pliku.</response>
    /// <response code="404">Brak zgłoszenia.</response>
    /// <response code="413">Plik za duży.</response>
    /// <response code="415">To nie jest obsługiwany obraz.</response>
    [HttpPut("{id:guid}/photo")]
    [Consumes("multipart/form-data")]
    [PhotoSizeLimit]
    [ProducesResponseType<ReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult<ReportDto>> UploadPhoto(Guid id, IFormFile? photo, CancellationToken ct)
    {
        if (photo is null || photo.Length == 0)
            return Problem(title: "Brak pliku", detail: "Wyślij zdjęcie jako multipart/form-data w polu 'photo'.", statusCode: StatusCodes.Status400BadRequest);
        if (photo.Length > storage.Value.MaxPhotoBytes)
            return TooLarge(storage.Value.MaxPhotoBytes);

        var email = User.Email();
        if (store.GetReport(email, id) is null) return NotFoundProblem();

        await using var stream = photo.OpenReadStream();
        var header = new byte[16];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (ImageSniffer.Detect(header.AsSpan(0, read)) is not { } format)
            return Problem(title: "Nieobsługiwany format", detail: "Dozwolone: JPEG, PNG, WebP, HEIC.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        stream.Position = 0;

        var saved = await store.SavePhotoAsync(email, id, stream, format.Extension, format.ContentType, ct);
        return saved is null ? NotFoundProblem() : saved.ToDto(Request);
    }

    /// <summary>Pobranie zdjęcia zgłoszenia: podpisanym adresem z <c>photoUrl</c> (bez tokenu) albo z tokenem Bearer.</summary>
    /// <remarks>
    /// `photoUrl` z odpowiedzi API zawiera `tech`, `exp` i `sig` — działa bez nagłówka Authorization
    /// (np. `Image Source="{Binding PhotoUrl}"`) do czasu `exp` (domyślnie 60 min od pobrania zgłoszenia).
    /// Po wygaśnięciu pobierz zgłoszenie ponownie, żeby dostać nowy adres.
    /// </remarks>
    /// <param name="id" example="3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f">Identyfikator zgłoszenia.</param>
    /// <param name="tech">Klucz technika z podpisanego adresu.</param>
    /// <param name="exp">Czas wygaśnięcia adresu (sekundy od 1970-01-01 UTC).</param>
    /// <param name="sig">Podpis adresu.</param>
    /// <response code="200">Plik obrazu.</response>
    /// <response code="401">Brak podpisu i brak tokenu.</response>
    /// <response code="403">Podpis nieprawidłowy albo adres wygasł.</response>
    /// <response code="404">Brak zgłoszenia lub zdjęcia.</response>
    [HttpGet("{id:guid}/photo")]
    [AllowAnonymous]
    [Produces("image/jpeg", "image/png", "image/webp", "image/heic", "application/problem+json")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult GetPhoto(Guid id, [FromQuery] string? tech, [FromQuery] long? exp, [FromQuery] string? sig)
    {
        if (sig is not null)
        {
            // podpisany adres: technika i zdjęcie wskazuje podpis, token nie jest potrzebny
            if (tech is null || exp is null || !photoUrls.IsValid(tech, id, exp.Value, sig))
                return Problem(title: "Nieprawidłowy adres zdjęcia", detail: "Podpis jest błędny albo adres wygasł — pobierz zgłoszenie ponownie.",
                    statusCode: StatusCodes.Status403Forbidden);
            var file = store.FindPhotoFile(tech, id);
            return file is null ? NotFoundProblem("Zgłoszenie nie ma zdjęcia.") : PhysicalFile(file, ImageSniffer.ContentTypeFor(file));
        }

        // bez podpisu: zwykłe uwierzytelnienie tokenem
        if (User.Identity?.IsAuthenticated != true) return Challenge();
        var email = User.Email();
        var report = store.GetReport(email, id);
        if (report?.PhotoFile is null) return NotFoundProblem("Zgłoszenie nie ma zdjęcia.");
        var path = store.PhotoPath(email, report.PhotoFile);
        return System.IO.File.Exists(path)
            ? PhysicalFile(path, report.PhotoContentType ?? "application/octet-stream")
            : NotFoundProblem("Zgłoszenie nie ma zdjęcia.");
    }

    private ObjectResult NotFoundProblem(string detail = "Nie znaleziono zgłoszenia o tym id (u zalogowanego technika).") =>
        Problem(title: "Nie znaleziono", detail: detail, statusCode: StatusCodes.Status404NotFound);

    private ObjectResult TooLarge(long max) =>
        Problem(title: "Plik za duży", detail: $"Maksymalny rozmiar zdjęcia to {max / 1024 / 1024.0:0.#} MB.",
            statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// Filtr zasobu (przed bindowaniem formularza): odrzuca za duże żądanie po Content-Length i ustawia
    /// limit treści żądania w Kestrelu na rozmiar zdjęcia + zapas na nagłówki multipart.
    /// </summary>
    private sealed class PhotoSizeLimitAttribute : Attribute, IResourceFilter
    {
        private const long MultipartOverhead = 64 * 1024;

        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            var max = context.HttpContext.RequestServices.GetRequiredService<IOptions<StorageOptions>>().Value.MaxPhotoBytes + MultipartOverhead;
            var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = max;
            if (context.HttpContext.Request.ContentLength > max)
            {
                var problem = new ProblemDetails
                {
                    Title = "Plik za duży",
                    Detail = $"Maksymalny rozmiar zdjęcia to {(max - MultipartOverhead) / 1024 / 1024.0:0.#} MB.",
                    Status = StatusCodes.Status413PayloadTooLarge
                };
                context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status413PayloadTooLarge };
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context) { }
    }
}
