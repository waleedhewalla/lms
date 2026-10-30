import type { MetadataRoute } from "next";

/** Makes the web app installable (home screen / desktop). start_url is the signed-in launcher; the sign-in guard handles the rest. */
export default function manifest(): MetadataRoute.Manifest {
  return {
    name: "EduNexus",
    short_name: "EduNexus",
    description: "Correspondence, approvals, boards, accreditation and records for universities and schools.",
    id: "/",
    start_url: "/dashboard",
    scope: "/",
    display: "standalone",
    background_color: "#0b1220",
    theme_color: "#0b1220",
    icons: [
      { src: "/icons/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
      { src: "/icons/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
      { src: "/icons/icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
    ],
  };
}
