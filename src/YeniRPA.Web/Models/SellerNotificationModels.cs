namespace YeniRPA.Web.Models;

/// <summary>The three send modes, one per tab of the Seller Notification panel.</summary>
public static class SellerNotificationKinds
{
    /// <summary>Mirakl topic "Return / Cancel the order"; the message is a saved return notice.</summary>
    public const string Return = "return";

    /// <summary>Same Mirakl topic as <see cref="Return"/>; the message covers a parcel that came back undelivered.</summary>
    public const string Undelivered = "undelivered";

    /// <summary>Mirakl topic "Other reason" plus a free-text topic the operator supplies.</summary>
    public const string Custom = "custom";

    /// <summary>Lower-cases and trims; anything unknown or missing (templates saved before kinds existed) is custom.</summary>
    public static string Normalize(string? kind)
    {
        var value = kind?.Trim().ToLowerInvariant();
        return value is Return or Undelivered ? value : Custom;
    }
}

/// <summary>
/// One saved Seller Notification message: a name the operator picks it by, the tab it belongs to
/// (<see cref="Kind"/>), the free-text topic (custom only — the other two use a fixed Mirakl topic)
/// and the message body. There is no placeholder substitution — unlike the WhatsApp/Outlook warning
/// modules, this is not run against an export row per seller, so nothing here is ever templated
/// against data.
/// </summary>
public sealed record SellerNotificationTemplate(
    string Id,
    string Name,
    string Topic,
    string Message,
    string? Kind = null);

/// <summary>The file <c>SellerNotificationStore</c> owns: every saved template, in save order.</summary>
public sealed record SellerNotificationTemplateFile(
    IReadOnlyList<SellerNotificationTemplate> Templates);
