/* =============================================================================
   14_group_inherit.sql  - per-column group-inheritance flag (mechanism 2a)

   Adds MappingTableColumn.GroupInherit. For a column with GroupInherit = 1, a grouped
   FOLLOWER row (a line item stacked directly under a leader, sharing its metadata block)
   inherits the LEADER's value for that column when its own cell is EMPTY. The leader/
   follower split is the SAME rule-4 gap classification the ANCHOR offset reader uses, so
   the row that came out empty is exactly the row that inherits. Only fills empty follower
   cells from a non-empty leader -> correct-or-EMPTY, never a wrong value.

   Use: a leader-relative field that is shared across a group (e.g. origin = LineOffset -1)
   sets GroupInherit = 1 so the followers below the leader carry the group's value. A
   per-item field (e.g. description, each item's own spec) leaves it 0.

   Safe / idempotent: guarded by COL_LENGTH; default 0 so every existing column is unchanged
   (the inheritance post-pass is a no-op unless at least one column opts in).
   ============================================================================= */
USE OcrPipeline;
GO

IF COL_LENGTH('dbo.MappingTableColumn', 'GroupInherit') IS NULL
BEGIN
    ALTER TABLE dbo.MappingTableColumn ADD GroupInherit BIT NOT NULL CONSTRAINT DF_MappingTableColumn_GroupInherit DEFAULT 0;
    PRINT 'Added dbo.MappingTableColumn.GroupInherit.';
END
ELSE
    PRINT 'dbo.MappingTableColumn.GroupInherit already present - no change.';
GO
