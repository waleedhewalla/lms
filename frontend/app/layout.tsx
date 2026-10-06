import type { Metadata } from "next";
import "./globals.css";
import { AppLayout } from "../components/AppLayout";
import { TranslationProvider } from "../components/TranslationProvider";

export const metadata: Metadata = {
  title: "EduNexus OS V2",
  description: "Release 1 — Foundation",
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en">
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