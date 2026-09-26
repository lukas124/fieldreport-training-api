using System.ComponentModel.DataAnnotations;

namespace FieldReport.Api.Contracts;

/// <summary>Priorytet zgłoszenia: <c>Low</c> = Niski, <c>Normal</c> = Normalny, <c>High</c> = Wysoki.</summary>
public enum ReportPriority
{
    Low,
    Normal,
    High
}

/// <summary>Status zgłoszenia: <c>New</c> = Nowe, <c>InProgress</c> = W trakcie, <c>Done</c> = Zakończone.</summary>
public enum ReportStatus
{
    New,
    InProgress,
    Done
}

/// <summary>Zgłoszenie serwisowe (spójne z <c>ReportDto</c> aplikacji).</summary>
public sealed class ReportDto
{
    /// <summary>Identyfikator nadawany przez aplikację (dla PUT musi być równy <c>{id}</c> z adresu lub pusty).</summary>
    /// <example>3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f</example>
    public Guid Id { get; set; }

    /// <summary>Tytuł (3–80 znaków).</summary>
    /// <example>Awaria pompy</example>
    [Required(ErrorMessage = "Tytuł jest wymagany.")]
    [StringLength(80, MinimumLength = 3, ErrorMessage = "Tytuł musi mieć od 3 do 80 znaków.")]
    public string Title { get; set; } = "";

    /// <summary>Opis (opcjonalny, maks. 500 znaków).</summary>
    /// <example>Pompa w węźle C2 nie startuje, słychać buczenie.</example>
    [StringLength(500, ErrorMessage = "Opis może mieć maksymalnie 500 znaków.")]
    public string? Description { get; set; }

    /// <summary>Priorytet: Low / Normal / High.</summary>
    /// <example>High</example>
    public ReportPriority Priority { get; set; } = ReportPriority.Normal;

    /// <summary>Status: New / InProgress / Done.</summary>
    /// <example>New</example>
    public ReportStatus Status { get; set; } = ReportStatus.New;

    /// <summary>Data utworzenia (ISO 8601). Przy pierwszym PUT brana z aplikacji (pusta = czas serwera), potem niezmienna.</summary>
    /// <example>2026-09-26T08:15:00+00:00</example>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Czas ostatniej zmiany na serwerze (UTC) — ustawia serwer; według niego działa <c>updatedSince</c>.</summary>
    /// <example>2026-09-26T08:20:31.1234567+00:00</example>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Czy na serwerze jest zdjęcie (tylko do odczytu — ustawia serwer).</summary>
    /// <example>false</example>
    public bool HasPhoto { get; set; }

    /// <summary>Pełny adres zdjęcia (<c>GET</c>, wymaga tokenu) albo null. Tylko do odczytu.</summary>
    /// <example>http://10.0.2.2:5080/api/reports/3f2c9a4e-8d1b-4c7a-9e55-0a1b2c3d4e5f/photo</example>
    public string? PhotoUrl { get; set; }
}

/// <summary>Dane logowania technika.</summary>
public sealed class LoginRequest
{
    /// <summary>Dowolny poprawny adres e-mail (każdy e-mail = osobny technik z osobnymi danymi).</summary>
    /// <example>jan.kowalski@firma.pl</example>
    public string? Email { get; set; }

    /// <summary>PIN — na szkoleniu zawsze <c>1234</c>.</summary>
    /// <example>1234</example>
    public string? Pin { get; set; }
}

/// <summary>Żądanie odświeżenia tokenu.</summary>
public sealed class RefreshRequest
{
    /// <summary>Refresh token z logowania lub poprzedniego odświeżenia (jednorazowy — po użyciu dostajesz nowy).</summary>
    /// <example>q8lV0m0Qm2yq3Zb7c6mJ0sQ0yXg1tY2m4Q5n8y6x0cA</example>
    public string? RefreshToken { get; set; }
}

/// <summary>Para tokenów (odpowiedź logowania i odświeżenia).</summary>
public sealed class TokenResponse
{
    /// <summary>JWT do nagłówka <c>Authorization: Bearer ...</c>.</summary>
    /// <example>eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...</example>
    public string AccessToken { get; set; } = "";

    /// <summary>Jednorazowy token do <c>POST /api/auth/refresh</c>.</summary>
    /// <example>q8lV0m0Qm2yq3Zb7c6mJ0sQ0yXg1tY2m4Q5n8y6x0cA</example>
    public string RefreshToken { get; set; } = "";

    /// <summary>Ważność access tokenu w sekundach.</summary>
    /// <example>600</example>
    public int ExpiresIn { get; set; }

    /// <summary>Imię i nazwisko technika wyprowadzone z e-maila.</summary>
    /// <example>Jan Kowalski</example>
    public string TechnicianName { get; set; } = "";
}

/// <summary>Rejestracja urządzenia do push.</summary>
public sealed class RegisterDeviceRequest
{
    /// <summary>Token rejestracyjny FCM z urządzenia.</summary>
    /// <example>fXk3...:APA91bH...</example>
    public string? Token { get; set; }

    /// <summary>Platforma: <c>android</c> lub <c>ios</c>.</summary>
    /// <example>android</example>
    public string? Platform { get; set; }
}

/// <summary>Zarejestrowane urządzenie.</summary>
public sealed class DeviceDto
{
    /// <example>fXk3...:APA91bH...</example>
    public string Token { get; set; } = "";

    /// <example>android</example>
    public string Platform { get; set; } = "";

    /// <example>2026-09-26T08:00:00+00:00</example>
    public DateTimeOffset RegisteredAt { get; set; }
}

/// <summary>Nowe zgłoszenie od dyspozytora.</summary>
public sealed class DispatcherCreateReportRequest
{
    /// <summary>E-mail technika. Pusty = wszyscy technicy, którzy się kiedykolwiek zalogowali (każdy dostaje osobne zgłoszenie).</summary>
    /// <example>jan.kowalski@firma.pl</example>
    public string? TechnicianEmail { get; set; }

    /// <summary>Tytuł (3–80 znaków).</summary>
    /// <example>Awaria pompy</example>
    [Required(ErrorMessage = "Tytuł jest wymagany.")]
    [StringLength(80, MinimumLength = 3, ErrorMessage = "Tytuł musi mieć od 3 do 80 znaków.")]
    public string Title { get; set; } = "";

    /// <summary>Opis (opcjonalny, maks. 500 znaków).</summary>
    /// <example>Klient zgłasza wyciek w kotłowni, budynek B.</example>
    [StringLength(500, ErrorMessage = "Opis może mieć maksymalnie 500 znaków.")]
    public string? Description { get; set; }

    /// <summary>Priorytet: Low / Normal / High.</summary>
    /// <example>High</example>
    public ReportPriority Priority { get; set; } = ReportPriority.Normal;
}

/// <summary>Zmiana statusu przez dyspozytora.</summary>
public sealed class DispatcherStatusRequest
{
    /// <summary>Nowy status: New / InProgress / Done (dyspozytor może ustawić dowolny).</summary>
    /// <example>InProgress</example>
    [Required(ErrorMessage = "Status jest wymagany.")]
    public ReportStatus? Status { get; set; }
}

/// <summary>Zgłoszenie wraz z technikiem (widok dyspozytora).</summary>
public sealed class DispatcherReportDto
{
    /// <example>jan.kowalski@firma.pl</example>
    public string TechnicianEmail { get; set; } = "";

    public ReportDto Report { get; set; } = new();
}

/// <summary>Wynik akcji dyspozytora.</summary>
public sealed class DispatcherResult
{
    /// <summary>Utworzone lub zmienione zgłoszenia.</summary>
    public List<DispatcherReportDto> Reports { get; set; } = [];

    /// <summary>Podsumowanie wysyłki push.</summary>
    public PushSummary Push { get; set; } = new();
}

/// <summary>Podsumowanie wysyłki push.</summary>
public sealed class PushSummary
{
    /// <summary>Typ powiadomienia (pole <c>type</c> w data).</summary>
    /// <example>report_assigned</example>
    public string Type { get; set; } = "";

    /// <summary>Czy Firebase jest skonfigurowany.</summary>
    /// <example>true</example>
    public bool FirebaseConfigured { get; set; }

    /// <summary>true = nic nie wysłano, bo Firebase nie jest skonfigurowany.</summary>
    /// <example>false</example>
    public bool Skipped { get; set; }

    /// <summary>Liczba urządzeń, do których wysłano / wysłano by powiadomienie.</summary>
    /// <example>2</example>
    public int TargetDevices { get; set; }

    /// <example>2</example>
    public int Sent { get; set; }

    /// <example>0</example>
    public int Failed { get; set; }

    /// <summary>Usunięte nieaktualne tokeny (FCM: <c>Unregistered</c>).</summary>
    /// <example>0</example>
    public int RemovedTokens { get; set; }

    /// <summary>Opis wyniku po polsku.</summary>
    /// <example>Wysłano 2 z 2 powiadomień.</example>
    public string Message { get; set; } = "";

    /// <summary>Błędy poszczególnych wysyłek (skrócone tokeny).</summary>
    public List<string> Errors { get; set; } = [];
}

/// <summary>Urządzenia technika (widok dyspozytora; tokeny skrócone).</summary>
public sealed class DispatcherTechnicianDto
{
    /// <example>jan.kowalski@firma.pl</example>
    public string Email { get; set; } = "";

    /// <example>Jan Kowalski</example>
    public string Name { get; set; } = "";

    /// <example>3</example>
    public int ReportCount { get; set; }

    public List<DeviceDto> Devices { get; set; } = [];
}

/// <summary>Stan usługi.</summary>
public sealed class HealthDto
{
    /// <example>ok</example>
    public string Status { get; set; } = "ok";

    /// <example>1.0.0</example>
    public string Version { get; set; } = "";

    /// <example>2026-09-26T08:00:00+00:00</example>
    public DateTimeOffset ServerTime { get; set; }

    /// <example>true</example>
    public bool FirebaseConfigured { get; set; }

    /// <summary>Opis konfiguracji Firebase (projekt albo powód braku).</summary>
    /// <example>Firebase skonfigurowany (projekt: fieldreport-training).</example>
    public string FirebaseStatus { get; set; } = "";

    /// <example>/maui/podstawowe</example>
    public string PathBase { get; set; } = "";

    /// <example>600</example>
    public int AccessTokenLifetimeSeconds { get; set; }

    /// <example>Production</example>
    public string Environment { get; set; } = "";
}

/// <summary>Wynik resetu danych.</summary>
public sealed class ResetResult
{
    /// <example>12</example>
    public int RemovedReports { get; set; }

    /// <example>3</example>
    public int RemovedTechnicians { get; set; }

    /// <example>Usunięto wszystkie dane.</example>
    public string Message { get; set; } = "";
}
