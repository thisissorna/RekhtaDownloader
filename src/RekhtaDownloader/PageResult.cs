using System.IO;

namespace RekhtaDownloader
{
    public sealed record PageResult(int PageNumber, int TotalPages, Stream ImageStream, string ContentType);
}
