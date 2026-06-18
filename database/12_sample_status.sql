/* =============================================================================
   12_sample_status.sql  - add the 'SAMPLE' pipeline status (template backdrop)

   (A) sub-step 3 support. A template's sample document is stored as a drawing
   BACKDROP (Document.StatusCode='SAMPLE' / SourceChannel='SAMPLE') and never
   enters the OCR pipeline. Document.StatusCode is FK'd to PipelineStatus
   (FK_Document_Status), so 'SAMPLE' must exist as a status row or the sample
   INSERT in MappingController.CreateTemplate fails with FK error 547.

   SAMPLE is a DISTINCT, non-pipeline status (not a reuse of CAPTURED): the queue
   worker selects work by the literal status string GetByStatus("CAPTURED")
   (Services/Queue/PipelineWorker.cs), so a SAMPLE doc is never picked up for
   processing. SortOrder is display-only (no C# reads PipelineStatus.SortOrder);
   5 places SAMPLE before the pipeline statuses (CAPTURED=10..FAILED=99).

   Mirror this row in 02_seed.sql so a fresh 01->12 build also has it.

   Safe / idempotent: guarded by NOT EXISTS. ASCII-only (no em-dashes) so sqlcmd
   codepage can't corrupt it. See also [11_template_sample.sql].
   ============================================================================= */
USE OcrPipeline;
GO
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM dbo.PipelineStatus WHERE StatusCode = 'SAMPLE')
BEGIN
    INSERT INTO dbo.PipelineStatus (StatusCode, DisplayName, SortOrder)
    VALUES ('SAMPLE', N'Sample (template backdrop)', 5);
    PRINT 'Added PipelineStatus SAMPLE.';
END
ELSE
    PRINT 'PipelineStatus SAMPLE already present - no change.';
GO
