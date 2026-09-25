-- Gallery rows are rebuildable projections. Source identity and XMP remain in 005/006.
CREATE UNIQUE INDEX gallery_owner_asset_identity ON assets(owner_id, id);

CREATE TABLE gallery_generations (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    branch_name text NOT NULL,
    generation bigint NOT NULL CHECK (generation > 0),
    schema_version integer NOT NULL CHECK (schema_version > 0),
    state text NOT NULL CHECK (state IN ('building', 'active', 'retired')),
    created_at timestamptz NOT NULL DEFAULT now(),
    activated_at timestamptz,
    PRIMARY KEY (owner_id, library_id, branch_name, generation)
);
CREATE UNIQUE INDEX gallery_one_active_generation
    ON gallery_generations(owner_id, library_id, branch_name) WHERE state='active';

CREATE TABLE gallery_projection (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    branch_name text NOT NULL,
    generation bigint NOT NULL,
    asset_id uuid NOT NULL,
    original_sha256 char(64) NOT NULL REFERENCES content_objects(sha256),
    xmp_sha256 char(64) REFERENCES content_objects(sha256),
    projection_state text NOT NULL CHECK (projection_state IN ('complete', 'stale', 'error')),
    parse_error_code text,
    display_path text NOT NULL,
    filename text NOT NULL,
    file_type text NOT NULL,
    imported_at timestamptz,
    captured_at timestamptz,
    capture_offset_minutes integer,
    camera text,
    lens text,
    focal_length_mm numeric(12,4),
    aperture numeric(12,4),
    shutter_seconds numeric(18,8),
    iso integer,
    width integer,
    height integer,
    orientation smallint,
    rating smallint,
    rejected boolean,
    title text,
    description text,
    creator text,
    has_location boolean NOT NULL DEFAULT false,
    latitude numeric(10,7),
    longitude numeric(10,7),
    refreshed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id, library_id, branch_name, generation, asset_id),
    FOREIGN KEY (owner_id, asset_id) REFERENCES assets(owner_id, id),
    FOREIGN KEY (owner_id, library_id, branch_name, generation)
        REFERENCES gallery_generations(owner_id, library_id, branch_name, generation) ON DELETE CASCADE,
    CHECK (width IS NULL OR width > 0),
    CHECK (height IS NULL OR height > 0),
    CHECK (iso IS NULL OR iso > 0),
    CHECK (orientation IS NULL OR orientation BETWEEN 1 AND 8),
    CHECK (rating IS NULL OR rating BETWEEN 0 AND 5),
    CHECK (latitude IS NULL OR latitude BETWEEN -90 AND 90),
    CHECK (longitude IS NULL OR longitude BETWEEN -180 AND 180),
    CHECK ((latitude IS NULL) = (longitude IS NULL)),
    CHECK (has_location = (latitude IS NOT NULL))
);
CREATE INDEX gallery_capture_sort ON gallery_projection(owner_id, library_id, branch_name, generation, captured_at, asset_id);
CREATE INDEX gallery_import_sort ON gallery_projection(owner_id, library_id, branch_name, generation, imported_at, asset_id);
CREATE INDEX gallery_filename_sort ON gallery_projection(owner_id, library_id, branch_name, generation, filename, asset_id);
CREATE INDEX gallery_rating_sort ON gallery_projection(owner_id, library_id, branch_name, generation, rating, asset_id);
CREATE INDEX gallery_dimensions_sort ON gallery_projection(owner_id, library_id, branch_name, generation, width, height, asset_id);

CREATE TABLE gallery_facet_values (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    branch_name text NOT NULL,
    generation bigint NOT NULL,
    asset_id uuid NOT NULL,
    facet text NOT NULL,
    ordinal integer NOT NULL DEFAULT 0 CHECK (ordinal >= 0),
    source text NOT NULL CHECK (source IN ('xmp', 'embedded', 'derived')),
    value_state text NOT NULL CHECK (value_state IN ('present', 'missing', 'cleared', 'invalid')),
    source_sha256 char(64) NOT NULL REFERENCES content_objects(sha256),
    text_value text,
    number_value numeric,
    date_value timestamptz,
    bool_value boolean,
    timestamp_basis text,
    PRIMARY KEY (owner_id, library_id, branch_name, generation, asset_id, facet, ordinal),
    FOREIGN KEY (owner_id, library_id, branch_name, generation, asset_id)
        REFERENCES gallery_projection(owner_id, library_id, branch_name, generation, asset_id) ON DELETE CASCADE,
    CHECK ((value_state = 'present' AND num_nonnulls(text_value, number_value, date_value, bool_value) = 1)
        OR (value_state <> 'present' AND num_nonnulls(text_value, number_value, date_value, bool_value) = 0))
);
CREATE INDEX gallery_facet_text ON gallery_facet_values(owner_id, library_id, branch_name, generation, facet, text_value, asset_id);
CREATE INDEX gallery_facet_number ON gallery_facet_values(owner_id, library_id, branch_name, generation, facet, number_value, asset_id);
CREATE INDEX gallery_facet_date ON gallery_facet_values(owner_id, library_id, branch_name, generation, facet, date_value, asset_id);
CREATE INDEX gallery_facet_bool ON gallery_facet_values(owner_id, library_id, branch_name, generation, facet, bool_value, asset_id);

CREATE TABLE gallery_refresh (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    branch_name text NOT NULL,
    generation bigint NOT NULL,
    state text NOT NULL CHECK (state IN ('queued', 'running', 'complete', 'failed')),
    last_asset_id uuid,
    total bigint NOT NULL DEFAULT 0 CHECK (total >= 0),
    completed bigint NOT NULL DEFAULT 0 CHECK (completed >= 0),
    failed bigint NOT NULL DEFAULT 0 CHECK (failed >= 0),
    last_error_code text,
    invalidation_revision bigint NOT NULL DEFAULT 0 CHECK (invalidation_revision >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id, library_id, branch_name, generation),
    FOREIGN KEY (owner_id, library_id, branch_name, generation)
        REFERENCES gallery_generations(owner_id, library_id, branch_name, generation) ON DELETE CASCADE,
    CHECK (completed + failed <= total)
);

CREATE TABLE gallery_preferences (
    owner_id text PRIMARY KEY,
    hide_geolocation boolean NOT NULL DEFAULT true,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE gallery_web_overlay_revisions (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    branch_name text NOT NULL,
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id, library_id, branch_name)
);

CREATE FUNCTION gallery_invalidate_asset(p_owner text, p_asset uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF p_asset IS NULL THEN RETURN; END IF;
    UPDATE gallery_projection SET projection_state='stale' WHERE owner_id=p_owner AND asset_id=p_asset AND projection_state='complete';
    UPDATE gallery_refresh SET state='queued',last_asset_id=NULL,invalidation_revision=invalidation_revision+1,updated_at=now()
        WHERE owner_id=p_owner;
END $$;

CREATE FUNCTION gallery_invalidate_owner_asset() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_owner text; v_asset uuid;
BEGIN
    IF TG_TABLE_NAME='assets' THEN
        v_owner := NEW.owner_id; v_asset := NEW.id;
    ELSIF TG_TABLE_NAME='asset_paths' OR TG_TABLE_NAME='upload_sessions' THEN
        v_owner := NEW.owner_id; v_asset := NEW.asset_id;
    ELSE
        v_asset := NEW.asset_id;
        SELECT owner_id INTO v_owner FROM assets WHERE id=v_asset;
    END IF;
    PERFORM gallery_invalidate_asset(v_owner,v_asset);
    RETURN NEW;
END $$;
CREATE TRIGGER gallery_assets_changed AFTER INSERT OR UPDATE OF original_sha256,state,lifecycle_version ON assets
    FOR EACH ROW EXECUTE FUNCTION gallery_invalidate_owner_asset();
CREATE TRIGGER gallery_paths_changed AFTER INSERT OR UPDATE OF display_path,active ON asset_paths
    FOR EACH ROW EXECUTE FUNCTION gallery_invalidate_owner_asset();
CREATE TRIGGER gallery_edits_changed AFTER INSERT OR UPDATE OF xmp_sha256,state ON edit_identities
    FOR EACH ROW EXECUTE FUNCTION gallery_invalidate_owner_asset();
CREATE TRIGGER gallery_imports_changed AFTER INSERT OR UPDATE OF verified_at,state ON upload_sessions
    FOR EACH ROW EXECUTE FUNCTION gallery_invalidate_owner_asset();

CREATE FUNCTION gallery_invalidate_branch_asset() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_owner text; v_library text; v_branch text; v_asset uuid;
BEGIN
    IF TG_OP='DELETE' THEN
        v_owner := OLD.owner_id; v_library := OLD.library_id; v_asset := OLD.asset_id;
        IF TG_TABLE_NAME='vc_materialized_state' THEN v_branch := OLD.normalized_ref;
        ELSE v_branch := OLD.branch_name; END IF;
    ELSE
        v_owner := NEW.owner_id; v_library := NEW.library_id; v_asset := NEW.asset_id;
        IF TG_TABLE_NAME='vc_materialized_state' THEN v_branch := NEW.normalized_ref;
        ELSE v_branch := NEW.branch_name; END IF;
    END IF;
    UPDATE gallery_projection SET projection_state='stale'
        WHERE owner_id=v_owner AND library_id=v_library AND branch_name=v_branch AND asset_id=v_asset AND projection_state='complete';
    UPDATE gallery_refresh SET state='queued',last_asset_id=NULL,invalidation_revision=invalidation_revision+1,updated_at=now()
        WHERE owner_id=v_owner AND library_id=v_library AND branch_name=v_branch;
    RETURN COALESCE(NEW,OLD);
END $$;
CREATE TRIGGER gallery_working_changed AFTER INSERT OR UPDATE OR DELETE ON vc_working_changes
    FOR EACH ROW EXECUTE FUNCTION gallery_invalidate_branch_asset();
CREATE TRIGGER gallery_committed_changed AFTER INSERT OR UPDATE OR DELETE ON vc_materialized_state
    FOR EACH ROW EXECUTE FUNCTION gallery_invalidate_branch_asset();
