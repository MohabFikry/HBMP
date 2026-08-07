-- masterdata-service — 0008 a list price for an examination (ADR-0034). ADDITIVE, nullable, unseeded.
--
-- WHY THE COLUMN EXISTS BEFORE ANY DATA DOES.
--
-- The dispensing counter can tell a beneficiary what a prescription costs because `drug.price_egp` exists.
-- The lab and imaging benches cannot tell them anything, because nothing in the platform records what an
-- examination costs — not `examination_type`, not `cpt_code`, not a fee schedule anywhere. Their counter
-- surface is otherwise identical to the pharmacy's, so this is the one fact standing between a technician
-- and the same three figures.
--
-- NULLABLE, AND NULL IS NOT ZERO. Every consumer of this column must treat NULL as "we do not know what this
-- costs" and refuse to quote, exactly as the drug price endpoint already does. Zero at a counter reads as
-- "free", and a refugee family told their scan is free either receives a bill later or declines something
-- they could have afforded.
--
-- NOT SEEDED. Inventing prices to make a screen look finished would put fabricated money in front of
-- patients. Every tile reads "cannot be quoted" with a stated reason until a real tariff is loaded, which is
-- the honest state and the same one pharmacy's member/payer split is in today.

ALTER TABLE masterdata.examination_type
    ADD COLUMN IF NOT EXISTS price_egp numeric(12,2);

COMMENT ON COLUMN masterdata.examination_type.price_egp IS
    'List price in EGP, or NULL when unknown. NULL is NOT zero: a consumer that cannot establish a price '
    'must refuse to quote rather than show 0.00, which at a counter reads as "free" (ADR-0034).';
