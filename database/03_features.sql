/* ============================================================================
   OCR Pipeline - Drupal-style feature additions
   Run after 02_seed.sql
   Adds: Processor (configured OCR service), DocumentProperty (extracted props),
         TransformerStep (preprocess pipeline attached to a mapping field).
   ============================================================================ */
USE OcrPipeline;
GO

/* ----------------------------------------------------------------------------
   PROCESSOR
   A named, reusable configuration of an OCR engine
   (e.g. a specific Google Document AI processor id, or a Tesseract profile).
---------------------------------------------------------------------------- */
CREATE TABLE dbo.Processor (
    ProcessorId   INT IDENTITY(1,1)  NOT NULL CONSTRAINT PK_Processor PRIMARY KEY,
    Name          NVARCHAR(150)      NOT NULL,
    Engine        VARCHAR(40)        NOT NULL,    -- GOOGLE_DOCAI, AZURE_FORM, AWS_TEXTRACT, TESSERACT
    ProcessorMode VARCHAR(20)        NOT NULL CONSTRAINT DF_Processor_Mode DEFAULT('REALTIME'), -- REALTIME / QUEUE
    -- engine-specific settings as JSON (processorId, region, endpoint, ...)
    ConfigJson    NVARCHAR(MAX)      NULL,
    StoreRawJson  BIT                NOT NULL CONSTRAINT DF_Processor_StoreRaw DEFAULT(1),
    IsActive      BIT                NOT NULL CONSTRAINT DF_Processor_Active DEFAULT(1),
    CreatedAtUtc  DATETIME2(3)       NOT NULL CONSTRAINT DF_Processor_Created DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT UQ_Processor_Name UNIQUE (Name)
);
GO

/* Link a document type to the processor that should handle it */
ALTER TABLE dbo.DocumentType ADD DefaultProcessorId INT NULL
    CONSTRAINT FK_DocumentType_Processor REFERENCES dbo.Processor(ProcessorId);
GO

/* ----------------------------------------------------------------------------
   DOCUMENT PROPERTY
   Flat key/value properties extracted from a document (the "export text as
   properties" idea). Filled from KEY/VALUE blocks or engine entities.
   These are what the mapping tool maps onto the target model.
---------------------------------------------------------------------------- */
CREATE TABLE dbo.DocumentProperty (
    PropertyId   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DocumentProperty PRIMARY KEY,
    DocumentId   BIGINT               NOT NULL,
    OcrRunId     BIGINT               NOT NULL,
    [Key]        NVARCHAR(200)        NOT NULL,
    [Value]      NVARCHAR(MAX)        NULL,
    Confidence   DECIMAL(5,4)         NULL,
    SourceRef    NVARCHAR(200)        NULL,        -- TextBlock:id / Entity:name
    CONSTRAINT FK_DocumentProperty_Doc FOREIGN KEY (DocumentId) REFERENCES dbo.Document(DocumentId),
    CONSTRAINT FK_DocumentProperty_Run FOREIGN KEY (OcrRunId)   REFERENCES dbo.OcrRun(OcrRunId)
);
GO
CREATE INDEX IX_DocumentProperty_Doc ON dbo.DocumentProperty(DocumentId, [Key]);
GO

/* ----------------------------------------------------------------------------
   TRANSFORMER STEP
   An ordered preprocessing step attached to a mapping field. Stacking several
   steps = the "Pipeline transformer". Each step has a Type + JSON config.
   Types: trim, case, regex_replace, date_normalize, number_clean,
          default, ai_summary, translate
---------------------------------------------------------------------------- */
CREATE TABLE dbo.TransformerStep (
    StepId      INT IDENTITY(1,1)  NOT NULL CONSTRAINT PK_TransformerStep PRIMARY KEY,
    FieldId     INT                NOT NULL,
    StepOrder   INT                NOT NULL,
    [Type]      VARCHAR(40)        NOT NULL,
    ConfigJson  NVARCHAR(MAX)      NULL,
    IsActive    BIT                NOT NULL CONSTRAINT DF_TransformerStep_Active DEFAULT(1),
    CONSTRAINT FK_TransformerStep_Field FOREIGN KEY (FieldId) REFERENCES dbo.MappingField(FieldId),
    CONSTRAINT UQ_TransformerStep UNIQUE (FieldId, StepOrder)
);
GO

/* --- Seed: a processor + a transformer pipeline on the invoice fields --- */
IF NOT EXISTS (SELECT 1 FROM dbo.Processor WHERE Name = N'Default Tesseract')
BEGIN
    INSERT dbo.Processor (Name, Engine, ProcessorMode, ConfigJson)
    VALUES (N'Default Tesseract', 'TESSERACT', 'REALTIME', N'{ "lang": "eng+tha" }');
END
GO

/* Trim + clean number on TotalAmount; trim + normalize date on InvoiceDate */
DECLARE @totalFieldId INT =
    (SELECT FieldId FROM dbo.MappingField WHERE TargetProperty = N'TotalAmount');
DECLARE @dateFieldId INT =
    (SELECT FieldId FROM dbo.MappingField WHERE TargetProperty = N'InvoiceDate');

IF @totalFieldId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.TransformerStep WHERE FieldId = @totalFieldId)
BEGIN
    INSERT dbo.TransformerStep (FieldId, StepOrder, [Type], ConfigJson) VALUES
        (@totalFieldId, 1, 'trim',         NULL),
        (@totalFieldId, 2, 'number_clean', N'{ "decimals": 2 }');
END

IF @dateFieldId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.TransformerStep WHERE FieldId = @dateFieldId)
BEGIN
    INSERT dbo.TransformerStep (FieldId, StepOrder, [Type], ConfigJson) VALUES
        (@dateFieldId, 1, 'trim',          NULL),
        (@dateFieldId, 2, 'date_normalize', N'{ "format": "yyyy-MM-dd" }');
END
GO

PRINT 'Drupal-style features added.';
GO
