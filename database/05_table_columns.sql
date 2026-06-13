/* ============================================================================
   Migration 05 — MappingTableColumn (Prompt 3: table sub-columns / line_item)
   ----------------------------------------------------------------------------
   Adds dbo.MappingTableColumn so a TABLE_CELL mapping field (e.g. line_item) can
   bind MULTIPLE OCR table columns to typed sub-properties.

   Idempotent: safe to run repeatedly and on databases created before this table
   existed. Fresh databases already get it from 01_schema.sql; this script upgrades
   existing ones without rewriting history.
   ============================================================================ */
USE OcrPipeline;
GO

IF OBJECT_ID('dbo.MappingTableColumn', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MappingTableColumn (
        ColumnId          INT IDENTITY(1,1)  NOT NULL CONSTRAINT PK_MappingTableColumn PRIMARY KEY,
        FieldId           INT                NOT NULL,
        TargetSubProperty NVARCHAR(100)      NOT NULL,
        DataType          VARCHAR(20)        NOT NULL CONSTRAINT DF_MTC_DataType DEFAULT('STRING'),
        TableHeader       NVARCHAR(120)      NULL,
        SortOrder         INT                NOT NULL CONSTRAINT DF_MTC_Sort DEFAULT(0),
        IsActive          BIT                NOT NULL CONSTRAINT DF_MTC_Active DEFAULT(1),
        CONSTRAINT FK_MappingTableColumn_Field FOREIGN KEY (FieldId) REFERENCES dbo.MappingField(FieldId)
    );
    PRINT 'Created dbo.MappingTableColumn';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MappingTableColumn_Field'
               AND object_id = OBJECT_ID('dbo.MappingTableColumn'))
BEGIN
    CREATE INDEX IX_MappingTableColumn_Field ON dbo.MappingTableColumn(FieldId, SortOrder);
    PRINT 'Created IX_MappingTableColumn_Field';
END
GO
