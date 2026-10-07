using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldReport.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FieldReport.Api.Tests;

public sealed class AdminResetTests
{
    [Test]
    public async Task Reset_without_or_with_wrong_key_returns_401_and_keeps_data()
    {
        using var factory = new ApiFactory();
        var (client, _) = await Api.AuthorizedClientAsync(factory);
        await Api.CreateReportAsync(client, Api.NewReport());
        var anonymous = factory.CreateClient();

        var without = await anonymous.PostAsync("api/admin/reset", null);
        var wrong = new HttpRequestMessage(HttpMethod.Post, "api/admin/reset");
        wrong.Headers.Add("X-Admin-Key", "zly-klucz");
        var wrongResponse = await anonymous.SendAsync(wrong);

        Assert.That(without.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(wrongResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Reset_with_key_removes_everything()
    {
        using var factory = new ApiFactory();
        var (client, _) = await Api.AuthorizedClientAsync(factory);
        await Api.CreateReportAsync(client, Api.NewReport());
        await Api.CreateReportAsync(client, Api.NewReport());

        var request = new HttpRequestMessage(HttpMethod.Post, "api/admin/reset");
        request.Headers.Add("X-Admin-Key", ApiFactory.AdminKey);
        var response = await factory.CreateClient().SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await Api.ReadAsync<ResetResult>(response);
        Assert.That(result.RemovedReports, Is.EqualTo(2));
        Assert.That(result.RemovedTechnicians, Is.EqualTo(1));
        Assert.That(await client.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Is.Empty);
    }

    [Test]
    public async Task Reset_single_technician_keeps_others()
    {
        using var factory = new ApiFactory();
        var (a, emailA) = await Api.AuthorizedClientAsync(factory);
        var (b, _) = await Api.AuthorizedClientAsync(factory);
        await Api.CreateReportAsync(a, Api.NewReport());
        await Api.CreateReportAsync(b, Api.NewReport());

        var request = new HttpRequestMessage(HttpMethod.Post, $"api/admin/reset?technicianEmail={Uri.EscapeDataString(emailA)}");
        request.Headers.Add("X-Admin-Key", ApiFactory.AdminKey);
        var result = await Api.ReadAsync<ResetResult>(await factory.CreateClient().SendAsync(request));

        Assert.That(result.RemovedTechnicians, Is.EqualTo(1));
        Assert.That(await a.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Is.Empty);
        Assert.That(await b.GetFromJsonAsync<List<ReportDto>>("api/reports", Api.Json), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Reset_is_disabled_without_configured_key()
    {
        using var factory = new ApiFactory(new() { ["Admin:Key"] = "" });
        var request = new HttpRequestMessage(HttpMethod.Post, "api/admin/reset");
        request.Headers.Add("X-Admin-Key", "cokolwiek");
        var response = await factory.CreateClient().SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}

public sealed class HostingTests
{
    private const string Prefix = "/maui/podstawowe";

    [Test]
    public async Task Health_reports_firebase_not_configured()
    {
        using var factory = new ApiFactory(new() { ["Firebase:Disabled"] = "false", ["Firebase:CredentialsPath"] = "/nie/ma/takiego/pliku.json" });
        var health = await factory.CreateClient().GetFromJsonAsync<HealthDto>("api/health", Api.Json);

        Assert.That(health!.Status, Is.EqualTo("ok"));
        Assert.That(health.FirebaseConfigured, Is.False);
        Assert.That(health.FirebaseStatus, Does.Contain("nie jest skonfigurowany"));
        Assert.That(health.PathBase, Is.Empty);
    }

    [Test]
    public async Task Health_answers_head_requests()
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Head, "api/health"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task PathBase_serves_api_under_prefix()
    {
        using var factory = new ApiFactory(new() { ["PathBase"] = Prefix + "/" });
        var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<HealthDto>($"{Prefix}/api/health", Api.Json);
        Assert.That(health!.PathBase, Is.EqualTo(Prefix));

        var login = await client.PostAsJsonAsync($"{Prefix}/api/auth/login", new { email = "jan@firma.pl", pin = "1234" });
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var home = await client.GetStringAsync($"{Prefix}/");
        Assert.That(home, Does.Contain($"href=\"{Prefix}/swagger\""));
    }

    [Test]
    public async Task PathBase_photoUrl_and_location_include_prefix()
    {
        using var factory = new ApiFactory(new() { ["PathBase"] = Prefix });
        var client = factory.CreateClient();
        client.BaseAddress = new Uri(client.BaseAddress!, Prefix + "/");
        var tokens = await Api.LoginAsync(client, Api.NewEmail());
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        var report = Api.NewReport();

        var put = await Api.CreateReportAsync(client, report);
        var photo = await Api.ReadAsync<ReportDto>(await client.PutAsync($"api/reports/{report.Id}/photo", Api.PhotoContent(Api.FakeJpeg())));

        Assert.That(put.Headers.Location!.ToString(), Does.Contain($"{Prefix}/api/reports/{report.Id}"));
        Assert.That(photo.PhotoUrl, Does.Contain($"{Prefix}/api/reports/{report.Id}/photo"));
        Assert.That((await client.GetAsync($"api/reports/{report.Id}/photo")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Swagger_json_servers_contains_prefix_and_ui_is_served()
    {
        using var factory = new ApiFactory(new() { ["PathBase"] = Prefix });
        var client = factory.CreateClient();

        var json = await client.GetStringAsync($"{Prefix}/swagger/v1/swagger.json");
        using var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("servers").EnumerateArray().Select(s => s.GetProperty("url").GetString()).ToList();
        var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();

        Assert.That(servers, Is.EqualTo(new[] { Prefix }));
        Assert.That(paths, Is.SupersetOf(new[]
        {
            "/api/auth/login", "/api/auth/refresh", "/api/reports", "/api/reports/{id}", "/api/reports/{id}/photo",
            "/api/devices", "/api/devices/{token}", "/api/dispatcher/reports", "/api/dispatcher/reports/{id}/status",
            "/api/admin/reset", "/api/health"
        }));

        var ui = await client.GetAsync($"{Prefix}/swagger/index.html");
        Assert.That(ui.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ui.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        Assert.That(await ui.Content.ReadAsStringAsync(), Does.Contain("swagger-ui"));
    }

    [Test]
    public async Task Swagger_without_prefix_has_root_server()
    {
        using var factory = new ApiFactory();
        var json = await factory.CreateClient().GetStringAsync("swagger/v1/swagger.json");
        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("servers")[0].GetProperty("url").GetString(), Is.EqualTo("/"));
    }

    [Test]
    public async Task Health_reports_database_connected()
    {
        using var factory = new ApiFactory();
        var health = await factory.CreateClient().GetFromJsonAsync<HealthDto>("api/health", Api.Json);
        Assert.That(health!.DatabaseConnected, Is.True);
    }

    [TestCase("/")]
    [TestCase("/swagger")]
    [TestCase("/maui/inne/api/health")]
    public async Task Paths_outside_pathbase_redirect_301_when_configured(string path)
    {
        using var factory = new ApiFactory(new() { ["PathBase"] = Prefix, ["RedirectOutsidePathBase"] = "https://strefakodera.pl/szkolenia" });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MovedPermanently));
        Assert.That(response.Headers.Location!.ToString(), Is.EqualTo("https://strefakodera.pl/szkolenia"));
        Assert.That((await client.GetAsync($"{Prefix}/api/health")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
