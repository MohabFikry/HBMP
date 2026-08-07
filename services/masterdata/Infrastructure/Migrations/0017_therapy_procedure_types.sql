-- masterdata-service — 0017: Occupational Therapy and Speech Therapy (phase 30 Gate 6, design 46 §8).
--
-- ============================================================================================================
-- THIS FILE IS THE WHOLE CHANGE, AND THAT IS THE POINT
-- ============================================================================================================
-- Design 45 §2 made `is_session_based` DATA rather than code precisely so that adding a session-based therapy
-- would be an INSERT: "the composer reveals a 'number of sessions' field when the SELECTED TYPE carries this
-- flag. It must never be `if (type === 'Physiotherapy')`."
--
-- Design 46 §8 then asks for exactly that: two more types, "both is_session_based = true — the same shape as
-- physiotherapy, which is exactly why that flag was made data rather than code."
--
-- So this migration is the test of that decision. If a line of C# or TypeScript had to change alongside it,
-- the flag was not implemented as designed and THAT would be the bug to fix — the prompt says so in as many
-- words. `ProcedureTypeIsDataNotCodeTests` asserts the absence: no source file anywhere names these two
-- types, and none names Physiotherapy in a conditional either.
--
-- Both mirror Physiotherapy's shape: Medicine-section CPT codes, a sensible default course, and a ceiling
-- that a partial approval can narrow but a prescriber cannot exceed.
--
-- Additive + idempotent.

INSERT INTO masterdata.procedure_type
    (code, name_en, name_ar, is_session_based, default_sessions, max_sessions, allowed_cpt_scopes, sort_order)
VALUES
    ('OccupationalTherapy', 'Occupational Therapy', 'العلاج الوظيفي', true,  8, 40, '["Medicine"]', 12),
    ('SpeechTherapy',       'Speech Therapy',       'علاج النطق',     true, 10, 48, '["Medicine"]', 14)
ON CONFLICT (code) DO NOTHING;
