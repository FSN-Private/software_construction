-- Connect accepted tag ref movements to their exact audit event without logging private tag names.
ALTER TABLE vc_reflog ADD COLUMN audit_event_id uuid REFERENCES audit_events(id);
CREATE UNIQUE INDEX vc_reflog_audit_event ON vc_reflog(audit_event_id) WHERE audit_event_id IS NOT NULL;
