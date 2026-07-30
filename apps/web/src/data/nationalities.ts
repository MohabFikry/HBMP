import type { Localized } from "@mersal/contracts";

/**
 * ISO 3166-1 alpha-2 nationalities, bilingual.
 *
 * ============================================================================================================
 * WHY THIS LIVES IN THE CLIENT, AND WHAT SHOULD REPLACE IT
 * ============================================================================================================
 * Nationality is reference data and belongs in masterdata-service beside the drug, ICD-10 and CPT catalogues.
 * It is not there yet, and inventing a service surface for it as a side effect of building a registration form
 * would be the wrong place to make that decision. So the codes live here, the SERVER validates only the SHAPE
 * (two letters — `PersonFieldValidation.IsValidNationalityCode`), and nothing depends on this list being
 * complete: a code masterdata later accepts and this list lacks is stored fine, it simply renders as its code.
 *
 * The order is deliberate. The countries Mersal's beneficiaries actually come from sit at the top, because a
 * refugee-health registration desk types "Sudan" many times a day and "Iceland" never — and an operator who
 * has to scroll past 90 irrelevant entries every time is an operator who will eventually pick the wrong one.
 * Everything else follows alphabetically. The control has typeahead either way.
 */

export interface Nationality {
  /** ISO 3166-1 alpha-2. */
  code: string;
  label: Localized;
}

/** The populations this registry serves, first. */
const PRIORITY: Nationality[] = [
  { code: "SY", label: { en: "Syria", ar: "سوريا" } },
  { code: "SD", label: { en: "Sudan", ar: "السودان" } },
  { code: "SS", label: { en: "South Sudan", ar: "جنوب السودان" } },
  { code: "ER", label: { en: "Eritrea", ar: "إريتريا" } },
  { code: "ET", label: { en: "Ethiopia", ar: "إثيوبيا" } },
  { code: "SO", label: { en: "Somalia", ar: "الصومال" } },
  { code: "YE", label: { en: "Yemen", ar: "اليمن" } },
  { code: "IQ", label: { en: "Iraq", ar: "العراق" } },
  { code: "PS", label: { en: "Palestine", ar: "فلسطين" } },
  { code: "LY", label: { en: "Libya", ar: "ليبيا" } },
  { code: "EG", label: { en: "Egypt", ar: "مصر" } },
];

const REST: Nationality[] = [
  { code: "AF", label: { en: "Afghanistan", ar: "أفغانستان" } },
  { code: "DZ", label: { en: "Algeria", ar: "الجزائر" } },
  { code: "AO", label: { en: "Angola", ar: "أنغولا" } },
  { code: "AR", label: { en: "Argentina", ar: "الأرجنتين" } },
  { code: "AM", label: { en: "Armenia", ar: "أرمينيا" } },
  { code: "AU", label: { en: "Australia", ar: "أستراليا" } },
  { code: "AT", label: { en: "Austria", ar: "النمسا" } },
  { code: "AZ", label: { en: "Azerbaijan", ar: "أذربيجان" } },
  { code: "BH", label: { en: "Bahrain", ar: "البحرين" } },
  { code: "BD", label: { en: "Bangladesh", ar: "بنغلاديش" } },
  { code: "BY", label: { en: "Belarus", ar: "بيلاروسيا" } },
  { code: "BE", label: { en: "Belgium", ar: "بلجيكا" } },
  { code: "BJ", label: { en: "Benin", ar: "بنين" } },
  { code: "BR", label: { en: "Brazil", ar: "البرازيل" } },
  { code: "BF", label: { en: "Burkina Faso", ar: "بوركينا فاسو" } },
  { code: "BI", label: { en: "Burundi", ar: "بوروندي" } },
  { code: "CM", label: { en: "Cameroon", ar: "الكاميرون" } },
  { code: "CA", label: { en: "Canada", ar: "كندا" } },
  { code: "CF", label: { en: "Central African Republic", ar: "جمهورية أفريقيا الوسطى" } },
  { code: "TD", label: { en: "Chad", ar: "تشاد" } },
  { code: "CN", label: { en: "China", ar: "الصين" } },
  { code: "CO", label: { en: "Colombia", ar: "كولومبيا" } },
  { code: "KM", label: { en: "Comoros", ar: "جزر القمر" } },
  { code: "CG", label: { en: "Congo", ar: "الكونغو" } },
  { code: "CD", label: { en: "Congo (DRC)", ar: "الكونغو الديمقراطية" } },
  { code: "CI", label: { en: "Côte d'Ivoire", ar: "ساحل العاج" } },
  { code: "DJ", label: { en: "Djibouti", ar: "جيبوتي" } },
  { code: "FR", label: { en: "France", ar: "فرنسا" } },
  { code: "GM", label: { en: "Gambia", ar: "غامبيا" } },
  { code: "GE", label: { en: "Georgia", ar: "جورجيا" } },
  { code: "DE", label: { en: "Germany", ar: "ألمانيا" } },
  { code: "GH", label: { en: "Ghana", ar: "غانا" } },
  { code: "GR", label: { en: "Greece", ar: "اليونان" } },
  { code: "GN", label: { en: "Guinea", ar: "غينيا" } },
  { code: "IN", label: { en: "India", ar: "الهند" } },
  { code: "ID", label: { en: "Indonesia", ar: "إندونيسيا" } },
  { code: "IR", label: { en: "Iran", ar: "إيران" } },
  { code: "IE", label: { en: "Ireland", ar: "أيرلندا" } },
  { code: "IT", label: { en: "Italy", ar: "إيطاليا" } },
  { code: "JO", label: { en: "Jordan", ar: "الأردن" } },
  { code: "KZ", label: { en: "Kazakhstan", ar: "كازاخستان" } },
  { code: "KE", label: { en: "Kenya", ar: "كينيا" } },
  { code: "KW", label: { en: "Kuwait", ar: "الكويت" } },
  { code: "LB", label: { en: "Lebanon", ar: "لبنان" } },
  { code: "LR", label: { en: "Liberia", ar: "ليبيريا" } },
  { code: "MY", label: { en: "Malaysia", ar: "ماليزيا" } },
  { code: "ML", label: { en: "Mali", ar: "مالي" } },
  { code: "MR", label: { en: "Mauritania", ar: "موريتانيا" } },
  { code: "MA", label: { en: "Morocco", ar: "المغرب" } },
  { code: "MZ", label: { en: "Mozambique", ar: "موزمبيق" } },
  { code: "MM", label: { en: "Myanmar", ar: "ميانمار" } },
  { code: "NL", label: { en: "Netherlands", ar: "هولندا" } },
  { code: "NE", label: { en: "Niger", ar: "النيجر" } },
  { code: "NG", label: { en: "Nigeria", ar: "نيجيريا" } },
  { code: "NO", label: { en: "Norway", ar: "النرويج" } },
  { code: "OM", label: { en: "Oman", ar: "عُمان" } },
  { code: "PK", label: { en: "Pakistan", ar: "باكستان" } },
  { code: "PH", label: { en: "Philippines", ar: "الفلبين" } },
  { code: "PL", label: { en: "Poland", ar: "بولندا" } },
  { code: "PT", label: { en: "Portugal", ar: "البرتغال" } },
  { code: "QA", label: { en: "Qatar", ar: "قطر" } },
  { code: "RO", label: { en: "Romania", ar: "رومانيا" } },
  { code: "RU", label: { en: "Russia", ar: "روسيا" } },
  { code: "RW", label: { en: "Rwanda", ar: "رواندا" } },
  { code: "SA", label: { en: "Saudi Arabia", ar: "السعودية" } },
  { code: "SN", label: { en: "Senegal", ar: "السنغال" } },
  { code: "SL", label: { en: "Sierra Leone", ar: "سيراليون" } },
  { code: "ZA", label: { en: "South Africa", ar: "جنوب أفريقيا" } },
  { code: "ES", label: { en: "Spain", ar: "إسبانيا" } },
  { code: "LK", label: { en: "Sri Lanka", ar: "سريلانكا" } },
  { code: "SE", label: { en: "Sweden", ar: "السويد" } },
  { code: "CH", label: { en: "Switzerland", ar: "سويسرا" } },
  { code: "TZ", label: { en: "Tanzania", ar: "تنزانيا" } },
  { code: "TH", label: { en: "Thailand", ar: "تايلاند" } },
  { code: "TG", label: { en: "Togo", ar: "توغو" } },
  { code: "TN", label: { en: "Tunisia", ar: "تونس" } },
  { code: "TR", label: { en: "Türkiye", ar: "تركيا" } },
  { code: "UG", label: { en: "Uganda", ar: "أوغندا" } },
  { code: "UA", label: { en: "Ukraine", ar: "أوكرانيا" } },
  { code: "AE", label: { en: "United Arab Emirates", ar: "الإمارات" } },
  { code: "GB", label: { en: "United Kingdom", ar: "المملكة المتحدة" } },
  { code: "US", label: { en: "United States", ar: "الولايات المتحدة" } },
  { code: "UZ", label: { en: "Uzbekistan", ar: "أوزبكستان" } },
  { code: "VN", label: { en: "Viet Nam", ar: "فيتنام" } },
  { code: "ZM", label: { en: "Zambia", ar: "زامبيا" } },
  { code: "ZW", label: { en: "Zimbabwe", ar: "زيمبابوي" } },
];

export const NATIONALITIES: readonly Nationality[] = [...PRIORITY, ...REST];

/**
 * Dial codes for the same populations, so the phone field can be entered as a code plus a national number
 * and stored as one E.164 string. Only the codes a registration desk here actually reaches for — the field
 * accepts a typed value too, so an unlisted country is never a dead end.
 */
export const DIAL_CODES: readonly { code: string; country: string }[] = [
  { code: "+20", country: "EG" },
  { code: "+963", country: "SY" },
  { code: "+249", country: "SD" },
  { code: "+211", country: "SS" },
  { code: "+291", country: "ER" },
  { code: "+251", country: "ET" },
  { code: "+252", country: "SO" },
  { code: "+967", country: "YE" },
  { code: "+964", country: "IQ" },
  { code: "+970", country: "PS" },
  { code: "+218", country: "LY" },
  { code: "+962", country: "JO" },
  { code: "+961", country: "LB" },
  { code: "+966", country: "SA" },
  { code: "+971", country: "AE" },
];
