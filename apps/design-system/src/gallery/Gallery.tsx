import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  Button,
  Card,
  Column,
  DataTable,
  Icon,
  InlineAlert,
  InputField,
  KpiCard,
  Logo,
  Modal,
  NavItem,
  NavRail,
  SearchField,
  SegmentedControl,
  StatusChip,
  Tabs,
  TextareaField,
  useToast,
  type StatusKind,
} from "..";
import { useTheme } from "../theme/ThemeProvider";

const STATUS_KINDS: StatusKind[] = ["ok", "info", "part", "warn", "bad", "neu"];

interface DemoRow {
  id: string;
  member: string;
  service: string;
  status: StatusKind;
}
const DEMO_ROWS: DemoRow[] = [
  { id: "AUTH-2026-0K7F2", member: "MRS-M-4820117", service: "MRI — Lumbar spine", status: "info" },
  { id: "AUTH-2026-0K7G9", member: "MRS-M-3391044", service: "CT — Brain w/ contrast", status: "warn" },
  { id: "AUTH-2026-0K7H1", member: "MRS-M-7740233", service: "Trastuzumab — 6 cycles", status: "ok" },
];

function Section({ id, title, children }: { id: string; title: string; children: React.ReactNode }) {
  return (
    <section aria-labelledby={`${id}-h`} style={{ marginBottom: "var(--sp8)" }}>
      <h2 id={`${id}-h`} style={{ fontSize: "var(--fs-title-2)", marginBottom: "var(--sp4)" }}>
        {title}
      </h2>
      <Card style={{ padding: "var(--sp5)" }}>{children}</Card>
    </section>
  );
}

const Row = ({ children }: { children: React.ReactNode }) => (
  <div style={{ display: "flex", gap: "var(--sp3)", flexWrap: "wrap", alignItems: "center" }}>{children}</div>
);

export function Gallery() {
  const { t } = useTranslation();
  const { theme, lang, toggleTheme, toggleLang } = useTheme();
  const { toast } = useToast();
  const [seg, setSeg] = useState("all");
  const [tab, setTab] = useState("soap");
  const [selected, setSelected] = useState<string | null>(DEMO_ROWS[0].id);
  const [modalOpen, setModalOpen] = useState(false);
  const [fieldError, setFieldError] = useState(false);

  const navItems: NavItem[] = [
    { key: "reception", group: t("ds.sec_nav"), label: "Reception · Eligibility", icon: <Icon name="user" /> },
    { key: "doctor", group: "Clinical", label: "Doctor · Consultation", icon: <Icon name="doc" /> },
    { key: "approvals", group: "Approvals", label: "Approval worklist", icon: <Icon name="check2" /> },
    { key: "dashboard", group: "Insights", label: "Executive dashboard", icon: <Icon name="chart" /> },
  ];
  const [nav, setNav] = useState("reception");

  const columns: Column<DemoRow>[] = [
    { key: "id", header: "Authorization", cell: (r) => <span className="mono">{r.id}</span>, sortable: true },
    { key: "member", header: "Beneficiary", cell: (r) => <b>{r.member}</b> },
    { key: "service", header: "Service", cell: (r) => r.service },
    {
      key: "status",
      header: "Status",
      cell: (r) => <StatusChip kind={r.status} label={t(`status.${r.status}`)} />,
    },
  ];

  return (
    <div style={{ maxWidth: 1000, margin: "0 auto", padding: "var(--sp6)" }}>
      <header
        className="mrs-glass"
        style={{
          display: "flex",
          alignItems: "center",
          gap: "var(--sp4)",
          padding: "var(--sp4) var(--sp5)",
          marginBottom: "var(--sp6)",
        }}
      >
        <Logo variant="mark" wordmark="HBMP" />
        <div style={{ flex: 1 }} />
        <Button variant="ghost" onClick={toggleLang} aria-label={t("ds.language")}>
          {lang === "en" ? "ع" : "EN"}
        </Button>
        <Button variant="ghost" leadingIcon={<Icon name="moon" />} onClick={toggleTheme} aria-label={t("ds.theme")}>
          {theme === "dark" ? t("ds.light") : t("ds.dark")}
        </Button>
      </header>

      <div style={{ marginBottom: "var(--sp8)" }}>
        <h1 style={{ fontSize: "var(--fs-title-1)" }}>{t("ds.galleryTitle")}</h1>
        <p className="muted">{t("ds.gallerySub")}</p>
      </div>

      <Section id="logo" title={t("ds.sec_logo")}>
        <Row>
          <Logo variant="lockup" height={64} />
          <Logo variant="mark" wordmark="HBMP" />
        </Row>
      </Section>

      <Section id="buttons" title={t("ds.sec_buttons")}>
        <Row>
          <Button variant="primary" leadingIcon={<Icon name="plus" />}>
            {t("ds.primary")}
          </Button>
          <Button variant="secondary">{t("ds.secondary")}</Button>
          <Button variant="ghost">{t("ds.ghost")}</Button>
          <Button variant="danger" leadingIcon={<Icon name="cross" />}>
            {t("ds.danger")}
          </Button>
          <Button variant="primary" loading>
            {t("ds.loading")}
          </Button>
          <Button variant="secondary" disabled>
            {t("ds.secondary")}
          </Button>
          <Button variant="secondary" size="sm">
            sm
          </Button>
          <Button variant="secondary" size="lg">
            lg
          </Button>
        </Row>
      </Section>

      <Section id="status" title={t("ds.sec_status")}>
        <Row>
          {STATUS_KINDS.map((k) => (
            <StatusChip key={k} kind={k} label={t(`status.${k}`)} />
          ))}
        </Row>
      </Section>

      <Section id="fields" title={t("ds.sec_fields")}>
        <div style={{ display: "grid", gap: "var(--sp4)", maxWidth: 420 }}>
          <SearchField aria-label={t("ds.search")} placeholder={t("ds.search")} />
          <InputField
            label={t("ds.fieldLabel")}
            help={t("ds.fieldHelp")}
            error={fieldError ? t("ds.fieldError") : undefined}
          />
          <TextareaField label="Note" />
          <Button variant="secondary" onClick={() => setFieldError((v) => !v)}>
            Toggle error state
          </Button>
        </div>
      </Section>

      <Section id="seg" title={t("ds.sec_seg")}>
        <SegmentedControl
          aria-label="Filter"
          value={seg}
          onChange={setSeg}
          segments={[
            { value: "all", label: "All" },
            { value: "review", label: "Under review" },
            { value: "emergency", label: "Emergency" },
          ]}
        />
      </Section>

      <Section id="tabs" title={t("ds.sec_tabs")}>
        <Tabs
          aria-label="Encounter"
          value={tab}
          onValueChange={setTab}
          items={[
            { value: "soap", label: "SOAP", content: <p>SOAP note fields…</p> },
            { value: "vitals", label: "Vitals", content: <p>Vitals grid…</p> },
            { value: "orders", label: "Orders", content: <p>Orders & prescriptions…</p> },
          ]}
        />
      </Section>

      <Section id="table" title={t("ds.sec_table")}>
        <div style={{ overflow: "hidden", borderRadius: "var(--r-md)" }}>
          <DataTable
            caption="Approval worklist demo"
            columns={columns}
            rows={DEMO_ROWS}
            rowKey={(r) => r.id}
            interactive
            selectedKey={selected}
            onSelect={(r) => setSelected(r.id)}
          />
        </div>
        <div style={{ marginTop: "var(--sp4)", display: "grid", gap: "var(--sp3)" }}>
          <DataTable caption="Empty demo" columns={columns} rows={[]} rowKey={(r) => r.id} emptyLabel="No requests in queue" />
          <DataTable caption="Loading demo" columns={columns} rows={[]} rowKey={(r) => r.id} loading />
          <DataTable caption="Error demo" columns={columns} rows={[]} rowKey={(r) => r.id} error="Failed to load worklist." />
        </div>
      </Section>

      <Section id="kpi" title={t("ds.sec_kpi")}>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill,minmax(200px,1fr))", gap: "var(--sp4)" }}>
          <KpiCard label="Visits today" value="148" delta="+12%" direction="up" />
          <KpiCard label="Avg approval TAT" value="6.2h" delta="−0.8h" direction="down" />
          <KpiCard label="Pending approvals" value="4" />
          {/* A tone marks the SUBJECT of the figure, so it holds at zero as well as at forty. */}
          <KpiCard label="No-shows" value="0" tone="bad" />
        </div>
      </Section>

      <Section id="nav" title={t("ds.sec_nav")}>
        <div style={{ maxWidth: 250, border: "1px solid var(--border)", borderRadius: "var(--r-md)", overflow: "hidden" }}>
          <NavRail aria-label="Screens" items={navItems} current={nav} onNavigate={setNav} />
        </div>
      </Section>

      <Section id="modal" title={t("ds.sec_modal")}>
        <Row>
          <Button variant="primary" onClick={() => setModalOpen(true)}>
            {t("ds.openModal")}
          </Button>
          <Button variant="secondary" onClick={() => toast("Encounter saved")}>
            {t("ds.showToast")}
          </Button>
        </Row>
        <div style={{ marginTop: "var(--sp4)", display: "grid", gap: "var(--sp3)", maxWidth: 480 }}>
          <InlineAlert tone="ok">Decision recorded and audited.</InlineAlert>
          <InlineAlert tone="bad">A rationale is required to reject or request info.</InlineAlert>
        </div>
        <Modal
          open={modalOpen}
          onOpenChange={setModalOpen}
          title="Confirm decision"
          description="This action is audited."
          footer={
            <>
              <Button variant="ghost" onClick={() => setModalOpen(false)}>
                {t("ds.cancel")}
              </Button>
              <Button
                variant="primary"
                onClick={() => {
                  setModalOpen(false);
                  toast("Decision recorded");
                }}
              >
                {t("ds.save")}
              </Button>
            </>
          }
        >
          <p style={{ margin: 0 }}>Record this authorization decision? A clinical rationale has been captured.</p>
        </Modal>
      </Section>
    </div>
  );
}
