using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using FieldReport.Api.Auth;
using FieldReport.Api.Controllers;
using FieldReport.Api.Data;
using FieldReport.Api.Infrastructure;
using FieldReport.Api.Push;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ---------- nasłuch ----------
// Domyślnie http://localhost:5080 (emulator Android: http://10.0.2.2:5080).
// Hosting:ListenOnAllInterfaces=true → 0.0.0.0 (uczestnicy w sieci sali łączą się z laptopem trenera).
// Jawne --urls / ASPNETCORE_URLS / Kestrel:Endpoints mają pierwszeństwo.
var hosting = builder.Configuration.GetSection(HostingOptions.Section).Get<HostingOptions>() ?? new HostingOptions();
var explicitUrls = !string.IsNullOrEmpty(builder.Configuration["urls"])
                   || !string.IsNullOrEmpty(builder.Configuration["http_ports"])
                   || builder.Configuration.GetSection("Kestrel:Endpoints").Exists();
if (!explicitUrls)
{
    builder.WebHost.UseUrls(hosting.ListenOnAllInterfaces ? $"http://0.0.0.0:{hosting.Port}" : $"http://localhost:{hosting.Port}");
}

// ---------- opcje ----------
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<FirebaseOptions>(builder.Configuration.GetSection(FirebaseOptions.Section));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.Configure<DispatcherOptions>(builder.Configuration.GetSection(DispatcherOptions.Section));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.Section));

// ---------- usługi ----------
builder.Services.AddSingleton(TimeProvider.System);
// baza: connection string tylko z konfiguracji środowiska (ConnectionStrings__FieldReport)
var connectionString = builder.Configuration.GetConnectionString("FieldReport");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Brak ConnectionStrings:FieldReport (zmienna ConnectionStrings__FieldReport).");
builder.Services.AddDbContext<FieldReportDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.AddSingleton<StoragePaths>();
builder.Services.AddScoped<DataStore>();
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddSingleton<PhotoUrlSigner>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddSingleton<IPushSender, FirebasePushSender>();
builder.Services.AddScoped<PushService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();
builder.Services.AddAuthorization();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddFieldReportSwagger();
builder.Services.AddFieldReportRateLimiting();

// API stoi za reverse proxy hostingu: adres klienta i https bierzemy z nagłówków X-Forwarded-*
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// tabele tworzone przy pierwszym starcie (bez migracji — patrz README → Baza danych)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FieldReportDbContext>();
    db.Database.EnsureCreated();
    db.CreateIdempotencyKeysTableIfMissing(); // tabela dodana później: dla bazy założonej wcześniej
}

// ---------- potok ----------
app.UseForwardedHeaders();

var pathBase = NormalizePathBase(app.Configuration["PathBase"]);
if (pathBase.Length > 0)
{
    app.UsePathBase(pathBase); // /maui/podstawowe/api/... → PathBase=/maui/podstawowe, Path=/api/...
}

// żądania spoza PathBase (np. „/” domeny) → 301 na adres z konfiguracji; pusty adres = wyłączone
var outsideRedirect = app.Configuration["RedirectOutsidePathBase"];
if (pathBase.Length > 0 && !string.IsNullOrWhiteSpace(outsideRedirect))
{
    app.Use((context, next) =>
    {
        if (context.Request.PathBase.HasValue) return next(context);
        context.Response.Redirect(outsideRedirect, permanent: true);
        return Task.CompletedTask;
    });
}

app.UseExceptionHandler();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue("Swagger:Enabled", true))
{
    app.UseFieldReportSwagger();
}

app.MapControllers();
app.MapGet("/", (HttpContext ctx, IPushSender push) => Results.Content(HomePage.Render(ctx.Request.PathBase.Value ?? "", push), "text/html; charset=utf-8"))
    .ExcludeFromDescription();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var paths = app.Services.GetRequiredService<StoragePaths>();
    var push = app.Services.GetRequiredService<IPushSender>();
    app.Logger.LogInformation("FieldReport API {Version} | PathBase: '{PathBase}' | dane: {Dir} | {Firebase}",
        HealthController.Version, pathBase, paths.DataDirectory, push.StatusDescription);
    if (!explicitUrls && hosting.ListenOnAllInterfaces)
    {
        foreach (var ip in LanAddresses())
            app.Logger.LogInformation("Sieć sali: http://{Ip}:{Port}{PathBase}/swagger", ip, hosting.Port, pathBase);
    }
});

app.Run();

static string NormalizePathBase(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "";
    var trimmed = value.Trim().TrimEnd('/');
    return trimmed.Length == 0 ? "" : trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
}

static IEnumerable<IPAddress> LanAddresses() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

/// <summary>Punkt wejścia widoczny dla testów (WebApplicationFactory).</summary>
public partial class Program;
