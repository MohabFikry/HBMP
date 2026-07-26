# Provider Onboarding Kit (العربية / English)

Phase 12.3. For external **labs**, **imaging centres**, and **pharmacies** joining the pilot. Quick-start
+ video storyboard. Provider users are **scoped to their own organization only** — you never see other
providers' data (verified before your account is enabled).

## First sign-in — أول تسجيل دخول
**EN** 1. You receive a username + a first-login link. 2. Set your password (min 12 chars) and enrol
your **authenticator app** (scan the QR → 6-digit codes). 3. Save your **recovery codes** somewhere
safe. 4. Sign in → you land on your provider portal, scoped to your organization.

**AR** ١. تصلك اسم مستخدم ورابط أول دخول. ٢. عيّن كلمة المرور (١٢ حرفًا على الأقل) وفعّل **تطبيق
المصادقة** (امسح رمز QR ← أكواد من ٦ أرقام). ٣. احفظ **أكواد الاسترداد** في مكان آمن. ٤. سجّل الدخول
← تفتح بوابة المزوّد الخاصة بمنشأتك فقط.

## Labs & Imaging — المختبرات والأشعة
**EN — Order queue → atomic consume → result upload**
1. Open your **order queue**; new orders arrive from clinics automatically.
2. **Consume** the order line to claim the work — each line consumes **once**; the system prevents
   two technicians from consuming the same line.
3. Perform the test; **upload the result** (PDF/values). It routes back to the ordering clinician.
4. Sensitive examinations follow the release workflow — the clinic controls disclosure.

**AR — قائمة الطلبات ← الاستهلاك الذرّي ← رفع النتيجة**
1. افتح **قائمة الطلبات**؛ تصل الطلبات الجديدة من العيادات تلقائيًا.
2. **استهلك** بند الطلب لبدء العمل — يُستهلك كل بند **مرة واحدة**؛ يمنع النظام استهلاك فنيَّين للبند نفسه.
3. نفّذ الفحص و**ارفع النتيجة** (ملف/قيم). تعود إلى الطبيب الطالب.
4. تتبع الفحوصات الحسّاسة مسار الإفراج — العيادة تتحكم في الإفصاح.

## Pharmacies — الصيدليات
**EN — Dispense**
1. Open **dispensable prescriptions**; search by Rx id.
2. Verify patient + item; **dispense** (batch/expiry). Dispensing is atomic + idempotent — a
   double-click never double-dispenses.
3. **Partial dispense** the available quantity; the remainder stays open. Use **substitution** when
   out of stock (record the reason).
4. You see only what you need to dispense — no diagnoses or investigation results.

**AR — الصرف**
1. افتح **الوصفات القابلة للصرف**؛ ابحث برقم الوصفة.
2. تحقّق من المريض والصنف؛ **اصرف** (التشغيلة/الصلاحية). الصرف ذرّي وغير قابل للتكرار — لا يُكرّر النقر
   المزدوج الصرف.
3. **اصرف جزئيًا** الكمية المتاحة؛ يبقى الباقي مفتوحًا. استخدم **الاستبدال** عند النفاد (سجّل السبب).
4. ترى فقط ما يلزم للصرف — بدون تشخيصات أو نتائج فحوصات.

## Support — الدعم
- In week 1, your named **champion** and the hypercare war-room support you on the floor.
- Report any issue immediately (it enters the incident register). If the platform is unavailable, the
  clinic uses the paper fallback and the data is entered afterward.
- خلال الأسبوع الأول، يدعمك **البطل** المخصّص وغرفة الطوارئ. أبلغ عن أي مشكلة فورًا. عند تعذّر النظام،
  تستخدم العيادة الإجراء الورقي ثم تُدخل البيانات لاحقًا.
