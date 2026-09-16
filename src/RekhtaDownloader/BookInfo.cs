#nullable enable

namespace RekhtaDownloader
{
    public sealed record BookInfo(
        string BookUrl,
        string TitleUr,
        string? AuthorNameUr,
        string? PublisherNameUr,
        int TotalPages,
        string BookId,
        string Slug,
        string ImageFolderName,
        string[] PageFileNames);
}
