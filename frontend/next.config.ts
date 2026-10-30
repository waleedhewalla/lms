import type { NextConfig } from "next";
const nextConfig: NextConfig = {
  reactStrictMode: true,
  output: "standalone",
  async headers() {
    // The worker script and the offline page must always be revalidated, or a fix to either never reaches installed apps.
    return [
      { source: "/sw.js", headers: [{ key: "Cache-Control", value: "no-cache, max-age=0" }, { key: "Service-Worker-Allowed", value: "/" }] },
      { source: "/offline.html", headers: [{ key: "Cache-Control", value: "no-cache, max-age=0" }] },
    ];
  },
};
export default nextConfig;