# Stellaegis.Documents

Stellaegis.Documents is a .NET library that keeps documents for an application: it stores each
document's content, keeps its versions when you ask for them, and records what identifies the
document — its type, file name, media type, length and SHA-256 digest. It partitions documents by
**compartment**, the party a document belongs to, so one deployment can serve many customers without
one of them ever reaching another's documents. Documents that belong to no compartment live in a
**shared space**. Content is stored through
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
| the space, the document type, file name, media type, length, digest | search, built from the events the library publishes |
| the versions of a versioned document | the links between your objects and the documents, by GUID |
| the content, through Stellaegis.Blobs | fine-grained authorization, through an authorization handler you write |

A typical application stores the document's GUID next to its own data — an invoice row holding the
GUID of its PDF — and asks the library for the content when it needs it. Your database knows only the
GUIDs of your own documents; the library knows all of them.

The trade-off is deliberate. Search and classification stay in your database, under your schema, so
two applications sharing the library can organize documents differently. In return, the library cannot
answer a question such as "the invoices of this customer for March": your application answers it from
its own data. The library publishes an event on every change so that you can keep such a projection.

## Compartments and the shared space

Every operation names the **space** it acts in: `CreateAsync(space, …)`, `OpenContentAsync(space, id, actor)`
and so on. The `Space` type comes from
[Stellaegis.Primitives](https://github.com/Stellaegis/Stellaegis.Primitives), and a space is one of two
things:

- **A compartment**, the `Compartment` type: the unit of isolation of an organization, such as a customer.
  A user is not a compartment: the documents a user owns are a matter of ownership within one. Its identifier has 3 to
  63 characters: a lowercase letter first, then lowercase letters, digits and single hyphens, ending with a
  letter or a digit, such as `acme` or `customer-42`. A `Compartment` converts implicitly to its space, so
  you can pass one wherever a space is expected.
- **The shared space**, `Space.Shared`: the documents that belong to no compartment, such as templates
  that every customer uses.

A document of another space behaves exactly as an absent one, on every path, versions included. A
compartment never reaches the shared space, and the shared space never reaches a compartment.

An application that does not partition its documents uses a single compartment, named after the
organization that owns the data, everywhere: `Compartment.Parse("acme")`, declared once as a constant. The
day a second organization joins, the first keeps its name, and nothing moves. A placeholder such as `main`
would then name one organization among several.

The library trusts the space it receives. Your code must take it from a source it controls — the
authenticated caller, a route value it has authorized — and never pass on a compartment a user typed.

Every operation also names its **actor**, an `Actor`, the other type of Stellaegis.Primitives the library
takes: a user identifier, a client identifier, or the name a scheduled task gives itself. The actor of a
change is recorded on the document. The actor of every call, reads included, reaches the database in the
session context of its connection, where row-level security can read it.

## Getting started in process

```sh
dotnet add package Stellaegis.Documents.PostgreSQL
dotnet add package Stellaegis.Blobs.FileSystem
```

Create the schema with [`docs/sql/stellaegis_documents.sql`](https://github.com/Stellaegis/Stellaegis.Documents/blob/main/docs/sql/stellaegis_documents.sql).
The script creates the `doc` schema and enables the `pgcrypto` extension, which encrypts file names;
the role that runs it needs the right to create an extension. It also enables row-level security, so
connect the application under an ordinary role: see [Isolation in the database](#isolation-in-the-database).

Then register a blob store and the document store:

```csharp
builder.Services.AddStellaegisFileSystemBlobStore(o => o.RootPath = "/var/lib/myapp/blobs");
builder.Services.AddStellaegisDocumentsPostgreSQL(builder.Configuration);
```

```json
{
  "Stellaegis": {
    "Documents": {
      "KeyPrefix": "documents",
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

- `KeyPrefix` is the first segment of the key of every blob holding document content, in Stellaegis.Blobs: it keeps the documents apart from the other content of the store. A prefix the store refuses fails when the service is built.
- `ListPageSize` is the number of documents in one page of a list, between 1 and 1000.
- `Keys` are the passphrases that encrypt file names (see
  [File names are encrypted](#file-names-are-encrypted)). Supply them from a secret store or the
  environment, never from a committed file.

`IDocumentService` is then available from dependency injection:

```csharp
var acme = Compartment.Parse("acme");

var document = await documents.CreateAsync(
    space: acme,
    documentType: "invoice",
    new DocumentUpload { Content = stream, FileName = "Invoice 2026-001.pdf", ContentType = "application/pdf" },
    actor: Actor.Parse(userId),
    isVersioned: false);

await using var content = await documents.OpenContentAsync(acme, document.DocumentUniqueId, Actor.Parse(userId));
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

Content is stored flat: one blob per content, in the document's compartment or in the shared space, with
the key `{KeyPrefix}/{document GUID}-{content number}`. Stellaegis.Blobs keeps the shared
space apart from every compartment. The library stores no key: it derives each key from the document's
identity. Moving the content to another storage therefore needs no change in the database.

### Trash and purge

`TrashAsync` marks a document as trashed. It leaves the list and can no longer change; its content
stays.

`PurgeCompartmentAsync(compartment, actor)` deletes every document of a compartment for good, whatever its
status: content first, then the index, in batches of a hundred. A purge cut halfway resumes when called
again, and a complete one deletes nothing more. It takes a `Compartment`, so the shared space cannot be
purged this way: its trashed documents stay.

The purge publishes no event. It logs its actor when it starts and when it ends, so an interrupted purge
still names its author. A task that purges on a schedule acts under its own name, such as `job:purge`.

The library performs the purge; it does not decide when. Confirmations ("type the customer's name to
confirm") and any retention period before the final deletion belong to the part of your application
that manages the compartment's life — closing a customer account, for example — which calls the purge
once they are through.

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
builder.Services.AddStellaegisRequestContext(c => c.CompartmentFromRoute("org"));

app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/api/orgs/{org}").MapDocumentsEndpoints()
   .RequireAuthorizationPerOperation(op => op.ToString());   // "documents.read", "documents.trash"…
```

The endpoints are mapped under `documents` on the builder you pass, so the URL prefix is yours.

**The space and the actor of a request** come from the request context of
[Stellaegis.Primitives.AspNetCore](https://github.com/Stellaegis/Stellaegis.Primitives), which your host
registers with `AddStellaegisRequestContext`. You choose where the compartment is read: a route value as
above, a claim of the caller's token, a single compartment for the whole application, or a source of your
own. A compartment that cannot be resolved — a missing or invalid route value, for instance — answers
`404`. Since a compartment read from the URL is user input, your security is where the host checks that
the caller has rights on it. The actor is read from the caller's claims, and a request without an
authenticated user is recorded as `anonymous`; whether a route requires an authenticated user is decided
by your policies. Startup fails when the module is mapped without a request context registered.

**The shared space** is reached by mapping the endpoints a second time, on a group you mark
`InSharedSpace()`:

```csharp
app.MapGroup("/api/shared").MapDocumentsEndpoints().InSharedSpace()
   .RequireAuthorizationPerOperation(op => $"shared.{op}");
```

On a marked group the space is the shared space, and the compartment is not read. A group without the
mark never reaches the shared space; if you map none, shared documents are not reachable over HTTP at all.
The cost of this choice is a second mapping, with its own URL and its own policies. In return, nothing
reaches the shared space by accident: a missing compartment answers `404`, and it never falls back to the
shared space.

**Route names.** The endpoints carry no route name, because ASP.NET Core requires names to be unique across
a host, and the module is mapped once per space. A host that wants names — to generate links, or as
operation identifiers in an OpenAPI document — sets them on the returned group from each route's
operation.

| Request | Operation | Effect |
| --- | --- | --- |
| `POST documents` | `create` | creates a document from a `multipart/form-data` body |
| `GET documents?continuationToken=…` | `list` | one page of the active documents of the space |
| `GET documents/{id}` | `read` | a document's metadata |
| `PUT documents/{id}/content` | `replace-content` | replaces its content (multipart body, `file` part only) |
| `GET documents/{id}/content` | `read-content` | its current content |
| `GET documents/{id}/versions` | `list-versions` | its versions |
| `GET documents/{id}/versions/{n}/content` | `read-version-content` | the content of version `n` |
| `DELETE documents/{id}` | `trash` | moves it to the trash |

An upload is a multipart body with a `documentType` field, an optional `isVersioned` field, then a
`file` part, which may carry a `Content-Digest` header. **The fields come before the `file` part.** The
endpoints read the request as it arrives and hand the file part to the library while it streams in,
without buffering it in memory or on disk; a part that has gone by cannot be read again. A request
that sends the file first answers `400` and says so.

**Administration.** The purge of a compartment acts on the compartment, not within it, so it is mapped
apart, on a group you secure for your administrators. The compartment is a segment of its path, and the
request context gives only the actor. A host that does not map it has no purge: in an application with a
single compartment, a purge in the main mapping would empty the whole application for anyone the group's
policy admits.

```csharp
app.MapGroup("/api/admin").MapDocumentsAdminEndpoints().RequireAuthorization("platform-admin");
```

| Request | Operation | Effect |
| --- | --- | --- |
| `DELETE documents/compartments/{compartment}` | `purge` | deletes every document of the compartment for good, and answers how many; `404` for a value that is not a compartment, the shared space included |

Your host's request size limit applies: 30 MB by default under Kestrel. For larger documents, raise
`KestrelServerOptions.Limits.MaxRequestBodySize`, or the limit of a single request through
`IHttpMaxRequestBodySizeFeature`.

Everything else about the host belongs to you: authentication schemes, the policies themselves, CORS,
rate limiting, forwarded headers, health checks.

**Answers.** An absent document, a document of another space, and a document your authorization refuses
all answer `404`, so a caller cannot probe which documents exist. A `401` or a `403` comes from your
policies, never from the endpoints themselves. Errors are
answered as problem responses (RFC 9457) by the translation of
[Stellaegis.Primitives.AspNetCore](https://github.com/Stellaegis/Stellaegis.Primitives): invalid input
answers `400`, an unexpected failure `500`.

### Securing the endpoints

The library attaches no security to its routes. Each route carries an **operation** as metadata — the
`Operation` type of Stellaegis.Primitives.AspNetCore, with a target (`documents`), a name and a kind —
and you decide, per operation, which policy a caller must satisfy. `DocumentOperations` lists them.

`RequireAuthorizationPerOperation` receives each operation and returns the name of the policy to
require. The operation's **name** is the key: one policy per operation, `op.ToString()` giving
`documents.read`, `documents.trash` and so on, or any grouping of names you prefer. The **kind**
(`Read`, `Write`, `Delete`) follows the HTTP verb and says nothing of who may perform an operation.

Startup fails when a route is left without an authorization requirement or `AllowAnonymous()`, naming the
routes: a fallback policy does not count, since it does not know the operation. A host that secures the
whole group with one policy declares it, `RequireAuthorization("member")`.

### Per-document authorization

The policies decide whether a caller may use an operation in a space. Whether the caller may reach
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

**Refusal is the default.** Without a handler, every document is refused, and startup fails with a
message that names the routes and the way out. A host in which every caller allowed by the policies may
reach every document of the space declares it on the group, as it would declare `[AllowAnonymous]`:

```csharp
app.MapGroup("/api/orgs/{org}").MapDocumentsEndpoints().AllowAnyResource();
```

So does a host whose database keeps each user to their own documents: see
[Isolation in the database](#isolation-in-the-database).

### Reaching the endpoints from another process

```sh
dotnet add package Stellaegis.Documents.Client
```

```csharp
builder.Services
    .AddStellaegisDocumentsClient(builder.Configuration)
    .AddHttpMessageHandler<MyTokenHandler>();
```

```json
{
  "Stellaegis": {
    "Documents": {
      "Client": {
        "CompartmentUrl": "https://documents.example.com/api/orgs/{compartment}/",
        "SharedUrl": "https://documents.example.com/api/shared/",
        "AdminUrl": "https://documents.example.com/api/admin/"
      }
    }
  }
}
```

The client registers `IDocumentService`, so code written against the in-process service runs
unchanged. It sends requests as they are: add a handler to the returned builder to attach your token.

The URLs are the groups your host maps, and the client deduces none from another:

- `CompartmentUrl` serves the calls in a compartment. `{compartment}` is replaced by the compartment of each
  call. Without the placeholder, every compartment goes to the same URL, for a host that resolves the
  compartment some other way, such as from the token.
- `SharedUrl` serves the calls in the shared space.
- `AdminUrl` serves the purge.

Any may be left out. A call whose URL is missing throws an `InvalidOperationException`, and never goes to
another URL. A client configured with none fails when it is resolved. The URLs go through the same
`HttpClient` and its handlers: if your host authenticates the shared space or the administration
differently, your handler tells them apart by the request URL.

Two differences with the in-process service follow from the protocol:

- the server takes the authenticated caller as the actor of every call, so the `actor` argument is not
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
document's GUID and what identifies it, never its file name, and its space as two fields: `Compartment`,
and `IsShared`, true when the document belongs to the shared space, whose `Compartment` is then `null`.
The `Space` type has no serialized form, and your transport may serialize the events; the two fields
travel as they are. Rebuild the space with `e.IsShared ? Space.Shared : Space.Of(e.Compartment)`.

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

## Isolation in the database

The PostgreSQL store opens every call in a space, for an actor, through `ISpaceUnitOfWorkFactory` of
[Stellaegis.Primitives.Data](https://github.com/Stellaegis/Stellaegis.Primitives): both are set in the
session context of the connection, `stellaegis.space` (the compartment, or `_shared`) and
`stellaegis.actor`. Row-level security reads them, so the database keeps the spaces apart even from a
query that would forget its filter.

**Spaces.** The schema script enables row-level security on both tables: a session sees and writes only
the documents of its space, a version only when its document is visible, and a session without a space
sees nothing. It applies to every host.

⚠ **Connect under an ordinary role.** PostgreSQL never applies row-level security to a superuser. The
script forces it on the owner of the tables, so the role that created the schema is filtered too.

**Owners, optionally.** [`docs/sql/owner-isolation.sql`](https://github.com/Stellaegis/Stellaegis.Documents/blob/main/docs/sql/owner-isolation.sql)
further keeps every actor to the documents it created: Alice and Bob work in the same compartment, and
neither sees the other's documents. Apply it after the schema script, where a document belongs to its
author alone. Do not apply it where your application shares documents between users, through an
authorization handler for instance: the database would hide the documents of others before the handler
is asked. The purge opens its session under `DocumentActors.Purge`, `admin:purge`, which the script lets
through so that a purge empties the whole compartment; the library logs the actor who asked for it.

This guards against a mistake of the application, not against a client of the database: whoever holds a
connection can change its session context.

An application with a single compartment, where each user sees only their own documents and nothing
else decides who reaches what:

```csharp
public static class Tenant
{
    public static readonly Compartment Owner = Compartment.Parse("acme");   // the organization owning the data
}

builder.Services.AddStellaegisFileSystemBlobStore(o => o.RootPath = "/var/lib/myapp/blobs");
builder.Services.AddStellaegisDocumentsPostgreSQL(builder.Configuration);   // schema script, then owner-isolation.sql
builder.Services.AddStellaegisRequestContext(c => c.SingleCompartment(Tenant.Owner));

app.MapGroup("/api").MapDocumentsEndpoints()
   .RequireAuthorization()      // any authenticated user
   .AllowAnyResource();         // the database returns the caller's documents only
// MapDocumentsAdminEndpoints is not mapped: this host has no purge.
```

⚠ `AllowAnyResource()` relies on the owner script being applied. Without it, every user reaches every
document of the compartment, and nothing at startup can tell.

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

The PostgreSQL tests use databases of their own, `stellaegis_documents_tests` on a local server as
user `postgres`, or the connection string in `STELLAEGIS_DOCUMENTS_TEST_POSTGRES`, and the same name
suffixed with `_rls` and with `_owner`, the latter with `owner-isolation.sql` applied. They rebuild the `doc` schema from
`docs/sql` at every run, and they are skipped when no server answers. The tests of row-level security
connect under `stellaegis_documents_app`, an ordinary role they create, since a superuser escapes the
policies. The other tests
need no external service.

## License

MIT — see [LICENSE](https://github.com/Stellaegis/Stellaegis.Documents/blob/main/LICENSE). The packages depend on third-party components listed in
[THIRD-PARTY-NOTICES.md](https://github.com/Stellaegis/Stellaegis.Documents/blob/main/THIRD-PARTY-NOTICES.md).
