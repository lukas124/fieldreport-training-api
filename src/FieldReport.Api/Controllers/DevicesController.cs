using FieldReport.Api.Auth;
using FieldReport.Api.Contracts;
using FieldReport.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldReport.Api.Controllers;

/// <summary>Rejestracja urządzeń do push (F7).</summary>
[ApiController]
[Route("api/devices")]
[Authorize]
[Produces("application/json")]
public sealed class DevicesController(DataStore store) : ControllerBase
{
    private static readonly string[] Platforms = ["android", "ios"];

    /// <summary>Rejestracja tokenu FCM urządzenia zalogowanego technika.</summary>
    /// <remarks>
    /// Wywołuj po zgodzie na powiadomienia i po każdej zmianie tokenu. Ponowna rejestracja tego samego tokenu
    /// jest bezpieczna (nadpisuje). Token zarejestrowany wcześniej przez innego technika zostaje przeniesiony.
    ///
    ///     POST /api/devices
    ///     { "token": "fXk3...:APA91bH...", "platform": "android" }
    /// </remarks>
    /// <response code="200">Zarejestrowano.</response>
    /// <response code="400">Brak tokenu lub nieznana platforma (dozwolone: android, ios).</response>
    [HttpPost]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<DeviceDto> Register([FromBody] RegisterDeviceRequest request)
    {
        var token = request.Token?.Trim();
        var platform = request.Platform?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(token) || token.Length > 4096 || token.Any(char.IsWhiteSpace))
            return Problem(title: "Niepoprawny token", detail: "Pole 'token' jest wymagane (maks. 4096 znaków, bez spacji).", statusCode: StatusCodes.Status400BadRequest);
        if (platform is null || !Platforms.Contains(platform))
            return Problem(title: "Nieznana platforma", detail: "Pole 'platform' musi mieć wartość 'android' lub 'ios'.", statusCode: StatusCodes.Status400BadRequest);

        var device = store.RegisterDevice(User.Email(), User.Name(), token, platform)!;
        return new DeviceDto { Token = device.Token, Platform = device.Platform, RegisteredAt = device.RegisteredAt };
    }

    /// <summary>Wyrejestrowanie tokenu (przy wylogowaniu). Idempotentne — nieznany token też daje 204.</summary>
    /// <param name="token" example="fXk3...:APA91bH...">Token FCM (zakodowany w adresie, np. Uri.EscapeDataString).</param>
    /// <response code="204">Wyrejestrowano (lub tokenu nie było).</response>
    [HttpDelete("{token}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Unregister(string token)
    {
        store.RemoveDevice(User.Email(), Uri.UnescapeDataString(token).Trim());
        return NoContent();
    }
}
