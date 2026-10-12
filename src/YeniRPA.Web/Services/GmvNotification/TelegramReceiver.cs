using System.Net.Http.Json;
using System.Text.Json;

namespace YeniRPA.Web.Services.GmvNotification;

/// <summary>One incoming Telegram update. <see cref="Text"/> is null for anything that is not a text message.</summary>
public sealed record TelegramUpdate(long UpdateId, string? ChatId, string? Text);

public interface ITelegramReceiver
{
    /// <summary>
    /// Waits up to <paramref name="waitSeconds"/> for updates with an id of at least
    /// <paramref name="offset"/> (long polling). A null offset means "everything still unconfirmed";
    /// -1 means "only the latest one".
    /// </summary>
    /// <exception cref="GmvSendException">Telegram refused or could not be reached.</exception>
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string token, long? offset, int waitSeconds, CancellationToken cancellationToken);
}

/// <summary>
/// Reads incoming messages through the Bot API's <c>getUpdates</c>. It is an outbound request like
/// <see cref="TelegramSender"/>'s, so it needs no open port or public address — the app asks Telegram
/// whether anything arrived. As in the sender, the token is scrubbed from every message raised here.
/// </summary>
public sealed class TelegramReceiver(IHttpClientFactory httpClientFactory) : ITelegramReceiver
{
    /// <summary>Slack on top of the long-poll wait for the round trip itself.</summary>
    const int RoundTripSeconds = 15;

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        string token, long? offset, int waitSeconds, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(waitSeconds + RoundTripSeconds));

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.telegram.org/bot{token}/getUpdates")
        {
            Content = JsonContent.Create(new
            {
                offset,
                timeout = waitSeconds,
                allowed_updates = new[] { "message" },
            }),
        };

        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new GmvSendException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Telegram rejected the bot token."
                    : response.StatusCode == System.Net.HttpStatusCode.Conflict
                        ? "Telegram says another program is reading this bot's messages (or a webhook is set)."
                        : $"Telegram refused the request ({(int)response.StatusCode}).");
            }

            return Parse(body);
        }
        catch (HttpRequestException ex)
        {
            throw new GmvSendException("Telegram could not be reached: " + ex.Message.Replace(token, "***"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GmvSendException("Telegram did not answer in time.");
        }
    }

    internal static IReadOnlyList<TelegramUpdate> Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return [];

            var updates = new List<TelegramUpdate>();
            foreach (var item in result.EnumerateArray())
            {
                if (!item.TryGetProperty("update_id", out var id) || !id.TryGetInt64(out var updateId))
                    continue;

                string? chatId = null;
                string? text = null;
                if (item.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var chatIdElement))
                        chatId = chatIdElement.GetRawText();
                    if (message.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                        text = textElement.GetString();
                }

                updates.Add(new TelegramUpdate(updateId, chatId, text));
            }

            return updates;
        }
        catch (JsonException)
        {
            throw new GmvSendException("Telegram answered with something unreadable.");
        }
    }
}
