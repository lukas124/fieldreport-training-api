using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldReport.Api.Contracts;
using FieldReport.Api.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FieldReport.Api.Tests;

/// <summary>
/// API w pamięci (TestServer) z osobnym, tymczasowym katalogiem danych. Firebase domyślnie wyłączony,
/// limity żądań wyłączone. Ustawienia przez <see cref="WebHostBuilderExtensions.UseSetting"/> — są widoczne
/// także dla kodu w Program.cs czytającego konfigurację przed <c>Build()</c>.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";

    private readonly Dictionary<string, string?> _settings;
    private readonly IPushSender? _pushSender;

    public ApiFactory(Dictionary<string, string?>? settings = null, IPushSender? pushSender = null)
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "fieldreport-api-tests", Guid.NewGuid().ToString("N"));
        _settings = new Dictionary<string, string?>
        {
            ["Storage:DataDirectory"] = DataDirectory,
            ["Firebase:Disabled"] = "true",
            ["Admin:Key"] = AdminKey,
            ["RateLimiting:Enabled"] = "false",
            ["Auth:SigningKey"] = "test-signing-key-test-signing-key-0123456789"
        };
        foreach (var (k, v) in settings ?? []) _settings[k] = v;
        _pushSender = pushSender;
    }

    public string DataDirectory { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
        if (_pushSender is not null)
        {
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<IPushSender>();
                s.AddSingleton(_pushSender);
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class Api
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string NewEmail(string prefix = "technik") => $"{prefix}.{Guid.NewGuid():N}@firma.pl";

    public static async Task<TokenResponse> LoginAsync(HttpClient client, string email, string pin = "1234")
    {
        var response = await client.PostAsJsonAsync("api/auth/login", new { email, pin });
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    /// <summary>Loguje nowego technika i zwraca klienta z nagłówkiem Bearer.</summary>
    public static async Task<(HttpClient Client, string Email)> AuthorizedClientAsync(WebApplicationFactory<Program> factory, string? email = null)
    {
        email ??= NewEmail();
        var client = factory.CreateClient();
        var tokens = await LoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (client, email);
    }

    public static ReportDto NewReport(Guid? id = null, string title = "Awaria pompy") => new()
    {
        Id = id ?? Guid.NewGuid(),
        Title = title,
        Description = "Pompa w węźle C2 nie startuje.",
        Priority = ReportPriority.High,
        Status = ReportStatus.New,
        CreatedAt = new DateTimeOffset(2026, 9, 26, 8, 15, 0, TimeSpan.Zero)
    };

    public static Task<HttpResponseMessage> PutReportAsync(HttpClient client, ReportDto report) =>
        client.PutAsJsonAsync($"api/reports/{report.Id}", report, Json);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>Minimalny poprawny nagłówek JPEG + wypełnienie do zadanego rozmiaru.</summary>
    public static byte[] FakeJpeg(int size = 2048)
    {
        var bytes = new byte[size];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        return bytes;
    }

    public static MultipartFormDataContent PhotoContent(byte[] bytes, string field = "photo", string fileName = "usterka.jpg")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, field, fileName);
        return content;
    }
}

/// <summary>Podstawiany nadawca push: zapisuje wysyłki, wybrane tokeny udają „Unregistered”.</summary>
public sealed class RecordingPushSender : IPushSender
{
    public List<(string Token, PushMessage Message)> Sent { get; } = [];
    public HashSet<string> DeadTokens { get; } = [];
    public bool IsConfigured => true;
    public string StatusDescription => "Firebase (atrapa testowa).";

    public Task<PushSendResult> SendAsync(string token, PushMessage message, CancellationToken ct)
    {
        lock (Sent)
        {
            if (DeadTokens.Contains(token)) return Task.FromResult(new PushSendResult(PushSendOutcome.Unregistered, "Unregistered"));
            Sent.Add((token, message));
            return Task.FromResult(new PushSendResult(PushSendOutcome.Sent));
        }
    }
}
