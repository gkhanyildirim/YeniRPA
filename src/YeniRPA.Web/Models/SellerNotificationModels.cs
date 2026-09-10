namespace YeniRPA.Web.Models;

/// <summary>
/// One saved Seller Notification message: a name the operator picks it by, the free-text topic
/// sent to Mirakl's conversation dialog, and the message body. There is no placeholder
/// substitution — unlike the WhatsApp/Outlook warning modules, this is not run against an export
/// row per seller, so nothing here is ever templated against data.
/// </summary>
public sealed record SellerNotificationTemplate(
    string Id,
    string Name,
    string Topic,
    string Message);

/// <summary>The file <c>SellerNotificationStore</c> owns: every saved template, in save order.</summary>
public sealed record SellerNotificationTemplateFile(
    IReadOnlyList<SellerNotificationTemplate> Templates);
