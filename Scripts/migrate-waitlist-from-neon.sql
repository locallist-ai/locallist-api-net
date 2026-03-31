-- One-time migration: Neon waitlist + survey emails → Railway waitlist_entries
-- Run this AFTER the AddWaitlistEntries migration has been applied to Railway.
-- Execute via: railway connect postgres < migrate-waitlist-from-neon.sql
-- Or paste into Railway's database query console.

-- Source 1: Neon waitlist (5 entries, Feb 2026)
INSERT INTO waitlist_entries (email, created_at) VALUES
  ('pabloguerrero@locallist.ai', '2026-02-07T12:34:36.718Z'),
  ('candelaalvarez3@gmail.com', '2026-02-07T12:35:44.028Z'),
  ('nitkine18@gmail.com', '2026-02-07T12:56:08.899Z'),
  ('jackgreenwald34@gmail.com', '2026-02-07T13:28:06.479Z'),
  ('nicolecasaletto247@gmail.com', '2026-02-07T13:58:44.205Z')
ON CONFLICT (email) DO NOTHING;

-- Source 2: MVP Survey valid emails (36 entries, Dec 2025)
-- These users filled out the initial validation survey and provided their email.
INSERT INTO waitlist_entries (email, created_at) VALUES
  ('juliandan24@gmail.com', '2025-12-17T23:21:43.818Z'),
  ('andresgancedo31@gmail.com', '2025-12-18T00:55:52.504Z'),
  ('jmontes.sneakz@gmail.com', '2025-12-18T01:09:35.670Z'),
  ('knarfst_yl3@hotmail.com', '2025-12-18T01:27:50.754Z'),
  ('kjosmile@me.com', '2025-12-18T03:49:03.111Z'),
  ('mikeyfernndz@gmail.com', '2025-12-18T22:36:28.165Z'),
  ('cameron.beard08@gmail.com', '2025-12-18T22:51:11.196Z'),
  ('caseymcc@live.com', '2025-12-19T05:00:01.631Z'),
  ('emily.tso24@gmail.com', '2025-12-19T16:15:35.004Z'),
  ('perikayla@gmail.com', '2025-12-20T00:59:43.824Z'),
  ('cpardo525@yahoo.com', '2025-12-21T06:08:52.226Z'),
  ('joshrod9@yahoo.com', '2025-12-21T19:10:02.567Z'),
  ('jackgreenwald34@gmail.com', '2025-12-21T19:13:21.169Z'),
  ('shelbyham27@aol.com', '2025-12-21T19:17:14.398Z'),
  ('thetymking@gmail.com', '2025-12-21T19:22:56.594Z'),
  ('domflorino65@gmail.com', '2025-12-21T19:25:40.514Z'),
  ('megeverhard@gmail.com', '2025-12-21T19:33:07.108Z'),
  ('taliaflorino99@gmail.com', '2025-12-21T19:36:33.892Z'),
  ('lilianaflorino13@gmail.com', '2025-12-21T19:36:57.494Z'),
  ('maia.whelan98@gmail.com', '2025-12-21T19:48:33.003Z'),
  ('deja.lighty1@gmail.com', '2025-12-21T19:48:40.476Z'),
  ('aeris.feinberg@hirequestdirect.com', '2025-12-21T20:00:51.652Z'),
  ('lflorinoa@gmail.com', '2025-12-21T20:15:02.565Z'),
  ('pompeotj@gmail.com', '2025-12-21T20:33:14.627Z'),
  ('cfornaris19@gmail.com', '2025-12-21T20:42:03.352Z'),
  ('lindseyburroughs21@gmail.com', '2025-12-21T20:48:30.493Z'),
  ('maxaweinberg@gmail.com', '2025-12-21T21:06:05.672Z'),
  ('bryan@revenue-project.com', '2025-12-21T21:28:10.647Z'),
  ('anaharrington97@gmail.com', '2025-12-21T22:01:43.924Z'),
  ('ashley.joos1@gmail.com', '2025-12-21T22:37:07.663Z'),
  ('nikkisaecker@live.com', '2025-12-22T21:49:58.897Z'),
  ('karlifeinstein@gmail.com', '2025-12-23T01:23:53.281Z'),
  ('gomezrr15@gmail.com', '2025-12-23T01:36:25.660Z'),
  ('alyshamercado123@gmail.com', '2025-12-30T18:11:16.098Z'),
  ('jamieson.creative@gmail.com', '2025-12-30T18:56:47.673Z'),
  ('holdplease12345@gmail.com', '2025-12-31T16:21:24.934Z')
ON CONFLICT (email) DO NOTHING;

-- Verify: should return 40 (5 Neon + 36 survey - 1 duplicate = 40 unique)
SELECT COUNT(*) as total FROM waitlist_entries;
