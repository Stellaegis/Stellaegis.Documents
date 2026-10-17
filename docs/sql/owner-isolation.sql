-- ============================================================================
-- Stellaegis.Documents — isolation by owner (optional)
-- ============================================================================
-- Keeps every actor to the documents it created, within its space: Alice and
-- Bob work in the same compartment, and neither sees the other's documents.
-- Apply it after stellaegis_documents.sql, to a host where a document belongs
-- to its author alone. Do not apply it where a document is shared between users
-- — through an authorization handler on the resource, for instance: the
-- database would hide the documents of others before the handler is asked.
--
-- The policy is restrictive: it narrows the space policy of the schema, and
-- never widens it. The actor is the one the store sets in the session context,
-- stellaegis.actor; a session without one sees nothing.
--
-- The purge opens its session under the actor admin:purge (DocumentActors.Purge
-- in the library), which this policy lets through, so that a purge empties the
-- whole compartment. Two consequences:
--   - a named value opens an access: any code holding the connection can set
--     it, as it can set any actor. This guards against a query that forgets its
--     filter, not against a client of the database;
--   - an audit trigger sees admin:purge as the actor of a purge. The library logs
--     the actor who asked for it.
--
-- Usage: psql -d <database> -f owner-isolation.sql. It can be applied again.
-- ============================================================================

begin;

drop policy if exists pl_documents_owner on doc.documents;

create policy pl_documents_owner on doc.documents
  as restrictive
  using (
    created_by = current_setting('stellaegis.actor', true)
    or current_setting('stellaegis.actor', true) = 'admin:purge')
  with check (
    created_by = current_setting('stellaegis.actor', true)
    or current_setting('stellaegis.actor', true) = 'admin:purge');

-- The versions follow: their policy reads doc.documents, this policy included.

commit;
