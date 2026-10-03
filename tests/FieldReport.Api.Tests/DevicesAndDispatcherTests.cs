using System.Net;
using System.Net.Http.Json;
using FieldReport.Api.Contracts;
using FieldReport.Api.Push;

namespace FieldReport.Api.Tests;

public sealed class DevicesTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Register_list_and_unregister_device()
    {
        var (client, email) = await Api.AuthorizedClientAsync(_factory);
        var token = "fcm-token-" + Guid.NewGuid().ToString("N") + ":APA91b";

        var register = await client.PostAsJsonAsync("api/devices", new { token, platform = "Android" });
        Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var device = await Api.ReadAsync<DeviceDto>(register);
        Assert.That(device.Platform, Is.EqualTo("android"));

        // ponowna rejestracja tego samego tokenu nie dubluje
        await client.PostAsJsonAsync("api/devices", new { token, platform = "android" });
        Assert.That(await DeviceCount(email), Is.EqualTo(1));

        var delete = await client.DeleteAsync($"api/devices/{Uri.EscapeDataString(token)}");
        Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await DeviceCount(email), Is.EqualTo(0));

        var again = await client.DeleteAsync($"api/devices/{Uri.EscapeDataString(token)}");
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "idempotentne");
    }

    [TestCase("", "android")]
    [TestCase("abc", "windows")]
    [TestCase("ab c", "ios")]
    public async Task Register_invalid_returns_400(string token, string platform)
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var response = await client.PostAsJsonAsync("api/devices", new { token, platform });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Register_requires_token()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("api/devices", new { token = "x", platform = "android" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Token_moves_to_the_technician_who_registered_it_last()
    {
        var (a, emailA) = await Api.AuthorizedClientAsync(_factory);
        var (b, emailB) = await Api.AuthorizedClientAsync(_factory);
        var token = "shared-" + Guid.NewGuid().ToString("N");

        await a.PostAsJsonAsync("api/devices", new { token, platform = "ios" });
        await b.PostAsJsonAsync("api/devices", new { token, platform = "ios" });

        Assert.That(await DeviceCount(emailA), Is.EqualTo(0));
        Assert.That(await DeviceCount(emailB), Is.EqualTo(1));
    }

    private async Task<int> DeviceCount(string email)
    {
        var technicians = await _factory.CreateClient().GetFromJsonAsync<List<DispatcherTechnicianDto>>("api/dispatcher/technicians", Api.Json);
        return technicians!.Single(t => t.Email == email.ToLowerInvariant()).Devices.Count;
    }
}

public sealed class DispatcherWithoutFirebaseTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Create_report_for_technician_is_visible_in_app_and_push_is_skipped()
    {
        var (client, email) = await Api.AuthorizedClientAsync(_factory);
        await client.PostAsJsonAsync("api/devices", new { token = "tok-" + Guid.NewGuid().ToString("N"), platform = "android" });

        var response = await _factory.CreateClient().PostAsJsonAsync("api/dispatcher/reports",
            new { technicianEmail = email, title = "Awaria pompy", description = "Budynek B", priority = "High" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var result = await Api.ReadAsync<DispatcherResult>(response);
        Assert.Multiple(() =>
        {
            Assert.That(result.Reports, Has.Count.EqualTo(1));
            Assert.That(result.Reports[0].Report.Status, Is.EqualTo(ReportStatus.New));
            Assert.That(result.Push.Type, Is.EqualTo(PushTypes.ReportAssigned));
            Assert.That(result.Push.FirebaseConfigured, Is.False);
            Assert.That(result.Push.Skipped, Is.True);
            Assert.That(result.Push.TargetDevices, Is.EqualTo(1));
            Assert.That(result.Push.Sent, Is.EqualTo(0));
        });

        var mine = await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json);
        Assert.That(mine!.Select(r => r.Id), Does.Contain(result.Reports[0].Report.Id));
    }

    [Test]
    public async Task Change_status_updates_report_and_updatedAt()
    {
        var (client, _) = await Api.AuthorizedClientAsync(_factory);
        var report = await Api.ReadAsync<ReportDto>(await Api.PutReportAsync(client, Api.NewReport()));

        var response = await _factory.CreateClient().PatchAsJsonAsync($"api/dispatcher/reports/{report.Id}/status", new { status = "Done" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await Api.ReadAsync<DispatcherResult>(response);
        Assert.That(result.Push.Type, Is.EqualTo(PushTypes.ReportStatusChanged));
        Assert.That(result.Push.Skipped, Is.True);

        var changed = await client.GetFromJsonAsync<ReportDto>($"api/reports/{report.Id}", Api.Json);
        Assert.That(changed!.Status, Is.EqualTo(ReportStatus.Done));
        Assert.That(changed.UpdatedAt, Is.GreaterThan(report.UpdatedAt));
    }

    [Test]
    public async Task Change_status_of_unknown_report_returns_404()
    {
        var response = await _factory.CreateClient().PatchAsJsonAsync($"api/dispatcher/reports/{Guid.NewGuid()}/status", new { status = "Done" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Create_report_with_invalid_email_or_title_returns_400()
    {
        var client = _factory.CreateClient();
        var badEmail = await client.PostAsJsonAsync("api/dispatcher/reports", new { technicianEmail = "zly", title = "Awaria pompy" });
        var badTitle = await client.PostAsJsonAsync("api/dispatcher/reports", new { technicianEmail = "jan@firma.pl", title = "x" });
        Assert.That(badEmail.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(badTitle.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Empty_technician_email_targets_every_technician()
    {
        using var factory = new ApiFactory();
        var (a, _) = await Api.AuthorizedClientAsync(factory);
        var (b, _) = await Api.AuthorizedClientAsync(factory);

        var result = await Api.ReadAsync<DispatcherResult>(
            await factory.CreateClient().PostAsJsonAsync("api/dispatcher/reports", new { technicianEmail = "", title = "Dla wszystkich" }));

        Assert.That(result.Reports, Has.Count.EqualTo(2));
        Assert.That(await a.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Has.Count.EqualTo(1));
        Assert.That(await b.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Dispatcher_key_is_enforced_when_configured()
    {
        using var factory = new ApiFactory(new() { ["Dispatcher:Key"] = "tajny" });
        var client = factory.CreateClient();

        var without = await client.GetAsync("api/dispatcher/reports");
        client.DefaultRequestHeaders.Add("X-Dispatcher-Key", "tajny");
        var with = await client.GetAsync("api/dispatcher/reports");

        Assert.That(without.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(with.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}

public sealed class DispatcherPushPayloadTests
{
    [Test]
    public async Task Push_carries_type_and_reportId_and_dead_tokens_are_removed()
    {
        var sender = new RecordingPushSender();
        using var factory = new ApiFactory(pushSender: sender);
        var (client, email) = await Api.AuthorizedClientAsync(factory);
        await client.PostAsJsonAsync("api/devices", new { token = "alive-token", platform = "android" });
        await client.PostAsJsonAsync("api/devices", new { token = "dead-token", platform = "android" });
        sender.DeadTokens.Add("dead-token");

        var result = await Api.ReadAsync<DispatcherResult>(await factory.CreateClient()
            .PostAsJsonAsync("api/dispatcher/reports", new { technicianEmail = email, title = "Awaria pompy" }));

        var reportId = result.Reports.Single().Report.Id;
        Assert.Multiple(() =>
        {
            Assert.That(result.Push.Skipped, Is.False);
            Assert.That(result.Push.Sent, Is.EqualTo(1));
            Assert.That(result.Push.RemovedTokens, Is.EqualTo(1));
            Assert.That(sender.Sent.Single().Token, Is.EqualTo("alive-token"));
            Assert.That(sender.Sent.Single().Message.Type, Is.EqualTo("report_assigned"));
            Assert.That(sender.Sent.Single().Message.ReportId, Is.EqualTo(reportId));
        });

        var technicians = await factory.CreateClient().GetFromJsonAsync<List<DispatcherTechnicianDto>>("api/dispatcher/technicians", Api.Json);
        Assert.That(technicians!.Single().Devices, Has.Count.EqualTo(1));
    }
}
