using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OcrPipeline.Web.Services.Imaging;

/// <summary>One rendered page preview: 1-based page number and its pixel size.</summary>
public sealed record RenderedPage(int PageNumber, int Width, int Height);

/// <summary>
/// Renders per-page PNG previews next to an uploaded file so the mapping UI has a stable
/// image to overlay OCR boxes on. PDFs are rasterized page-by-page via PDFium (Docnet.Core);
/// raster images are normalized to a single-page PNG. Preview paths follow a deterministic
/// convention (see <see cref="PreviewPath"/>) so no extra DB column is needed.
/// </summary>
public sealed class PagePreviewRenderer
{
    private const int PdfPointsPerInch = 72; // PDF user space is 72 units/inch

    /// <summary>Deterministic preview path: "<stored-without-ext>.page-{n}.png" beside the upload.</summary>
    public static string PreviewPath(string storedPath, int pageNumber)
        => Path.ChangeExtension(storedPath, null) + $".page-{pageNumber}.png";

    /// <summary>
    /// Writes a PNG preview per page next to <paramref name="storedPath"/> and returns each
    /// page's pixel dimensions. PDFs render at <paramref name="dpi"/>; images use their own size.
    /// </summary>
    public IReadOnlyList<RenderedPage> Render(string storedPath, string contentType, int dpi = 200)
        => IsPdf(storedPath, contentType)
            ? RenderPdf(storedPath, dpi)
            : RenderImage(storedPath);

    private static bool IsPdf(string path, string? contentType)
        => contentType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true ||
           Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static List<RenderedPage> RenderImage(string storedPath)
    {
        using var image = Image.Load<Rgba32>(storedPath);
        image.SaveAsPng(PreviewPath(storedPath, 1));
        return [new RenderedPage(1, image.Width, image.Height)];
    }

    private static List<RenderedPage> RenderPdf(string storedPath, int dpi)
    {
        var pages = new List<RenderedPage>();
        double scaling = (double)dpi / PdfPointsPerInch;

        // DocLib.Instance is a process-wide singleton — get a reader from it, but do not dispose it.
        using var docReader = DocLib.Instance.GetDocReader(storedPath, new PageDimensions(scaling));
        int pageCount = docReader.GetPageCount();

        for (int i = 0; i < pageCount; i++)
        {
            using var pageReader = docReader.GetPageReader(i);
            int width = pageReader.GetPageWidth();
            int height = pageReader.GetPageHeight();
            byte[] bgra = pageReader.GetImage(); // BGRA, width*height*4

            using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);
            image.Mutate(c => c.BackgroundColor(Color.White)); // flatten transparency to white
            image.SaveAsPng(PreviewPath(storedPath, i + 1));

            pages.Add(new RenderedPage(i + 1, width, height));
        }
        return pages;
    }
}
