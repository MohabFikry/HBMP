-- ============================================================================================================
-- seed-positions.sql — a JOB TITLE for every demo login (28.13).
--
--   psql -h localhost -p 55432 -U hbmp -d hbmp -v ON_ERROR_STOP=1 -f tools/dev/seed-positions.sql
--
-- A position is what the organisation calls the job. It is NOT a role: a role is drawn from a frozen
-- vocabulary and decides what the platform permits; this is free text and decides nothing. The two are shown
-- side by side in Users & Access precisely so the difference is visible.
--
-- The titles below deliberately do NOT paraphrase the role they sit on. `reception` is a "Front Desk
-- Officer", not a "Reception"; `org_admin` is an "Operations Director", not an "Org Admin". A seed where
-- every title restated its role would make a screen that mistakenly rendered the ROLE in this column look
-- correct, which is the one failure this column can have.
--
-- Idempotent, and it does not overwrite a title somebody has already set by hand: `WHERE position IS NULL`.
-- Re-running after editing one in the UI leaves the edit alone.
-- ============================================================================================================

\set ON_ERROR_STOP on

DO $guard$
BEGIN
    IF current_database() <> 'hbmp' THEN
        RAISE EXCEPTION 'seed-positions.sql is written for the local dev DB (hbmp), not "%".', current_database();
    END IF;
END
$guard$;

UPDATE identity."user" u
   SET position = t.title
  FROM (VALUES
        ('org_admin',                    'Operations Director'),
        ('super_admin',                  'Platform Administrator'),
        ('reception',                    'Front Desk Officer'),
        ('doctor',                       'Consultant Physician'),
        ('nurse',                        'Staff Nurse'),
        ('pharmacist',                   'Senior Pharmacist'),
        ('lab_tech',                     'Laboratory Technologist'),
        ('imaging_tech',                 'Radiographer'),
        ('radiology_tech',               'Radiographer'),
        ('medical_approval',             'Medical Review Officer'),
        ('medical_director',             'Medical Director'),
        ('beneficiary_mgmt',             'Registration Officer'),
        ('beneficiary_mgmt_supervisor',  'Registration Supervisor'),
        ('branch_coordinator',           'Clinic Coordinator'),
        ('clinics_manager',              'Regional Clinics Manager'),
        ('call_center',                  'Contact Centre Agent'),
        ('case_manager',                 'Case Management Lead'),
        ('claims_officer',               'Claims Officer'),
        ('finance',                      'Finance Officer'),
        ('policy_admin',                 'Benefits Product Manager'),
        ('provider_admin',               'Network Operations Manager'),
        ('network_team',                 'Provider Network Analyst'),
        ('procedure_provider',           'Procedure Centre Coordinator')
       ) AS t(login, title)
 WHERE u.user_name = t.login
   AND u.position IS NULL;

SELECT user_name, position
  FROM identity."user"
 WHERE position IS NOT NULL
 ORDER BY user_name;
