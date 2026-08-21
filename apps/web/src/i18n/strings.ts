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
  // ---- 32.6 — correcting a contact on the call (design 11 §3.1: the call centre holds U on contacts) ----
  ccEditContact: { en: "Correct", ar: "تصحيح" },
  ccContactValue: { en: "New value", ar: "القيمة الجديدة" },
  ccSaveContact: { en: "Save", ar: "حفظ" },
  ccCancelEdit: { en: "Cancel", ar: "إلغاء" },
  ccAddContact: { en: "Add a contact", ar: "إضافة وسيلة تواصل" },
  ccContactKind: { en: "Kind", ar: "النوع" },
  ccContactPrimary: { en: "Make this the primary one", ar: "اجعلها الوسيلة الأساسية" },
  ccContactSaved: { en: "Contact updated.", ar: "تم تحديث بيانات التواصل." },
  ccContactAdded: { en: "Contact added.", ar: "تمت إضافة وسيلة التواصل." },
  ccContactInvalid: {
    en: "That is not a well-formed value for this kind. Read it back to the member and try again.",
    ar: "هذه القيمة غير صحيحة لهذا النوع. أعِد قراءتها على المستفيد وحاول مجدداً.",
  },
  ccContactNotVerified: {
    en: "This call is not open on that member's file, so their contacts cannot be changed. Open their file first.",
    ar: "هذه المكالمة غير مفتوحة على ملف ذلك المستفيد، لذا لا يمكن تعديل بيانات تواصله. افتح ملفه أولاً.",
  },
  ccContactFailed: { en: "That didn't go through.", ar: "لم يتم تنفيذ ذلك." },
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
  // The three steps of a call-centre booking, mirroring reception's "1. Patient / 2. Appointment" so the two
  // front-of-house screens read the same way — with the step the call centre has and reception does not.
  ccStepMember: { en: "1. Member", ar: "١. المستفيد" },
  ccStepAppointment: { en: "2. Appointment", ar: "٢. الموعد" },
  ccStepCallRecord: { en: "3. Call record", ar: "٣. سجل المكالمة" },
  ccChoose: { en: "Choose", ar: "اختيار" },
  ccChange: { en: "Change", ar: "تغيير" },
  ccBookAction: { en: "Book appointment", ar: "احجز الموعد" },
  // Told only AFTER a booking is attempted — listing what is missing before the agent has tried is noise.
  ccNeedMember: { en: "Choose a member first.", ar: "اختر المستفيد أولاً." },
  ccNeedDoctor: { en: "Choose a specialty and doctor.", ar: "اختر التخصص والطبيب." },
  ccNeedSlot: { en: "Choose a time.", ar: "اختر الوقت." },
  ccNoteTooLong: { en: "Shorten the appointment notes before booking.", ar: "اختصر ملاحظات الموعد قبل الحجز." },
  // Who rang whom. Recorded on the interaction at the moment it opens, so it cannot be corrected afterwards —
  // hence the control locks once the call is under way rather than pretending a later change would stick.
  ccDirection: { en: "Direction", ar: "اتجاه المكالمة" },
  ccInbound: { en: "Inbound — the member called us", ar: "وارد — المستفيد اتصل بنا" },
  ccOutbound: { en: "Outbound — we called the member", ar: "صادر — نحن اتصلنا بالمستفيد" },
  ccDirectionLocked: {
    en: "Set when the call was opened and kept as the record of what happened.",
    ar: "حُدِّد عند فتح المكالمة ويُحفظ كسجل لما حدث.",
  },
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
  loginTitle: { en: "Sign in", ar: "تسجيل الدخول" },
  // One short line, left-aligned with the heading. The old copy ran to two centred lines and said three
  // things at a moment when somebody wants to type a username.
  loginSub: {
    en: "Use your Mersal single sign-on to continue.",
    ar: "استخدم الدخول الموحّد لمرسال للمتابعة.",
  },
  chooseRole: { en: "Role (demo sign-in)", ar: "الدور (دخول تجريبي)" },
  // Dev-only. The picker cannot be exercised without a session holding more than one portal, and this is
  // the only way to produce one with no issuer running.
  extraPortals: { en: "Additional portals (demo)", ar: "بوابات إضافية (تجريبي)" },
  extraPortalsHelp: {
    en: "Tick more portals to sign in holding several, which is what the portal picker is for.",
    ar: "حدّد بوابات إضافية للدخول بأكثر من بوابة، وهو ما تخدمه شاشة اختيار البوابة.",
  },
  mfaLabel: { en: "Authenticator code", ar: "رمز المصادقة" },
  mfaHelp: { en: "Enter the 6-digit code from your authenticator app.", ar: "أدخل الرمز المكوّن من 6 أرقام من تطبيق المصادقة." },
  mfaError: { en: "A valid 6-digit code is required.", ar: "يلزم رمز صحيح من 6 أرقام." },
  signIn: { en: "Sign in", ar: "دخول" },
  // Names the platform's own issuer. It said "via Keycloak" until phase 19 — long after ADR-0015 retired
  // Keycloak — so the screen told every operator their credentials were going somewhere they were not.
  signInVia: { en: "Secure sign-in via Mersal ID", ar: "تسجيل دخول آمن عبر هوية مرسال" },

  // ---- Portal picker + in-app switcher ----------------------------------------------------------------
  // `{name}` is substituted, never concatenated: Arabic puts the greeting and the name in the other order,
  // and a `${greeting} ${name}` in the component would hard-code the English one for both languages.
  welcomeBack: { en: "Welcome back, {name}", ar: "أهلاً بعودتك، {name}" },
  portalPickerLede: {
    en: "Choose where to work. Each portal shows only the data your role in it permits.",
    ar: "اختر مكان العمل. كل بوابة تعرض فقط البيانات التي يسمح بها دورك فيها.",
  },
  portalPickerTitle: { en: "Choose a portal", ar: "اختر بوابة" },
  portalSections: { en: "{n} sections", ar: "{n} أقسام" },
  portalSectionsOne: { en: "1 section", ar: "قسم واحد" },
  // The picker's app-bar search. It filters the CARDS — it is not the command palette, which searches the
  // sections inside a portal and therefore has nothing to search until one is chosen.
  searchPortals: { en: "Search portals", ar: "ابحث في البوابات" },
  portalsMatch: { en: "{n} portals match", ar: "{n} بوابات مطابقة" },
  portalsNoMatch: { en: "No portals match", ar: "لا توجد بوابات مطابقة" },
  portalsNoMatchHelp: {
    en: "Nothing here matches that. Clear the search to see every portal you hold.",
    ar: "لا يوجد ما يطابق ذلك. امسح البحث لعرض كل البوابات التي تملكها.",
  },
  currentPortal: { en: "Current portal", ar: "البوابة الحالية" },
  changePortal: { en: "Change portal", ar: "تغيير البوابة" },
  openPortal: { en: "Open", ar: "فتح" },

  // ---- 28.8: changing your own password, from the account pane ----------------------------------------
  security: { en: "Security", ar: "الأمان" },
  password: { en: "Password", ar: "كلمة المرور" },
  changePassword: { en: "Change password", ar: "تغيير كلمة المرور" },
  // Said BEFORE the change rather than after: signing every other device out is the reason somebody would
  // choose this over doing nothing when they suspect their password is known.
  changePasswordHelp: {
    en: "You'll stay signed in here. Every other device is signed out.",
    ar: "ستبقى مسجّلاً هنا. وسيتم إخراج كل الأجهزة الأخرى.",
  },
  currentPassword: { en: "Current password", ar: "كلمة المرور الحالية" },
  newPassword: { en: "New password", ar: "كلمة المرور الجديدة" },
  confirmPassword: { en: "Confirm new password", ar: "تأكيد كلمة المرور الجديدة" },
  passwordPolicy: {
    en: "At least 12 characters, with upper and lower case, a digit and a symbol.",
    ar: "12 حرفًا على الأقل، مع أحرف كبيرة وصغيرة ورقم ورمز.",
  },
  passwordMismatch: { en: "These two do not match.", ar: "الحقلان غير متطابقين." },
  passwordChanged: { en: "Password changed. Other devices have been signed out.", ar: "تم تغيير كلمة المرور. تم إخراج الأجهزة الأخرى." },

  // ---- 28.4: the sign-in happens HERE now (ADR-0036). Every string authored in both locales. ----
  usernameLabel: { en: "Username", ar: "اسم المستخدم" },
  passwordLabel: { en: "Password", ar: "كلمة المرور" },
  // ONE message for an unknown username, a wrong password and a deactivated account. The server already
  // refuses to tell them apart; saying more here would rebuild the enumeration oracle in the browser.
  signInInvalid: {
    en: "That username and password don't match. Check them and try again.",
    ar: "اسم المستخدم أو كلمة المرور غير صحيحة. تحقق منهما وحاول مجدداً.",
  },
  // Told deliberately (ADR-0036 §5.2): the alternative sends someone to reset a password that was never
  // wrong, and a reset does not unlock the account — they would lose the password AND stay locked out.
  signInLocked: {
    en: "This account is temporarily locked after too many attempts.",
    ar: "تم قفل هذا الحساب مؤقتاً بعد عدد كبير من المحاولات.",
  },
  signInLockedWait: { en: "Try again in about {n} minutes.", ar: "حاول مجدداً بعد حوالي {n} دقيقة." },
  // "We could not ask" is never rendered as "your password is wrong".
  signInUnavailable: {
    en: "Sign-in is unavailable right now. This is not a problem with your password — please try again shortly.",
    ar: "تسجيل الدخول غير متاح حالياً. هذه ليست مشكلة في كلمة المرور — يرجى المحاولة بعد قليل.",
  },
  signInNoMembership: {
    en: "Your account is not active in any organization. Contact your administrator.",
    ar: "حسابك غير مُفعّل في أي منظمة. تواصل مع المسؤول.",
  },

  twoFactorTitle: { en: "Two-step verification", ar: "التحقق بخطوتين" },
  twoFactorSub: {
    en: "Enter the 6-digit code from your authenticator app.",
    ar: "أدخل الرمز المكوّن من 6 أرقام من تطبيق المصادقة.",
  },
  twoFactorCode: { en: "Authenticator code", ar: "رمز المصادقة" },
  twoFactorUseRecovery: { en: "Use a recovery code instead", ar: "استخدم رمز استرداد بدلاً من ذلك" },
  twoFactorUseCode: { en: "Use my authenticator app", ar: "استخدم تطبيق المصادقة" },
  twoFactorRecoveryLabel: { en: "Recovery code", ar: "رمز الاسترداد" },
  twoFactorInvalid: { en: "That code wasn't accepted. Try again.", ar: "لم يتم قبول هذا الرمز. حاول مجدداً." },

  membershipTitle: { en: "Choose an organization", ar: "اختر منظمة" },
  membershipSub: {
    en: "Your account is active in more than one. This session will act for the one you pick.",
    ar: "حسابك مُفعّل في أكثر من واحدة. ستعمل هذه الجلسة نيابة عن التي تختارها.",
  },
  membershipContinue: { en: "Continue", ar: "متابعة" },

  signInBack: { en: "Back", ar: "رجوع" },
  signInWorking: { en: "Signing in…", ar: "جارٍ تسجيل الدخول…" },
  signInHelp: {
    en: "Need access? Contact your administrator.",
    ar: "بحاجة إلى صلاحية؟ تواصل مع المسؤول.",
  },
  signInForgot: { en: "Forgot password?", ar: "نسيت كلمة المرور؟" },

  // ---- 28.8: the sign-in hero. Decorative copy, still authored in both locales. ----
  heroKicker: { en: "Mersal Foundation", ar: "مؤسسة مرسال" },
  // Two lines on purpose — "One platform." lands, then the promise. The break is markup, not a newline in
  // the string: a \n would collapse in HTML and an Arabic translation may want to break elsewhere.
  heroHeadlineLead: { en: "One platform.", ar: "منصة واحدة." },
  heroHeadlineRest: {
    en: "Every stakeholder. Every step of care.",
    ar: "كل صاحب مصلحة. كل خطوة في الرعاية.",
  },
  heroLede: {
    en: "The Healthcare Benefit Management Platform for Mersal Foundation. Eligibility, care and approvals in one auditable, bilingual record.",
    ar: "منصة إدارة المنافع الصحية لمؤسسة مرسال. الأهلية والرعاية والموافقات في سجل واحد قابل للتدقيق وثنائي اللغة.",
  },
  rememberDevice: { en: "Remember this device", ar: "تذكّر هذا الجهاز" },
  toggleLanguage: { en: "Switch language", ar: "تغيير اللغة" },
  toggleTheme: { en: "Switch theme", ar: "تغيير السمة" },

  // ---- 28.6: self-service password reset (ADR-0036 §6) ----
  forgotTitle: { en: "Reset your password", ar: "إعادة تعيين كلمة المرور" },
  forgotSub: {
    en: "Enter your username and we'll send a reset link to the email on your account.",
    ar: "أدخل اسم المستخدم وسنرسل رابط إعادة التعيين إلى البريد المسجّل على حسابك.",
  },
  forgotSubmit: { en: "Send reset link", ar: "إرسال رابط إعادة التعيين" },
  // Deliberately vague about whether the account exists — the server answers the same either way, and a
  // precise message here would be a free account-existence oracle costing an attacker nothing.
  forgotSent: {
    en: "If that account exists, a reset link is on its way. The link works once and expires in 30 minutes.",
    ar: "إذا كان هذا الحساب موجوداً، فسيصلك رابط إعادة التعيين. يعمل الرابط مرة واحدة وينتهي خلال 30 دقيقة.",
  },
  // The vagueness stops at delivery. Never "we've emailed you" when nothing could be emailed.
  forgotUnavailable: {
    en: "Password reset isn't available on this system yet. Contact your administrator to have it reset for you.",
    ar: "إعادة تعيين كلمة المرور غير متاحة بعد على هذا النظام. تواصل مع المسؤول لإعادة تعيينها لك.",
  },

  resetTitle: { en: "Choose a new password", ar: "اختر كلمة مرور جديدة" },
  resetSub: {
    en: "This link works once. Pick a password you haven't used here before.",
    ar: "هذا الرابط يعمل مرة واحدة. اختر كلمة مرور لم تستخدمها هنا من قبل.",
  },
  resetNewPassword: { en: "New password", ar: "كلمة المرور الجديدة" },
  resetConfirmPassword: { en: "Confirm new password", ar: "تأكيد كلمة المرور الجديدة" },
  resetSubmit: { en: "Set new password", ar: "تعيين كلمة المرور" },
  resetMismatch: { en: "The two passwords don't match.", ar: "كلمتا المرور غير متطابقتين." },
  resetInvalidLink: {
    en: "That reset link is no longer valid. Links expire after 30 minutes and can be used only once — request a new one.",
    ar: "هذا الرابط لم يعد صالحاً. تنتهي الروابط بعد 30 دقيقة وتُستخدم مرة واحدة فقط — اطلب رابطاً جديداً.",
  },
  // Said BEFORE the fields, not after the deed.
  resetEndsSessions: {
    en: "Setting a new password signs you out everywhere.",
    ar: "تعيين كلمة مرور جديدة سيُنهي جلساتك على كل الأجهزة.",
  },
  resetKeepsTwoFactor: {
    en: "It does not turn off two-step verification — you'll still need your authenticator code.",
    ar: "لن يُلغي ذلك التحقق بخطوتين — ستظل بحاجة إلى رمز المصادقة.",
  },
  resetDone: {
    en: "Your password is set and every other session has ended. Sign in with the new password.",
    ar: "تم تعيين كلمة المرور وانتهت كل الجلسات الأخرى. سجّل الدخول بكلمة المرور الجديدة.",
  },
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
