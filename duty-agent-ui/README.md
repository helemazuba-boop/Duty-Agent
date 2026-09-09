# Vue 3 + TypeScript + Vite

This template should help get you started developing with Vue 3 and TypeScript in Vite. The template uses Vue 3 `<script setup>` SFCs, check out the [script setup docs](https://v3.vuejs.org/api/sfc-script-setup.html#sfc-script-setup) to learn more.

Learn more about the recommended Project Setup and IDE Support in the [Vue Docs TypeScript Guide](https://vuejs.org/guide/typescript/overview.html#project-setup).

## Environment

The UI defaults to same-origin API requests, which works with the Vite dev proxy and the desktop-hosted app.

For standalone frontend hosting, set `VITE_API_BASE_URL` to the backend origin:

```env
VITE_API_BASE_URL=http://127.0.0.1:8765
```

`VITE_BACKEND_TOKEN` is supported for **local development only** (`vite` dev server): the
`inject-dev-token` plugin reads it and injects `window.__DEV_TOKEN__` into the served page.
It is never applied to `vite build` output — a baked-in token would override the per-boot
token the desktop host passes via the URL and break auth with 401s. `New-DutyAgentClientRelease.ps1`
fails the release if `__DEV_TOKEN__` appears in `dist/index.html`.
