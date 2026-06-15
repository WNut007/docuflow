/* ============================================================================
   OCR Pipeline - Database Schema (SQL Server)
   Target: SQL Server 2019+  |  Local instance: LAPTOP-CSB3KO3E
   Pipeline: Capture -> Classify -> Extract(OCR text+table) -> Enrich
             -> Validate -> Map(to model) -> Consume
   ----------------------------------------------------------------------------
   Conventions
   - All access via Dapper using PARAMETERIZED queries (no string concat).
   - Money/score columns use DECIMAL, never FLOAT.
   - Every business table carries CreatedAtUtc; mutable ones carry UpdatedAtUtc.
   - Status fields are short codes resolved against lookup tables.
   ============================================================================ */

IF DB_ID('OcrPipeline') IS NULL
    CREATE DATABASE OcrPipeline;
GO
USE OcrPipeline;
GO

/* ----------------------------------------------------------------------------
   AUTH  (Cookie auth + PBKDF2 password hashing)
   PasswordHash stores: {iterations}.{base64 salt}.{base64 hash}
---------------------------------------------------------------------------- */
CREATE TABLE dbo.AppUser (
    UserId          INT IDENTITY(1,1)    NOT NULL CONSTRAINT PK_AppUser PRIMARY KEY,
    UserName        NVARCHAR(100)        NOT NULL,
    Email           NVARCHAR(256)        NOT NULL,
    PasswordHash    NVARCHAR(512)        NOT NULL,
    DisplayName     NVARCHAR(150)        NULL,
    IsActive        BIT                  NOT NULL CONSTRAINT DF_AppUser_IsActive DEFAULT(1),
    CreatedAtUtc    DATETIME2(3)         NOT NULL CONSTRAINT DF_AppUser_Created DEFAULT(SYSUTCDATETIME()),
    LastLoginUtc    DATETIME2(3)         NULL,
    CONSTRAINT UQ_AppUser_Email     UNIQUE (Email),
    CONSTRAINT UQ_AppUser_UserName  UNIQUE (UserName)
);
GO

CREATE TABLE dbo.AppRole (
    RoleId      INT IDENTITY(1,1)   NOT NULL CONSTRAINT PK_AppRole PRIMARY KEY,
    RoleName    NVARCHAR(50)        NOT NULL CONSTRAINT UQ_AppRole_Name UNIQUE
);
GO

CREATE TABLE dbo.AppUserRole (
    UserId  INT NOT NULL,
    RoleId  INT NOT NULL,
    CONSTRAINT PK_AppUserRole PRIMARY KEY (UserId, RoleId),
    CONSTRAINT FK_AppUserRole_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(UserId),
    CONSTRAINT FK_AppUserRole_Role FOREIGN KEY (RoleId) REFERENCES dbo.AppRole(RoleId)
);
GO

/* ----------------------------------------------------------------------------
   LOOKUPS
---------------------------------------------------------------------------- */
CREATE TABLE dbo.DocumentType (
    DocumentTypeId  INT IDENTITY(1,1)   NOT NULL CONSTRAINT PK_DocumentType PRIMARY KEY,
    Code            VARCHAR(40)         NOT NULL CONSTRAINT UQ_DocumentType_Code UNIQUE, -- INVOICE, RECEIPT, PO, CONTRACT
    DisplayName     NVARCHAR(100)       NOT NULL,
    IsActive        BIT                 NOT NULL CONSTRAINT DF_DocumentType_Active DEFAULT(1)
);
GO

/* Pipeline stage codes: CAPTURED, CLASSIFIED, EXTRACTED, ENRICHED,
                         VALIDATED, NEEDS_REVIEW, MAPPED, CONSUMED, FAILED   */
CREATE TABLE dbo.PipelineStatus (
    StatusCode      VARCHAR(20)         NOT NULL CONSTRAINT PK_PipelineStatus PRIMARY KEY,
    DisplayName     NVARCHAR(60)        NOT NULL,
    SortOrder       INT                 NOT NULL
);
GO

/* ----------------------------------------------------------------------------
   DOCUMENT  (one uploaded file = one document, tracked through the pipeline)
---------------------------------------------------------------------------- */
CREATE TABLE dbo.Document (
    DocumentId       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Document PRIMARY KEY,
    OriginalFileName NVARCHAR(260)        NOT NULL,
    StoredPath       NVARCHAR(500)        NOT NULL,   -- relative path / blob key
    ContentType      VARCHAR(100)         NOT NULL,
    FileSizeBytes    BIGINT               NOT NULL,
    Sha256           CHAR(64)             NOT NULL,    -- dedupe / integrity
    SourceChannel    VARCHAR(30)          NOT NULL,    -- SCANNER, EMAIL, MOBILE, CLOUD, UPLOAD
    DocumentTypeId   INT                  NULL,        -- set after classification
    ClassifyConfidence DECIMAL(5,4)       NULL,
    StatusCode       VARCHAR(20)          NOT NULL CONSTRAINT DF_Document_Status DEFAULT('CAPTURED'),
    PageCount        INT                  NOT NULL CONSTRAINT DF_Document_Pages DEFAULT(0),
    UploadedByUserId INT                  NULL,
    OcrLanguages     VARCHAR(50)          NULL,        -- per-document OCR language override (e.g. 'eng'); NULL = configured default
    CreatedAtUtc     DATETIME2(3)         NOT NULL CONSTRAINT DF_Document_Created DEFAULT(SYSUTCDATETIME()),
    UpdatedAtUtc     DATETIME2(3)         NULL,
    CONSTRAINT FK_Document_Type   FOREIGN KEY (DocumentTypeId)  REFERENCES dbo.DocumentType(DocumentTypeId),
    CONSTRAINT FK_Document_Status FOREIGN KEY (StatusCode)      REFERENCES dbo.PipelineStatus(StatusCode),
    CONSTRAINT FK_Document_User   FOREIGN KEY (UploadedByUserId) REFERENCES dbo.AppUser(UserId)
);
GO
CREATE INDEX IX_Document_Status ON dbo.Document(StatusCode) INCLUDE (DocumentTypeId, CreatedAtUtc);
CREATE INDEX IX_Document_Sha256 ON dbo.Document(Sha256);
GO

CREATE TABLE dbo.DocumentPage (
    PageId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DocumentPage PRIMARY KEY,
    DocumentId    BIGINT               NOT NULL,
    PageNumber    INT                  NOT NULL,
    WidthPx       INT                  NULL,
    HeightPx      INT                  NULL,
    -- IDQ (Intelligent Document Quality) metrics from the OCR engine
    IdqScore      DECIMAL(5,4)         NULL,   -- 0..1 overall page quality
    IdqIssues     NVARCHAR(400)        NULL,   -- e.g. "low_resolution,poor_lighting"
    CONSTRAINT FK_DocumentPage_Doc FOREIGN KEY (DocumentId) REFERENCES dbo.Document(DocumentId),
    CONSTRAINT UQ_DocumentPage UNIQUE (DocumentId, PageNumber)
);
GO

/* ----------------------------------------------------------------------------
   OCR RESULTS  (Extraction stage)
   One run per document/engine. Raw payload kept for audit + re-mapping.
---------------------------------------------------------------------------- */
CREATE TABLE dbo.OcrRun (
    OcrRunId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OcrRun PRIMARY KEY,
    DocumentId   BIGINT               NOT NULL,
    Engine       VARCHAR(40)          NOT NULL,   -- GOOGLE_DOCAI, AZURE_FORM, AWS_TEXTRACT, TESSERACT
    EngineVersion VARCHAR(40)         NULL,
    StartedAtUtc DATETIME2(3)         NOT NULL CONSTRAINT DF_OcrRun_Started DEFAULT(SYSUTCDATETIME()),
    FinishedAtUtc DATETIME2(3)        NULL,
    Succeeded    BIT                  NOT NULL CONSTRAINT DF_OcrRun_Ok DEFAULT(0),
    RawJson      NVARCHAR(MAX)        NULL,        -- engine raw response (audit / replay)
    ErrorMessage NVARCHAR(1000)       NULL,
    CONSTRAINT FK_OcrRun_Doc FOREIGN KEY (DocumentId) REFERENCES dbo.Document(DocumentId)
);
GO
CREATE INDEX IX_OcrRun_Doc ON dbo.OcrRun(DocumentId);
GO

/* Text blocks: lines / paragraphs / key-value pairs with geometry + confidence */
CREATE TABLE dbo.OcrTextBlock (
    TextBlockId  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OcrTextBlock PRIMARY KEY,
    OcrRunId     BIGINT               NOT NULL,
    PageNumber   INT                  NOT NULL,
    BlockType    VARCHAR(20)          NOT NULL,    -- LINE, PARAGRAPH, KEY, VALUE, WORD
    Content      NVARCHAR(MAX)        NOT NULL,
    NormalizedContent NVARCHAR(MAX)   NULL,        -- normalized value (Thai digits/number/date); see 04_normalized_content.sql
    Confidence   DECIMAL(5,4)         NULL,
    -- normalized bounding box (0..1) so it survives page resizing
    BBoxLeft     DECIMAL(7,6)         NULL,
    BBoxTop      DECIMAL(7,6)         NULL,
    BBoxWidth    DECIMAL(7,6)         NULL,
    BBoxHeight   DECIMAL(7,6)         NULL,
    -- for KEY/VALUE pairing (a VALUE points back to its KEY block)
    PairedWithId BIGINT               NULL,
    CONSTRAINT FK_OcrTextBlock_Run FOREIGN KEY (OcrRunId) REFERENCES dbo.OcrRun(OcrRunId),
    CONSTRAINT FK_OcrTextBlock_Pair FOREIGN KEY (PairedWithId) REFERENCES dbo.OcrTextBlock(TextBlockId)
);
GO
CREATE INDEX IX_OcrTextBlock_Run ON dbo.OcrTextBlock(OcrRunId, PageNumber);
GO

/* ----------------------------------------------------------------------------
   TABLE EXTRACTION  (structured: table -> cell grid)
---------------------------------------------------------------------------- */
CREATE TABLE dbo.OcrTable (
    OcrTableId   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OcrTable PRIMARY KEY,
    OcrRunId     BIGINT               NOT NULL,
    PageNumber   INT                  NOT NULL,
    TableIndex   INT                  NOT NULL,    -- nth table on the page
    [RowCount]   INT                  NOT NULL,     -- bracketed: ROWCOUNT is a reserved keyword
    ColumnCount  INT                  NOT NULL,
    Confidence   DECIMAL(5,4)         NULL,
    Caption      NVARCHAR(300)        NULL,
    CONSTRAINT FK_OcrTable_Run FOREIGN KEY (OcrRunId) REFERENCES dbo.OcrRun(OcrRunId)
);
GO
CREATE INDEX IX_OcrTable_Run ON dbo.OcrTable(OcrRunId, PageNumber);
GO

CREATE TABLE dbo.OcrTableCell (
    OcrTableCellId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OcrTableCell PRIMARY KEY,
    OcrTableId     BIGINT               NOT NULL,
    RowIndex       INT                  NOT NULL,  -- 0-based
    ColIndex       INT                  NOT NULL,  -- 0-based
    RowSpan        INT                  NOT NULL CONSTRAINT DF_Cell_RowSpan DEFAULT(1),
    ColSpan        INT                  NOT NULL CONSTRAINT DF_Cell_ColSpan DEFAULT(1),
    IsHeader       BIT                  NOT NULL CONSTRAINT DF_Cell_Header DEFAULT(0),
    Content        NVARCHAR(MAX)        NULL,
    NormalizedContent NVARCHAR(MAX)     NULL,        -- normalized value; see 04_normalized_content.sql
    Confidence     DECIMAL(5,4)         NULL,
    -- normalized (0..1) bounding box for on-image click-to-bind; see 06_table_cell_bbox.sql
    BBoxLeft       DECIMAL(7,6)         NULL,
    BBoxTop        DECIMAL(7,6)         NULL,
    BBoxWidth      DECIMAL(7,6)         NULL,
    BBoxHeight     DECIMAL(7,6)         NULL,
    CONSTRAINT FK_OcrTableCell_Table FOREIGN KEY (OcrTableId) REFERENCES dbo.OcrTable(OcrTableId),
    CONSTRAINT UQ_OcrTableCell UNIQUE (OcrTableId, RowIndex, ColIndex)
);
GO

/* ----------------------------------------------------------------------------
   MAPPING  (extracted data -> target business model)
   A MappingTemplate is bound to a DocumentType and lists target fields.
   Each field has a source rule (which OCR artifact to read).
---------------------------------------------------------------------------- */
CREATE TABLE dbo.MappingTemplate (
    TemplateId      INT IDENTITY(1,1)   NOT NULL CONSTRAINT PK_MappingTemplate PRIMARY KEY,
    DocumentTypeId  INT                 NOT NULL,
    Name            NVARCHAR(150)       NOT NULL,
    TargetModel     NVARCHAR(100)       NOT NULL,   -- e.g. "InvoiceModel"
    Version         INT                 NOT NULL CONSTRAINT DF_MappingTemplate_Ver DEFAULT(1),
    IsActive        BIT                 NOT NULL CONSTRAINT DF_MappingTemplate_Active DEFAULT(1),
    CreatedAtUtc    DATETIME2(3)        NOT NULL CONSTRAINT DF_MappingTemplate_Created DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT FK_MappingTemplate_Type FOREIGN KEY (DocumentTypeId) REFERENCES dbo.DocumentType(DocumentTypeId)
);
GO

/* SourceType drives how the engine resolves a value:
   KEY_VALUE  -> match a KEY text block by KeyPattern, take its paired VALUE
   REGEX      -> run SourcePattern over full page text
   TABLE_CELL -> pull from a table by header name (TableHeader) + RowSelector
   CONSTANT   -> literal value                                                  */
CREATE TABLE dbo.MappingField (
    FieldId        INT IDENTITY(1,1)    NOT NULL CONSTRAINT PK_MappingField PRIMARY KEY,
    TemplateId     INT                  NOT NULL,
    TargetProperty NVARCHAR(100)        NOT NULL,   -- e.g. "InvoiceNumber"
    DataType       VARCHAR(20)          NOT NULL,   -- STRING, DECIMAL, DATE, INT, BOOL
    IsRequired     BIT                  NOT NULL CONSTRAINT DF_MappingField_Req DEFAULT(0),
    SourceType     VARCHAR(20)          NOT NULL,   -- KEY_VALUE, REGEX, TABLE_CELL, CONSTANT
    KeyPattern     NVARCHAR(200)        NULL,       -- for KEY_VALUE
    SourcePattern  NVARCHAR(400)        NULL,       -- for REGEX
    TableHeader    NVARCHAR(120)        NULL,       -- for TABLE_CELL
    RowSelector    VARCHAR(20)          NULL,       -- FIRST, LAST, ALL
    DefaultValue   NVARCHAR(400)        NULL,
    MinConfidence  DECIMAL(5,4)         NOT NULL CONSTRAINT DF_MappingField_MinConf DEFAULT(0.60),
    ZonePageRole   VARCHAR(12)          NULL,       -- FIRST|CONTINUATION|LAST|ANY (multi-page; NULL=ANY)
    CONSTRAINT FK_MappingField_Template FOREIGN KEY (TemplateId) REFERENCES dbo.MappingTemplate(TemplateId)
);
GO

/* Sub-columns for a TABLE_CELL field (e.g. line_item -> description/qty/unit_price/amount).
   Each row binds an OCR table column (matched by TableHeader) to a sub-property + DataType.
   Also created idempotently in 05_table_columns.sql for existing databases. */
CREATE TABLE dbo.MappingTableColumn (
    ColumnId          INT IDENTITY(1,1)  NOT NULL CONSTRAINT PK_MappingTableColumn PRIMARY KEY,
    FieldId           INT                NOT NULL,
    TargetSubProperty NVARCHAR(100)      NOT NULL,   -- e.g. "description", "qty", "unit_price", "amount"
    DataType          VARCHAR(20)        NOT NULL CONSTRAINT DF_MTC_DataType DEFAULT('STRING'),
    TableHeader       NVARCHAR(120)      NULL,        -- OCR column header to match
    SortOrder         INT                NOT NULL CONSTRAINT DF_MTC_Sort DEFAULT(0),
    IsActive          BIT                NOT NULL CONSTRAINT DF_MTC_Active DEFAULT(1),
    -- Table-zone columns (Phase 2): boundaries within the table zone + row-anchor + multi-line rule
    ColXStart         DECIMAL(9,6)       NULL,
    ColXEnd           DECIMAL(9,6)       NULL,
    IsAnchor          BIT                NOT NULL CONSTRAINT DF_MTC_IsAnchor DEFAULT(0),
    LineSelectMode    VARCHAR(10)        NULL,        -- ALL|PICK|FIRST
    LineSelectIndices VARCHAR(50)        NULL,        -- e.g. '0,2'
    LineJoinSeparator NVARCHAR(10)       NULL,        -- e.g. ' '
    CONSTRAINT FK_MappingTableColumn_Field FOREIGN KEY (FieldId) REFERENCES dbo.MappingField(FieldId)
);
GO
CREATE INDEX IX_MappingTableColumn_Field ON dbo.MappingTableColumn(FieldId, SortOrder);
GO

/* Output of running a template against a document */
CREATE TABLE dbo.MappingResult (
    MappingResultId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MappingResult PRIMARY KEY,
    DocumentId      BIGINT               NOT NULL,
    TemplateId      INT                  NOT NULL,
    OverallConfidence DECIMAL(5,4)       NULL,
    NeedsReview     BIT                  NOT NULL CONSTRAINT DF_MappingResult_Review DEFAULT(0),
    MappedJson      NVARCHAR(MAX)        NULL,        -- final target model as JSON
    CreatedAtUtc    DATETIME2(3)         NOT NULL CONSTRAINT DF_MappingResult_Created DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT FK_MappingResult_Doc      FOREIGN KEY (DocumentId) REFERENCES dbo.Document(DocumentId),
    CONSTRAINT FK_MappingResult_Template FOREIGN KEY (TemplateId) REFERENCES dbo.MappingTemplate(TemplateId)
);
GO
CREATE INDEX IX_MappingResult_Doc ON dbo.MappingResult(DocumentId);
GO

/* Per-field resolved values (audit trail behind MappedJson) */
CREATE TABLE dbo.MappingResultValue (
    ResultValueId   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MappingResultValue PRIMARY KEY,
    MappingResultId BIGINT               NOT NULL,
    FieldId         INT                  NOT NULL,
    TargetProperty  NVARCHAR(100)        NOT NULL,
    RawValue        NVARCHAR(MAX)        NULL,
    NormalizedValue NVARCHAR(MAX)        NULL,
    Confidence      DECIMAL(5,4)         NULL,
    SourceRef       NVARCHAR(200)        NULL,        -- e.g. "TextBlock:1234" / "TableCell:55"
    IsBelowThreshold BIT                 NOT NULL CONSTRAINT DF_MRV_Below DEFAULT(0),
    CONSTRAINT FK_MRV_Result FOREIGN KEY (MappingResultId) REFERENCES dbo.MappingResult(MappingResultId),
    CONSTRAINT FK_MRV_Field  FOREIGN KEY (FieldId)         REFERENCES dbo.MappingField(FieldId)
);
GO

/* ----------------------------------------------------------------------------
   PIPELINE AUDIT  (every stage transition is logged)
---------------------------------------------------------------------------- */
CREATE TABLE dbo.PipelineEvent (
    EventId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PipelineEvent PRIMARY KEY,
    DocumentId   BIGINT               NOT NULL,
    Stage        VARCHAR(20)          NOT NULL,    -- CAPTURE..CONSUME
    FromStatus   VARCHAR(20)          NULL,
    ToStatus     VARCHAR(20)          NOT NULL,
    Message      NVARCHAR(1000)       NULL,
    ByUserId     INT                  NULL,
    CreatedAtUtc DATETIME2(3)         NOT NULL CONSTRAINT DF_PipelineEvent_Created DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT FK_PipelineEvent_Doc FOREIGN KEY (DocumentId) REFERENCES dbo.Document(DocumentId)
);
GO
CREATE INDEX IX_PipelineEvent_Doc ON dbo.PipelineEvent(DocumentId, CreatedAtUtc);
GO

/* ----------------------------------------------------------------------------
   EXPORT / CONSUMPTION  (push the mapped model downstream)
   Also created idempotently in 07_export.sql for existing databases.
---------------------------------------------------------------------------- */
CREATE TABLE dbo.ExportTarget (
    TargetId       INT IDENTITY(1,1)  NOT NULL CONSTRAINT PK_ExportTarget PRIMARY KEY,
    Name           NVARCHAR(150)      NOT NULL,
    Kind           VARCHAR(20)        NOT NULL,     -- REST_WEBHOOK, ERP
    Endpoint       NVARCHAR(500)      NULL,
    AuthHeaderName NVARCHAR(100)      NULL,         -- optional static auth header name
    AuthSecret     NVARCHAR(400)      NULL,         -- HMAC key (+ auth header value); never logged
    DocumentTypeId INT                NULL,         -- NULL = applies to all document types
    IsActive       BIT                NOT NULL CONSTRAINT DF_ExportTarget_Active DEFAULT(1),
    CreatedAtUtc   DATETIME2(3)       NOT NULL CONSTRAINT DF_ExportTarget_Created DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT FK_ExportTarget_Type FOREIGN KEY (DocumentTypeId) REFERENCES dbo.DocumentType(DocumentTypeId)
);
GO

CREATE TABLE dbo.ExportLog (
    LogId           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExportLog PRIMARY KEY,
    DocumentId      BIGINT               NOT NULL,
    TargetId        INT                  NULL,
    StatusCode      VARCHAR(20)          NOT NULL,   -- SUCCESS / FAILED
    HttpStatus      INT                  NULL,
    ResponseSnippet NVARCHAR(500)        NULL,        -- truncated response; never a secret/signature
    Attempt         INT                  NOT NULL CONSTRAINT DF_ExportLog_Attempt DEFAULT(1),
    CreatedAtUtc    DATETIME2(3)         NOT NULL CONSTRAINT DF_ExportLog_Created DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT FK_ExportLog_Doc    FOREIGN KEY (DocumentId) REFERENCES dbo.Document(DocumentId),
    CONSTRAINT FK_ExportLog_Target FOREIGN KEY (TargetId)   REFERENCES dbo.ExportTarget(TargetId)
);
GO
CREATE INDEX IX_ExportLog_Doc ON dbo.ExportLog(DocumentId, CreatedAtUtc);
GO

PRINT 'Schema created.';
GO
