-- ============================================================================
-- Stellaegis.Documents — database schema (doc)
-- ============================================================================
-- Schema: doc. Requires pgcrypto, which encrypts the file names.
-- All constraints and indexes are explicitly named.
--
-- The schema indexes the existence and identity of documents, never their
-- content (stored through Stellaegis.Blobs) nor their business semantics: the
-- links between documents and business objects, search and fine-grained access
-- stay in the consumer's own database, keyed by document_unique_id.
--
-- Isolation: every document carries its compartment, and every query of the
-- store filters on it. A null compartment is the shared space: documents that
-- belong to no compartment, reached only through routes the host marks for it.
--
-- Content: one blob per content, stored flat under a key the module derives
-- from the identity alone, {document_unique_id:N}-{version_number}. No key is
-- stored here. A change of content always goes to the next number, so no
-- content is overwritten in place.
--
-- Keys: document_unique_id is the primary key, and doc.document_versions carries
-- it as its foreign key. There is no bigint surrogate on purpose: every access
-- starts from the public identifier, and the space check joins a version to its
-- document in any case, so no internal join would use one.
-- ============================================================================

create extension if not exists pgcrypto;

create schema if not exists doc;

create type doc.document_status as enum ('active', 'trashed');

-- ============================================================================
-- Documents
-- ============================================================================

create table doc.documents (
  document_unique_id  uuid                not null default gen_random_uuid(),
  compartment         text,
  document_type       text                not null,
  version_number      integer             not null default 1,
  content_hash        text                not null,
  file_name           bytea               not null,
  content_type        text                not null,
  content_length      bigint              not null,
  key_slot            smallint            not null,
  status              doc.document_status not null default 'active',
  is_versioned        boolean             not null default false,

  created_at          timestamptz         not null default now(),
  created_by          text                not null,
  modified_at         timestamptz,
  modified_by         text,

  constraint pk_documents primary key (document_unique_id),
  constraint ck_documents_compartment check (
    compartment ~ '^[a-z](-?[a-z0-9])+$' and char_length(compartment) between 3 and 63
  ),
  constraint ck_documents_document_type check (char_length(document_type) between 1 and 64),
  constraint ck_documents_version_number check (version_number >= 1),
  constraint ck_documents_content_hash check (content_hash ~ '^[0-9a-f]{64}$'),
  constraint ck_documents_content_type check (char_length(content_type) between 1 and 255),
  constraint ck_documents_content_length check (content_length >= 0),
  constraint ck_documents_key_slot check (key_slot in (1, 2)),
  constraint ck_documents_created_by check (char_length(created_by) between 1 and 128),
  constraint ck_documents_modified_by check (char_length(modified_by) between 1 and 128)
);

comment on table doc.documents is
  'Index of documents: existence and identity, never the content nor the business semantics. The version number, hash and file metadata always describe the current content; doc.document_versions keeps the revisions of a versioned document.';
comment on column doc.documents.document_unique_id is
  'Public identifier, published in the events and kept by the consumer next to its own objects. Stable across versions.';
comment on column doc.documents.compartment is
  'The compartment the document belongs to; null for the shared space, which holds the documents that belong to no compartment. Same grammar as the Compartment of Stellaegis.Primitives, which the check repeats.';
comment on column doc.documents.version_number is
  'Number of the current content, from 1, increased by each change of content; the blob key derives from it. A versioned document keeps its former contents in doc.document_versions; one that is not versioned keeps only the current.';
comment on column doc.documents.content_hash is
  'SHA-256 of the current content (lowercase hex). Identical content sent again neither overwrites nor adds a version; a download serves it as Repr-Digest and ETag.';
comment on column doc.documents.file_name is
  'File name given by the uploader, encrypted by pgp_sym_encrypt with the passphrase of key_slot. Sensitive: never decrypt it in a view or a log. Its length is checked by the module, before encryption.';
comment on column doc.documents.key_slot is
  'Passphrase slot (1 or 2) that encrypted file_name; two slots alternate for a key rotation.';
comment on column doc.documents.status is
  'Lifecycle: trashed only marks the document; the content stays until its compartment is purged. The shared space has no purge: its trashed documents stay.';
comment on column doc.documents.is_versioned is
  'Set at creation by the consumer: a content change then adds a row to doc.document_versions instead of overwriting in place.';

-- ============================================================================
-- Document versions
-- ============================================================================

create table doc.document_versions (
  document_unique_id  uuid        not null,
  version_number      integer     not null,
  file_name           bytea       not null,
  content_type        text        not null,
  content_length      bigint      not null,
  content_hash        text        not null,
  key_slot            smallint    not null,

  created_at          timestamptz not null default now(),
  created_by          text        not null,

  constraint pk_document_versions primary key (document_unique_id, version_number),
  constraint fk_document_versions_document
    foreign key (document_unique_id) references doc.documents (document_unique_id) on delete cascade,
  constraint ck_document_versions_version_number check (version_number >= 1),
  constraint ck_document_versions_content_type check (char_length(content_type) between 1 and 255),
  constraint ck_document_versions_content_length check (content_length >= 0),
  constraint ck_document_versions_content_hash check (content_hash ~ '^[0-9a-f]{64}$'),
  constraint ck_document_versions_key_slot check (key_slot in (1, 2)),
  constraint ck_document_versions_created_by check (char_length(created_by) between 1 and 128)
);

comment on table doc.document_versions is
  'Revisions of a versioned document, each with its own file metadata; its content is the blob of the same version number. Read through the document, whose compartment scopes every access.';
comment on column doc.document_versions.file_name is
  'File name of this revision, encrypted as doc.documents.file_name.';

-- ============================================================================
-- Indexes
-- ============================================================================

-- The documents of a space: the paged list (active ones, most recent first, keyset
-- on created_at then document_unique_id) and the purge (any status). The shared
-- space is the null compartment, which the index holds like any other value.
create index ix_documents_compartment_created
  on doc.documents (compartment, created_at desc, document_unique_id desc);

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
