using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Common;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RekhtaDownloader.Models;

namespace RekhtaDownloader
{
    internal class Book
    {
        private const int PageIdBatchSize = 40;

        private readonly List<Page> _pages = new List<Page>();

        private string _bookUrl;
        private readonly int _threadCount;
        private readonly int _imageQuality;
        private readonly ILogger _logger;
        private readonly CancellationToken _cancellationToken;

        private string BookId { get; set; }
        public string BookName { get; private set; }

        public IEnumerable<Page> Pages => _pages.OrderBy(p => p.PageIndex);

        private readonly object _lock = new object();

        private int _completeCount = 0;

        private string _outputDirectory = String.Empty;

        public Book(string bookUrl, int threadCount, ILogger logger, int imageQuality, CancellationToken cancellationToken)
        {
            _bookUrl = bookUrl;
            _threadCount = threadCount;
            _logger = logger;
            _cancellationToken = cancellationToken;
            _imageQuality = imageQuality;
        }

        public async Task<Models.BookInfo> GetBookInformation()
        {
            await CheckDetailsPageAndResolveBookPage();
            var pageContents = await HttpHelper.GetTextBody(_bookUrl);
            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml(pageContents);
            var docNode = document.DocumentNode;
            var imageUrl = docNode.QuerySelector(".AddInfoWrap > .addINFOimg > img")?.GetAttributeValue<string>("src", null);

            var (title, author, publisher, year) = ParseBookDetails(pageContents);
            var bookinfo = new Models.BookInfo
            {
                Title = title,
                Authors = author != null ? new[] { author } : null,
                Publisher = publisher,
                Year = year,
            };
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                var bitmap = await HttpHelper.GetImage(imageUrl);
                bookinfo.Image = bitmap.ToByteArray();
            }

            return bookinfo;
        }

        // Shared by GetBookInformation() and GetBookInfoAsync(), both of which scrape the same
        // ".AddInfoWrap" book-details markup from the (possibly already-fetched) page HTML.
        private (string Title, string Author, string Publisher, int Year) ParseBookDetails(string pageContents)
        {
            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml(pageContents);
            var docNode = document.DocumentNode;

            var title = docNode.QuerySelector(".AddInfoWrap > .B-descript > h5")?.InnerText?.Trim();
            string author = null;
            string publisher = null;
            var year = 0;

            var infos = docNode.QuerySelectorAll(".AddInfoWrap > .B-descript > ul > li");
            foreach (var item in infos)
            {
                var type = item?.InnerText?.Trim();

                if (type.Contains("AUTHOR"))
                {
                    author = item.QuerySelector("p > span > a")?.NextSibling?.InnerText?.Trim();
                }
                else if (type.Contains("PUBLISHER"))
                {
                    publisher = item.QuerySelector("p > span")?.InnerText?.Trim();
                }
                else if (type.Contains("YEAR"))
                {
                    var yearText = item.QuerySelector("p > span")?.InnerText?.Trim();
                    if (int.TryParse(yearText, out var parsedYear))
                    {
                        year = parsedYear;
                    }
                }
            }

            return (title, author, publisher, year);
        }

        // Fetches book metadata (title/author/publisher plus the internal ids needed to download
        // pages) in a single request. This is the only method that hits the book page for
        // metadata - the returned BookInfo carries everything DownloadPagesAsync needs so it
        // never has to be re-fetched, which is what makes resuming a download cheap.
        public async Task<RekhtaDownloader.BookInfo> GetBookInfoAsync()
        {
            await CheckDetailsPageAndResolveBookPage();
            var pageContents = await HttpHelper.GetTextBody(_bookUrl);

            var imageFolderName = FindTextBetween(pageContents, "Critique_id = \"", ";")?.Trim().Trim('"', '\'');
            var bookId = FindTextBetween(pageContents, "var bookId = \"", "\";")?.Trim().Trim('"', '\'');
            var actualUrl = FindTextBetween(pageContents, "var actualUrl =", ";")?.Trim().Trim('"', '\'');
            var slug = actualUrl?.ToLower().Replace("/ebooks/", "").Trim().Trim('/', '\\');
            var pageCount = int.Parse(FindTextBetween(pageContents, "var totalPageCount =", ";")?.Trim().Trim('"', '\'') ?? throw new InvalidOperationException("Unable to parse total page count"));
            var pageFileNames = StringToStringArray(FindTextBetween(pageContents, "var pages = [", "];"));

            var (title, author, publisher, _) = ParseBookDetails(pageContents);

            return new RekhtaDownloader.BookInfo(
                BookUrl: _bookUrl,
                TitleUr: string.IsNullOrWhiteSpace(title) ? slug : title,
                AuthorNameUr: author,
                PublisherNameUr: publisher,
                TotalPages: pageCount,
                BookId: bookId,
                Slug: slug,
                ImageFolderName: imageFolderName,
                PageFileNames: pageFileNames);
        }

        // Streams pages starting at startPage, fetching the 40-page id batches lazily as the
        // enumeration reaches them and bounding concurrent downloads to taskCount. Stopping
        // enumeration early (break / cancellation) cleanly stops further downloads.
        public async IAsyncEnumerable<PageResult> DownloadPagesAsync(
            RekhtaDownloader.BookInfo bookInfo,
            int startPage,
            int taskCount,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (bookInfo == null) throw new ArgumentNullException(nameof(bookInfo));
            if (startPage < 1) throw new ArgumentOutOfRangeException(nameof(startPage));
            if (taskCount < 1) throw new ArgumentOutOfRangeException(nameof(taskCount));

            var totalPages = bookInfo.TotalPages;
            if (startPage > totalPages)
            {
                yield break;
            }

            // One lazily-started fetch task per 40-page batch, shared across concurrently
            // downloading pages that happen to land in the same batch.
            var pageIdBatchTasks = new ConcurrentDictionary<int, Task<string[]>>();

            async Task<string> GetPageIdAsync(int pageIndex, CancellationToken token)
            {
                var batchIndex = pageIndex / PageIdBatchSize;
                var batchTask = pageIdBatchTasks.GetOrAdd(
                    batchIndex,
                    bi => GetPageIdsBatchAsync(bookInfo.Slug, bi * PageIdBatchSize, totalPages, token));
                var ids = await batchTask;
                return ids[pageIndex - batchIndex * PageIdBatchSize];
            }

            // Bounded to taskCount so at most taskCount pages are buffered awaiting consumption
            // on top of the taskCount pages Parallel.ForEachAsync has in flight downloading below.
            var channel = Channel.CreateBounded<PageResult>(new BoundedChannelOptions(taskCount) { SingleReader = true });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var producer = Task.Run(async () =>
            {
                try
                {
                    var pageIndexes = Enumerable.Range(startPage - 1, totalPages - (startPage - 1));
                    await Parallel.ForEachAsync(
                        pageIndexes,
                        new ParallelOptions { MaxDegreeOfParallelism = taskCount, CancellationToken = cts.Token },
                        async (pageIndex, token) =>
                        {
                            var pageId = await GetPageIdAsync(pageIndex, token);
                            var fileName = bookInfo.PageFileNames[pageIndex];
                            var bytes = await DownloadPageBytesAsync(pageId, pageIndex, bookInfo, fileName, token);
                            await channel.Writer.WriteAsync(
                                new PageResult(pageIndex + 1, totalPages, new MemoryStream(bytes), "image/jpeg"),
                                token);
                        });
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, cts.Token);

            try
            {
                await foreach (var page in channel.Reader.ReadAllAsync(ct))
                {
                    yield return page;
                }
            }
            finally
            {
                cts.Cancel();
                try
                {
                    await producer;
                }
                catch
                {
                    // The consumer has already stopped enumerating; nothing left to report to.
                }
            }
        }

        public async Task DownloadBook(string outputPath)
        {
            var bookInfo = await GetBookInfoAsync();

            BookName = bookInfo.Slug;
            BookId = bookInfo.BookId;
            _outputDirectory = Path.Combine(outputPath, bookInfo.ImageFolderName.ToSafeFilename());
            _outputDirectory.EnsureEmptyDirectory();

            _logger.LogInformation($"Book Name : {BookName}");
            _logger.LogInformation($"Page Count: {bookInfo.TotalPages}");

            await foreach (var page in DownloadPagesAsync(bookInfo, 1, _threadCount, _cancellationToken))
            {
                using (page.ImageStream)
                {
                    var fileName = bookInfo.PageFileNames[page.PageNumber - 1];
                    var filePath = Path.Combine(_outputDirectory, fileName);
                    _outputDirectory.CreateIfDirectoryDoesNotExists();
                    filePath.MakeSureFileDoesNotExist();

                    using (var fileStream = File.Create(filePath))
                    {
                        await page.ImageStream.CopyToAsync(fileStream, _cancellationToken);
                    }

                    lock (_lock)
                    {
                        _pages.Add(new Page
                        {
                            Index = page.PageNumber - 1,
                            PageId = null,
                            FolderName = bookInfo.ImageFolderName,
                            PageNumber = Path.GetFileNameWithoutExtension(fileName),
                            FileName = fileName,
                            PageImagePath = filePath,
                        });
                        _completeCount++;
                        _logger.LogInformation($"Downloaded page {_completeCount} of {page.TotalPages}");
                    }
                }
            }
        }

        private async Task CheckDetailsPageAndResolveBookPage()
        {
            if (_bookUrl.Contains("/ebooks/detail/"))
            {
                Console.WriteLine("Warning: Url Provided is for the book details page. " +
                    "I will try to resolve the book page URL from it." +
                    "If I fail to do so please use the correct url for book page, where you can see the book pages to read.");

                var pageContents = await HttpHelper.GetTextBody(_bookUrl);

                var document = new HtmlAgilityPack.HtmlDocument();
                document.LoadHtml(pageContents);
                var docNode = document.DocumentNode;
                var href = docNode.QuerySelector(".ebkDtlScl .rReadMore")?.GetAttributeValue("href", null);

                if (href != null)
                {
                    _bookUrl = href;
                }
            }
        }

        private async Task<string[]> GetPageIdsBatchAsync(string bookSlug, int batchStart, int pagesCount, CancellationToken cancellationToken)
        {
            var count = Math.Min(PageIdBatchSize, pagesCount - batchStart);

            return await new RetryPolicyProvider(_logger).PageRetryPolicy.ExecuteAsync(async () =>
            {
                _logger.LogInformation($"Fetching page ids for pages {batchStart + 1} to {batchStart + count}");
                var data = await HttpHelper.GetTextBody(
                    $"https://www.rekhta.org/EbookData/GetEbookPageIds/?slug={bookSlug}&lang=1&from={batchStart}&count={count}");
                var pageIdData = JsonConvert.DeserializeObject<PageIdData>(data);
                return pageIdData.Ids.ToArray();
            });
        }

        private async Task<byte[]> DownloadPageBytesAsync(string pageId, int pageIndex, RekhtaDownloader.BookInfo bookInfo, string fileName, CancellationToken cancellationToken)
        {
            return await new RetryPolicyProvider(_logger).PageRetryPolicy.ExecuteAsync(async () =>
            {
                var data = await HttpHelper.GetTextBody($"https://www.rekhta.org/EbookData/GetEbookFromApi/?pgid={pageId}&bkId={bookInfo.BookId}&pgIdx={pageIndex}");
                var pageData = JsonConvert.DeserializeObject<PageData>(data);

                var pageImage = await HttpHelper.GetImage($"https://ebooksapi.rekhta.org/images/{bookInfo.ImageFolderName}/{fileName}");
                pageImage = ImageHelper.RearrangeImage(pageImage, pageData);

                return pageImage.ToByteArray(_imageQuality);
            });
        }

        private string FindTextBetween(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            var endIndex = source.IndexOf(end, startIndex + 1, StringComparison.Ordinal);
            return source.Substring(startIndex + start.Length, endIndex - startIndex - start.Length);
        }

        private string[] StringToStringArray(string input)
        {
            var result = new List<string>();
            var items = input.Split(',');
            foreach (var item in items)
            {
                result.Add(item.Trim().Trim('"'));
            }

            return result.ToArray();
        }
    }
}
