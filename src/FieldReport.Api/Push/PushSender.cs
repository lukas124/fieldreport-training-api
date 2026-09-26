using System.Text.Json;
using FieldReport.Api.Infrastructure;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace FieldReport.Api.Push;

public static class PushTypes
{
    public const string ReportAssigned = "report_assigned";
    public const string ReportStatusChanged = "report_status_changed";
    public const string AndroidChannelId = "reports";
}

/// <summary>Treść powiadomienia: notification (tytuł/treść) + data (<c>type</c>, <c>reportId</c>).</summary>
public sealed record PushMessage(string Type, Guid ReportId, string Title, string Body);

public enum PushSendOutcome { Sent, Unregistered, Failed }

public sealed record PushSendResult(PushSendOutcome Outcome, string? Error = null);

/// <summary>Jedyne miejsce znające Firebase Admin SDK.</summary>
public interface IPushSender
{
    bool IsConfigured { get; }
    string StatusDescription { get; }
    Task<PushSendResult> SendAsync(string token, PushMessage message, CancellationToken ct);
}

/// <summary>
/// Wysyłka przez FCM HTTP v1 (Firebase Admin SDK). Poświadczenia: <c>Firebase:CredentialsPath</c>
/// albo zmienna <c>GOOGLE_APPLICATION_CREDENTIALS</c> (plik konta serwisowego poza repo).
/// Brak poświadczeń = API działa, wysyłka jest pomijana.
/// </summary>
public sealed class FirebasePushSender : IPushSender
{
    private const string AppName = "fieldreport-api";
    private readonly FirebaseMessaging? _messaging;
    private readonly ILogger<FirebasePushSender> _logger;

    public FirebasePushSender(IOptions<FirebaseOptions> options, ILogger<FirebasePushSender> logger)
    {
        _logger = logger;
        var o = options.Value;
        if (o.Disabled)
        {
            StatusDescription = "Firebase wyłączony w konfiguracji (Firebase:Disabled=true).";
            return;
        }

        var fromConfig = !string.IsNullOrWhiteSpace(o.CredentialsPath);
        var path = fromConfig ? o.CredentialsPath : Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusDescription = "Firebase nie jest skonfigurowany (brak GOOGLE_APPLICATION_CREDENTIALS).";
            return;
        }
        if (!File.Exists(path))
        {
            StatusDescription = $"Firebase nie jest skonfigurowany: plik poświadczeń nie istnieje ({Path.GetFileName(path)}).";
            logger.LogWarning("Plik poświadczeń Firebase nie istnieje: {Path}", path);
            return;
        }

        try
        {
            var projectId = ReadProjectId(path);
            var app = FirebaseApp.GetInstance(AppName) ?? FirebaseApp.Create(new AppOptions
            {
                // tak jak w scenariuszu: GetApplicationDefault() czyta GOOGLE_APPLICATION_CREDENTIALS
                Credential = fromConfig
                    ? CredentialFactory.FromFile<ServiceAccountCredential>(path).ToGoogleCredential()
                    : GoogleCredential.GetApplicationDefault(),
                ProjectId = projectId
            }, AppName);
            _messaging = FirebaseMessaging.GetMessaging(app);
            IsConfigured = true;
            StatusDescription = $"Firebase skonfigurowany (projekt: {projectId ?? "?"}).";
            logger.LogInformation("Firebase Admin SDK gotowy, projekt {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            StatusDescription = $"Firebase nie jest skonfigurowany: błąd poświadczeń ({ex.GetType().Name}).";
            logger.LogError(ex, "Nie udało się zainicjalizować Firebase Admin SDK");
        }
    }

    public bool IsConfigured { get; }
    public string StatusDescription { get; } = "";

    public async Task<PushSendResult> SendAsync(string token, PushMessage message, CancellationToken ct)
    {
        if (_messaging is null) return new PushSendResult(PushSendOutcome.Failed, "Firebase nie jest skonfigurowany.");
        try
        {
            await _messaging.SendAsync(new Message
            {
                // FirebaseAdmin 3.7 oznacza Message.Token jako [Obsolete] na rzecz Fid, ale FCM HTTP v1 nadal adresuje
                // urządzenie tokenem rejestracyjnym z aplikacji (FirebaseMessaging.Instance.GetToken) — Fid to inny
                // identyfikator (instalacji). Zostajemy przy Token, zgodnie ze scenariuszem szkolenia.
#pragma warning disable CS0618
                Token = token,
#pragma warning restore CS0618
                Notification = new Notification { Title = message.Title, Body = message.Body },
                Data = new Dictionary<string, string>
                {
                    ["type"] = message.Type,
                    ["reportId"] = message.ReportId.ToString()
                },
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification { ChannelId = PushTypes.AndroidChannelId }
                }
            }, ct);
            return new PushSendResult(PushSendOutcome.Sent);
        }
        catch (FirebaseMessagingException ex) when (IsDeadToken(ex))
        {
            // nieaktualny token (reinstalacja, wyczyszczone dane, inny projekt, śmieciowy token) → usuwamy
            return new PushSendResult(PushSendOutcome.Unregistered, ex.MessagingErrorCode?.ToString() ?? "InvalidArgument");
        }
        catch (FirebaseMessagingException ex)
        {
            _logger.LogWarning(ex, "FCM odrzucił wiadomość: {Code}", ex.MessagingErrorCode);
            return new PushSendResult(PushSendOutcome.Failed, $"{ex.MessagingErrorCode?.ToString() ?? ex.ErrorCode.ToString()}: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Błąd wysyłki FCM");
            return new PushSendResult(PushSendOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// <c>Unregistered</c> (token wygasł / aplikację odinstalowano), <c>SenderIdMismatch</c> (token z innego projektu)
    /// oraz <c>InvalidArgument</c> dotyczący samego tokenu („not a valid FCM registration token”).
    /// </summary>
    internal static bool IsDeadToken(FirebaseMessagingException ex) =>
        ex.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.SenderIdMismatch ||
        (ex.ErrorCode == ErrorCode.InvalidArgument && ex.Message.Contains("registration token", StringComparison.OrdinalIgnoreCase));

    private static string? ReadProjectId(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("project_id", out var p) ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
