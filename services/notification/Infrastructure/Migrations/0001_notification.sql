-- Phase 8.1 — notification schema: in-app inbox + email/sms delivery rows, versioned bilingual templates,
-- event-dedupe ledger. Bodies carry ONLY min-necessary, non-clinical interpolated fields.
CREATE SCHEMA IF NOT EXISTS notification;

CREATE TABLE IF NOT EXISTS notification.notification (
    notification_id        uuid PRIMARY KEY,
    tenant_id              text NOT NULL,
    recipient_user_id      text NOT NULL,
    recipient_role         text NOT NULL,
    channel                text NOT NULL CHECK (channel IN ('InApp','Email','Sms','WhatsApp')),
    locale                 text NOT NULL CHECK (locale IN ('ar','en')),
    template_key           text NOT NULL,
    subject                text NOT NULL,
    body                   text NOT NULL,
    status_text            text NOT NULL,
    source_event_id        uuid NOT NULL,
    source_event_type      text NOT NULL,
    entity_ref             text,
    sensitive              boolean NOT NULL DEFAULT false,
    status                 text NOT NULL DEFAULT 'Queued' CHECK (status IN ('Queued','Sent','Delivered','Failed','Skipped')),
    attempts               integer NOT NULL DEFAULT 0,
    last_error             text,
    created_at             timestamptz NOT NULL,
    sent_at                timestamptz,
    delivered_at           timestamptz,
    failed_at              timestamptz,
    read_at                timestamptz,
    actionable             boolean NOT NULL DEFAULT false,
    escalation_due_at      timestamptz,
    escalated_at           timestamptz,
    escalated_from_id      uuid,
    escalation_to_user_id  text,
    escalation_to_role     text,
    escalation_to_locale   text
);

-- One notification per (event, recipient, channel): idempotent fan-out under redelivery / concurrency.
CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_event_recipient_channel
    ON notification.notification (source_event_id, recipient_user_id, channel);
CREATE INDEX IF NOT EXISTS ix_notification_inbox ON notification.notification (recipient_user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_notification_status ON notification.notification (status);
-- The escalation sweep predicate.
CREATE INDEX IF NOT EXISTS ix_notification_escalation
    ON notification.notification (escalation_due_at)
    WHERE actionable AND read_at IS NULL AND escalated_at IS NULL;

CREATE TABLE IF NOT EXISTS notification.notification_template (
    template_id  uuid PRIMARY KEY,
    template_key text NOT NULL,
    locale       text NOT NULL CHECK (locale IN ('ar','en')),
    version      integer NOT NULL DEFAULT 1,
    active       boolean NOT NULL DEFAULT true,
    subject      text NOT NULL,
    body         text NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_template_key_locale_version
    ON notification.notification_template (template_key, locale, version);

CREATE TABLE IF NOT EXISTS notification.processed_event (
    event_id             uuid PRIMARY KEY,
    event_type           text NOT NULL,
    notifications_created integer NOT NULL DEFAULT 0,
    consumed_at          timestamptz NOT NULL
);

-- ── Seed bilingual (ar/en) templates. Both locales authored; Arabic is RTL, never machine-translated.
-- Tokens are min-necessary + non-clinical: {ref} business key, {providerName}, {status}.
INSERT INTO notification.notification_template (template_id, template_key, locale, version, active, subject, body) VALUES
 (gen_random_uuid(),'auth.approved','en',1,true,'Authorization {ref} approved','Authorization {ref} for {providerName} was approved.'),
 (gen_random_uuid(),'auth.approved','ar',1,true,'تمت الموافقة على التفويض {ref}','تمت الموافقة على التفويض {ref} الخاص بـ {providerName}.'),
 (gen_random_uuid(),'auth.partially_approved','en',1,true,'Authorization {ref} partially approved','Authorization {ref} for {providerName} was partially approved.'),
 (gen_random_uuid(),'auth.partially_approved','ar',1,true,'تمت الموافقة الجزئية على التفويض {ref}','تمت الموافقة الجزئية على التفويض {ref} الخاص بـ {providerName}.'),
 (gen_random_uuid(),'auth.rejected','en',1,true,'Authorization {ref} rejected','Authorization {ref} for {providerName} was rejected.'),
 (gen_random_uuid(),'auth.rejected','ar',1,true,'تم رفض التفويض {ref}','تم رفض التفويض {ref} الخاص بـ {providerName}.'),
 (gen_random_uuid(),'auth.emergency_approved','en',1,true,'Authorization {ref} emergency-approved','Authorization {ref} for {providerName} was emergency-approved.'),
 (gen_random_uuid(),'auth.emergency_approved','ar',1,true,'موافقة طارئة على التفويض {ref}','تمت الموافقة الطارئة على التفويض {ref} الخاص بـ {providerName}.'),
 (gen_random_uuid(),'auth.info_requested','en',1,true,'More information needed for {ref}','Additional information is required for authorization {ref}. Please respond.'),
 (gen_random_uuid(),'auth.info_requested','ar',1,true,'مطلوب معلومات إضافية للتفويض {ref}','مطلوب معلومات إضافية للتفويض {ref}. يرجى الرد.'),
 (gen_random_uuid(),'auth.sla_breach','en',1,true,'Pending approval {ref} breached SLA','Authorization {ref} is past its review SLA and needs attention.'),
 (gen_random_uuid(),'auth.sla_breach','ar',1,true,'تجاوز التفويض {ref} مهلة المراجعة','تجاوز التفويض {ref} مهلة المراجعة ويحتاج إلى اهتمام.'),
 (gen_random_uuid(),'order.line_available','en',1,true,'Order {ref} available','Order {ref} is now available.'),
 (gen_random_uuid(),'order.line_available','ar',1,true,'الطلب {ref} متاح','الطلب {ref} متاح الآن.'),
 (gen_random_uuid(),'result.ready','en',1,true,'Result ready for {ref}','A result is ready for order {ref}.'),
 (gen_random_uuid(),'result.ready','ar',1,true,'النتيجة جاهزة للطلب {ref}','أصبحت نتيجة الطلب {ref} جاهزة.'),
 (gen_random_uuid(),'rx.ready','en',1,true,'Prescription {ref} ready','Prescription {ref} is ready for dispensing.'),
 (gen_random_uuid(),'rx.ready','ar',1,true,'الوصفة {ref} جاهزة','الوصفة {ref} جاهزة للصرف.'),
 (gen_random_uuid(),'rx.out_of_stock','en',1,true,'Prescription {ref} out of stock','A line on prescription {ref} is out of stock and needs action.'),
 (gen_random_uuid(),'rx.out_of_stock','ar',1,true,'الوصفة {ref} غير متوفرة','أحد بنود الوصفة {ref} غير متوفر ويحتاج إلى إجراء.'),
 (gen_random_uuid(),'appointment.reminder','en',1,true,'Appointment reminder {ref}','Reminder: your appointment {ref} is upcoming.'),
 (gen_random_uuid(),'appointment.reminder','ar',1,true,'تذكير بالموعد {ref}','تذكير: موعدك {ref} قادم.'),
 (gen_random_uuid(),'appointment.no_show','en',1,true,'No-show for {ref}','Appointment {ref} was marked as a no-show.'),
 (gen_random_uuid(),'appointment.no_show','ar',1,true,'عدم حضور للموعد {ref}','تم تسجيل الموعد {ref} كعدم حضور.')
ON CONFLICT DO NOTHING;

-- notification-service owns this schema; the app role reads/writes it. Unlike the append-only audit / decision
-- ledgers, the notification store is operational (retention purge is a legitimate need), so DELETE is granted.
GRANT USAGE ON SCHEMA notification TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA notification TO hbmp_app;
