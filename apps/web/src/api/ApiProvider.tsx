import { createContext, useContext, useMemo, type ReactNode } from "react";
import type { ApiClient } from "./client";
import { HttpApiClient } from "./HttpApiClient";
import { FIXTURES } from "@dev/fixtures";
import { LIVE } from "../config";

const ApiContext = createContext<ApiClient | null>(null);

/**
 * Provides the {@link ApiClient} to the flagship screens. Defaults to the dev fixture client (bilingual,
 * contract-valid, no PHI); tests inject their own client to drive loading/empty/error/replay, and the browser
 * entry swaps in {@link HttpApiClient} once the services are reachable.
 *
 * The fixture client is reached through `@dev/fixtures`, which a live build resolves to a stub — so a
 * production bundle does not contain it at all. See `src/dev/fixtures.ts`.
 */
export function ApiProvider({ client, children }: { client?: ApiClient; children: ReactNode }) {
  const value = useMemo(
    () => client ?? (LIVE ? new HttpApiClient() : FIXTURES.createApi()),
    [client],
  );
  return <ApiContext.Provider value={value}>{children}</ApiContext.Provider>;
}

export function useApi(): ApiClient {
  const ctx = useContext(ApiContext);
  if (!ctx) throw new Error("useApi must be used within <ApiProvider>");
  return ctx;
}
