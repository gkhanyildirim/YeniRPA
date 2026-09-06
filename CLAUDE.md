# YeniRPA

## İletişim ve Çıktı Formatı

- Kodun nasıl yazıldığını veya teknik mantığını uzun uzun anlatma.
- Sadece yapılan değişiklikleri ve eklenen özellikleri özetle.
- Açıklamaları akıcı ve sade bir Türkçe ile ver.
- Yanıt formatın her zaman şu 2 başlıktan oluşsun:

**Ne Eklendi / Değişti?**
* (Eklenen dosya, fonksiyon, paket veya bileşenler - madde madde)

**Ne Yapıldı?**
* (Bu değişikliğin ne işe yaradığı ve sistemde neyi sağladığı - kısa özet)

### Kapsam

- Bu kural yalnızca sohbetteki yanıtlar içindir. Kod, arayüz metinleri, kod içi yorumlar ve
  README İngilizce kalır.
- Değişiklik yapılmayan yanıtlarda (soru cevaplama, dosya inceleme, hata teşhisi) başlıklar
  zorlanmaz; aynı sadelikle doğrudan cevap verilir.
- Bir riski veya kaybolabilecek veriyi bildirmek gerekiyorsa kısa tek cümleyle söylenir,
  paragraflarca açıklanmaz.

## Backend Engineering Rules (.NET 10 / ASP.NET Core)

These govern all backend code written for this project from here on. They override default
judgment calls; deviate only when the user explicitly asks for an exception in the moment.

1. **Target:** .NET 10, C#, ASP.NET Core MVC.

2. **Persistence layer (LiteDB).**
   - All persistent application data goes through **LiteDB** via `ILiteDbContext`
     (`Services/LiteDbContext.cs`), never through hand-rolled JSON file read/write.
   - The database file lives at `%LOCALAPPDATA%\YeniRPA\database.db`, resolved dynamically with
     `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` — never a hardcoded path.
   - Always open it with `ConnectionType.Shared` (LiteDB's actual API name for this — some docs
     say "ConnectionMode", the type is `ConnectionType`) so a second local process or a second
     machine profile sharing the same file cannot corrupt it.
   - Every store is decoupled behind an interface (`ISellerGroupStore`, `ITitleRuleStore`, …) and
     registered in DI as a Singleton — see `Program.cs` for the existing six. Controllers depend
     on the interface, never the concrete class.
   - Call `EnsureIndex` on the collection's identifier field. Most stores here are single-document
     settings blobs (`Document.Id` fixed at `1`), so this is idiomatic scaffolding rather than a
     load-bearing performance index — do not invent a busier schema than the data actually needs
     just to make the index "matter".
   - Transient/session-only state that is meant to be invalidated by a restart — batch pairings
     (`OfferBatchStore`, `VatBatchStore`), the last automation run's result (`ProductStatusStore`)
     — stays in-memory. Do **not** move it into LiteDB; that would silently remove the
     restart-invalidates-it guarantee those classes are documented as relying on.
   - A store's own read-modify-write methods (something that loads the current document, changes
     it, and saves it back as one logical step) still need a `lock` around that sequence — LiteDB
     guarantees each individual call is atomic, not a multi-call sequence across it.

3. **Excel & file I/O.**
   - Read an uploaded file straight from `IFormFile.OpenReadStream()` — it is already seekable
     (ASP.NET Core buffers the multipart body before the action runs). Do not copy it into an
     intermediate `MemoryStream` first; that is a second full copy of data ASP.NET Core already
     buffered once.
   - A large workbook (tens of thousands of rows or more) is read with `DocumentFormat.OpenXml`'s
     `OpenXmlReader`, one row at a time, not loaded whole through ClosedXML's DOM — see
     `Services/OfferExportReader.cs` for the pattern and why it exists.
   - Every `Stream`, `IDisposable` browser/COM/file resource is inside `using` / `await using`.

4. **Automation services (Playwright & Outlook).**
   - One browser/context per target site, owned by a singleton, never spun up per request — see
     `MiraklBrowser` / `WhatsAppBrowser`.
   - A session credential (cookies, storage state) is handed to the library in memory —
     Playwright's `StorageState` string option, not `StorageStatePath` — and never written to disk
     unencrypted, not even to a temp file that gets deleted after. Where it must persist between
     runs, encrypt it at rest (`IDataProtector`, as `MiraklBrowser.auth.dat` does).
   - Outlook COM calls run on one dedicated STA thread owned by the sender class; nothing outside
     that class ever touches a COM object directly. Every COM reference obtained is released with
     `Marshal.FinalReleaseComObject` (or `ReleaseComObject` where final release would be wrong — see
     `OutlookMailSender.CloseInspector`'s doc comment) in a `finally` block.

5. **Performance & memory.**
   - File and I/O calls are `async`/`await` (`File.ReadAllTextAsync`, `File.WriteAllTextAsync`, …)
     unless the surrounding method is a narrow, already-synchronous hot path with no I/O of its own.
   - `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` on a `Task` are not used anywhere in this
     codebase; keep it that way.
   - New AJAX JSON endpoints return `{ success: bool, message: string, data: object }`. This is
     forward-only: the ~20 existing endpoints keep their current per-endpoint response shapes
     (`{ error }` on failure via `ReportExceptionFilter`, bespoke fields on success), and the
     `wwwroot/js/*.js` files that read them are not touched to match — only genuinely new endpoints
     use the envelope.
