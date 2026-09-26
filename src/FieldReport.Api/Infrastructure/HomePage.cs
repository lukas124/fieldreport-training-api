using System.Net;
using FieldReport.Api.Controllers;
using FieldReport.Api.Push;

namespace FieldReport.Api.Infrastructure;

/// <summary>Prosta strona informacyjna pod <c>GET /</c>.</summary>
public static class HomePage
{
    public static string Render(string pathBase, IPushSender push)
    {
        var b = WebUtility.HtmlEncode(pathBase);
        var firebase = WebUtility.HtmlEncode(push.StatusDescription);
        return $$"""
        <!doctype html>
        <html lang="pl">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>FieldReport API — szkolenie .NET MAUI</title>
          <style>
            :root { color-scheme: light dark; --accent: #2f7de1; }
            body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; max-width: 46rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }
            code { font-family: ui-monospace, "JetBrains Mono", Menlo, monospace; font-size: .9em; }
            a.button { display: inline-block; padding: .6rem 1rem; border-radius: .5rem; background: var(--accent); color: #fff; text-decoration: none; font-weight: 600; }
            table { border-collapse: collapse; width: 100%; font-size: .92rem; }
            td { padding: .3rem .5rem; border-bottom: 1px solid color-mix(in srgb, currentColor 15%, transparent); vertical-align: top; }
            .muted { opacity: .7; }
          </style>
        </head>
        <body>
          <h1>FieldReport API</h1>
          <p>API szkoleniowe do aplikacji <strong>FieldReport</strong> (szkolenie „Podstawy .NET MAUI”). Wersja {{HealthController.Version}}.</p>
          <p><a class="button" href="{{b}}/swagger">Otwórz Swaggera</a> &nbsp; <a href="{{b}}/api/health">/api/health</a></p>
          <p class="muted">Push: {{firebase}}</p>
          <h2>Jak zacząć</h2>
          <ol>
            <li><code>POST {{b}}/api/auth/login</code> z dowolnym poprawnym e-mailem i PIN-em <code>1234</code>.</li>
            <li>W Swaggerze: <em>Authorize</em> → wklej <code>accessToken</code>.</li>
            <li>Dyspozytor (bez logowania): <code>POST {{b}}/api/dispatcher/reports</code> → zgłoszenie + push.</li>
          </ol>
          <h2>Endpointy</h2>
          <table>
            <tr><td><code>POST /api/auth/login</code></td><td>logowanie → tokeny</td></tr>
            <tr><td><code>POST /api/auth/refresh</code></td><td>nowy token</td></tr>
            <tr><td><code>GET /api/reports?updatedSince=</code></td><td>zmienione zgłoszenia</td></tr>
            <tr><td><code>GET|PUT /api/reports/{id}</code></td><td>szczegóły / upsert</td></tr>
            <tr><td><code>GET|PUT /api/reports/{id}/photo</code></td><td>zdjęcie (multipart)</td></tr>
            <tr><td><code>POST /api/devices</code>, <code>DELETE /api/devices/{token}</code></td><td>rejestracja push</td></tr>
            <tr><td><code>POST /api/dispatcher/reports</code></td><td>dyspozytor: nowe zgłoszenie + <code>report_assigned</code></td></tr>
            <tr><td><code>PATCH /api/dispatcher/reports/{id}/status</code></td><td>dyspozytor: status + <code>report_status_changed</code></td></tr>
          </table>
        </body>
        </html>
        """;
    }
}
