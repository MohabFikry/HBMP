-- ============================================================================================================
-- 0017 — blood pressure gets its diastolic half.
-- ============================================================================================================
-- `vital_type` has allowed one 'BP' row since 0005, and the service comment beside it said the diastolic
-- "rides in Unit/notes". It never did: `unit` is a 10-character varchar the service overwrites with the
-- canonical 'mmHg', and nothing ever wrote a second number anywhere. So every blood pressure this platform
-- has recorded is a systolic with no partner.
--
-- A lone systolic is not a blood pressure. 118 is unremarkable over 76 and a hypertensive emergency over 118,
-- and the vitals panel a doctor reads before prescribing cannot tell those apart from what we stored. This is
-- the one vital in the set whose clinical meaning is a PAIR, and it was the one stored as a scalar.
--
-- The diastolic is its own row rather than a second column on `vital`: every other type here is one row, one
-- number, one LOINC code, and a `value_num_2` that is null for six of the seven types would put the exception
-- in the shape of the table instead of in the pair that actually has it.

ALTER TABLE emr.vital DROP CONSTRAINT IF EXISTS vital_vital_type_check;  -- migrate-compat: contract-ok (re-added immediately below with 'BPd' added to the allowed set — a WIDENING, so no row valid before is invalid after)
ALTER TABLE emr.vital ADD CONSTRAINT vital_vital_type_check
    CHECK (vital_type IN ('BP','BPDiastolic','HR','Temp','SpO2','Weight','Height','BMI'));

COMMENT ON COLUMN emr.vital.vital_type IS
    'Vital observation type. BP is the SYSTOLIC value and BPDiastolic its partner: a blood pressure is two '
    'rows on the same encounter, read as a pair. Existing BP rows keep no partner — they were recorded '
    'before one could be stored, and inventing a diastolic for them would be fabricating a clinical value.';
