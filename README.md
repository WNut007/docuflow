# OCR Pipeline (mockup)

ระบบ OCR ที่ดึง **text + table** ออกจากเอกสาร แล้ว **map เข้า target model** ตาม template
ออกแบบตาม pipeline: `Capture → Classify → Extract(OCR) → Enrich → Validate → Map → Consume`

## Stack
- .NET 8 LTS, C# 12
- Dapper (no EF Core, parameterized SQL ทุกจุด — ไม่มี string concat)
- SQL Server (local: `LAPTOP-CSB3KO3E`)
- Cookie authentication + PBKDF2 (HMAC-SHA256, 100k iterations)
- Bootstrap 5.3 + Bootstrap Icons

## โครงสร้างโปรเจกต์
```
OcrPipeline/
├─ database/
│  ├─ 01_schema.sql        # ตารางทั้งหมด (auth, document, ocr, table, mapping, audit)
│  └─ 02_seed.sql          # lookup + template ตัวอย่าง (Invoice) + admin user
└─ src/OcrPipeline.Web/
   ├─ Program.cs           # DI + cookie auth + routing
   ├─ Security/            # Pbkdf2PasswordHasher
   ├─ Domain/              # entities + OcrExtraction
   ├─ Data/                # SqlConnectionFactory + Dapper repositories
   ├─ Services/
   │  ├─ Ocr/              # IOcrEngine + TesseractOcrEngine (จุดเสียบ provider จริง)
   │  ├─ Mapping/          # MappingEngine (resolve field → model)
   │  ├─ ExtractionService.cs
   │  └─ PipelineService.cs# orchestrate ทุก stage
   ├─ Controllers/         # Account, Documents
   └─ Views/               # Bootstrap UI
```

## วิธีรัน
```bash
# 1) สร้างฐานข้อมูล (SSMS / sqlcmd)
sqlcmd -S LAPTOP-CSB3KO3E -E -i database/01_schema.sql
sqlcmd -S LAPTOP-CSB3KO3E -E -i database/02_seed.sql

# 2) รันเว็บ
cd src/OcrPipeline.Web
dotnet restore
dotnet run
```
เปิด `https://localhost:5001` → login `admin@local / Admin@123` → Upload เอกสาร → ระบบรัน pipeline แล้วโชว์ text blocks + tables ที่ดึงได้

> หมายเหตุ: hash ใน `02_seed.sql` เป็นค่า placeholder ให้สร้างค่าจริงด้วย `Pbkdf2PasswordHasher.Hash("Admin@123")` แล้วแทนที่

## หัวใจของการ mapping
`MappingTemplate` ผูกกับ `DocumentType` หนึ่งประเภท และมี `MappingField` หลายตัว
แต่ละ field บอกว่าจะดึงค่าจาก OCR artifact แบบไหน:

| SourceType | ดึงจาก |
|------------|--------|
| `KEY_VALUE` | จับ KEY block ด้วย `KeyPattern` แล้วเอา VALUE ที่จับคู่ |
| `REGEX` | รัน `SourcePattern` บน full page text |
| `TABLE_CELL` | ดึงจาก column ตาม `TableHeader` + `RowSelector` (FIRST/LAST/ALL) |
| `CONSTANT` | ค่าคงที่จาก `DefaultValue` |

`MappingEngine` คำนวณ `OverallConfidence` และตั้ง `NeedsReview = true`
ถ้า field ที่ required ว่าง หรือ confidence ต่ำกว่า `MinConfidence` → เข้า Human Review

## เสียบ OCR provider จริง
`TesseractOcrEngine` เป็น mockup ที่คืนข้อมูลตัวอย่าง สลับได้ที่ `Program.cs`:
```csharp
builder.Services.AddScoped<IOcrEngine, GoogleDocumentAiEngine>(); // หรือ Azure / Textract
```
ทุก provider ต้องคืน `OcrExtraction` ที่มี text blocks (พร้อม normalized bbox 0..1 + confidence)
และ tables (cell grid พร้อม row/col span) — ส่วนอื่นของ pipeline ไม่ต้องแก้

- **Google Document AI**: map `entities` → KEY/VALUE blocks, `tables` → OcrTable/Cell (ตรงกับสไลด์ Form Parser / Custom Doc Extractor)
- **Azure Form Recognizer / AWS Textract**: shape เดียวกัน เปลี่ยนแค่ SDK
- **Tesseract**: เก่งเรื่อง text แต่ table ไม่ดี ควรจับคู่กับ table-structure model

## Tesseract offline OCR setup (Thai + English)

`TesseractOcrEngine` is a **real** offline fallback (libtesseract via the `Tesseract` NuGet
wrapper, LSTM engine, languages `tha+eng`). Google Document AI stays the primary engine for
production Thai/table accuracy. The traineddata language files are **not shipped with the NuGet
package and are never auto-downloaded** — you must install them once:

1. Download the LSTM **"best"** models from the official Tesseract repo
   (https://github.com/tesseract-ocr/tessdata_best):
   - `tha.traineddata` — https://github.com/tesseract-ocr/tessdata_best/raw/main/tha.traineddata
   - `eng.traineddata` — https://github.com/tesseract-ocr/tessdata_best/raw/main/eng.traineddata
2. Put both files in a `tessdata` folder, e.g. `src/OcrPipeline.Web/tessdata/`.
3. Point config at it via `Ocr:Tesseract` in `appsettings.json`:
   ```json
   "Ocr": {
     "Provider": "Tesseract",
     "Tesseract": { "TessdataPath": "tessdata", "Languages": "tha+eng", "Dpi": 300 }
   }
   ```
   `TessdataPath` may be absolute or relative to the app base directory.

If the folder or a `<lang>.traineddata` file is missing, the engine **fails fast with a clear
message** telling you what to install — it will not silently fall back or download anything.

Before recognition every image runs through `ImagePreprocessor` (managed-only, ImageSharp):
grayscale → ensure ≥300 DPI → projection-profile deskew → median denoise. (OpenCV is noted in
code as the upgrade path for production-grade deskew/denoise.) PDFs are not yet rasterized for
Tesseract — that arrives in Prompt 2; use Google Document AI for PDFs until then.

All engines run extracted text through the shared, pure `TextNormalizer` and store **both** the
raw OCR text and a normalized value (Thai digits ๐-๙ → 0-9; currency → decimal; `dd/MM/yyyy` and
Buddhist-era พ.ศ. dates → Gregorian, with per-document day/month inference).

> **Smoke-testing the Tesseract path at runtime requires a live tessdata install.**
> The `Tesseract` NuGet package brings the native libtesseract binaries, but the language data
> (`tha.traineddata` + `eng.traineddata`) is intentionally **not** included and is **never
> auto-downloaded** — by design, per our no-auto-download guardrail. Until you install those files
> into the configured `Ocr:Tesseract:TessdataPath`, `TesseractOcrEngine` **fails fast** with a clear
> setup message instead of producing OCR output. The pure `TextNormalizer` logic is covered by
> offline unit tests and needs no tessdata; the engine's word/line recognition can only be
> exercised end-to-end once the traineddata is in place (see the install steps above).

## ความปลอดภัย
- รหัสผ่าน hash ด้วย PBKDF2 เก็บรูปแบบ `{iterations}.{salt}.{hash}`, verify แบบ constant-time
- SQL ทุก query เป็น parameterized ผ่าน Dapper — ไม่มีการต่อ string ค่า input เข้า SQL
- Cookie: HttpOnly, SameSite=Lax, sliding expiration 8 ชม.

## ฟีเจอร์ที่ได้แรงบันดาลใจจาก Drupal Document OCR module
รันเพิ่ม `database/03_features.sql` หลัง seed

### 1. Processor (configured OCR service)
ตาราง `Processor` = instance ของ OCR engine ที่ตั้งค่าไว้แล้ว (เช่น Google Document AI processor id, ภาษา Tesseract, region)
ดูได้ที่หน้า `/Processors` มีโหมด `REALTIME` / `QUEUE` และ flag `StoreRawJson`

### 2. Document properties
ตาราง `DocumentProperty` เก็บ key/value ที่ดึงได้จากเอกสาร (แนวคิด "export text as properties")
สร้างอัตโนมัติจาก KEY/VALUE block หลัง OCR — เป็น input ให้ mapping tool

### 3. Transformer pipeline (พระเอก)
แต่ละ `MappingField` ผูก `TransformerStep` ได้หลายตัว เรียงตาม `StepOrder`
ค่าจะไหลผ่านทีละ step: `value → trim → number_clean → ...` ก่อนเข้า model
(ตรงกับ "Pipeline transformer allows to stack up multiple transformers and change their execution order")

Transformer ที่มีให้ (`Services/Transform/Transformers.cs`):

| Type | หน้าที่ | config |
|------|--------|--------|
| `trim` | ตัด whitespace | — |
| `case` | upper/lower/title | `{ "mode": "upper" }` |
| `regex_replace` | แทนที่ด้วย regex | `{ "pattern": "...", "replacement": "..." }` |
| `number_clean` | ล้าง thousand sep + format ทศนิยม | `{ "decimals": 2 }` |
| `date_normalize` | จัดรูปแบบวันที่ | `{ "format": "yyyy-MM-dd" }` |
| `default` | ใส่ค่า default ถ้าว่าง | `{ "value": "..." }` |
| `ai_summary` | สรุปด้วย AI (stub → ต่อ OpenAI) | `{ "maxChars": 120 }` |
| `translate` | แปลภาษา (stub → ต่อ Google/Azure Translate) | `{ "to": "th" }` |

เพิ่ม transformer ใหม่: implement `IValueTransformer` แล้ว register ใน `Program.cs` — pipeline จะ resolve ตาม `Type` ให้เอง

### Roadmap (ฟีเจอร์ที่เหลือของ module)
- **Queue processing** — เปลี่ยน `PipelineService.ProcessAsync` ที่เรียก inline ไปเป็น enqueue (Channel / Hangfire / Azure Queue) ตาม `Processor.ProcessorMode = QUEUE`
- **One-time / bulk import** — หน้าอัปโหลดหลายไฟล์ + เลือก template ครั้งเดียว
- **Text-to-Speech** — transformer ตัวใหม่ที่ gen mp3 จาก text แล้วแนบกับ document

## Mapping UI (หน้าจับคู่ property → field)
หน้า `/Mapping` แสดง template ทั้งหมด → กด **Edit mapping** เข้าหน้าเครื่องมือจับคู่ (`/Mapping/Edit/{id}`)
แต่ละแถวคือ target field หนึ่งตัว ตั้งค่าได้:
- **Target property** + Data type + Required + Min confidence
- **Source type** (เลือกแล้ว input ที่เกี่ยวข้องจะโชว์เฉพาะตัวนั้น):
  - `KEY_VALUE` → ช่อง key pattern พร้อม datalist ของ property keys ที่เคยดึงได้จริง (จับคู่ property → field)
  - `REGEX` → regex (capture group 1)
  - `TABLE_CELL` → table header + row selector
  - `CONSTANT` → ค่าคงที่
- **Transformer pipeline** ต่อ field — พิมพ์ทีละบรรทัด `type|configJson`
- เพิ่ม/ลบ field ได้ (ปุ่ม Add field / ถังขยะ), บันทึกทีเดียว

การบันทึกเป็น upsert (field เดิม update, field ใหม่ insert) และ replace transformer steps ทั้งชุด — รองรับ row ที่ index ไม่ต่อเนื่องด้วย `Fields.Index`

## Google Document AI engine (ตัวจริง)
`Services/Ocr/GoogleDocumentAiEngine.cs` เรียก online `ProcessDocument` แล้ว map `Document` proto เข้า `OcrExtraction`:
- `page.FormFields` → KEY/VALUE block (รูปแบบ `Key: Value` ให้เข้ากับ mapping/property)
- `page.Lines` → LINE block (สำหรับ REGEX) พร้อม normalized bbox + confidence
- `page.Tables` → `OcrTable`/`OcrTableCell` (header rows = IsHeader, รองรับ row/col span)
- เก็บ proto เป็น JSON ไว้ audit

**เปิดใช้:**
```jsonc
// appsettings.json
"Ocr": {
  "Provider": "GoogleDocAi",
  "GoogleDocAi": {
    "ProjectId": "my-gcp-project",
    "Location": "us",            // หรือ "eu"
    "ProcessorId": "abc123",     // Form Parser / Custom Doc Extractor
    "ProcessorVersion": ""        // ระบุเพื่อ reproducible
  }
}
```
**Auth:** ใช้ Application Default Credentials — ตั้ง `GOOGLE_APPLICATION_CREDENTIALS` ชี้ service-account JSON
(หรือ `gcloud auth application-default login` ตอน dev) service account ต้องมี role `roles/documentai.apiUser`

**NuGet:** `Google.Cloud.DocumentAI.V1` (ใช้เวอร์ชันล่าสุดจาก nuget.org)

> ไฟล์ใหญ่/หลายหน้า (> ~15 หน้า): ต้องใช้ batch processing ผ่าน Google Cloud Storage แทน online process — เป็นจุดต่อยอด


## Accuracy tests (offline) & real OCR

The mapping/normalization accuracy is covered by **offline** tests (no Tesseract/Google/network/DB):
they feed a synthetic `OcrExtraction` per sample and run `MappingEngine`, asserting the CLAUDE.md
ground truth.

```bash
dotnet test tests/OcrPipeline.Tests
# accuracy fixtures live in:
#   tests/OcrPipeline.Tests/InvoicePipelineTests.cs   (English East Repair + Thai)
#   tests/OcrPipeline.Tests/LineItemMappingTests.cs   (line_item typed array)
```

- **English** (`samples/east-repair-invoice.png`): asserts `invoice_id "US-001"`,
  `invoice_date "2019-02-11"` (dd/MM — the Due Date `26/02` makes the document day-first),
  `subtotal 145.00`, `sales_tax 9.06`, `total 154.06`, and `line_item` = 3 typed rows.
- **Thai** (`samples/thai-invoice.png`): asserts Thai digits `๐-๙` → Arabic and a Buddhist-era
  (พ.ศ.) date → Gregorian through the same normalization path.

> `samples/thai-invoice.png` is a **synthetic** fixture produced by
> `scripts/generate-thai-sample.ps1` (Windows, `System.Drawing` + Tahoma). The offline test does
> **not** read the image — it feeds an `OcrExtraction` modelling the same content.

**Running the same assertions against a real Google Document AI run:** set
`Ocr:Provider = "GoogleDocAi"` (+ processor config, see above), run the app, and upload
`samples/east-repair-invoice.png`. The mapped JSON on the document's Detail/Review screen should
match the same ground truth. Real OCR text/confidence can vary, so treat exact-string equality as a
guide rather than a hard gate when validating against live OCR.
