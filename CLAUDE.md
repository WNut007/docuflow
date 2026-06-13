# DocuFlow — AI-Powered Document Processing Pipeline

> Context file for Claude Code. Read this before making changes.

## What this is
DocuFlow ingests documents (PDF / images), runs **OCR to extract text + tables**,
derives flat **key/value properties**, then **maps them onto a target business model**
via configurable templates with a per-field **transformer pipeline**. Inspired by the
Drupal `document_ocr` module. Pipeline stages:

`Capture → Classify → Extract (OCR) → derive Properties → Map (transformers) → Validate → Consume`

Status codes a document moves through: `CAPTURED, CLASSIFIED, EXTRACTED, ENRICHED,
VALIDATED, NEEDS_REVIEW, MAPPED, CONSUMED, FAILED`.

## Tech stack (hard constraints — do not change without asking)
- **.NET 8 LTS, C# 12** (ASP.NET Core MVC)
- **Dapper** for data access — NO EF Core. **All SQL parameterized; never concatenate input into SQL.**
- **SQL Server** — local instance `LAPTOP-CSB3KO3E`, database `OcrPipeline`
- **Cookie authentication + PBKDF2** (HMAC-SHA256, 100k iterations; stored `iterations.salt.hash`, constant-time verify)
- **Bootstrap 5.3 + Bootstrap Icons** (CDN) for the frontend
- Money/score columns are **DECIMAL, never FLOAT**

## Repository layout
```
database/                       SQL scripts, run in order
  01_schema.sql                 core tables (auth, document, ocr, table, mapping, audit)
  02_seed.sql                   lookups + sample Invoice template + admin user
  03_features.sql               Processor, DocumentProperty, TransformerStep + seeds
src/OcrPipeline.Web/            root namespace OcrPipeline.Web
  Program.cs                    DI, cookie auth, OCR-provider selection by config
  Security/                     Pbkdf2PasswordHasher
  Domain/Entities.cs            POCOs incl. OcrExtraction
  Data/                         SqlConnectionFactory + Dapper repositories
  Services/Ocr/                 IOcrEngine + TesseractOcrEngine (stub) + GoogleDocumentAiEngine (real)
  Services/Transform/           IValueTransformer + built-ins + TransformerPipeline
  Services/Mapping/             MappingEngine (resolve field → model)
  Services/                     ExtractionService, PipelineService (orchestrator)
  Controllers/                  Account, Documents, Mapping, Processors
  Views/                        Razor + Bootstrap (Mapping/Edit = the property→field tool)
```

## Key architecture rules
- **OCR is pluggable** behind `IOcrEngine`. Provider chosen by config `Ocr:Provider`
  (`Tesseract` | `GoogleDocAi`); swap in `Program.cs`. Every engine must return an
  `OcrExtraction` with text blocks (normalized 0..1 bbox + confidence) and tables
  (cell grid with row/col span). KEY/VALUE pairs are emitted as `"Key: Value"` blocks
  so the mapping engine and property derivation (split on `:`) stay engine-agnostic.
- **Mapping**: a `MappingTemplate` (bound to a `DocumentType`) holds `MappingField`s.
  Each field's `SourceType` decides resolution: `KEY_VALUE`, `REGEX`, `TABLE_CELL`, `CONSTANT`.
  Resolved values flow through an ordered **transformer pipeline** before landing in the model.
  `MappingEngine` sets `NeedsReview` if a required field is empty or any value is below `MinConfidence`.
- **Transformers** implement `IValueTransformer` (resolved by `Type`): `trim, case,
  regex_replace, number_clean, date_normalize, default, ai_summary, translate`.
  Add one → implement the interface + register in `Program.cs`. `ai_summary`/`translate`
  are stubs to wire to OpenAI/Azure/Google later.
- **PipelineService** runs stages inline (mockup). Production path = enqueue per
  `Processor.ProcessorMode = QUEUE`.

## Build & run
```bash
# 1) database (SSMS or sqlcmd), in order
sqlcmd -S LAPTOP-CSB3KO3E -E -i database/01_schema.sql
sqlcmd -S LAPTOP-CSB3KO3E -E -i database/02_seed.sql
sqlcmd -S LAPTOP-CSB3KO3E -E -i database/03_features.sql

# 2) app
cd src/OcrPipeline.Web
dotnet restore
dotnet run
```
Login: `admin@local` / `Admin@123` (regenerate the seeded hash with `Pbkdf2PasswordHasher.Hash`).

## Enabling Google Document AI
Set in `appsettings.json`: `Ocr:Provider = "GoogleDocAi"` and fill
`Ocr:GoogleDocAi` (ProjectId, Location `us`/`eu`, ProcessorId, optional ProcessorVersion).
Auth via Application Default Credentials (`GOOGLE_APPLICATION_CREDENTIALS` → service-account JSON,
or `gcloud auth application-default login`). SA role: `roles/documentai.apiUser`.
NuGet: `Google.Cloud.DocumentAI.V1` — verify the version pin against nuget.org.

## Gotchas / known TODOs
- The scaffold was authored without a local .NET SDK, so it has **not been compiled** —
  run `dotnet build` first and fix any minor issues before extending.
- Online Document AI processing has a page limit (~15). Large PDFs need **batch processing via GCS**.
- Mapping list binding uses `Fields.Index` hidden inputs to support non-contiguous rows.
- Field deletion in the mapping UI is not implemented (FK from `MappingResultValue`); only upsert.

## Roadmap (next candidates)
1. Batch processing for large PDFs via Google Cloud Storage
2. Real queue processing honoring `Processor.ProcessorMode`
3. Human Review screen to correct low-confidence fields and re-emit the model
4. Bulk / one-time import
5. Real `ai_summary` / `translate` transformers; Text-to-Speech transformer

## Conventions for changes
- Keep SQL parameterized; add indexes when adding query paths.
- New entities → add POCO in `Domain`, repository in `Data`, register in `Program.cs`.
- Prefer small, focused files; match existing C# 12 style (primary constructors, file-scoped namespaces).
- Don't introduce EF Core, raw string-concatenated SQL, or FLOAT for money.

## OCR languages & accuracy (HARD REQUIREMENTS — do not regress)
- Must OCR **Thai and English** in the same document.
- Primary engine: **Google Document AI** (supports Thai) for production accuracy.
  Offline fallback: **Tesseract with `tha+eng`** (lower accuracy on Thai, weak on tables).
- Preprocess images before Tesseract: grayscale, ensure >=300 DPI, deskew, denoise.
- Normalization layer (used by all engines) — store BOTH raw and normalized:
  - Thai digits ๐-๙ -> 0-9
  - numbers/currency -> decimal (strip thousands separators)
  - dates support `dd/MM/yyyy` and Buddhist era (พ.ศ.) -> Gregorian; infer day/month order per document
- Capture per-field confidence; flag values below `Ocr:MinPageConfidence` for human review.

## Sample test document (ground truth)
`samples/east-repair-invoice.png` — pipeline must extract & map to:
- invoice_id `US-001`
- invoice_date `2019-02-11`  (format is dd/MM/yyyy — Due Date `26/02/2019` proves day-first)
- po_number `2312/2019`
- due_date `2019-02-26`
- subtotal `145.00`, sales_tax `9.06`, total `154.06`
- line_item (3 rows):
  1) Front and rear brake cables · qty 1 · unit 100.00 · amount 100.00
  2) New set of pedal arms · qty 2 · unit 15.00 · amount 30.00
  3) Labor 3hrs · qty 3 · unit 5.00 · amount 15.00

## Point-and-click mapping (target UX — for non-technical users)
LEFT = page image with clickable OCR bounding boxes (hover shows the text).
RIGHT = field list with tabs (Fields / Key-value / Tables / OCR), document-type selector,
filter, "New field" button; `line_item` is a table field shown expanded with sub-fields
(description, qty, unit_price, amount).
Flow: user selects a field on the right, then clicks its box on the document to bind it.
Infer source type from the block type (KEY/VALUE -> KEY_VALUE, table cell -> TABLE_CELL /
MappingTableColumn). **Never show regex or patterns to the user.** Show raw + normalized value.
Save bindings to `MappingField` and `MappingTableColumn`.
