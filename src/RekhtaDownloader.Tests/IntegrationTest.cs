using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RekhtaDownloader.Tests
{
    [TestClass]
    public class IntegrationTest
    {
        static readonly ILoggerFactory LoggingFactory = LoggerFactory.Create(builder => builder.AddConsole());

        [TestMethod]
        public async Task TestDownload()
        {
            var logger = LoggingFactory.CreateLogger(nameof(IntegrationTest));
            var downloader = new BookExporter(logger);
            var outputPath = await downloader.DownloadBook("https://www.rekhta.org/ebooks/rafeeq-e-manzil-shumara-number-10-magazines", 10, OutputType.Pdf);
        }

        [TestMethod]
        public async Task TestGetInformation()
        {
            var logger = LoggingFactory.CreateLogger(nameof(IntegrationTest));
            var downloader = new BookExporter(logger);
            var bookInfo= await downloader.GetBookInformation("https://www.rekhta.org/ebooks/sarguzisht-abdul-majeed-salik-ebooks-1");
            Assert.IsNotNull(bookInfo);
            Assert.AreEqual("Sarguzisht", bookInfo.Title);
            Assert.AreEqual("Abdul Majeed Salik", bookInfo.Authors.First());
            Assert.AreEqual("Qaumi Kutub Khana, Lahore", bookInfo.Publisher);
            Assert.AreEqual(1955, bookInfo.Year);
            Assert.IsNotNull(bookInfo.Image);
        }

        [TestMethod]
        public async Task TestStreamingDownloadStaysWithinMemoryBoundAndResumes()
        {
            const string bookUrl = "https://www.rekhta.org/ebooks/rafeeq-e-manzil-shumara-number-10-magazines";

            var logger = LoggingFactory.CreateLogger(nameof(IntegrationTest));
            var downloader = new BookExporter(logger);

            // GetBookInfoAsync is the only call that should ever hit the book-info page - it is
            // called once here and the returned BookInfo is reused for both runs below.
            var bookInfo = await downloader.GetBookInfoAsync(bookUrl);
            Assert.IsNotNull(bookInfo);
            Assert.IsTrue(bookInfo.TotalPages > 0);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var memoryBefore = GC.GetTotalMemory(true);

            var firstRunPageCount = 0;
            await foreach (var page in downloader.DownloadPagesAsync(bookUrl, bookInfo, startPage: 1, taskCount: 1, ct: CancellationToken.None))
            {
                using (page.ImageStream)
                {
                    firstRunPageCount++;
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var memoryAfter = GC.GetTotalMemory(true);

            Assert.AreEqual(bookInfo.TotalPages, firstRunPageCount);
            logger.LogInformation($"Memory before: {memoryBefore} bytes, after: {memoryAfter} bytes for {firstRunPageCount} pages");

            // Resuming after the last page should not re-fetch any page-id batches (watch the
            // "Fetching page ids" log lines - none should appear for this second run) and should
            // yield no further pages.
            var resumedPageCount = 0;
            await foreach (var page in downloader.DownloadPagesAsync(bookUrl, bookInfo, startPage: bookInfo.TotalPages + 1, taskCount: 1, ct: CancellationToken.None))
            {
                using (page.ImageStream)
                {
                    resumedPageCount++;
                }
            }

            Assert.AreEqual(0, resumedPageCount);
        }
    }
}