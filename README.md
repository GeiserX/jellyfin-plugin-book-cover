# Jellyfin Book Cover Plugin

Fallback cover extraction for Jellyfin **book and audiobook** libraries. Works alongside the official [Bookshelf plugin](https://github.com/jellyfin/jellyfin-plugin-bookshelf) as a safety net.

- **EPUB**: Searches the archive for cover images by filename (`cover.*`, `portada.*`, `front.*`), by path pattern, or by picking the largest image.
- **PDF**: Renders the first page as a JPEG via `pdftoppm`.
- **Audiobooks**: Extracts embedded artwork from MP3, M4A, M4B, FLAC, OGG, and other audio files via ffmpeg raw stream copy — correctly handling mislabeled codec tags (e.g. JPEG data tagged as PNG in ID3) that break Jellyfin's built-in Image Extractor. Also supports folder-based audiobooks (multi-file chapters).

## When does it run?

Jellyfin tries image providers in order. Built-in providers run first. If they fail to find a cover — or crash on mislabeled embedded art — this plugin kicks in as fallback.

## Requirements

- Jellyfin 10.11+
- [Bookshelf plugin](https://github.com/jellyfin/jellyfin-plugin-bookshelf) v13+ (recommended, handles standard EPUB covers)
- `poppler-utils` in the Jellyfin container (only needed for PDF support)
- `ffmpeg` (bundled with Jellyfin Docker images — no extra install needed for audio covers)

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
   /config/plugins/PdfCover_3.0.0.0/Jellyfin.Plugin.PdfCover.dll
   ```
3. Restart Jellyfin
4. In each Books/Audiobooks library settings, ensure **Book Cover** is listed under Image Fetchers

## Library Configuration

For the plugin to run during scans, image fetchers must be enabled for the relevant types:

**Books library** — Image Fetchers for the **Book** type:
1. **Epub Metadata** (from Bookshelf plugin — primary)
2. **Book Cover** (this plugin — fallback)

**Audiobooks library** — Image Fetchers for the **AudioBook** type:
1. **Image Extractor** (Jellyfin built-in — primary)
2. **Book Cover** (this plugin — fallback for mislabeled embedded art)

## Configuration

In the Jellyfin admin panel under **Dashboard > Plugins > Book Cover**:

- **DPI** — Resolution for PDF rendering (default: 150). Higher = better quality, slower.
- **JPEG Quality** — Output compression (default: 85). Lower = smaller files.
- **Timeout** — Max seconds per extraction (default: 30). Applies to both pdftoppm and ffmpeg.

## How Audio Cover Extraction Works

Jellyfin's built-in Image Extractor uses ffmpeg to decode embedded artwork. This fails when the codec tag doesn't match the actual data (common in MP3 files where JPEG cover art is tagged as PNG in ID3).

This plugin uses `ffmpeg -vcodec copy` to raw-copy the embedded image stream without decoding, then detects the actual format from magic bytes:

| Magic bytes | Format |
|------------|--------|
| `FF D8 FF` | JPEG |
| `89 50 4E 47` | PNG |
| `47 49 46` | GIF |
| `52 49 46 46 ... 57 45 42 50` | WebP |

For folder-based audiobooks (multi-file chapters), the plugin extracts artwork from the first audio file in the directory.

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
