# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Git

Commit messages should be concise and written in the author's own voice.

## Commands

```bash
dotnet run          # run locally (port 5000/5001 per launchSettings.json)
dotnet build        # build only
dotnet watch        # hot reload

docker compose up --build   # run in container (port 8080)
```

No test project currently exists.

## Environment

Copy `.env.example` to `.env` and fill in values before running locally. `DotNetEnv` loads `.env` automatically at startup.

Required env vars:
- `SYNCFUSION_LICENSE_KEY` — get free community license at syncfusion.com/products/communitylicense

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
