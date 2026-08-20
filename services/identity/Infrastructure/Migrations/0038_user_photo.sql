-- identity-service — 0038: a staff member's photograph.
--
-- ============================================================================================================
-- WHY THE BYTES LIVE HERE AND NOT IN MINIO
-- ============================================================================================================
-- The platform already stores one kind of photograph — a BENEFICIARY's — and it does so through MinIO with a
-- short-TTL signed URL, a narrower allow-list than the profile itself, and an audit event on every read
-- (services/profile/Api/PhotoEndpoints.cs, design 39 §5). All of that is right, and none of it transfers.
-- That photo is identity-sensitive, biometric-adjacent data about a refugee, taken with consent that can be
-- refused, and every disclosure of it is a fact a data-subject request can ask about.
--
-- This is a member of staff's own picture of themselves, chosen by them, shown to colleagues beside their
-- name — the same category as their display name, which sits unencrypted two columns away. Giving it the
-- refugee-photo apparatus would not make it safer; it would make identity-service depend on an object store
-- it does not otherwise use, for an asset measured in tens of kilobytes.
--
-- So: a bounded row, in its own table.
--
-- ============================================================================================================
-- ITS OWN TABLE, NOT A COLUMN ON `user`
-- ============================================================================================================
-- `identity."user"` is read on every sign-in, every token mint and every admin list. A bytea column on it
-- would ride along in the row width of all of those, for a value that is wanted on exactly one endpoint.
-- Separating it keeps the hot table narrow and makes "has a photo" a join rather than a payload.
--
-- ON DELETE CASCADE: a photograph of a person whose account has gone is not a record of a decision, so the
-- no-hard-deletes rule (CLAUDE.md § Audit) does not reach it. The audit event describing the upload does,
-- and that lives in the audit store, not here.

CREATE TABLE IF NOT EXISTS identity.user_photo (
    user_id      uuid PRIMARY KEY REFERENCES identity."user"(id) ON DELETE CASCADE,
    content_type varchar(40)  NOT NULL,
    bytes        bytea        NOT NULL,
    byte_size    integer      NOT NULL,
    updated_at   timestamptz  NOT NULL,
    -- WHO set it. An administrator may set a photo for somebody else, and "who chose the picture on my
    -- profile" is a question the person it depicts is entitled to an answer to.
    updated_by   text         NOT NULL,

    -- The cap is enforced in three places on purpose: the browser downscales before upload, the endpoint
    -- refuses an oversized body, and this constraint is what holds if either is ever bypassed. 512 KB is
    -- generous for a 512px square and small enough that no request here is a memory concern.
    CONSTRAINT user_photo_size_bounded CHECK (byte_size > 0 AND byte_size <= 524288),
    -- An ALLOW-LIST, not a pattern. The endpoint additionally verifies the magic bytes, because a declared
    -- content type is a claim by the uploader; this constraint is what stops anything else being stored at
    -- all, including by a future code path that forgets to check.
    CONSTRAINT user_photo_type_allowed CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp'))
);

COMMENT ON TABLE identity.user_photo IS
    'A staff member''s own avatar. Display only; never an authorization input. Beneficiary photographs are a '
    'different thing entirely and live behind profile-service (see the 0038 header).';
