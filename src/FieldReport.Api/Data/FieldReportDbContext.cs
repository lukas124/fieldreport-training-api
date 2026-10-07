using FieldReport.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldReport.Api.Data;

/// <summary>Technik = jeden adres e-mail (małe litery).</summary>
public sealed class Technician
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset LastSeenAt { get; set; }
    public List<Report> Reports { get; set; } = [];
    public List<Device> Devices { get; set; } = [];
}

/// <summary>Zgłoszenie technika. Klucz: (e-mail technika, id) — dwóch techników może mieć to samo id.</summary>
public sealed class Report
{
    public string TechnicianEmail { get; set; } = "";
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public ReportPriority Priority { get; set; }
    public ReportStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Nazwa pliku zdjęcia w katalogu zdjęć technika albo null.</summary>
    public string? PhotoFile { get; set; }
    public string? PhotoContentType { get; set; }
}

/// <summary>Urządzenie do push. Token jest unikalny — należy do technika, który zarejestrował go ostatni.</summary>
public sealed class Device
{
    public string Token { get; set; } = "";
    public string TechnicianEmail { get; set; } = "";
    public string Platform { get; set; } = "";
    public DateTimeOffset RegisteredAt { get; set; }
}

/// <summary>Refresh token zapisany jako skrót SHA-256 (sam token zna tylko aplikacja).</summary>
public sealed class RefreshToken
{
    public string Hash { get; set; } = "";
    public string TechnicianEmail { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Klucz idempotencji z nagłówka <c>Idempotency-Key</c> (POST /api/reports). Zapisywany razem z nowym zgłoszeniem.
/// Ten sam klucz od tego samego technika = powtórka tego samego żądania: serwer zwraca zapisany wynik
/// zamiast tworzyć drugie zgłoszenie.
/// </summary>
public sealed class IdempotencyKey
{
    public string TechnicianEmail { get; set; } = "";
    public string Key { get; set; } = "";

    /// <summary>
    /// Skrót SHA-256 pól ustalanych przy tworzeniu (tytuł, opis, priorytet, data utworzenia — bez statusu, który może
    /// się zmienić przed ponowieniem): ten sam klucz z inną treścią to błąd klienta (422).
    /// </summary>
    public string RequestHash { get; set; } = "";

    /// <summary>Wynik pierwszego żądania: Id utworzonego zgłoszenia (powtórka zwraca to zgłoszenie).</summary>
    public Guid ReportId { get; set; }

    /// <summary>Kiedy klucz zapisano (po 24 h klucz jest usuwany).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class FieldReportDbContext(DbContextOptions<FieldReportDbContext> options) : DbContext(options)
{
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    /// <summary>
    /// Tworzy tabelę kluczy idempotencji, jeśli jej nie ma. EnsureCreated nie zmienia istniejącej bazy
    /// (zakłada tabele tylko w pustej), a tabela doszła po pierwszym wdrożeniu — dlatego przy starcie API
    /// wykonujemy ten jeden <c>CREATE TABLE IF NOT EXISTS</c>. Gdy tabela już jest, nic się nie dzieje;
    /// istniejących danych polecenie nie rusza.
    /// </summary>
    public void CreateIdempotencyKeysTableIfMissing() => Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "IdempotencyKeys" (
            "TechnicianEmail" text NOT NULL REFERENCES "Technicians" ("Email") ON DELETE CASCADE,
            "Key" character varying(100) NOT NULL,
            "RequestHash" text NOT NULL,
            "ReportId" uuid NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            PRIMARY KEY ("TechnicianEmail", "Key"))
        """);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Technician>(e =>
        {
            e.HasKey(t => t.Email);
            // usunięcie technika usuwa jego zgłoszenia, urządzenia, sesje i klucze idempotencji
            e.HasMany(t => t.Reports).WithOne().HasForeignKey(r => r.TechnicianEmail).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.Devices).WithOne().HasForeignKey(d => d.TechnicianEmail).OnDelete(DeleteBehavior.Cascade);
            e.HasMany<RefreshToken>().WithOne().HasForeignKey(r => r.TechnicianEmail).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Report>(e =>
        {
            e.HasKey(r => new { r.TechnicianEmail, r.Id });
            e.HasIndex(r => r.Id);
            e.Property(r => r.Title).HasMaxLength(80);
            e.Property(r => r.Description).HasMaxLength(500);
            // enumy jako tekst — czytelne w bazie i odporne na zmianę kolejności wartości
            e.Property(r => r.Priority).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        });

        model.Entity<IdempotencyKey>(e =>
        {
            // klucz główny (technik, klucz) jest unikalny: drugi równoczesny zapis tego samego klucza się nie uda (409)
            e.HasKey(k => new { k.TechnicianEmail, k.Key });
            e.Property(k => k.Key).HasMaxLength(100);
            e.HasOne<Technician>().WithMany().HasForeignKey(k => k.TechnicianEmail).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Device>().HasKey(d => d.Token);
        model.Entity<RefreshToken>().HasKey(r => r.Hash);
    }
}
