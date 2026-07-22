-- masterdata-service — 0002 starter allergen catalog (phase-0b §0b.3).
-- Idempotent (ON CONFLICT DO NOTHING); versioned; drug-class allergens resolve against ATC for cross-check.
-- Drug-drug interactions are seeded at runtime after drugs load (they reference drug_id) — see loader README.

INSERT INTO masterdata.allergen (allergen_id, code, name, category, source_release) VALUES
  (gen_random_uuid(), 'ALG-PENICILLIN', 'Penicillins',                'Drug',          'seed-v1'),
  (gen_random_uuid(), 'ALG-SULFA',      'Sulfonamides',               'Drug',          'seed-v1'),
  (gen_random_uuid(), 'ALG-NSAID',      'NSAIDs',                     'Drug',          'seed-v1'),
  (gen_random_uuid(), 'ALG-ASPIRIN',    'Aspirin / Salicylates',     'Drug',          'seed-v1'),
  (gen_random_uuid(), 'ALG-CEPHALO',    'Cephalosporins',            'Drug',          'seed-v1'),
  (gen_random_uuid(), 'ALG-CODEINE',    'Codeine / Opiates',         'Drug',          'seed-v1'),
  (gen_random_uuid(), 'ALG-IODINE',     'Iodine / Contrast media',   'Drug',          'seed-v1'),
  (gen_random_uuid(), 'ALG-PEANUT',     'Peanuts',                   'Food',          'seed-v1'),
  (gen_random_uuid(), 'ALG-EGG',        'Egg',                       'Food',          'seed-v1'),
  (gen_random_uuid(), 'ALG-MILK',       'Milk / Dairy',              'Food',          'seed-v1'),
  (gen_random_uuid(), 'ALG-SHELLFISH',  'Shellfish',                 'Food',          'seed-v1'),
  (gen_random_uuid(), 'ALG-GLUTEN',     'Gluten / Wheat',            'Food',          'seed-v1'),
  (gen_random_uuid(), 'ALG-POLLEN',     'Pollen',                    'Environmental', 'seed-v1'),
  (gen_random_uuid(), 'ALG-DUST',       'Dust mites',                'Environmental', 'seed-v1'),
  (gen_random_uuid(), 'ALG-LATEX',      'Latex',                     'Environmental', 'seed-v1')
ON CONFLICT (code) DO NOTHING;
