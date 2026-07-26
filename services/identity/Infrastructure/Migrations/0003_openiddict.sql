-- identity-service — 0003 OpenIddict store (Phase 17.2). The authorization server's application /
-- authorization / scope / token tables, in the same `identity` schema + context. This DDL is GENERATED from
-- the OpenIddict EF model (Database.GenerateCreateScript) so it matches exactly, then made idempotent +
-- granted to hbmp_app. Do not hand-edit column shapes — regenerate if the OpenIddict model version changes.

CREATE TABLE IF NOT EXISTS identity."OpenIddictApplications" (
    id text NOT NULL,
    application_type character varying(50),
    client_id character varying(100),
    client_secret text,
    client_type character varying(50),
    concurrency_token character varying(50),
    consent_type character varying(50),
    display_name text,
    display_names text,
    json_web_key_set text,
    permissions text,
    post_logout_redirect_uris text,
    properties text,
    redirect_uris text,
    requirements text,
    settings text,
    CONSTRAINT pk_open_iddict_applications PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS identity."OpenIddictScopes" (
    id text NOT NULL,
    concurrency_token character varying(50),
    description text,
    descriptions text,
    display_name text,
    display_names text,
    name character varying(200),
    properties text,
    resources text,
    CONSTRAINT pk_open_iddict_scopes PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS identity."OpenIddictAuthorizations" (
    id text NOT NULL,
    application_id text,
    concurrency_token character varying(50),
    creation_date timestamp with time zone,
    properties text,
    scopes text,
    status character varying(50),
    subject character varying(400),
    type character varying(50),
    CONSTRAINT pk_open_iddict_authorizations PRIMARY KEY (id),
    CONSTRAINT fk_open_iddict_authorizations_open_iddict_applications_application FOREIGN KEY (application_id) REFERENCES identity."OpenIddictApplications" (id)
);

CREATE TABLE IF NOT EXISTS identity."OpenIddictTokens" (
    id text NOT NULL,
    application_id text,
    authorization_id text,
    concurrency_token character varying(50),
    creation_date timestamp with time zone,
    expiration_date timestamp with time zone,
    payload text,
    properties text,
    redemption_date timestamp with time zone,
    reference_id character varying(100),
    status character varying(50),
    subject character varying(400),
    type character varying(50),
    CONSTRAINT pk_open_iddict_tokens PRIMARY KEY (id),
    CONSTRAINT fk_open_iddict_tokens_open_iddict_applications_application_id FOREIGN KEY (application_id) REFERENCES identity."OpenIddictApplications" (id),
    CONSTRAINT fk_open_iddict_tokens_open_iddict_authorizations_authorization_id FOREIGN KEY (authorization_id) REFERENCES identity."OpenIddictAuthorizations" (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_open_iddict_applications_client_id ON identity."OpenIddictApplications" (client_id);
CREATE INDEX IF NOT EXISTS ix_open_iddict_authorizations_application_id_status_subject_type ON identity."OpenIddictAuthorizations" (application_id, status, subject, type);
CREATE UNIQUE INDEX IF NOT EXISTS ix_open_iddict_scopes_name ON identity."OpenIddictScopes" (name);
CREATE INDEX IF NOT EXISTS ix_open_iddict_tokens_application_id_status_subject_type ON identity."OpenIddictTokens" (application_id, status, subject, type);
CREATE INDEX IF NOT EXISTS ix_open_iddict_tokens_authorization_id ON identity."OpenIddictTokens" (authorization_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_open_iddict_tokens_reference_id ON identity."OpenIddictTokens" (reference_id);

-- The issuer (identity-service) reads/writes these under the hbmp_app runtime role. Not tenant-RLS: the
-- authorization server operates outside request tenant context (same rationale as identity core, 0002).
GRANT SELECT, INSERT, UPDATE, DELETE ON
    identity."OpenIddictApplications", identity."OpenIddictScopes",
    identity."OpenIddictAuthorizations", identity."OpenIddictTokens" TO hbmp_app;
