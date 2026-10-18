using System.Buffers;

namespace Scarab.Util;

public static class HttpClientExt
{
    public const int MaxDownloadBytes = 512 * 1024 * 1024;
    public static async Task<(ArraySegment<byte>, HttpResponseMessage)> DownloadBytesWithProgressAsync(
        this HttpClient self, Uri uri, IProgress<DownloadProgressArgs> progress, CancellationToken cts = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = self.DefaultRequestVersion };
        var response = await self.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            long? expected = response.Content.Headers.ContentLength;
            if (expected is > MaxDownloadBytes or < 0) throw new InvalidDataException("Download exceeds the 512 MiB limit.");
            await using var input = await response.Content.ReadAsStreamAsync(cts).ConfigureAwait(false);
            using var output = new MemoryStream(expected is > 0 ? (int)Math.Min(expected.Value, 1024 * 1024) : 65536);
            var args = new DownloadProgressArgs { TotalBytes = expected is null ? null : (int)expected.Value };
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            progress.Report(args);
            try
            {
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), cts).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > MaxDownloadBytes) throw new InvalidDataException("Download exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cts).ConfigureAwait(false);
                    args.BytesRead += count;
                    progress.Report(args);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (expected is not null && output.Length != expected.Value)
                throw new IOException($"Incomplete download: received {output.Length} of {expected.Value} bytes.");
            args.Completed = true;
            progress.Report(args);
            // Return precisely the downloaded bytes, never MemoryStream's spare capacity.
            return (new ArraySegment<byte>(output.ToArray()), response);
        }
        catch { response.Dispose(); throw; }
    }
}
