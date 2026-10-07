using System.Security.Cryptography;
using System.Text;
using FieldReport.Api.Contracts;
using FieldReport.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldReport.Api.Data;

public sealed record TechnicianInfo(string Email, string Name, int ReportCount, IReadOnlyList<Device> Devices);

/// <summary>
/// Wynik POST z kluczem idempotencji: Created = nowe zgłoszenie, Repeated = powtórka (to samo zgłoszenie),
/// KeyReused = ten sam klucz z inną treścią, InProgress = ten sam klucz zapisywany równolegle, LimitExceeded = limit zgłoszeń.
/// </summary>
public enum CreateOutcome { Created, Repeated, KeyReused, InProgress, LimitExceeded }

/// <summary>
/// Operacje na danych API: technicy, zgłoszenia, urządzenia i refresh tokeny w PostgreSQL (EF Core),
/// zdjęcia jako pliki w <c>{DataDirectory}/photos/{klucz technika}/{id}.{ext}</c>.
/// Klucz technika w bazie to e-mail małymi literami.
/// </summary>
public sealed class DataStore(FieldReportDbContext db, StoragePaths paths, IOptions<StorageOptions> options, TimeProvider time)
{
    private readonly StorageOptions _options = options.Value;

    /// <summary>Jak długo pamiętamy klucz idempotencji (potem ten sam klucz działa jak nowy).</summary>
    public static readonly TimeSpan IdempotencyKeyLifetime = TimeSpan.FromHours(24);

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    // ---------- technicy ----------

    public void EnsureTechnician(string email, string name)
    {
        GetOrAddTechnician(email, name).LastSeenAt = Now();
        db.SaveChanges();
    }

    public IReadOnlyList<TechnicianInfo> GetTechnicians() =>
        db.Technicians
            .OrderBy(t => t.Email)
            .Select(t => new TechnicianInfo(t.Email, t.Name, t.Reports.Count, t.Devices.ToList()))
            .ToList();

    public string? GetTechnicianName(string email) =>
        db.Technicians.Where(t => t.Email == Normalize(email)).Select(t => t.Name).FirstOrDefault();

    // ---------- zgłoszenia ----------

    /// <summary>Wszystkie zgłoszenia technika, od ostatnio zmienionego.</summary>
    public IReadOnlyList<Report> GetReports(string email) =>
        db.Reports.AsNoTracking()
            .Where(r => r.TechnicianEmail == Normalize(email))
            .OrderByDescending(r => r.UpdatedAt)
            .ToList();

    public Report? GetReport(string email, Guid id) =>
        db.Reports.AsNoTracking().FirstOrDefault(r => r.TechnicianEmail == Normalize(email) && r.Id == id);

    /// <summary>
    /// Nowe zgłoszenie z kluczem idempotencji (POST /api/reports). Id nadaje serwer.
    /// Pierwsze żądanie: zgłoszenie i klucz zapisują się razem. Powtórka z tym samym kluczem i tą samą treścią
    /// zwraca zapisane zgłoszenie (bez drugiego zapisu); ten sam klucz z inną treścią → KeyReused;
    /// ten sam klucz zapisany w tej chwili przez inne żądanie → InProgress.
    /// </summary>
    public (CreateOutcome Outcome, Report? Report) Create(string email, string name, string idempotencyKey, ReportDto dto)
    {
        var key = Normalize(email);
        var requestHash = Hash($"{dto.Title}|{dto.Description}|{dto.Priority}|{dto.Status}|{dto.CreatedAt:O}");

        // klucze starsze niż doba usuwamy przy okazji — tyle czasu wystarcza na ponowienia z aplikacji
        var expired = Now() - IdempotencyKeyLifetime;
        db.IdempotencyKeys.Where(k => k.CreatedAt < expired).ExecuteDelete();

        // klucz już zapisany = powtórka żądania: nic nie zapisujemy, zwracamy wynik pierwszego żądania
        var saved = db.IdempotencyKeys.AsNoTracking().FirstOrDefault(k => k.TechnicianEmail == key && k.Key == idempotencyKey);
        if (saved is not null)
        {
            return saved.RequestHash == requestHash
                ? (CreateOutcome.Repeated, GetReport(email, saved.ReportId))
                : (CreateOutcome.KeyReused, null);
        }

        GetOrAddTechnician(email, name);
        if (db.Reports.Count(r => r.TechnicianEmail == key) >= _options.MaxReportsPerTechnician)
            return (CreateOutcome.LimitExceeded, null);

        var now = Now();
        var report = new Report
        {
            TechnicianEmail = key,
            Id = Guid.NewGuid(),
            Title = dto.Title,
            Description = dto.Description,
            Priority = dto.Priority,
            Status = dto.Status,
            CreatedAt = dto.CreatedAt == default ? now : Microseconds(dto.CreatedAt.ToUniversalTime()),
            UpdatedAt = now
        };
        db.Reports.Add(report);
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            TechnicianEmail = key, Key = idempotencyKey, RequestHash = requestHash, ReportId = report.Id, CreatedAt = now
        });
        try
        {
            db.SaveChanges(); // jedna transakcja: zgłoszenie i klucz zapisują się razem albo wcale
            return (CreateOutcome.Created, report);
        }
        catch (DbUpdateException)
        {
            // ten sam klucz zapisało w tej chwili równoległe żądanie (klucz w bazie jest unikalny) — klient ponowi później
            db.ChangeTracker.Clear();
            return (CreateOutcome.InProgress, null);
        }
    }

    /// <summary>Aktualizacja istniejącego zgłoszenia (PUT). Brak zgłoszenia u technika → null.</summary>
    public Report? Update(string email, Guid id, ReportDto dto)
    {
        var report = db.Reports.FirstOrDefault(r => r.TechnicianEmail == Normalize(email) && r.Id == id);
        if (report is null) return null;
        report.Title = dto.Title;
        report.Description = dto.Description;
        report.Priority = dto.Priority;
        report.Status = dto.Status;
        report.UpdatedAt = Now();
        db.SaveChanges();
        return report;
    }

    /// <summary>Nowe zgłoszenie od dyspozytora (status New, identyfikator nadaje serwer).</summary>
    public Report? CreateByDispatcher(string email, string name, string title, string? description, ReportPriority priority)
    {
        var key = Normalize(email);
        GetOrAddTechnician(email, name);
        if (db.Reports.Count(r => r.TechnicianEmail == key) >= _options.MaxReportsPerTechnician) return null;

        var now = Now();
        var report = new Report
        {
            TechnicianEmail = key,
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Priority = priority,
            Status = ReportStatus.New,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Reports.Add(report);
        db.SaveChanges();
        return report;
    }

    /// <summary>Zgłoszenia o danym id u wszystkich techników (zwykle jedno).</summary>
    public IReadOnlyList<(string Email, Report Report)> FindById(Guid id) =>
        db.Reports.AsNoTracking().Where(r => r.Id == id).AsEnumerable().Select(r => (r.TechnicianEmail, r)).ToList();

    public IReadOnlyList<(string Email, Report Report)> GetAllReports(string? email)
    {
        var query = db.Reports.AsNoTracking();
        if (email is not null)
        {
            var key = Normalize(email);
            query = query.Where(r => r.TechnicianEmail == key);
        }
        return query.OrderByDescending(r => r.UpdatedAt).AsEnumerable().Select(r => (r.TechnicianEmail, r)).ToList();
    }

    public Report? SetStatus(string email, Guid id, ReportStatus status)
    {
        var report = db.Reports.FirstOrDefault(r => r.TechnicianEmail == Normalize(email) && r.Id == id);
        if (report is null) return null;
        report.Status = status;
        report.UpdatedAt = Now();
        db.SaveChanges();
        return report;
    }

    // ---------- zdjęcia ----------

    public string PhotoPath(string email, string fileName) => Path.Combine(paths.PhotosDirectory, TechnicianKey(email), fileName);

    /// <summary>Plik zdjęcia zgłoszenia <paramref name="id"/> w katalogu technika o kluczu <paramref name="technicianKey"/> albo null.</summary>
    public string? FindPhotoFile(string technicianKey, Guid id)
    {
        var dir = Path.Combine(paths.PhotosDirectory, technicianKey);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, $"{id:N}.*").FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.Ordinal))
            : null;
    }

    /// <summary>Zapisuje plik zdjęcia (najpierw do pliku tymczasowego) i oznacza zgłoszenie jako mające zdjęcie.</summary>
    public async Task<Report?> SavePhotoAsync(string email, Guid id, Stream content, string extension, string contentType, CancellationToken ct)
    {
        var report = await db.Reports.FirstOrDefaultAsync(r => r.TechnicianEmail == Normalize(email) && r.Id == id, ct);
        if (report is null) return null;

        var fileName = $"{id:N}{extension}";
        var path = PhotoPath(email, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var fs = File.Create(temp))
        {
            await content.CopyToAsync(fs, ct);
        }
        if (report.PhotoFile is { } old && old != fileName) TryDelete(PhotoPath(email, old));
        File.Move(temp, path, overwrite: true);

        report.PhotoFile = fileName;
        report.PhotoContentType = contentType;
        report.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
        return report;
    }

    // ---------- urządzenia ----------

    /// <summary>Rejestruje token u technika (token zarejestrowany przez innego technika zostaje przeniesiony).</summary>
    public Device RegisterDevice(string email, string name, string token, string platform)
    {
        var key = Normalize(email);
        GetOrAddTechnician(email, name);

        var device = db.Devices.Find(token);
        if (device is null)
        {
            // limit urządzeń: najstarsze rejestracje technika są usuwane
            var surplus = db.Devices.Where(d => d.TechnicianEmail == key)
                .OrderByDescending(d => d.RegisteredAt).Skip(_options.MaxDevicesPerTechnician - 1).ToList();
            db.Devices.RemoveRange(surplus);
            device = new Device { Token = token };
            db.Devices.Add(device);
        }
        device.TechnicianEmail = key;
        device.Platform = platform;
        device.RegisteredAt = Now();
        db.SaveChanges();
        return device;
    }

    public IReadOnlyList<Device> GetDevices(string email) =>
        db.Devices.AsNoTracking().Where(d => d.TechnicianEmail == Normalize(email)).ToList();

    public bool RemoveDevice(string email, string token) =>
        db.Devices.Where(d => d.TechnicianEmail == Normalize(email) && d.Token == token).ExecuteDelete() > 0;

    public void RemoveTokenEverywhere(string token) => db.Devices.Where(d => d.Token == token).ExecuteDelete();

    // ---------- refresh tokeny ----------

    public void AddRefreshToken(string token, string email, DateTimeOffset expiresAt)
    {
        var now = Now();
        db.RefreshTokens.Where(r => r.ExpiresAt <= now).ExecuteDelete();
        db.RefreshTokens.Add(new RefreshToken { Hash = Hash(token), TechnicianEmail = Normalize(email), ExpiresAt = expiresAt.ToUniversalTime() });
        db.SaveChanges();
    }

    /// <summary>Zużywa refresh token (jednorazowy). Zwraca e-mail technika albo null.</summary>
    public string? ConsumeRefreshToken(string token)
    {
        var entry = db.RefreshTokens.Find(Hash(token));
        if (entry is null) return null;
        db.RefreshTokens.Remove(entry);
        db.SaveChanges();
        return entry.ExpiresAt > Now() ? entry.TechnicianEmail : null;
    }

    // ---------- reset ----------

    /// <summary>Usuwa technika (albo wszystkich) razem ze zgłoszeniami, urządzeniami, sesjami i zdjęciami.</summary>
    public (int Reports, int Technicians) Reset(string? email)
    {
        var technicians = db.Technicians.AsQueryable();
        var reports = db.Reports.AsQueryable();
        if (email is not null)
        {
            var key = Normalize(email);
            technicians = technicians.Where(t => t.Email == key);
            reports = reports.Where(r => r.TechnicianEmail == key);
        }

        var emails = technicians.Select(t => t.Email).ToList();
        var reportCount = reports.Count();
        technicians.ExecuteDelete(); // kaskadowo: zgłoszenia, urządzenia, refresh tokeny
        foreach (var e in emails) TryDeleteDirectory(Path.Combine(paths.PhotosDirectory, TechnicianKey(e)));
        return (reportCount, emails.Count);
    }

    // ---------- pomocnicze ----------

    private Technician GetOrAddTechnician(string email, string name)
    {
        var key = Normalize(email);
        var technician = db.Technicians.Find(key);
        if (technician is null)
        {
            technician = new Technician { Email = key, Name = name, LastSeenAt = Now() };
            db.Technicians.Add(technician);
        }
        return technician;
    }

    /// <summary>Bieżący czas UTC z dokładnością do mikrosekundy (tyle przechowuje PostgreSQL).</summary>
    private DateTimeOffset Now() => Microseconds(time.GetUtcNow());

    private static DateTimeOffset Microseconds(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    /// <summary>Nazwa katalogu zdjęć technika: pierwsze 16 znaków SHA-256 z e-maila (e-mail nie trafia do adresów URL).</summary>
    public static string TechnicianKey(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(email))))[..16].ToLowerInvariant();

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { }
    }
}
