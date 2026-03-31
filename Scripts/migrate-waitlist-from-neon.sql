-- One-time migration: Neon waitlist_entries → Railway waitlist_entries
-- Run this AFTER the AddWaitlistEntries migration has been applied to Railway.
-- Execute via: railway connect postgres < migrate-waitlist-from-neon.sql
-- Or paste into Railway's database query console.

INSERT INTO waitlist_entries (email, created_at) VALUES
  ('pabloguerrero@locallist.ai', '2026-02-07T12:34:36.718Z'),
  ('candelaalvarez3@gmail.com', '2026-02-07T12:35:44.028Z'),
  ('nitkine18@gmail.com', '2026-02-07T12:56:08.899Z'),
  ('jackgreenwald34@gmail.com', '2026-02-07T13:28:06.479Z'),
  ('nicolecasaletto247@gmail.com', '2026-02-07T13:58:44.205Z')
ON CONFLICT (email) DO NOTHING;

-- Verify: should return 5
SELECT COUNT(*) as total FROM waitlist_entries;
