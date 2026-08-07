import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { loginOriginsAgree } from "../src/config";

/**
 * Phase 28.2 — the SPA, the API and the issuer share ONE origin (ADR-0036 §4).
 *
 * Two different subjects here, deliberately. `loginOriginsAgree` is the rule; the files below are the places
 * the rule can be broken. `tools/ci/check-login-origin.py` guards the same files from the other side — this
 * suite is what fails on a developer's machine before CI ever sees it.
 *
 * The failure being prevented has no useful symptom. The issuer's Identity cookies are `SameSite=Strict`, so
 * a cross-origin login POST has its session cookie dropped by the BROWSER: the sign-in reports success and
 * the authorize that follows reports `login_required`. Nothing logs. The user is told their password is wrong.
 */
describe("one origin for the app, the API and the issuer", () => {
  const APP = "http://localhost:5173";

  it("accepts relative values, which mean 'this origin' by definition", () => {
    expect(loginOriginsAgree("", "http://localhost:5173/", APP)).toBe(true);
    expect(loginOriginsAgree("/", "http://localhost:5173/", APP)).toBe(true);
  });

  it("accepts an absolute value that names the app's own origin", () => {
    expect(loginOriginsAgree("http://localhost:5173", "http://localhost:5173/", APP)).toBe(true);
  });

  it("rejects the pre-28.2 arrangement", () => {
    // Exactly what shipped for two years: the app on :5173 and the issuer on :8090. This is the assertion
    // that would have caught it, and its absence is why nothing did.
    expect(loginOriginsAgree("http://localhost:8090", "http://localhost:5173/", APP)).toBe(false);
  });

  it("rejects a redirect target on a different origin from the app", () => {
    expect(loginOriginsAgree("", "http://elsewhere.example/", APP)).toBe(false);
  });

  it("treats a differing port as a differing origin", () => {
    // Same host, same scheme. Origins are (scheme, host, port) and the browser's cookie rules agree — a
    // comparison that only looked at the hostname would pass the exact defect above.
    expect(loginOriginsAgree("http://localhost:8000", "http://localhost:5173/", APP)).toBe(false);
  });

  it("treats a differing scheme as a differing origin", () => {
    expect(loginOriginsAgree("https://localhost:5173", "http://localhost:5173/", APP)).toBe(false);
  });

  // ---- the files where the rule is actually set ------------------------------------------------------

  const read = (p: string) => readFileSync(resolve(__dirname, "..", p), "utf-8");

  it("the shipped .env.example is same-origin and no longer points at Keycloak", () => {
    const env = read(".env.example");
    // SETTINGS only, not prose. The first version of this asserted the word "keycloak" never appeared, and
    // failed on the comment recording that the file used to say it — an assertion that would have forced the
    // deletion of the note explaining the fix. What must not survive is a configured VALUE.
    const settings = env.split("\n").filter((l) => !l.trimStart().startsWith("#") && l.includes("="));
    expect(settings.some((l) => /keycloak|realms\//i.test(l))).toBe(false);
    expect(settings).toContain("VITE_API_BASE=/api/v1");
    expect(settings).toContain("VITE_OIDC_AUTHORITY=");
  });

  it("compose builds the bundle with same-origin values", () => {
    const compose = readFileSync(
      resolve(__dirname, "..", "..", "..", "infra", "compose", "compose.yaml"), "utf-8");
    expect(compose).toMatch(/VITE_API_BASE: "\/api\/v1"/);
    expect(compose).toMatch(/VITE_OIDC_AUTHORITY: ""/);
    // The one value that must stay absolute — the issuer compares redirect_uri to the registered client
    // byte-for-byte, so a relative one can never match.
    expect(compose).toMatch(/VITE_OIDC_REDIRECT: "http:\/\/localhost:5173\/"/);
  });

  it("the dev server proxies the gateway prefixes, so development is not the one cross-origin environment", () => {
    // A dev/prod split in authentication TOPOLOGY is the split most likely to be found in production.
    const vite = read("vite.config.ts");
    for (const prefix of ["/api", "/connect", "/identity", "/.well-known"]) {
      expect(vite).toContain(`"${prefix}"`);
    }
  });
});

/**
 * The Content-Security-Policy the deployed image serves (ADR-0036 §8.1).
 *
 * Asserted against the Dockerfile rather than a running container because it is the artefact that decides it,
 * and because a policy is easy to weaken in a way that still looks like a policy. `script-src 'self'` is the
 * directive the SPA login depends on: after that change a password is typed into this origin, so an injected
 * script could keylog it rather than having to forge a login form first.
 */
describe("the web image's Content-Security-Policy", () => {
  const dockerfile = readFileSync(resolve(__dirname, "..", "Dockerfile"), "utf-8");
  const policy = /CSP_POLICY="([^"]+)"/.exec(dockerfile)?.[1] ?? "";

  it("is set at all", () => {
    expect(policy).not.toBe("");
  });

  it("does not allow inline or eval'd SCRIPT", () => {
    // The whole point. 'unsafe-inline' here would make the policy decorative for the threat it exists to
    // reduce, while still reading as a Content-Security-Policy in every audit.
    const scriptSrc = /script-src ([^;]+)/.exec(policy)?.[1] ?? "";
    expect(scriptSrc).toContain("'self'");
    expect(scriptSrc).not.toContain("unsafe-inline");
    expect(scriptSrc).not.toContain("unsafe-eval");
    expect(scriptSrc).not.toContain("*");
  });

  it("denies framing, base-uri and plugins outright", () => {
    expect(policy).toContain("frame-ancestors 'none'");
    expect(policy).toContain("base-uri 'none'");
    expect(policy).toContain("object-src 'none'");
  });

  it("keeps blob: for images and frames, because previews are streams and not bearer links", () => {
    // Documents and member photos are fetched as authorized, audited streams and rendered from object URLs.
    // Dropping blob: would break every preview — and it would break as "document unavailable", which is a
    // lie about the document rather than about the policy.
    expect(/img-src [^;]*blob:/.test(policy)).toBe(true);
    expect(/frame-src [^;]*blob:/.test(policy)).toBe(true);
  });

  it("allows inline STYLE, and only style", () => {
    // Radix's dialog pulls in react-remove-scroll, which injects a <style> element to lock the body. This is
    // a real concession and is recorded as one: it is not the vector script-src guards.
    expect(/style-src [^;]*'unsafe-inline'/.test(policy)).toBe(true);
  });
});
