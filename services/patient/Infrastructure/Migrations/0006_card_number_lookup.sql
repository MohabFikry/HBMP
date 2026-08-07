-- patient-service — 0006 card-number lookup (phase 26.6, doc 43 §7).
--
-- The column has existed since 0004 and is unique among live rows, but nothing could SEARCH it: the
-- beneficiary search only queried the `identifier` child table, and IdentifierType had no CardNumber member.
-- So "find the patient by the number on their card" — how a pharmacy counter actually works — could not be
-- expressed, and the endpoint the pharmacy called to do it did not exist at all.
--
-- The existing unique index is on the raw column, which a case-insensitive lookup cannot use. Resolution
-- normalises the card number (strip a decorative '#', drop spaces, upper-case) so that "#A-1234", "a 1234"
-- and "A1234" are one card rather than three misses; this index is on the same normalised expression, so
-- the lookup is an index hit rather than a scan of the beneficiary table.
CREATE INDEX IF NOT EXISTS ix_beneficiary_card_number_upper
    ON patient.beneficiary (upper(card_number))
    WHERE is_deleted = false AND card_number IS NOT NULL;

-- Member number is the other half of the pharmacy's two-identifier lookup, and was equally unindexed.
CREATE INDEX IF NOT EXISTS ix_beneficiary_member_no_upper
    ON patient.beneficiary (upper(member_no))
    WHERE is_deleted = false AND member_no IS NOT NULL;

COMMENT ON COLUMN patient.beneficiary.card_number IS
    'The number printed on the physical card. A LOOKUP KEY, never an authenticator: the card is shared, '
    'photographed and reused, so resolution requires a second identifier (doc 43 §7, D5).';
