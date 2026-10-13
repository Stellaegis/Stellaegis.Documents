-- ============================================================================
-- Stellaegis.Documents — reset script for schema doc
-- ============================================================================
-- WARNING: destroys every document of every holder. Irreversible, and the
-- content in the blob store is left behind. For development only.
-- Usage: psql -d <database> -f reset-doc.sql
-- ============================================================================

begin;

-- The versions go with their documents (on delete cascade).
delete from doc.documents;

commit;
