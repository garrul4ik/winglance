using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using WinGlance.Engine;
using WinGlance.Model;
using Xunit;

namespace WinGlance.Tests;

public class WinGlanceTests
{
    [Fact]
    public void TestXmpParsingAttributeFormat()
    {
        var xmp = """
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 7.0">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:xmp="http://ns.adobe.com/xap/1.0/"
            xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
            xmlns:crs="http://ns.adobe.com/camera-raw/1.0/"
            xmp:Rating="4"
            xmp:Label="Red"
            crs:PickStatus="1">
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

        var doc = XDocument.Parse(xmp);
        var meta = XmpSidecarManager.Parse(doc);

        Assert.Equal(4, meta.Rating);
        Assert.Equal(PickStatus.Pick, meta.Pick);
        Assert.Equal(ColorLabel.Red, meta.Color);
    }

    [Fact]
    public void TestXmpParsingElementFormat()
    {
        var xmp = """
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:xmp="http://ns.adobe.com/xap/1.0/"
            xmlns:crs="http://ns.adobe.com/camera-raw/1.0/">
           <xmp:Rating>5</xmp:Rating>
           <crs:PickStatus>-1</crs:PickStatus>
           <xmp:Label>Green</xmp:Label>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

        var doc = XDocument.Parse(xmp);
        var meta = XmpSidecarManager.Parse(doc);

        Assert.Equal(5, meta.Rating);
        Assert.Equal(PickStatus.Reject, meta.Pick);
        Assert.Equal(ColorLabel.Green, meta.Color);
    }

    [Fact]
    public void TestXmpRoundtripSerialization()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlanceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var testImg = Path.Combine(tempDir, "_DSC0001.ARW");
            File.WriteAllText(testImg, "dummy");

            var originalMeta = new CullingMetadata(Rating: 5, Pick: PickStatus.Pick, Color: ColorLabel.Yellow, Orientation: 6);
            XmpSidecarManager.Save(originalMeta, testImg);

            var sidecarPath = XmpSidecarManager.ExistingSidecarPath(testImg);
            Assert.NotNull(sidecarPath);
            Assert.True(File.Exists(sidecarPath));

            var loadedMeta = XmpSidecarManager.Read(sidecarPath);
            Assert.Equal(5, loadedMeta.Rating);
            Assert.Equal(PickStatus.Pick, loadedMeta.Pick);
            Assert.Equal(ColorLabel.Yellow, loadedMeta.Color);
            Assert.Equal(6, loadedMeta.Orientation);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TestLosslessRotationTransitions()
    {
        // Clockwise: 1 -> 6 -> 3 -> 8 -> 1
        Assert.Equal(6, LosslessRotationEngine.NextOrientation(1, RotationDirection.Clockwise));
        Assert.Equal(3, LosslessRotationEngine.NextOrientation(6, RotationDirection.Clockwise));
        Assert.Equal(8, LosslessRotationEngine.NextOrientation(3, RotationDirection.Clockwise));
        Assert.Equal(1, LosslessRotationEngine.NextOrientation(8, RotationDirection.Clockwise));

        // Counter-Clockwise: 1 -> 8 -> 3 -> 6 -> 1
        Assert.Equal(8, LosslessRotationEngine.NextOrientation(1, RotationDirection.CounterClockwise));
        Assert.Equal(3, LosslessRotationEngine.NextOrientation(8, RotationDirection.CounterClockwise));
        Assert.Equal(6, LosslessRotationEngine.NextOrientation(3, RotationDirection.CounterClockwise));
        Assert.Equal(1, LosslessRotationEngine.NextOrientation(6, RotationDirection.CounterClockwise));
    }

    [Fact]
    public void TestCullingFilterMatching()
    {
        var pick = new CullingMetadata(Rating: 0, Pick: PickStatus.Pick);
        var reject = new CullingMetadata(Rating: 0, Pick: PickStatus.Reject);
        var fiveStars = new CullingMetadata(Rating: 5, Pick: PickStatus.Unflagged);
        var unrated = new CullingMetadata(Rating: 0, Pick: PickStatus.Unflagged);

        Assert.True(CullingFilter.All.Matches(pick));
        Assert.True(CullingFilter.All.Matches(reject));

        Assert.True(CullingFilter.PicksOnly.Matches(pick));
        Assert.False(CullingFilter.PicksOnly.Matches(reject));

        Assert.False(CullingFilter.HideRejects.Matches(reject));
        Assert.True(CullingFilter.HideRejects.Matches(pick));
        Assert.True(CullingFilter.HideRejects.Matches(fiveStars));

        Assert.True(CullingFilter.RejectsOnly.Matches(reject));
        Assert.False(CullingFilter.RejectsOnly.Matches(pick));

        Assert.True(CullingFilter.UnratedOnly.Matches(unrated));
        Assert.False(CullingFilter.UnratedOnly.Matches(fiveStars));

        var min3 = new CullingFilter(CullingFilterType.MinRating, 3);
        Assert.True(min3.Matches(fiveStars));
        Assert.False(min3.Matches(unrated));
    }

    [Fact]
    public void TestSupportedFormats()
    {
        Assert.True(SupportedFormats.IsSupported("photo.jpg"));
        Assert.True(SupportedFormats.IsSupported("photo.CR3"));
        Assert.True(SupportedFormats.IsSupported("photo.ARW"));
        Assert.True(SupportedFormats.IsSupported("photo.NEF"));
        Assert.True(SupportedFormats.IsSupported("photo.RAF"));
        Assert.True(SupportedFormats.IsSupported("photo.dng"));
        Assert.True(SupportedFormats.IsSupported("photo.webp"));
        Assert.False(SupportedFormats.IsSupported("document.pdf"));
        Assert.False(SupportedFormats.IsSupported("script.ps1"));

        Assert.True(SupportedFormats.IsRaw("photo.ARW"));
        Assert.False(SupportedFormats.IsRaw("photo.PNG"));
    }

    [Fact]
    public void TestHistogramEngineCalculation()
    {
        // 100x100 solid red image (R=255, G=0, B=0, A=255)
        var width = 100;
        var height = 100;
        var stride = width * 4;
        var pixels = new byte[height * stride];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;     // Blue
            pixels[i + 1] = 0; // Green
            pixels[i + 2] = 255; // Red
            pixels[i + 3] = 255; // Alpha
        }

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();

        var hist = HistogramEngine.Compute(bmp);

        Assert.Equal(10000, hist.Red[255]);
        Assert.Equal(10000, hist.Green[0]);
        Assert.Equal(10000, hist.Blue[0]);
        Assert.True(hist.MaxCount >= 10000);
    }

    [Fact]
    public void TestClippingEngineHighlightAndShadow()
    {
        // 2x2 image:
        // px 0: pure white (255,255,255) -> blown highlight
        // px 1: pure black (0,0,0) -> crushed shadow
        // px 2, 3: neutral gray (128,128,128) -> no clipping
        var width = 2;
        var height = 2;
        var stride = width * 4;
        var pixels = new byte[height * stride];

        // Px 0: White
        pixels[0] = 255; pixels[1] = 255; pixels[2] = 255; pixels[3] = 255;
        // Px 1: Black
        pixels[4] = 0; pixels[5] = 0; pixels[6] = 0; pixels[7] = 255;
        // Px 2: Gray
        pixels[8] = 128; pixels[9] = 128; pixels[10] = 128; pixels[11] = 255;
        // Px 3: Gray
        pixels[12] = 128; pixels[13] = 128; pixels[14] = 128; pixels[15] = 255;

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();

        var mask = ClippingEngine.GenerateClippingMask(bmp);
        Assert.NotNull(mask);

        var maskPixels = new byte[height * stride];
        mask.CopyPixels(maskPixels, stride, 0);

        // White pixel should have red warning overlay (B=0x33, G=0x00, R=0xFF, A=0xE1)
        Assert.Equal(0xFF, maskPixels[2]); // Red
        Assert.Equal(0xE1, maskPixels[3]); // Alpha

        // Black pixel should have blue warning overlay (B=0xFF, G=0x66, R=0x00, A=0xE1)
        Assert.Equal(0xFF, maskPixels[4]); // Blue
        Assert.Equal(0xE1, maskPixels[7]); // Alpha

        // Gray pixels should be transparent (Alpha = 0)
        Assert.Equal(0, maskPixels[11]);
        Assert.Equal(0, maskPixels[15]);
    }
}
