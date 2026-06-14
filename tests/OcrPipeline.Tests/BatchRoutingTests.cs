using OcrPipeline.Web.Services.Ocr;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>Offline tests for the online-vs-batch routing decision (no GCP calls).</summary>
public sealed class BatchRoutingTests
{
    [Theory]
    [InlineData(10, 15, "my-bucket", false)]  // under the limit -> online
    [InlineData(15, 15, "my-bucket", false)]  // exactly at the limit -> online
    [InlineData(20, 15, "my-bucket", true)]   // over the limit + bucket configured -> batch
    [InlineData(20, 15, "", false)]           // over the limit but no bucket -> not batch (engine errors)
    [InlineData(1, 15, "my-bucket", false)]   // single page (image) -> online
    public void ShouldBatch_decides_by_page_count_and_bucket(int pages, int limit, string bucket, bool expected)
    {
        var options = new GoogleDocAiOptions { OnlinePageLimit = limit, Bucket = bucket };
        Assert.Equal(expected, BatchRouting.ShouldBatch(pages, options));
    }
}
