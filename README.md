# Jellyfin PDF Cover Plugin

Extracts the first page of PDF files as cover images for Jellyfin book/magazine libraries.

## Requirements

- Jellyfin 10.11+
- `poppler-utils` installed in the Jellyfin container (provides `pdftoppm`)

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

1. Download the latest release from the [Releases](https://github.com/GeiserX/jellyfin-plugin-pdf-cover/releases) page
2. Extract `Jellyfin.Plugin.PdfCover.dll` into your Jellyfin plugins directory:
   ```
   /config/plugins/PdfCover_1.0.0.0/Jellyfin.Plugin.PdfCover.dll
   ```
3. Restart Jellyfin

The plugin automatically provides cover images for any PDF file in a Books library.

## Configuration

In the Jellyfin admin panel under **Dashboard > Plugins > PDF Cover**:

- **DPI** — Resolution for rendering (default: 150). Higher = better quality, slower.
- **JPEG Quality** — Output compression (default: 85). Lower = smaller files.
- **Timeout** — Max seconds per PDF render (default: 30).

## How It Works

When Jellyfin scans a Books library, this plugin intercepts PDF items and:
1. Calls `pdftoppm` to render the first page as JPEG
2. Returns the image as the Primary (cover) image

The plugin status page shows whether `pdftoppm` is detected in the container.

## Building

```bash
dotnet build -c Release
```

Output: `Jellyfin.Plugin.PdfCover/bin/Release/net9.0/Jellyfin.Plugin.PdfCover.dll`
