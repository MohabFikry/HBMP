import { BrowserRouter } from "react-router-dom";
import { ThemeProvider, ToastProvider, useTheme } from "@mersal/design-system";
import { AuthProvider } from "./auth/AuthProvider";
import { ApiProvider } from "./api/ApiProvider";
import { AppRouter } from "./routing/AppRouter";
import type { AuthClient } from "./auth/authClient";
import type { ApiClient } from "./api/client";
import type { ReactNode } from "react";

/** Skip link lives outside the router so it's the first focusable element on every screen. */
function SkipLink() {
  const { lang } = useTheme();
  return (
    <a className="skip" href="#main">
      {lang === "ar" ? "تخطٍّ إلى المحتوى" : "Skip to content"}
    </a>
  );
}

/**
 * Provider stack WITHOUT a router (Theme → Toast → Auth). Exported so tests can wrap the router of their
 * choice (MemoryRouter) around <AppRouter/>. `authClient` is injectable so tests and the real OIDC client
 * can substitute the dev stub.
 */
export function AppProviders({
  authClient,
  apiClient,
  children,
}: {
  authClient?: AuthClient;
  apiClient?: ApiClient;
  children: ReactNode;
}) {
  return (
    <ThemeProvider>
      <ToastProvider>
        <SkipLink />
        <ApiProvider client={apiClient}>
          <AuthProvider client={authClient}>{children}</AuthProvider>
        </ApiProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

/** Full app for the browser entry point. */
export function App({ authClient, apiClient }: { authClient?: AuthClient; apiClient?: ApiClient } = {}) {
  return (
    <AppProviders authClient={authClient} apiClient={apiClient}>
      <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <AppRouter />
      </BrowserRouter>
    </AppProviders>
  );
}
