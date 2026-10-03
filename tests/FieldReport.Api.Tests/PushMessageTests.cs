using System.Text.Json;
using FieldReport.Api.Push;
using Newtonsoft.Json;

namespace FieldReport.Api.Tests;

/// <summary>Kształt wiadomości FCM budowanej dla Firebase Admin SDK (bez wysyłki).</summary>
public sealed class PushMessageTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void Message_has_data_channel_and_notification()
    {
        var id = Guid.NewGuid();
        var m = FirebasePushSender.BuildMessage("tok", new PushMessage(PushTypes.ReportAssigned, id, "Nowe zgłoszenie", "Awaria pompy"), Now);

        Assert.Multiple(() =>
        {
#pragma warning disable CS0618 // Message.Token — patrz komentarz w FirebasePushSender.BuildMessage
            Assert.That(m.Token, Is.EqualTo("tok"));
#pragma warning restore CS0618
            Assert.That(m.Data["type"], Is.EqualTo("report_assigned"));
            Assert.That(m.Data["reportId"], Is.EqualTo(id.ToString()));
            Assert.That(m.Notification.Title, Is.EqualTo("Nowe zgłoszenie"));
            Assert.That(m.Android.Notification.ChannelId, Is.EqualTo("reports"));
        });
    }

    // event_time musi być czasem wysłania (inaczej Android pokazuje datę 0001-01-01)
    [Test]
    public void Event_time_is_send_time_not_min_value()
    {
        var m = FirebasePushSender.BuildMessage("tok", new PushMessage(PushTypes.ReportStatusChanged, Guid.NewGuid(), "t", "b"), Now);

        Assert.That(m.Android.Notification.EventTimestamp, Is.EqualTo(Now));
        Assert.That(m.Android.Notification.EventTimestamp, Is.Not.EqualTo(DateTime.MinValue));

        // tak, jak SDK serializuje do FCM HTTP v1 (Newtonsoft, pole event_time)
        var json = JsonConvert.SerializeObject(m.Android.Notification);
        using var doc = JsonDocument.Parse(json);
        var eventTime = doc.RootElement.GetProperty("event_time").GetString();
        Assert.That(eventTime, Does.StartWith("2026-09-26T12:00:00"));
        Assert.That(eventTime, Does.Not.StartWith("0001"));
    }
}
