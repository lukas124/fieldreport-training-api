using System.Net;
using System.Net.Http.Json;
using FieldReport.Api.Contracts;
using FieldReport.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldReport.Api.Tests;

public sealed class ReportsTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory(new() { ["Storage:MaxPhotoBytes"] = "4096" });
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    private static string NewKey() => $"\"{Guid.NewGuid()}\"";

    private FieldReportDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<FieldReportDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    [Test]
    public async Task Post_creates_report_with_id_from_server_201()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();

        var response = await Api.PostReportAsync(client, report, NewKey());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created), await response.Content.ReadAsStringAsync());
        var created = await Api.ReadAsync<ReportDto>(response);
        Assert.Multiple(() =>
        {
            Assert.That(created.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(response.Headers.Location?.ToString(), Does.EndWith($"/api/reports/{created.Id}"));
            Assert.That(created.Title, Is.EqualTo("Awaria pompy"));
            Assert.That(created.CreatedAt, Is.EqualTo(report.CreatedAt));
            Assert.That(created.HasPhoto, Is.False);
        });
    }

    [Test]
    public async Task Post_repeated_with_same_key_returns_same_report_without_second_record()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        var key = NewKey();

        var first = await Api.PostReportAsync(client, report, key);
        var repeated = await Api.PostReportAsync(client, report, key); // np. odpowiedź na pierwsze zginęła

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That((await Api.ReadAsync<ReportDto>(repeated)).Id, Is.EqualTo((await Api.ReadAsync<ReportDto>(first)).Id));
        var all = await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);
        Assert.That(all, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Post_key_without_quotes_is_the_same_key()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        var key = Guid.NewGuid().ToString();

        var quoted = await Api.ReadAsync<ReportDto>(await Api.PostReportAsync(client, report, $"\"{key}\""));
        var plain = await Api.ReadAsync<ReportDto>(await Api.PostReportAsync(client, report, key));

        Assert.That(plain.Id, Is.EqualTo(quoted.Id));
    }

    [Test]
    public async Task Post_same_key_with_different_body_returns_422()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var key = NewKey();
        await Api.PostReportAsync(client, Api.NewReport("Pierwsze"), key);

        var response = await Api.PostReportAsync(client, Api.NewReport("Inna treść"), key);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        Assert.That((await Api.ReadAsync<ProblemDetails>(response)).Status, Is.EqualTo(422));
        Assert.That(await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Post_repeated_with_other_status_returns_saved_report_not_422()
    {
        // odpowiedź na POST zginęła, technik zmienił status w telefonie, aplikacja ponawia POST z tym samym kluczem
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        var key = NewKey();
        var first = await Api.ReadAsync<ReportDto>(await Api.PostReportAsync(client, report, key));

        report.Status = ReportStatus.InProgress;
        var repeated = await Api.PostReportAsync(client, report, key);

        Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var saved = await Api.ReadAsync<ReportDto>(repeated);
        Assert.That(saved.Id, Is.EqualTo(first.Id));
        Assert.That(saved.Status, Is.EqualTo(ReportStatus.New), "zapisany stan — nowy status aplikacja wysyła przez PUT");
        Assert.That(await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Post_without_idempotency_key_returns_400_problem_details()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);

        var response = await Api.PostReportAsync(client, Api.NewReport(), idempotencyKey: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await Api.ReadAsync<ProblemDetails>(response);
        Assert.That(problem.Status, Is.EqualTo(400));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("Nagłówek Idempotency-Key jest wymagany"));
        Assert.That(await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Is.Empty);
    }

    [Test]
    public async Task Post_same_key_while_first_request_is_saving_returns_409()
    {
        var (client, email) = await Api.AuthorizedClientAsync(_factory);
        var key = Guid.NewGuid().ToString();

        // „pierwsze żądanie” w toku: transakcja zapisała klucz, ale jeszcze się nie zakończyła
        using var db = OpenDb();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            TechnicianEmail = email, Key = key, RequestHash = "w toku", ReportId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // drugie żądanie nie widzi jeszcze klucza i czeka na unikalnym kluczu w bazie
        var second = Api.PostReportAsync(client, Api.NewReport(), key);
        await Task.Delay(500);
        await transaction.CommitAsync();
        var response = await second;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), await response.Content.ReadAsStringAsync());
        Assert.That(await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Is.Empty);
    }

    [Test]
    public async Task Post_key_older_than_24h_is_removed_and_works_as_new()
    {
        var (client, email) = await Api.AuthorizedClientAsync(_factory);
        var key = Guid.NewGuid().ToString();
        var first = await Api.ReadAsync<ReportDto>(await Api.PostReportAsync(client, Api.NewReport(), key));
        using (var db = OpenDb())
        {
            db.IdempotencyKeys.Where(k => k.TechnicianEmail == email)
                .ExecuteUpdate(k => k.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddHours(-25)));
        }

        var again = await Api.ReadAsync<ReportDto>(await Api.PostReportAsync(client, Api.NewReport(), key));

        Assert.That(again.Id, Is.Not.EqualTo(first.Id));
    }

    [Test]
    public async Task Same_key_from_two_technicians_creates_two_reports()
    {
        var (a, _) = await Api.AuthorizedClientAsync(_factory);
        var (b, _) = await Api.AuthorizedClientAsync(_factory);
        var key = NewKey();

        var forA = await Api.PostReportAsync(a, Api.NewReport(), key);
        var forB = await Api.PostReportAsync(b, Api.NewReport(), key);

        Assert.That(forA.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(forB.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That((await Api.ReadAsync<ReportDto>(forB)).Id, Is.Not.EqualTo((await Api.ReadAsync<ReportDto>(forA)).Id));
    }

    [Test]
    public async Task Put_updates_existing_report_200()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(client, report);

        report.Status = ReportStatus.InProgress;
        report.Title = "Awaria pompy — poprawione";
        var response = await Api.PutReportAsync(client, report);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var all = await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);
        Assert.That(all, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(all![0].Id, Is.EqualTo(report.Id));
            Assert.That(all[0].Status, Is.EqualTo(ReportStatus.InProgress));
            Assert.That(all[0].Title, Is.EqualTo("Awaria pompy — poprawione"));
            Assert.That(all[0].CreatedAt, Is.EqualTo(report.CreatedAt));
        });
    }

    [Test]
    public async Task Put_of_missing_report_returns_404_and_creates_nothing()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        report.Id = Guid.NewGuid();

        var response = await Api.PutReportAsync(client, report);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Is.Empty);
    }

    [Test]
    public async Task Post_serializes_enums_as_strings()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);

        var json = await (await Api.CreateReportAsync(client, Api.NewReport())).Content.ReadAsStringAsync();

        Assert.That(json, Does.Contain("\"priority\":\"High\"").And.Contain("\"status\":\"New\""));
    }

    [Test]
    public async Task Put_with_mismatched_id_or_invalid_title_returns_400()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(client, report);

        var mismatch = await client.PutAsJsonAsync($"api/reports/{Guid.NewGuid()}", report, Api.Json);
        report.Title = "ab";
        var tooShort = await Api.PutReportAsync(client, report);

        Assert.That(mismatch.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(tooShort.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Get_by_id_returns_report_or_404()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(client, report);

        var found = await client.GetFromJsonAsync<ReportDto>($"api/reports/{report.Id}", Api.Json);
        var missing = await client.GetAsync($"api/reports/{Guid.NewGuid()}");

        Assert.That(found!.Title, Is.EqualTo(report.Title));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task List_returns_all_reports_newest_change_first()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var first = await Api.ReadAsync<ReportDto>(await Api.CreateReportAsync(client, Api.NewReport(title: "Pierwsze")));
        var second = await Api.ReadAsync<ReportDto>(await Api.CreateReportAsync(client, Api.NewReport(title: "Drugie")));

        var list = await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);

        Assert.That(list!.Select(r => r.Id), Is.EqualTo(new[] { second.Id, first.Id }));
    }

    [Test]
    public async Task Technicians_are_isolated()
    {
        var (a, _) = await Api.AuthorizedClientAsync(_factory, Api.NewEmail("anna"));
        var (b, _) = await Api.AuthorizedClientAsync(_factory, Api.NewEmail("bartek"));
        var report = Api.NewReport(title: "Tylko dla Anny");
        await Api.CreateReportAsync(a, report);

        var bList = await b.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);
        var bGet = await b.GetAsync($"api/reports/{report.Id}");
        var bPhoto = await b.GetAsync($"api/reports/{report.Id}/photo");
        var bPut = await Api.PutReportAsync(b, report); // id zgłoszenia Anny
        var aAfter = await a.GetFromJsonAsync<ReportDto>($"api/reports/{report.Id}", Api.Json);

        Assert.Multiple(() =>
        {
            Assert.That(bList, Is.Empty);
            Assert.That(bGet.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(bPhoto.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(bPut.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "B nie zmieni zgłoszenia A");
            Assert.That(aAfter!.Title, Is.EqualTo("Tylko dla Anny"), "dane A nietknięte");
        });
    }

    [Test]
    public async Task Photo_upload_then_get_returns_same_bytes_and_sets_photo_url()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(client, report);
        var jpeg = Api.FakeJpeg(2048);

        var upload = await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(jpeg));
        Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK), await upload.Content.ReadAsStringAsync());
        var dto = await Api.ReadAsync<ReportDto>(upload);
        Assert.That(dto.HasPhoto, Is.True);
        Assert.That(dto.PhotoUrl, Does.Contain($"/api/reports/{report.Id}/photo?"));

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
        await Api.CreateReportAsync(client, report);

        // limit w tej fabryce: 4096 B (+ zapas na multipart); 200 KB przekracza oba progi
        var response = await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(Api.FakeJpeg(200 * 1024)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task Photo_slightly_over_limit_returns_413_from_length_check()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(client, report);

        var response = await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(Api.FakeJpeg(4097)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task Photo_not_an_image_returns_415_and_missing_report_404_and_no_photo_404()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = Api.NewReport();
        await Api.CreateReportAsync(client, report);

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
        var email = Api.NewEmail();
        var report = Api.NewReport();
        var first = new ApiFactory { KeepData = true };
        using (first)
        {
            var (client, _) = await Api.AuthorizedClientAsync(first, email);
            await Api.CreateReportAsync(client, report);
        }

        // drugi proces API na tej samej bazie i katalogu danych
        using var second = new ApiFactory(new()
        {
            ["ConnectionStrings:FieldReport"] = first.ConnectionString,
            ["Storage:DataDirectory"] = first.DataDirectory
        });
        var (again, _) = await Api.AuthorizedClientAsync(second, email);
        var list = await again.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);

        Assert.That(list!.Select(r => r.Id), Is.EqualTo(new[] { report.Id }));
    }
}

/// <summary>Baza założona przed dodaniem tabeli kluczy: API dokłada tabelę przy starcie, nie ruszając danych.</summary>
public sealed class IdempotencyKeysTableUpgradeTests
{
    [Test]
    public async Task Existing_database_without_keys_table_gets_it_on_start_and_keeps_data()
    {
        var email = Api.NewEmail();
        var report = Api.NewReport();
        var first = new ApiFactory { KeepData = true };
        using (first)
        {
            var (client, _) = await Api.AuthorizedClientAsync(first, email);
            await Api.CreateReportAsync(client, report);
        }
        using (var db = new FieldReportDbContext(new DbContextOptionsBuilder<FieldReportDbContext>().UseNpgsql(first.ConnectionString).Options))
        {
            db.Database.ExecuteSqlRaw("DROP TABLE \"IdempotencyKeys\""); // stan produkcyjnej bazy sprzed zmiany
        }

        using var second = new ApiFactory(new()
        {
            ["ConnectionStrings:FieldReport"] = first.ConnectionString,
            ["Storage:DataDirectory"] = first.DataDirectory
        });
        var (again, _) = await Api.AuthorizedClientAsync(second, email);
        var created = await Api.CreateReportAsync(again, Api.NewReport("Po aktualizacji"));
        var list = await again.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);

        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(list!.Select(r => r.Id), Does.Contain(report.Id));
        Assert.That(list, Has.Count.EqualTo(2));
    }
}
