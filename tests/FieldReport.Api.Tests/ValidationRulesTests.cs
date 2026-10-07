using System.Net;
using System.Net.Http.Json;
using FieldReport.Api.Contracts;
using FieldReport.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FieldReport.Api.Tests;

/// <summary>Reguły walidacji i zachowania brzegowe: tytuł po Trim, niejednoznaczne id, pole zdjęcia, statusy, PIN, limit logowania.</summary>
public sealed class ValidationRulesTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    // długość tytułu sprawdzana po obcięciu spacji z brzegów
    [TestCase("  ab  ", HttpStatusCode.BadRequest)]
    [TestCase("ab\n\n", HttpStatusCode.BadRequest)]
    [TestCase("   ", HttpStatusCode.BadRequest)]
    [TestCase("  abc  ", HttpStatusCode.Created)]
    public async Task Post_title_length_is_validated_after_trim(string title, HttpStatusCode expected)
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var response = await Api.CreateReportAsync(client, Api.NewReport(title: title));
        Assert.That(response.StatusCode, Is.EqualTo(expected), await response.Content.ReadAsStringAsync());
        if (expected == HttpStatusCode.Created)
            Assert.That((await Api.ReadAsync<ReportDto>(response)).Title, Is.EqualTo("abc"));
    }

    [Test]
    public async Task Post_title_80_chars_with_surrounding_spaces_is_accepted_and_81_rejected()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var ok = await Api.CreateReportAsync(client, Api.NewReport(title: " " + new string('x', 80) + " "));
        var tooLong = await Api.CreateReportAsync(client, Api.NewReport(title: " " + new string('x', 81) + " "));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That((await Api.ReadAsync<ReportDto>(ok)).Title, Has.Length.EqualTo(80));
        Assert.That(tooLong.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [TestCase("  ab  ", HttpStatusCode.BadRequest)]
    [TestCase(" xyz ", HttpStatusCode.OK)]
    public async Task Dispatcher_title_is_validated_after_trim(string title, HttpStatusCode expected)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("api/dispatcher/reports",
            new { technicianEmail = Api.NewEmail(), title });
        Assert.That(response.StatusCode, Is.EqualTo(expected));
        if (expected == HttpStatusCode.OK)
            Assert.That((await Api.ReadAsync<DispatcherResult>(response)).Reports[0].Report.Title, Is.EqualTo("xyz"));
    }

    // to samo id u dwóch techników (np. dane sprzed nadawania id przez serwer): dyspozytor musi wskazać technika
    [Test]
    public async Task Patch_status_with_id_shared_by_two_technicians_requires_email_and_touches_only_target()
    {
        var (a, emailA) = await Api.AuthorizedClientAsync(_factory);
        var (b, emailB) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(a, report);
        var id = report.Id;
        using (var db = new FieldReportDbContext(new DbContextOptionsBuilder<FieldReportDbContext>().UseNpgsql(_factory.ConnectionString).Options))
        {
            // przez API tego się już nie da zrobić (id nadaje serwer), więc kopię u technika B wstawiamy wprost do bazy
            db.Reports.Add(new Report { TechnicianEmail = emailB, Id = id, Title = "Kopia", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.SaveChanges();
        }
        var dispatcher = _factory.CreateClient();

        var ambiguous = await dispatcher.PatchAsJsonAsync($"api/dispatcher/reports/{id}/status", new { status = "Done" });
        Assert.That(ambiguous.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        var targeted = await dispatcher.PatchAsJsonAsync(
            $"api/dispatcher/reports/{id}/status?technicianEmail={Uri.EscapeDataString(emailA)}", new { status = "Done" });
        Assert.That(targeted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Api.ReadAsync<DispatcherResult>(targeted)).Reports, Has.Count.EqualTo(1));

        Assert.That((await a.GetFromJsonAsync<ReportDto>($"api/reports/{id}", Api.Json))!.Status, Is.EqualTo(ReportStatus.Done));
        Assert.That((await b.GetFromJsonAsync<ReportDto>($"api/reports/{id}", Api.Json))!.Status, Is.EqualTo(ReportStatus.New));

        var wrongTech = await dispatcher.PatchAsJsonAsync(
            $"api/dispatcher/reports/{id}/status?technicianEmail=nikt@firma.pl", new { status = "Done" });
        Assert.That(wrongTech.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // zdjęcie przyjmowane tylko z pola multipart "photo"
    [Test]
    public async Task Photo_in_other_multipart_field_returns_400()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(client, report);

        var response = await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(Api.FakeJpeg(), field: "zdjecie"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("Brak pliku"));
    }

    // serwer przyjmuje dowolny status (kolejność pilnuje aplikacja) i opisuje to w Swaggerze
    [Test]
    public async Task Status_may_go_backwards_by_design_and_swagger_says_so()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        report.Status = ReportStatus.Done;
        await Api.CreateReportAsync(client, report);
        report.Status = ReportStatus.New;
        Assert.That((await Api.PutReportAsync(client, report)).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var swagger = await _factory.CreateClient().GetStringAsync("swagger/v1/swagger.json");
        Assert.That(swagger, Does.Contain("NIE pilnuje kolejności"));
    }

    // PIN porównywany dokładnie, bez obcinania spacji
    [TestCase("1234 ")]
    [TestCase(" 1234")]
    [TestCase("12345")]
    public async Task Login_pin_must_match_exactly(string pin)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("api/auth/login", new { email = "jan@firma.pl", pin });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // po przekroczeniu limitu logowań z jednego IP → 429
    [Test]
    public async Task Login_rate_limit_returns_429_after_limit()
    {
        using var factory = new ApiFactory(new() { ["RateLimiting:Enabled"] = "true", ["RateLimiting:AuthPermitsPerMinute"] = "3" });
        var client = factory.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            codes.Add((await client.PostAsJsonAsync("api/auth/login", new { email = "jan@firma.pl", pin = "0000" })).StatusCode);

        Assert.That(codes.Take(3), Is.All.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(codes.Skip(3), Is.All.EqualTo(HttpStatusCode.TooManyRequests));
    }
}
