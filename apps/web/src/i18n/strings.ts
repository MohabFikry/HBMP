import type { Localized } from "../portals/catalog";

/** Shell / auth strings, authored in both locales (no machine translation). Section labels live in the catalog. */
export const L = {
  search: { en: "Search", ar: "بحث" },
  language: { en: "Language", ar: "اللغة" },
  theme: { en: "Theme", ar: "السمة" },
  light: { en: "Light theme", ar: "سمة فاتحة" },
  dark: { en: "Dark theme", ar: "سمة داكنة" },
  signedInAs: { en: "Signed in as", ar: "مسجّل الدخول باسم" },
  notifications: { en: "Notifications", ar: "الإشعارات" },
  notificationsUnread: { en: "unread notifications", ar: "إشعارات غير مقروءة" },
  notificationsEmpty: { en: "You're all caught up — no notifications.", ar: "لا توجد إشعارات جديدة." },
  notificationsError: { en: "Couldn't load notifications. Try again.", ar: "تعذّر تحميل الإشعارات. حاول مجدداً." },
  notificationsClose: { en: "Close notifications", ar: "إغلاق الإشعارات" },
  notificationsViewAll: { en: "View all notifications", ar: "عرض كل الإشعارات" },
  notificationsMarkRead: { en: "Mark read", ar: "تحديد كمقروء" },
  notificationsMarkAllRead: { en: "Mark all read", ar: "تحديد الكل كمقروء" },
  notificationsAllRead: { en: "All notifications marked read.", ar: "تم تحديد كل الإشعارات كمقروءة." },
  notificationsGoTo: { en: "Open", ar: "فتح" },
  notificationsActionNeeded: { en: "Action needed", ar: "يتطلب إجراء" },
  signOut: { en: "Sign out", ar: "تسجيل الخروج" },
  // 14.8 — branch switcher + restricted-result UI
  branch: { en: "Branch", ar: "الفرع" },
  activeBranch: { en: "Active branch", ar: "الفرع الحالي" },
  homeBranch: { en: "Home", ar: "الرئيسي" },
  allBranches: { en: "All branches", ar: "كل الفروع" },
  branchSwitched: { en: "Active branch changed to", ar: "تم تغيير الفرع الحالي إلى" },
  restricted: { en: "Restricted", ar: "مقيّد" },
  restrictedResult: { en: "Restricted result", ar: "نتيجة مقيّدة" },
  restrictedBody: {
    en: "This result is special-category and its content is restricted. Request access with a justified purpose.",
    ar: "هذه النتيجة من فئة خاصة ومحتواها مقيّد. اطلب الوصول بغرض مبرَّر.",
  },
  purpose: { en: "Purpose", ar: "الغرض" },
  justification: { en: "Justification", ar: "المبرر" },
  duration: { en: "Requested duration (hours)", ar: "المدة المطلوبة (ساعات)" },
  justificationRequired: { en: "A justification is required.", ar: "المبرر مطلوب." },
  purposeRequired: { en: "A purpose is required.", ar: "الغرض مطلوب." },
  submit: { en: "Submit request", ar: "إرسال الطلب" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  // 15.5 — Call Centre workspace
  ccStartCall: { en: "Start call", ar: "بدء المكالمة" },
  ccCloseCall: { en: "Close call", ar: "إنهاء المكالمة" },
  ccOnCall: { en: "On call", ar: "مكالمة جارية" },
  ccReason: { en: "Call reason", ar: "سبب المكالمة" },
  ccSearchLabel: { en: "Find member", ar: "ابحث عن مستفيد" },
  // What ONE box matches. Name leads because it is what a caller offers first and what the old identifier-led
  // picker made look unsupported.
  ccSearchHelp: {
    en: "Search by name, phone number, card or member number, national ID, passport, refugee ID or UNHCR number.",
    ar: "ابحث بالاسم أو رقم الهاتف أو رقم البطاقة أو العضوية أو الرقم القومي أو الجواز أو رقم اللاجئ أو UNHCR.",
  },
  ccSearch: { en: "Search", ar: "بحث" },
  // Identity is confirmed BY THE AGENT ON THE PHONE. Opening the file records that and binds the call to this
  // member; the agent is told what the click means rather than asked to tick boxes the platform used to score.
  ccOpenFile: { en: "Open member file", ar: "فتح ملف المستفيد" },
  ccOpenFileHelp: {
    en: "Confirm who you are speaking to before you open the file. Opening it records that you did, against this call.",
    ar: "تأكّد ممّن تتحدث إليه قبل فتح الملف. فتح الملف يسجّل ذلك على هذه المكالمة.",
  },
  ccFileOpened: { en: "Member file open for this call", ar: "ملف المستفيد مفتوح لهذه المكالمة" },
  ccOpenFileFailed: {
    en: "Couldn't open the member's file — nothing was recorded. Try again.",
    ar: "تعذّر فتح ملف المستفيد — لم يُسجَّل شيء. حاول مرة أخرى.",
  },
  ccOpenProfile: { en: "Open full profile", ar: "فتح الملف الكامل" },
  ccCoverage: { en: "Coverage & remaining limits", ar: "التغطية والحدود المتبقية" },
  ccContacts: { en: "Contacts", ar: "بيانات التواصل" },
  ccAppointments: { en: "Appointments (all branches)", ar: "المواعيد (كل الفروع)" },
  ccReferrals: { en: "Open referrals & follow-ups", ar: "الإحالات والمتابعات المفتوحة" },
  ccBook: { en: "Book", ar: "حجز" },
  ccReschedule: { en: "Reschedule", ar: "إعادة جدولة" },
  ccCancel: { en: "Cancel appointment", ar: "إلغاء الموعد" },
  ccCancelConfirm: {
    en: "The time is released for someone else and anyone on the waitlist may be offered it. Tell the caller before you confirm.",
    ar: "سيُتاح الوقت لشخص آخر وقد يُعرض على من في قائمة الانتظار. أبلغ المتصل قبل التأكيد.",
  },
  ccKeep: { en: "Keep it", ar: "الاحتفاظ به" },
  ccCancelReason: { en: "Cancellation reason", ar: "سبب الإلغاء" },
  ccCancelReasonRequired: { en: "A cancellation reason is required.", ar: "سبب الإلغاء مطلوب." },
  ccSlotTaken: { en: "That slot was just taken — pick another.", ar: "تم حجز هذا الموعد للتو — اختر موعدًا آخر." },
  ccBooked: { en: "Appointment booked and confirmed to the member.", ar: "تم حجز الموعد وتأكيده للمستفيد." },
  // ccFailed says "Verification failed", which is true for a verification and a lie for a booking — the same
  // string was announced for both, so a rejected reservation told the agent the caller was not verified.
  ccBookFailed: { en: "That did not go through — nothing was changed.", ar: "لم تُنفَّذ العملية — لم يتغيّر شيء." },
  ccCancelled: { en: "Appointment cancelled and confirmed to the member.", ar: "تم إلغاء الموعد وتأكيده للمستفيد." },
  ccBranch: { en: "Branch", ar: "الفرع" },
  ccPickBranch: { en: "Choose a branch", ar: "اختر الفرع" },
  ccPickBranchFirst: { en: "Choose a branch first", ar: "اختر الفرع أولاً" },
  ccClinic: { en: "Clinic", ar: "العيادة" },
  ccPickClinic: { en: "Choose a clinic", ar: "اختر العيادة" },
  ccTime: { en: "Time", ar: "الوقت" },
  ccPickTime: { en: "Choose a time first.", ar: "اختر الوقت أولاً." },
  ccNoClinics: {
    en: "No clinic has bookable times right now.",
    ar: "لا توجد عيادة بأوقات متاحة للحجز حالياً.",
  },
  ccNoSlots: { en: "No open times at this clinic.", ar: "لا توجد أوقات متاحة في هذه العيادة." },
  ccReserveOnly: {
    en: "Reservations only — arrivals are recorded by the branch desk.",
    ar: "الحجز فقط — يسجّل مكتب الفرع وصول المرضى.",
  },
  ccBookAt: { en: "Book at", ar: "حجز في" },
  ccWrapUp: { en: "Wrap up", ar: "إنهاء" },
  ccOutcome: { en: "Outcome", ar: "النتيجة" },
  ccNotes: { en: "Notes", ar: "ملاحظات" },
  ccNoResults: { en: "No match — try a phone number or another identifier.", ar: "لا يوجد تطابق — جرّب رقم هاتف أو هوية أخرى." },
  ccHistoryTitle: { en: "Call history", ar: "سجل المكالمات" },
  ccHistoryEmpty: { en: "No calls yet.", ar: "لا توجد مكالمات بعد." },
  ccHistoryError: { en: "Couldn't load call history.", ar: "تعذّر تحميل سجل المكالمات." },
  ccRescheduled: { en: "Appointment rescheduled and confirmed to the member.", ar: "تمت إعادة جدولة الموعد وتأكيده للمستفيد." },
  // ── Wrap-up: the summary other roles read, and the close outcomes that used to be silent ───────────────
  ccSummary: { en: "Call summary", ar: "ملخّص المكالمة" },
  ccSummaryHelp: {
    en: "The record of this call. Read by coordinators and clinicians on the member's profile — what the call was about and what you did, with no clinical detail.",
    ar: "سجل هذه المكالمة. يقرأه المنسّقون والأطباء في ملف المستفيد — موضوع المكالمة وما قمت به، بدون أي تفاصيل طبية.",
  },
  ccSummaryRequired: {
    en: "A summary is required to close this call. Another role reads it later to see what happened.",
    ar: "يلزم إدخال ملخّص لإنهاء هذه المكالمة. يقرأه لاحقًا زملاء آخرون لمعرفة ما جرى.",
  },
  ccCloseFailed: {
    en: "The call could not be closed and is still open. Try again.",
    ar: "تعذّر إنهاء المكالمة وما زالت مفتوحة. حاول مرة أخرى.",
  },
  ccNotYourCall: {
    en: "This call was taken by another agent. Only they or a supervisor can change its record.",
    ar: "هذه المكالمة تولّاها موظف آخر. لا يمكن تعديل سجلّها إلا بواسطته أو بواسطة مشرف.",
  },
  ccApptStale: {
    en: "This appointment changed while you were on the call. Reload the member's file and try again.",
    ar: "تغيّر هذا الموعد أثناء المكالمة. أعد تحميل ملف المستفيد وحاول مرة أخرى.",
  },
  // ── The member file: act from the file the agent is already looking at ────────────────────────────────
  ccNewAppointment: { en: "New appointment", ar: "موعد جديد" },
  ccCopy: { en: "Copy summary", ar: "نسخ الملخّص" },
  ccCopied: { en: "Call summary copied to the clipboard.", ar: "تم نسخ ملخّص المكالمة." },
  ccCopyFailed: { en: "Couldn't copy — select the text and copy manually.", ar: "تعذّر النسخ — حدّد النص وانسخه يدويًا." },
  // ── Standalone "Book appointment" journey ─────────────────────────────────────────────────────────────
  ccBookTitle: { en: "Book appointment", ar: "حجز موعد" },
  ccBookIntro: {
    en: "Find the member, open their file, then choose the branch, clinic and time. The booking is logged against this call.",
    ar: "ابحث عن المستفيد، افتح ملفه، ثم اختر الفرع والعيادة والوقت. يُسجَّل الحجز على هذه المكالمة.",
  },
  ccStepFind: { en: "1. Find the member", ar: "١. ابحث عن المستفيد" },
  ccStepChoose: { en: "2. Choose branch, clinic and time", ar: "٢. اختر الفرع والعيادة والوقت" },
  ccBookAnother: { en: "Book for another member", ar: "حجز لمستفيد آخر" },
  ccBookFinish: { en: "Finish and close the call", ar: "إنهاء المكالمة" },
  ccBookClosed: { en: "Call closed.", ar: "تم إنهاء المكالمة." },
  ccBookOpenFailed: {
    en: "Couldn't open a call record — the booking was not started. Try again.",
    ar: "تعذّر إنشاء سجل المكالمة — لم يبدأ الحجز. حاول مرة أخرى.",
  },
  retry: { en: "Retry", ar: "إعادة المحاولة" },
  account: { en: "Account", ar: "الحساب" },
  accountOpen: { en: "Account menu", ar: "قائمة الحساب" },
  accountClose: { en: "Close account menu", ar: "إغلاق قائمة الحساب" },
  settings: { en: "Settings", ar: "الإعدادات" },
  appearance: { en: "Appearance", ar: "المظهر" },
  staySignedIn: { en: "Stay signed in", ar: "البقاء مسجلاً" },
  timeoutTitle: { en: "Session about to expire", ar: "الجلسة على وشك الانتهاء" },
  timeoutBody: {
    en: "You've been inactive. For security, you'll be signed out shortly unless you continue.",
    ar: "لم تكن نشطًا. لأسباب أمنية، سيتم تسجيل خروجك قريبًا ما لم تتابع.",
  },
  // Login
  loginTitle: { en: "Sign in to Mersal HBMP", ar: "تسجيل الدخول إلى مرسال HBMP" },
  loginSub: {
    en: "Single sign-on with multi-factor authentication. You'll land on your role's portal only.",
    ar: "دخول موحّد مع مصادقة متعددة العوامل. ستصل إلى بوابة دورك فقط.",
  },
  chooseRole: { en: "Role (demo sign-in)", ar: "الدور (دخول تجريبي)" },
  mfaLabel: { en: "Authenticator code", ar: "رمز المصادقة" },
  mfaHelp: { en: "Enter the 6-digit code from your authenticator app.", ar: "أدخل الرمز المكوّن من 6 أرقام من تطبيق المصادقة." },
  mfaError: { en: "A valid 6-digit code is required.", ar: "يلزم رمز صحيح من 6 أرقام." },
  signIn: { en: "Sign in", ar: "دخول" },
  // Names the platform's own issuer. It said "via Keycloak" until phase 19 — long after ADR-0015 retired
  // Keycloak — so the screen told every operator their credentials were going somewhere they were not.
  signInVia: { en: "Secure sign-in via Mersal ID", ar: "تسجيل دخول آمن عبر هوية مرسال" },
  // 403 / 404
  forbiddenTitle: { en: "You don't have access to this page", ar: "ليس لديك صلاحية الوصول لهذه الصفحة" },
  forbiddenBody: {
    en: "This route is outside your role's permissions. The attempt has been logged. You can request access from an administrator.",
    ar: "هذا المسار خارج صلاحيات دورك. تم تسجيل المحاولة. يمكنك طلب الوصول من المسؤول.",
  },
  requestAccess: { en: "Request access", ar: "طلب الوصول" },
  // Phase 21.6 — THREE distinct 403 treatments (design 40 §4/§6). One generic page would send every user
  // to the wrong person: these three are fixed by an administrator, by Mersal, and by the user themselves.
  notEnabledTitle: { en: "This module isn't enabled for your organization", ar: "هذه الوحدة غير مفعّلة لمؤسستك" },
  notEnabledBody: {
    en: "Your permissions are fine — this programme has not been enabled for your organization yet. Mersal programme administration can enable it.",
    ar: "صلاحياتك سليمة — لكن هذا البرنامج لم يُفعّل لمؤسستك بعد. يمكن لإدارة برامج مرسال تفعيله.",
  },
  contactMersal: { en: "Contact Mersal programme administration", ar: "تواصل مع إدارة برامج مرسال" },
  limitReachedTitle: { en: "Your organization has reached its limit", ar: "وصلت مؤسستك إلى الحد المسموح" },
  limitReachedBody: {
    en: "Free a slot, or ask Mersal programme administration to raise the limit.",
    ar: "حرّر مكانًا، أو اطلب من إدارة برامج مرسال رفع الحد.",
  },
  branchOutOfScopeTitle: { en: "That branch isn't in your current access", ar: "هذا الفرع ليس ضمن صلاحيات وصولك" },
  branchOutOfScopeBody: {
    en: "You asked for a branch you don't currently have a grant for. Switch to a branch you can reach, or ask your administrator to extend your access.",
    ar: "طلبت فرعًا ليس لديك تصريح به حاليًا. انتقل إلى فرع يمكنك الوصول إليه، أو اطلب من المسؤول توسيع صلاحياتك.",
  },
  switchBranch: { en: "Switch branch", ar: "تبديل الفرع" },
  backToPortal: { en: "Back to my portal", ar: "العودة إلى بوابتي" },
  notFoundTitle: { en: "Page not found", ar: "الصفحة غير موجودة" },
  notFoundBody: { en: "The page you're looking for doesn't exist.", ar: "الصفحة التي تبحث عنها غير موجودة." },
  // Authenticated but no portal role mapped (fail-closed)
  noPortalTitle: { en: "No portal assigned", ar: "لا توجد بوابة مخصّصة" },
  noPortalBody: {
    en: "You are signed in, but your account has no role that maps to a portal. Ask an administrator to grant you a role, then sign in again.",
    ar: "لقد سجّلت الدخول، لكن حسابك لا يملك دورًا مرتبطًا بأي بوابة. اطلب من المسؤول منحك دورًا ثم سجّل الدخول مرة أخرى.",
  },
  // Section placeholder
  sectionStub: {
    en: "This screen is wired in Phase 9.3 (flagship screens). The portal shell, permission routing and min-necessary navigation are live.",
    ar: "يتم ربط هذه الشاشة في المرحلة 9.3 (الشاشات الرئيسية). هيكل البوابة والتوجيه حسب الصلاحيات والتنقل بالحد الأدنى فعّالة.",
  },
  requestSent: { en: "Access request sent to administrator", ar: "تم إرسال طلب الوصول إلى المسؤول" },
} satisfies Record<string, Localized>;
