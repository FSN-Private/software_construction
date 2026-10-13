-- Preserve the absence of historical creation evidence. PostgreSQL adds existing
-- rows as NULL before a default is installed for newly inserted holds.
ALTER TABLE lifecycle_holds ADD COLUMN IF NOT EXISTS created_at_utc timestamptz;
ALTER TABLE lifecycle_holds ALTER COLUMN created_at_utc SET DEFAULT now();
