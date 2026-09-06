using LiteDB;

namespace YeniRPA.Web.Services;

/// <summary>
/// The one LiteDB database the app's JSON-shaped settings stores now live in:
/// <c>%LOCALAPPDATA%\YeniRPA\database.db</c>. Every store gets its own collection out of it rather
/// than its own file, the same way they used to each own one JSON file under the same root.
/// </summary>
public interface ILiteDbContext
{
    /// <summary>Where the <c>.db</c> file lives. Stores that used to expose their JSON file's path
    /// (for the operator-facing "where is this stored" line) expose this instead.</summary>
    string DatabasePath { get; }

    ILiteCollection<T> GetCollection<T>(string name);
}

/// <summary>
/// Singleton owner of the shared <see cref="LiteDatabase"/> connection.
///
/// <para><c>ConnectionType.Shared</c> rather than the default <c>Direct</c>: this app is deliberately
/// run as several independent local installs, not one server, but a single machine can still end up
/// with two instances pointed at the same profile (a second `dotnet run`, a stray process from a
/// crashed one). Shared mode takes a real file lock so a second connection queues instead of
/// corrupting the file — the LiteDB equivalent of the atomic-write-plus-lock pattern the JSON stores
/// used to hand-roll each.</para>
///
/// <para>Registered once as a singleton and disposed by the DI container at shutdown, the same
/// lifetime <see cref="Automation.MiraklBrowser"/> and <see cref="Automation.WhatsAppBrowser"/> already
/// use for the one resource they own.</para>
/// </summary>
public sealed class LiteDbContext : ILiteDbContext, IDisposable
{
    readonly LiteDatabase _database;

    public LiteDbContext()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA");
        Directory.CreateDirectory(directory);

        DatabasePath = Path.Combine(directory, "database.db");

        _database = new LiteDatabase(new ConnectionString
        {
            Filename = DatabasePath,
            Connection = ConnectionType.Shared,
        });
    }

    public string DatabasePath { get; }

    public ILiteCollection<T> GetCollection<T>(string name) => _database.GetCollection<T>(name);

    public void Dispose() => _database.Dispose();
}
