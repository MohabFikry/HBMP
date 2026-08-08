-- masterdata-service — 0011 the ICD-10 hierarchy (44-clinical-validation-hardening §6, phase 28 Gate 7).
-- Idempotent.
--
-- ============================================================================================================
-- THE HIERARCHY WAS ALWAYS IN THE FILE. THE LOADER READ IT AND THREW IT AWAY.
-- ============================================================================================================
-- `Raw Files/ICD10_2019_full.csv` has nine columns: Code, Description, Type, Parent_Code, Parent_Description,
-- Chapter_Code, Chapter_Description, Block_Code, Block_Description. `IcdCsvRow` binds FOUR of them, and uses
-- `Type` for one thing only — setting is_billable = false on chapters and blocks. The parent relationship
-- was read on every load and discarded on every load.
--
-- What that cost: the indication check truncates a diagnosis to three characters and compares. That works
-- for the common case and breaks in three ways. A BLOCK-level indication ("J00-J06", acute upper respiratory
-- infections) cannot be expressed by truncation at all. An indication more specific than three characters is
-- silently widened to its whole category. And a diagnosis LESS specific than the indication reads as a
-- mismatch when it is really an open question.
--
-- With the parent chain loaded the rule becomes a real one: a drug indication at node L matches an encounter
-- diagnosis D when D is a DESCENDANT-OR-SELF of L.

ALTER TABLE masterdata.icd_code
    -- The immediate parent: a subcategory's category, a category's block, a block's chapter.
    ADD COLUMN IF NOT EXISTS parent_code text,
    ADD COLUMN IF NOT EXISTS node_kind   text
        CHECK (node_kind IN ('Chapter','Block','Category','Subcategory'));

CREATE INDEX IF NOT EXISTS ix_icd_parent ON masterdata.icd_code (parent_code);
CREATE INDEX IF NOT EXISTS ix_icd_node_kind ON masterdata.icd_code (node_kind);

COMMENT ON COLUMN masterdata.icd_code.parent_code IS
    'Immediate parent in the ICD-10 tree, from the source file''s Parent_Code column. Populated by the '
    'loader; NULL on a chapter and on rows loaded before phase 28.';

-- ------------------------------------------------------------------------------------------------------
-- The closure. A materialised ancestor list rather than a recursive walk at query time.
-- ------------------------------------------------------------------------------------------------------
-- The indication check runs on every keystroke-triggered validation, against every diagnosis on the
-- encounter and every indication of every prescribed drug. A recursive CTE per comparison would put a tree
-- walk inside a loop inside a consultation. The tree changes once a year, when the catalogue is reloaded;
-- the query runs thousands of times a day. Materialise it.
CREATE TABLE IF NOT EXISTS masterdata.icd_ancestor (
    code          text NOT NULL,
    ancestor_code text NOT NULL,
    -- 1 = parent, 2 = grandparent. Lets a caller ask for "the category" without knowing the tree's shape.
    depth         int  NOT NULL,
    PRIMARY KEY (code, ancestor_code)
);

-- The lookup the engine makes: "everything above this diagnosis".
CREATE INDEX IF NOT EXISTS ix_icd_ancestor_code ON masterdata.icd_ancestor (code);
-- The reverse: "every code underneath this indication node", for the alternatives list.
CREATE INDEX IF NOT EXISTS ix_icd_ancestor_ancestor ON masterdata.icd_ancestor (ancestor_code);

COMMENT ON TABLE masterdata.icd_ancestor IS
    'Transitive closure of icd_code.parent_code — every (code, ancestor) pair with its depth. Rebuilt by '
    'the loader after each catalogue load; a descendant-or-self test is one indexed read against it.';

-- ------------------------------------------------------------------------------------------------------
-- Rebuild the closure from whatever parent_code currently says. Idempotent and safe to re-run.
-- ------------------------------------------------------------------------------------------------------
-- Written as a function so the loader can call it after an upsert without shipping the recursive SQL twice.
-- The depth guard is not decoration: a malformed source row pointing a code at its own descendant would
-- otherwise spin here, and a catalogue load is not a place to discover that.
CREATE OR REPLACE FUNCTION masterdata.rebuild_icd_ancestors() RETURNS void AS $$
BEGIN
    DELETE FROM masterdata.icd_ancestor;

    INSERT INTO masterdata.icd_ancestor (code, ancestor_code, depth)
    WITH RECURSIVE walk AS (
        SELECT c.code, c.parent_code AS ancestor_code, 1 AS depth
        FROM masterdata.icd_code c
        WHERE c.parent_code IS NOT NULL AND c.parent_code <> c.code

        UNION ALL

        SELECT w.code, p.parent_code, w.depth + 1
        FROM walk w
        JOIN masterdata.icd_code p ON p.code = w.ancestor_code
        WHERE p.parent_code IS NOT NULL
          AND p.parent_code <> p.code
          -- ICD-10 is four levels deep (chapter → block → category → subcategory). Anything beyond that is
          -- a cycle in the source, and the load should end rather than hang.
          AND w.depth < 8
    )
    SELECT DISTINCT code, ancestor_code, MIN(depth)
    FROM walk
    WHERE ancestor_code IS NOT NULL
    GROUP BY code, ancestor_code;
END;
$$ LANGUAGE plpgsql;

SELECT masterdata.rebuild_icd_ancestors();
