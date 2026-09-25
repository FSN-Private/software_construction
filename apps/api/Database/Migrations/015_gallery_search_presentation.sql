-- Keep a display form alongside the normalized search key without rewriting 014.
ALTER TABLE gallery_facet_values ADD COLUMN display_text text;
CREATE INDEX render_gallery_thumbnail ON render_requests(owner_id,library_id,asset_id,edit_id,rendition,created_at DESC);
CREATE INDEX gallery_location_filter ON gallery_projection(owner_id,library_id,branch_name,generation,latitude,longitude,asset_id) WHERE has_location;
