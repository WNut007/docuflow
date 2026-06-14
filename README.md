# DocuFlow — AI-Powered Document Processing Pipeline

DocuFlow ingests documents (PDF / images), runs **OCR to extract text + tables**, derives flat
**key/value properties**, then **maps them onto a target business model** via configurable templates
with a per-field **transformer pipeline**. Inspired by the Drupal `document_ocr` module.

Pipeline stages:

```
Capture → Classify → Extract (OCR) → derive Properties → Map (transformers) → Validate → Consume
```

Document status codes: `CAPTURED, CLASSIFIED, EXTRACTED, ENRICHED, VALIDATED, NEEDS_REVIEW, MAPPED,
CONSUMED, FAILED`.

## Project status

This repo follows the staged build plan in `docs/cc-build-plan.md`. **The core pipeline (Prompts
0–6) is built, tested, and committed.**

| Done | Feature |
|------|---------|
| 0 | Compiles & baseline; Google.Cloud.DocumentAI pinned; seeded admin hash |
| 1 | Thai+English OCR: real Tesseract `tha+eng` fallback, `ImagePreprocessor`, pure `TextNormalizer` (Thai digits / currency / Buddhist-era dates) |
| 2 | Page-image rasterization + authenticated OCR-blocks API (`/api/documents/{id}/…`) |
| 3 | `MappingTableColumn` schema + `line_item` multi-column mapping → typed JSON array |
| 4 | Point-and-click visual mapping screen (image overlay + click-to-bind) |
| 5 | Accuracy review screen (confidence-banded overlay + inline correction → `VALIDATED`) |
| 6 | Offline accuracy/ground-truth tests (English + Thai) |

**Not yet built (production stage, Prompts 7–9):**
- **7** — Batch processing for large/multi-page PDFs via Google Cloud Storage (`BatchProcessDocuments`).
- **8** — Queue processing off the request thread (`Channel`/`BackgroundService`, honoring `Processor.ProcessorMode`).
- **9** — Consumption stage: export the mapped model to ERP / REST / webhook.

> The inline `PipelineService` runs all stages on the request thread today (mockup). Real queueing
> is Prompt 8.

## Tech stack

- **.NET 8 LTS, C# 12** (ASP.NET Core MVC)
- **Dapper** over **SQL Server** — no EF Core, all SQL parameterized
- **Cookie auth + PBKDF2** (HMAC-SHA256, 100k iterations)
- **Bootstrap 5.3 + Bootstrap Icons** (CDN), vanilla JS (no SPA framework)
- OCR via a pluggable `IOcrEngine`: **Google Document AI** (primary) / **Tesseract `tha+eng`** (fallback)

---

## Local setup from scratch

### Prerequisites
- **.NET 8 SDK**
- **SQL Server** (local instance; Windows auth used by default) and `sqlcmd`

### 1. Database — run every script in order

The connection string in `src/OcrPipeline.Web/appsettings.json` is:

```
Server=LAPTOP-CSB3KO3E;Database=OcrPipeline;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True
```

Change `Server=` to your instance. Then run all six scripts **in this exact order** (from the repo
root):

```bash
sqlcmd -S <YOUR_SERVER> -E -i database/01_schema.sql            # core tables (auth, document, ocr, table, mapping, audit)
sqlcmd -S <YOUR_SERVER> -E -i database/02_seed.sql              # lookups + sample Invoice template + admin user (real PBKDF2 hash)
sqlcmd -S <YOUR_SERVER> -E -i database/03_features.sql         # Processor, DocumentProperty, TransformerStep + seeds
sqlcmd -S <YOUR_SERVER> -E -i database/04_normalized_content.sql  # adds OcrTextBlock/OcrTableCell.NormalizedContent
sqlcmd -S <YOUR_SERVER> -E -i database/05_table_columns.sql    # adds dbo.MappingTableColumn
sqlcmd -S <YOUR_SERVER> -E -i database/06_table_cell_bbox.sql  # adds OcrTableCell bbox columns
```

`04`–`06` are **idempotent migrations** (`IF COL_LENGTH … / IF OBJECT_ID … IS NULL`). On a fresh
database they are safe no-ops (their columns/tables already exist in `01_schema.sql`); they exist so
databases created before those features can upgrade in place.

### 2. Run the app

```bash
cd src/OcrPipeline.Web
dotnet restore
dotnet run        # serves on the URL it prints (Development environment by default)
```

Open that URL and **log in: `admin@local` / `Admin@123`** (seeded by `02_seed.sql`).

### 3. See the visual mapping + review screens WITHOUT a live OCR engine (Development only)

Real OCR needs Google Document AI or installed Tesseract data. To exercise the UI immediately,
there's a **Development-only** seeder:

1. Make sure the app is running in the **Development** environment.
2. Browse to **`<base-url>/dev/seed-sample`**. It inserts one Invoice document from
   `samples/east-repair-invoice.png` with a page preview and a **synthetic OCR run** (KEY/VALUE +
   LINE blocks and a line-items table, all with 0..1 bboxes), then redirects you to the visual
   mapper. Outside Development it returns 404.
3. **Visual mapping** (`/Mapping/Visual?templateId=…`): the page image is on the left with clickable
   OCR boxes; pick a field on the right, click its box to bind it, then **Save**.
4. **Accuracy review** (`/Documents/Review/{id}`, also linked from a document's Detail page once it
   has a mapping result): mapped values are overlaid on their source boxes, **coloured by confidence**
   (green / amber / red around `Ocr:MinPageConfidence`); correct any value inline and Save —
   `NEEDS_REVIEW` → `VALIDATED` with a `PipelineEvent`.

> The seeded bounding boxes are **hand-measured demo data** (see `Gotchas`). Real bbox accuracy
> comes from the OCR engine at runtime.

---

## OCR engines

Provider is chosen by config `Ocr:Provider` (`Tesseract` | `GoogleDocAi`), wired in `Program.cs`.
Every engine returns an `OcrExtraction` (text blocks with normalized 0..1 bbox + confidence, and
tables as a cell grid). KEY/VALUE pairs are emitted as `"Key: Value"` blocks so mapping and property
derivation stay engine-agnostic.

### Google Document AI (primary — best Thai + table accuracy)

```jsonc
// appsettings.json
"Ocr": {
  "Provider": "GoogleDocAi",
  "MinPageConfidence": 0.60,
  "GoogleDocAi": {
    "ProjectId": "my-gcp-project",
    "Location": "us",            // or "eu"
    "ProcessorId": "abc123",     // Form Parser / Custom Doc Extractor
    "ProcessorVersion": "",      // optional pin for reproducibility
    "OnlinePageLimit": 15        // online ProcessDocument page cap; larger files need batch (Prompt 7)
  }
}
```

- **Auth:** Application Default Credentials — set `GOOGLE_APPLICATION_CREDENTIALS` to a service-account
  JSON key (or `gcloud auth application-default login` in dev). The service account needs role
  `roles/documentai.apiUser`.
- NuGet: `Google.Cloud.DocumentAI.V1` (pinned in the csproj).
- Documents over `OnlinePageLimit` throw a clear "use batch processing" error today — batch via GCS
  is Prompt 7.

### Tesseract `tha+eng` (offline fallback)

Real libtesseract via the `Tesseract` NuGet wrapper (LSTM engine). The language data is **not shipped
and is never auto-downloaded** — install it once:

1. Download the LSTM **"best"** models from <https://github.com/tesseract-ocr/tessdata_best>:
   `tha.traineddata` and `eng.traineddata`.
2. Put both in a `tessdata` folder, e.g. `src/OcrPipeline.Web/tessdata/`.
3. Configure:
   ```json
   "Ocr": { "Provider": "Tesseract",
            "Tesseract": { "TessdataPath": "tessdata", "Languages": "tha+eng", "Dpi": 300 } }
   ```
   `TessdataPath` may be absolute or relative to the app base directory.

If the folder or a `<lang>.traineddata` file is missing, the engine **fails fast with a clear
message** — it never silently falls back or downloads anything. Before recognition, images run
through `ImagePreprocessor` (managed-only, ImageSharp): grayscale → ≥300 DPI → projection-profile
deskew → median denoise. (PDF rasterization for Tesseract is not wired yet; use Google Doc AI for
PDFs.)

All engines run extracted text through the shared, pure `TextNormalizer` and store **both** raw and
normalized values (Thai digits ๐-๙ → 0-9; currency → decimal; `dd/MM/yyyy` and Buddhist-era พ.ศ.
dates → Gregorian, with per-document day/month inference).

---

## Tests

All tests are **offline** — no Tesseract, Google, network, or database. They run the pure
normalization and the mapping engine against synthetic `OcrExtraction` fixtures.

```bash
dotnet test           # from the repo root — 47 tests
```

Notable suites:
- `NormalizationTests` — Thai digits, currency, พ.ศ. → ค.ศ., dd/MM inference.
- `InvoicePipelineTests` — **ground-truth accuracy**. English (`samples/east-repair-invoice.png`):
  `invoice_id "US-001"`, `invoice_date "2019-02-11"` (dd/MM — the Due Date `26/02` makes the doc
  day-first), `subtotal 145.00`, `sales_tax 9.06`, `total 154.06`, `line_item` = 3 typed rows. Thai
  (`samples/thai-invoice.png`): Thai digits → Arabic and a พ.ศ. date → Gregorian through the same path.
- `LineItemMappingTests` — `line_item` → typed JSON array (numbers for qty/unit_price/amount).
- `BindingInferenceTests`, `ConfidenceBandTests`, `ReviewWorkflowTests`, `VisualSavePartialTests`,
  `PipelineFailureTests`.

`samples/thai-invoice.png` is a **synthetic** fixture generated by `scripts/generate-thai-sample.ps1`
(Windows, `System.Drawing` + Tahoma). The tests do **not** read the image — they feed an
`OcrExtraction` modelling the same content.

**Validating against a real Google Document AI run:** set `Ocr:Provider = "GoogleDocAi"` (+ processor
config), run the app, upload `samples/east-repair-invoice.png`, and the mapped JSON should match the
same ground truth. Real OCR text/confidence can vary, so treat exact-string equality as a guide, not
a hard gate, against live OCR.

---

## Key design decisions / gotchas

- **Dapper only, all SQL parameterized.** No EF Core; never concatenate input into SQL.
- **Money/score columns are DECIMAL, never FLOAT.** (line_item numbers serialize as JSON numbers but
  stay DECIMAL in SQL.)
- **PBKDF2** password hashing (HMAC-SHA256, 100k iterations, `iterations.salt.hash`, constant-time
  verify). All endpoints are `[Authorize]` (cookie auth); the JSON `fetch` POSTs send the antiforgery
  token via the `RequestVerificationToken` header.
- **One normalization path.** Both single-value and table-cell mapping normalize through the shared
  `TextNormalizer` (`MappingEngine.NormalizeTyped`). The old `DateTime.TryParse` MM/dd path was
  removed — a key/value `DATE` like `11/02/2019` normalizes to `2019-02-11` using the document's
  inferred day/month order, not `2019-11-02`.
- **`/dev/seed-sample` bboxes are demo-only.** They're hand-measured against
  `east-repair-invoice.png` for UI testing; misaligned seed boxes are a seed-data issue
  (`DevController`), not an overlay bug. Real bbox accuracy comes from the OCR engine.
- **The overlay renderer is shared** (`wwwroot/js/overlay.js`) by the visual-mapping and review
  screens — percent-positioned boxes re-rendered on image load (resolution-independent).
- **`VisualSave` is a partial upsert.** It writes only the fields the user actually changed
  (bound / explicitly unbound / new) and never touches untouched fields' patterns, sub-columns, or
  transformer steps. Full field editing lives in the text-based `/Mapping/Edit` screen.

---

## Repository layout

```
database/                       SQL scripts, run 01 → 06 in order
src/OcrPipeline.Web/
  Program.cs                    DI, cookie auth, OCR-provider selection, route + API mapping
  Security/                     Pbkdf2PasswordHasher
  Domain/Entities.cs            POCOs incl. OcrExtraction
  Data/                         SqlConnectionFactory + Dapper repos (IDocumentRepository, IMappingRepository)
  Services/Ocr/                 IOcrEngine, TesseractOcrEngine, GoogleDocumentAiEngine, ImagePreprocessor
  Services/Normalization/       TextNormalizer (pure)
  Services/Imaging/             PagePreviewRenderer (PDFium via Docnet.Core → PNG)
  Services/Transform/           IValueTransformer + built-ins + TransformerPipeline
  Services/Mapping/             MappingEngine, ConfidenceBand, ReviewWorkflow, BindingInference
  Services/                     ExtractionService, PipelineService (inline orchestrator)
  Controllers/                  Account, Documents (+ Review), Mapping (+ Visual), Processors,
                                Api/DocumentsApiController, DevController (Development-only seeder)
  Views/                        Razor + Bootstrap; wwwroot/js/{overlay,visual-mapping,review}.js
scripts/generate-thai-sample.ps1  Regenerates samples/thai-invoice.png
tests/OcrPipeline.Tests/        xUnit, offline (47 tests)
docs/cc-build-plan.md           The staged build plan (Prompts 0–9)
```

## Roadmap

Production-stage prompts in `docs/cc-build-plan.md`: **7** GCS batch OCR for large PDFs, **8** queue
processing (off the request thread, honoring `Processor.ProcessorMode`), **9** Consumption stage
(export mapped model to ERP / REST / webhook with retry + audit).
