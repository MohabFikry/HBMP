import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import type { Lang, Theme } from "../tokens/tokens";

export interface ThemeContextValue {
  theme: Theme;
  lang: Lang;
  dir: "ltr" | "rtl";
  setTheme: (t: Theme) => void;
  toggleTheme: () => void;
  setLang: (l: Lang) => void;
  toggleLang: () => void;
}

/** Exported for `useDirection`, which must be able to ask WITHOUT throwing when there is no provider —
 *  see that hook for why a primitive cannot use `useTheme()`. Screens use `useTheme()`. */
export const ThemeContext = createContext<ThemeContextValue | null>(null);

const THEME_KEY = "mersal-theme";
const LANG_KEY = "mersal-lang";

function initialTheme(): Theme {
  try {
    const saved = localStorage.getItem(THEME_KEY);
    if (saved === "light" || saved === "dark") return saved;
    if (typeof matchMedia === "function" && matchMedia("(prefers-color-scheme: dark)").matches) return "dark";
  } catch {
    /* ignore */
  }
  return "light";
}
function initialLang(): Lang {
  try {
    const saved = localStorage.getItem(LANG_KEY);
    if (saved === "en" || saved === "ar") return saved;
  } catch {
    /* ignore */
  }
  return "en";
}

/**
 * ThemeProvider — owns theme (light/dark) + language/direction (en-ltr / ar-rtl), and applies them to the
 * document root (`data-theme`, `lang`, `dir`). Preferences persist to localStorage; theme defaults to
 * prefers-color-scheme. Components mirror via logical CSS properties, so no per-component RTL branching.
 */
export function ThemeProvider({
  children,
  onLangChange,
}: {
  children: ReactNode;
  /** Optional hook so the app can switch its i18next language in lockstep. */
  onLangChange?: (lang: Lang) => void;
}) {
  const [theme, setThemeState] = useState<Theme>(initialTheme);
  const [lang, setLangState] = useState<Lang>(initialLang);
  const dir = lang === "ar" ? "rtl" : "ltr";

  useEffect(() => {
    const root = document.documentElement;
    root.dataset.theme = theme;
    root.lang = lang;
    root.dir = dir;
  }, [theme, lang, dir]);

  const setTheme = useCallback((t: Theme) => {
    setThemeState(t);
    try {
      localStorage.setItem(THEME_KEY, t);
    } catch {
      /* ignore */
    }
  }, []);

  const setLang = useCallback(
    (l: Lang) => {
      setLangState(l);
      try {
        localStorage.setItem(LANG_KEY, l);
      } catch {
        /* ignore */
      }
      onLangChange?.(l);
    },
    [onLangChange],
  );

  const value = useMemo<ThemeContextValue>(
    () => ({
      theme,
      lang,
      dir,
      setTheme,
      toggleTheme: () => setTheme(theme === "dark" ? "light" : "dark"),
      setLang,
      toggleLang: () => setLang(lang === "ar" ? "en" : "ar"),
    }),
    [theme, lang, dir, setTheme, setLang],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error("useTheme must be used within <ThemeProvider>");
  return ctx;
}
