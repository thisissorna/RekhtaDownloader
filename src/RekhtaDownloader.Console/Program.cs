using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RekhtaDownloader.Console
{
    public static class Program
    {
        static readonly ILoggerFactory LoggingFactory = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(opt =>
            {
                opt.SingleLine = true;
            })
        );

    public static async Task<int> Main(string[] args)
        {
            var urlOption = new Option<string>(
                name: "--url",
                description: "Url the book page where you can read the book contents.")
                { IsRequired = true };
            urlOption.AddAlias("-u");

            var tasksOption = new Option<int>(
                name: "--tasks",
                description: "Number of parallel pages to get. Default will be 10 pages.",
                getDefaultValue: () => 10);
            tasksOption.AddAlias("-t");

            var outputOption = new Option<OutputType>(
                name: "--output",
                description: "Type of output to be generated.",
                getDefaultValue: () => OutputType.Pdf);
            outputOption.AddAlias("-o");

            var infoOption = new Option<bool>(
                name: "--info",
                description: "Tells if only book information is to be read. No download of book would be done if this option is selected.",
                getDefaultValue: () => false);
            infoOption.AddAlias("-i");

            var qualityOption = new Option<int>(
                name: "--quality",
                description: "JPEG quality (1-100) to use when saving page images. Lower values produce smaller files.",
                getDefaultValue: () => 90);
            qualityOption.AddAlias("-q");

            var rootCommand = new RootCommand("Rekhta download tool to download the rekhta books.");
            rootCommand.AddOption(urlOption);
            rootCommand.AddOption(tasksOption);
            rootCommand.AddOption(outputOption);
            rootCommand.AddOption(infoOption);
            rootCommand.AddOption(qualityOption);

            rootCommand.SetHandler(async (context) =>
            {
                string url = context.ParseResult.GetValueForOption(urlOption);
                int tasks= context.ParseResult.GetValueForOption(tasksOption);
                OutputType output = context.ParseResult.GetValueForOption(outputOption);
                bool infoOnly = context.ParseResult.GetValueForOption(infoOption);
                int quality = context.ParseResult.GetValueForOption(qualityOption);
                var token = context.GetCancellationToken();

                if (infoOnly)
                {
                    await GetBookInfo(url, token);
                }
                else
                {
                    await DownloadBook(url, tasks, output, quality, token);
                }
            });

            await rootCommand.InvokeAsync(args);

            return 0;
        }

        private static async Task DownloadBook(string bookUrl, int taskCount, OutputType outputType, int quality, CancellationToken token)
        {
            //var bookUrl = "https://rekhta.org/ebooks/alfaz-shumara-number-000-jameel-akhtar-magazines-7/";

            await new BookExporter(LoggingFactory.CreateLogger(nameof(RekhtaDownloader)))
                .DownloadBook(bookUrl, taskCount, outputType, null, quality, token);
        }

        private static async Task GetBookInfo(string bookUrl, CancellationToken token)
        {
            //var bookUrl = "https://www.rekhta.org/ebooks/detail/patras-ke-mazameen-patras-bukhari-ebooks-2?lang=ur";
            var logger = LoggingFactory.CreateLogger(nameof(RekhtaDownloader));
            var bookInfo = await new BookExporter(logger).GetBookInformation(bookUrl, token);
            if (bookInfo != null)
            {
                logger.LogInformation("BOOK INFORMATION");
                logger.LogInformation("=======================");
                logger.LogInformation($"TITLE : { bookInfo.Title }");
                if (bookInfo.Authors != null && bookInfo.Authors.Any())
                {
                    logger.LogInformation($"AUTHOR : {bookInfo.Authors.FirstOrDefault()}");
                }

                if (!string.IsNullOrWhiteSpace(bookInfo.Publisher))
                {
                    logger.LogInformation($"PUBLISHER : {bookInfo.Publisher}");
                }

                if (bookInfo.Year > 0)
                {
                    logger.LogInformation($"PUBLISH YEAR : {bookInfo.Year}");
                }
            }
        }
    }
}