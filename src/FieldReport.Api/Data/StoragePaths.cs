using FieldReport.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace FieldReport.Api.Data;

/// <summary>Katalogi na dysku: dane (klucz podpisu JWT) i zdjęcia zgłoszeń.</summary>
public sealed class StoragePaths
{
    public StoragePaths(IOptions<StorageOptions> options, IHostEnvironment env)
    {
        var dir = options.Value.DataDirectory;
        DataDirectory = Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir));
        PhotosDirectory = Path.Combine(DataDirectory, "photos");
        Directory.CreateDirectory(PhotosDirectory);
    }

    public string DataDirectory { get; }
    public string PhotosDirectory { get; }
}
