namespace FieldReport.Api.Infrastructure;

/// <summary>Sekcja <c>Auth</c>: logowanie i tokeny.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>PIN akceptowany dla każdego poprawnego e-maila.</summary>
    public string Pin { get; set; } = "1234";

    /// <summary>Czas życia access tokenu w sekundach (domyślnie 10 min; np. 60 do demo odświeżania po 401).</summary>
    public int AccessTokenLifetimeSeconds { get; set; } = 600;

    /// <summary>Czas życia refresh tokenu w dniach.</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    /// <summary>Klucz podpisu JWT (min. 32 znaki). Pusty = wygenerowany i zapisany w katalogu danych.</summary>
    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = "FieldReport.Api";
    public string Audience { get; set; } = "FieldReport.App";
}

/// <summary>Sekcja <c>Storage</c>: trwały zapis danych.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Katalog danych (JSON + zdjęcia). Ścieżka względna liczona od katalogu aplikacji.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Maksymalny rozmiar zdjęcia w bajtach (domyślnie 5 MB).</summary>
    public long MaxPhotoBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Limit zgłoszeń na technika (ochrona dysku na publicznym serwerze).</summary>
    public int MaxReportsPerTechnician { get; set; } = 500;

    /// <summary>Limit urządzeń na technika.</summary>
    public int MaxDevicesPerTechnician { get; set; } = 10;
}

/// <summary>Sekcja <c>Firebase</c>: wysyłka push.</summary>
public sealed class FirebaseOptions
{
    public const string Section = "Firebase";

    /// <summary>Ścieżka do pliku konta serwisowego. Pusta = zmienna <c>GOOGLE_APPLICATION_CREDENTIALS</c>.</summary>
    public string? CredentialsPath { get; set; }

    /// <summary>Wyłącza push niezależnie od poświadczeń (np. w testach).</summary>
    public bool Disabled { get; set; }
}

/// <summary>Sekcja <c>Admin</c>.</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Klucz dla <c>POST /api/admin/reset</c> (nagłówek <c>X-Admin-Key</c>). Pusty = reset wyłączony.</summary>
    public string? Key { get; set; }
}

/// <summary>Sekcja <c>Dispatcher</c>.</summary>
public sealed class DispatcherOptions
{
    public const string Section = "Dispatcher";

    /// <summary>Opcjonalny klucz wymagany w nagłówku <c>X-Dispatcher-Key</c>. Pusty = endpointy dyspozytora otwarte.</summary>
    public string? Key { get; set; }
}

/// <summary>Sekcja <c>RateLimiting</c> (limity na adres IP, okno 1 minuta).</summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimiting";

    public bool Enabled { get; set; } = true;
    public int PermitsPerMinute { get; set; } = 300;
    public int AuthPermitsPerMinute { get; set; } = 30;
    public int DispatcherPermitsPerMinute { get; set; } = 60;
}

/// <summary>Sekcja <c>ReverseProxy</c>.</summary>
public sealed class ReverseProxyOptions
{
    public const string Section = "ReverseProxy";

    /// <summary>Ufaj nagłówkom X-Forwarded-* z dowolnego adresu (proxy na tym samym hoście / w sieci wewnętrznej).</summary>
    public bool TrustAllProxies { get; set; } = true;

    /// <summary>Gdy <c>PathBase</c> jest pusty, a proxy wysyła <c>X-Forwarded-Prefix</c>, użyj go jako PathBase.</summary>
    public bool UseForwardedPrefix { get; set; } = true;
}

/// <summary>Sekcja <c>Hosting</c>: adres nasłuchu (gdy nie podano <c>--urls</c>/<c>ASPNETCORE_URLS</c>).</summary>
public sealed class HostingOptions
{
    public const string Section = "Hosting";

    public int Port { get; set; } = 5080;

    /// <summary>true = nasłuch na 0.0.0.0 (sieć sali), false = tylko localhost.</summary>
    public bool ListenOnAllInterfaces { get; set; }
}
