-- ============================================================================
-- Stellaegis — Reset script for schema: doc (Documents)
-- ============================================================================
-- WARNING: Destroys ALL data in the doc schema. Irreversible.
-- Intended for development/pre-release reset only.
-- Usage: psql -U stellaegis -d stellaegis -f reset-doc.sql
-- ============================================================================

BEGIN;

DELETE FROM doc.documents;

COMMIT;
