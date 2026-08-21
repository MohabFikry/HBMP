-- eligibility-service — 0007: carry the CARD NUMBER on the member projection (33.9b).
--
-- WHY. A member number and a card number are two different identifiers. `member_no` is the enrolment key
-- (MRS-M-…) that policy-service issues; `card_number` is the number PRINTED ON THE CARD the beneficiary
-- carries, owned by patient-service, normalized there by PersonFieldValidation.NormalizeCardNumber, and
-- unique per beneficiary. Reception is handed the card.
--
-- The reception search and the 33.9 verified lookup matched member_no, national_id, passport, refugee_id and
-- unhcr_no — every identifier except the one on the object in the operator's hand. A desk typing the card
-- number found nobody, and the fallback is to search by name, which is what 33.9 exists to stop.
--
-- NOTHING NEW CROSSES THE WIRE. `BeneficiaryRegistered` has carried `cardNumber` since the intake path was
-- written — patient-service publishes it from both registration entry points — and ProjectionUpdater read
-- every other field of that event and dropped this one. The same shape as the rest of this phase: a value
-- published, delivered, and never read.
--
-- Expand-only and idempotent: a nullable column, no default, no constraint. NULL is correct for every row
-- written before this migration and stays correct until that beneficiary's next BeneficiaryRegistered /
-- BeneficiaryUpdated — a member whose card is not yet projected is found by member number exactly as before,
-- so nobody becomes unreachable. It is deliberately NOT backfilled here: the value lives in patient-service,
-- a cross-schema read would couple two services' storage, and the projection's whole contract is that it is
-- fed by events.

ALTER TABLE eligibility.member_projection
    ADD COLUMN IF NOT EXISTS card_number text;

CREATE INDEX IF NOT EXISTS ix_member_projection_card_number
    ON eligibility.member_projection (card_number);

COMMENT ON COLUMN eligibility.member_projection.card_number IS
    'The number printed on the beneficiary''s card (patient.beneficiary.card_number), via '
    'BeneficiaryRegistered/BeneficiaryUpdated. DISTINCT from member_no, which is the enrolment key: a desk '
    'is handed the card, so both must resolve a member. NULL until that beneficiary''s next projection '
    'event; such a member is still found by member number.';
