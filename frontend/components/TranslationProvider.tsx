"use client";
import React, { createContext, useContext, useState, useEffect, ReactNode } from "react";
import { translations, Locale } from "../lib/translations";
import { setUiLocale } from "../lib/dates";

type TranslationContextType = {
  locale: Locale;
  setLocale: (locale: Locale) => void;
  t: (key: keyof typeof translations.en) => string;
};

const TranslationContext = createContext<TranslationContextType | undefined>(undefined);

export function TranslationProvider({ children }: { children: ReactNode }) {
  const [locale, setLocale] = useState<Locale>("en");
  setUiLocale(locale); // date helpers follow the language synchronously (see lib/dates.ts)

  // Load initial locale from localStorage on mount
  useEffect(() => {
    const saved = window.localStorage.getItem("edunexus.locale") as Locale;
    if (saved === "ar" || saved === "en") {
      setLocale(saved);
    }
  }, []);

  useEffect(() => {
    document.documentElement.dir = locale === "ar" ? "rtl" : "ltr";
    document.documentElement.lang = locale;
  }, [locale]);

  // Persist only on an explicit change. Persisting from an effect wrote the "en" default over the saved
  // language on first mount, before the saved value was applied, so a quick reload could lose Arabic.
  const changeLocale = (next: Locale) => {
    setLocale(next);
    try { window.localStorage.setItem("edunexus.locale", next); } catch { /* storage unavailable */ }
  };

  const t = (key: keyof typeof translations.en) => {
    return translations[locale][key] || translations.en[key] || key;
  };

  return (
    <TranslationContext.Provider value={{ locale, setLocale: changeLocale, t }}>
      {children}
    </TranslationContext.Provider>
  );
}

export function useTranslation() {
  const context = useContext(TranslationContext);
  if (!context) {
    throw new Error("useTranslation must be used within a TranslationProvider");
  }
  return context;
}
