using System;
using System.CommandLine;
using System.Linq;
using System.Net;
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
            Option<string> urlOption = new("--url")
            {
                Description = "Url the book page where you can read the book contents.",
                Required = true,
                Aliases = { "-u" }
            };

            Option<int> tasksOption = new("--tasks")
            {
                Description = "Number of parallel pages to get. Default will be 10 pages.",
                DefaultValueFactory = _ => 10,
                Aliases = { "-t" }
            };

            Option<OutputType> outputOption = new("--output")
            {
                Description =
                    "Type of output to be generated.",
                DefaultValueFactory =
                    _ => OutputType.Pdf,
                Aliases = { "-o" }
            };

            Option<bool> infoOption = new("--info")
            {
                Description =
                    "Tells if only book information is to be read. No download of book would be done if this option is selected.",
                DefaultValueFactory = _ => false,
                Aliases = { "-i" }
            };

            Option<int> qualityOption = new("--quality")
            {
                Description = "JPEG quality (1-100) to use when saving page images. Lower values produce smaller files.",
                DefaultValueFactory = _ => 90,
                Aliases = { "-q" }
            };

            Option<string> proxyOption = new("--proxy")
            {
                Description = "Using proxy:port for downloading.",
                DefaultValueFactory = _ => "",
                Aliases = { "-p" }
            };

            var rootCommand = new RootCommand("Rekhta download tool to download the rekhta books.");
            rootCommand.Options.Add(urlOption);
            rootCommand.Options.Add(tasksOption);
            rootCommand.Options.Add(outputOption);
            rootCommand.Options.Add(infoOption);
            rootCommand.Options.Add(qualityOption);
            rootCommand.Options.Add(proxyOption);

            ParseResult parseResult = rootCommand.Parse(args);
            
            rootCommand.SetAction(async (result, cancellationToken) =>
            {
                if (result.Errors.Count == 0 && Uri.TryCreate(result.GetValue(urlOption), UriKind.Absolute, out var uriResult))
                {
                    string url = result.GetValue(urlOption);
                    int tasks = result.GetValue(tasksOption);
                    OutputType output = result.GetValue(outputOption);
                    bool infoOnly = result.GetValue(infoOption);
                    int quality = result.GetValue(qualityOption);
                    string proxy = result.GetValue(proxyOption);

                    if (!string.IsNullOrWhiteSpace(proxy))
                    {
                        WebRequest.DefaultWebProxy = new WebProxy(proxy);
                    }

                    if (infoOnly)
                    {
                        await GetBookInfo(url, cancellationToken);
                    }
                    else
                    {
                        await DownloadBook(url, tasks, output, quality, cancellationToken);
                    }
                }
                
                return 0;
            });
            
            foreach (var parseError in parseResult.Errors)
            {
                await System.Console.Error.WriteLineAsync(parseError.Message);
                return 1;
            }
            
            return await rootCommand.Parse(args).InvokeAsync();
        }

        private static async Task DownloadBook(string bookUrl, int taskCount, OutputType outputType, int quality, CancellationToken token)
        {
            await new BookExporter(LoggingFactory.CreateLogger(nameof(RekhtaDownloader)))
                .DownloadBook(bookUrl, taskCount, outputType, null, quality, token);
        }

        private static async Task GetBookInfo(string bookUrl, CancellationToken token)
        {
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