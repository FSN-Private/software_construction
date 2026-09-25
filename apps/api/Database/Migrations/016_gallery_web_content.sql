-- Verified web-originated sidecars are bound to their owner and edit identity.
CREATE TABLE gallery_web_content (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    branch_name text NOT NULL,
    asset_id uuid NOT NULL,
    edit_id uuid NOT NULL REFERENCES edit_identities(id),
    sha256 char(64) NOT NULL REFERENCES content_objects(sha256),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id,library_id,branch_name,asset_id,edit_id,sha256),
    FOREIGN KEY (owner_id,asset_id) REFERENCES assets(owner_id,id)
);
CREATE INDEX gallery_web_content_edit ON gallery_web_content(owner_id,asset_id,edit_id,sha256);

CREATE FUNCTION gallery_bump_web_overlay() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_owner text; v_library text; v_branch text;
BEGIN
    IF TG_OP='DELETE' THEN
        IF OLD.client_id<>'__gallery_web__' THEN RETURN OLD; END IF;
        v_owner := OLD.owner_id; v_library := OLD.library_id; v_branch := OLD.branch_name;
    ELSE
        IF NEW.client_id<>'__gallery_web__' THEN RETURN NEW; END IF;
        v_owner := NEW.owner_id; v_library := NEW.library_id; v_branch := NEW.branch_name;
    END IF;
    INSERT INTO gallery_web_overlay_revisions(owner_id,library_id,branch_name,revision)
    VALUES(v_owner,v_library,v_branch,1)
    ON CONFLICT(owner_id,library_id,branch_name) DO UPDATE
      SET revision=gallery_web_overlay_revisions.revision+1,updated_at=now();
    RETURN COALESCE(NEW,OLD);
END $$;
CREATE TRIGGER gallery_web_overlay_changed AFTER INSERT OR UPDATE OR DELETE ON vc_working_changes
    FOR EACH ROW EXECUTE FUNCTION gallery_bump_web_overlay();
