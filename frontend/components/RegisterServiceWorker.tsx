"use client";
import { useEffect } from "react";

/** Registers the service worker in production builds only (in development it would serve stale code). */
export function RegisterServiceWorker() {
  useEffect(() => {
    if (process.env.NODE_ENV !== "production" || !("serviceWorker" in navigator)) return;
    navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch((e) => console.warn("Service worker not registered", e));
  }, []);
  return null;
}
