using System.Diagnostics;
using System.IO.Compression;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PdfCover;

/// <summary>
/// Fallback cover provider for books and audiobooks. Handles PDFs (first page
/// via pdftoppm), EPUBs (aggressive image search inside the ZIP archive), and
/// audio files (embedded art extraction via ffmpeg raw stream copy).
/// Acts as a safety net when built-in providers fail — particularly for audio
/// files with mislabeled codec tags (e.g. JPEG data tagged as PNG in ID3).
/// </summary>
public class PdfCoverImageProvider : IDynamicImageProvider
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif"
    };

    private static readonly HashSet<string> CoverFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover", "portada", "front", "frontcover", "front_cover", "book_cover"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".opus", ".wma", ".aac", ".wav"
    };

    private readonly ILogger<PdfCoverImageProvider> _logger;
    private bool? _pdftoppmAvailable;
    private string? _ffmpegPath;
    private bool _ffmpegChecked;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfCoverImageProvider"/> class.
    /// </summary>
    public PdfCoverImageProvider(ILogger<PdfCoverImageProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Book Cover";

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Book || item is AudioBook;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
    }

    /// <inheritdoc />
    public async Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
    {
        var path = item.Path;

        if (Directory.Exists(path))
        {
            return await GetFolderAudioCover(path, cancellationToken).ConfigureAwait(false);
        }

        var ext = Path.GetExtension(path);

        if (string.Equals(ext, ".epub", StringComparison.OrdinalIgnoreCase))
        {
            return await GetEpubCover(path, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return await GetPdfCover(path, cancellationToken).ConfigureAwait(false);
        }

        if (AudioExtensions.Contains(ext))
        {
            return await GetAudioCover(path, cancellationToken).ConfigureAwait(false);
        }

        return new DynamicImageResponse { HasImage = false };
    }

    private async Task<DynamicImageResponse> GetEpubCover(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);

            var imageEntries = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && IsImageFile(e.Name))
                .ToList();

            if (imageEntries.Count == 0)
            {
                return new DynamicImageResponse { HasImage = false };
            }

            // Strategy 1: file explicitly named "cover", "portada", etc.
            var coverByName = imageEntries
                .Where(e => CoverFileNames.Contains(Path.GetFileNameWithoutExtension(e.Name)))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();

            if (coverByName != null)
            {
                _logger.LogDebug("EPUB cover by name: {Entry} in {Path}", coverByName.FullName, path);
                return await ExtractZipEntry(coverByName, cancellationToken).ConfigureAwait(false);
            }

            // Strategy 2: "cover" anywhere in the path (e.g. OEBPS/Images/cover-image.jpg)
            var coverInPath = imageEntries
                .Where(e => e.FullName.Contains("cover", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();

            if (coverInPath != null)
            {
                _logger.LogDebug("EPUB cover by path: {Entry} in {Path}", coverInPath.FullName, path);
                return await ExtractZipEntry(coverInPath, cancellationToken).ConfigureAwait(false);
            }

            // Strategy 3: largest image (>5 KB to skip icons/logos)
            var largest = imageEntries
                .Where(e => e.Length > 5_000)
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();

            if (largest != null)
            {
                _logger.LogDebug("EPUB cover by size ({Size} bytes): {Entry} in {Path}", largest.Length, largest.FullName, path);
                return await ExtractZipEntry(largest, cancellationToken).ConfigureAwait(false);
            }

            return new DynamicImageResponse { HasImage = false };
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("Corrupt or unreadable EPUB archive: {Path}", path);
            return new DynamicImageResponse { HasImage = false };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to extract EPUB cover for {Path}", path);
            return new DynamicImageResponse { HasImage = false };
        }
    }

    private static async Task<DynamicImageResponse> ExtractZipEntry(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        using (var stream = entry.Open())
        {
            await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        }

        ms.Position = 0;

        if (ms.Length == 0)
        {
            ms.Dispose();
            return new DynamicImageResponse { HasImage = false };
        }

        var response = new DynamicImageResponse
        {
            HasImage = true,
            Stream = ms
        };

        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
        response.SetFormatFromMimeType(mime);

        return response;
    }

    private static bool IsImageFile(string fileName)
    {
        return ImageExtensions.Contains(Path.GetExtension(fileName));
    }

    private async Task<DynamicImageResponse> GetPdfCover(string path, CancellationToken cancellationToken)
    {
        var noImage = new DynamicImageResponse { HasImage = false };

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
            process.StartInfo.ArgumentList.Add(path);
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
                _logger.LogWarning("pdftoppm timed out after {Timeout}s for {Path}", timeoutSec, path);
                return noImage;
            }

            var jpegPath = tempPrefix + ".jpg";

            if (process.ExitCode != 0 || !File.Exists(jpegPath))
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "pdftoppm exit {Code} for {Path}: {Error}",
                    process.ExitCode,
                    path,
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
            _logger.LogError(ex, "Failed to extract PDF cover for {Path}", path);
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

    private async Task<DynamicImageResponse> GetAudioCover(string path, CancellationToken cancellationToken)
    {
        var noImage = new DynamicImageResponse { HasImage = false };
        var ffmpegPath = GetFfmpegPath();

        if (ffmpegPath == null)
        {
            return noImage;
        }

        var config = Plugin.Instance?.Configuration;
        var timeoutSec = config?.TimeoutSeconds ?? 30;

        // Use .bin extension — the actual format is detected from magic bytes
        var tempFile = Path.Combine(Path.GetTempPath(), $"jf-audio-{Guid.NewGuid():N}.bin");

        try
        {
            // Raw-copy the embedded art stream without re-encoding.
            // This bypasses codec tag validation, which is exactly what fails
            // in Jellyfin's built-in Image Extractor when ID3 tags declare PNG
            // but the actual data is JPEG (or vice versa).
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(path);
            process.StartInfo.ArgumentList.Add("-an");
            process.StartInfo.ArgumentList.Add("-vcodec");
            process.StartInfo.ArgumentList.Add("copy");
            process.StartInfo.ArgumentList.Add(tempFile);

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
                _logger.LogWarning("ffmpeg timed out after {Timeout}s for {Path}", timeoutSec, path);
                return noImage;
            }

            if (!File.Exists(tempFile))
            {
                return noImage;
            }

            var bytes = await File.ReadAllBytesAsync(tempFile, cancellationToken).ConfigureAwait(false);
            CleanupTemp(tempFile);

            if (bytes.Length < 1000)
            {
                return noImage;
            }

            var format = DetectImageFormat(bytes);
            if (format == null)
            {
                _logger.LogDebug("Extracted embedded art but unrecognised image format for {Path}", path);
                return noImage;
            }

            _logger.LogDebug("Extracted {Format} cover ({Size} bytes) from {Path}", format, bytes.Length, path);

            return new DynamicImageResponse
            {
                HasImage = true,
                Stream = new MemoryStream(bytes),
                Format = format.Value
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to extract audio cover for {Path}", path);
            CleanupTemp(tempFile);
            return noImage;
        }
    }

    /// <summary>
    /// For folder-based audiobooks (multi-file chapters), extracts embedded
    /// art from the first audio file in the directory.
    /// </summary>
    private async Task<DynamicImageResponse> GetFolderAudioCover(string dirPath, CancellationToken cancellationToken)
    {
        try
        {
            var audioFile = Directory.EnumerateFiles(dirPath)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (audioFile != null)
            {
                return await GetAudioCover(audioFile, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to scan folder for audio files: {Path}", dirPath);
        }

        return new DynamicImageResponse { HasImage = false };
    }

    /// <summary>
    /// Detects the actual image format from magic bytes, ignoring file
    /// extensions or codec tags which may be incorrect.
    /// </summary>
    private static ImageFormat? DetectImageFormat(byte[] data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ImageFormat.Jpg;
        }

        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return ImageFormat.Png;
        }

        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
        {
            return ImageFormat.Gif;
        }

        if (data.Length > 12
            && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return ImageFormat.Webp;
        }

        return null;
    }

    /// <summary>
    /// Locates ffmpeg — checks the Jellyfin bundled path first, then system PATH.
    /// </summary>
    internal string? GetFfmpegPath()
    {
        if (_ffmpegChecked)
        {
            return _ffmpegPath;
        }

        _ffmpegChecked = true;

        const string jellyfinFfmpeg = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        if (File.Exists(jellyfinFfmpeg))
        {
            _ffmpegPath = jellyfinFfmpeg;
            _logger.LogInformation("Found jellyfin-ffmpeg — audio cover extraction enabled");
            return _ffmpegPath;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            process.WaitForExit(5000);

            _ffmpegPath = "ffmpeg";
            _logger.LogInformation("Found system ffmpeg — audio cover extraction enabled");
        }
        catch (Exception)
        {
            _logger.LogWarning("ffmpeg not found. Audio cover extraction disabled");
            _ffmpegPath = null;
        }

        return _ffmpegPath;
    }
}
