import type { Localized } from "../portals/catalog";

/** Shell / auth strings, authored in both locales (no machine translation). Section labels live in the catalog. */
export const L = {
  search: { en: "Search", ar: "بحث" },
  language: { en: "Language", ar: "اللغة" },
  theme: { en: "Theme", ar: "السمة" },
  light: { en: "Light theme", ar: "سمة فاتحة" },
  dark: { en: "Dark theme", ar: "سمة داكنة" },
  signedInAs: { en: "Signed in as", ar: "مسجّل الدخول باسم" },
  signOut: { en: "Sign out", ar: "تسجيل الخروج" },
  breadcrumb: { en: "Breadcrumb", ar: "مسار التنقل" },
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
  // 403 / 404
  forbiddenTitle: { en: "You don't have access to this page", ar: "ليس لديك صلاحية الوصول لهذه الصفحة" },
  forbiddenBody: {
    en: "This route is outside your role's permissions. The attempt has been logged. You can request access from an administrator.",
    ar: "هذا المسار خارج صلاحيات دورك. تم تسجيل المحاولة. يمكنك طلب الوصول من المسؤول.",
  },
  requestAccess: { en: "Request access", ar: "طلب الوصول" },
  backToPortal: { en: "Back to my portal", ar: "العودة إلى بوابتي" },
  notFoundTitle: { en: "Page not found", ar: "الصفحة غير موجودة" },
  notFoundBody: { en: "The page you're looking for doesn't exist.", ar: "الصفحة التي تبحث عنها غير موجودة." },
  // Section placeholder
  sectionStub: {
    en: "This screen is wired in Phase 9.3 (flagship screens). The portal shell, permission routing and min-necessary navigation are live.",
    ar: "يتم ربط هذه الشاشة في المرحلة 9.3 (الشاشات الرئيسية). هيكل البوابة والتوجيه حسب الصلاحيات والتنقل بالحد الأدنى فعّالة.",
  },
  requestSent: { en: "Access request sent to administrator", ar: "تم إرسال طلب الوصول إلى المسؤول" },
} satisfies Record<string, Localized>;
