# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Git

Commit messages should be concise and written in the author's own voice.

## Commands

```bash
dotnet run          # run locally (http profile: http://localhost:5082 — override with --urls)
dotnet build        # build only
dotnet watch        # hot reload

docker compose up --build   # run in container (port 8080)
```

No test project currently exists.

## Environment

`DotNetEnv` loads `.env` automatically at startup; real environment variables work too.

Required env vars:
- `SYNCFUSION_LICENSE_KEY` — Syncfusion Community license key generated for the library version in use (33.x). Missing / wrong key → PDFs get a watermark.

Local secrets come from Vault directly — `VaultEnv.Load()` (called at the top of `Program.cs`) reads `.vault.json` (committed, same format as the backend), picks the env from `VAULT_ENV` (set to `file-converter-dev` in `launchSettings.json`) and puts the secrets into the process environment. No `.env` is written, no Node tooling needed:

```bash
dotnet run --launch-profile http     # or: dotnet watch run --launch-profile http → http://localhost:5082/swagger (or F5 in VS Code — .vscode/launch.json)
```

Vault token, in order: `VAULT_TOKEN` env var → token kept in RAM by `VaultTokenCache` → interactive prompt on the console (paste a token, or log in with userpass). The token is never written to disk: on Windows it lives in a volatile registry key (`HKCU\Software\Zhizhu\VaultTokens`, memory only), on Linux in `$XDG_RUNTIME_DIR` (tmpfs) — both are gone after logoff / reboot, like vault-start; elsewhere it is not cached. Variables already in the environment (or loaded from a local `.env` by DotNetEnv) are not overwritten. Success is logged in green; any failure (missing or bad `.vault.json`, no token, wrong path, Vault unreachable) is logged in red and the app keeps running — the values may still come from a local `.env`.

`VaultEnv` only runs when `ASPNETCORE_ENVIRONMENT=Development` (set by `launchSettings.json`); the container runs as Production and `.vault.json` is excluded in `.dockerignore`, so on the server it is a no-op and secrets come from the `.env` written by `up.sh`. Without Vault access locally: `cp .env.example .env` and fill in the key by hand.

## Deploy

Same flow as the backend: `.github/workflows/deploy.yml` runs on every push to `main` — builds the image, pushes `ghcr.io/fega-kt/netcore:latest` + `:sha-<commit>`, then SSHs to the server through Cloudflare Access and runs `VAULT_TOKEN=… APP_IMAGE=…:latest bash up.sh` (the `:sha-<commit>` tag stays available to pin or roll back) in the `file-converter/` directory of the `zhizhu-server-deploy` repo (`up.sh` fetches secrets from Vault `secret/zhizhu/file-converter`, writes `.env`, pulls and recreates `zhizhu-file-converter`).

Repository secrets required: `SERVER_SSH_KEY`, `SERVER_HOST`, `SERVER_USER`, `SERVER_DEPLOY_PATH` (e.g. `/opt/zhizhu/file-converter`), `CF_ACCESS_CLIENT_ID`, `CF_ACCESS_CLIENT_SECRET`, `GHCR_PAT` (read:packages), `VAULT_TOKEN`. Environment `production` must exist.

The service is internal only (no auth): the backend calls `http://zhizhu-file-converter:8080` over the `zhizhu_net` Docker network; no Cloudflare Tunnel route.

## Architecture

Single ASP.NET Core 8 Web API project. No layers beyond controller → service.

**Request flow:**
1. `ConvertController` receives file via `POST /api/convert/file` (multipart) or HTML string via `POST /api/convert/html`
2. `ConversionService.ConvertToPdfAsync` reads magic bytes to validate the file matches its declared extension, then routes to the appropriate private converter method
3. Each converter returns `ConversionResult` with `PdfBytes` — the controller streams it back as `application/pdf`

**Conversion backends by file type:**
| Format | Library |
|---|---|
| `.docx` / `.doc` | Syncfusion DocIO + DocIORenderer |
| `.xlsx` / `.xls` | Syncfusion XlsIO + XlsIORenderer |
| `.pptx` / `.ppt` | Syncfusion PresentationRenderer (`PresentationToPdfConverter.Convert`) |
| `.html` / `.htm` | PuppeteerSharp (headless Chromium) |
| Images (jpg/png/gif/bmp/tiff/webp) | iText7 |
| `.pdf` | pass-through |

## Key gotchas

**Syncfusion namespace conflicts** — `FormatType` exists in both `Syncfusion.DocIO` and `Syncfusion.Presentation`. Always use the fully qualified `Syncfusion.DocIO.FormatType` when working in `ConversionService.cs`.

**`IWorkbook` and `XlsIORenderer` are not `IDisposable`** — do not use `using` on them; `ExcelEngine` handles cleanup.

**`PresentationToPdfConverter.Convert` is static** — do not instantiate it.

**Excel formulas** — call `sheet.EnableSheetCalculations()` on each worksheet before rendering, otherwise formula cells show cached/zero values.

**Magic byte validation** — file type is validated by reading the first 12 bytes, not by `Content-Type` header (which is client-controlled). Adding a new supported format requires updating both the extension sets and `IsValidMagic`.

**PuppeteerSharp in Docker** — requires Chromium installed in the image. `PUPPETEER_EXECUTABLE_PATH` env var must point to the binary; if unset, PuppeteerSharp attempts to download Chromium at runtime.

**Vietnamese fonts** — Syncfusion renders text using system fonts. On Linux containers, install `fonts-noto` or `fonts-liberation` to avoid garbled Vietnamese characters.
