/* ============================================================================
   Migration 09 — Zonal / template-based OCR (Phase 0)
   ----------------------------------------------------------------------------
   Adds zonal mapping support alongside the existing OCR-first flow:
     - dbo.MappingTemplate.MappingMode  ('OCR_FIRST' default | 'ZONAL')
     - dbo.MappingField zone columns: a normalized 0..1 rectangle the user draws
       on a sample, plus an OCR hint and optional PageSegMode override. At
       processing time a ZONAL template OCRs only inside each zone.

   Normalized coords are DECIMAL (never FLOAT), consistent with the bbox/money
   convention. NULL ZoneX = the field has no zone.

   Idempotent: safe to run repeatedly and on databases created before these
   columns existed. Fresh databases already get them from 01_schema.sql.
   ============================================================================ */
USE OcrPipeline;
GO

IF COL_LENGTH('dbo.MappingTemplate', 'MappingMode') IS NULL
BEGIN
    ALTER TABLE dbo.MappingTemplate
        ADD MappingMode VARCHAR(10) NOT NULL
            CONSTRAINT DF_MappingTemplate_Mode DEFAULT('OCR_FIRST');
    PRINT 'Added dbo.MappingTemplate.MappingMode.';
END
ELSE
    PRINT 'dbo.MappingTemplate.MappingMode already present — no change.';
GO

IF COL_LENGTH('dbo.MappingField', 'ZoneX') IS NULL
BEGIN
    ALTER TABLE dbo.MappingField ADD
        ZonePage    INT           NULL,   -- 1-based page the zone is on
        ZoneX       DECIMAL(9,6)  NULL,   -- normalized 0..1 rectangle (left/top/width/height)
        ZoneY       DECIMAL(9,6)  NULL,
        ZoneW       DECIMAL(9,6)  NULL,
        ZoneH       DECIMAL(9,6)  NULL,
        ZoneOcrHint VARCHAR(20)   NULL,   -- TEXT / NUMERIC / DATE / INT
        ZonePsm     TINYINT       NULL;   -- optional explicit Tesseract PageSegMode override
    PRINT 'Added dbo.MappingField zone columns.';
END
ELSE
    PRINT 'dbo.MappingField zone columns already present — no change.';
GO

-- Multi-page role (Phase 3): which physical page-role a zone applies to. Dormant until Phase 3;
-- added now so multi-page support needs no re-migration. NULL = ANY (applies on every page).
IF COL_LENGTH('dbo.MappingField', 'ZonePageRole') IS NULL
BEGIN
    ALTER TABLE dbo.MappingField ADD ZonePageRole VARCHAR(12) NULL;  -- FIRST | CONTINUATION | LAST | ANY
    PRINT 'Added dbo.MappingField.ZonePageRole.';
END
ELSE
    PRINT 'dbo.MappingField.ZonePageRole already present — no change.';
GO

-- Table-zone columns (Phase 2): column x-boundaries within the table zone, the row-bounding anchor
-- column, and the multi-line-cell collapse rule. All dormant until Phase 2 — added now so table
-- zones + row segmentation need no re-migration.
IF COL_LENGTH('dbo.MappingTableColumn', 'ColXStart') IS NULL
BEGIN
    ALTER TABLE dbo.MappingTableColumn ADD
        ColXStart         DECIMAL(9,6) NULL,   -- normalized 0..1 column left/right boundary
        ColXEnd           DECIMAL(9,6) NULL,
        IsAnchor          BIT          NOT NULL CONSTRAINT DF_MTC_IsAnchor DEFAULT(0), -- one-value-per-row column (bounds rows)
        LineSelectMode    VARCHAR(10)  NULL,   -- ALL | PICK | FIRST  (multi-line cell -> one value)
        LineSelectIndices VARCHAR(50)  NULL,   -- e.g. '0,2' for PICK
        LineJoinSeparator NVARCHAR(10) NULL;   -- e.g. ' ' to join multi-line into one value
    PRINT 'Added dbo.MappingTableColumn zone/line columns.';
END
ELSE
    PRINT 'dbo.MappingTableColumn zone/line columns already present — no change.';
GO
