# Codex Queue Client

React 19 + TypeScript + Vite client for the Codex Queue app.

```bash
npm install
npm run dev
```

The Vite dev server proxies `/api` to the Docker web app at `http://localhost:6767` by default, so it uses the same persisted queue and history as the normal app. To develop against a separately running local API, set `VITE_API_PROXY_TARGET=http://localhost:5153` before starting Vite.
