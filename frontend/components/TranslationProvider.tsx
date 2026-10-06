"use client";
import React, { createContext, useContext, useState, useEffect, ReactNode } from "react";
import { translations, Locale } from "../lib/translations";

type TranslationContextType = {
  locale: Locale;
  setLocale: (locale: Locale) => void;
  t: (key: keyof typeof translations.en) => string;
};

const TranslationContext = createContext<TranslationContextType | undefined>(undefined);

export function TranslationProvider({ children }: { children: ReactNode }) {
  const [locale, setLocale] = useState<Locale>("en");

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
    window.localStorage.setItem("edunexus.locale", locale);
  }, [locale]);

  const t = (key: keyof typeof translations.en) => {
    return translations[locale][key] || translations.en[key] || key;
  };

  return (
    <TranslationContext.Provider value={{ locale, setLocale, t }}>
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
