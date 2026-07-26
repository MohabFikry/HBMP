-- Mersal HBMP — initial databases (Tier 1 starter)
-- Runs once on first Postgres boot. A shared app DB to start (identity lives in the `identity` schema of
-- the hbmp DB — Phase 17 retired the separate Keycloak database).
-- As you build services, create a database (or schema) per service per 0A/16.

-- Application database (schema-per-service lives inside this DB for Tier 1; `identity` is one of them).
CREATE DATABASE hbmp;

-- Per-service databases can be added later, e.g.:
-- CREATE DATABASE patient;
-- CREATE DATABASE policy;
-- CREATE DATABASE eligibility;
-- CREATE DATABASE emr;
-- CREATE DATABASE orders;
-- CREATE DATABASE approvals;
-- CREATE DATABASE provider;
-- CREATE DATABASE pharmacy;
-- CREATE DATABASE notification;
-- CREATE DATABASE reporting;
-- CREATE DATABASE audit;
-- CREATE DATABASE document;
-- CREATE DATABASE masterdata;

-- Enable pgcrypto in the app DB for column-level PHI/PII encryption (0C §security).
\connect hbmp
CREATE EXTENSION IF NOT EXISTS pgcrypto;
