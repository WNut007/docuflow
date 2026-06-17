/* =============================================================================
   11_template_sample.sql  - bind each template to its OWN sample document

   (A) sub-step 1. A template carries the sample document its zones are drawn over, so
   the zone designer stops showing a picker of every uploaded document. Adds:

     MappingTemplate.SampleDocumentId -> Document(DocumentId), nullable
       (NULL = no sample yet; the designer shows an "upload a sample" empty-state).

   Live-DB migration binds the two existing templates to their most-recent matching
   sample doc (resolved BY FILENAME, so it is environment-independent). On a fresh DB
   (no Document rows) the binds are no-ops and the templates stay NULL until a sample is
   uploaded via the create flow.

   Safe / idempotent: guarded by COL_LENGTH / NOT EXISTS / IS NULL. ASCII-only (no
   em-dashes) so sqlcmd codepage can't corrupt it. See also [10_multi_template.sql].
   ============================================================================= */
SET NOCOUNT ON;

/* ---- 1) MappingTemplate.SampleDocumentId + FK ----------------------------- */
IF COL_LENGTH('dbo.MappingTemplate', 'SampleDocumentId') IS NULL
BEGIN
    ALTER TABLE dbo.MappingTemplate ADD SampleDocumentId BIGINT NULL;  -- matches Document.DocumentId (BIGINT)
    PRINT 'Added dbo.MappingTemplate.SampleDocumentId.';
END
ELSE
    PRINT 'dbo.MappingTemplate.SampleDocumentId already present - no change.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_MappingTemplate_SampleDocument')
BEGIN
    ALTER TABLE dbo.MappingTemplate
        ADD CONSTRAINT FK_MappingTemplate_SampleDocument FOREIGN KEY (SampleDocumentId)
            REFERENCES dbo.Document(DocumentId);
    PRINT 'Added FK_MappingTemplate_SampleDocument.';
END
ELSE
    PRINT 'FK_MappingTemplate_SampleDocument already present - no change.';
GO

/* ---- 2) Bind existing templates to their most-recent matching sample doc ----
   Resolve the newest Document (that already has rendered pages) per filename and bind
   it. Guarded by IS NULL so re-runs and any manual rebind are preserved; a fresh DB
   (no Document rows) binds nothing and leaves the templates NULL for the empty-state. */
DECLARE @multipage BIGINT = (
    SELECT TOP 1 d.DocumentId FROM dbo.Document d
    WHERE d.OriginalFileName LIKE N'%multipage-invoice%'
      AND EXISTS (SELECT 1 FROM dbo.DocumentPage p WHERE p.DocumentId = d.DocumentId)
    ORDER BY d.CreatedAtUtc DESC, d.DocumentId DESC);

UPDATE dbo.MappingTemplate
   SET SampleDocumentId = @multipage
 WHERE SampleDocumentId IS NULL AND @multipage IS NOT NULL
   AND Name LIKE N'%Multipage%';

DECLARE @eastrepair BIGINT = (
    SELECT TOP 1 d.DocumentId FROM dbo.Document d
    WHERE d.OriginalFileName LIKE N'%east-repair%'
      AND EXISTS (SELECT 1 FROM dbo.DocumentPage p WHERE p.DocumentId = d.DocumentId)
    ORDER BY d.CreatedAtUtc DESC, d.DocumentId DESC);

UPDATE dbo.MappingTemplate
   SET SampleDocumentId = @eastrepair
 WHERE SampleDocumentId IS NULL AND @eastrepair IS NOT NULL
   AND Name LIKE N'%East Repair%';

PRINT 'Bound existing templates to sample docs (multipage='
      + ISNULL(CONVERT(VARCHAR(10), @multipage), 'none') + ', east-repair='
      + ISNULL(CONVERT(VARCHAR(10), @eastrepair), 'none') + ').';
GO
