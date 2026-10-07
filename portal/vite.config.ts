import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Local dev: `npm run dev` proxies /_api to the deployed site (set PORTAL_URL), see docs/PORTAL.md.
export default defineConfig({
  plugins: [react()],
  build: { outDir: "dist", assetsDir: "assets" },
  server: process.env.PORTAL_URL
    ? { proxy: { "/_api": { target: process.env.PORTAL_URL, changeOrigin: true, secure: true } } }
    : undefined,
});
