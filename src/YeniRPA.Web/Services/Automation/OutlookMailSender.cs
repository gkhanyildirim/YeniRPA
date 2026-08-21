using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// The only place in the app that touches Outlook.
///
/// <para><b>Why Outlook and not SMTP.</b> The operator's mailbox is a corporate Exchange account
/// behind modern authentication; an SMTP path would need either an app password the tenant does not
/// issue or a service account whose address is not the one sellers already correspond with. Driving
/// the desktop client borrows a session that is already authenticated, and every warning lands in the
/// operator's own Sent Items, where the audit trail belongs.</para>
///
/// <para><b>The STA trap.</b> ASP.NET Core request threads are MTA. Outlook's object model is an STA
/// apartment-threaded server: calls from an MTA thread go through a marshalling proxy that mostly
/// works and intermittently does not — <c>RPC_E_SERVERFAULT</c> halfway through a batch, with no
/// pattern to it. So this class owns one long-lived STA thread and every COM call happens on it. The
/// public surface is <see cref="Task"/>-based and marshals onto that thread; nothing outside this
/// file ever sees a COM object.</para>
///
/// <para>Late-bound through reflection rather than an interop assembly, so the app builds and runs
/// without Outlook installed and does not pin a particular Outlook version.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OutlookMailSender : IDisposable
{
    /// <summary><c>olMailItem</c>.</summary>
    const int MailItemType = 0;

    readonly ILogger<OutlookMailSender> _logger;
    readonly BlockingCollection<Action> _work = new();
    readonly Thread _thread;

    /// <summary>The Outlook.Application object. Touched <b>only</b> on <see cref="_thread"/>.</summary>
    object? _application;

    public OutlookMailSender(ILogger<OutlookMailSender> logger)
    {
        _logger = logger;

        _thread = new Thread(Pump)
        {
            Name = "Outlook COM (STA)",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>What the last probe found, for the panel's badge. Null until something has asked.</summary>
    public bool? LastKnownAvailable { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// Resolves Outlook and confirms a mail session, starting the client if it is not running.
    /// Cheap on the second call — the application object is kept.
    /// </summary>
    public Task<bool> ProbeAsync() => RunAsync(() =>
    {
        try
        {
            EnsureApplication();
            LastKnownAvailable = true;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastKnownAvailable = false;
            LastError = Describe(ex) + Hint();
            _logger.LogWarning(ex, "Outlook could not be reached.");
            return false;
        }
    });

    /// <summary>
    /// The one failure worth explaining rather than reporting. <c>CO_E_SERVER_EXEC_FAILURE</c> from a
    /// machine that has been moved to the new Outlook for Windows is not a broken install: the new
    /// client (<c>olk.exe</c>) is a store app with <b>no COM object model at all</b>, and COM's attempt
    /// to cold-start the classic client in its place is what fails. The raw HRESULT sends you looking
    /// at DCOM permissions for an afternoon; "start classic Outlook" fixes it in ten seconds.
    /// </summary>
    static string Hint()
    {
        var classicRunning = Process.GetProcessesByName("OUTLOOK").Length > 0;
        if (classicRunning)
            return "";

        return Process.GetProcessesByName("olk").Length > 0
            ? " — the new Outlook for Windows is running, and it has no COM interface. Start the classic Outlook (Office16\\OUTLOOK.EXE) and check again."
            : " — Outlook does not appear to be running. Start the classic Outlook and check again.";
    }

    /// <summary>
    /// Composes one mail and either sends it or leaves it in Drafts.
    ///
    /// <para>A dry run <c>Save()</c>s instead of <c>Send()</c>ing: the draft is a real mail with the
    /// real recipient and the real attachment, so it can be opened and checked. A dry run that only
    /// logged what it would have done would not catch the two things that actually go wrong — a
    /// mangled address and the wrong file attached.</para>
    /// </summary>
    public Task SendAsync(string to, string subject, string body, string attachmentPath, bool dryRun) =>
        RunAsync<object?>(() =>
        {
            try
            {
                Compose(to, subject, body, attachmentPath, dryRun);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                // Outlook was closed, restarted or crashed between mails: the cached application is a
                // dead proxy. Drop it and try once more on a fresh one before giving up on this row.
                _logger.LogWarning(ex, "The Outlook call failed; reconnecting and retrying once.");
                ReleaseApplication();
                Compose(to, subject, body, attachmentPath, dryRun);
            }

            return null;
        });

    // ---------------------------------------------------------------------
    // On the STA thread
    // ---------------------------------------------------------------------

    void Compose(string to, string subject, string body, string attachmentPath, bool dryRun)
    {
        var application = EnsureApplication();

        var mail = Call(application, "CreateItem", MailItemType)
            ?? throw new InvalidOperationException("Outlook returned no mail item.");

        object? attachments = null;
        try
        {
            Set(mail, "To", to);
            Set(mail, "Subject", subject);
            // Plain text. The template is a plain-text box, and letting Outlook decide the format
            // would render the operator's line breaks differently from the preview they approved.
            Set(mail, "BodyFormat", 1);
            Set(mail, "Body", body);

            attachments = Get(mail, "Attachments")
                ?? throw new InvalidOperationException("Outlook returned no attachments collection.");
            Call(attachments, "Add", attachmentPath);

            if (dryRun)
                Call(mail, "Save");
            else
                Call(mail, "Send");
        }
        finally
        {
            Release(attachments);
            Release(mail);
        }
    }

    object EnsureApplication()
    {
        if (_application is not null)
            return _application;

        var type = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false)
            ?? throw new InvalidOperationException(
                "Outlook is not installed on this machine, or its COM registration is missing.");

        // Outlook is a single-instance COM server, so this attaches to the running client when there
        // is one and starts it otherwise. Marshal.GetActiveObject does not exist on .NET Core and is
        // not needed here.
        var application = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Outlook did not return an application object.");

        // Without a logged-on MAPI session, CreateItem fails on a machine where Outlook was started
        // by this call rather than by the operator. Logging on to the default profile is a no-op when
        // a session already exists.
        var session = Get(application, "Session");
        if (session is not null)
        {
            try
            {
                Call(session, "Logon", "", "", false, false);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                // Some profiles refuse an explicit Logon while already signed in. That is exactly the
                // state we wanted, so it is not a failure.
                _logger.LogDebug(ex, "MAPI Logon was refused; assuming an existing session.");
            }
            finally
            {
                Release(session);
            }
        }

        _application = application;
        return application;
    }

    void ReleaseApplication()
    {
        Release(_application);
        _application = null;
    }

    void Pump()
    {
        foreach (var item in _work.GetConsumingEnumerable())
        {
            try
            {
                item();
            }
            catch (Exception ex)
            {
                // RunAsync already routed the failure to its caller's task; reaching here would mean
                // the completion source itself threw.
                _logger.LogError(ex, "The Outlook worker item failed outside its own error handling.");
            }
        }

        ReleaseApplication();
    }

    /// <summary>Queues <paramref name="work"/> onto the STA thread and hands back its result.</summary>
    Task<T> RunAsync<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(_work.IsAddingCompleted, this);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _work.Add(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return completion.Task;
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        // The pump releases the application on its own thread, which is the only thread allowed to.
        _thread.Join(TimeSpan.FromSeconds(5));
        _work.Dispose();
    }

    // ---------------------------------------------------------------------
    // Late binding
    // ---------------------------------------------------------------------
    //
    // Reflection rather than `dynamic`: this is a handful of calls, and InvokeMember states plainly
    // which of them are properties and which are methods — a distinction `dynamic` hides and Outlook's
    // object model does not forgive.

    static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
            return;

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch (ArgumentException)
        {
            // Already released. Nothing to do, and nothing worth logging on a 188-mail run.
        }
    }

    /// <summary>
    /// Reflection wraps whatever Outlook threw in a <see cref="TargetInvocationException"/>, whose own
    /// message is "Exception has been thrown by the target of an invocation" — useless on a badge.
    /// </summary>
    internal static string Describe(Exception ex)
    {
        var inner = ex is TargetInvocationException { InnerException: { } wrapped } ? wrapped : ex;
        return inner.Message;
    }

    /// <summary>
    /// True when Outlook itself failed the call, as opposed to us passing it something invalid.
    /// Every call here goes through <see cref="MethodBase.Invoke(object, object[])"/>, so the
    /// <see cref="COMException"/> always arrives wrapped — testing for it directly would make the
    /// reconnect-and-retry above dead code.
    /// </summary>
    static bool IsComFailure(Exception ex) =>
        ex is COMException or InvalidComObjectException ||
        (ex is TargetInvocationException { InnerException: { } inner } && IsComFailure(inner));
}
