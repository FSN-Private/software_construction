CREATE TABLE windows_sync_owner_revisions (
    owner_id text PRIMARY KEY,
    revision bigint NOT NULL DEFAULT 1 CHECK(revision > 0)
);

CREATE FUNCTION windows_sync_bump_owner_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    affected_owner text;
    affected_asset uuid;
BEGIN
    IF TG_TABLE_NAME = 'edit_identities' THEN
        affected_asset := CASE WHEN TG_OP = 'DELETE' THEN OLD.asset_id ELSE NEW.asset_id END;
        SELECT owner_id INTO affected_owner FROM assets WHERE id=affected_asset;
    ELSE
        affected_owner := CASE WHEN TG_OP = 'DELETE' THEN OLD.owner_id ELSE NEW.owner_id END;
    END IF;
    IF affected_owner IS NOT NULL THEN
        INSERT INTO windows_sync_owner_revisions(owner_id) VALUES(affected_owner)
        ON CONFLICT(owner_id) DO UPDATE SET revision=windows_sync_owner_revisions.revision+1;
    END IF;
    RETURN NULL;
END $$;

CREATE TRIGGER windows_sync_assets_revision AFTER INSERT OR UPDATE OR DELETE ON assets FOR EACH ROW EXECUTE FUNCTION windows_sync_bump_owner_revision();
CREATE TRIGGER windows_sync_edits_revision AFTER INSERT OR UPDATE OR DELETE ON edit_identities FOR EACH ROW EXECUTE FUNCTION windows_sync_bump_owner_revision();
CREATE TRIGGER windows_sync_paths_revision AFTER INSERT OR UPDATE OR DELETE ON asset_paths FOR EACH ROW EXECUTE FUNCTION windows_sync_bump_owner_revision();

INSERT INTO windows_sync_owner_revisions(owner_id)
SELECT owner_id FROM assets
UNION
SELECT owner_id FROM asset_paths
ON CONFLICT(owner_id) DO NOTHING;
