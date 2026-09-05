using System.Globalization;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Builds the Incidents Report out of the Mirakl incident exports.
///
/// <para>The incident panel can only download <b>open</b> and <b>closed</b> incidents separately —
/// there is no single export carrying both — so this builder takes two files and reports over their
/// union. Both downloads carry the same 23-column header, so one reader serves both; each row simply
/// remembers which upload it came from. Either file may be left out, and the caller is responsible
/// for rejecting the case where both are missing.</para>
///
/// <para><b>Lifecycle is derived from the row, not from the file it arrived in.</b> "Status" is free
/// text that Mirakl extends whenever it adds a state, so keying the report on the literal strings
/// seen today ("New incident", "Incident in progress", "Closed") would silently mis-bucket every new
/// one. The dates decide instead: a closing date means closed, a closing reason without one means the
/// seller has answered and Mirakl has not stamped the closure yet, and anything else is open. The raw
/// status still travels to the dashboard as its own filterable dimension.</para>
///
/// <para><b>This is an incident queue, not a financial report.</b> The export carries Quantity, the
/// order total and the currency; none of them are read. They describe the <em>order</em> rather than
/// the incident — the same total repeats on every incident raised against one order, and on a split
/// order on every seller's half — so they were never safe to sum row by row, and the team does not work
/// the queue by value. They are not in <see cref="RequiredColumns"/> either: the module requires only
/// what it actually reads.</para>
///
/// <para>Everything past the per-row derivations below is left to wwwroot/js/incidents-report.js.
/// Aggregating server-side would freeze the scorecards at the full data set, and the whole point of
/// the seller and reason tables is that they answer for whatever date range the operator has picked.</para>
/// </summary>
public static class IncidentsReportBuilder
{
    /// <summary>An open incident older than this many days is flagged before it breaches.</summary>
    public const int WarningDays = 7;

    /// <summary>An open incident older than this many days has breached.</summary>
    public const int BreachDays = 14;

    /// <summary>An incident nobody has touched for this many days is stale, whatever its age.</summary>
    public const int StaleDays = 3;

    /// <summary>Below this many closed incidents, a seller's average resolution time is noise.</summary>
    public const int MinSampleSize = 3;

    /// <summary>A thread this long has stopped being a question and become an escalation.</summary>
    public const int HotThreadMessages = 8;

    /// <summary>Mailbox domain that marks an action as taken by the marketplace operator, not a seller.</summary>
    public const string OperatorMailDomain = "media-saturn.com";

    /// <summary>The user Mirakl writes when an action was posted by automation rather than a person.</summary>
    public const string AutomationUser = "Operator API";

    /// <summary>
    /// Default lower bound for closed incidents. The closed export is a full history dump going back
    /// roughly a year, and only the incidents from this date forward are wanted in the report.
    ///
    /// <para>Applied as the pre-filled value of the dashboard's "Closed from" input rather than by
    /// dropping rows here: the default is the rule, but a date the operator can widen costs nothing
    /// and keeps the history one change away instead of one deployment away.</para>
    /// </summary>
    public static readonly DateOnly ClosedFrom = new(2026, 9, 2);

    /// <summary>
    /// Columns both exports must carry. Resolved up front so a wrong file is one clear message rather
    /// than a dashboard that renders with a silently empty column.
    ///
    /// <para>Only what the report reads. The export's Quantity, order-total and Currency columns may be
    /// present or absent without changing anything — see the class remarks.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredColumns =
    [
        "Order number", "Order created on", "Customer name", "Seller", "Status",
        "Number of messages", "Opened by", "Opened by user", "Opened on", "Reason",
        "Closed by", "Closed by user", "Closed on", "Closing reason",
        "Product SKU", "Product",
        "Last action by", "Last action by user", "Last action date", "Last action",
    ];

    // Data-quality flags, as they travel to the review table.
    public const string IssueClosedBeforeOpened = "Closed before it was opened";
    public const string IssueOpenedBeforeOrder = "Opened before the order was created";
    public const string IssueUnreadableDate = "A date could not be read";
    public const string IssueMissingSeller = "No seller on the row";
    public const string IssueMissingOrderNumber = "No order number on the row";
    public const string IssueInBothFiles = "The same incident is in both uploads";
    public const string IssueClosedRowInOpenFile = "Carries a closing date but came from the open export";
    public const string IssueOpenRowInClosedFile = "Carries no closing date but came from the closed export";

    const string DisplayFormat = "yyyy-MM-dd HH:mm";
    const string DayFormat = "yyyy-MM-dd";

    /// <summary>One row as read off a file, before the cross-file checks that need both sides.</summary>
    sealed record RawIncident(
        string Source,
        string OrderNumber,
        DateTime? OrderCreatedOn,
        string CustomerName,
        string Seller,
        string Status,
        int MessageCount,
        string OpenedBy,
        string OpenedByUser,
        DateTime? OpenedOn,
        string Reason,
        string ClosedBy,
        string ClosedByUser,
        DateTime? ClosedOn,
        string ClosingReason,
        string ProductSku,
        string Product,
        string LastActionBy,
        string LastActionByUser,
        DateTime? LastActionDate,
        string LastAction,
        bool HadUnreadableDate);

    /// <summary>
    /// The <paramref name="openFileName"/> / <paramref name="closedFileName"/> arguments are
    /// load-bearing: the table reader picks the XLSX or the CSV path purely from the file extension.
    /// </summary>
    public static IncidentsData BuildData(
        Stream? openStream, string? openFileName,
        Stream? closedStream, string? closedFileName)
    {
        var warnings = new List<string>();

        var openRaw = openStream is null
            ? []
            : ReadFile(openStream, openFileName!, IncidentSource.Open, "open incidents");
        var closedRaw = closedStream is null
            ? []
            : ReadFile(closedStream, closedFileName!, IncidentSource.Closed, "closed incidents");

        if (openRaw.Count == 0 && closedRaw.Count == 0)
            throw new InvalidOperationException("No incident rows were found in the uploaded file(s).");

        // Captured once for the whole build: a per-row DateTime.Now would let a slow parse straddle a
        // minute boundary and leave two rows of the same age reported a day apart.
        var referenceTime = DateTime.Now;

        var all = openRaw.Concat(closedRaw).ToList();

        // An incident in both uploads would be double-counted everywhere. It should not happen — the
        // two exports partition the panel — but the operator downloads them minutes apart, so an
        // incident closed in between lands in both. Flagged rather than silently de-duplicated,
        // because which of the two copies is the true one is not ours to decide.
        var duplicates = all
            .Where(r => r.OrderNumber.Length > 0)
            .GroupBy(r => IncidentKey(r), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.Source).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (duplicates.Count > 0)
            warnings.Add($"{duplicates.Count:N0} incident(s) appear in both uploads and are listed twice — see the review section.");

        var rows = all.Select(raw => ToRow(raw, referenceTime, duplicates)).ToList();

        var unreadable = rows.Count(r => r.Issues.Contains(IssueUnreadableDate));
        if (unreadable > 0)
            warnings.Add($"{unreadable:N0} row(s) carry a date that could not be read; their ages are left blank rather than guessed.");

        var dataAsOf = all
            .SelectMany(r => new[] { r.LastActionDate, r.ClosedOn, r.OpenedOn })
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .DefaultIfEmpty()
            .Max();

        if (dataAsOf != default && (referenceTime - dataAsOf).TotalDays > 1)
            warnings.Add($"The newest action in the upload is {dataAsOf.ToString(DisplayFormat, CultureInfo.InvariantCulture)} — the export is more than a day old.");

        return new IncidentsData(
            rows,
            openRaw.Count,
            closedRaw.Count,
            referenceTime.ToString(DisplayFormat, CultureInfo.InvariantCulture),
            dataAsOf == default ? null : dataAsOf.ToString(DisplayFormat, CultureInfo.InvariantCulture),
            WarningDays,
            BreachDays,
            StaleDays,
            ClosedFrom.ToString(DayFormat, CultureInfo.InvariantCulture),
            MinSampleSize,
            warnings);
    }

    /// <summary>
    /// Identity of an incident across the two files. Mirakl gives an incident no id of its own in this
    /// export, so the order number plus the moment it was opened is the closest thing to one.
    /// </summary>
    static string IncidentKey(RawIncident r) =>
        r.OrderNumber.Trim() + "|" + (r.OpenedOn?.ToString("O", CultureInfo.InvariantCulture) ?? "");

    // ---------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------

    static List<RawIncident> ReadFile(Stream stream, string fileName, string source, string label)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException($"No rows were found in the uploaded {label} file.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        int Col(string name)
        {
            if (header.TryGetValue(name, out var index))
                return index;
            throw new InvalidOperationException(
                $"Required column '{name}' was not found in the uploaded {label} file. " +
                "Export the incident list from Mirakl without changing its columns.");
        }

        // Resolved up front so a wrong file is reported once, not one column at a time across uploads.
        var cOrderNumber = Col("Order number");
        var cOrderCreated = Col("Order created on");
        var cCustomer = Col("Customer name");
        var cSeller = Col("Seller");
        var cStatus = Col("Status");
        var cMessages = Col("Number of messages");
        var cOpenedBy = Col("Opened by");
        var cOpenedByUser = Col("Opened by user");
        var cOpenedOn = Col("Opened on");
        var cReason = Col("Reason");
        var cClosedBy = Col("Closed by");
        var cClosedByUser = Col("Closed by user");
        var cClosedOn = Col("Closed on");
        var cClosingReason = Col("Closing reason");
        var cSku = Col("Product SKU");
        var cProduct = Col("Product");
        var cLastActionBy = Col("Last action by");
        var cLastActionByUser = Col("Last action by user");
        var cLastActionDate = Col("Last action date");
        var cLastAction = Col("Last action");

        var rows = new List<RawIncident>(table.Count - 1);

        foreach (var row in table.Skip(1))
        {
            var orderNumber = TabularFile.GetCell(row, cOrderNumber).Trim();
            var status = TabularFile.GetCell(row, cStatus).Trim();

            // A trailing blank line, or a totals row the operator pasted in: nothing to report on.
            if (orderNumber.Length == 0 && status.Length == 0)
                continue;

            var orderCreatedText = TabularFile.GetCell(row, cOrderCreated);
            var openedOnText = TabularFile.GetCell(row, cOpenedOn);
            var closedOnText = TabularFile.GetCell(row, cClosedOn);
            var lastActionText = TabularFile.GetCell(row, cLastActionDate);

            var orderCreatedOn = TabularFile.ParseDate(orderCreatedText);
            var openedOn = TabularFile.ParseDate(openedOnText);
            var closedOn = TabularFile.ParseDate(closedOnText);
            var lastActionDate = TabularFile.ParseDate(lastActionText);

            // A date that is present but unreadable is a different problem from one that is absent,
            // and only the first is worth an operator's attention.
            var unreadable =
                Unreadable(orderCreatedText, orderCreatedOn) ||
                Unreadable(openedOnText, openedOn) ||
                Unreadable(closedOnText, closedOn) ||
                Unreadable(lastActionText, lastActionDate);

            rows.Add(new RawIncident(
                source,
                orderNumber,
                orderCreatedOn,
                TabularFile.GetCell(row, cCustomer).Trim(),
                TabularFile.GetCell(row, cSeller).Trim(),
                status,
                (int)TabularFile.ParseNumber(TabularFile.GetCell(row, cMessages)),
                TabularFile.GetCell(row, cOpenedBy).Trim(),
                TabularFile.GetCell(row, cOpenedByUser).Trim(),
                openedOn,
                TabularFile.GetCell(row, cReason).Trim(),
                TabularFile.GetCell(row, cClosedBy).Trim(),
                TabularFile.GetCell(row, cClosedByUser).Trim(),
                closedOn,
                TabularFile.GetCell(row, cClosingReason).Trim(),
                TabularFile.GetCell(row, cSku).Trim(),
                TabularFile.GetCell(row, cProduct).Trim(),
                TabularFile.GetCell(row, cLastActionBy).Trim(),
                TabularFile.GetCell(row, cLastActionByUser).Trim(),
                lastActionDate,
                TabularFile.GetCell(row, cLastAction).Trim(),
                unreadable));
        }

        return rows;
    }

    static bool Unreadable(string text, DateTime? parsed) => text.Trim().Length > 0 && parsed is null;

    // ---------------------------------------------------------------------------
    // Derivation
    // ---------------------------------------------------------------------------

    static IncidentRow ToRow(RawIncident r, DateTime referenceTime, HashSet<string> duplicates)
    {
        var lifecycle = Lifecycle(r);
        var isClosed = lifecycle == IncidentLifecycle.Closed;

        var ageDays = isClosed || r.OpenedOn is null
            ? (double?)null
            : Days(referenceTime - r.OpenedOn.Value);

        var resolutionDays = r.ClosedOn is null || r.OpenedOn is null
            ? (double?)null
            : Days(r.ClosedOn.Value - r.OpenedOn.Value);

        var orderToIncidentDays = r.OpenedOn is null || r.OrderCreatedOn is null
            ? (double?)null
            : Days(r.OpenedOn.Value - r.OrderCreatedOn.Value);

        var silenceDays = r.LastActionDate is null
            ? (double?)null
            : Days(referenceTime - r.LastActionDate.Value);

        return new IncidentRow(
            r.OrderNumber,
            TabularFile.OrderCore(r.OrderNumber),
            SplitSuffix(r.OrderNumber),
            Display(r.OrderCreatedOn),
            r.CustomerName,
            r.Seller,
            r.Status,
            r.MessageCount,
            r.OpenedBy,
            r.OpenedByUser,
            Display(r.OpenedOn),
            Day(r.OpenedOn),
            r.Reason,
            r.ClosedBy,
            r.ClosedByUser,
            Display(r.ClosedOn),
            Day(r.ClosedOn),
            r.ClosingReason,
            r.ProductSku,
            r.Product,
            r.LastActionBy,
            r.LastActionByUser,
            Display(r.LastActionDate),
            Day(r.LastActionDate),
            r.LastAction,
            r.Source,
            lifecycle,
            WaitingOn(r, lifecycle),
            ActorKind(r.OpenedBy, r.OpenedByUser),
            ActorKind(r.LastActionBy, r.LastActionByUser),
            r.ClosedBy.Length == 0 && r.ClosedByUser.Length == 0 ? null : ActorKind(r.ClosedBy, r.ClosedByUser),
            ageDays,
            resolutionDays,
            orderToIncidentDays,
            silenceDays,
            Issues(r, lifecycle, duplicates));
    }

    /// <summary>
    /// Where the incident stands, read off the dates. See the class remarks for why the status column
    /// is not what decides this.
    /// </summary>
    static string Lifecycle(RawIncident r)
    {
        if (r.ClosedOn is not null)
            return IncidentLifecycle.Closed;

        return r.ClosingReason.Length > 0
            ? IncidentLifecycle.Resolved
            : IncidentLifecycle.Open;
    }

    /// <summary>
    /// Who owes the next move.
    ///
    /// <para><b>The lifecycle decides whether it is ours; only then does "Last action by" matter.</b>
    /// The seller marking an incident resolved is the handover: until that happens the thread runs
    /// between the customer and the seller and our team owes nothing, and once it happens the
    /// verification and the closure are ours no matter who typed the last message. An earlier version
    /// read this off "Last action by" alone and called a waiting customer message our queue, which
    /// produced the exact inverse of the team's real worklist.</para>
    /// </summary>
    static string WaitingOn(RawIncident r, string lifecycle)
    {
        if (lifecycle == IncidentLifecycle.Closed)
            return IncidentWaitingOn.None;

        if (lifecycle == IncidentLifecycle.Resolved)
            return IncidentWaitingOn.Us;

        if (r.LastActionBy.Equals("Customer", StringComparison.OrdinalIgnoreCase))
            return IncidentWaitingOn.Seller;

        if (r.LastActionBy.Equals("Seller", StringComparison.OrdinalIgnoreCase))
            return IncidentWaitingOn.Customer;

        if (r.LastActionBy.Equals("Operator", StringComparison.OrdinalIgnoreCase))
            return IncidentWaitingOn.OperatorActed;

        return IncidentWaitingOn.None;
    }

    /// <summary>
    /// What kind of account acted. The role column alone cannot tell a person at the operator from the
    /// automation posting under the same role, so the user is what decides.
    /// </summary>
    static string ActorKind(string role, string user)
    {
        var value = user.Trim();

        if (value.Length == 0)
        {
            // No user at all is how the export writes a customer action; any other role with a blank
            // user is still best described by the role it claims.
            return role.Equals("Seller", StringComparison.OrdinalIgnoreCase)
                ? IncidentActorKind.Seller
                : IncidentActorKind.Customer;
        }

        if (value.Equals(AutomationUser, StringComparison.OrdinalIgnoreCase))
            return IncidentActorKind.Automation;

        return value.EndsWith("@" + OperatorMailDomain, StringComparison.OrdinalIgnoreCase)
            ? IncidentActorKind.Internal
            : IncidentActorKind.Seller;
    }

    static IReadOnlyList<string> Issues(RawIncident r, string lifecycle, HashSet<string> duplicates)
    {
        var issues = new List<string>();

        if (r.HadUnreadableDate)
            issues.Add(IssueUnreadableDate);

        if (r.ClosedOn is not null && r.OpenedOn is not null && r.ClosedOn < r.OpenedOn)
            issues.Add(IssueClosedBeforeOpened);

        if (r.OpenedOn is not null && r.OrderCreatedOn is not null && r.OpenedOn < r.OrderCreatedOn)
            issues.Add(IssueOpenedBeforeOrder);

        if (r.OrderNumber.Length == 0)
            issues.Add(IssueMissingOrderNumber);

        if (r.Seller.Length == 0)
            issues.Add(IssueMissingSeller);

        if (r.Source == IncidentSource.Open && lifecycle == IncidentLifecycle.Closed)
            issues.Add(IssueClosedRowInOpenFile);

        if (r.Source == IncidentSource.Closed && lifecycle != IncidentLifecycle.Closed)
            issues.Add(IssueOpenRowInClosedFile);

        if (r.OrderNumber.Length > 0 && duplicates.Contains(IncidentKey(r)))
            issues.Add(IssueInBothFiles);

        return issues;
    }

    // ---------------------------------------------------------------------------
    // Formatting
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The split suffix of a Mirakl order number: the "A" of "01259_326674352-A". More than one
    /// suffix on the same core means the customer's order was served by several sellers.
    /// </summary>
    static string SplitSuffix(string orderNumber)
    {
        var dash = orderNumber.LastIndexOf('-');
        return dash >= 0 && dash < orderNumber.Length - 1
            ? orderNumber[(dash + 1)..].Trim()
            : "";
    }

    static double Days(TimeSpan span) => Math.Round(span.TotalDays, 1);

    static string? Display(DateTime? value) =>
        value?.ToString(DisplayFormat, CultureInfo.InvariantCulture);

    static string? Day(DateTime? value) =>
        value?.ToString(DayFormat, CultureInfo.InvariantCulture);
}
