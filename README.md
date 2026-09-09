# Rekhta Downloader

A library to download books from rekhta website.

## Build Status

[![.NET](https://github.com/inshapardaz/RekhtaDownloader/actions/workflows/dotnet.yml/badge.svg)](https://github.com/inshapardaz/RekhtaDownloader/actions/workflows/dotnet.yml)

[![NuGet version](https://img.shields.io/nuget/v/RekhtaDownloader.svg)](https://www.nuget.org/packages/RekhtaDownloader/)


## Usage

Add reference to your c# application. You can access the functionality using the `BookExporter` class.

### Downloading a book

``` c#
var downloader = new BookExporter(new ConsoleLogger());
var path = await downloader.DownloadBook(
    bookUrl: url,
    taskCount: 10,
    output: OutputType.Pdf,
    outputPath: null,
    imageQuality: 90,
    token: CancellationToken.None);
```

`DownloadBook` returns the path to the generated PDF file, or to the folder containing the downloaded images, depending on `output`.

#### Parameters

1. `bookUrl` (`string`, required)

The url of the rekhta book. This is not the rekhta book's details/main page but the page where you can see the actual pages from the book (the reader page). If a details page URL is passed, the library will try to resolve the reader page URL automatically.

2. `taskCount` (`int`, default `10`)

Number of parallel threads to use for downloading the book pages.

3. `output` (`OutputType`, default `OutputType.Pdf`)

Specifies the type of output wanted. Possible values are `OutputType.Pdf` and `OutputType.Images`. In case of Pdf, a single PDF file is created with a name matching the book. In case of Images, the downloaded page images are stored in a folder with a name matching the book.

4. `outputPath` (`string`, default `null`)

Folder in which the PDF file or images folder will be created. When `null`, the current working directory is used.

5. `imageQuality` (`int`, default `90`)

JPEG quality (1-100) used when saving the page images and, consequently, the images embedded in the generated PDF. Lower values produce smaller files at the cost of image quality.

6. `token` (`CancellationToken`, default `CancellationToken.None`)

Token used to cancel the download.

### Reading book information only

If you only want to read a book's metadata (title, author, publisher, year, cover image) without downloading its pages, use `GetBookInformation`:

``` c#
var downloader = new BookExporter(new ConsoleLogger());
var bookInfo = await downloader.GetBookInformation(bookUrl, CancellationToken.None);
```

## CLI application

The repository also includes a console application, `RekhtaDownloader.Console`, which wraps `BookExporter` for command line use.

``` bash
dotnet run --project src/RekhtaDownloader.Console -- --url <bookUrl> [options]
```

Or, once published/installed as a tool:

``` bash
RekhtaDownloader.Console --url <bookUrl> [options]
```

### Options

| Option | Alias | Description | Default |
| --- | --- | --- | --- |
| `--url` | `-u` | Url of the rekhta book page where you can read the book contents. Required. | - |
| `--tasks` | `-t` | Number of parallel pages to download at a time. | `10` |
| `--output` | `-o` | Type of output to generate. Possible values: `Pdf`, `Images`. | `Pdf` |
| `--quality` | `-q` | JPEG quality (1-100) used when saving page images. Lower values produce smaller files. | `90` |
| `--info` | `-i` | Only reads and prints book information (title, author, publisher, year). No pages are downloaded when this is set. | `false` |

### Examples

Download a book as a PDF with default settings:

``` bash
dotnet run --project src/RekhtaDownloader.Console -- --url https://rekhta.org/ebooks/some-book-name/
```

Download a book as individual images, using 20 parallel downloads and a lower image quality to save space:

``` bash
dotnet run --project src/RekhtaDownloader.Console -- -u https://rekhta.org/ebooks/some-book-name/ -o Images -t 20 -q 75
```

Only display book information without downloading:

``` bash
dotnet run --project src/RekhtaDownloader.Console -- -u https://rekhta.org/ebooks/some-book-name/ -i
```
