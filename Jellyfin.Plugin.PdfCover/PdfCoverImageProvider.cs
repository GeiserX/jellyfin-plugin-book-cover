using System.Diagnostics;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PdfCover;

/// <summary>
/// Provides cover images for PDF books by extracting the first page via pdftoppm.
/// </summary>
public class PdfCoverImageProvider : IDynamicImageProvider
{
    private readonly ILogger<PdfCoverImageProvider> _logger;
    private bool? _pdftoppmAvailable;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfCoverImageProvider"/> class.
    /// </summary>
    public PdfCoverImageProvider(ILogger<PdfCoverImageProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "PDF Cover";

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Book;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
    }

    /// <inheritdoc />
    public async Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
    {
        var noImage = new DynamicImageResponse { HasImage = false };

        if (!string.Equals(Path.GetExtension(item.Path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return noImage;
        }

        if (!IsPdftoppmAvailable())
        {
            return noImage;
        }

        var config = Plugin.Instance?.Configuration;
        var dpi = config?.Dpi ?? 150;
        var jpegQuality = config?.JpegQuality ?? 85;
        var timeoutSec = config?.TimeoutSeconds ?? 30;

        var tempPrefix = Path.Combine(Path.GetTempPath(), $"jf-pdf-{Guid.NewGuid():N}");

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "pdftoppm",
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.StartInfo.ArgumentList.Add("-jpeg");
            process.StartInfo.ArgumentList.Add("-jpegopt");
            process.StartInfo.ArgumentList.Add($"quality={jpegQuality}");
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-l");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-r");
            process.StartInfo.ArgumentList.Add(dpi.ToString(System.Globalization.CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add("-singlefile");
            process.StartInfo.ArgumentList.Add(item.Path);
            process.StartInfo.ArgumentList.Add(tempPrefix);

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                _logger.LogWarning("pdftoppm timed out after {Timeout}s for {Path}", timeoutSec, item.Path);
                return noImage;
            }

            var jpegPath = tempPrefix + ".jpg";

            if (process.ExitCode != 0 || !File.Exists(jpegPath))
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "pdftoppm exit {Code} for {Path}: {Error}",
                    process.ExitCode,
                    item.Path,
                    stderr.Length > 200 ? stderr[..200] : stderr);
                CleanupTemp(jpegPath);
                return noImage;
            }

            var bytes = await File.ReadAllBytesAsync(jpegPath, cancellationToken).ConfigureAwait(false);
            CleanupTemp(jpegPath);

            if (bytes.Length == 0)
            {
                return noImage;
            }

            return new DynamicImageResponse
            {
                HasImage = true,
                Stream = new MemoryStream(bytes),
                Format = ImageFormat.Jpg
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to extract PDF cover for {Path}", item.Path);
            CleanupTemp(tempPrefix + ".jpg");
            return noImage;
        }
    }

    /// <summary>
    /// Checks whether pdftoppm is available on the system.
    /// </summary>
    internal bool IsPdftoppmAvailable()
    {
        if (_pdftoppmAvailable.HasValue)
        {
            return _pdftoppmAvailable.Value;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "pdftoppm",
                ArgumentList = { "-v" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            process.WaitForExit(5000);

            _pdftoppmAvailable = true;
            _logger.LogInformation("pdftoppm detected — PDF cover extraction enabled");
        }
        catch (Exception)
        {
            _pdftoppmAvailable = false;
            _logger.LogWarning("pdftoppm not found. Install poppler-utils to enable PDF cover extraction");
        }

        return _pdftoppmAvailable.Value;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static void CleanupTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
