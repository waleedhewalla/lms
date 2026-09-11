import type { Metadata } from "next";
export const metadata: Metadata = { title: "EduNexus OS V2", description: "Release 1 — Foundation" };
export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body style={{ fontFamily: "system-ui", margin: 0 }}>{children}</body>
    </html>
  );
}