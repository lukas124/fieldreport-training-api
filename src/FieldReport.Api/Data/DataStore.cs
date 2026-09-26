using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldReport.Api.Contracts;
using FieldReport.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace FieldReport.Api.Data;

public sealed record StoredReport(
    Guid Id,
    string Title,
    string? Description,
    ReportPriority Priority,
    ReportStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PhotoFile,
    string? PhotoContentType);

public sealed record StoredDevice(string Token, string Platform, DateTimeOffset RegisteredAt);

public sealed record TechnicianInfo(string Email, string Name, int ReportCount, IReadOnlyList<StoredDevice> Devices);

public enum UpsertOutcome { Created, Updated, LimitExceeded }

/// <summary>
/// Dane w pamięci + trwały zapis do <c>{DataDirectory}/data.json</c> (zapis atomowy po każdej zmianie).
/// Zdjęcia leżą w <c>{DataDirectory}/photos/{klucz technika}/{id}.{ext}</c>.
/// Wszystkie klucze techników to znormalizowany (małe litery) e-mail.
/// </summary>
public sealed class DataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<DataStore> _logger;
    private readonly StorageOptions _options;
    private readonly string _dataFile;
    private State _state;
    private DateTimeOffset _lastStamp = DateTimeOffset.MinValue;

    public DataStore(IOptions<StorageOptions> options, IHostEnvironment env, TimeProvider time, ILogger<DataStore> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
        DataDirectory = Path.GetFullPath(Path.IsPathRooted(_options.DataDirectory)
            ? _options.DataDirectory
            : Path.Combine(env.ContentRootPath, _options.DataDirectory));
        PhotosDirectory = Path.Combine(DataDirectory, "photos");
        Directory.CreateDirectory(PhotosDirectory);
        _dataFile = Path.Combine(DataDirectory, "data.json");
        _state = Load();
    }

    public string DataDirectory { get; }
    public string PhotosDirectory { get; }

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    // ---------- technicy ----------

    public void EnsureTechnician(string email, string name)
    {
        lock (_gate)
        {
            var key = Normalize(email);
            if (_state.Technicians.TryGetValue(key, out var t))
            {
                t.LastSeenAt = _time.GetUtcNow();
            }
            else
            {
                _state.Technicians[key] = new Technician { Email = key, Name = name, LastSeenAt = _time.GetUtcNow() };
            }
            Save();
        }
    }

    public IReadOnlyList<TechnicianInfo> GetTechnicians()
    {
        lock (_gate)
        {
            return _state.Technicians.Values
                .OrderBy(t => t.Email)
                .Select(t => new TechnicianInfo(t.Email, t.Name, t.Reports.Count, t.Devices.Values.ToList()))
                .ToList();
        }
    }

    public string? GetTechnicianName(string email)
    {
        lock (_gate)
        {
            return _state.Technicians.TryGetValue(Normalize(email), out var t) ? t.Name : null;
        }
    }

    // ---------- zgłoszenia ----------

    public IReadOnlyList<StoredReport> GetReports(string email, DateTimeOffset? updatedSince)
    {
        lock (_gate)
        {
            if (!_state.Technicians.TryGetValue(Normalize(email), out var t)) return [];
            return t.Reports.Values
                .Where(r => updatedSince is null || r.UpdatedAt > updatedSince.Value)
                .OrderByDescending(r => r.UpdatedAt)
                .ToList();
        }
    }

    public StoredReport? GetReport(string email, Guid id)
    {
        lock (_gate)
        {
            return _state.Technicians.TryGetValue(Normalize(email), out var t) && t.Reports.TryGetValue(id, out var r) ? r : null;
        }
    }

    /// <summary>Idempotentny upsert: to samo <paramref name="id"/> zawsze nadpisuje ten sam rekord technika.</summary>
    public (UpsertOutcome Outcome, StoredReport? Report) Upsert(string email, string name, Guid id, ReportDto dto)
    {
        lock (_gate)
        {
            var t = GetOrAddTechnician(email, name);
            var now = NextStamp();
            if (t.Reports.TryGetValue(id, out var existing))
            {
                var updated = existing with
                {
                    Title = dto.Title.Trim(),
                    Description = dto.Description,
                    Priority = dto.Priority,
                    Status = dto.Status,
                    UpdatedAt = now
                };
                t.Reports[id] = updated;
                Save();
                return (UpsertOutcome.Updated, updated);
            }

            if (t.Reports.Count >= _options.MaxReportsPerTechnician) return (UpsertOutcome.LimitExceeded, null);

            var created = new StoredReport(id, dto.Title.Trim(), dto.Description, dto.Priority, dto.Status,
                dto.CreatedAt == default ? now : dto.CreatedAt, now, null, null);
            t.Reports[id] = created;
            Save();
            return (UpsertOutcome.Created, created);
        }
    }

    /// <summary>Nowe zgłoszenie od dyspozytora (status New, identyfikator nadaje serwer).</summary>
    public StoredReport? CreateByDispatcher(string email, string name, string title, string? description, ReportPriority priority)
    {
        lock (_gate)
        {
            var t = GetOrAddTechnician(email, name);
            if (t.Reports.Count >= _options.MaxReportsPerTechnician) return null;
            var now = NextStamp();
            var report = new StoredReport(Guid.NewGuid(), title.Trim(), description, priority, ReportStatus.New, now, now, null, null);
            t.Reports[report.Id] = report;
            Save();
            return report;
        }
    }

    /// <summary>Wszystkie zgłoszenia o danym id (w praktyce jedno) wraz z e-mailem technika.</summary>
    public IReadOnlyList<(string Email, StoredReport Report)> FindById(Guid id)
    {
        lock (_gate)
        {
            return _state.Technicians.Values
                .Where(t => t.Reports.ContainsKey(id))
                .Select(t => (t.Email, t.Reports[id]))
                .ToList();
        }
    }

    public IReadOnlyList<(string Email, StoredReport Report)> GetAllReports(string? email)
    {
        lock (_gate)
        {
            return _state.Technicians.Values
                .Where(t => email is null || t.Email == Normalize(email))
                .SelectMany(t => t.Reports.Values.Select(r => (t.Email, r)))
                .OrderByDescending(x => x.r.UpdatedAt)
                .ToList();
        }
    }

    public StoredReport? SetStatus(string email, Guid id, ReportStatus status)
    {
        lock (_gate)
        {
            if (!_state.Technicians.TryGetValue(Normalize(email), out var t) || !t.Reports.TryGetValue(id, out var r)) return null;
            var updated = r with { Status = status, UpdatedAt = NextStamp() };
            t.Reports[id] = updated;
            Save();
            return updated;
        }
    }

    // ---------- zdjęcia ----------

    public string PhotoPath(string email, string fileName) => Path.Combine(PhotosDirectory, TechnicianKey(email), fileName);

    public async Task<StoredReport?> SavePhotoAsync(string email, Guid id, Stream content, string extension, string contentType, CancellationToken ct)
    {
        if (GetReport(email, id) is null) return null;

        var fileName = $"{id:N}{extension}";
        var path = PhotoPath(email, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var fs = File.Create(temp))
        {
            await content.CopyToAsync(fs, ct);
        }

        lock (_gate)
        {
            if (!_state.Technicians.TryGetValue(Normalize(email), out var t) || !t.Reports.TryGetValue(id, out var r))
            {
                File.Delete(temp);
                return null;
            }
            if (r.PhotoFile is { } old && old != fileName) TryDelete(PhotoPath(email, old));
            File.Move(temp, path, overwrite: true);
            var updated = r with { PhotoFile = fileName, PhotoContentType = contentType, UpdatedAt = NextStamp() };
            t.Reports[id] = updated;
            Save();
            return updated;
        }
    }

    // ---------- urządzenia ----------

    /// <summary>Rejestruje token u technika (token przypisany wcześniej innemu technikowi zostaje przeniesiony).</summary>
    public StoredDevice? RegisterDevice(string email, string name, string token, string platform)
    {
        lock (_gate)
        {
            foreach (var other in _state.Technicians.Values) other.Devices.Remove(token);
            var t = GetOrAddTechnician(email, name);
            if (t.Devices.Count >= _options.MaxDevicesPerTechnician)
            {
                var oldest = t.Devices.Values.OrderBy(d => d.RegisteredAt).First();
                t.Devices.Remove(oldest.Token);
            }
            var device = new StoredDevice(token, platform, _time.GetUtcNow());
            t.Devices[token] = device;
            Save();
            return device;
        }
    }

    public IReadOnlyList<StoredDevice> GetDevices(string email)
    {
        lock (_gate)
        {
            return _state.Technicians.TryGetValue(Normalize(email), out var t) ? t.Devices.Values.ToList() : [];
        }
    }

    public bool RemoveDevice(string email, string token)
    {
        lock (_gate)
        {
            if (!_state.Technicians.TryGetValue(Normalize(email), out var t) || !t.Devices.Remove(token)) return false;
            Save();
            return true;
        }
    }

    public void RemoveTokenEverywhere(string token)
    {
        lock (_gate)
        {
            var removed = false;
            foreach (var t in _state.Technicians.Values) removed |= t.Devices.Remove(token);
            if (removed) Save();
        }
    }

    // ---------- refresh tokeny ----------

    public void AddRefreshToken(string token, string email, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var expired in _state.RefreshTokens.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
                _state.RefreshTokens.Remove(expired);
            _state.RefreshTokens[Hash(token)] = new RefreshEntry { Email = Normalize(email), ExpiresAt = expiresAt };
            Save();
        }
    }

    /// <summary>Zużywa refresh token (jednorazowy). Zwraca e-mail technika albo null.</summary>
    public string? ConsumeRefreshToken(string token)
    {
        lock (_gate)
        {
            var hash = Hash(token);
            if (!_state.RefreshTokens.Remove(hash, out var entry)) return null;
            Save();
            return entry.ExpiresAt > _time.GetUtcNow() ? entry.Email : null;
        }
    }

    // ---------- reset ----------

    public (int Reports, int Technicians) Reset(string? email)
    {
        lock (_gate)
        {
            var targets = _state.Technicians.Values.Where(t => email is null || t.Email == Normalize(email)).ToList();
            var reports = targets.Sum(t => t.Reports.Count);
            foreach (var t in targets)
            {
                _state.Technicians.Remove(t.Email);
                TryDeleteDirectory(Path.Combine(PhotosDirectory, TechnicianKey(t.Email)));
                foreach (var key in _state.RefreshTokens.Where(kv => kv.Value.Email == t.Email).Select(kv => kv.Key).ToList())
                    _state.RefreshTokens.Remove(key);
            }
            if (email is null) _state.RefreshTokens.Clear();
            Save();
            return (reports, targets.Count);
        }
    }

    // ---------- wnętrze ----------

    private Technician GetOrAddTechnician(string email, string name)
    {
        var key = Normalize(email);
        if (!_state.Technicians.TryGetValue(key, out var t))
        {
            t = new Technician { Email = key, Name = name, LastSeenAt = _time.GetUtcNow() };
            _state.Technicians[key] = t;
        }
        return t;
    }

    /// <summary>Ściśle rosnący znacznik czasu — dwie zmiany nigdy nie mają tego samego <c>UpdatedAt</c>.</summary>
    private DateTimeOffset NextStamp()
    {
        var now = _time.GetUtcNow();
        if (now <= _lastStamp) now = _lastStamp.AddTicks(1);
        _lastStamp = now;
        return now;
    }

    private static string TechnicianKey(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(email))))[..16].ToLowerInvariant();

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private State Load()
    {
        if (!File.Exists(_dataFile)) return new State();
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_dataFile), JsonOptions) ?? new State();
            foreach (var r in state.Technicians.Values.SelectMany(t => t.Reports.Values))
                if (r.UpdatedAt > _lastStamp) _lastStamp = r.UpdatedAt;
            _logger.LogInformation("Wczytano dane: {Technicians} techników z {File}", state.Technicians.Count, _dataFile);
            return state;
        }
        catch (Exception ex)
        {
            var backup = _dataFile + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_dataFile, backup, overwrite: true);
            _logger.LogError(ex, "Nie udało się wczytać {File}; kopia w {Backup}, start z pustymi danymi", _dataFile, backup);
            return new State();
        }
    }

    private void Save()
    {
        var temp = _dataFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonOptions));
        File.Move(temp, _dataFile, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { }
    }

    private sealed class State
    {
        public Dictionary<string, Technician> Technicians { get; set; } = [];
        public Dictionary<string, RefreshEntry> RefreshTokens { get; set; } = [];
    }

    private sealed class Technician
    {
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset LastSeenAt { get; set; }
        public Dictionary<Guid, StoredReport> Reports { get; set; } = [];
        public Dictionary<string, StoredDevice> Devices { get; set; } = [];
    }

    private sealed class RefreshEntry
    {
        public string Email { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
