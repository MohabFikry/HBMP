import { Card, InlineAlert, useTheme } from "@mersal/design-system";
import { useAuth } from "../auth/AuthProvider";
import { portalForRole, type Section } from "../portals/catalog";
import { L } from "../i18n/strings";

/**
 * Generic section page for Phase 9.2 — renders the page header (eyebrow + title) for the section and a
 * note that the flagship interactive screen lands in 9.3. It proves the shell + permission routing work
 * end-to-end; 9.3 swaps specific sections for their wired screens.
 */
export function SectionPage({ section }: { section: Section }) {
  const { session } = useAuth();
  const { lang } = useTheme();
  if (!session?.role) return null;
  const portal = portalForRole(session.role);

  return (
    <>
      <div className="pagehead">
        <div>
          <div className="role-eyebrow">{portal.eyebrow[lang]}</div>
          <h1>{section.label[lang]}</h1>
        </div>
      </div>
      <Card style={{ padding: "var(--sp6)" }}>
        <InlineAlert tone="info">{L.sectionStub[lang]}</InlineAlert>
      </Card>
    </>
  );
}
