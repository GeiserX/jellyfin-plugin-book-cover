using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PdfCover;

/// <summary>
/// API controller exposing tool availability status for the admin config page.
/// </summary>
[ApiController]
[Route("PdfCover")]
[Authorize(Policy = "RequiresElevation")]
public class PdfCoverStatusController : ControllerBase
{
    private readonly PdfCoverImageProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfCoverStatusController"/> class.
    /// </summary>
    public PdfCoverStatusController(PdfCoverImageProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Returns tool availability status for cover extraction.
    /// </summary>
    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PdfCoverStatus> GetStatus()
    {
        return new PdfCoverStatus
        {
            PdftoppmAvailable = _provider.IsPdftoppmAvailable(),
            FfmpegAvailable = _provider.GetFfmpegPath() != null
        };
    }
}

/// <summary>
/// Status response indicating tool availability for cover extraction.
/// </summary>
public class PdfCoverStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether pdftoppm is available (PDF covers).
    /// </summary>
    public bool PdftoppmAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether ffmpeg is available (audio covers).
    /// </summary>
    public bool FfmpegAvailable { get; set; }
}
