# DocuFlow — Claude Code build plan

Paste these into Claude Code **one at a time, in order**. Review the diff after each,
build, and only move on when it's green. Every prompt assumes CC has read `CLAUDE.md`.

> Tip: start each session with `claude` from `C:\dev\docuflow` so `CLAUDE.md` loads automatically.

---

## Prompt 0 — Compile & baseline
```
Read CLAUDE.md. Run `dotnet build` on src/OcrPipeline.Web and fix every compile error
WITHOUT violating the hard constraints (Dapper only, parameterized SQL, DECIMAL for money,
PBKDF2, cookie auth). Then:
1) Verify the Google.Cloud.DocumentAI.V1 version in the csproj exists on nuget.org and bump
   it to the latest stable that restores cleanly.
2) Generate a correct PBKDF2 hash for the password "Admin@123" using Pbkdf2PasswordHasher and
   update the seeded admin user (02_seed.sql + the DB) so login works.
Summarise exactly what you changed.
```

## Prompt 1 — Thai/English OCR + accuracy
```
Make OCR support Thai AND English in the same document and raise accuracy. Per CLAUDE.md:
- Keep Google Document AI as the primary engine (it supports Thai); make the processor fully
  configurable and confirm GoogleDocumentAiEngine handles multi-page and multi-language docs.
- Implement a REAL TesseractOcrEngine offline fallback using the Tesseract .NET wrapper with
  languages "tha+eng". Capture per word/line confidence and bounding boxes. In the README,
  document how to obtain tha.traineddata + eng.traineddata (LSTM "best" models) and where to
  put the tessdata folder.
- Add an ImagePreprocessor service used before Tesseract: grayscale, ensure >=300 DPI, deskew,
  denoise. Keep it separate and unit-testable.
- Add a Normalization service used by ALL engines that stores BOTH raw and normalized values:
  Thai digits ๐-๙ -> 0-9; numbers/currency -> decimal; dates supporting dd/MM/yyyy and Buddhist
  era (พ.ศ.) -> Gregorian, inferring day/month order per document.
Add unit tests for normalization (Thai digits, พ.ศ. -> ค.ศ., dd/MM detection). Build.
```

## Prompt 2 — Page image + OCR blocks API
```
Add the data the mapping UI needs. Per CLAUDE.md:
- On upload, rasterize each PDF page to a PNG preview (images: use the file itself) and store it
  next to the upload.
- Add authenticated endpoints:
  GET /api/documents/{id}/pages/{n}/image  -> the page PNG
  GET /api/documents/{id}/ocr              -> JSON: for each page { width, height (px) } and a
     list of blocks { id, type (KEY|VALUE|LINE|TABLE_CELL), text, normalizedValue, confidence,
     bbox {left,top,width,height} normalized 0..1 }, plus tables with their cells.
Reuse OcrRepository; parameterized SQL only. Build.
```

## Prompt 3 — Schema for table sub-columns (line_item)
```
Support mapping a table field (line_item) to MULTIPLE columns. Per CLAUDE.md:
- Create database/04_table_columns.sql adding dbo.MappingTableColumn
  (ColumnId PK, FieldId FK->MappingField, TargetSubProperty, DataType, TableHeader, SortOrder,
   IsActive) with sensible indexes.
- Update MappingEngine: when a field SourceType = TABLE_CELL and it has MappingTableColumn rows,
  iterate the matched table's rows (by RowSelector) and emit an ARRAY of objects built from those
  columns, applying DataType normalization and any per-column transformer steps.
- Update MappingRepository to load + save MappingTableColumn.
Add a unit test asserting samples/east-repair-invoice.png maps line_item to an array of 3 objects
matching the ground truth in CLAUDE.md. Build.
```

## Prompt 4 — Point-and-click mapping screen
```
Build the point-and-click mapping screen exactly per the "Point-and-click mapping" section in
CLAUDE.md. Non-technical users must be able to map without seeing regex or patterns.
- Two panels. LEFT: the page image with clickable bounding-box overlays from
  /api/documents/{id}/ocr; hovering a box shows a tooltip of its text. RIGHT: tabs
  (Fields / Key-value / Tables / OCR), a document-type selector, a filter box, a "New field"
  button, and the field list. line_item is shown expanded with sub-fields
  (description, qty, unit_price, amount).
- Interaction: click a field on the right to select it, then click a box on the document to bind.
  Infer source type from the block type (KEY/VALUE -> KEY_VALUE, table cell -> TABLE_CELL /
  MappingTableColumn). Show raw + normalized value with a small badge. Allow unbinding.
- Save bindings via an authenticated POST to MappingField (+ MappingTableColumn for sub-fields).
Bootstrap 5.3 + Bootstrap Icons + vanilla JS, no SPA framework. Follow CLAUDE.md. Build.
```

## Prompt 5 — Accuracy review screen
```
Add a per-document Review screen for accuracy. Per CLAUDE.md:
- Overlay the mapped values on the page image; colour each box by confidence
  (green high / amber medium / red low) using Ocr:MinPageConfidence as the medium/low cutoff.
- Let a reviewer correct any value inline; saving updates MappingResultValue and moves the
  document from NEEDS_REVIEW to VALIDATED, logging a PipelineEvent.
Parameterized SQL only. Build.
```

## Prompt 6 — Test fixtures + accuracy assertions
```
Add integration tests for accuracy. Per CLAUDE.md ground truth:
- Use samples/east-repair-invoice.png and add ONE Thai-language invoice sample to samples/.
- Write a test that runs the pipeline on the English sample and asserts: invoice_id "US-001",
  invoice_date "2019-02-11" (dd/MM/yyyy; Due Date 26/02 proves day-first), subtotal 145.00,
  sales_tax 9.06, total 154.06, and line_item has the 3 rows from CLAUDE.md.
- Document how to run the same assertions against the Thai sample.
Build and run the tests.
```

---

## Production stage — run after the core feature is working

These are heavier and touch cloud / infra. Do them one at a time too.

### Prompt 7 — Batch processing via GCS (large / multi-page PDFs)
```
Add Google Document AI BATCH processing for PDFs larger than the online page limit. Per CLAUDE.md:
- Refactor the Document-proto -> OcrExtraction mapping in GoogleDocumentAiEngine into a shared
  mapper class so the online and batch paths reuse the exact same mapping.
- Add GCS config under Ocr:GoogleDocAi (Bucket, InputPrefix, OutputPrefix). When a document's page
  count exceeds OnlinePageLimit, upload the file to gs://bucket/<InputPrefix>, call
  BatchProcessDocuments (a long-running operation), poll to completion, then read the output
  Document JSON shards from gs://bucket/<OutputPrefix> and map them with the shared mapper.
- NuGet: Google.Cloud.Storage.V1 (use the latest stable). Same Application Default Credentials;
  document that the service account needs roles/documentai.apiUser plus object admin on the bucket.
- Delete the GCS input/output objects after a successful run. On failure set status FAILED and log
  a PipelineEvent with the operation error.
Keep online ProcessDocument as the default for small docs. Follow CLAUDE.md. Build.
```

### Prompt 8 — Queue processing (off the request thread)
```
Move pipeline processing into a queue instead of running inline on upload. Per CLAUDE.md:
- On upload, insert the document as CAPTURED and ENQUEUE a job; do not run PipelineService on the
  request thread.
- Add an in-process Channel<long>-backed queue plus a BackgroundService worker that dequeues
  document ids, opens a fresh DI scope per job (scoped repositories must be resolved inside that
  scope), and runs PipelineService.ProcessAsync. Honour Processor.ProcessorMode (QUEUE vs REALTIME).
- Add bounded concurrency (Ocr:Queue:MaxConcurrency) and retry with exponential backoff
  (max attempts configurable); on final failure set status FAILED and log a PipelineEvent.
- Document how to swap the in-process queue for a durable one (Azure Storage Queue / RabbitMQ /
  Hangfire) WITHOUT changing PipelineService.
- Show a queue status indicator on the Documents list (queued / processing / done / failed).
Follow CLAUDE.md. Build.
```

### Prompt 9 — Export to ERP / REST / webhook (Consumption stage)
```
Add the Consumption stage that pushes the mapped model downstream. Per CLAUDE.md:
- Create database/05_export.sql:
  dbo.ExportTarget (TargetId PK, Name, Kind ['REST'|'ERP'|'WEBHOOK'], Endpoint, AuthHeaderName,
     AuthSecret, DocumentTypeId NULL FK, IsActive, CreatedAtUtc)
  dbo.ExportLog (LogId PK, DocumentId FK, TargetId FK, StatusCode, HttpStatus, Attempt,
     ResponseSnippet, CreatedAtUtc)  with indexes on DocumentId.
- Add an IExportTarget abstraction (mirroring IOcrEngine), resolved by Kind:
  RestWebhookExporter -> POST MappingResult.MappedJson to ExportTarget.Endpoint with the configured
  auth header and an HMAC-SHA256 signature header; ErpExporter -> stub with a clear extension point.
- After a document reaches VALIDATED (or MAPPED when no review is needed), run every active
  ExportTarget that matches the document type, record each attempt in ExportLog, retry with
  backoff, then set status CONSUMED and log a PipelineEvent. Never write secrets to logs.
- Add a minimal /Exports admin page: list targets and the most recent ExportLog rows.
Parameterized SQL only; DECIMAL for money; no EF Core. Follow CLAUDE.md. Build.
```

---

## Notes
- Run prompts in order; don't batch them — each is a reviewable unit.
- If CC proposes EF Core, string-concatenated SQL, or FLOAT for money, reject and point it to
  the hard constraints in CLAUDE.md.
- For real Thai accuracy testing you need either a Google Document AI processor (cloud) or the
  Thai Tesseract traineddata installed locally (Prompt 1 documents this).
