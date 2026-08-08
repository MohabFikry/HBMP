-- masterdata-service — 0007 drug typeahead support (phase 26.2, doc 43 §6).
--
-- The prescribing combobox searches ONE field across trade name, active ingredient and Arabic name, and it
-- runs on every keystroke past the second. Against 31,651 drugs an ILIKE '%q%' is a sequential scan per
-- keystroke; a typeahead that table-scans is a typeahead nobody uses, and the fallback is the hard-coded
-- drug the modal ships with today.

CREATE EXTENSION IF NOT EXISTS pg_trgm;    -- trigram GIN indexes for infix matching
CREATE EXTENSION IF NOT EXISTS unaccent;   -- "Céfalexin" must be found by typing "cefalexin"

-- The single normalisation applied to BOTH the stored text and the query. Anything else is a bug factory:
-- the index is built on this expression, so a query normalised differently silently stops using it.
--
-- IMMUTABLE is what makes it indexable. unaccent() itself is only STABLE because a dictionary can be
-- reloaded; passing the dictionary explicitly is the documented idiom for pinning it, at the cost of a
-- promise we are now making — if the unaccent dictionary is ever changed, these indexes must be REINDEXed.
CREATE OR REPLACE FUNCTION masterdata.search_key(input text)
RETURNS text
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $$
    SELECT lower(
        translate(
            -- Strip Arabic short vowels (tashkeel, U+064B–U+0652) and the tatweel elongation character.
            -- They are optional in written Arabic, so a name stored with them must still match a query
            -- typed without them — the Arabic equivalent of the accent problem unaccent() solves.
            regexp_replace(public.unaccent('public.unaccent', input), '[ً-ْـ]', '', 'g'),
            -- Orthographic variants users do not distinguish when typing: the four alef forms, alef maqsura
            -- for yeh, and teh marbuta for heh.
            'أإآٱىة',
            'اااايه'
        )
    )
$$;

COMMENT ON FUNCTION masterdata.search_key(text) IS
    'Case-, accent- and tashkeel-insensitive search key. Used by the drug typeahead on both the indexed '
    'column expression and the incoming query — they must normalise identically or the index goes unused.';

-- Trigram GIN, not btree: the combobox matches mid-string ("clav" must find "amoxicillin + clavulanic acid"),
-- which no btree prefix index can serve.
CREATE INDEX IF NOT EXISTS ix_drug_search_name
    ON masterdata.drug USING gin (masterdata.search_key(name) gin_trgm_ops);
CREATE INDEX IF NOT EXISTS ix_drug_search_scientific
    ON masterdata.drug USING gin (masterdata.search_key(scientific_name) gin_trgm_ops);
CREATE INDEX IF NOT EXISTS ix_drug_search_name_ar
    ON masterdata.drug USING gin (masterdata.search_key(name_ar) gin_trgm_ops);

-- The prescribing search serves the CURRENT market list only (drugs carrying a source_row_id), so this
-- partial index is the one the typeahead actually hits. Legacy rows loaded from the earlier CSV stay
-- readable by drug_id and drug_code — historical prescriptions point at them — but a prescriber must not be
-- offered two entries for one product where only one carries indication data.
CREATE INDEX IF NOT EXISTS ix_drug_current_market
    ON masterdata.drug (name) WHERE source_row_id IS NOT NULL;
