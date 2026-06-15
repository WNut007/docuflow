using System.Security.Cryptography;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Services.Ocr;

namespace OcrPipeline.Web.Services;

public sealed class ExtractionService(
    IOcrEngine ocrEngine,
    OcrRepository ocrRepo)
{
    public async Task<long> ExtractAsync(long documentId, string filePath, string contentType, string? languages = null, CancellationToken ct = default)
    {
        var extraction = await ocrEngine.ExtractAsync(filePath, contentType, languages, ct);
        return ocrRepo.SaveExtraction(documentId, extraction);
    }

    public static string ComputeSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
