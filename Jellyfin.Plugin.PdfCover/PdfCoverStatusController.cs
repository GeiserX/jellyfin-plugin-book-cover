using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PdfCover;

/// <summary>
/// API controller exposing pdftoppm availability status for the admin config page.
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
    /// Returns whether pdftoppm is available in the container.
    /// </summary>
    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PdfCoverStatus> GetStatus()
    {
        return new PdfCoverStatus { PdftoppmAvailable = _provider.IsPdftoppmAvailable() };
    }
}

/// <summary>
/// Status response indicating whether the PDF rendering tool is available.
/// </summary>
public class PdfCoverStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether pdftoppm is available.
    /// </summary>
    public bool PdftoppmAvailable { get; set; }
}
