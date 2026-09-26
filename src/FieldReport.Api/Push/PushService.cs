using FieldReport.Api.Contracts;
using FieldReport.Api.Data;

namespace FieldReport.Api.Push;

/// <summary>Wysyła powiadomienie na wszystkie urządzenia wskazanych techników i sprząta nieaktualne tokeny.</summary>
public sealed class PushService(DataStore store, IPushSender sender, ILogger<PushService> logger)
{
    public async Task<PushSummary> SendAsync(string type, IReadOnlyList<(string Email, PushMessage Message)> targets, CancellationToken ct)
    {
        var deliveries = targets
            .SelectMany(t => store.GetDevices(t.Email).Select(d => (d.Token, t.Message)))
            .ToList();

        var summary = new PushSummary
        {
            Type = type,
            FirebaseConfigured = sender.IsConfigured,
            TargetDevices = deliveries.Count
        };

        if (!sender.IsConfigured)
        {
            summary.Skipped = true;
            summary.Message = $"{sender.StatusDescription} Push pominięto — wysłano by do {Devices(deliveries.Count)}.";
            return summary;
        }

        foreach (var (token, message) in deliveries)
        {
            var result = await sender.SendAsync(token, message, ct);
            switch (result.Outcome)
            {
                case PushSendOutcome.Sent:
                    summary.Sent++;
                    break;
                case PushSendOutcome.Unregistered:
                    summary.Failed++;
                    summary.RemovedTokens++;
                    store.RemoveTokenEverywhere(token);
                    summary.Errors.Add($"{Short(token)}: token nieaktualny ({result.Error}) — usunięty");
                    break;
                default:
                    summary.Failed++;
                    summary.Errors.Add($"{Short(token)}: {result.Error}");
                    break;
            }
        }

        summary.Message = deliveries.Count == 0
            ? "Brak zarejestrowanych urządzeń — nie było do kogo wysłać."
            : $"Wysłano {summary.Sent} z {deliveries.Count} powiadomień" +
              (summary.RemovedTokens > 0 ? $", usunięto {summary.RemovedTokens} nieaktualnych tokenów." : ".");
        logger.LogInformation("Push {Type}: {Sent}/{Total} (usunięte tokeny: {Removed})",
            type, summary.Sent, deliveries.Count, summary.RemovedTokens);
        return summary;
    }

    public static string Short(string token) => token.Length <= 12 ? token : token[..12] + "…";

    private static string Devices(int n) => n switch
    {
        1 => "1 urządzenia",
        _ => $"{n} urządzeń"
    };
}
