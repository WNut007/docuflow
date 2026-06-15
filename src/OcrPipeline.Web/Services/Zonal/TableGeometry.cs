namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// Pure geometry for table cells: maps a column x-range and a row band to a pixel sub-rectangle inside
/// the already-positioned table-zone raster. No image types, so it is unit-testable. Reuses
/// <see cref="PixelRect"/> from <see cref="ZoneGeometry"/>.
/// </summary>
public static class TableGeometry
{
    /// <summary>
    /// Convert a PAGE-normalized x (e.g. <see cref="Domain.MappingTableColumn.ColXStart"/>) to a value
    /// relative to the table zone's x-range. Clamped to 0..1; returns 0 when the zone has no width.
    /// </summary>
    public static double ToZoneRelativeX(double pageX, double zoneX, double zoneW)
    {
        if (zoneW <= 0) return 0;
        return Clamp01((pageX - zoneX) / zoneW);
    }

    /// <summary>
    /// Pixel rect of one cell within <paramref name="zoneRect"/>. Column edges and the row band are
    /// normalized 0..1 relative to the zone. Always a non-empty rect clamped to the zone.
    /// </summary>
    public static PixelRect CellPixelRect(PixelRect zoneRect, double colXStartRel, double colXEndRel, RowBand band)
    {
        double xs = Clamp01(Math.Min(colXStartRel, colXEndRel));
        double xe = Clamp01(Math.Max(colXStartRel, colXEndRel));
        double ys = Clamp01(Math.Min(band.YStart, band.YEnd));
        double ye = Clamp01(Math.Max(band.YStart, band.YEnd));

        int x = zoneRect.X + (int)Math.Round(xs * zoneRect.Width);
        int y = zoneRect.Y + (int)Math.Round(ys * zoneRect.Height);
        int w = Math.Max(1, (int)Math.Round((xe - xs) * zoneRect.Width));
        int h = Math.Max(1, (int)Math.Round((ye - ys) * zoneRect.Height));

        if (x + w > zoneRect.X + zoneRect.Width) w = zoneRect.X + zoneRect.Width - x;
        if (y + h > zoneRect.Y + zoneRect.Height) h = zoneRect.Y + zoneRect.Height - y;
        return new PixelRect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
}
