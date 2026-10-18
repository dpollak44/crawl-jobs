import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// In development the dev server proxies /api to the API so there is no cross-origin setup to think about.
// In the container build, VITE_API_URL points the browser straight at the API's own address.
export default defineConfig({
    plugins: [react()],
    server: {
        port: 3000,
        proxy: { "/api": "http://localhost:8080" },
    },
});
