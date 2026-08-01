import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { PolicyApi, PolicyDocumentView } from "../src/api/policyApi";
import { BeneficiaryDocuments } from "../src/screens/BeneficiaryDocuments";
import { seedSession } from "./helpers";

/**
 * The member's documents.
 *
 * The defects these pin are the ones the panel exists to prevent: a filed document whose upload date and
 * uploader are not on screen (so nobody can tell which of three scans is current), a "view" that navigates
 * away from the record, a locked document rendered as an absent button (which reads as a broken screen), and
 * — the one with teeth — a LOOK and a TAKE recorded as the same disclosure.
 */

const ENROLLMENT = "33333333-3333-3333-3333-333333333333";

function doc(over: Partial<PolicyDocumentView> = {}): PolicyDocumentView {
  return {
    linkId: "link-1", scope: "Member", scopeRef: ENROLLMENT, documentId: "doc-1", versionNo: 1,
    documentClass: "CardCopy", visibilityClass: "Administrative",
    title: "unhcr-card-front.jpg", uploadedByUsername: "a.hassan", uploadedByDisplay: "A. Hassan",
    uploadedAt: "2026-02-12T09:30:00Z", status: "Active", expired: false, canDownload: true,
    ...over,
  } as PolicyDocumentView;
}

function stub(over: Partial<PolicyApi> = {}): PolicyApi {
  return {
    documents: async () => [doc()],
    documentDownloadUrl: async () => ({ url: "https://minio.example/blob/abc.jpg?sig=x" }),
    attachDocument: async () => doc(),
    ...over,
  } as unknown as PolicyApi;
}

function renderPanel(api: PolicyApi) {
  seedSession("beneficiary_mgmt");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <MemoryRouter>
        <BeneficiaryDocuments api={api} enrollmentId={ENROLLMENT} />
      </MemoryRouter>
    </AppProviders>,
  );
}

afterEach(() => cleanup());

/** Filing a document is a dialog now — the tab is the list. Every form assertion opens it first. */
async function openFileDialog() {
  await userEvent.click(screen.getByRole("button", { name: /add document/i }));
  return screen.findByRole("dialog");
}

describe("Beneficiary documents", () => {
  it("shows the file name, the upload date and who uploaded it", async () => {
    renderPanel(stub());

    expect(await screen.findByText("unhcr-card-front.jpg")).toBeInTheDocument();
    // Three scans of the same card are indistinguishable without these two facts.
    expect(screen.getByText(/uploaded by A\. Hassan/i)).toBeInTheDocument();
    expect(screen.getByText(/2026/)).toBeInTheDocument();
  });

  it("offers the operator's word for the document type, not the server's class name", async () => {
    renderPanel(stub());

    await screen.findByText("unhcr-card-front.jpg");
    // The row is labelled "Card copy"; `CardCopy` is an implementation detail of the classification rules.
    expect(screen.getByText(/^card copy$/i)).toBeInTheDocument();
  });

  it("records a view and a download as DIFFERENT disclosures", async () => {
    const documentDownloadUrl = vi.fn().mockResolvedValue({ url: "https://minio.example/blob/abc.jpg?sig=x" });
    renderPanel(stub({ documentDownloadUrl } as Partial<PolicyApi>));
    await screen.findByText("unhcr-card-front.jpg");

    await userEvent.click(screen.getByRole("button", { name: /view — unhcr-card-front\.jpg/i }));
    expect(documentDownloadUrl).toHaveBeenCalledWith("link-1", "preview");

    // Both are disclosures; they are not the same one, and a year later the audit has to be able to say which.
    await userEvent.click(screen.getByRole("button", { name: /close/i }));
    await userEvent.click(screen.getByRole("button", { name: /download — unhcr-card-front\.jpg/i }));
    expect(documentDownloadUrl).toHaveBeenCalledWith("link-1", "download");
  });

  it("opens the document in place rather than navigating away from the record", async () => {
    renderPanel(stub());
    await screen.findByText("unhcr-card-front.jpg");

    await userEvent.click(screen.getByRole("button", { name: /view — unhcr-card-front\.jpg/i }));

    const dialog = await screen.findByRole("dialog");
    expect(dialog).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "unhcr-card-front.jpg" })).toBeInTheDocument();
  });

  it("names a locked document instead of rendering nothing where the buttons would be", async () => {
    renderPanel(stub({ documents: async () => [doc({ canDownload: false })] } as Partial<PolicyApi>));
    await screen.findByText("unhcr-card-front.jpg");

    // An empty cell reads as a broken screen; the rule is the useful thing to say — and it is said ON the
    // row, not in a `title` that needs a hover and does not exist on touch. An officer who filed the
    // paperwork themselves and then cannot open it deserves the reason without having to go looking.
    expect(screen.getByText(/^locked$/i)).toBeInTheDocument();
    expect(screen.getByText(/carries a clinical floor/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /view —/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /download —/i })).not.toBeInTheDocument();
  });

  it("opens the tab on the documents, not on an empty upload form", async () => {
    renderPanel(stub());
    await screen.findByText("unhcr-card-front.jpg");

    // The form is behind the + button. Reading is the common case and it used to sit under four controls.
    expect(screen.queryByRole("combobox", { name: /document type/i })).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/^file/i)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /add document/i })).toBeInTheDocument();

    await openFileDialog();
    expect(screen.getByRole("combobox", { name: /document type/i })).toBeInTheDocument();
  });

  it("lists the newest document first whatever order the server returned", async () => {
    renderPanel(
      stub({
        documents: async () => [
          doc({ linkId: "old", title: "old-scan.jpg", uploadedAt: "2026-01-02T09:00:00Z" }),
          doc({ linkId: "new", title: "new-scan.jpg", uploadedAt: "2026-05-02T09:00:00Z" }),
        ],
      } as Partial<PolicyApi>),
    );

    // Three scans of the same card, and the current one has to be the one at the top.
    const names = (await screen.findAllByText(/-scan\.jpg$/)).map((n) => n.textContent);
    expect(names).toEqual(["new-scan.jpg", "old-scan.jpg"]);
  });

  it("refuses an upload with no type and no file, at the fields", async () => {
    const attachDocument = vi.fn();
    renderPanel(stub({ attachDocument } as Partial<PolicyApi>));
    await screen.findByText("unhcr-card-front.jpg");
    await openFileDialog();

    await userEvent.click(screen.getByRole("button", { name: /^upload$/i }));

    expect(await screen.findByText(/choose a document type/i)).toBeInTheDocument();
    expect(screen.getByText(/choose a file/i)).toBeInTheDocument();
    expect(attachDocument).not.toHaveBeenCalled();
  });

  it("states the photo consent rule before the upload is attempted, not as a 422", async () => {
    renderPanel(stub());
    await screen.findByText("unhcr-card-front.jpg");
    await openFileDialog();

    await userEvent.click(screen.getByRole("combobox", { name: /document type/i }));
    await userEvent.click(await screen.findByRole("option", { name: /personal photo/i }));

    // The server enforces it either way; saying so here turns a refusal into something the operator could
    // have known. It also names where the photo ends up, which is the point of choosing it.
    expect(await screen.findByText(/consent covering photography/i)).toBeInTheDocument();
    expect(screen.getByText(/becomes the member's picture on their file/i)).toBeInTheDocument();
  });

  it("files an upload under the class the chosen type maps to", async () => {
    const attachDocument = vi.fn().mockResolvedValue(doc());
    renderPanel(stub({ attachDocument } as Partial<PolicyApi>));
    await screen.findByText("unhcr-card-front.jpg");
    await openFileDialog();

    await userEvent.click(screen.getByRole("combobox", { name: /document type/i }));
    await userEvent.click(await screen.findByRole("option", { name: /^investigations$/i }));
    await userEvent.type(screen.getByLabelText(/^title/i), "CBC 12 Feb");
    await userEvent.upload(
      screen.getByLabelText(/^file/i),
      new File(["x"], "cbc.pdf", { type: "application/pdf" }),
    );
    await userEvent.click(screen.getByRole("button", { name: /^upload$/i }));

    // "Investigations" is the operator's word; LabResult is what carries the clinical floor on the server.
    expect(attachDocument).toHaveBeenCalledWith(
      "enrollments",
      ENROLLMENT,
      expect.any(File),
      expect.objectContaining({ documentClass: "LabResult", title: "CBC 12 Feb" }),
    );
  });
});
