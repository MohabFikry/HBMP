import { z } from "zod";
import {
  zAdminHistoryPage, zContractAdmin, zContractTerminationResult, zCredentialWithdrawResult,
  zProviderCredential, zProviderDetail, zProviderLocationAdmin, zProviderUser, zServiceLine,
} from "@mersal/contracts";
import type {
  AdminHistoryPage, ContractAdmin, ContractTerminationResult, ContractWrite, CredentialWithdrawResult,
  CredentialWrite, LocationWrite, ProviderCredentialView, ProviderDetail, ProviderLocationAdmin,
  ProviderUserView, ProviderWrite, ServiceLine, ServiceLineWrite,
} from "@mersal/contracts";
import { deleteRaw, getRaw, parseOr, postRaw, putRaw } from "./http";

/**
 * Phase 19.9 — the administrative half of provider-service (design 58).
 *
 * ============================================================================================================
 * WHY THIS IS A SEPARATE MODULE
 * ============================================================================================================
 * `ApiClient` carries the provider DIRECTORY — the list, a provider's live locations, its contracts — because
 * half the platform reads those: routing, booking, the tier resolver. This is the other half, the one only the
 * Network Team's four screens call, and it is twenty operations. Growing `ApiClient` by twenty methods also
 * grows `DevApiClient` by twenty stubs that nothing exercises, which is how a fixture drifts from the server it
 * is pretending to be. `policyApi` made the same split for the same reason and the payer/plan screens take it
 * as a prop, which is also what makes them testable without a network.
 *
 * ============================================================================================================
 * EVERY RESPONSE IS PARSED, NOT CAST
 * ============================================================================================================
 * `parsed` validates against the schema the type was inferred from, so contract drift is a loud
 * `ApiError("schema")` rather than a screen of blanks. That matters more here than usual: an `agreedPrice`
 * that arrives absent because the caller lacks `provider:finance` must stay absent, and a cast would let
 * `undefined` flow into a currency formatter and render as "EGP 0.00" — free, rather than withheld.
 */

const parsed = <T>(schema: z.ZodType<T>, p: Promise<unknown>): Promise<T> => p.then((d) => parseOr(schema, d));

export interface NetworkApi {
  /** Identity + readiness + what hangs off the provider. A second read, not a slice of the list: the
   *  readiness checklist and the counts are aggregates the directory cannot afford per row. */
  provider(id: string): Promise<ProviderDetail>;
  updateProvider(id: string, body: ProviderWrite): Promise<ProviderDetail>;
  providerHistory(id: string): Promise<AdminHistoryPage>;

  activateProvider(id: string, reason: string, key?: string): Promise<unknown>;
  suspendProvider(id: string, reason: string, key?: string): Promise<unknown>;
  /** First call opens a dual-controlled request and changes nothing; a call from a DIFFERENT authenticated
   *  subject approves it and terminates. The 202 on the first leg is not an error. */
  terminateProvider(id: string, reason: string, key?: string): Promise<unknown>;
  withdrawTermination(id: string, reason: string, key?: string): Promise<unknown>;

  /** Includes deactivated locations — see `zProviderLocationAdmin`. */
  locations(providerId: string): Promise<ProviderLocationAdmin[]>;
  createLocation(providerId: string, body: LocationWrite & { isPrimary: boolean }, key?: string): Promise<unknown>;
  updateLocation(providerId: string, locationId: string, body: LocationWrite): Promise<ProviderLocationAdmin>;
  makeLocationPrimary(providerId: string, locationId: string, key?: string): Promise<ProviderLocationAdmin>;
  deactivateLocation(providerId: string, locationId: string, reason: string, key?: string): Promise<ProviderLocationAdmin>;
  reactivateLocation(providerId: string, locationId: string, reason: string, key?: string): Promise<ProviderLocationAdmin>;
  locationHistory(providerId: string, locationId: string): Promise<AdminHistoryPage>;

  contracts(providerId: string): Promise<ContractAdmin[]>;
  createContract(providerId: string, body: ContractWrite, key?: string): Promise<unknown>;
  updateContract(contractId: string, body: ContractWrite): Promise<ContractAdmin>;
  activateContract(contractId: string, key?: string): Promise<unknown>;
  terminateContract(contractId: string, reason: string, key?: string): Promise<ContractTerminationResult>;
  contractHistory(contractId: string): Promise<AdminHistoryPage>;

  serviceLines(contractId: string): Promise<ServiceLine[]>;
  addServiceLine(contractId: string, body: ServiceLineWrite, key?: string): Promise<unknown>;
  updateServiceLine(contractId: string, lineId: string, body: { agreedPrice: number; currencyCode?: string }): Promise<ServiceLine>;
  removeServiceLine(contractId: string, lineId: string): Promise<void>;

  credentials(providerId: string): Promise<ProviderCredentialView[]>;
  addCredential(providerId: string, body: CredentialWrite, key?: string): Promise<unknown>;
  updateCredential(providerId: string, credentialId: string, body: CredentialWrite): Promise<unknown>;
  withdrawCredential(providerId: string, credentialId: string, reason: string, key?: string): Promise<CredentialWithdrawResult>;

  users(providerId: string): Promise<ProviderUserView[]>;
  provisionUser(providerId: string, body: { subjectRef: string; role: string }, key?: string): Promise<unknown>;
  revokeUser(providerId: string, userId: string, reason: string, key?: string): Promise<unknown>;
}

export function createHttpNetworkApi(): NetworkApi {
  return {
    provider: (id) => parsed(zProviderDetail, getRaw(`/providers/${id}/administration`)),
    updateProvider: (id, body) => parsed(zProviderDetail, putRaw(`/providers/${id}`, body)),
    providerHistory: (id) => parsed(zAdminHistoryPage, getRaw(`/providers/${id}/history`)),

    activateProvider: (id, reason, key) => postRaw(`/providers/${id}/activate`, { reason }, key),
    suspendProvider: (id, reason, key) => postRaw(`/providers/${id}/suspend`, { reason }, key),
    terminateProvider: (id, reason, key) => postRaw(`/providers/${id}/terminate`, { reason }, key),
    withdrawTermination: (id, reason, key) => postRaw(`/providers/${id}/terminate/withdraw`, { reason }, key),

    locations: (providerId) =>
      parsed(z.array(zProviderLocationAdmin), getRaw(`/providers/${providerId}/locations/all`)),
    createLocation: (providerId, body, key) => postRaw(`/providers/${providerId}/locations`, body, key),
    updateLocation: (providerId, locationId, body) =>
      parsed(zProviderLocationAdmin, putRaw(`/providers/${providerId}/locations/${locationId}`, body)),
    makeLocationPrimary: (providerId, locationId, key) =>
      parsed(zProviderLocationAdmin, postRaw(`/providers/${providerId}/locations/${locationId}/primary`, {}, key)),
    deactivateLocation: (providerId, locationId, reason, key) =>
      parsed(zProviderLocationAdmin, postRaw(`/providers/${providerId}/locations/${locationId}/deactivate`, { reason }, key)),
    reactivateLocation: (providerId, locationId, reason, key) =>
      parsed(zProviderLocationAdmin, postRaw(`/providers/${providerId}/locations/${locationId}/reactivate`, { reason }, key)),
    locationHistory: (providerId, locationId) =>
      parsed(zAdminHistoryPage, getRaw(`/providers/${providerId}/locations/${locationId}/history`)),

    // The contract list is the phase-2b read, reshaped: it already returns the counts and the window, and a
    // second endpoint returning the same rows in a different order is how two lists start disagreeing.
    contracts: async (providerId) => {
      const rows = (await getRaw(`/providers/${providerId}/contracts`)) as unknown[];
      return (Array.isArray(rows) ? rows : []).map((r) => {
        const c = r as Record<string, unknown>;
        return parseOr(zContractAdmin, {
          contractId: c.contractId,
          contractNo: String(c.contractNo ?? ""),
          status: String(c.status ?? ""),
          effectiveFrom: String(c.effectiveFrom ?? ""),
          effectiveTo: c.effectiveTo ?? null,
          serviceLines: Number(c.serviceLines ?? 0),
          // The list endpoint predates `inEffect`; Active-and-within-window is what it means, and the one
          // place it is decided for real is `ContractRules.InEffect` on the server. This is the list view's
          // approximation and the DETAIL view corrects it — never the other way round.
          inEffect: String(c.status ?? "") === "Active",
          statusReason: c.statusReason ?? null,
          statusActorName: c.statusActorName ?? null,
          statusChangedAt: c.statusChangedAt ?? null,
        });
      });
    },
    createContract: (providerId, body, key) => postRaw(`/providers/${providerId}/contracts`, body, key),
    updateContract: (contractId, body) => parsed(zContractAdmin, putRaw(`/contracts/${contractId}`, body)),
    activateContract: (contractId, key) => postRaw(`/contracts/${contractId}/activate`, {}, key),
    terminateContract: (contractId, reason, key) =>
      parsed(zContractTerminationResult, postRaw(`/contracts/${contractId}/terminate`, { reason }, key)),
    contractHistory: (contractId) => parsed(zAdminHistoryPage, getRaw(`/contracts/${contractId}/history`)),

    serviceLines: (contractId) => parsed(z.array(zServiceLine), getRaw(`/contracts/${contractId}/service-lines`)),
    addServiceLine: (contractId, body, key) => postRaw(`/contracts/${contractId}/service-lines`, body, key),
    updateServiceLine: (contractId, lineId, body) =>
      parsed(zServiceLine, putRaw(`/contracts/${contractId}/service-lines/${lineId}`, body)),
    removeServiceLine: async (contractId, lineId) => {
      await deleteRaw(`/contracts/${contractId}/service-lines/${lineId}`);
    },

    credentials: (providerId) => parsed(z.array(zProviderCredential), getRaw(`/providers/${providerId}/credentials`)),
    addCredential: (providerId, body, key) => postRaw(`/providers/${providerId}/credentials`, body, key),
    updateCredential: (providerId, credentialId, body) =>
      putRaw(`/providers/${providerId}/credentials/${credentialId}`, body),
    withdrawCredential: (providerId, credentialId, reason, key) =>
      parsed(zCredentialWithdrawResult, postRaw(`/providers/${providerId}/credentials/${credentialId}/withdraw`, { reason }, key)),

    users: (providerId) => parsed(z.array(zProviderUser), getRaw(`/providers/${providerId}/users`)),
    provisionUser: (providerId, body, key) => postRaw(`/providers/${providerId}/users`, body, key),
    revokeUser: (providerId, userId, reason, key) =>
      postRaw(`/providers/${providerId}/users/${userId}/revoke`, { reason }, key),
  };
}
