-- notification-service — 0003 the registration info-request template.
--
-- A supervisor reviewing a registration can ask the filing officer for more information (US-003). Until now
-- that decision changed a status and wrote a note, and told nobody: the officer had no reason to reopen a row
-- they had already finished with, so the request sat unanswered and the application aged in the queue.
--
-- Tokens are min-necessary and non-clinical, per 11-permission-matrix and the dispatcher's own field guard:
--   {ref} — the operational reference the officer recognises (the card number, or the member number, or the
--           registration id when the person holds neither). NOT a name and not a diagnosis.
-- The supervisor's actual prose stays on the registration thread, behind authorization, where the officer
-- reads it in context and answers in place. A notification body is a doorbell, not the conversation.
--
-- Both locales authored, Arabic first-class rather than machine-translated, exactly as 0001 established.
-- Idempotent: re-running the migration must not seed a second copy of version 1.
INSERT INTO notification.notification_template (template_id, template_key, locale, version, active, subject, body)
SELECT * FROM (VALUES
  (gen_random_uuid(),'registration.info_requested','en',1,true,
   'More information needed for registration {ref}',
   'A supervisor needs more information before registration {ref} can be approved. Open the registration to read the request and reply.'),
  (gen_random_uuid(),'registration.info_requested','ar',1,true,
   'مطلوب معلومات إضافية للتسجيل {ref}',
   'يحتاج المشرف إلى معلومات إضافية قبل اعتماد التسجيل {ref}. افتح التسجيل لقراءة الطلب والرد عليه.')
) AS seed(template_id, template_key, locale, version, active, subject, body)
WHERE NOT EXISTS (
    SELECT 1 FROM notification.notification_template t
    WHERE t.template_key = seed.template_key AND t.locale = seed.locale AND t.version = seed.version
);
