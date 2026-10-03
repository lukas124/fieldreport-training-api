using System.Globalization;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FieldReport.Api.Contracts;
using FieldReport.Api.Data;
using FieldReport.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FieldReport.Api.Auth;

/// <summary>Klucz podpisu JWT: z konfiguracji albo wygenerowany raz i zapisany w katalogu danych (przeżywa restart).</summary>
public sealed class SigningKeyProvider
{
    public SigningKeyProvider(IOptions<AuthOptions> auth, StoragePaths paths)
    {
        var configured = auth.Value.SigningKey;
        string secret;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Length < 32) throw new InvalidOperationException("Auth:SigningKey musi mieć co najmniej 32 znaki.");
            secret = configured;
        }
        else
        {
            var file = Path.Combine(paths.DataDirectory, "jwt-signing-key.txt");
            if (File.Exists(file))
            {
                secret = File.ReadAllText(file).Trim();
            }
            else
            {
                secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                File.WriteAllText(file, secret);
            }
        }
        Key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public SymmetricSecurityKey Key { get; }
}

/// <summary>Wydaje access tokeny (JWT) i refresh tokeny (losowe, jednorazowe, przechowywane jako skrót).</summary>
public sealed class TokenService(
    SigningKeyProvider keys, IOptionsMonitor<AuthOptions> options, DataStore store, TimeProvider time)
{
    public TokenResponse Issue(string email, string name)
    {
        var o = options.CurrentValue;
        var now = time.GetUtcNow();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddSeconds(o.AccessTokenLifetimeSeconds).UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = DataStore.Normalize(email),
                [JwtRegisteredClaimNames.Name] = name,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N")
            },
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256)
        };
        var access = new JsonWebTokenHandler().CreateToken(descriptor);

        var refresh = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        store.AddRefreshToken(refresh, email, now.AddDays(o.RefreshTokenLifetimeDays));

        return new TokenResponse
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresIn = o.AccessTokenLifetimeSeconds,
            TechnicianName = name
        };
    }
}

/// <summary>Konfiguracja JwtBearer z zależnościami z DI (klucz, wystawca, brak tolerancji zegara).</summary>
public sealed class ConfigureJwtBearer(SigningKeyProvider keys, IOptions<AuthOptions> auth)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme) return;
        Configure(options);
    }

    public void Configure(JwtBearerOptions options)
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = auth.Value.Issuer,
            ValidAudience = auth.Value.Audience,
            IssuerSigningKey = keys.Key,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.Zero, // krótki token ma naprawdę wygasać (demo 401 → refresh)
            NameClaimType = JwtRegisteredClaimNames.Name
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                var expired = context.AuthenticateFailure is SecurityTokenExpiredException;
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = expired
                    ? "Bearer error=\"invalid_token\", error_description=\"The token expired\""
                    : "Bearer";
                await Results.Problem(
                        title: expired ? "Token wygasł" : "Brak autoryzacji",
                        detail: expired
                            ? "Access token wygasł — odśwież go przez POST /api/auth/refresh."
                            : "Wymagany nagłówek Authorization: Bearer <accessToken> (POST /api/auth/login).",
                        statusCode: StatusCodes.Status401Unauthorized)
                    .ExecuteAsync(context.HttpContext);
            }
        };
    }
}

public static partial class Technicians
{
    /// <summary>Poprawny e-mail: jedna małpa, domena z kropką, bez spacji, maks. 254 znaki.</summary>
    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254) return false;
        if (!EmailRegex().IsMatch(email.Trim())) return false;
        return MailAddress.TryCreate(email.Trim(), out var address) && address.Address == email.Trim();
    }

    /// <summary><c>jan.kowalski@firma.pl</c> → „Jan Kowalski”.</summary>
    public static string NameFromEmail(string email)
    {
        var local = email.Trim().Split('@')[0].Split('+')[0];
        var parts = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.Where(char.IsLetter).ToArray()))
            .Where(p => p.Length > 0)
            .Select(p => char.ToUpper(p[0], CultureInfo.GetCultureInfo("pl-PL")) + p[1..].ToLower(CultureInfo.GetCultureInfo("pl-PL")))
            .ToList();
        return parts.Count > 0 ? string.Join(' ', parts) : "Technik";
    }

    public static string Email(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? throw new InvalidOperationException("Brak claimu sub.");

    public static string Name(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Name) ?? NameFromEmail(user.Email());

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s.]{2,}$")]
    private static partial Regex EmailRegex();
}
