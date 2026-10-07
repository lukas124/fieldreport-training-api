using System.Net;
using System.Net.Http.Json;
using FieldReport.Api.Contracts;

namespace FieldReport.Api.Tests;

/// <summary>photoUrl z podpisem (tech, exp, sig) działa bez tokenu do czasu wygaśnięcia.</summary>
public sealed class SignedPhotoUrlTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    private static async Task<(ReportDto Report, byte[] Photo)> ReportWithPhotoAsync(ApiFactory factory)
    {
        var (client, _) = await Api.AuthorizedClientAsync(factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(client, report);
        var photo = Api.FakeJpeg(1500);
        var response = await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(photo));
        return (await Api.ReadAsync<ReportDto>(response), photo);
    }

    [Test]
    public async Task Photo_url_contains_signature_and_works_without_token()
    {
        var (report, photo) = await ReportWithPhotoAsync(_factory);
        Assert.That(report.PhotoUrl, Does.Contain("tech=").And.Contain("exp=").And.Contain("sig="));

        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(report.PhotoUrl);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(photo));
    }

    [Test]
    public async Task List_returns_signed_photo_url_too()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(client, report);
        await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(Api.FakeJpeg()));

        var list = await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);

        var url = list!.Single().PhotoUrl!;
        Assert.That((await _factory.CreateClient().GetAsync(url)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase("sig")]
    [TestCase("exp")]
    [TestCase("tech")]
    [TestCase("id")]
    public async Task Tampered_url_returns_403(string part)
    {
        var (report, _) = await ReportWithPhotoAsync(_factory);
        var url = report.PhotoUrl!;
        var tampered = part switch
        {
            "sig" => url[..^2] + (url[^2] == 'A' ? "BB" : "AA"),
            "exp" => System.Text.RegularExpressions.Regex.Replace(url, @"exp=(\d+)", m => $"exp={long.Parse(m.Groups[1].Value) + 3600}"),
            "tech" => System.Text.RegularExpressions.Regex.Replace(url, @"tech=([0-9a-f])", m => "tech=" + (m.Groups[1].Value == "0" ? "1" : "0")),
            _ => url.Replace(report.Id.ToString(), Guid.NewGuid().ToString())
        };

        var response = await _factory.CreateClient().GetAsync(tampered);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Expired_url_returns_403()
    {
        using var factory = new ApiFactory(new() { ["Storage:PhotoUrlLifetimeMinutes"] = "0" });
        var (report, _) = await ReportWithPhotoAsync(factory);

        var response = await factory.CreateClient().GetAsync(report.PhotoUrl);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Without_signature_and_token_returns_401()
    {
        var (report, _) = await ReportWithPhotoAsync(_factory);
        var response = await _factory.CreateClient().GetAsync($"api/reports/{report.Id}/photo");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Swagger_describes_photo_url_as_signed_without_token()
    {
        var json = await _factory.CreateClient().GetStringAsync("swagger/v1/swagger.json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var description = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ReportDto")
            .GetProperty("properties").GetProperty("photoUrl").GetProperty("description").GetString();

        Assert.That(description, Does.Contain("Podpisany adres").And.Contain("bez tokenu"));
        Assert.That(description, Does.Not.Contain("wymaga tokenu"));
    }
}
