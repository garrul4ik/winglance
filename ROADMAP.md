# Glance Development Roadmap

This document outlines the strategic engineering roadmap for **Glance** as a pro photographer tool for macOS.

---

## Architecture Principles
1. **Zero-Latency Presentation (0ms UI thread)**: UI thread only paints pixels and reads input; all decoding, I/O, and metadata manipulation run concurrently in background actors.
2. **Memory Bounding**: Downsampled screen-size decoding for fast folder traversal; full-resolution decode on demand for 100% inspection.
3. **Non-Destructive & Reversible**: All batch operations utilize macOS Trash (`FileManager.trashItem`) or standard sidecars without touching master RAW bits.

---

## Phased Milestones

### Phase 1: Critical Sharpness & Inspection
- [x] **100% 1:1 Pixel Peep (`Z` / Click to Zoom)**:
  - Instant zoom to 100% actual device pixels centered around mouse cursor position.
  - Smooth pan & drag with trackpad / mouse.
  - Instant toggle back to fit-to-screen with `Z` or click.
  - Background full-resolution decode pipeline that doesn't block the screen-bounding lookahead cache.

### Phase 2: Professional Ecosystem Interoperability
- [x] **Industry-Standard XMP Sidecar Sync**:
  - Automatically read & write Adobe XMP sidecar files (`<filename>.xmp`).
  - Full compatibility with Adobe Lightroom Classic, Capture One, Darktable, and Photo Mechanic.
  - Stores Star Ratings (`xmp:Rating`), Color Labels (`xmp:Label` / `photoshop:Urgency`), and Pick status (`crs:PickStatus`) without touching proprietary RAW files.
  - Non-destructive updates that strictly preserve camera raw develop settings, EXIF tags, and copyright information.
  - Synchronized trashing: moving images to macOS Trash safely trashes their matching XMP sidecars to avoid directory orphans.
- [x] **RAW + JPEG & Sibling Stacking**:
  - Automatic grouping of paired RAW + JPEG shots (`_DSC001.ARW` + `_DSC001.JPG`) into a single slide.
  - Instant browsing using the embedded preview, with background RAW linking.
  - 100% pixel peeping seamlessly inspects master uncompressed RAW sensor data.
  - Combined `[ RAW + JPG ]` stack indicator badge in the frosted-glass HUD.
  - Synchronized culling tags & multi-file trashing so RAWs are never orphaned.

### Phase 3: Exposure & Tone Analysis
- [x] **Live Frosted-Glass Histogram (`H`)**:
  - Real-time 256-bin RGB and Rec. 709 Luminance histogram overlay in the corner.
  - SIMD / Accelerate `vImage` calculation (<1ms) offloaded entirely to background tasks.
  - Per-channel color curves with square-root scaling, outlier suppression, and additive RGB blending.
  - Frosted-glass NSVisualEffectView HUD overlay seamlessly toggled via `H`.
- [x] **Exposure Clipping Warnings ("Blinkies" / `O`)**:
  - Overexposure warning overlay for blown highlights (Red `#FF0032`, $\ge 254$).
  - Underexposure warning overlay for crushed shadows (Blue `#0066FF`, $\le 2$).
  - Animated non-destructive pulsing (`CABasicAnimation`) so underlying image details stay readable.
  - Native integration with 100% zoom, panning, and split-screen comparison mode toggled via `O`.
- [x] **Dedicated Frosted-Glass EXIF HUD & Exposure Bar (`I`)**:
  - Top-left HUD panel showing Shutter Speed, Aperture, ISO, Focal Length, Camera, Lens, and Date.
  - Bottom HUD pill displays immediate one-line exposure summary on every photo.

### Phase 4: Output & Workflow Productivity
- [x] **Quick Export / Batch Copy Picks (`E`)**:
  - One-key background copy of all flagged Picks (`P`) or 4+ stars into a `./Picks` subfolder.
  - Full stack & sidecar integrity: copies paired RAW + companion JPEG + XMP sidecars together so files are never orphaned.
  - Background `ExportManager` actor with live progress pill `[ EXPORTING 12/48 ]` in the frosted HUD.
- [x] **Lossless EXIF Orientation Rotation (`[` / `]`)**:
  - `[` rotates 90° CCW, `]` rotates 90° CW.
  - Rewrites EXIF orientation headers in-place via ImageIO bitstream copying without decoding or re-compressing pixels.
  - Synchronizes `tiff:Orientation` in Adobe XMP sidecars for RAW files.
  - Immediate cache invalidation and UI reload for 0ms visual update.
- [x] **Configurable Hands-Free Slideshow (`Return`)**:
  - Automatic timed slideshow mode toggled with `Return`.
  - Non-blocking timer with customizable interval (default 3s).
  - Displays frosted `[ SLIDESHOW 3s ]` HUD badge.
  - Graceful auto-pause or exit upon user navigation, zoom, or Esc.
