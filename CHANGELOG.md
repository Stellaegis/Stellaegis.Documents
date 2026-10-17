# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Built on Stellaegis.Primitives 3.0.0 and Stellaegis.Blobs 3.0.0.

### Added

- `DocumentsModule`, the identity of the module (`IStellaegisModule`): the routes of `MapDocumentsEndpoints`
  belong to it.
- `MapDocumentsAdminEndpoints`, the administration of the module, mapped apart on a group the host secures for
  its administrators. It holds the purge: `DELETE documents/compartments/{compartment}`, the compartment being
  the object of the route rather than its context. `404` for a value that is not a compartment.
- Row-level security on `doc.documents` and `doc.document_versions`, in the schema script: a session sees and
  writes only the documents of the space in its session context, a version only when its document is visible,
  and a session without a space sees nothing. It is forced on the owner of the tables; a superuser escapes it,
  so the application connects under an ordinary role.
- `docs/sql/owner-isolation.sql`, optional: within a compartment, each actor sees only the documents it created.
  For a host where a document belongs to its author alone, never one that shares documents between users.
- `DocumentActors.Purge`, `admin:purge`: the actor the purge opens its session under, which `owner-isolation.sql`
  lets through so that a purge empties the whole compartment.
- `DocumentsClientOptions.AdminUrl`, where the client sends a purge. A purge without it throws an
  `InvalidOperationException`; a client configured with none of the three URLs fails when it is resolved.

### Changed

- **Breaking:** the purge leaves `MapDocumentsEndpoints`, and `DELETE documents` no longer exists: in an
  application with a single compartment, one policy on the group opened the emptying of the whole application
  to any user it admitted. Map `MapDocumentsAdminEndpoints` to keep a purge over HTTP.
- **Breaking:** `PurgeCompartmentAsync(compartment, actor)` takes the actor who purges, logged when the purge
  starts and when it ends. No event is published. Over HTTP the server takes the actor from the request
  context, and the client does not send it.

- **Breaking:** `DocumentsOptions.BlobName` becomes `KeyPrefix`, the first segment of every blob key:
  Stellaegis.Blobs 3.0.0 no longer has a blob space name. A blob is stored at
  `{compartment}/{KeyPrefix}/{document GUID}-{content number}`, `_shared/…` for the shared space, which is
  where `FileSystemBlobStore` kept it before. A prefix the blob store refuses throws an `ArgumentException`
  when the service is built.
- **Breaking:** `DocumentOperations.Module` becomes `Target`: the first argument of an `Operation` is the type
  of data it acts on. Its value, `documents`, and the operation names do not change.
- **Breaking:** every operation of `IDocumentService` takes an `Actor`, reads included: `GetAsync`, `ListAsync`
  (`ListAsync(space, actor, continuationToken)`), `OpenContentAsync`, `OpenVersionContentAsync` and
  `GetVersionsAsync`. Every method of `IDocumentStore` takes one too. The client does not send it: the server
  takes the authenticated caller.
- The PostgreSQL store opens every call through `AdoSpaceUnitOfWorkFactory` of Stellaegis.Primitives.Data: the
  space and the actor are set in the session context of the connection, `stellaegis.space` and
  `stellaegis.actor`, for row-level security, audit triggers or functions of the database to read. The
  in-memory store ignores the actor of a read.
- Depends on Stellaegis.Primitives 3.0.0, Stellaegis.Primitives.Data 3.0.0, Stellaegis.Primitives.AspNetCore
  3.0.0 and Stellaegis.Blobs 3.0.0. The startup check of Stellaegis.Primitives.AspNetCore now refuses a route
  without an authorization requirement: a host relying on its fallback policy declares `RequireAuthorization()`
  on the group.

## [2.0.0] — 2026-10-14

Each document belongs to a compartment or to the shared space, and the host attaches its own security
to the routes. Built on Stellaegis.Primitives 2.0.0 and Stellaegis.Blobs 2.0.1.

### Added

- The shared space, `Space.Shared`: documents that belong to no compartment. Over HTTP, the host reaches
  it by mapping the endpoints a second time on a group marked `InSharedSpace()`; a group without the mark
  never reaches it.
- `DocumentIndexedEvent` and `DocumentTrashedEvent` carry `IsShared`.
- `DocumentsClientOptions.CompartmentUrl` and `SharedUrl`: the client calls one URL per kind of space,
  `{compartment}` in `CompartmentUrl` replaced by the compartment of each call. A call in a space whose
  URL is not configured throws an `InvalidOperationException`.

### Changed

- **Breaking:** every operation of `IDocumentService` takes a `Space` instead of a holder string, and an
  `Actor` instead of an actor string. `Document.Holder` becomes `Document.Space`; `CreatedBy` and
  `ModifiedBy` of `Document`, and `CreatedBy` of `DocumentVersion`, become `Actor`. The empty holder that
  stood for the platform is gone: such documents belong to the shared space.
- **Breaking:** `PurgeHolderAsync(string)` becomes `PurgeCompartmentAsync(Compartment)`. The shared space
  has no purge.
- **Breaking:** the events carry `Compartment`, `null` for the shared space, instead of `Holder`.
- **Breaking:** the `doc` schema stores `compartment`, nullable, instead of `holder`, with a check that
  repeats the grammar of `Compartment`, and its index is renamed `ix_documents_compartment_created`.
- **Breaking:** `MapDocumentsEndpoints()` takes no resolver. The endpoints read the space and the actor
  from the request context of Stellaegis.Primitives.AspNetCore, which the host registers with
  `AddStellaegisRequestContext`; startup fails without it. An unresolved compartment answers `404`.
- **Breaking:** the endpoints no longer read the `sub` claim nor answer `401`: the actor comes from the
  request context, and whether a route requires an authenticated user is decided by the host's policies.
- **Breaking:** the endpoints carry no route name, so the module can be mapped once per space: ASP.NET
  Core requires route names to be unique across a host.
- **Breaking:** each route carries an operation (`DocumentOperations`, on the `Operation` type of
  Stellaegis.Primitives.AspNetCore), and the host attaches its policies to the returned group, per
  operation (`RequireAuthorizationPerOperation`). `MapDocumentsEndpoints` no longer takes `readPolicy` and
  `writePolicy`.
- **Breaking:** the purge is mapped with the other routes, as the `purge` operation, and answers `404` in
  the shared space. `MapDocumentsPurgeEndpoint` is removed.
- **Breaking:** access to each loaded document is asked through ASP.NET Core resource authorization
  (`AuthorizeResourceAsync`), refusal by default. A host answers it with an
  `AuthorizationHandler<Operation, Document>`, or declares `AllowAnyResource()`.
- **Breaking:** `AddStellaegisDocumentsClient` no longer takes a holder path, and
  `DocumentsClientOptions.BaseUrl` is replaced by `CompartmentUrl` and `SharedUrl`.
- **Breaking:** the `BlobSpace` option becomes `BlobName` (`Stellaegis:Documents:BlobName`).
- Errors are translated by Stellaegis.Primitives.AspNetCore: an unexpected failure answers `500`
  instead of `422`, and the error code travels in the `code` extension of the problem details.
- Depends on Stellaegis.Primitives 2.0.0, Stellaegis.Blobs 2.0.1 and Microsoft.Extensions 10.0.13.

### Removed

- `DocumentsHolderPath`.
- `IDocumentAccessPolicy`, `AllowAllDocumentAccessPolicy`, `DocumentAccessRequest` and
  `DocumentAction`, replaced by resource authorization.
- `IDocumentService.TryGetContentUrlAsync`, and the redirect of `GET documents/{id}/content` to a
  pre-signed URL: content is always streamed by the endpoints.

### Updating from 1.0.0

- Pass a `Space` where you passed a holder: a `Compartment`, such as `Compartment.Parse("acme")`, which
  converts implicitly, or `Space.Shared` where you passed the empty holder. Pass an `Actor`, such as
  `Actor.Parse(userId)`, where you passed an actor string.
- Recreate the `doc` schema from `docs/sql/stellaegis_documents.sql`. No migration script is provided.
- With `FileSystemBlobStore`, move the content as the changelog of Stellaegis.Blobs 2.0.0 describes, and
  rename `BlobSpace` to `BlobName` in your configuration.
- Register a request context, such as `AddStellaegisRequestContext(c => c.CompartmentFromRoute("org"))`,
  call `MapDocumentsEndpoints()` without a resolver, and map a second group marked `InSharedSpace()` if
  you serve shared documents.
- Attach your policies per operation, and register an `AuthorizationHandler<Operation, Document>` or
  declare `AllowAnyResource()`.
- Configure the client with `CompartmentUrl` and `SharedUrl` instead of `BaseUrl` and a holder path.

## [1.0.0] — 2026-10-13

First release as a standalone library.

### Added

- `IDocumentService`, which creates documents, replaces their content, reads the current content or a
  former version, lists a holder's documents a page at a time, moves a document to the trash and purges
  every document of a holder. Every operation names its holder; the empty holder is the platform.
- Versioning chosen per document at creation: a versioned document keeps each former content as a
  version, one that is not keeps only the current. Reading a document by its identifier alone always
  returns the current content.
- Content streamed end to end: the service hashes (SHA-256) and measures a content on its way to the
  blob store, never holding it in memory, and stores it flat in `Stellaegis.Blobs` under a key derived
  from the document's identity. No content is overwritten in place. Identical content sent again
  changes nothing; content that does not match the digest its sender announced is refused.
- `DocumentIndexedEvent` and `DocumentTrashedEvent`, published through the `IEventPublisher` port of
  `Stellaegis.Primitives`, so a consumer can keep its own projection.
- `IDocumentAccessPolicy`, a per-document authorization hook the consumer implements.
- `Stellaegis.Documents.PostgreSQL`: the store on PostgreSQL (schema `doc`), file names encrypted with
  `pgcrypto` under two alternating passphrase slots.
- `Stellaegis.Documents.AspNetCore`: the HTTP endpoints, mapped on a route group the host provides, for
  a holder the host resolves; downloads carry `Repr-Digest`, a strong `ETag` and answer `Range`
  requests; uploads stream from the request and may carry a `Content-Digest`.
- `Stellaegis.Documents.Client`: `IDocumentService` over HTTP, which sends and checks digests.

[2.0.0]: https://github.com/Stellaegis/Stellaegis.Documents/releases/tag/v2.0.0
[1.0.0]: https://github.com/Stellaegis/Stellaegis.Documents/releases/tag/v1.0.0
