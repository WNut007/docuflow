/* ============================================================================
   OCR Pipeline - Seed data
   Run after 01_schema.sql
   ============================================================================ */
USE OcrPipeline;
GO

/* --- Pipeline statuses --- */
MERGE dbo.PipelineStatus AS t
USING (VALUES
    ('CAPTURED',    N'Captured',      10),
    ('CLASSIFIED',  N'Classified',    20),
    ('EXTRACTED',   N'Extracted',     30),
    ('ENRICHED',    N'Enriched',      40),
    ('VALIDATED',   N'Validated',     50),
    ('NEEDS_REVIEW',N'Needs Review',  55),
    ('MAPPED',      N'Mapped',        60),
    ('CONSUMED',    N'Consumed',      70),
    ('FAILED',      N'Failed',        99)
) AS s(StatusCode, DisplayName, SortOrder)
ON t.StatusCode = s.StatusCode
WHEN NOT MATCHED THEN
    INSERT (StatusCode, DisplayName, SortOrder)
    VALUES (s.StatusCode, s.DisplayName, s.SortOrder);
GO

/* --- Document types --- */
MERGE dbo.DocumentType AS t
USING (VALUES
    ('INVOICE',  N'Invoice'),
    ('RECEIPT',  N'Receipt'),
    ('PO',       N'Purchase Order'),
    ('CONTRACT', N'Contract')
) AS s(Code, DisplayName)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, DisplayName) VALUES (s.Code, s.DisplayName);
GO

/* --- Roles --- */
MERGE dbo.AppRole AS t
USING (VALUES (N'Admin'), (N'Operator'), (N'Reviewer')) AS s(RoleName)
ON t.RoleName = s.RoleName
WHEN NOT MATCHED THEN INSERT (RoleName) VALUES (s.RoleName);
GO

/* --- Admin user ---
   Password = "Admin@123"  (PBKDF2/HMAC-SHA256, 100k iterations; format it.salt.hash)
   To rotate: regenerate with Pbkdf2PasswordHasher.Hash and replace the value below.  */
IF NOT EXISTS (SELECT 1 FROM dbo.AppUser WHERE Email = N'admin@local')
BEGIN
    INSERT dbo.AppUser (UserName, Email, PasswordHash, DisplayName)
    VALUES (N'admin', N'admin@local',
            N'100000.S5le5Vjl50KJWMUcw34nUg==.DFOuceFreRGserbt+c9H8yAQWkqXUQff6JjskfymbF8=',
            N'Administrator');

    DECLARE @uid INT = SCOPE_IDENTITY();
    INSERT dbo.AppUserRole (UserId, RoleId)
    SELECT @uid, RoleId FROM dbo.AppRole WHERE RoleName = N'Admin';
END
GO

/* --- Sample mapping template: INVOICE -> InvoiceModel --- */
DECLARE @invTypeId INT = (SELECT DocumentTypeId FROM dbo.DocumentType WHERE Code = 'INVOICE');

IF NOT EXISTS (SELECT 1 FROM dbo.MappingTemplate WHERE Name = N'Default Invoice v1')
BEGIN
    INSERT dbo.MappingTemplate (DocumentTypeId, Name, TargetModel, Version, IsActive)
    VALUES (@invTypeId, N'Default Invoice v1', N'InvoiceModel', 1, 1);

    DECLARE @tplId INT = SCOPE_IDENTITY();

    INSERT dbo.MappingField
        (TemplateId, TargetProperty, DataType, IsRequired, SourceType, KeyPattern, SourcePattern, TableHeader, RowSelector, MinConfidence)
    VALUES
        (@tplId, N'InvoiceNumber', 'STRING',  1, 'KEY_VALUE', N'Invoice\s*(No|Number|#)', NULL, NULL, NULL, 0.70),
        (@tplId, N'InvoiceDate',   'DATE',    1, 'KEY_VALUE', N'(Invoice\s*)?Date',       NULL, NULL, NULL, 0.65),
        (@tplId, N'VendorName',    'STRING',  1, 'KEY_VALUE', N'(Vendor|Supplier|From)',  NULL, NULL, NULL, 0.60),
        (@tplId, N'TotalAmount',   'DECIMAL', 1, 'REGEX',     NULL, N'(?:Grand\s*)?Total\s*[:\-]?\s*([\d,]+\.\d{2})', NULL, NULL, 0.70),
        (@tplId, N'TaxAmount',     'DECIMAL', 0, 'REGEX',     NULL, N'(?:VAT|Tax)\s*[:\-]?\s*([\d,]+\.\d{2})',        NULL, NULL, 0.60),
        (@tplId, N'LineItems',     'STRING',  0, 'TABLE_CELL', NULL, NULL, N'Description', 'ALL', 0.55);
END
GO

PRINT 'Seed completed.';
GO
