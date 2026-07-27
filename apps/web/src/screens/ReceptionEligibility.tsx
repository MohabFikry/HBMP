import { useState } from "react";
import { useFormat } from "../i18n/useFormat";
import { Button, Card, InputField, StatusChip, useTheme } from "@mersal/design-system";
import type { EligibilityResult, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { ApiError } from "../api/http";
import { PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Eligibility search", ar: "التحقق من الأهلية" },
  field: { en: "Card number, national ID, or name", ar: "رقم البطاقة أو الهوية أو الاسم" },
  help: { en: "Minimum-necessary — reception sees coverage only, never clinical data.", ar: "الحد الأدنى — يرى الاستقبال التغطية فقط دون بيانات سريرية." },
  check: { en: "Check eligibility", ar: "تحقق من الأهلية" },
  idle: { en: "Search a beneficiary to check coverage and visit eligibility.", ar: "ابحث عن مستفيد للتحقق من التغطية وأهلية الزيارة." },
  loading: { en: "Checking…", ar: "جارٍ التحقق…" },
  error: { en: "Couldn't check eligibility. Try again.", ar: "تعذّر التحقق من الأهلية. حاول مجدداً." },
  coverage: { en: "Coverage", ar: "التغطية" },
  plan: { en: "Plan", ar: "الخطة" },
  band: { en: "Benefit band", ar: "فئة المنفعة" },
  copay: { en: "Copay", ar: "المساهمة" },
  validUntil: { en: "Valid until", ar: "صالح حتى" },
  capRemaining: { en: "Annual cap remaining", ar: "المتبقي من الحد السنوي" },
  visit: { en: "Visit gating", ar: "أهلية الزيارة" },
  visitOk: { en: "Visit allowed today", ar: "الزيارة مسموحة اليوم" },
  visitNo: { en: "Visit not allowed", ar: "الزيارة غير مسموحة" },
  card: { en: "Card", ar: "البطاقة" },
  dob: { en: "Date of birth", ar: "تاريخ الميلاد" },
} satisfies Record<string, Localized>;

export function ReceptionEligibility() {
  const api = useApi();
  const t = useLoc();
  const { lang } = useTheme();
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<"idle" | "loading" | "error" | "success">("idle");
  const [result, setResult] = useState<EligibilityResult | null>(null);

  async function run() {
    if (query.trim().length < 2) return;
    setStatus("loading");
    try {
      const hits = await api.searchEligibility(query.trim());
      if (hits.length === 0) {
        setResult(null);
        setStatus("success");
        return;
      }
      const res = await api.checkEligibility(hits[0].id);
      setResult(res);
      setStatus("success");
    } catch (err) {
      void (err instanceof ApiError);
      setStatus("error");
    }
  }

  function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    void run();
  }

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <form onSubmit={onSubmit} className="stack" aria-label={t(S.title)}>
          <InputField
            label={t(S.field)}
            help={t(S.help)}
            value={query}
            onChange={(e) => setQuery(e.currentTarget.value)}
            autoComplete="off"
          />
          <div>
            <Button type="submit" variant="primary" loading={status === "loading"}>
              {t(S.check)}
            </Button>
          </div>
        </form>
      </Card>

      {/* Async outcome — announced politely for screen readers. */}
      <div aria-live="polite" className="stack" style={{ marginTop: "var(--sp4)" }}>
        {status === "idle" && (
          <Card style={{ padding: "var(--sp5)" }}>
            <p className="muted">{t(S.idle)}</p>
          </Card>
        )}
        {status === "loading" && (
          <Card style={{ padding: "var(--sp5)" }}>
            <div className="async-loading" role="status">
              <span className="mrs-spin" aria-hidden="true" />
              <span>{t(S.loading)}</span>
            </div>
          </Card>
        )}
        {status === "error" && (
          <Card style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
            <StatusChip kind="bad" label={t(S.error)} />
            <div>
              <Button variant="secondary" onClick={() => void run()}>
                {t(S.check)}
              </Button>
            </div>
          </Card>
        )}
        {status === "success" && !result && (
          <Card style={{ padding: "var(--sp5)" }}>
            <StatusChip kind="neu" label={lang === "ar" ? "لا توجد نتائج" : "No matching beneficiary"} />
          </Card>
        )}
        {status === "success" && result && <ResultCard result={result} t={t} S={S} />}
      </div>
    </>
  );
}

function ResultCard({ result, t, S }: { result: EligibilityResult; t: (l: Localized) => string; S: Record<string, Localized> }) {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const b = result.beneficiary;
  const c = result.coverage;
  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <div className="result-head">
        <div>
          <h2 style={{ margin: 0 }}>{t(b.name)}</h2>
          <p className="muted" style={{ margin: "4px 0 0" }}>
            {t(S.card)}: <span className="tnum">{b.cardNumber}</span>
            {b.dateOfBirth && <> · {t(S.dob)}: <span className="tnum">{b.dateOfBirth}</span></>}
          </p>
        </div>
        <StatusChip kind={result.status.kind} label={t(result.status.label)} />
      </div>

      {c && (
        <div className="kv-grid" aria-label={t(S.coverage)}>
          <div><dt>{t(S.plan)}</dt><dd>{t(c.planName)}</dd></div>
          <div><dt>{t(S.band)}</dt><dd>{t(c.band)}</dd></div>
          {c.copayPercent != null && <div><dt>{t(S.copay)}</dt><dd className="tnum">{c.copayPercent}%</dd></div>}
          {c.validUntil && <div><dt>{t(S.validUntil)}</dt><dd className="tnum">{c.validUntil}</dd></div>}
          {c.annualCapRemaining && <div><dt>{t(S.capRemaining)}</dt><dd className="tnum">{fmt.money(c.annualCapRemaining)}</dd></div>}
        </div>
      )}

      <div>
        {result.visitGate.allowed ? (
          <StatusChip kind="ok" label={t(S.visitOk)} />
        ) : (
          <StatusChip kind="warn" label={result.visitGate.reason ? t(result.visitGate.reason) : t(S.visitNo)} />
        )}
      </div>
    </Card>
  );
}
