/* =============================================================================
   13_line_offset.sql  - signed per-column line offset for ANCHOR-mode columns

   Adds MappingTableColumn.LineOffset (signed INT, nullable). For a column with
   LineSelectMode='ANCHOR':
     NULL / 0  -> the anchor (quantity/spec) line itself  (part-1 ANCHOR behavior)
     -N        -> the Nth distinct OCR line ABOVE the anchor within the item's band
     +N        -> the Nth distinct OCR line BELOW the anchor within the item's band
   Reads only its side; past-end / crossing the adjacent anchor / a follower row all
   return EMPTY (never a wrong-but-valid value). INT (not a string token) so it is
   not bound by the VARCHAR(10) LineSelectMode width.

   Safe / idempotent: guarded by COL_LENGTH; existing columns get NULL (= unchanged
   ANCHOR/ALL/FIRST/PICK behavior).
   ============================================================================= */
USE OcrPipeline;
GO

IF COL_LENGTH('dbo.MappingTableColumn', 'LineOffset') IS NULL
BEGIN
    ALTER TABLE dbo.MappingTableColumn ADD LineOffset INT NULL;  -- signed; NULL = anchor line
    PRINT 'Added dbo.MappingTableColumn.LineOffset.';
END
ELSE
    PRINT 'dbo.MappingTableColumn.LineOffset already present - no change.';
GO
