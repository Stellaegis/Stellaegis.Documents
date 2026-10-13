-- ============================================================================
-- Stellaegis — Reset script for schema: doc (Documents)
-- ============================================================================
-- WARNING: Destroys ALL data in the doc schema. Irreversible.
-- Intended for development/pre-release reset only.
-- Usage: psql -U stellaegis -d stellaegis -f reset-doc.sql
-- ============================================================================

BEGIN;

-- Documents before templates (no FK between them, but keeps a stable order
-- consistent with the other reset-*.sql scripts — children first).
DELETE FROM doc.documents;
DELETE FROM doc.templates;

COMMIT;
