# 53 — The Report Nobody Could Read

> **Status:** implemented (2026-08-21).
> **Reads on:** [10](10-role-matrix.md) §3.8, [11](11-permission-matrix.md) §3.2 and §3.4,
> [17](17-api-specifications.md), [18](18-security-model.md) §2,
> [37](37-branch-scoping-and-clinical-sensitivity.md) §6,
> [45](45-encounter-and-prescription-adjustments.md) §7, [51](51-the-counters.md) §2.
> **Found by:** tracing a question — *"where is the DICOM viewer?"* — through the code rather than answering it
> from memory.

---

## 1. One route, two shapes, and a dialog made of defaults

`GET /api/v1/investigation-orders/{orderId}/lines/{lineId}/result` answered in two different kinds of thing:

| Path | Shape |
|---|---|
| Result readable | `IEnumerable<ResultResponse>` — a JSON **array** of fulfillment rows |
| Result restricted (14.7) | `RestrictedResultView` — a single **object** carrying `restricted: true` |

The client read both as an object:

```ts
const r = await getRaw(`…/result`);
if (r?.restricted === true) { /* existence-only */ }
return parseOr(zResultDetail, { value: r?.resultValue ?? r?.value ?? "—", … });
```

`r.restricted` on an array is `undefined`, so it fell to the readable branch — the right branch, by accident.
Then `r?.resultValue` on an array is also `undefined`, so **every standard result rendered as an em-dash**
against a real gateway.

It was worse than one field. `ResultResponse` is the *fulfillment row*: `FulfillmentId`, `OrderLineId`,
`ResultValue`, `ResultDocumentId`, `ResultUploadedAt`. It knows nothing about what was ordered. So `category`,
`code` and `status` were never sent at all, and the dialog rendered its three defaults — `"Result"`, `"—"`,
`"Completed"` — on every read, for every result, always. **All four fields a clinician looks at were
placeholders.**

Nothing was watching. `resultDetail` had no test, and `DevApiClient.resultDetail` returns the finished
contract object — so the fixture never exercised the mapping, and a test written against the dev client would
have passed throughout.

### 1.1 What changed

Both paths return an object and both carry `restricted`, so a client reads one field to know which it has.
The readable one is `LineResultView`, and it carries the line's own context — `Code`, `CodeSystem`,
`Category`, `Status` — which the endpoint already had in hand and simply never sent.

**And the client now refuses a shape it does not recognise** rather than defaulting through it:

```ts
if (Array.isArray(r) || r === null || typeof r !== "object") throw new ApiError("schema", …);
```

That is the durable half. The mapping's `??` fallbacks are what turned a contract break into a plausible
answer, and a plausible answer is the one failure a reader cannot detect: **a doctor cannot tell a missing
result from an em-dash.**

---

## 2. The file that was written and never read

A performing provider has been able to attach a report to a result since phase 5.3, and pass 6 wired the file
input on the upload screen because the service had accepted one for a phase while the screen sent only the
summary. The upload path is complete and careful:

1. `ResultUpload.tsx` posts `report` as multipart — accepting `.pdf,.png,.jpg,.jpeg,.webp,.tif,.tiff,.dcm`
2. orders-service checks the line was consumed *by this provider*, then calls document-service
3. document-service validates, checksums, **malware-scans fail-closed**, encrypts and stores the blob
4. `ResultDocumentId` is pinned on the fulfillment row **in the same transaction** as the routing event
5. the `OrderResultUploaded` event carries `resultDocumentId` onward

Then nothing. `grep` for `ResultDocumentId` across every service, library and the SPA returns the write, the
contract, the entity field, and two tests. **No consumer anywhere.**

And no consumer was possible, because **document-service has no read path for clinical bytes**:

| Route | Serves |
|---|---|
| `GET /beneficiaries/{id}/documents` | **metadata only** — id, type, classification, version, checksum, size, uploader |
| `POST /beneficiaries/{id}/documents` | the write |
| `GET /operational-documents/{id}/content` | bytes — from `db.OperationalDocuments`, a **different table** |

`DocumentPolicies.Read` says so in as many words: *"List/read a beneficiary's document metadata
(min-necessary — **never blob bytes**)."* A clinical document id passed to the operational route 404s. The two
things that look like a way out are not: `policyApi.documentDownloadUrl` reads `db.PolicyDocuments`, the
member/policy subsystem with its own link ids.

So a radiographer could upload a signed report or a DICOM study — scanned, encrypted, checksummed, referenced,
audited — and **no role in the platform, through any endpoint, could retrieve it.** For imaging this is the
whole result: the summary field beside it is a courtesy line, not the finding.

> There is no DICOM viewer in this platform and this document does not add one. Rendering a study in the
> browser is a product decision with real weight — a viewer that windows badly is a viewer that hides a
> finding. What was missing first is cruder than that: nobody could get the bytes out at all.

---

## 3. Where the gate has to live

The obvious fix — let document-service serve clinical content — is wrong, and the reason is worth stating.

The rule that decides who may read a result is 14.7 (design 37 §6): a non-Standard line is default-deny except
for the authoring clinician or the holder of an active, time-boxed, single-result grant, and it **overrides
the approval team's standing oversight**. That decision turns on the LINE's sensitivity and the caller's
grants — facts document-service does not have. It knows a document belongs to a beneficiary. Serving the blob
on that authority answers a question it cannot ask.

So the clinician path is `GET /investigation-orders/{orderId}/lines/{lineId}/result/report`, in
orders-service, which:

- re-applies the same 14.7 decision as the values read, refusing with `urn:hbmp:sensitive-result-restricted`
- looks the document id up from **its own fulfillment row** rather than accepting one from the caller
- fetches from document-service **forwarding the caller's own bearer**, so that service's role and tenant
  rules apply underneath rather than being stepped over by a service credential
- audits the retrieval as an **Export** at High severity — bytes leaving the platform, on the same reasoning
  the operational download states: the fourth retrieval discloses exactly as much as the first

document-service gains `GET /beneficiaries/{b}/documents/{d}/content` as the primitive underneath, gated on a
**new, narrower** action:

```
DocumentPolicies.ContentRead → doctor, medical_approval, medical_director
DocumentPolicies.Read        → + reception, beneficiary_mgmt, nurse, case_manager, org_admin
```

Seeing that a file is on hand and reading it are different disclosures. Reception legitimately sees that a
beneficiary has documents without being a person who reads radiology reports.

### 3.1 The residual, stated

A doctor holding `ContentRead` could in principle call document-service directly and bypass the 14.7 gate —
**if they had the document id.** They cannot get one: the restricted projection withholds it by design
(*"Existence metadata ONLY — never values, never a document ref"*), and `LineResultView` does not carry it
either. The id is a capability, and the only thing that hands one out is the gate itself. `hasReport` — a
boolean — is what reaches the browser.

Closing that residual completely means a service credential document-service can distinguish from a user's.
The machinery exists (`IdentityContract.ServiceClientId`, client-credentials, `ServiceScopes`) and **no
service mints one at runtime today**; every service-to-service call in this codebase forwards the caller's
bearer. Building that acquisition path — cache, refresh, secret injection through OpenBao — is its own piece
of work and is not done here.

---

## 4. What a clinician sees

The result dialog gains a download when `hasReport` is true, with a line saying what it is:

> The signed report or study the performing centre uploaded. For imaging this is usually the finding itself,
> and the summary above is not a substitute for it.

A fetch through the api client, never an `<a download href>` — an anchor sends no `Authorization` header,
which behind the gateway is a 401 the browser renders as a broken download with no message. The same
reasoning `BulkErrorReport` records.

A **403 is told apart from a failure**, because only one of them can be retried: a refusal is the sensitivity
gate, and the route back is the time-boxed access request already on the restricted card.

---

## 5. Left undone, deliberately

**No viewer.** `.dcm` downloads as bytes. See the note in §2.

**Content type and original filename are not persisted** on the clinical path. `DocumentVersion` carries blob
path, checksum, size and uploader — no media type, no name — unlike `OperationalDocument`, which stores both.
The media type does reach MinIO on the object, but `IBlobStore.GetAsync` returns a bare stream. The endpoint
therefore serves `application/octet-stream` with a name built from the doc type and the id. Sniffing a type
from the first bytes would be a guess presented as a fact; two columns and a migration would fix it properly,
and this change does not need them.

**Multiple fulfillments collapse to the latest.** A line fulfilled more than once returns its most recently
uploaded result — what a clinician opening the record means by "the result". Earlier ones remain on the
fulfillment rows and on the audit trail, and nothing reads them back.

---

## 6. How this was missed

Pass 6 added the file input to the upload screen, correctly, because the service had been accepting a report
that the screen never sent. It fixed the write and did not ask whether anything could read it — which is the
question those passes exist to ask. The result mapping in §1 sits in code pass 5 touched.

Both are the same shape as everything the seven audit passes have found, arriving one layer further in: not a
missing endpoint, but a **complete, careful, well-tested write path with no reader**. The upload is scanned,
encrypted, transactional, evented and audited. Every property it was built for holds. And the finding it
stores has, until now, been unreachable by the person who ordered it.
