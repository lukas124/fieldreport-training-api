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

public sealed class FieldReportDbContext(DbContextOptions<FieldReportDbContext> options) : DbContext(options)
{
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Technician>(e =>
        {
            e.HasKey(t => t.Email);
            // usunięcie technika usuwa jego zgłoszenia, urządzenia i sesje
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

        model.Entity<Device>().HasKey(d => d.Token);
        model.Entity<RefreshToken>().HasKey(r => r.Hash);
    }
}
