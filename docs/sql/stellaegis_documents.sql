-- ============================================================================
-- Stellaegis Documents — Database schema (doc)
-- ============================================================================
-- Database: stellaegis_documents
-- Schema:   doc
-- No cross-database joins.
-- All constraints explicitly named.
--
-- Isolation regime: pooled, correlated by tenant_slug — NOT tenant_id.
-- This schema is not guaranteed to cohabit with reg (different resource
-- profiles, especially on the documents side). tenant_slug is the public,
-- stable, immutable, cross-database identifier already used everywhere in-database
-- cohabitation is not guaranteed (tenantDB.*/tenantSys.*, vault keys, /t/{tenantSlug}).
-- See _workspace/atlas.md § Isolation (three regimes) and
-- _workspace/todo.md [GED] § Templates for the full rationale.
--
-- No bigint surrogate: between doc.templates/doc.documents (and with trash,
-- cross-database), no internal relation justifies a technical FK — see
-- sql-conventions.md § Internal primary key ("when a relation links two tables
-- internally to the model"). {entity}_unique_id uuid is therefore the primary
-- key directly.
--
-- The exception that proves the rule: doc.document_versions does have a real
-- internal relation to doc.documents (revision history). Since
-- documents.document_unique_id is already a uuid PK, the FK targets it directly
-- — still no bigint surrogate resurrected (see § Document versions).
-- ============================================================================

create schema if not exists doc;

-- ============================================================================
-- Documents (GED metadata — never the binary content)
-- ============================================================================
-- The GED indexes the existence and identity of documents, not their business
-- semantics (contract formalized in todo.md [GED]). The binary content lives
-- exclusively in IDocumentBlobStore, never in the database.
-- ============================================================================

create type doc.document_status as enum ('active', 'trashed');

create table doc.documents (
  document_unique_id    uuid        not null default gen_random_uuid(),
  tenant_slug           text        not null,
  document_type         text        not null,
  storage_path          text        not null,
  content_hash          text        not null,
  business_object_link  uuid,
  status                doc.document_status not null default 'active',
  is_versioned          boolean     not null default false,

  created_at            timestamptz not null default now(),
  created_by            text        not null,
  modified_at           timestamptz,
  modified_by           text,

  constraint pk_documents primary key (document_unique_id),
  constraint ck_documents_tenant_slug check (
    tenant_slug ~ '^[a-z][a-z0-9-]{2,62}$' and tenant_slug !~ '--'
  ),
  constraint ck_documents_type check (char_length(document_type) between 1 and 64),
  constraint ck_documents_storage_path check (char_length(storage_path) between 1 and 2048),
  constraint ck_documents_content_hash check (content_hash ~ '^[0-9a-f]{64}$'),
  constraint ck_documents_created_by check (char_length(created_by) <= 128),
  constraint ck_documents_modified_by check (modified_by is null or char_length(modified_by) <= 128)
);

comment on table  doc.documents is
  'GED index: existence and identity of documents — never the content, never the business semantics (boundary formalized in todo.md [GED]). storage_path/content_hash always point to the CURRENT version (denormalized pointer — one GUID, one read, the document, see todo.md [GED] § Versionning). The full history lives in doc.document_versions.';
comment on column doc.documents.document_unique_id is
  'Public GUID — the one published in Backplane events (DocumentIndexed/DocumentRenamed/DocumentTrashed) and referenced by the tenant''s business projection. Stable regardless of the number of versions.';
comment on column doc.documents.storage_path is
  'LOGICAL storage key in the tenant''s blob space (BlobNames.Documents = "ged") — NOT a physical URI: provider/account/container/credentials resolved per tenant (vault binding), see atlas § Documents & Blobs. Always reflects the current version: updated (same transaction as the insert into doc.document_versions) on each new revision, whether historized (is_versioned) or overwritten.';
comment on column doc.documents.content_hash is
  'SHA-256 fingerprint (hex, 64 chars) of the current content. Compared with the incoming hash on each upload to decide whether there is a real change — an identical re-upload must neither overwrite nor create a superfluous version (avoids volume blow-up on retries/re-reads). No need to duplicate this hash per historized version: a version exists only because its hash differed at creation time.';
comment on column doc.documents.business_object_link is
  'Opaque GUID to the tenant''s business entity. The GED does not interpret it — boundary "index the existence, not the semantics".';
comment on column doc.documents.status is
  'GED lifecycle. "trashed" signals the move to trash (trash schema — a distinct service, accepted duplication, see todo.md [TRASH]). The exact transition (row kept or deleted on trashing?) remains an open arbitration.';
comment on column doc.documents.is_versioned is
  'Opt-in flag set by the calling application at creation (a versioning system fails to be adopted if it demands work from developers; absent at creation, defaults to false). If true, a content change detected by hash creates a new row in doc.document_versions instead of overwriting in place. Remains a deliberate choice per document type: versioning everything blows up storage volume, and some documents structurally must not be mutable once issued (e.g. invoices — immutability potentially required by regulation, outside the GED''s arbitration scope).';

-- ============================================================================
-- Document versions (revision history — resolving a specific version)
-- ============================================================================
-- Does NOT hold the current version as a semantic duplicate: doc.documents stays
-- the denormalized "current" pointer (storage_path/content_hash always up to date)
-- to preserve single-access read by GUID. This table serves two uses: listing the
-- history (with dates) and resolving a specific version via a dedicated GUID +
-- number route.
--
-- First real internal relation of the doc schema (FK to doc.documents) — directly
-- on the existing uuid PK, no bigint surrogate resurrected (the exception that
-- proves the rule of sql-conventions.md § Internal primary key).
--
-- No content_hash here: redundant for change detection, which only ever compares
-- the incoming hash with the CURRENT one (on doc.documents) — a version exists only
-- because its hash differed at creation, comparing historical hashes with each other
-- adds nothing.
--
-- No revision note/label column (e.g. "change_note"): if a developer wants to
-- annotate their versions, that belongs in THEIR database — adding it here would
-- break the confidentiality/isolation principle (the GED indexes existence, never
-- business semantics — see todo.md [GED]).
-- ============================================================================

create table doc.document_versions (
  document_unique_id   uuid        not null,
  version_number       integer     not null,
  storage_path         text        not null,

  created_at           timestamptz not null default now(),
  created_by           text        not null,

  constraint pk_document_versions primary key (document_unique_id, version_number),
  constraint fk_document_versions_document
    foreign key (document_unique_id) references doc.documents (document_unique_id) on delete cascade,
  constraint ck_document_versions_number check (version_number >= 1),
  constraint ck_document_versions_storage_path check (char_length(storage_path) between 1 and 2048),
  constraint ck_document_versions_created_by check (char_length(created_by) <= 128)
);

comment on table  doc.document_versions is
  'Revision history of a versioned document (is_versioned = true). Composite key (document_unique_id, version_number) — the document keeps its stable public identity, only the revision changes. Does not duplicate the current version: it resolves in one access via doc.documents.storage_path (denormalized pointer). Used to list the history (with dates) and to resolve a specific version (dedicated GUID + number route).';
comment on column doc.document_versions.storage_path is
  'LOGICAL storage key in the tenant''s "ged" blob space — NOT a physical URI (per-tenant resolution, see atlas § Documents & Blobs). Each version has its own key, never overwritten in place.';

-- ============================================================================
-- Views (lifecycle filters)
-- ============================================================================

create view doc.v_documents as
select *
from doc.documents
where status = 'active';

-- ============================================================================
-- Indexes
-- ============================================================================

-- Documents: per-tenant listing ("my documents" view)
create index ix_documents_tenant_slug on doc.documents (tenant_slug) where status = 'active';

-- Documents: resolution from the tenant's business projection
create index ix_documents_business_object_link
  on doc.documents (business_object_link) where business_object_link is not null;

-- ============================================================================
-- Framework evolution tracking
-- ============================================================================

create table doc._schema_migrations (
  version      smallint     not null,
  script_name  text         not null,
  checksum     text         not null,
  applied_at   timestamptz  not null default now(),
  constraint pk_doc_schema_migrations primary key (version)
);

create table doc._seed_migrations (
  version      smallint     not null,
  description  text         not null,
  applied_at   timestamptz  not null default now(),
  constraint pk_doc_seed_migrations primary key (version)
);
