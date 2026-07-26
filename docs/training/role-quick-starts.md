# Role Quick-Starts (العربية / English)

Phase 12.3. One quick-start per pilot portal. Each is bilingual and doubles as a video storyboard
(numbered steps = shots). Sign in at the clinic URL with your username + password, then your
**authenticator code** (TOTP) — privileged actions require the second factor.

---

## Reception — الاستقبال
**EN — Find a beneficiary and start a visit**
1. Sign in → open **Reception**.
2. Search by name / national ID / UNHCR number (minimum-necessary results only — no clinical data).
3. Confirm identity, check **eligibility** (green = eligible; read the coverage note).
4. Start the visit / check-in the appointment. If the board shows "changed — refresh", reload and
   re-check-in (someone else updated it).
5. Hand off to the clinician queue.

**AR — البحث عن مستفيد وبدء الزيارة**
1. سجّل الدخول ← افتح شاشة **الاستقبال**.
2. ابحث بالاسم / الرقم القومي / رقم المفوضية (تظهر بيانات الحد الأدنى فقط — بدون بيانات سريرية).
3. تحقّق من الهوية، وافحص **الأهلية** (أخضر = مؤهل؛ اقرأ ملاحظة التغطية).
4. ابدأ الزيارة / سجّل حضور الموعد. إذا ظهرت "تم التغيير — حدّث"، أعد التحميل ثم سجّل الحضور من جديد.
5. حوّل المستفيد إلى قائمة الطبيب.

---

## Registration — التسجيل
**EN** 1. Open **Registration** → New beneficiary. 2. Enter identifiers (national ID / UNHCR /
passport) — the system normalizes + checks for duplicates. 3. If a possible duplicate is flagged,
**do not force-create** — send to review. 4. Attach required documents (upload; scanned for malware).
5. Assign policy / coverage → activate.

**AR** ١. افتح **التسجيل** ← مستفيد جديد. ٢. أدخل المعرّفات (الرقم القومي / المفوضية / جواز السفر) —
يوحّد النظام الصيغة ويتحقق من التكرار. ٣. عند وجود تكرار محتمل **لا تُنشئ السجل بالقوة** — أرسله
للمراجعة. ٤. أرفق المستندات المطلوبة (يتم فحصها من الفيروسات). ٥. اربط الوثيقة/التغطية ← فعّل السجل.

---

## Clinician (Doctor) — الطبيب
**EN — Encounter, order, e-prescription**
1. Open **Clinician** → your patient list (you see **only assigned patients**).
2. Open the encounter → record vitals + **structured diagnosis** (ICD).
3. Create investigation **orders** and/or an **e-prescription** (drug from the formulary; interaction
   + allergy checks run automatically).
4. Submit — high-cost / restricted items route to **approvals** automatically.
5. Review returned results in your worklist; sensitive results follow the release workflow.

**AR — الكشف والطلبات والوصفة الإلكترونية**
1. افتح **الطبيب** ← قائمة مرضاك (ترى **المرضى المحوّلين إليك فقط**).
2. افتح الكشف ← سجّل العلامات الحيوية و**تشخيصًا منظّمًا** (ICD).
3. أنشئ **طلبات** فحوصات و/أو **وصفة إلكترونية** (الدواء من الدليل؛ يتم فحص التداخلات والحساسية آليًا).
4. أرسل — تُحوّل العناصر مرتفعة التكلفة/المقيّدة إلى **الموافقات** تلقائيًا.
5. راجع النتائج العائدة في قائمتك؛ تتبع النتائج الحسّاسة مسار الإفراج.

---

## Nurse — التمريض
**EN** 1. Open **Nurse** → today's queue. 2. Record vitals / triage. 3. Administer + document per
order (medication administration is logged). 4. Flag anything urgent to the clinician.

**AR** ١. افتح **التمريض** ← قائمة اليوم. ٢. سجّل العلامات الحيوية/الفرز. ٣. نفّذ ووثّق حسب الطلب
(يُسجّل إعطاء الدواء). ٤. نبّه الطبيب لأي حالة عاجلة.

---

## Lab / Imaging — المختبر / الأشعة
**EN — Fulfil an order (atomic, no double-use)**
1. Open **Lab** or **Imaging** → the provider order queue; search by order id.
2. **Consume** the order line to start work — each line can be consumed **once** (the system blocks
   re-use even under simultaneous clicks).
3. Perform the test; **upload the result** (routes back to the ordering clinician).
4. For sensitive examinations, results follow the sensitivity/release workflow.

**AR — تنفيذ الطلب (ذرّي، بدون استخدام مزدوج)**
1. افتح **المختبر** أو **الأشعة** ← قائمة طلبات المزوّد؛ ابحث برقم الطلب.
2. **استهلك** بند الطلب لبدء العمل — يُستهلك كل بند **مرة واحدة** (يمنع النظام التكرار حتى مع النقر
   المتزامن).
3. نفّذ الفحص و**ارفع النتيجة** (تعود إلى الطبيب الطالب).
4. للفحوصات الحسّاسة، تتبع النتائج مسار الحساسية/الإفراج.

---

## Pharmacy — الصيدلية
**EN — Dispense a prescription**
1. Open **Pharmacy** → dispensable prescriptions; search by Rx id.
2. Verify the patient + item. **Dispense** (batch/expiry captured) — dispensing is atomic and requires
   an idempotency key, so a double-click never double-dispenses.
3. **Partial dispensing:** record what was given; the remainder stays open.
4. **Out of stock / substitution:** use the substitution flow (records reason); you do **not** see
   investigation results — pharmacy is minimum-necessary.

**AR — صرف الوصفة**
1. افتح **الصيدلية** ← الوصفات القابلة للصرف؛ ابحث برقم الوصفة.
2. تحقّق من المريض والصنف. **اصرف** (يُسجّل التشغيلة/الصلاحية) — الصرف ذرّي ويتطلّب مفتاح تكرار، فلا
   يُكرّر النقر المزدوج الصرف.
3. **الصرف الجزئي:** سجّل ما تم صرفه؛ يبقى الباقي مفتوحًا.
4. **نفاد المخزون / البديل:** استخدم مسار الاستبدال (يسجّل السبب)؛ لا ترى نتائج الفحوصات — الصيدلية
   بحد أدنى ضروري.

---

## Approvals — الموافقات
**EN — Review and decide**
1. Open **Approvals** → worklist (member-scoped; you see the clinical context needed to decide).
2. Open a case → review notes / reports; check SLA/TAT timer.
3. **Decide** (approve / reject / request info) with a reason — the decision is hash-chain audited and
   flows downstream. Segregation-of-duties + dual control apply where required.
4. **Break-glass** only for a genuine emergency: time-boxed, justified, and fully audited.

**AR — المراجعة واتخاذ القرار**
1. افتح **الموافقات** ← قائمة العمل (مقيّدة بالمستفيد؛ ترى السياق السريري اللازم للقرار).
2. افتح الحالة ← راجع الملاحظات/التقارير؛ تابع مؤقّت مستوى الخدمة/زمن الإنجاز.
3. **اتخذ القرار** (موافقة/رفض/طلب معلومات) مع ذكر السبب — يُدوّن القرار في سلسلة تدقيق ويُنفّذ لاحقًا.
   تطبّق فصل المهام والرقابة المزدوجة عند الحاجة.
4. **كسر الزجاج** للطوارئ الحقيقية فقط: محدّد بزمن، مبرّر، ومُدقّق بالكامل.
