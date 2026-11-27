ALTER TABLE pr_snapshots ADD COLUMN own_approval_json TEXT NULL;

-- Re-enrich existing rows once so personal approvals appear without a new push.
UPDATE pull_requests SET dossier_version = 0;
