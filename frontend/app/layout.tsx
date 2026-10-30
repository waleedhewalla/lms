import type { Metadata, Viewport } from "next";
import "./globals.css";
import "./landing.css";
import { AppLayout } from "../components/AppLayout";
import { RegisterServiceWorker } from "../components/RegisterServiceWorker";
import { TranslationProvider } from "../components/TranslationProvider";

export const metadata: Metadata = {
  title: "EduNexus — the institutional operating system for education",
  description: "Correspondence, approvals, committees and boards, policies, accreditation, strategy and records for universities and schools, in Arabic and English.",
};

export const viewport: Viewport = { themeColor: "#0b1220" };

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en">
      <head>
        {/* Runtime config (API URL, identity provider) must load before the app hydrates. */}
        {/* eslint-disable-next-line @next/next/no-sync-scripts */}
        <script src="/runtime-config.js" />
      </head>
      <body>
        <RegisterServiceWorker />
        <TranslationProvider>
          <AppLayout>
            {children}
          </AppLayout>
        </TranslationProvider>
      </body>
    </html>
  );
}