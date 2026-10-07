using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace YeniRPA.Web.Services.GmvNotification;

public interface ITelegramSender
{
    /// <exception cref="GmvSendException">Telegram refused or could not be reached.</exception>
    Task SendAsync(string token, string chatId, string text, CancellationToken cancellationToken);
}

/// <summary>
/// Posts a text message through the Telegram Bot API. The token is part of the request URL, so it is
/// scrubbed from every message this class raises — those end up in the history table and the log.
/// </summary>
public sealed class TelegramSender(IHttpClientFactory httpClientFactory) : ITelegramSender
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task SendAsync(string token, string chatId, string text, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.telegram.org/bot{token}/sendMessage")
        {
            Content = JsonContent.Create(new { chat_id = chatId, text }),
        };

        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
                return;

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            throw new GmvSendException(Describe(response.StatusCode, body));
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

    static string Describe(HttpStatusCode status, string body)
    {
        string? description = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("description", out var d))
                description = d.GetString();
        }
        catch (JsonException)
        {
            // Not Telegram's usual JSON error body; the status code has to do.
        }

        return status == HttpStatusCode.Unauthorized
            ? "Telegram rejected the bot token."
            : $"Telegram refused the message ({(int)status}){(string.IsNullOrWhiteSpace(description) ? "" : ": " + description)}.";
    }
}
