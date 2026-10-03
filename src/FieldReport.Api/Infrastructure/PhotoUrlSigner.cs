using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FieldReport.Api.Auth;
using FieldReport.Api.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FieldReport.Api.Infrastructure;

/// <summary>
/// Podpisany adres zdjęcia: <c>…/api/reports/{id}/photo?tech=…&amp;exp=…&amp;sig=…</c>.
/// Taki adres działa bez nagłówka Authorization (np. w <c>Image Source</c> w MAUI) do czasu <c>exp</c>.
/// <c>sig</c> = HMAC-SHA256 z „tech|id|exp” kluczem podpisu API — bez klucza nie da się podrobić ani przedłużyć adresu.
/// </summary>
public sealed class PhotoUrlSigner(SigningKeyProvider keys, TimeProvider time, IOptions<StorageOptions> storage)
{
    public string CreateUrl(HttpRequest request, string email, Guid id)
    {
        var tech = DataStore.TechnicianKey(email);
        var exp = time.GetUtcNow().AddMinutes(storage.Value.PhotoUrlLifetimeMinutes).ToUnixTimeSeconds();
        return $"{request.Scheme}://{request.Host}{request.PathBase}/api/reports/{id}/photo" +
               $"?tech={tech}&exp={exp}&sig={Sign(tech, id, exp)}";
    }

    /// <summary>Podpis zgodny i adres jeszcze ważny.</summary>
    public bool IsValid(string tech, Guid id, long exp, string sig) =>
        exp > time.GetUtcNow().ToUnixTimeSeconds() &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(tech, id, exp)), Encoding.ASCII.GetBytes(sig));

    private string Sign(string tech, Guid id, long exp)
    {
        var data = Encoding.UTF8.GetBytes($"{tech}|{id:N}|{exp.ToString(CultureInfo.InvariantCulture)}");
        return Base64UrlEncoder.Encode(HMACSHA256.HashData(keys.Key.Key, data));
    }
}
