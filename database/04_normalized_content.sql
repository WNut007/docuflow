/* ============================================================================
   Migration 04 — NormalizedContent columns (Prompt 1: Thai/English OCR)
   ----------------------------------------------------------------------------
   Adds a nullable NormalizedContent column to the OCR result tables so every
   engine can store BOTH the raw OCR text and its normalized form (Thai digits
   -> ASCII, currency -> decimal, Buddhist-era dates -> Gregorian).

   Idempotent: safe to run repeatedly and on databases created before this
   column existed. Fresh databases already get the column from 01_schema.sql;
   this script upgrades existing ones without rewriting history.
   ============================================================================ */
USE OcrPipeline;
GO

IF COL_LENGTH('dbo.OcrTextBlock', 'NormalizedContent') IS NULL
BEGIN
    ALTER TABLE dbo.OcrTextBlock ADD NormalizedContent NVARCHAR(MAX) NULL;
    PRINT 'Added dbo.OcrTextBlock.NormalizedContent';
END
GO

IF COL_LENGTH('dbo.OcrTableCell', 'NormalizedContent') IS NULL
BEGIN
    ALTER TABLE dbo.OcrTableCell ADD NormalizedContent NVARCHAR(MAX) NULL;
    PRINT 'Added dbo.OcrTableCell.NormalizedContent';
END
GO
