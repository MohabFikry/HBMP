-- 2026-08-09 audit §2.3 — say where a settlement line's price came from.
--
-- THE DEFECT. A service code the provider's contract does not price was settled at the provider's own
-- OBSERVED AVERAGE unit cost for the period, and nothing on the line said so. Two things are wrong with
-- that. The platform's stated rule everywhere else — claims' Cap(), reimbursement's Min(tariff, receipt) —
-- is that the absence of a tariff is not permission to pay anything; and an average is the statistic most
-- easily moved by the thing you least want to trust, so one mispriced small delivery lifts the rate for
-- every unit of that code in the period.
--
-- The fallback is now the LOWEST observed unit cost, and this column records that it was a fallback. The
-- second half matters as much as the first: a settlement is a Draft that a human issues, and "which of
-- these lines has no agreed price behind it" is the question they need answered before they do.
--
-- EXPAND-ONLY. A nullable column with a default; existing rows are backfilled to 'Contract', which is what
-- they were: every settlement generated before this change either found a contract price or fell back
-- silently, and re-deriving which is not possible from the row. 'Contract' is the reading that does not
-- invent a warning about historic lines nobody can re-check.

ALTER TABLE finance.settlement_line
    ADD COLUMN IF NOT EXISTS price_source text NOT NULL DEFAULT 'Contract';

-- migrate-compat: contract-ok (drops a constraint THIS migration also adds, so the pair is idempotent and
-- there is no window in which a deployed reader sees the column without its check)
ALTER TABLE finance.settlement_line DROP CONSTRAINT IF EXISTS ck_settlement_line_price_source;
ALTER TABLE finance.settlement_line
    ADD CONSTRAINT ck_settlement_line_price_source
    CHECK (price_source IN ('Contract', 'ObservedFloor'));

COMMENT ON COLUMN finance.settlement_line.price_source IS
    'Contract = the provider''s agreed price book named this code. ObservedFloor = it did not, and the line '
    'is priced at the LOWEST unit cost observed for the code in the period — a floor, pending a tariff. '
    'Never the observed average: see 0004''s header.';
