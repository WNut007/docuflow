/* ============================================================================
   Migration 07 — Export / Consumption (Prompt 9)
   ----------------------------------------------------------------------------
   Adds dbo.ExportTarget (downstream endpoints) and dbo.ExportLog (per-attempt
   audit). After a document is VALIDATED, each active target receives the mapped
   model; when all succeed the document moves to CONSUMED.

   Idempotent: safe to run repeatedly and on databases created before these
   tables existed. Fresh databases already get them from 01_schema.sql.
   ============================================================================ */
USE OcrPipeline;
GO

IF OBJECT_ID('dbo.ExportTarget', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportTarget (
        TargetId       INT IDENTITY(1,1)  NOT NULL CONSTRAINT PK_ExportTarget PRIMARY KEY,
        Name           NVARCHAR(150)      NOT NULL,
        Kind           VARCHAR(20)        NOT NULL,
        Endpoint       NVARCHAR(500)      NULL,
        AuthHeaderName NVARCHAR(100)      NULL,
        AuthSecret     NVARCHAR(400)      NULL,
        DocumentTypeId INT                NULL,
        IsActive       BIT                NOT NULL CONSTRAINT DF_ExportTarget_Active DEFAULT(1),
        CreatedAtUtc   DATETIME2(3)       NOT NULL CONSTRAINT DF_ExportTarget_Created DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_ExportTarget_Type FOREIGN KEY (DocumentTypeId) REFERENCES dbo.DocumentType(DocumentTypeId)
    );
    PRINT 'Created dbo.ExportTarget';
END
GO

IF OBJECT_ID('dbo.ExportLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportLog (
        LogId           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExportLog PRIMARY KEY,
        DocumentId      BIGINT               NOT NULL,
        TargetId        INT                  NULL,
        StatusCode      VARCHAR(20)          NOT NULL,
        HttpStatus      INT                  NULL,
        ResponseSnippet NVARCHAR(500)        NULL,
        Attempt         INT                  NOT NULL CONSTRAINT DF_ExportLog_Attempt DEFAULT(1),
        CreatedAtUtc    DATETIME2(3)         NOT NULL CONSTRAINT DF_ExportLog_Created DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_ExportLog_Doc    FOREIGN KEY (DocumentId) REFERENCES dbo.Document(DocumentId),
        CONSTRAINT FK_ExportLog_Target FOREIGN KEY (TargetId)   REFERENCES dbo.ExportTarget(TargetId)
    );
    PRINT 'Created dbo.ExportLog';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ExportLog_Doc' AND object_id = OBJECT_ID('dbo.ExportLog'))
    CREATE INDEX IX_ExportLog_Doc ON dbo.ExportLog(DocumentId, CreatedAtUtc);
GO

/* Sample target (inactive) so the /Exports admin page isn't empty. Configure real
   targets via SQL; set IsActive = 1 to enable auto-export on VALIDATED. */
IF NOT EXISTS (SELECT 1 FROM dbo.ExportTarget WHERE Name = N'Sample webhook (disabled)')
    INSERT dbo.ExportTarget (Name, Kind, Endpoint, AuthHeaderName, AuthSecret, DocumentTypeId, IsActive)
    VALUES (N'Sample webhook (disabled)', 'REST_WEBHOOK', N'https://example.com/docuflow/webhook',
            N'X-Api-Key', N'change-me-secret', NULL, 0);
GO
