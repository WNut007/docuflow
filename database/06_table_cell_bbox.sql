/* ============================================================================
   Migration 06 — OcrTableCell bounding box (Prompt 4 follow-up: click cells on image)
   ----------------------------------------------------------------------------
   Adds a nullable normalized (0..1) bounding box to OCR table cells so the visual
   mapper can render clickable cell overlays on the page image. Populated by engines
   that carry cell geometry (Google Document AI cell.Layout.BoundingPoly); left null
   otherwise (e.g. Tesseract), in which case cells bind via the Tables tab.

   Idempotent: safe to run repeatedly and on databases created before these columns
   existed. Fresh databases already get them from 01_schema.sql.
   ============================================================================ */
USE OcrPipeline;
GO

IF COL_LENGTH('dbo.OcrTableCell', 'BBoxLeft') IS NULL
    ALTER TABLE dbo.OcrTableCell ADD BBoxLeft DECIMAL(7,6) NULL;
GO
IF COL_LENGTH('dbo.OcrTableCell', 'BBoxTop') IS NULL
    ALTER TABLE dbo.OcrTableCell ADD BBoxTop DECIMAL(7,6) NULL;
GO
IF COL_LENGTH('dbo.OcrTableCell', 'BBoxWidth') IS NULL
    ALTER TABLE dbo.OcrTableCell ADD BBoxWidth DECIMAL(7,6) NULL;
GO
IF COL_LENGTH('dbo.OcrTableCell', 'BBoxHeight') IS NULL
    ALTER TABLE dbo.OcrTableCell ADD BBoxHeight DECIMAL(7,6) NULL;
GO
