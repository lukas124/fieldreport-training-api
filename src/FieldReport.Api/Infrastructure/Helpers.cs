using System.Security.Cryptography;
using System.Text;
using FieldReport.Api.Contracts;
using FieldReport.Api.Data;

namespace FieldReport.Api.Infrastructure;

public static class ReportMapper
{
    public static ReportDto ToDto(this Report r, HttpRequest request) => new()
    {
        Id = r.Id,
        Title = r.Title,
        Description = r.Description,
        Priority = r.Priority,
        Status = r.Status,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        HasPhoto = r.PhotoFile is not null,
        PhotoUrl = r.PhotoFile is null
            ? null
            : request.HttpContext.RequestServices.GetRequiredService<PhotoUrlSigner>().CreateUrl(request, r.TechnicianEmail, r.Id)
    };
}

public static class Secrets
{
    public static bool FixedTimeEquals(string? provided, string expected) =>
        provided is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}

public static class ImageSniffer
{
    /// <summary>Rozpoznaje format po sygnaturze (klienci MAUI często wysyłają plik bez Content-Type).</summary>
    public static (string Extension, string ContentType)? Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return (".jpg", "image/jpeg");
        if (h.Length >= 8 && h[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return (".png", "image/png");
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8)) return (".webp", "image/webp");
        if (h.Length >= 12 && h[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(h[8..12]);
            if (brand is "heic" or "heix" or "mif1" or "msf1" or "hevc") return (".heic", "image/heic");
        }
        return null;
    }

    /// <summary>Typ MIME po rozszerzeniu zapisanego pliku zdjęcia.</summary>
    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        _ => "application/octet-stream"
    };
}
