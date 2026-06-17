/* =============================================================================
   10_multi_template.sql  - multiple templates per DocumentType + doc->template binding

   Why: GetActiveTemplateForType collapsed all templates of a type to TOP 1, so every
   invoice layout shared ONE template and authoring one clobbered another (the multi-page
   zone work overwrote the single-page East Repair geometry). The schema already allows
   N MappingTemplate rows per type; this script adds the missing pieces:

     1) Document.TemplateId  - bind a document to the template it was processed with
                               (NULL = let the pipeline resolve by page count).
     2) Split the clobbered template 1 into:
          - template 1  -> the MULTI-PAGE invoice template (left exactly as-is)
          - template 2  -> a CLEAN single-page East Repair template, seeded from the
                           hand-measured DevController coords (Qty-first columns).

   Safe / idempotent: guarded by COL_LENGTH / NOT EXISTS. Existing documents' cached
   results (docs 29, 33, ...) are NOT touched; template 1's fields/columns/zones
   (including the SQL line_item rename) are NOT modified.
   ============================================================================= */
SET NOCOUNT ON;

/* ---- 1) Document.TemplateId + FK ------------------------------------------- */
IF COL_LENGTH('dbo.Document', 'TemplateId') IS NULL
BEGIN
    ALTER TABLE dbo.Document ADD TemplateId INT NULL;
    PRINT 'Added dbo.Document.TemplateId.';
END
ELSE
    PRINT 'dbo.Document.TemplateId already present - no change.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Document_Template')
BEGIN
    ALTER TABLE dbo.Document
        ADD CONSTRAINT FK_Document_Template FOREIGN KEY (TemplateId)
            REFERENCES dbo.MappingTemplate(TemplateId);
    PRINT 'Added FK_Document_Template.';
END
GO

/* ---- 2a) Name template 1 as the multi-page template (leave its data intact) - */
UPDATE dbo.MappingTemplate
SET Name = N'Invoice - Multipage'
WHERE TemplateId = 1 AND Name = N'Default Invoice v1';
GO

/* ---- 2c) Populate template 1's VERIFIED multi-page geometry (idempotent) -----
   02_seed.sql creates template 1 with PRE-ZONAL fields (no zones/roles, a single
   role-less LineItems TABLE_CELL with no columns). The FIRST/CONTINUATION/LAST geometry
   below was authored in the live DB via the zone designer and verified end-to-end
   (samples/multipage-invoice.pdf -> 12 line_item rows, page split 5/5/2, no junk/bleed).
   This block reproduces that working template on a FRESH DB. Coords are normalized 0..1,
   captured verbatim from the live DB. Guarded on the CONTINUATION region: it builds once,
   then NO-OPs - and never deletes fields a MappingResultValue might reference on a used DB.
   NOTE: the FIRST region's columns are Capitalised (Description/Qty/UnitPrice/Amount) and
   the CONTINUATION/LAST columns are lower_snake - intentional: FIRST is the canonical
   emitter, so the Capitalised names are what land in the output model. */
-- Anchor on ASCII-only words so the lookup is immune to em-dash codepage mojibake
-- (the live DB's name may be a corrupted dash; "Multipage" always survives intact).
DECLARE @tpl1 INT = (SELECT TOP 1 TemplateId FROM dbo.MappingTemplate
                     WHERE Name LIKE N'%Multipage%' OR Name = N'Default Invoice v1'
                     ORDER BY TemplateId);

IF @tpl1 IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.MappingField
                   WHERE TemplateId = @tpl1 AND TargetProperty = N'line_item'
                         AND ZonePageRole = 'CONTINUATION')
BEGIN
    /* (a) scalar/regex zones: header read on page 1 (role NULL -> resolves to FIRST),
       totals on the LAST page. UPDATE the fields 02_seed created (match by property). */
    UPDATE dbo.MappingField SET ZonePage=1, ZoneX=0.804634, ZoneY=0.080588, ZoneW=0.180282, ZoneH=0.022210, ZoneOcrHint='TEXT', ZonePageRole=NULL, MinConfidence=0.60 WHERE TemplateId=@tpl1 AND TargetProperty=N'InvoiceNumber';
    UPDATE dbo.MappingField SET ZonePage=1, ZoneX=0.824520, ZoneY=0.103509, ZoneW=0.171060, ZoneH=0.020935, ZoneOcrHint='TEXT', ZonePageRole=NULL, MinConfidence=0.65 WHERE TemplateId=@tpl1 AND TargetProperty=N'InvoiceDate';
    UPDATE dbo.MappingField SET ZonePage=1, ZoneX=0.066028, ZoneY=0.051110, ZoneW=0.386545, ZoneH=0.034762, ZoneOcrHint='TEXT', ZonePageRole=NULL, MinConfidence=0.60 WHERE TemplateId=@tpl1 AND TargetProperty=N'VendorName';
    UPDATE dbo.MappingField SET ZonePage=3, ZoneX=0.755466, ZoneY=0.276338, ZoneW=0.177550, ZoneH=0.027021, ZoneOcrHint='TEXT', ZonePageRole='LAST', MinConfidence=0.70 WHERE TemplateId=@tpl1 AND TargetProperty=N'TotalAmount';
    UPDATE dbo.MappingField SET ZonePage=3, ZoneX=0.755466, ZoneY=0.241597, ZoneW=0.176184, ZoneH=0.024126, ZoneOcrHint='TEXT', ZonePageRole='LAST', MinConfidence=0.60 WHERE TemplateId=@tpl1 AND TargetProperty=N'TaxAmount';

    /* (b) FIRST line_item region: rename the seeded LineItems -> line_item, give it the
       page-1 table zone + FIRST role + its 4 (canonical) columns. */
    UPDATE dbo.MappingField
       SET TargetProperty=N'line_item', ZonePage=1, ZoneX=0.075311, ZoneY=0.238763,
           ZoneW=0.849509, ZoneH=0.196828, ZoneOcrHint='TEXT', ZonePageRole='FIRST',
           TableHeader=N'Description', RowSelector='ALL', MinConfidence=0.55
     WHERE TemplateId=@tpl1 AND TargetProperty=N'LineItems';
    DECLARE @first INT = (SELECT TOP 1 FieldId FROM dbo.MappingField
                          WHERE TemplateId=@tpl1 AND TargetProperty=N'line_item' AND ZonePageRole='FIRST');

    IF @first IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.MappingTableColumn WHERE FieldId=@first)
        INSERT dbo.MappingTableColumn (FieldId, TargetSubProperty, DataType, SortOrder, IsActive, ColXStart, ColXEnd, IsAnchor, LineSelectMode, LineJoinSeparator)
        VALUES
            (@first, N'Description', 'STRING',  0, 1, 0.075311, 0.287689, 0, 'ALL', N' '),
            (@first, N'Qty',         'INT',     1, 1, 0.287689, 0.500066, 1, 'ALL', N' '),
            (@first, N'UnitPrice',   'DECIMAL', 2, 1, 0.500066, 0.712443, 0, 'ALL', N' '),
            (@first, N'Amount',      'DECIMAL', 3, 1, 0.712443, 0.924821, 0, 'ALL', N' ');

    /* (c) CONTINUATION (page 2) + LAST (page 3) line_item regions + their columns. */
    INSERT dbo.MappingField (TemplateId, TargetProperty, DataType, IsRequired, SourceType, MinConfidence, ZonePage, ZoneX, ZoneY, ZoneW, ZoneH, ZoneOcrHint, ZonePageRole)
    VALUES (@tpl1, N'line_item', 'STRING', 0, 'TABLE_CELL', 0.60, 2, 0.078044, 0.099735, 0.849509, 0.193009, 'TEXT', 'CONTINUATION');
    DECLARE @cont INT = SCOPE_IDENTITY();
    INSERT dbo.MappingTableColumn (FieldId, TargetSubProperty, DataType, SortOrder, IsActive, ColXStart, ColXEnd, IsAnchor, LineSelectMode, LineJoinSeparator)
    VALUES
        (@cont, N'description', 'STRING',  0, 1, 0.078044, 0.290421, 0, 'ALL', N' '),
        (@cont, N'qty',         'INT',     1, 1, 0.290421, 0.502799, 1, 'ALL', N' '),
        (@cont, N'unit_price',  'DECIMAL', 2, 1, 0.502799, 0.715176, 0, 'ALL', N' '),
        (@cont, N'amount',      'DECIMAL', 3, 1, 0.715176, 0.927553, 0, 'ALL', N' ');

    INSERT dbo.MappingField (TemplateId, TargetProperty, DataType, IsRequired, SourceType, MinConfidence, ZonePage, ZoneX, ZoneY, ZoneW, ZoneH, ZoneOcrHint, ZonePageRole)
    VALUES (@tpl1, N'line_item', 'STRING', 0, 'TABLE_CELL', 0.60, 3, 0.075312, 0.101666, 0.850875, 0.074308, 'TEXT', 'LAST');
    DECLARE @last INT = SCOPE_IDENTITY();
    INSERT dbo.MappingTableColumn (FieldId, TargetSubProperty, DataType, SortOrder, IsActive, ColXStart, ColXEnd, IsAnchor, LineSelectMode, LineJoinSeparator)
    VALUES
        (@last, N'description', 'STRING',  0, 1, 0.075312, 0.288031, 0, 'ALL', N' '),
        (@last, N'qty',         'INT',     1, 1, 0.288031, 0.500750, 1, 'ALL', N' '),
        (@last, N'unit_price',  'DECIMAL', 2, 1, 0.500750, 0.713469, 0, 'ALL', N' '),
        (@last, N'amount',      'DECIMAL', 3, 1, 0.713469, 0.926187, 0, 'ALL', N' ');

    PRINT 'Populated template 1 verified multi-page geometry (FIRST/CONTINUATION/LAST + columns).';
END
ELSE
    PRINT 'Template 1 multi-page geometry already present (or template missing) - no change.';
GO

/* ---- 2b) Create the clean single-page East Repair template (idempotent) ----- */
IF NOT EXISTS (SELECT 1 FROM dbo.MappingTemplate WHERE Name LIKE N'%East Repair%')
BEGIN
    DECLARE @docType INT = (SELECT TOP 1 DocumentTypeId FROM dbo.MappingTemplate WHERE TemplateId = 1);
    DECLARE @model NVARCHAR(200) = (SELECT TOP 1 TargetModel FROM dbo.MappingTemplate WHERE TemplateId = 1);

    INSERT dbo.MappingTemplate (DocumentTypeId, Name, TargetModel, Version, IsActive, MappingMode)
    VALUES (@docType, N'Invoice - East Repair (single page)', @model, 1, 1, 'ZONAL');
    DECLARE @tpl2 INT = SCOPE_IDENTITY();

    /* scalar zones (KEY_VALUE, single page, no page-role). Coords = DevController
       hand-measured boxes for samples/east-repair-invoice.png, normalized 0..1. */
    INSERT dbo.MappingField
        (TemplateId, TargetProperty, DataType, IsRequired, SourceType, MinConfidence,
         ZonePage, ZoneX, ZoneY, ZoneW, ZoneH, ZoneOcrHint, ZonePageRole)
    VALUES
        (@tpl2, N'VendorName',    'STRING',  0, 'KEY_VALUE', 0.60, 1, 0.073, 0.062, 0.210, 0.028, 'TEXT',    NULL),
        (@tpl2, N'InvoiceNumber', 'STRING',  0, 'KEY_VALUE', 0.60, 1, 0.840, 0.227, 0.090, 0.022, 'TEXT',    NULL),
        (@tpl2, N'InvoiceDate',   'DATE',    0, 'KEY_VALUE', 0.60, 1, 0.795, 0.253, 0.135, 0.022, 'DATE',    NULL),
        (@tpl2, N'TaxAmount',     'DECIMAL', 0, 'KEY_VALUE', 0.60, 1, 0.825, 0.521, 0.060, 0.022, 'NUMERIC', NULL),
        (@tpl2, N'TotalAmount',   'DECIMAL', 0, 'KEY_VALUE', 0.60, 1, 0.775, 0.551, 0.110, 0.026, 'NUMERIC', NULL);

    /* the line_item table zone (single page, no role) */
    INSERT dbo.MappingField
        (TemplateId, TargetProperty, DataType, IsRequired, SourceType, MinConfidence,
         ZonePage, ZoneX, ZoneY, ZoneW, ZoneH, ZoneOcrHint, ZonePageRole)
    VALUES
        (@tpl2, N'line_item', 'STRING', 0, 'TABLE_CELL', 0.60, 1, 0.073, 0.385, 0.854, 0.105, 'TEXT', NULL);
    DECLARE @lineField INT = SCOPE_IDENTITY();

    /* columns in the CORRECT east-repair order: Qty (anchor) | Description | UnitPrice | Amount */
    INSERT dbo.MappingTableColumn
        (FieldId, TargetSubProperty, DataType, SortOrder, IsActive, ColXStart, ColXEnd, IsAnchor,
         LineSelectMode, LineJoinSeparator)
    VALUES
        (@lineField, N'Qty',         'INT',     0, 1, 0.073, 0.155, 1, 'ALL', ' '),
        (@lineField, N'Description', 'STRING',  1, 1, 0.155, 0.565, 0, 'ALL', ' '),
        (@lineField, N'UnitPrice',   'DECIMAL', 2, 1, 0.565, 0.755, 0, 'ALL', ' '),
        (@lineField, N'Amount',      'DECIMAL', 3, 1, 0.755, 0.927, 0, 'ALL', ' ');

    PRINT 'Created template 2 (East Repair single page) with seeded zones + columns.';
END
ELSE
    PRINT 'East Repair template already exists - no change.';
GO
