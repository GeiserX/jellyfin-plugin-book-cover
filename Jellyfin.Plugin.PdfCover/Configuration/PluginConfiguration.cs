using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PdfCover.Configuration;

/// <summary>
/// Plugin configuration for PDF Cover extraction.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the DPI used for rendering the PDF first page.
    /// Higher values produce better quality but larger images.
    /// </summary>
    public int Dpi { get; set; } = 150;

    /// <summary>
    /// Gets or sets the JPEG quality (1-100) for the output cover image.
    /// </summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>
    /// Gets or sets the timeout in seconds for the pdftoppm process.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}
