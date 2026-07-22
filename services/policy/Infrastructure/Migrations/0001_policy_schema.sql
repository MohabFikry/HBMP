-- policy-service — 0001 policy/coverage schema (15-database-erd §5, 22-data-dictionary).
-- Cross-service beneficiary_id is a logical reference (value), not an enforced FK. consumed_value is
-- the authoritative usage accumulator (incremented by consume/dispense sagas in later phases).

CREATE SCHEMA IF NOT EXISTS policy;

CREATE TABLE IF NOT EXISTS policy.policy (
    policy_id      uuid PRIMARY KEY,
    policy_no      text NOT NULL UNIQUE,
    sponsor        text,
    effective_from date NOT NULL,
    effective_to   date,
    status         text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Suspended','Expired')),
    is_deleted     boolean NOT NULL DEFAULT false,
    row_version    int NOT NULL DEFAULT 0,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS policy.benefit_category (
    benefit_category_id uuid PRIMARY KEY,
    code text NOT NULL UNIQUE CHECK (code IN ('LAB','IMAGING','PHARMACY','CONSULT','REFERRAL')),
    name text NOT NULL
);

CREATE TABLE IF NOT EXISTS policy.coverage (
    coverage_id         uuid PRIMARY KEY,
    policy_id           uuid NOT NULL REFERENCES policy.policy(policy_id),
    beneficiary_id      uuid NOT NULL,
    benefit_category_id uuid NOT NULL REFERENCES policy.benefit_category(benefit_category_id),
    effective_from      date NOT NULL,
    effective_to        date,
    status              text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Suspended','Expired')),
    is_deleted          boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_coverage_beneficiary ON policy.coverage (beneficiary_id);

CREATE TABLE IF NOT EXISTS policy.coverage_limit (
    coverage_limit_id uuid PRIMARY KEY,
    coverage_id       uuid NOT NULL REFERENCES policy.coverage(coverage_id),
    limit_type        text NOT NULL CHECK (limit_type IN ('Annual','PerEncounter','Lifetime','Count')),
    limit_value       numeric(14,3) NOT NULL,
    consumed_value    numeric(14,3) NOT NULL DEFAULT 0 CHECK (consumed_value >= 0),
    currency_code     char(3) NOT NULL DEFAULT 'EGP',
    reset_period      text NOT NULL DEFAULT 'None' CHECK (reset_period IN ('None','Monthly','Quarterly','Yearly')),
    last_reset_on     date
);

CREATE TABLE IF NOT EXISTS policy.coverage_limit_history (
    history_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    coverage_limit_id uuid NOT NULL,
    operation         text NOT NULL,
    row_snapshot      jsonb NOT NULL,
    changed_at        timestamptz NOT NULL DEFAULT now()
);
CREATE OR REPLACE FUNCTION policy.write_limit_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO policy.coverage_limit_history (coverage_limit_id, operation, row_snapshot)
    VALUES (NEW.coverage_limit_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_limit_history ON policy.coverage_limit;
CREATE TRIGGER trg_limit_history AFTER INSERT OR UPDATE ON policy.coverage_limit
    FOR EACH ROW EXECUTE FUNCTION policy.write_limit_history();

INSERT INTO policy.benefit_category (benefit_category_id, code, name) VALUES
  (gen_random_uuid(),'LAB','Laboratory'),
  (gen_random_uuid(),'IMAGING','Imaging'),
  (gen_random_uuid(),'PHARMACY','Pharmacy'),
  (gen_random_uuid(),'CONSULT','Consultation'),
  (gen_random_uuid(),'REFERRAL','Referral')
ON CONFLICT (code) DO NOTHING;
