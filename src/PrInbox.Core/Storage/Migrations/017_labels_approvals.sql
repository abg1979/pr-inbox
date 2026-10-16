-- Persist upstream PR labels and the number of currently effective approvals.
-- These are latest-observation fields, refreshed in-place when an enrichment
-- doesn't otherwise require a new snapshot.

ALTER TABLE pr_snapshots ADD COLUMN labels_json TEXT;
ALTER TABLE pr_snapshots ADD COLUMN approval_count INTEGER;
