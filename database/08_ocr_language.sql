/* ============================================================================
   Migration 08 — Per-document OCR language override
   ----------------------------------------------------------------------------
   Adds dbo.Document.OcrLanguages: an optional Tesseract language string chosen
   at upload (e.g. 'eng' for a Latin-only invoice, 'tha+eng' for mixed). NULL
   means "use the configured default" (Ocr:Tesseract:Languages). This lets a
   single-language document avoid stray foreign glyphs from an unused script.

   Idempotent: safe to run repeatedly and on databases created before this
   column existed. Fresh databases already get it from 01_schema.sql.
   ============================================================================ */
USE OcrPipeline;
GO

IF COL_LENGTH('dbo.Document', 'OcrLanguages') IS NULL
BEGIN
    ALTER TABLE dbo.Document ADD OcrLanguages VARCHAR(50) NULL;
    PRINT 'Added dbo.Document.OcrLanguages.';
END
ELSE
    PRINT 'dbo.Document.OcrLanguages already present — no change.';
GO
