import type { Metadata } from "next";
import "./globals.css";
import { AppLayout } from "../components/AppLayout";
import { TranslationProvider } from "../components/TranslationProvider";

export const metadata: Metadata = {
  title: "EduNexus OS V2",
  description: "Institutional operating system for education",
};

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
        <TranslationProvider>
          <AppLayout>
            {children}
          </AppLayout>
        </TranslationProvider>
      </body>
    </html>
  );
}