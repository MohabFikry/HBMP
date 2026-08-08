import { describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ApprovalEngineAdmin } from "../src/screens/ApprovalEngineAdmin";

/**
 * Authoring the approvals engine's routing and SLA rules (ADR-0035 §5.1/§5.4).
 *
 * <p>These two families were built first because they change WHO decides and BY WHEN — never WHAT is decided.
 * The worst outcome a bug here can produce is work arriving on the wrong desk, not a benefit decision made
 * without a human. Pre-auth triggers and auto-approval build on this infrastructure once it is proved.</p>
 *
 * <p>What these tests guard is the governance: a rationale is not optional, superseded versions stay visible
 * (today's rules cannot answer "why did this go there last week"), a routing rule can only name a queue
 * somebody watches, and a catch-all is warned about rather than silently allowed to swallow the queue.</p>
 */

function render(api: ApiClient = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient) {
  return renderNode(<ApprovalEngineAdmin />, api);
}

/** `SelectField` is a custom listbox, not a native <select> — open it, then pick. */
async function choose(user: ReturnType<typeof userEvent.setup>, name: RegExp, option: RegExp) {
  await user.click(await screen.findByRole("combobox", { name }));
  await user.click(await screen.findByRole("option", { name: option }));
}

describe("the engine's rules", () => {
  it("says it can change who decides and by when, but never what is decided", async () => {
    render();
    // The sentence that bounds the whole feature. A supervisor must not believe this screen can approve.
    expect(await screen.findByText(/never what is decided/i)).toBeInTheDocument();
    expect(screen.getByText(/Nothing here can approve or refuse anything/i)).toBeInTheDocument();
  });

  it("says where a request that matches nothing goes", async () => {
    render();
    // Routing must never strand work. A request nobody can see is worse than one routed imperfectly, and the
    // supervisor needs to know the floor exists before they start writing rules above it.
    expect(await screen.findByText(/matching no rule goes to "default"/i)).toBeInTheDocument();
  });

  it("keeps superseded versions on screen, dated", async () => {
    render();
    await screen.findByText(/Emergencies go to the on-call desk/i);

    // Effective dating is only useful if the closed windows are visible: the question a supervisor actually
    // asks is "why did this go there last week", and today's rules cannot answer it.
    expect(screen.getByText(/^Superseded/)).toBeInTheDocument();
    expect(screen.getByText("Initial routing.")).toBeInTheDocument();
  });

  it("renders a rule as the sentence a supervisor would say, not as JSON", async () => {
    render();
    await screen.findByText(/Emergencies go to the on-call desk/i);
    // A predicate shown as `{"priority":"Emergency"}` is a rule nobody reviews.
    expect(screen.getAllByText(/Request priority = Emergency/).length).toBeGreaterThan(0);
    expect(screen.getAllByText("→ escalation").length).toBeGreaterThan(0);
  });

  it("shows time-limit rules separately from routing ones", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("radio", { name: "Time limits" }));

    // An SLA rule cannot route and a routing rule cannot set a deadline; mixing them in one list would
    // suggest a supervisor's change to one could alter the other.
    await waitFor(() => expect(screen.getByText(/is not being treated as one/i)).toBeInTheDocument());
    expect(screen.getByText("1h")).toBeInTheDocument();
  });
});

describe("publishing a rule", () => {
  it("will not publish without a rationale", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "saveApprovalRule");
    render(api);
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("button", { name: /New rule/i }));
    await user.click(screen.getByRole("button", { name: "Publish" }));

    // A rule that silently redirected a queue for three weeks, with no account of who decided that or what
    // they were solving, is not something anybody can review afterwards.
    expect(await screen.findByText(/State why/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });

  it("warns that a rule with no conditions will swallow everything", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("button", { name: /New rule/i }));

    // Warned, not refused: a catch-all is how you give unmatched work a home. Placed above a specific rule it
    // takes everything, and the specific rule then looks live while doing nothing.
    expect(await screen.findByText(/matches EVERY request/i)).toBeInTheDocument();
  });

  it("only offers queues somebody watches", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText(/Emergencies go to the on-call desk/i);
    await user.click(screen.getByRole("button", { name: /New rule/i }));

    // A free-text queue would let a typo route work somewhere invisible, and the symptom is a quiet queue
    // rather than an error.
    await user.click(await screen.findByRole("combobox", { name: /Send to/i }));
    const options = (await screen.findAllByRole("option")).map((o) => o.textContent);
    expect(options).toContain("escalation");
    expect(options.every((o) => (o ?? "").trim() !== "")).toBe(true);
  });

  it("refuses an SLA outside its bounds", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "saveApprovalRule");
    render(api);
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("radio", { name: "Time limits" }));
    await user.click(screen.getByRole("button", { name: /New rule/i }));
    const hours = await screen.findByLabelText(/Hours allowed/i);
    await user.clear(hours);
    await user.type(hours, "0");

    // Zero would breach the moment the request arrived, which is a deadline that means nothing.
    expect(await screen.findByText(/Between 1 and 720 hours/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
    expect(spy).not.toHaveBeenCalled();
  });

  it("sends the predicate, the action and the rationale", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "saveApprovalRule");
    render(api);
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("button", { name: /New rule/i }));
    await choose(user, /Request priority/i, /^Urgent$/);
    await user.type(screen.getByLabelText(/Why this rule/i), "Urgent work needs the clinical desk.");
    await user.click(screen.getByRole("button", { name: "Publish" }));

    await waitFor(() => expect(spy).toHaveBeenCalled());
    const sent = spy.mock.calls[spy.mock.calls.length - 1][0];
    expect(sent.family).toBe("Routing");
    expect(sent.predicate.priority).toBe("Urgent");
    expect(sent.rationale).toBe("Urgent work needs the clinical desk.");
    expect(sent.action).toHaveProperty("queue");
  });

  it("has no serious or critical a11y violations", async () => {
    const user = userEvent.setup();
    const { container } = render();
    await screen.findByText(/Emergencies go to the on-call desk/i);
    await user.click(screen.getByRole("button", { name: /New rule/i }));
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

describe("pre-approval triggers", () => {
  it("says these rules only ever ADD a requirement", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText(/Emergencies go to the on-call desk/i);
    await user.click(screen.getByRole("radio", { name: "Pre-approval" }));

    // The plan's own terms are contractual. A supervisor must not believe this screen can remove one — the
    // divergence would surface months later as a denied claim nobody could trace to a config change.
    await waitFor(() =>
      expect(screen.getByText(/only ever ADD a requirement/i)).toBeInTheDocument());
    expect(screen.getByText(/nothing here can switch one off/i)).toBeInTheDocument();
  });

  it("REFUSES a pre-approval rule with no conditions, rather than warning about it", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "saveApprovalRule");
    render(api);
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("radio", { name: "Pre-approval" }));
    await user.click(screen.getByRole("button", { name: /New rule/i }));

    // Different from routing, where a catch-all is legitimate — it gives unmatched work a home. Here it would
    // put EVERY act of care on the platform behind a decision: a service outage with a benefit rationale.
    expect(await screen.findByText(/EVERY act of care/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
    expect(spy).not.toHaveBeenCalled();
  });

  it("will not publish without a reason the provider will see", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "saveApprovalRule");
    render(api);
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("radio", { name: "Pre-approval" }));
    await user.click(screen.getByRole("button", { name: /New rule/i }));
    await choose(user, /Benefit category/i, /^IMAGING$/);
    await user.type(screen.getByLabelText(/Why this rule/i), "High-cost imaging.");
    await user.click(screen.getByRole("button", { name: "Publish" }));

    // The rationale is for the audit; the REASON is for the provider who gets stopped. Two different
    // audiences, and "authorization is required" with no account of why is how a gate gets worked around.
    expect(await screen.findByText(/Say why. The provider sees this/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });

  it("sends the category, the floor and the reason", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "saveApprovalRule");
    render(api);
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("radio", { name: "Pre-approval" }));
    await user.click(screen.getByRole("button", { name: /New rule/i }));
    await choose(user, /Benefit category/i, /^IMAGING$/);
    await user.type(screen.getByLabelText(/Amount at least/i), "5000");
    await user.type(screen.getByLabelText(/Reason shown to the provider/i), "Reviewed before it is performed.");
    await user.type(screen.getByLabelText(/Why this rule/i), "High-cost imaging drove retrospective denials.");
    await user.click(screen.getByRole("button", { name: "Publish" }));

    await waitFor(() => expect(spy).toHaveBeenCalled());
    const sent = spy.mock.calls[spy.mock.calls.length - 1][0];
    expect(sent.family).toBe("Preauth");
    expect(sent.predicate.benefitCategory).toBe("IMAGING");
    expect(sent.predicate.amountAtLeast).toBe(5000);
    expect(sent.action).toEqual({ reason: "Reviewed before it is performed." });
    // No boolean anywhere in the action. There is nothing to send that could mean "stop requiring".
    expect(Object.keys(sent.action)).toEqual(["reason"]);
  });

  it("says an unknown amount does not clear the floor", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText(/Emergencies go to the on-call desk/i);
    await user.click(screen.getByRole("radio", { name: "Pre-approval" }));
    await user.click(screen.getByRole("button", { name: /New rule/i }));

    // The trap this warns about: an absent figure is not a small one, and treating it as below the threshold
    // would let exactly the requests nobody could price slip through ungated.
    expect(await screen.findByText(/an absent figure is not a small one/i)).toBeInTheDocument();
  });
});

describe("auto-approval and the kill switch", () => {
  it("shows the switch OFF for a tenant that has never touched it", async () => {
    render();
    // Not an error and not a 404. Auto-approval is opt-in and stays opt-in: a new tenant, a restored database
    // and a failed migration all produce "no row", and every one must mean nobody is paid without a human.
    expect(await screen.findByText(/OFF — every request waits for a person/i)).toBeInTheDocument();
  });

  it("says the switch edits no rule", async () => {
    render();
    // The point of a kill switch. One you can only reach by editing the thing that is misbehaving is not one.
    expect(await screen.findByText(/does not edit any rule/i)).toBeInTheDocument();
  });

  it("will not flip the switch without a reason, in either direction", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "setAutoDecision");
    render(api);
    await screen.findByText(/OFF — every request waits/i);

    await user.click(screen.getByRole("button", { name: "Turn on" }));

    // Turning it on is a decision somebody owns; turning it off in a hurry is one somebody has to explain the
    // following morning.
    expect(await screen.findByText(/State why. It is recorded/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });

  it("turns on with a reason and reports the new state", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "setAutoDecision");
    render(api);
    await screen.findByText(/OFF — every request waits/i);

    await user.type(screen.getByLabelText(/^Why$/i), "Piloting on routine consultations.");
    await user.click(screen.getByRole("button", { name: "Turn on" }));

    await waitFor(() => expect(spy).toHaveBeenCalled());
    expect(spy.mock.calls[0][0]).toEqual({ enabled: true, reason: "Piloting on routine consultations." });
    await waitFor(() =>
      expect(screen.getByText(/ON — some requests are approved without a human/i)).toBeInTheDocument());
  });

  it("says there is no auto-rejection and why", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText(/Emergencies go to the on-call desk/i);
    await user.click(screen.getByRole("radio", { name: "Auto-approval" }));

    // A supervisor must not go looking for a reject rule. A wrong auto-approval costs the payer money and a
    // human reviews the claim later; a wrong auto-rejection denies care with nobody having looked.
    await waitFor(() => expect(screen.getByText(/no auto-rejection and there will not be/i)).toBeInTheDocument());
  });

  it("REFUSES an auto-approval rule with no conditions", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "saveApprovalRule");
    render(api);
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("radio", { name: "Auto-approval" }));
    await user.click(screen.getByRole("button", { name: /New rule/i }));

    // The worst rule anybody could write: approve anything under the ceiling, with no human.
    expect(await screen.findByText(/approve ANY request under the ceiling/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
    expect(spy).not.toHaveBeenCalled();
  });

  it("refuses a ceiling above the platform maximum", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "saveApprovalRule");
    render(api);
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("radio", { name: "Auto-approval" }));
    await user.click(screen.getByRole("button", { name: /New rule/i }));
    await choose(user, /Benefit category/i, /^CONSULT$/);
    const ceiling = await screen.findByLabelText(/Approve up to/i);
    await user.clear(ceiling);
    await user.type(ceiling, "999999");

    // Without a ceiling on the ceiling, "bounded" would mean bounded by whatever the last person typed.
    expect(await screen.findByText(/Between 1 and 5000 EGP/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
    expect(spy).not.toHaveBeenCalled();
  });

  it("sends the ceiling and the reason", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "saveApprovalRule");
    render(api);
    await screen.findByText(/Emergencies go to the on-call desk/i);

    await user.click(screen.getByRole("radio", { name: "Auto-approval" }));
    await user.click(screen.getByRole("button", { name: /New rule/i }));
    await choose(user, /Benefit category/i, /^CONSULT$/);
    await user.type(screen.getByLabelText(/Reason shown to the provider/i), "Routine consults under 500.");
    await user.type(screen.getByLabelText(/Why this rule/i), "Queue depth was low-value consults.");
    await user.click(screen.getByRole("button", { name: "Publish" }));

    await waitFor(() => expect(spy).toHaveBeenCalled());
    const sent = spy.mock.calls[spy.mock.calls.length - 1][0];
    expect(sent.family).toBe("AutoApprove");
    expect(sent.action).toEqual({ maxAmountEgp: 500, reason: "Routine consults under 500." });
  });
});
