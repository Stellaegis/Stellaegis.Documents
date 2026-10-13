# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] — Unreleased

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

[1.0.0]: https://github.com/Stellaegis/Stellaegis.Documents/releases/tag/v1.0.0
