using System.Net;

namespace Scarab.Services;

public static class DownloadService
{
    public static async Task<(ArraySegment<byte> Data, string Filename)> FetchAsync(HttpClient client, string address,
        Action<DownloadProgressArgs> progress, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidDataException("Некорректная ссылка на файл мода/API.");
        for (int attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                var (bytes, response) = await client.DownloadBytesWithProgressAsync(uri, new Progress<DownloadProgressArgs>(progress), timeout.Token);
                using (response)
                {
                    string? filename = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
                    if (string.IsNullOrWhiteSpace(filename)) filename = Path.GetFileName(uri.AbsolutePath);
                    filename = Path.GetFileName(Uri.UnescapeDataString(filename.Trim('"')).Replace('\\', '/'));
                    if (string.IsNullOrWhiteSpace(filename)) filename = "download";
                    return (bytes, filename);
                }
            }
            catch (Exception e) when (attempt < 3 && !cancellation.IsCancellationRequested && IsTransient(e))
            {
                Log.Warning(e, "Download attempt {Attempt} failed for {Host}; retrying", attempt, uri.Host);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellation);
            }
        }
    }
    private static bool IsTransient(Exception e)
    {
        if (e is InvalidDataException) return false;
        return e is OperationCanceledException or IOException || e is HttpRequestException h &&
            (h.StatusCode is null || h.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)h.StatusCode.Value >= 500);
    }
}
