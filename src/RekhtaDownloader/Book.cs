using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
        private readonly List<Page> _pages = new List<Page>();

        private readonly BlockingCollection<Page> _jobs = new BlockingCollection<Page>();

        private string _bookUrl;
        private readonly int _threadCount;
        private readonly ILogger _logger;
        private readonly CancellationToken _cancellationToken;

        private int _pageCount;

        private string BookId { get; set; }
        public string BookName { get; private set; }

        public IEnumerable<Page> Pages => _pages.OrderBy(p => p.PageIndex);

        private readonly object _lock = new object();

        private int _completeCount = 0;

        private string _outputDirectory = String.Empty;

        public Book(string bookUrl, int threadCount, ILogger logger, CancellationToken cancellationToken)
        {
            _bookUrl = bookUrl;
            _threadCount = threadCount;
            _logger = logger;
            _cancellationToken = cancellationToken;
        }

        public async Task<BookInfo> GetBookInformation()
        {
            await CheckDetailsPageAndResolveBookPage();
            var pageContents = await HttpHelper.GetTextBody(_bookUrl);
            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml(pageContents);
            var docNode = document.DocumentNode;
            var title = docNode.QuerySelector(".AddInfoWrap > .B-descript > h5")?.InnerText?.Trim();
            var imageUrl = docNode.QuerySelector(".AddInfoWrap > .addINFOimg > img")?.GetAttributeValue<string>("src", null);
            var bookinfo = new BookInfo
            {
                Title = title,
            };
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                var bitmap = await HttpHelper.GetImage(imageUrl);
                bookinfo.Image = bitmap.ToByteArray();
            }
            var infos = docNode.QuerySelectorAll(".AddInfoWrap > .B-descript > ul > li");
            foreach (var item in infos)
            {
                var type = item?.InnerText?.Trim();

                if (type.Contains("AUTHOR"))
                {
                    bookinfo.Authors = new[] { item.QuerySelector("p > span > a")?.NextSibling?.InnerText?.Trim() };
                }
                else if(type.Contains("PUBLISHER"))
                {
                    bookinfo.Publisher = item.QuerySelector("p > span")?.InnerText?.Trim();
                }
                else if (type.Contains("YEAR"))
                {
                    var yearText = item.QuerySelector("p > span")?.InnerText?.Trim();
                    if (int.TryParse(yearText, out var year))
                    {
                        bookinfo.Year = year;
                    }
                }
            }

            return bookinfo;
        }

        public async Task DownloadBook(string outputPath)
        {
            await CheckDetailsPageAndResolveBookPage();
            var pageContents = await HttpHelper.GetTextBody(_bookUrl);
            var imageFolderName = FindTextBetween(pageContents, "Critique_id = \"", ";")?.Trim().Trim('"', '\'');

            BookId = FindTextBetween(pageContents, "var bookId = \"", "\";")?.Trim().Trim('"', '\'');
            var actualUrl = FindTextBetween(pageContents, "var actualUrl =", ";")?.Trim().Trim('"', '\'');
            BookName = actualUrl?.ToLower().Replace("/ebooks/", "").Trim().Trim('/', '\\');
            _logger.LogInformation($"Book Name : {BookName}");

            _pageCount = int.Parse(FindTextBetween(pageContents, "var totalPageCount =", ";")?.Trim().Trim('"', '\'') ?? throw new InvalidOperationException("Unable to parse total page count"));
            _logger.LogInformation($"Page Count: {_pageCount}");
            _outputDirectory = Path.Combine(outputPath, imageFolderName.ToSafeFilename());

            _outputDirectory.EnsureEmptyDirectory();

            var pages = StringToStringArray(FindTextBetween(pageContents, "var pages = [", "];"));
            var pageIds = GetPageIds(BookName, pages.Length, _logger, _cancellationToken);
            var tasks = new ConsumerStarter().StartAsyncConsumers(_threadCount, _cancellationToken, DownloadPage);

            for (var i = 0; i < _pageCount; i++)
            {
                _jobs.Add(new Page { Index = i, PageId = pageIds[i], FolderName = imageFolderName, PageNumber = Path.GetFileNameWithoutExtension(pages[i]), FileName = pages[i] }, _cancellationToken);
            }

            _jobs.CompleteAdding();
            Task.WaitAll(tasks.ToArray(), _cancellationToken);
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

        private string[] GetPageIds(string bookSlug, int pagesCount,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var pageIds = new List<string>();
            int batchSize = 40;
            for (int i = 0; i < pagesCount; i = i + batchSize)
            {
                new RetryPolicyProvider(logger).PageRetryPolicy.ExecuteAsync(async () =>
                {
                    logger.LogInformation($"Fetching page ids for pages {i + 1} to {Math.Min(i + batchSize, pagesCount)}");
                    var data = await HttpHelper.GetTextBody(
                        $"https://www.rekhta.org/EbookData/GetEbookPageIds/?slug={bookSlug}&lang=1&from={i}&count={batchSize}");
                    var pageIdData = JsonConvert.DeserializeObject<PageIdData>(data);
                    pageIds.AddRange(pageIdData.Ids);
                }).Wait(cancellationToken);
            }

            return pageIds.ToArray();
        }

        private void DownloadPage()
        {
            foreach (var page in _jobs.GetConsumingEnumerable(_cancellationToken))
            {
                new RetryPolicyProvider(_logger).PageRetryPolicy.ExecuteAsync(async () =>
                {
                    var data = await HttpHelper.GetTextBody($"https://www.rekhta.org/EbookData/GetEbookFromApi/?pgid={page.PageId}&bkId={BookId}&pgIdx={page.Index}");
                    page.PageData = JsonConvert.DeserializeObject<PageData>(data);

                    var pageImage = await HttpHelper.GetImage($"https://ebooksapi.rekhta.org/images/{page.FolderName}/{page.FileName}");
                    pageImage = ImageHelper.RearrangeImage(pageImage, page.PageData);

                    var filePath = Path.Combine(_outputDirectory, page.FileName);
                    _outputDirectory.CreateIfDirectoryDoesNotExists();
                    filePath.MakeSureFileDoesNotExist();

                    File.WriteAllBytes(filePath, pageImage.ToByteArray());

                    page.PageImagePath = filePath;

                    lock (_lock)
                    {
                        _pages.Add(page);
                        _completeCount++;
                        _logger.LogInformation($"Downloaded page {_completeCount} of {_pageCount}");
                    }
                }).Wait(_cancellationToken);
            }
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