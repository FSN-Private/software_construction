-- Derived branch reachability is paired with the existing vc_entry_asset index.
-- Immutable commits, entries, and refs remain the authoritative history.
CREATE TABLE vc_lineage_reachable (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    normalized_ref text NOT NULL,
    commit_id char(64) NOT NULL REFERENCES vc_commits(id),
    received_at timestamptz NOT NULL,
    PRIMARY KEY (owner_id, library_id, normalized_ref, commit_id),
    FOREIGN KEY (owner_id, library_id, normalized_ref)
        REFERENCES vc_refs(owner_id, library_id, normalized_name)
        ON UPDATE CASCADE ON DELETE CASCADE
);

CREATE INDEX vc_visibility_hidden_scope ON vc_visibility(owner_id, library_id) WHERE hidden;

CREATE FUNCTION vc_rebuild_lineage_reachable(p_owner text, p_library text, p_ref text, p_head char(64))
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    DELETE FROM vc_lineage_reachable
    WHERE owner_id=p_owner AND library_id=p_library AND normalized_ref=p_ref;
    IF p_head IS NULL THEN RETURN; END IF;
    INSERT INTO vc_lineage_reachable(owner_id, library_id, normalized_ref, commit_id, received_at)
    WITH RECURSIVE reachable(id) AS (
        SELECT p_head
        UNION
        SELECT parent.parent_id FROM reachable r
        JOIN vc_commit_parents parent ON parent.commit_id=r.id
    )
    SELECT p_owner, p_library, p_ref, commit.id, commit.received_at
    FROM reachable r
    JOIN vc_commits commit ON commit.id=r.id AND commit.owner_id=p_owner AND commit.library_id=p_library;
END;
$$;

CREATE FUNCTION vc_sync_lineage_reachable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.kind <> 'branch' THEN RETURN NEW; END IF;
    IF TG_OP = 'UPDATE' THEN
        IF NEW.head_commit_id IS NOT DISTINCT FROM OLD.head_commit_id THEN RETURN NEW; END IF;
        -- First-parent advancement adds one commit. A merge also imports the
        -- second parent's ancestry, including commits absent from the target.
        IF OLD.kind = 'branch' AND OLD.head_commit_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM vc_commit_parents
            WHERE commit_id=NEW.head_commit_id AND ordinal=0 AND parent_id=OLD.head_commit_id
        ) THEN
            INSERT INTO vc_lineage_reachable(owner_id, library_id, normalized_ref, commit_id, received_at)
            SELECT NEW.owner_id, NEW.library_id, NEW.normalized_name, id, received_at
            FROM vc_commits WHERE id=NEW.head_commit_id
            ON CONFLICT DO NOTHING;
            IF EXISTS (SELECT 1 FROM vc_commit_parents WHERE commit_id=NEW.head_commit_id AND ordinal=1) THEN
                INSERT INTO vc_lineage_reachable(owner_id, library_id, normalized_ref, commit_id, received_at)
                WITH RECURSIVE incoming(id) AS (
                    SELECT parent_id FROM vc_commit_parents WHERE commit_id=NEW.head_commit_id AND ordinal=1
                    UNION
                    SELECT parent.parent_id FROM incoming i
                    JOIN vc_commit_parents parent ON parent.commit_id=i.id
                )
                SELECT NEW.owner_id, NEW.library_id, NEW.normalized_name, commit.id, commit.received_at
                FROM incoming i
                JOIN vc_commits commit ON commit.id=i.id AND commit.owner_id=NEW.owner_id AND commit.library_id=NEW.library_id
                ON CONFLICT DO NOTHING;
            END IF;
            RETURN NEW;
        END IF;
    END IF;
    PERFORM vc_rebuild_lineage_reachable(NEW.owner_id, NEW.library_id, NEW.normalized_name, NEW.head_commit_id);
    RETURN NEW;
END;
$$;

CREATE TRIGGER vc_refs_sync_lineage_reachable
AFTER INSERT OR UPDATE OF head_commit_id ON vc_refs
FOR EACH ROW EXECUTE FUNCTION vc_sync_lineage_reachable();

DO $$
DECLARE branch record;
BEGIN
    FOR branch IN SELECT owner_id, library_id, normalized_name, head_commit_id
                  FROM vc_refs WHERE kind='branch' AND head_commit_id IS NOT NULL FOR UPDATE LOOP
        PERFORM vc_rebuild_lineage_reachable(branch.owner_id, branch.library_id, branch.normalized_name, branch.head_commit_id);
    END LOOP;
END;
$$;
