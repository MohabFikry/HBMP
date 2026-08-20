import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { NurseResults } from "../src/screens/NursePortal";

/**
 * 32.6 — the nurse's results inbox.
 *
 * <p>The rail said "Results Inbox", the permission was `results.inbox`, and the screen rendered the heart
 * rate and temperature the same nurse had typed on the other tab. Design 11 §3.2 grants nurses
 * <code>lab_result R🟠(TR)</code>; the read existed on paper and had no door.</p>
 */

function renderScreen(api: ApiClient) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <NurseResults />
      </MemoryRouter>
    </AppProviders>,
  );
}

async function pickFirstPatient() {
  const user = userEvent.setup();
  const rows = await screen.findAllByRole("button");
  await user.click(rows[0]);
  return user;
}

describe("the nurse's results inbox", () => {
  it("asks for investigation results, not for vitals", async () => {
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const profile = vi.fn(new DevApiClient({ latencyMs: 0 }).patientProfile.bind(new DevApiClient({ latencyMs: 0 })));
    (api as { patientProfile: unknown }).patientProfile = profile;
    const encounter = vi.fn();
    (api as { getEncounter: unknown }).getEncounter = encounter;

    renderScreen(api);
    await pickFirstPatient();

    // THE assertion: what the screen asked the server for. It used to ask for the encounter and show its
    // vitals, which is a different promise from the one on the rail.
    expect(profile).toHaveBeenCalled();
    expect(profile.mock.calls[0][1]).toEqual(["investigations"]);
    expect(encounter).not.toHaveBeenCalled();
  });

  it("says a withheld section is withheld rather than showing no results", async () => {
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    // The composer omits a section the caller may not read. Rendering that as an empty table would tell a
    // nurse this patient has no investigations when the truth is that she may not see them.
    (api as { patientProfile: unknown }).patientProfile = async () => ({
      beneficiaryId: "ben-1", servedAt: new Date().toISOString(), sections: [],
    });

    renderScreen(api);
    await pickFirstPatient();

    expect(await screen.findByText(/not part of what your role may read/)).toBeInTheDocument();
  });
});
