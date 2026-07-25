import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { I18nextProvider } from "react-i18next";
import "./styles/index.css";
import { initI18n } from "./i18n";
import { ThemeProvider } from "./theme/ThemeProvider";
import { ToastProvider } from "./components/Toast";
import { Gallery } from "./gallery/Gallery";

const i18n = initI18n();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <I18nextProvider i18n={i18n}>
      <ThemeProvider onLangChange={(l) => void i18n.changeLanguage(l)}>
        <ToastProvider>
          <a className="skip" href="#gallery-main">
            Skip to content
          </a>
          <main id="gallery-main">
            <Gallery />
          </main>
        </ToastProvider>
      </ThemeProvider>
    </I18nextProvider>
  </StrictMode>,
);
