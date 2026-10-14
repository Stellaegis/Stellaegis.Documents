# Stellaegis.Documents

Stellaegis.Documents is a .NET library that keeps documents for an application: it stores each
document's content, keeps its versions when you ask for them, and records what identifies the
document — its type, file name, media type, length and SHA-256 digest. It partitions documents by
**holder**, the party a document belongs to, so one deployment can serve many customers without one
of them ever reaching another's documents. Content is stored through
[Stellaegis.Blobs](https://github.com/Stellaegis/Stellaegis.Blobs), on whatever storage its adapter
targets.

You can use the library in process, or expose it over HTTP and reach it from another process with the
same interface. Four packages are published:

| Package | Content |
| --- | --- |
| `Stellaegis.Documents` | `IDocumentService`, the document model, the events, an in-memory store for development |
| `Stellaegis.Documents.PostgreSQL` | the store on PostgreSQL, with file names encrypted |
| `Stellaegis.Documents.AspNetCore` | the HTTP endpoints, mapped on a route group your host provides |
| `Stellaegis.Documents.Client` | `IDocumentService` over HTTP, against those endpoints |

The library targets .NET 10 and is released under the MIT license.

## What the library keeps, and what it leaves to you

The library records that a document exists and what identifies it. It does not know what a document
means to your application.

| The library keeps | Your application keeps |
| --- | --- |
| identity: a public GUID, stable across versions | what the document is about: tags, statuses, classification |
| the holder, the document type, file name, media type, length, digest | search, built from the events the library publishes |
| the versions of a versioned document | the links between your objects and the documents, by GUID |
| the content, through Stellaegis.Blobs | fine-grained authorization, through a hook you implement |

A typical application stores the document's GUID next to its own data — an invoice row holding the
GUID of its PDF — and asks the library for the content when it needs it. Your database knows only the
GUIDs of your own documents; the library knows all of them.

The trade-off is deliberate. Search and classification stay in your database, under your schema, so
two applications sharing the library can organize documents differently. In return, the library cannot
answer a question such as "the invoices of this customer for March": your application answers it from
its own data. The library publishes an event on every change so that you can keep such a projection.

## Holders

Every operation names its holder: `CreateAsync(holder, …)`, `OpenContentAsync(holder, id)` and so on.
A document of another holder behaves exactly as an absent one, on every path, versions included.

- A holder starts with a lowercase letter, followed by 2 to 62 lowercase letters, digits or single
  hyphens, the grammar of a Stellaegis.Blobs location: `acme`, `customer-42`.
- The empty string designates the platform: documents that belong to the application itself rather
  than to one of its holders.
- An application that does not partition its documents can use the empty holder everywhere.

The library trusts the holder it receives. Your code must take it from a source it controls — the
authenticated caller, a route value it has authorized — and never pass on a holder a user typed.

## Getting started in process

```sh
dotnet add package Stellaegis.Documents.PostgreSQL
dotnet add package Stellaegis.Blobs.FileSystem
```

Create the schema with [`docs/sql/stellaegis_documents.sql`](https://github.com/Stellaegis/Stellaegis.Documents/blob/main/docs/sql/stellaegis_documents.sql).
The script creates the `doc` schema and enables the `pgcrypto` extension, which encrypts file names;
the role that runs it needs the right to create an extension.

Then register a blob store and the document store:

```csharp
builder.Services.AddStellaegisFileSystemBlobStore(o => o.RootPath = "/var/lib/myapp/blobs");
builder.Services.AddStellaegisDocumentsPostgreSQL(builder.Configuration);
```

```json
{
  "Stellaegis": {
    "Documents": {
      "BlobSpace": "documents",
      "ListPageSize": 100,
      "PostgreSQL": {
        "ConnectionString": "Host=…;Database=…",
        "ActiveKeySlot": 1,
        "Keys": { "1": "from your secret store", "2": "" }
      }
    }
  }
}
```

- `BlobSpace` is the Stellaegis.Blobs space the content goes to, within each holder's storage.
- `ListPageSize` is the number of documents in one page of a list, between 1 and 1000.
- `Keys` are the passphrases that encrypt file names (see
  [File names are encrypted](#file-names-are-encrypted)). Supply them from a secret store or the
  environment, never from a committed file.

`IDocumentService` is then available from dependency injection:

```csharp
var document = await documents.CreateAsync(
    holder: "acme",
    documentType: "invoice",
    new DocumentUpload { Content = stream, FileName = "Invoice 2026-001.pdf", ContentType = "application/pdf" },
    actor: userId,
    isVersioned: false);

await using var content = await documents.OpenContentAsync("acme", document.DocumentUniqueId);
```

For development and tests, `AddStellaegisDocumentsInMemory(configuration)` registers a store that
keeps the index in memory instead of PostgreSQL.

## Documents and versions

**Reading a document by its GUID always returns its current content.** An application that does not
care about versions never sees one: it creates documents, replaces their content and reads them by
GUID.

You choose at creation whether a document is versioned. The choice does not change afterwards.

- **A versioned document** keeps every former content. `GetVersionsAsync` lists them, with their own
  file name, media type, length and digest, and `OpenVersionContentAsync` reads one.
- **A document that is not versioned** keeps only its current content. Its former content is deleted
  once the new one is in place.

Each change of content gets the next **content number** (`Document.VersionNumber`), versioned document
or not, and the document's pointer moves to it only once the new content is complete and checked. No
content is ever overwritten in place: a reader always gets a whole content, and a change that fails
leaves the current content untouched. The cost is that a document that is not versioned shows content
numbers above 1 without any history behind them.

Sending the same content again changes nothing, even under another file name: the library compares
digests, and renaming is not a change of content.

The document type is a code your application defines, such as `invoice` or `contract`. The library
requires it and keeps it, but never interprets it. A usual practice is to derive versioning from the
type: an invoice is corrected by a credit note rather than edited, so it is not versioned; a draft
contract is.

### Storage layout

Content is stored flat: one blob per content, in the holder's space, under the key
`{document GUID}-{content number}`. The library stores no key: it derives each key from the document's
identity. Moving the content to another storage therefore needs no change in the database.

### Trash and purge

`TrashAsync` marks a document as trashed. It leaves the list and can no longer change; its content
stays.

`PurgeHolderAsync(holder)` deletes every document of a holder for good, whatever its status: content
first, then the index, in batches of a hundred. A purge cut halfway resumes when called again. It
refuses the empty holder, so the platform's documents are never purged this way.

The library performs the purge; it does not decide when. Confirmations ("type the customer's name to
confirm") and any retention period before the final deletion belong to the part of your application
that manages the holder's life — closing a customer account, for example — which calls the purge once
they are through.

## Streaming and integrity

Content streams through the library. It is hashed and measured on its way to the blob store, and read
back the same way, so a document of any size passes through a few buffers. The library's tests check
it: storing, replacing and reading 64 MB each allocate less than 8 MB, and so does uploading 64 MB over
HTTP, sender, endpoints and library together.

The SHA-256 digest guards the content end to end:

- **Upload.** `DocumentUpload.ExpectedContentHash` carries the digest the sender computed. The library
  refuses content that does not match it, before anything points to it. When the announced digest
  equals the current one, the content is not even read.
- **Download over HTTP.** A content carries its digest as `Repr-Digest`
  ([RFC 9530](https://www.rfc-editor.org/rfc/rfc9530)) and as a strong `ETag`. The endpoints answer
  `Range` requests, so an interrupted download resumes where it stopped, and a conditional request on
  an unchanged content answers `304 Not Modified`.

The digest is also what the library compares to detect identical content.

## Over HTTP

### Exposing the endpoints

```sh
dotnet add package Stellaegis.Documents.AspNetCore
```

```csharp
app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/api/t/{holder}")
   .MapDocumentsEndpoints(resolveHolder: http => http.GetRouteValue("holder") as string)
   .RequireAuthorizationPerOperation(op => op.ToString());   // "documents.read", "documents.purge"…
```

The endpoints are mapped under `documents` on the builder you pass, so the URL prefix is yours:
`/api/t/{holder}` above, a holder taken from a claim of the caller's token, or no holder at all.
`resolveHolder` gives the holder of each request, or `null` to answer `404`. Since a holder read from
the URL is user input, this function is where your host checks that the caller has rights on that
holder.

| Request | Operation | Effect |
| --- | --- | --- |
| `POST documents` | `create` | creates a document from a `multipart/form-data` body |
| `GET documents?continuationToken=…` | `list` | one page of the holder's active documents |
| `GET documents/{id}` | `read` | a document's metadata |
| `PUT documents/{id}/content` | `replace-content` | replaces its content (multipart body, `file` part only) |
| `GET documents/{id}/content` | `read-content` | its current content |
| `GET documents/{id}/versions` | `list-versions` | its versions |
| `GET documents/{id}/versions/{n}/content` | `read-version-content` | the content of version `n` |
| `DELETE documents/{id}` | `trash` | moves it to the trash |
| `DELETE documents` | `purge` | deletes every document of the holder for good, and answers how many |

An upload is a multipart body with a `documentType` field, an optional `isVersioned` field, then a
`file` part, which may carry a `Content-Digest` header. **The fields come before the `file` part.** The
endpoints read the request as it arrives and hand the file part to the library while it streams in,
without buffering it in memory or on disk; a part that has gone by cannot be read again. A request
that sends the file first answers `400` and says so.

Your host's request size limit applies: 30 MB by default under Kestrel. For larger documents, raise
`KestrelServerOptions.Limits.MaxRequestBodySize`, or the limit of a single request through
`IHttpMaxRequestBodySizeFeature`.

Everything else about the host belongs to you: authentication schemes, the policies themselves, CORS,
rate limiting, forwarded headers, health checks.

**Answers.** A caller without a `sub` claim (or name identifier) answers `401`; the library records it
as the actor of every change. An absent document, a document of another holder, and a document your
authorization refuses all answer `404`, so a caller cannot probe which documents exist. Errors are
answered as problem responses (RFC 9457) by the translation of
[Stellaegis.Primitives.AspNetCore](https://github.com/Stellaegis/Stellaegis.Primitives): invalid input
answers `400`, an unexpected failure `500`.

### Securing the endpoints

The library attaches no security to its routes. Each route carries an **operation** as metadata — the
`Operation` type of Stellaegis.Primitives.AspNetCore, with a module (`documents`), a name and a kind —
and you decide, per operation, which policy a caller must satisfy. `DocumentOperations` lists them.

`RequireAuthorizationPerOperation` receives each operation and returns the name of the policy to
require. The operation's **name** is the key: one policy per operation, `op.ToString()` giving
`documents.read`, `documents.purge` and so on, or any grouping of names you prefer. The **kind**
(`Read`, `Write`, `Delete`) follows the HTTP verb and says nothing of who may perform an operation.

**The purge is one route among the others**, the `purge` operation, of kind `Delete` like the trash.
Its consequence is worth weighing before you choose your policies:

- a policy chosen per operation name keeps the purge apart: `documents.purge` can require an
  administrator while `documents.trash` requires a member;
- a single policy on the whole group, or a policy chosen per kind, opens the purge to everyone who may
  trash a document. Choose it knowingly, or answer `documents.purge` with a policy of its own.

A route your mapping leaves without a policy falls to the fallback policy of your host, when it sets
one.

### Per-document authorization

The policies decide whether a caller may use an operation for a holder. Whether the caller may reach
*this* document is a second question, which the endpoints ask on every document they load, items of a
list included: they call `AuthorizeResourceAsync`, with the route's operation as the requirement and
the `Document` as the resource. A refusal answers `404`, and a refused document is left out of a list.

You answer with an ordinary ASP.NET Core authorization handler, typically one that looks the document's
GUID up in your database to check the caller's relation to the object it belongs to:

```csharp
public sealed class DocumentAuthorizationHandler : AuthorizationHandler<Operation, Document>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, Operation operation, Document document)
    {
        if (/* the caller may perform operation on document */)
            context.Succeed(operation);
        return Task.CompletedTask;
    }
}

builder.Services.AddSingleton<IAuthorizationHandler, DocumentAuthorizationHandler>();
```

**Refusal is the default.** Without a handler, every document is refused. A host in which every caller
allowed by the policies may reach every document of the holder declares it on the group, as it would
declare `[AllowAnonymous]`:

```csharp
app.MapGroup("/api/t/{holder}").MapDocumentsEndpoints(resolveHolder).AllowAnyResource();
```

### Reaching the endpoints from another process

```sh
dotnet add package Stellaegis.Documents.Client
```

```csharp
builder.Services
    .AddStellaegisDocumentsClient(builder.Configuration, holderPath: holder => $"t/{holder}/")
    .AddHttpMessageHandler<MyTokenHandler>();
```

```json
{ "Stellaegis": { "Documents": { "Client": { "BaseUrl": "https://documents.example.com/api/" } } } }
```

The client registers `IDocumentService`, so code written against the in-process service runs
unchanged. `holderPath` gives the path under which the host maps a holder's endpoints; without it, the
holder is not part of the URL. The client sends requests as they are: add a handler to the returned
builder to attach your token.

Two differences with the in-process service follow from the protocol:

- the server records the authenticated caller as the actor of a change, so the `actor` argument is not
  sent;
- the client checks a downloaded content against its `Repr-Digest` as you read it, and throws
  `InvalidDataException` at the end of a content damaged on its way. It sends the digest of an upload
  when you supply it or when the stream can be read twice; a forward-only stream goes unchecked rather
  than held in memory.

Answers map to the exceptions of the in-process service: `404` to `DocumentNotFoundException` (or
`null` from `GetAsync`), `400`, `401`, `403` and `409` to a `UserFacingException` of the matching
`ErrorType`.

### In a browser

A browser sends an `Authorization` header only with requests your script makes: a plain link or an
`<img>` element does not carry it. To download with a bearer token, fetch the content from script and
save it, or serve the API behind a cookie-authenticated backend. The library does not issue
short-lived download URLs.

## Events

The library publishes `DocumentIndexedEvent` when a document is created or its content changes, and
`DocumentTrashedEvent` when it is trashed, through the `IEventPublisher` port of
[Stellaegis.Primitives](https://github.com/Stellaegis/Stellaegis.Primitives). Each event carries the
holder, the document's GUID and what identifies it, never its file name.

Register your own `IEventPublisher` to send them to your message transport; without one, the events
are dropped. A publisher does not throw on a transport failure: the change is stored, and publication
is best effort.

## File names are encrypted

A file name chosen by a person can reveal more than the content's identity, so the PostgreSQL store
encrypts it with `pgcrypto`. The other metadata stay in clear, since the store queries them.

Two passphrase slots alternate for a key rotation. New writes use `ActiveKeySlot`, and each row records
the slot that encrypted it. To rotate, set the new passphrase in the free slot and make it active: rows
written before keep their former slot and stay readable as long as its passphrase stays configured.
The library does not re-encrypt existing rows in this version, so a former passphrase stays needed
until every row that uses it has been rewritten.

## Limits of this version

- **Concurrent changes of one document.** Two changes of the same document at the same moment compute
  the same content number; the store refuses the second, but its content has already replaced the
  first one's in the blob store. Serialize changes to a document if they can collide.
- **No resumable upload.** An interrupted upload starts over; downloads resume.
- **No grouping of operations.** Each operation stands alone: creating a document and attaching it to
  your object are two steps, and a document stays unattached if the second fails. Clean such
  documents up from your side, for example by checking the GUIDs your data does not reference.
- **No rename** of a document, and the list returns active documents only.

## Building and testing

```sh
dotnet test --solution Stellaegis.Documents.slnx
```

The PostgreSQL tests use a database of their own, `stellaegis_documents_tests` on a local server as
user `postgres`, or the connection string in `STELLAEGIS_DOCUMENTS_TEST_POSTGRES`. They rebuild the
`doc` schema from `docs/sql` at every run, and they are skipped when no server answers. The other tests
need no external service.

## License

MIT — see [LICENSE](https://github.com/Stellaegis/Stellaegis.Documents/blob/main/LICENSE). The packages depend on third-party components listed in
[THIRD-PARTY-NOTICES.md](https://github.com/Stellaegis/Stellaegis.Documents/blob/main/THIRD-PARTY-NOTICES.md).
