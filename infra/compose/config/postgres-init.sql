-- Mersal HBMP — initial databases (Tier 1 starter)
-- Runs once on first Postgres boot. Keycloak + a shared app DB to start.
-- As you build services, create a database (or schema) per service per 0A/16.

CREATE DATABASE keycloak;

-- Application database (schema-per-service lives inside this DB for Tier 1).
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
