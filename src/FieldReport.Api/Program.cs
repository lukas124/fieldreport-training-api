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
builder.Services.Configure<ReverseProxyOptions>(builder.Configuration.GetSection(ReverseProxyOptions.Section));

// ---------- usługi ----------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DataStore>();
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IPushSender, FirebasePushSender>();
builder.Services.AddSingleton<PushService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();
builder.Services.AddAuthorization();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddFieldReportSwagger();
builder.Services.AddFieldReportRateLimiting();

builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ReverseProxyOptions>>((o, proxy) =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    if (proxy.Value.TrustAllProxies)
    {
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    }
});

var app = builder.Build();

// ---------- potok ----------
app.UseForwardedHeaders();

var proxyOptions = app.Services.GetRequiredService<IOptions<ReverseProxyOptions>>().Value;
var pathBase = NormalizePathBase(app.Configuration["PathBase"]);
if (pathBase.Length > 0)
{
    app.UsePathBase(pathBase); // /maui/podstawowe/api/... → PathBase=/maui/podstawowe, Path=/api/...
}
if (proxyOptions.UseForwardedPrefix)
{
    // proxy obcinające prefiks może go podać w X-Forwarded-Prefix — wtedy linki i Swagger nadal są poprawne
    app.Use((context, next) =>
    {
        if (!context.Request.PathBase.HasValue
            && NormalizePathBase(context.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault()) is { Length: > 0 } prefix)
        {
            context.Request.PathBase = prefix;
        }
        return next(context);
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
    var store = app.Services.GetRequiredService<DataStore>();
    var push = app.Services.GetRequiredService<IPushSender>();
    app.Logger.LogInformation("FieldReport API {Version} | PathBase: '{PathBase}' | dane: {Dir} | {Firebase}",
        HealthController.Version, pathBase, store.DataDirectory, push.StatusDescription);
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
