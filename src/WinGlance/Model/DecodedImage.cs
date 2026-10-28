using System.Windows.Media.Imaging;

namespace WinGlance.Model;

public sealed record DecodedImage(
    BitmapSource Image,
    int OriginalWidth,
    int OriginalHeight,
    bool IsDownscaled,
    bool IsRaw,
    ExifMetadata? Exif = null
)
{
    public double AspectRatio => OriginalHeight > 0 ? (double)OriginalWidth / OriginalHeight : 1.0;
}
