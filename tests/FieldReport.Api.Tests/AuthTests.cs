using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FieldReport.Api.Contracts;

namespace FieldReport.Api.Tests;

public sealed class AuthTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Login_valid_email_and_pin_returns_tokens_and_name()
    {
        var client = _factory.CreateClient();

        var tokens = await Api.LoginAsync(client, "jan.kowalski@firma.pl");

        Assert.Multiple(() =>
        {
            Assert.That(tokens.AccessToken, Is.Not.Empty);
            Assert.That(tokens.RefreshToken, Is.Not.Empty);
            Assert.That(tokens.ExpiresIn, Is.EqualTo(600));
            Assert.That(tokens.TechnicianName, Is.EqualTo("Jan Kowalski"));
        });
    }

    [TestCase("jan.kowalski@firma.pl", "0000")]
    [TestCase("to-nie-jest-email", "1234")]
    [TestCase("", "1234")]
    public async Task Login_wrong_pin_or_email_returns_401_problem(string email, string pin)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("api/auth/login", new { email, pin });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        // [Produces("application/json")] na kontrolerze → ProblemDetails też jako application/json
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json").Or.EqualTo("application/problem+json"));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("Nieprawidłowy e-mail lub PIN"));
    }

    [Test]
    public async Task Refresh_rotates_token_and_old_one_is_rejected()
    {
        var client = _factory.CreateClient();
        var first = await Api.LoginAsync(client, Api.NewEmail());

        var refreshed = await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.That(refreshed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var second = await Api.ReadAsync<TokenResponse>(refreshed);
        Assert.That(second.RefreshToken, Is.Not.EqualTo(first.RefreshToken));

        var reused = await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        // nowy access token działa
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        Assert.That((await client.GetAsync("api/reports")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Refresh_unknown_token_returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = "nie-ma-takiego" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Reports_without_token_returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("api/reports");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Expired_access_token_returns_401_with_expired_hint()
    {
        using var factory = new ApiFactory(new() { ["Auth:AccessTokenLifetimeSeconds"] = "1" });
        var (client, _) = await Api.AuthorizedClientAsync(factory);

        await Task.Delay(TimeSpan.FromSeconds(2));
        var response = await client.GetAsync("api/reports");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Headers.WwwAuthenticate.ToString(), Does.Contain("expired"));
    }
}
