using System.Net;
using System.Net.Http.Json;
using FieldReport.Api.Contracts;

namespace FieldReport.Api.Tests;

public sealed class ReportsTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory(new() { ["Storage:MaxPhotoBytes"] = "4096" });
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Put_twice_with_same_id_creates_one_record_201_then_200()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();

        var first = await Api.PutReportAsync(client, report);
        report.Status = ReportStatus.InProgress;
        report.Title = "Awaria pompy — poprawione";
        var second = await Api.PutReportAsync(client, report);

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(first.Headers.Location?.ToString(), Does.EndWith($"/api/reports/{report.Id}"));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var all = await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);
        Assert.That(all, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(all![0].Id, Is.EqualTo(report.Id));
            Assert.That(all[0].Status, Is.EqualTo(ReportStatus.InProgress));
            Assert.That(all[0].Title, Is.EqualTo("Awaria pompy — poprawione"));
            Assert.That(all[0].CreatedAt, Is.EqualTo(report.CreatedAt));
            Assert.That(all[0].HasPhoto, Is.False);
        });
    }

    [Test]
    public async Task Put_serializes_enums_as_strings()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();

        var json = await (await Api.PutReportAsync(client, report)).Content.ReadAsStringAsync();

        Assert.That(json, Does.Contain("\"priority\":\"High\"").And.Contain("\"status\":\"New\""));
    }

    [Test]
    public async Task Put_with_mismatched_id_or_invalid_title_returns_400()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();

        var mismatch = await client.PutAsJsonAsync($"api/reports/{Guid.NewGuid()}", report, Api.Json);
        var tooShort = await Api.PutReportAsync(client, Api.NewReport(title: "ab"));

        Assert.That(mismatch.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(tooShort.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Get_by_id_returns_report_or_404()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.PutReportAsync(client, report);

        var found = await client.GetFromJsonAsync<ReportDto>($"api/reports/{report.Id}", Api.Json);
        var missing = await client.GetAsync($"api/reports/{Guid.NewGuid()}");

        Assert.That(found!.Title, Is.EqualTo(report.Title));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UpdatedSince_returns_only_later_changes()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var a = await Api.ReadAsync<ReportDto>(await Api.PutReportAsync(client, Api.NewReport(title: "Pierwsze")));
        var b = await Api.ReadAsync<ReportDto>(await Api.PutReportAsync(client, Api.NewReport(title: "Drugie")));

        var since = Uri.EscapeDataString(a.UpdatedAt.ToString("O"));
        var changes = await client.GetFromJsonAsync<List<ReportDto>>($"api/reports?updatedSince={since}", Api.Json);
        var everything = await client.GetFromJsonAsync<List<ReportDto>>("api/reports?updatedSince=", Api.Json);
        var future = await client.GetFromJsonAsync<List<ReportDto>>($"api/reports?updatedSince={Uri.EscapeDataString(b.UpdatedAt.ToString("O"))}", Api.Json);

        Assert.That(changes!.Select(r => r.Id), Is.EqualTo(new[] { b.Id }));
        Assert.That(everything, Has.Count.EqualTo(2));
        Assert.That(everything![0].Id, Is.EqualTo(b.Id), "sortowanie od najnowszej zmiany");
        Assert.That(future, Is.Empty);
    }

    [Test]
    public async Task UpdatedSince_accepts_unescaped_plus_offset_like_the_app_client()
    {
        // klient ze scenariusza: $"api/reports?updatedSince={since:O}" — bez EscapeDataString, "+02:00" dociera jako spacja
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        await Api.PutReportAsync(client, Api.NewReport());
        var since = new DateTimeOffset(2000, 1, 1, 10, 0, 0, TimeSpan.FromHours(2));

        var response = await client.GetAsync($"api/reports?updatedSince={since:O}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Api.ReadAsync<List<ReportDto>>(response), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task UpdatedSince_invalid_date_returns_400()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var response = await client.GetAsync("api/reports?updatedSince=wczoraj");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Technicians_are_isolated()
    {
        var (a, _) = await Api.AuthorizedClientAsync(_factory, Api.NewEmail("anna"));
        var (b, _) = await Api.AuthorizedClientAsync(_factory, Api.NewEmail("bartek"));
        var report = Api.NewReport(title: "Tylko dla Anny");
        await Api.PutReportAsync(a, report);

        var bList = await b.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);
        var bGet = await b.GetAsync($"api/reports/{report.Id}");
        var bPhoto = await b.GetAsync($"api/reports/{report.Id}/photo");
        var bPut = await Api.PutReportAsync(b, Api.NewReport(report.Id, "Bartek, to samo id"));
        var aAfter = await a.GetFromJsonAsync<ReportDto>($"api/reports/{report.Id}", Api.Json);

        Assert.Multiple(() =>
        {
            Assert.That(bList, Is.Empty);
            Assert.That(bGet.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(bPhoto.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(bPut.StatusCode, Is.EqualTo(HttpStatusCode.Created), "osobny rekord technika B");
            Assert.That(aAfter!.Title, Is.EqualTo("Tylko dla Anny"), "dane A nietknięte");
        });
    }

    [Test]
    public async Task Photo_upload_then_get_returns_same_bytes_and_sets_photo_url()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.PutReportAsync(client, report);
        var jpeg = Api.FakeJpeg(2048);

        var upload = await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(jpeg));
        Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK), await upload.Content.ReadAsStringAsync());
        var dto = await Api.ReadAsync<ReportDto>(upload);
        Assert.That(dto.HasPhoto, Is.True);
        Assert.That(dto.PhotoUrl, Does.EndWith($"/api/reports/{report.Id}/photo"));

        var photo = await client.GetAsync($"api/reports/{report.Id}/photo");
        Assert.That(photo.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(photo.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
        Assert.That(await photo.Content.ReadAsByteArrayAsync(), Is.EqualTo(jpeg));

        var changed = await client.GetFromJsonAsync<ReportDto>($"api/reports/{report.Id}", Api.Json);
        Assert.That(changed!.UpdatedAt, Is.GreaterThan(dto.CreatedAt));
    }

    [Test]
    public async Task Photo_too_large_returns_413()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.PutReportAsync(client, report);

        // limit w tej fabryce: 4096 B (+ zapas na multipart); 200 KB przekracza oba progi
        var response = await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(Api.FakeJpeg(200 * 1024)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task Photo_slightly_over_limit_returns_413_from_length_check()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.PutReportAsync(client, report);

        var response = await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(Api.FakeJpeg(4097)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task Photo_not_an_image_returns_415_and_missing_report_404_and_no_photo_404()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.PutReportAsync(client, report);

        var text = await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent("to nie jest obraz"u8.ToArray()));
        var missing = await client.PutAsync($"api/reports/{Guid.NewGuid()}/photo", Api.PhotoContent(Api.FakeJpeg()));
        var noPhoto = await client.GetAsync($"api/reports/{report.Id}/photo");

        Assert.That(text.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(noPhoto.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Data_survive_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fieldreport-api-tests", Guid.NewGuid().ToString("N"));
        var email = Api.NewEmail();
        var report = Api.NewReport();
        using (var first = new ApiFactory(new() { ["Storage:DataDirectory"] = dir }))
        {
            var (client, _) = await Api.AuthorizedClientAsync(first, email);
            await Api.PutReportAsync(client, report);
        }

        using var second = new ApiFactory(new() { ["Storage:DataDirectory"] = dir });
        var (again, _) = await Api.AuthorizedClientAsync(second, email);
        var list = await again.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);

        Assert.That(list!.Select(r => r.Id), Is.EqualTo(new[] { report.Id }));
        Directory.Delete(dir, recursive: true);
    }
}
