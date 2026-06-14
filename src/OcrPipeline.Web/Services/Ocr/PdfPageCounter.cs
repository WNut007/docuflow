using Docnet.Core;
using Docnet.Core.Models;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>Counts pages in a stored file so the OCR engine can route large PDFs to batch.</summary>
public interface IPdfPageCounter
{
    int CountPages(string filePath, string contentType);
}

/// <summary>PDF page count via PDFium (Docnet.Core). Non-PDF files are treated as a single page.</summary>
public sealed class PdfPageCounter : IPdfPageCounter
{
    public int CountPages(string filePath, string contentType)
    {
        if (!IsPdf(filePath, contentType)) return 1;
        using var reader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(1));
        return reader.GetPageCount();
    }

    private static bool IsPdf(string path, string? contentType)
        => contentType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true ||
           Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
}
