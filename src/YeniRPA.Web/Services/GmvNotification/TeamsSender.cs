using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YeniRPA.Web.Services.GmvNotification;

public interface ITeamsSender
{
    /// <exception cref="GmvSendException">Teams (the workflow) refused or could not be reached.</exception>
    Task SendAsync(string webhookUrl, string text, CancellationToken cancellationToken);
}

/// <summary>
/// Posts a message to a Teams chat through a Power Automate workflow that starts with "When a Teams
/// webhook request is received". The address is the whole credential — its <c>sig</c> query value is a
/// key — so it is scrubbed from every message this class raises, which end up in the history table.
///
/// <para>The workflow receives two forms of the same card and may use either: <c>card</c>, the
/// Adaptive Card as one JSON string (what the "Post card in a chat or channel" action takes as its card
/// when given <c>triggerBody()?['card']</c>), and the standard <c>attachments[0].content</c> object.</para>
///
/// <para>Telegram's answer to the same text says it arrived. This one only says the workflow accepted
/// it (HTTP 202): whether the chat got it is up to the workflow's own run history.</para>
/// </summary>
public sealed class TeamsSender(IHttpClientFactory httpClientFactory) : ITeamsSender
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Hosts a Power Automate / Logic Apps webhook can live on. Anything else is refused, so a
    /// mistyped or pasted-wrong address cannot send the figures somewhere unintended.</summary>
    static readonly string[] AllowedHostSuffixes =
    [
        ".api.powerplatform.com",
        ".logic.azure.com",
        ".webhook.office.com",
    ];

    /// <summary>An operator-facing reason when <paramref name="url"/> is not a usable webhook address, else null.</summary>
    public static string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return "The Teams webhook address is not a valid URL.";

        if (uri.Scheme != Uri.UriSchemeHttps)
            return "The Teams webhook address must start with https://.";

        if (!AllowedHostSuffixes.Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return "That does not look like a Power Automate webhook address (expected a host ending in .api.powerplatform.com or .logic.azure.com).";

        return null;
    }

    public async Task SendAsync(string webhookUrl, string text, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = JsonContent.Create(BuildPayload(text)),
        };

        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
                return;

            throw new GmvSendException($"The Teams workflow refused the message ({(int)response.StatusCode}).");
        }
        catch (HttpRequestException ex)
        {
            throw new GmvSendException("The Teams workflow could not be reached: " + Scrub(ex.Message, webhookUrl));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GmvSendException("The Teams workflow did not answer in time.");
        }
    }

    /// <summary>One TextBlock per line: a line break inside a single block is not reliably shown by Teams.</summary>
    internal static JsonObject BuildPayload(string text)
    {
        var body = new JsonArray();
        foreach (var line in text.Split('\n'))
        {
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = line.Length == 0 ? " " : line,
                ["wrap"] = true,
            });
        }

        var card = new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.4",
            ["body"] = body,
        };

        return new JsonObject
        {
            ["type"] = "message",
            ["card"] = card.ToJsonString(),
            ["attachments"] = new JsonArray
            {
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["content"] = JsonNode.Parse(card.ToJsonString()),
                },
            },
        };
    }

    static string Scrub(string message, string url)
    {
        var scrubbed = message.Replace(url, "***");
        var query = new Uri(url).Query;
        return query.Length > 1 ? scrubbed.Replace(query, "?***") : scrubbed;
    }
}
