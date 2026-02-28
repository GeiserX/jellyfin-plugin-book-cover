# Jellyfin Book Cover Plugin

Fallback cover extraction for Jellyfin book libraries. Works alongside the official [Bookshelf plugin](https://github.com/jellyfin/jellyfin-plugin-bookshelf) as a safety net.

- **EPUB**: Searches the archive for cover images by filename (`cover.*`, `portada.*`, `front.*`), by path pattern, or by picking the largest image.
- **PDF**: Renders the first page as a JPEG via `pdftoppm`.

## When does it run?

Jellyfin tries image providers in order. The Bookshelf plugin's "Epub Metadata" provider runs first (using OPF metadata). If it fails to find a cover, this plugin kicks in with a more aggressive search strategy.

## Requirements

- Jellyfin 10.11+
- [Bookshelf plugin](https://github.com/jellyfin/jellyfin-plugin-bookshelf) v13+ (recommended, handles standard EPUB covers)
- `poppler-utils` in the Jellyfin container (only needed for PDF support)

### Installing poppler-utils

Add to your Jellyfin docker-compose entrypoint:

```yaml
entrypoint:
  - /bin/bash
  - -c
  - |
    which pdftoppm > /dev/null 2>&1 || (apt-get update -qq && apt-get install -y -qq --no-install-recommends poppler-utils > /dev/null 2>&1 && rm -rf /var/lib/apt/lists/*)
    exec /jellyfin/jellyfin
```

## Installation

1. Download the latest release from the [Releases](https://github.com/GeiserX/jellyfin-plugin-book-cover/releases) page
2. Extract `Jellyfin.Plugin.PdfCover.dll` into your Jellyfin plugins directory:
   ```
   /config/plugins/PdfCover_2.0.0.0/Jellyfin.Plugin.PdfCover.dll
   ```
3. Restart Jellyfin
4. In each Books library settings, ensure **Book Cover** is listed under Image Fetchers

## Library Configuration

For the plugin to run during scans, the Book type must have image fetchers enabled. In the Jellyfin admin panel, go to each Books library > Settings and ensure the Image Fetchers for the **Book** type include:

1. **Epub Metadata** (from Bookshelf plugin — primary)
2. **Book Cover** (this plugin — fallback)

## Configuration

In the Jellyfin admin panel under **Dashboard > Plugins > Book Cover**:

- **DPI** — Resolution for PDF rendering (default: 150). Higher = better quality, slower.
- **JPEG Quality** — Output compression (default: 85). Lower = smaller files.
- **Timeout** — Max seconds per PDF render (default: 30).

## EPUB Cover Search Strategy

When the Bookshelf plugin fails to extract a cover from an EPUB, this plugin searches the ZIP archive:

1. **By filename**: files named `cover`, `portada`, `front`, `frontcover`, `book_cover` (with image extensions)
2. **By path**: any image file with `cover` in its path (e.g., `OEBPS/Images/cover-image.jpg`)
3. **By size**: the largest image in the archive (>5 KB, to skip icons and logos)

## Building

```bash
dotnet build Jellyfin.Plugin.PdfCover/Jellyfin.Plugin.PdfCover.csproj -c Release
```

Output: `Jellyfin.Plugin.PdfCover/bin/Release/net9.0/Jellyfin.Plugin.PdfCover.dll`
