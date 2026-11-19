using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Sdcb.LibRaw;
using Vortice.WIC;
using WinGlance.Engine;
using WinGlance.Gpu;
using WinGlance.Gpu.Overlays;
using WinGlance.Model;
using WinGlance.UI;
using WinGlance.Vision;
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
        Assert.Equal(6, LosslessRotationEngine.NextOrientation(1, RotationDirection.Clockwise));
        Assert.Equal(3, LosslessRotationEngine.NextOrientation(6, RotationDirection.Clockwise));
        Assert.Equal(8, LosslessRotationEngine.NextOrientation(3, RotationDirection.Clockwise));
        Assert.Equal(1, LosslessRotationEngine.NextOrientation(8, RotationDirection.Clockwise));

        Assert.Equal(8, LosslessRotationEngine.NextOrientation(1, RotationDirection.CounterClockwise));
        Assert.Equal(3, LosslessRotationEngine.NextOrientation(8, RotationDirection.CounterClockwise));
        Assert.Equal(6, LosslessRotationEngine.NextOrientation(3, RotationDirection.CounterClockwise));
        Assert.Equal(1, LosslessRotationEngine.NextOrientation(6, RotationDirection.CounterClockwise));
    }

    [Fact]
    public void TestLosslessJpegExifRotationPreservesCompressedScanBitForBit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlanceRot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var jpgPath = Path.Combine(tempDir, "portrait.jpg");
            byte[] initialJpeg = CreateTestJpegBytes(80, 40);
            File.WriteAllBytes(jpgPath, initialJpeg);

            byte[] originalScan = ExtractJpegSosToEnd(initialJpeg);
            Assert.True(originalScan.Length > 32);

            var item = new SlideItem(jpgPath, fileSize: initialJpeg.Length);

            // 1st CW rotation: injects APP1 Exif with Orientation = 6 (90 CW)
            int o1 = LosslessRotationEngine.Rotate(item, RotationDirection.Clockwise);
            Assert.Equal(6, o1);

            byte[] afterFirst = File.ReadAllBytes(jpgPath);
            Assert.Equal(originalScan, ExtractJpegSosToEnd(afterFirst));

            // Delete XMP sidecar to prove embedded binary EXIF tag 0x0112 alone drives orientation!
            var sidecar = XmpSidecarManager.ExistingSidecarPath(jpgPath);
            if (sidecar != null) File.Delete(sidecar);

            Assert.Equal(6, FastImageDecoder.ReadExifMetadata(jpgPath)?.Orientation);
            var decodedRotated = FastImageDecoder.Decode(jpgPath);
            Assert.Equal(40, decodedRotated.Width);
            Assert.Equal(80, decodedRotated.Height);

            // 2nd CW rotation: patches existing 0x0112 tag in-place (exact same file length & scan!)
            long sizeBeforeInPlace = new FileInfo(jpgPath).Length;
            int o2 = LosslessRotationEngine.Rotate(item, RotationDirection.Clockwise);
            Assert.Equal(3, o2);
            Assert.Equal(sizeBeforeInPlace, new FileInfo(jpgPath).Length);
            Assert.Equal(3, FastImageDecoder.ReadExifMetadata(jpgPath)?.Orientation);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
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
        Assert.True(SupportedFormats.IsSupported("photo.avif"));
        Assert.True(SupportedFormats.IsSupported("photo.jxl"));
        Assert.True(SupportedFormats.IsSupported("photo.CR3"));
        Assert.True(SupportedFormats.IsSupported("photo.ARW"));
        Assert.True(SupportedFormats.IsSupported("photo.NEF"));
        Assert.True(SupportedFormats.IsSupported("photo.RAF"));
        Assert.True(SupportedFormats.IsSupported("photo.RW2"));
        Assert.True(SupportedFormats.IsSupported("photo.IIQ"));
        Assert.True(SupportedFormats.IsSupported("photo.dng"));
        Assert.True(SupportedFormats.IsSupported("photo.webp"));
        Assert.False(SupportedFormats.IsSupported("document.pdf"));
        Assert.False(SupportedFormats.IsSupported("script.ps1"));

        Assert.True(SupportedFormats.IsRaw("photo.ARW"));
        Assert.True(SupportedFormats.IsRaw("photo.RAF"));
        Assert.True(SupportedFormats.IsRaw("photo.RW2"));
        Assert.False(SupportedFormats.IsRaw("photo.PNG"));
    }

    [Fact]
    public void TestHistogramEngine8BitAnd16Bit()
    {
        // 1. 8-bit BGRA32 (100x100 solid red)
        int width = 100, height = 100, stride8 = width * 4;
        var px8 = new byte[height * stride8];
        for (int i = 0; i < px8.Length; i += 4)
        {
            px8[i] = 0;
            px8[i + 1] = 0;
            px8[i + 2] = 255;
            px8[i + 3] = 255;
        }
        var img8 = new DecodedImage(px8, width, height, stride8, ImagePixelFormat.Bgra32, width, height, false, false);
        var hist8 = HistogramEngine.Compute(img8);
        Assert.Equal(10000, hist8.Red[255]);
        Assert.Equal(10000, hist8.Green[0]);
        Assert.Equal(10000, hist8.Blue[0]);

        // 2. 16-bit RGBA64 (50x50 pure green R=0, G=65535, B=0, A=65535)
        int w16 = 50, h16 = 50, stride16 = w16 * 8;
        var px16 = new byte[h16 * stride16];
        for (int i = 0; i < px16.Length; i += 8)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(i + 2, 2), 65535);
            BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(i + 6, 2), 65535);
        }
        var img16 = new DecodedImage(px16, w16, h16, stride16, ImagePixelFormat.Rgba64, w16, h16, false, true, true);
        var hist16 = HistogramEngine.Compute(img16);
        Assert.Equal(2500, hist16.Green[255]);
        Assert.Equal(2500, hist16.Red[0]);
        Assert.Equal(2500, hist16.Blue[0]);
    }

    [Fact]
    public void TestClippingEngine8BitAnd16Bit()
    {
        // 8-bit 2x2: white, black, gray, gray
        var px8 = new byte[16]
        {
            255, 255, 255, 255,
            0, 0, 0, 255,
            128, 128, 128, 255,
            128, 128, 128, 255
        };
        var img8 = new DecodedImage(px8, 2, 2, 8, ImagePixelFormat.Bgra32, 2, 2, false, false);
        var mask8 = ClippingEngine.GenerateClippingMask(img8);
        Assert.NotNull(mask8);
        Assert.Equal(0xFF, mask8.Pixels[2]); // Red highlight warning
        Assert.Equal(0xE1, mask8.Pixels[3]);
        Assert.Equal(0xFF, mask8.Pixels[4]); // Blue shadow warning
        Assert.Equal(0xE1, mask8.Pixels[7]);
        Assert.Equal(0, mask8.Pixels[11]);   // Midtone transparent

        // 16-bit 2x1: pixel 0 blown (65500), pixel 1 midtone (32768)
        var px16 = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(0, 2), 65500);
        BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(6, 2), 65535);
        BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(8, 2), 32768);
        BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(10, 2), 32768);
        BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(12, 2), 32768);
        BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(14, 2), 65535);

        var img16 = new DecodedImage(px16, 2, 1, 16, ImagePixelFormat.Rgba64, 2, 1, false, true, true);
        var mask16 = ClippingEngine.GenerateClippingMask(img16);
        Assert.NotNull(mask16);
        Assert.Equal(0xFF, mask16.Pixels[2]);
        Assert.Equal(0, mask16.Pixels[7]);
    }

    [Fact]
    public void TestZeroCopyMemoryMappedRawPreviews_Raf_Cr3_Rw2_Arw()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlanceRaw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            byte[] previewJpeg = CreateTestJpegBytes(96, 64);
            Assert.True(previewJpeg.Length > 300);

            // 1. Fujifilm .RAF ("FUJIFILMCCD-RAW " + offset @ 0x54 + len @ 0x58)
            var rafPath = Path.Combine(tempDir, "DSCF0001.RAF");
            var rafBytes = new byte[0x80 + previewJpeg.Length];
            "FUJIFILMCCD-RAW "u8.CopyTo(rafBytes.AsSpan(0, 16));
            BinaryPrimitives.WriteUInt32BigEndian(rafBytes.AsSpan(0x54, 4), 0x80);
            BinaryPrimitives.WriteUInt32BigEndian(rafBytes.AsSpan(0x58, 4), (uint)previewJpeg.Length);
            previewJpeg.CopyTo(rafBytes.AsSpan(0x80));
            File.WriteAllBytes(rafPath, rafBytes);

            var rafDecoded = FastImageDecoder.Decode(rafPath, maxBoundingPixelSize: 1920);
            Assert.True(rafDecoded.IsRaw);
            Assert.Equal(96, rafDecoded.Width);
            Assert.Equal(64, rafDecoded.Height);

            // 2. Canon .CR3 (ISOBMFF uuid container + PRVW box)
            var cr3Path = Path.Combine(tempDir, "IMG_0001.CR3");
            int prvwBoxLen = 8 + 6 + previewJpeg.Length;
            int uuidBoxLen = 8 + 16 + prvwBoxLen;
            var cr3Bytes = new byte[uuidBoxLen];
            BinaryPrimitives.WriteUInt32BigEndian(cr3Bytes.AsSpan(0, 4), (uint)uuidBoxLen);
            "uuid"u8.CopyTo(cr3Bytes.AsSpan(4, 4));
            BinaryPrimitives.WriteUInt32BigEndian(cr3Bytes.AsSpan(24, 4), (uint)prvwBoxLen);
            "PRVW"u8.CopyTo(cr3Bytes.AsSpan(28, 4));
            previewJpeg.CopyTo(cr3Bytes.AsSpan(38));
            File.WriteAllBytes(cr3Path, cr3Bytes);

            var cr3Decoded = FastImageDecoder.Decode(cr3Path, maxBoundingPixelSize: 1920);
            Assert.True(cr3Decoded.IsRaw);
            Assert.Equal(96, cr3Decoded.Width);

            // 3. Panasonic .RW2 (II + magic 0x0055 + IFD0 tag 0x002E JpgFromRaw)
            var rw2Path = Path.Combine(tempDir, "P1000001.RW2");
            var rw2Bytes = new byte[64 + previewJpeg.Length];
            rw2Bytes[0] = (byte)'I'; rw2Bytes[1] = (byte)'I';
            BinaryPrimitives.WriteUInt16LittleEndian(rw2Bytes.AsSpan(2, 2), 0x0055);
            BinaryPrimitives.WriteUInt32LittleEndian(rw2Bytes.AsSpan(4, 4), 8); // IFD0 @ 8
            BinaryPrimitives.WriteUInt16LittleEndian(rw2Bytes.AsSpan(8, 2), 1); // 1 entry
            BinaryPrimitives.WriteUInt16LittleEndian(rw2Bytes.AsSpan(10, 2), 0x002E); // JpgFromRaw
            BinaryPrimitives.WriteUInt16LittleEndian(rw2Bytes.AsSpan(12, 2), 7);
            BinaryPrimitives.WriteUInt32LittleEndian(rw2Bytes.AsSpan(14, 4), (uint)previewJpeg.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(rw2Bytes.AsSpan(18, 4), 64);
            previewJpeg.CopyTo(rw2Bytes.AsSpan(64));
            File.WriteAllBytes(rw2Path, rw2Bytes);

            var rw2Decoded = FastImageDecoder.Decode(rw2Path, maxBoundingPixelSize: 1920);
            Assert.True(rw2Decoded.IsRaw);
            Assert.Equal(96, rw2Decoded.Width);

            // 4. Sony .ARW (TIFF SubIFDs tag 0x014A -> SubIFD 0x0201/0x0202)
            var arwPath = Path.Combine(tempDir, "_DSC0001.ARW");
            var arwBytes = new byte[128 + previewJpeg.Length];
            arwBytes[0] = (byte)'I'; arwBytes[1] = (byte)'I';
            BinaryPrimitives.WriteUInt16LittleEndian(arwBytes.AsSpan(2, 2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(arwBytes.AsSpan(4, 4), 8); // IFD0 @ 8
            BinaryPrimitives.WriteUInt16LittleEndian(arwBytes.AsSpan(8, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(arwBytes.AsSpan(10, 2), 0x014A); // SubIFDs
            BinaryPrimitives.WriteUInt16LittleEndian(arwBytes.AsSpan(12, 2), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(arwBytes.AsSpan(14, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(arwBytes.AsSpan(18, 4), 40); // SubIFD @ 40

            BinaryPrimitives.WriteUInt16LittleEndian(arwBytes.AsSpan(40, 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(arwBytes.AsSpan(42, 2), 0x0201); // Offset
            BinaryPrimitives.WriteUInt16LittleEndian(arwBytes.AsSpan(44, 2), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(arwBytes.AsSpan(46, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(arwBytes.AsSpan(50, 4), 128);

            BinaryPrimitives.WriteUInt16LittleEndian(arwBytes.AsSpan(54, 2), 0x0202); // Length
            BinaryPrimitives.WriteUInt16LittleEndian(arwBytes.AsSpan(56, 2), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(arwBytes.AsSpan(58, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(arwBytes.AsSpan(62, 4), (uint)previewJpeg.Length);

            previewJpeg.CopyTo(arwBytes.AsSpan(128));
            File.WriteAllBytes(arwPath, arwBytes);

            var arwDecoded = FastImageDecoder.Decode(arwPath, maxBoundingPixelSize: 1920);
            Assert.True(arwDecoded.IsRaw);
            Assert.Equal(96, arwDecoded.Width);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TestIccProfileNameDetection_V2AndV4()
    {
        // ICC v4 'mluc' Display P3
        byte[] iccP3 = new byte[220];
        BinaryPrimitives.WriteUInt32BigEndian(iccP3.AsSpan(128, 4), 1); // 1 tag
        "desc"u8.CopyTo(iccP3.AsSpan(132, 4));
        BinaryPrimitives.WriteUInt32BigEndian(iccP3.AsSpan(136, 4), 144); // tag offset
        BinaryPrimitives.WriteUInt32BigEndian(iccP3.AsSpan(140, 4), 60);  // tag size

        "mluc"u8.CopyTo(iccP3.AsSpan(144, 4));
        BinaryPrimitives.WriteUInt32BigEndian(iccP3.AsSpan(152, 4), 1);  // 1 record
        BinaryPrimitives.WriteUInt32BigEndian(iccP3.AsSpan(156, 4), 12); // record size
        byte[] utf16P3 = Encoding.BigEndianUnicode.GetBytes("Display P3");
        BinaryPrimitives.WriteUInt32BigEndian(iccP3.AsSpan(164, 4), (uint)utf16P3.Length);
        BinaryPrimitives.WriteUInt32BigEndian(iccP3.AsSpan(168, 4), 28); // offset relative to 144 -> 172
        utf16P3.CopyTo(iccP3.AsSpan(172));

        Assert.Equal("Display P3", FastImageDecoder.DetectIccProfileName(iccP3));

        // ICC v2 'desc' Adobe RGB (1998)
        byte[] iccAdobe = new byte[220];
        BinaryPrimitives.WriteUInt32BigEndian(iccAdobe.AsSpan(128, 4), 1);
        "desc"u8.CopyTo(iccAdobe.AsSpan(132, 4));
        BinaryPrimitives.WriteUInt32BigEndian(iccAdobe.AsSpan(136, 4), 144);
        BinaryPrimitives.WriteUInt32BigEndian(iccAdobe.AsSpan(140, 4), 48);

        "desc"u8.CopyTo(iccAdobe.AsSpan(144, 4));
        byte[] asciiAdobe = Encoding.ASCII.GetBytes("Adobe RGB (1998)\0");
        BinaryPrimitives.WriteUInt32BigEndian(iccAdobe.AsSpan(152, 4), (uint)asciiAdobe.Length);
        asciiAdobe.CopyTo(iccAdobe.AsSpan(156));

        Assert.Equal("Adobe RGB", FastImageDecoder.DetectIccProfileName(iccAdobe));
    }

    [Fact]
    public void TestEmbeddedLibRawRuntimeLoads1000PlusCameras()
    {
        NativeRuntimeLoader.EnsureLibRawLoaded();
        var cameras = RawContext.SupportedCameras;
        Assert.NotNull(cameras);
        Assert.True(cameras.Length > 1000, $"Expected >1000 supported RAW cameras, got {cameras.Length}");
    }

    [Fact]
    public void TestDirectMlYuNetVisionEngineLoadsAndInfers()
    {
        Assert.True(EyeFocusDetector.IsNeuralEngineAvailable);
        Assert.Contains(EyeFocusDetector.ActiveBackend, new[] { "DirectML GPU", "ONNX CPU" });

        int w = 320, h = 240, stride = w * 4;
        var px = new byte[h * stride];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                // Add crisp checkerboard patch near (200, 80) to also exercise sharpness focus fallback
                bool checker = x is >= 180 and <= 220 && y is >= 60 and <= 100 && ((x + y) & 1) == 0;
                byte v = checker ? (byte)250 : (byte)40;
                px[i] = v;
                px[i + 1] = v;
                px[i + 2] = v;
                px[i + 3] = 255;
            }
        }

        var img = new DecodedImage(px, w, h, stride, ImagePixelFormat.Bgra32, w, h, false, false);
        var stops = EyeFocusDetector.BuildJumpSequence(img);
        Assert.NotEmpty(stops);
        Assert.InRange(stops[0].Point.X, 0f, 1f);
        Assert.InRange(stops[0].Point.Y, 0f, 1f);
    }

    [Fact]
    public void TestRealDngAndCr3FileDecodingAndVision()
    {
        string dngPath = @"C:\_dev\glance\IMGP6306.DNG";
        if (File.Exists(dngPath))
        {
            var dngPreviewBytes = FastImageDecoder.ExtractEmbeddedRawPreviewBytes(dngPath);
            Assert.NotNull(dngPreviewBytes);
            Assert.True(dngPreviewBytes.Length > 1_000_000, $"DNG preview should be > 1MB, was {dngPreviewBytes.Length}");

            var dngPreview = FastImageDecoder.Decode(dngPath, maxBoundingPixelSize: 2560);
            Assert.NotNull(dngPreview);
            Assert.Equal(ImagePixelFormat.Bgra32, dngPreview.PixelFormat);
            Assert.True(dngPreview.Width > 1000 && dngPreview.Height > 1000);

            var dngStops = EyeFocusDetector.BuildJumpSequence(dngPreview);
            Assert.NotEmpty(dngStops);
        }

        string cr3Path = @"C:\_dev\glance\IMG_7353.CR3";
        if (File.Exists(cr3Path))
        {
            var cr3PreviewBytes = FastImageDecoder.ExtractEmbeddedRawPreviewBytes(cr3Path);
            Assert.NotNull(cr3PreviewBytes);
            Assert.True(cr3PreviewBytes.Length > 100_000, $"CR3 preview should be > 100KB, was {cr3PreviewBytes.Length}");

            var cr3Preview = FastImageDecoder.Decode(cr3Path, maxBoundingPixelSize: 2560);
            Assert.NotNull(cr3Preview);
            Assert.Equal(ImagePixelFormat.Bgra32, cr3Preview.PixelFormat);
            Assert.True(cr3Preview.Width > 800 && cr3Preview.Height > 800);

            var cr3Stops = EyeFocusDetector.BuildJumpSequence(cr3Preview);
            Assert.NotEmpty(cr3Stops);
            // Verify eye detected in upper portion of photo
            Assert.True(cr3Stops[0].Point.Y < 0.5f, $"Eye Y should be in upper half, was {cr3Stops[0].Point.Y}");
        }
    }

    [Fact]
    public void TestDpiAwareDrawImageScalingMatchesDrawBitmap()
    {
        var levels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0 };
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware, Vortice.Direct3D11.DeviceCreationFlags.BgraSupport, levels, out Vortice.Direct3D11.ID3D11Device d3d, out Vortice.Direct3D11.ID3D11DeviceContext _);
        if (d3d == null) return;

        using var factory = Vortice.Direct2D1.D2D1.D2D1CreateFactory<Vortice.Direct2D1.ID2D1Factory6>();
        using var dxgi = d3d.QueryInterface<Vortice.DXGI.IDXGIDevice>();
        using var d2dDevice = factory.CreateDevice(dxgi);
        using var dc = d2dDevice.CreateDeviceContext(Vortice.Direct2D1.DeviceContextOptions.None);

        float dpi = 144.0f; // 150% scaling
        float dpiScale = dpi / 96.0f; // 1.5
        dc.SetDpi(dpi, dpi);

        int pixelW = 1024;
        int pixelH = 640;
        float dipW = pixelW / dpiScale;
        float dipH = pixelH / dpiScale;

        var bpTarget = new Vortice.Direct2D1.BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dpi, dpi, Vortice.Direct2D1.BitmapOptions.Target | Vortice.Direct2D1.BitmapOptions.CannotDraw);

        var bpRead = new Vortice.Direct2D1.BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dpi, dpi, Vortice.Direct2D1.BitmapOptions.CpuRead | Vortice.Direct2D1.BitmapOptions.CannotDraw);

        int srcW = 800;
        int srcH = 1200;
        var bpSrc = new Vortice.Direct2D1.BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f, Vortice.Direct2D1.BitmapOptions.None);

        var px = new byte[srcW * srcH * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 100;     // B
            px[i + 1] = 200; // G
            px[i + 2] = 50;  // R
            px[i + 3] = 255; // A
        }

        unsafe
        {
            fixed (byte* p = px)
            {
                using var bmp = dc.CreateBitmap(new Vortice.Mathematics.SizeI(srcW, srcH), (IntPtr)p, (uint)(srcW * 4), bpSrc);

                float fitDipScale = MathF.Min(dipW / srcW, dipH / srcH);
                float drawW = srcW * fitDipScale;
                float drawH = srcH * fitDipScale;
                float left = (dipW - drawW) * 0.5f;
                float top = (dipH - drawH) * 0.5f;
                var dest = new Vortice.RawRectF(left, top, left + drawW, top + drawH);

                // 1. DrawBitmap
                using var target1 = dc.CreateBitmap(new Vortice.Mathematics.SizeI(pixelW, pixelH), IntPtr.Zero, 0, bpTarget);
                dc.Target = target1;
                dc.BeginDraw();
                dc.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 1));
                dc.DrawBitmap(bmp, dest, 1.0f, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
                dc.EndDraw();

                using var read1 = dc.CreateBitmap(new Vortice.Mathematics.SizeI(pixelW, pixelH), IntPtr.Zero, 0, bpRead);
                read1.CopyFromBitmap(target1);
                var box1 = MeasureDrawnBox(read1, pixelW, pixelH);

                // 2. DrawImage with dpiScale-aware scaleX/scaleY
                using var cm = new Vortice.Direct2D1.Effects.ColorManagement(dc);
                cm.SetInput(0, bmp, true);
                using var srgb = dc.CreateColorContext(Vortice.Direct2D1.ColorSpace.Srgb, null, 0);
                using var scRgb = dc.CreateColorContext(Vortice.Direct2D1.ColorSpace.ScRgb, null, 0);
                cm.SourceColorContext = srgb;
                cm.DestinationColorContext = scRgb;
                cm.AlphaMode = Vortice.Direct2D1.ColorManagementAlphaMode.Premultiplied;

                using var target2 = dc.CreateBitmap(new Vortice.Mathematics.SizeI(pixelW, pixelH), IntPtr.Zero, 0, bpTarget);
                dc.Target = target2;
                dc.BeginDraw();
                dc.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 1));

                float scaleX = (drawW * dpiScale) / srcW;
                float scaleY = (drawH * dpiScale) / srcH;
                dc.Transform = System.Numerics.Matrix3x2.CreateScale(scaleX, scaleY) * System.Numerics.Matrix3x2.CreateTranslation(dest.Left, dest.Top);
                dc.DrawImage(cm, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, Vortice.Direct2D1.CompositeMode.SourceOver);
                dc.Transform = System.Numerics.Matrix3x2.Identity;
                dc.EndDraw();

                using var read2 = dc.CreateBitmap(new Vortice.Mathematics.SizeI(pixelW, pixelH), IntPtr.Zero, 0, bpRead);
                read2.CopyFromBitmap(target2);
                var box2 = MeasureDrawnBox(read2, pixelW, pixelH);

                Assert.True(Math.Abs(box1.L - box2.L) <= 1, $"L: {box1.L} vs {box2.L}");
                Assert.True(Math.Abs(box1.T - box2.T) <= 1, $"T: {box1.T} vs {box2.T}");
                Assert.True(Math.Abs(box1.R - box2.R) <= 1, $"R: {box1.R} vs {box2.R}");
                Assert.True(Math.Abs(box1.B - box2.B) <= 1, $"B: {box1.B} vs {box2.B}");
            }
        }
    }

    private static unsafe (int L, int T, int R, int B) MeasureDrawnBox(Vortice.Direct2D1.ID2D1Bitmap1 bmp, int w, int h)
    {
        var map = bmp.Map(Vortice.Direct2D1.MapOptions.Read);
        byte* p = (byte*)map.Bits;
        int pitch = (int)map.Pitch;
        int minX = -1, maxX = -1, minY = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (p[y * pitch + x * 4 + 1] > 50) // Green
                {
                    if (minY == -1) minY = y;
                    maxY = y;
                    if (minX == -1 || x < minX) minX = x;
                    if (maxX == -1 || x > maxX) maxX = x;
                }
            }
        }
        bmp.Unmap();
        return (minX, minY, maxX, maxY);
    }

    private static byte[] CreateTestJpegBytes(int width, int height)
    {
        using var wic = new IWICImagingFactory2();
        int stride = width * 4;
        var pixels = new byte[height * stride];
        var rng = new Random(42);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                pixels[i + 0] = (byte)((x * 3 + rng.Next(40)) & 0xFF);
                pixels[i + 1] = (byte)((y * 5 + rng.Next(40)) & 0xFF);
                pixels[i + 2] = (byte)(((x ^ y) * 7 + rng.Next(40)) & 0xFF);
                pixels[i + 3] = 255;
            }
        }

        using var ms = new MemoryStream();
        using var encoder = wic.CreateEncoder(ContainerFormat.Jpeg, ms, BitmapEncoderCacheOption.NoCache);
        using var frame = encoder.CreateNewFrame(out var props);
        props?.Dispose();
        frame.Initialize();
        frame.SetSize((uint)width, (uint)height);
        Guid fmt = PixelFormat.Format24bppBGR;
        using var bmp = wic.CreateBitmapFromMemory((uint)width, (uint)height, PixelFormat.Format32bppBGRA, pixels, (uint)stride);
        frame.SetPixelFormat(ref fmt);
        frame.WriteSource(bmp);
        frame.Commit();
        encoder.Commit();
        return ms.ToArray();
    }

    private static byte[] ExtractJpegSosToEnd(byte[] jpeg)
    {
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) break;
            while (pos < jpeg.Length && jpeg[pos] == 0xFF) pos++;
            byte marker = jpeg[pos++];
            if (marker == 0xDA) // SOS
            {
                return jpeg[(pos - 2)..];
            }
            ushort len = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos, 2));
            pos += len;
        }
        return Array.Empty<byte>();
    }

    [Fact]
    public void TestCommandMapKeyResolution()
    {
        Assert.Equal(AppCommand.Undo, CommandMap.Resolve(new KeyInput('Z', Ctrl: true, Shift: false, Alt: false), false));
        Assert.Equal(AppCommand.Redo, CommandMap.Resolve(new KeyInput('Z', Ctrl: true, Shift: true, Alt: false), false));
        Assert.Equal(AppCommand.Redo, CommandMap.Resolve(new KeyInput('Y', Ctrl: true, Shift: false, Alt: false), false));
        Assert.Equal(AppCommand.OpenFileDialog, CommandMap.Resolve(new KeyInput('O', Ctrl: true, Shift: false, Alt: false), false));
        Assert.Equal(AppCommand.OpenExternalEditor, CommandMap.Resolve(new KeyInput(CommandMap.VK_RETURN, Ctrl: true, Shift: false, Alt: false), false));
        Assert.Equal(AppCommand.BatchExportPicks, CommandMap.Resolve(new KeyInput('E', Ctrl: true, Shift: false, Alt: false), false));
        Assert.Equal(AppCommand.CycleFilter, CommandMap.Resolve(new KeyInput('F', Ctrl: true, Shift: false, Alt: false), false));

        Assert.Equal(AppCommand.NextImage, CommandMap.Resolve(new KeyInput(CommandMap.VK_SPACE, false, false, false), false));
        Assert.Equal(AppCommand.ToggleSlideshow, CommandMap.Resolve(new KeyInput(CommandMap.VK_RETURN, false, false, false), false));
        Assert.Equal(AppCommand.SlideshowIntervalIncrease, CommandMap.Resolve(new KeyInput(CommandMap.VK_OEM_PLUS, false, false, false), false));
        Assert.Equal(AppCommand.SlideshowIntervalDecrease, CommandMap.Resolve(new KeyInput(CommandMap.VK_OEM_MINUS, false, false, false), false));

        Assert.Equal(AppCommand.CycleFilter, CommandMap.Resolve(new KeyInput(CommandMap.VK_OEM_5, false, false, false), false));
        Assert.Equal(AppCommand.TogglePicksOnlyFilter, CommandMap.Resolve(new KeyInput(CommandMap.VK_OEM_5, false, true, false), false));
        Assert.Equal(AppCommand.ExportCurrent, CommandMap.Resolve(new KeyInput('E', false, false, false), false));
        Assert.Equal(AppCommand.BatchExportPicks, CommandMap.Resolve(new KeyInput('E', false, true, false), false));
        Assert.Equal(AppCommand.ToggleAutoAdvance, CommandMap.Resolve(new KeyInput('C', false, false, false), false));
        Assert.Equal(AppCommand.RotateCW, CommandMap.Resolve(new KeyInput('R', false, false, false), false));
        Assert.Equal(AppCommand.RotateCCW, CommandMap.Resolve(new KeyInput('L', false, false, false), false));

        // Phase 3: Scene Leap, Burst Hero & Burst Knockout shortcuts
        Assert.Equal(AppCommand.PreviousScene, CommandMap.Resolve(new KeyInput(CommandMap.VK_OEM_4, false, false, false), false));
        Assert.Equal(AppCommand.NextScene, CommandMap.Resolve(new KeyInput(CommandMap.VK_OEM_6, false, false, false), false));
        Assert.Equal(AppCommand.PreviousScene, CommandMap.Resolve(new KeyInput(CommandMap.VK_LEFT, true, false, false), false));
        Assert.Equal(AppCommand.NextScene, CommandMap.Resolve(new KeyInput(CommandMap.VK_RIGHT, true, false, false), false));
        Assert.Equal(AppCommand.JumpToBurstHero, CommandMap.Resolve(new KeyInput('B', false, false, false), false));
        Assert.Equal(AppCommand.TogglePick, CommandMap.Resolve(new KeyInput('P', false, false, false), false));
        Assert.Equal(AppCommand.BurstKnockout, CommandMap.Resolve(new KeyInput('P', false, true, false), false));

        // Cheatsheet open intercepts Esc vs other keys
        Assert.Equal(AppCommand.CancelOrExit, CommandMap.Resolve(new KeyInput(CommandMap.VK_ESCAPE, false, false, false), true));
        Assert.Equal(AppCommand.ToggleCheatsheet, CommandMap.Resolve(new KeyInput('P', false, false, false), true));
    }

    [Fact]
    public void TestCullingUndoManagerStackAndCap()
    {
        var mgr = new CullingUndoManager();
        Assert.False(mgr.CanUndo);
        Assert.False(mgr.CanRedo);

        for (int i = 0; i < 105; i++)
        {
            mgr.Record(new RatingAction($"item_{i}", 0, (i % 5) + 1));
        }

        Assert.Equal(CullingUndoManager.MaxHistoryCount, mgr.UndoCount);
        Assert.True(mgr.CanUndo);
        Assert.False(mgr.CanRedo);

        var popped = Assert.IsType<RatingAction>(mgr.PopUndo());
        Assert.Equal("item_104", popped.ItemId);
        Assert.Equal(99, mgr.UndoCount);
        Assert.Equal(1, mgr.RedoCount);

        var redone = Assert.IsType<RatingAction>(mgr.PopRedo());
        Assert.Equal("item_104", redone.ItemId);
        Assert.Equal(100, mgr.UndoCount);
        Assert.Equal(0, mgr.RedoCount);

        // Recording after undo clears redo history
        Assert.NotNull(mgr.PopUndo());
        Assert.True(mgr.CanRedo);
        mgr.Record(new RatingAction("fresh", 0, 5));
        Assert.False(mgr.CanRedo);
    }

    [Fact]
    public void TestDynamicPopulatedFilterCycleAndColorFilter()
    {
        var items = new[]
        {
            new SlideItem("a.jpg"),
            new SlideItem("b.jpg"),
            new SlideItem("c.jpg"),
            new SlideItem("d.jpg")
        };
        var map = new Dictionary<string, CullingMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.jpg"] = new(Rating: 4, Pick: PickStatus.Pick, Color: ColorLabel.Red),
            ["b.jpg"] = new(Rating: 2, Pick: PickStatus.Reject, Color: ColorLabel.Blue),
            ["c.jpg"] = new(Rating: 0, Pick: PickStatus.Unflagged, Color: ColorLabel.None),
            ["d.jpg"] = new(Rating: 4, Pick: PickStatus.Unflagged, Color: ColorLabel.None)
        };

        var svc = new CullingService((m, p, cb) => { });
        svc.SetFolderData(items, map);

        var avail = svc.GetAvailableFilters();
        // Should only include populated filters: All, HideRejects, PicksOnly, 2+ Stars, 4+ Stars, Red, Blue, UnratedOnly, RejectsOnly
        Assert.Contains(CullingFilter.All, avail);
        Assert.Contains(CullingFilter.HideRejects, avail);
        Assert.Contains(CullingFilter.PicksOnly, avail);
        Assert.Contains(new CullingFilter(CullingFilterType.MinRating, 2), avail);
        Assert.Contains(new CullingFilter(CullingFilterType.MinRating, 4), avail);
        Assert.DoesNotContain(new CullingFilter(CullingFilterType.MinRating, 3), avail);
        Assert.DoesNotContain(new CullingFilter(CullingFilterType.MinRating, 5), avail);
        Assert.Contains(CullingFilter.ForColor(ColorLabel.Red), avail);
        Assert.Contains(CullingFilter.ForColor(ColorLabel.Blue), avail);
        Assert.DoesNotContain(CullingFilter.ForColor(ColorLabel.Green), avail);
        Assert.Contains(CullingFilter.UnratedOnly, avail);
        Assert.Contains(CullingFilter.RejectsOnly, avail);

        // Toggle PicksOnly remembers prior filter
        Assert.True(svc.CycleFilter(out _)); // HideRejects
        Assert.Equal(CullingFilter.HideRejects, svc.ActiveFilter);

        Assert.True(svc.TogglePicksOnlyFilter(out _));
        Assert.Equal(CullingFilter.PicksOnly, svc.ActiveFilter);
        Assert.Single(svc.FilteredItems);

        Assert.True(svc.TogglePicksOnlyFilter(out _));
        Assert.Equal(CullingFilter.HideRejects, svc.ActiveFilter);
        Assert.Equal(3, svc.FilteredItems.Count);
    }

    [Fact]
    public async Task TestCullingServiceUndoRedoAllActions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlanceSvc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var jpgPath = Path.Combine(tempDir, "shot.jpg");
            File.WriteAllBytes(jpgPath, CreateTestJpegBytes(64, 48));
            var item = new SlideItem(jpgPath);

            var svc = new CullingService((meta, path, cb) => XmpSidecarManager.Save(meta, path, cb));
            svc.SetFolderData(new[] { item }, new Dictionary<string, CullingMetadata>());

            // 1. Rating + AutoAdvance
            svc.ApplyRating(item, 5, null, out _, out bool autoAdv, out _);
            Assert.True(autoAdv);
            Assert.Equal(5, svc.GetMetadata(item.Id).Rating);

            // 2. Pick
            svc.ApplyPick(item, PickStatus.Pick, true, null, out _, out _, out _);
            Assert.Equal(PickStatus.Pick, svc.GetMetadata(item.Id).Pick);

            // 3. Color
            svc.ApplyColor(item, ColorLabel.Green, null, out _, out _, out _);
            Assert.Equal(ColorLabel.Green, svc.GetMetadata(item.Id).Color);

            // 4. Rotation
            int o1 = svc.ApplyRotation(item, RotationDirection.Clockwise, null);
            Assert.Equal(6, o1);
            Assert.Equal(6, svc.GetMetadata(item.Id).Orientation);

            // Undo rotation -> 1
            var uRot = await svc.UndoAsync();
            Assert.True(uRot.Applied);
            Assert.Contains(item.Id, uRot.InvalidatedItemIds);
            Assert.Equal(1, svc.GetMetadata(item.Id).Orientation);

            // Undo color -> None
            await svc.UndoAsync();
            Assert.Equal(ColorLabel.None, svc.GetMetadata(item.Id).Color);

            // Undo pick -> Unflagged
            await svc.UndoAsync();
            Assert.Equal(PickStatus.Unflagged, svc.GetMetadata(item.Id).Pick);

            // Undo rating -> 0
            await svc.UndoAsync();
            Assert.Equal(0, svc.GetMetadata(item.Id).Rating);

            // Redo all 4 back
            await svc.RedoAsync();
            Assert.Equal(5, svc.GetMetadata(item.Id).Rating);
            await svc.RedoAsync();
            Assert.Equal(PickStatus.Pick, svc.GetMetadata(item.Id).Pick);
            await svc.RedoAsync();
            Assert.Equal(ColorLabel.Green, svc.GetMetadata(item.Id).Color);
            var rRot = await svc.RedoAsync();
            Assert.Contains(item.Id, rRot.InvalidatedItemIds);
            Assert.Equal(6, svc.GetMetadata(item.Id).Orientation);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TestTrashManagerMoveAndRestoreWithUndo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlanceTrash_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var jpgPath = Path.Combine(tempDir, "trash_me.jpg");
            byte[] jpgBytes = CreateTestJpegBytes(40, 30);
            File.WriteAllBytes(jpgPath, jpgBytes);

            var meta = new CullingMetadata(Rating: 3, Pick: PickStatus.Reject, Color: ColorLabel.Red);
            XmpSidecarManager.Save(meta, jpgPath);
            var sidecarPath = XmpSidecarManager.ExistingSidecarPath(jpgPath)!;
            Assert.True(File.Exists(sidecarPath));

            var item = new SlideItem(jpgPath);
            var svc = new CullingService((m, p, cb) => XmpSidecarManager.Save(m, p, cb));
            svc.SetFolderData(new[] { item }, new Dictionary<string, CullingMetadata> { [item.Id] = meta });

            bool trashed = await svc.TrashItemAsync(item);
            Assert.True(trashed);
            Assert.False(File.Exists(jpgPath));
            Assert.False(File.Exists(sidecarPath));
            Assert.Empty(svc.AllItems);

            var undoRes = await svc.UndoAsync();
            Assert.True(undoRes.Applied);
            Assert.True(File.Exists(jpgPath));
            Assert.True(File.Exists(sidecarPath));
            Assert.Equal(jpgBytes, File.ReadAllBytes(jpgPath));
            Assert.Single(svc.AllItems);
            Assert.Equal(3, svc.GetMetadata(item.Id).Rating);

            // Redo puts it back in Recycle Bin
            var redoRes = await svc.RedoAsync();
            Assert.True(redoRes.Applied);
            Assert.False(File.Exists(jpgPath));
            Assert.False(File.Exists(sidecarPath));
            Assert.Empty(svc.AllItems);

            // Undo once more so cleanup or subsequent restore works cleanly
            await svc.UndoAsync();
            Assert.True(File.Exists(jpgPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TestCompoundPurgeRejectsUndoRestoresAllFilesAndSidecars()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlancePurge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var keepPath = Path.Combine(tempDir, "01_keep.jpg");
            var rej1Path = Path.Combine(tempDir, "02_rej.jpg");
            var rej2Raw = Path.Combine(tempDir, "03_rej.CR3");
            var rej2Jpg = Path.Combine(tempDir, "03_rej.jpg");

            File.WriteAllBytes(keepPath, CreateTestJpegBytes(32, 32));
            File.WriteAllBytes(rej1Path, CreateTestJpegBytes(32, 32));
            File.WriteAllText(rej2Raw, "raw-dummy");
            File.WriteAllBytes(rej2Jpg, CreateTestJpegBytes(32, 32));

            var rejMeta = new CullingMetadata(Rating: 0, Pick: PickStatus.Reject);
            XmpSidecarManager.Save(rejMeta, rej1Path);
            XmpSidecarManager.Save(rejMeta, rej2Raw);

            var items = new[]
            {
                new SlideItem(keepPath),
                new SlideItem(rej1Path),
                new SlideItem(rej2Jpg, rawPath: rej2Raw, companionPath: rej2Jpg)
            };
            var map = new Dictionary<string, CullingMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [items[0].Id] = new(Pick: PickStatus.Pick),
                [items[1].Id] = rejMeta,
                [items[2].Id] = rejMeta
            };

            var svc = new CullingService((m, p, cb) => XmpSidecarManager.Save(m, p, cb));
            svc.SetFolderData(items, map);

            int purged = await svc.PurgeRejectsAsync();
            Assert.Equal(2, purged);
            Assert.Single(svc.AllItems);
            Assert.True(File.Exists(keepPath));
            Assert.False(File.Exists(rej1Path));
            Assert.False(File.Exists(rej2Raw));
            Assert.False(File.Exists(rej2Jpg));

            var undoRes = await svc.UndoAsync();
            Assert.True(undoRes.Applied);
            Assert.Equal(3, svc.AllItems.Count);
            Assert.Equal("01_keep.jpg", svc.AllItems[0].Filename);
            Assert.Equal("02_rej.jpg", svc.AllItems[1].Filename);
            Assert.Equal("03_rej.CR3", svc.AllItems[2].Filename);
            Assert.True(File.Exists(rej1Path));
            Assert.True(File.Exists(rej2Raw));
            Assert.True(File.Exists(rej2Jpg));
            Assert.NotNull(XmpSidecarManager.ExistingSidecarPath(rej1Path));
            Assert.NotNull(XmpSidecarManager.ExistingSidecarPath(rej2Raw));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TestRawOrientationDeltaMath()
    {
        Assert.Equal(1, FastImageDecoder.ComputeDeltaOrientation(1, 1));
        Assert.Equal(1, FastImageDecoder.ComputeDeltaOrientation(6, 6));
        Assert.Equal(6, FastImageDecoder.ComputeDeltaOrientation(1, 6)); // 90 CW
        Assert.Equal(6, FastImageDecoder.ComputeDeltaOrientation(6, 3)); // 90 CW from 90
        Assert.Equal(8, FastImageDecoder.ComputeDeltaOrientation(6, 1)); // 270 CW = 90 CCW
        Assert.Equal(3, FastImageDecoder.ComputeDeltaOrientation(6, 8)); // 180
        Assert.Equal(8, FastImageDecoder.ComputeDeltaOrientation(1, 8)); // 270 CW
    }

    [Fact]
    public void TestDirectoryWatcherFilteringAndSelfSuppression()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlanceWatch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var watcher = new DirectoryWatcher(tempDir);

            Assert.False(watcher.ShouldProcessPath(Path.Combine(tempDir, "photo.xmp")));
            Assert.False(watcher.ShouldProcessPath(Path.Combine(tempDir, "photo.xmp.tmp.1234")));
            Assert.False(watcher.ShouldProcessPath(Path.Combine(tempDir, ".hidden.jpg")));
            Assert.False(watcher.ShouldProcessPath(Path.Combine(tempDir, "notes.txt")));

            var imgPath = Path.Combine(tempDir, "DSC_0001.jpg");
            Assert.True(watcher.ShouldProcessPath(imgPath));

            watcher.SuppressPath(imgPath, ttlMs: 5000);
            Assert.False(watcher.ShouldProcessPath(imgPath));

            // Verify real debounced notification when an external image appears
            using var signaled = new ManualResetEventSlim(false);
            watcher.DirectoryChanged += _ => signaled.Set();
            watcher.Start();

            File.WriteAllBytes(Path.Combine(tempDir, "external.jpg"), CreateTestJpegBytes(16, 16));
            Assert.True(signaled.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TestExportSingleAndBatchToSelects()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlanceExport_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var rawPath = Path.Combine(tempDir, "IMG_1001.CR3");
            var jpgPath = Path.Combine(tempDir, "IMG_1001.jpg");
            var otherPath = Path.Combine(tempDir, "IMG_1002.jpg");

            File.WriteAllText(rawPath, "raw");
            File.WriteAllBytes(jpgPath, CreateTestJpegBytes(24, 24));
            File.WriteAllBytes(otherPath, CreateTestJpegBytes(24, 24));

            var stacked = new SlideItem(jpgPath, rawPath: rawPath, companionPath: jpgPath);
            var unpicked = new SlideItem(otherPath);
            XmpSidecarManager.Save(new CullingMetadata(Pick: PickStatus.Pick), rawPath);

            var singleRes = await ExportManager.ExportSingleAsync(unpicked, tempDir);
            Assert.Equal(1, singleRes.ItemCount);
            Assert.True(File.Exists(Path.Combine(tempDir, "Selects", "IMG_1002.jpg")));

            var map = new Dictionary<string, CullingMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [stacked.Id] = new(Pick: PickStatus.Pick),
                [unpicked.Id] = new(Pick: PickStatus.Unflagged)
            };
            var batchRes = await ExportManager.ExportPicksAsync(new[] { stacked, unpicked }, map, tempDir);
            Assert.Equal(1, batchRes.ItemCount);
            Assert.Equal(3, batchRes.FilesWritten); // CR3 + JPG + XMP
            Assert.True(File.Exists(Path.Combine(tempDir, "Selects", "IMG_1001.CR3")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Selects", "IMG_1001.jpg")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Selects", "IMG_1001.xmp")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TestSlidePipelineIdKeyedCacheSurvivesRemoval()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlancePipe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var p1 = Path.Combine(tempDir, "01.jpg");
            var p2 = Path.Combine(tempDir, "02.jpg");
            var p3 = Path.Combine(tempDir, "03.jpg");
            File.WriteAllBytes(p1, CreateTestJpegBytes(32, 32));
            File.WriteAllBytes(p2, CreateTestJpegBytes(32, 32));
            File.WriteAllBytes(p3, CreateTestJpegBytes(32, 32));

            var items = new[] { new SlideItem(p1), new SlideItem(p2), new SlideItem(p3) };
            var pipeline = new SlidePipeline();
            pipeline.SetPlaylist(items, 0, 1920);

            var img2 = await pipeline.GetImageAsync(1);
            var img3 = await pipeline.GetImageAsync(2);

            // Simulate removing item 0 -> item 1 shifts to index 0, item 2 shifts to index 1
            pipeline.UpdateItems(new[] { items[1], items[2] }, 0);

            Assert.True(pipeline.TryGetCachedScreenImage(0, out var cachedAfterShift));
            Assert.Same(img2, cachedAfterShift);
            Assert.True(pipeline.TryGetCachedScreenImage(1, out var cachedAfterShift2));
            Assert.Same(img3, cachedAfterShift2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TestPhase2CommandMapShortcuts()
    {
        Assert.Equal(AppCommand.ToggleTheme, CommandMap.Resolve(new KeyInput('T', false, false, false), false));
        Assert.Equal(AppCommand.ToggleTheme, CommandMap.Resolve(new KeyInput('D', false, false, false), false));
        Assert.Equal(AppCommand.ToggleClipping, CommandMap.Resolve(new KeyInput('O', false, false, false), false));
        Assert.Equal(AppCommand.ToggleFalseColor, CommandMap.Resolve(new KeyInput('O', false, true, false), false));
        Assert.Equal(AppCommand.CycleCursorLoupe, CommandMap.Resolve(new KeyInput('M', false, false, false), false));
        Assert.Equal(AppCommand.ToggleExifDossier, CommandMap.Resolve(new KeyInput('I', false, false, false), false));

        // Cheatsheet open: Space toggles expansion, Esc escapes/collapses, other keys dismiss
        Assert.Equal(AppCommand.ToggleCheatsheetExpanded, CommandMap.Resolve(new KeyInput(CommandMap.VK_SPACE, false, false, false), true));
        Assert.Equal(AppCommand.CancelOrExit, CommandMap.Resolve(new KeyInput(CommandMap.VK_ESCAPE, false, false, false), true));
        Assert.Equal(AppCommand.ToggleCheatsheet, CommandMap.Resolve(new KeyInput('T', false, false, false), true));
    }

    [Fact]
    public void TestAppSettingsPersistenceAndThemeResolution()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlanceCfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgPath = Path.Combine(tempDir, "settings.json");
            var original = new AppSettings
            {
                Theme = ThemePreference.Light,
                AutoAdvance = false
            };

            SettingsStore.Save(original, cfgPath);
            Assert.True(File.Exists(cfgPath));

            var loaded = SettingsStore.Load(cfgPath);
            Assert.Equal(ThemePreference.Light, loaded.Theme);
            Assert.False(loaded.AutoAdvance);

            Assert.Equal(ThemeMode.Dark, SettingsStore.ResolveTheme(ThemePreference.Dark));
            Assert.Equal(ThemeMode.Light, SettingsStore.ResolveTheme(ThemePreference.Light));
            var sysTheme = SettingsStore.ResolveTheme(ThemePreference.System);
            Assert.True(sysTheme is ThemeMode.Dark or ThemeMode.Light);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TestFalseColorEngineIreBands8BitAnd16Bit()
    {
        // Verify all 7 ARRI IRE LUT bands + monochrome midtone
        Assert.Equal((139, 31, 162), FalseColorEngine.MapLuminanceToRgb(0));     // 0 IRE Crush (Purple)
        Assert.Equal((0, 122, 255), FalseColorEngine.MapLuminanceToRgb(8));      // ~3.1 IRE Shadow (Blue)
        Assert.Equal((0, 230, 77), FalseColorEngine.MapLuminanceToRgb(102));     // 40 IRE 18% Gray (Green)
        Assert.Equal((255, 102, 178), FalseColorEngine.MapLuminanceToRgb(138));  // ~54 IRE Skin (Pink)
        Assert.Equal((160, 160, 160), FalseColorEngine.MapLuminanceToRgb(160));  // Midtone monochrome
        Assert.Equal((255, 122, 0), FalseColorEngine.MapLuminanceToRgb(204));    // 80 IRE High (Orange)
        Assert.Equal((255, 230, 0), FalseColorEngine.MapLuminanceToRgb(250));    // ~98 IRE Roll (Yellow)
        Assert.Equal((255, 0, 43), FalseColorEngine.MapLuminanceToRgb(255));     // 100 IRE Clip (Red)

        // 8-bit BGRA32 2x1: black crush & blown white clip
        var px8 = new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 };
        var img8 = new DecodedImage(px8, 2, 1, 8, ImagePixelFormat.Bgra32, 2, 1, false, false);
        var fc8 = FalseColorEngine.GenerateFalseColorImage(img8);
        Assert.NotNull(fc8);
        Assert.Equal(ImagePixelFormat.Bgra32, fc8.PixelFormat);
        // Pixel 0 -> Purple (B=162, G=31, R=139)
        Assert.Equal(162, fc8.Pixels[0]);
        Assert.Equal(31, fc8.Pixels[1]);
        Assert.Equal(139, fc8.Pixels[2]);
        // Pixel 1 -> Red (B=43, G=0, R=255)
        Assert.Equal(43, fc8.Pixels[4]);
        Assert.Equal(0, fc8.Pixels[5]);
        Assert.Equal(255, fc8.Pixels[6]);

        // 16-bit RGBA64 1x1: pure green (R=0, G=36608 -> luma ~102 -> 40 IRE green)
        var px16 = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(2, 2), 142 << 8); // (183 * 142) >> 8 = 101 -> 39.6 IRE (Green)
        BinaryPrimitives.WriteUInt16LittleEndian(px16.AsSpan(6, 2), 65535);
        var img16 = new DecodedImage(px16, 1, 1, 8, ImagePixelFormat.Rgba64, 1, 1, false, true, true);
        var fc16 = FalseColorEngine.GenerateFalseColorImage(img16);
        Assert.NotNull(fc16);
        Assert.Equal(77, fc16.Pixels[0]);   // B
        Assert.Equal(230, fc16.Pixels[1]);  // G
        Assert.Equal(0, fc16.Pixels[2]);    // R
    }

    [Fact]
    public void TestExpandedExifMetadataFormattingAndClipboardSummary()
    {
        var ff = new ExifMetadata(
            CameraMake: "Nikon",
            CameraModel: "Z 8",
            LensModel: "NIKKOR Z 50mm f/1.2 S",
            ShutterSpeed: 1.0 / 500.0,
            Aperture: 1.4,
            Iso: 100,
            FocalLength: 50.0,
            FocalLength35mm: 50,
            ExposureBias: 0.0,
            DateTimeOriginal: "2026:10:15 14:32:00",
            ExposureProgram: 3,
            MeteringMode: 5,
            WhiteBalance: 0,
            FlashCode: 16, // Did not fire (Off)
            ColorSpaceCode: 2,
            Software: "Ver.2.00",
            ShutterCount: 12450
        );

        Assert.Equal("Aperture Priority (A)", ff.FormattedExposureProgram);
        Assert.Equal("Matrix / Evaluative", ff.FormattedMeteringMode);
        Assert.Equal("Auto", ff.FormattedWhiteBalance);
        Assert.Equal("Did not fire (Off)", ff.FormattedFlash);
        Assert.Equal("50mm (Full Frame)", ff.FormattedFocalAndCrop);
        Assert.Equal("0.0 EV", ff.FormattedBiasDetailed);
        Assert.Null(ff.FormattedBias);
        Assert.Equal("Adobe RGB", ff.FormattedColorSpace("sRGB"));
        Assert.Equal("45.4 MP", ExifMetadata.FormatMegapixels(8256, 5504));

        var apsc = ff with { FocalLength = 35.0, FocalLength35mm = 53 };
        Assert.Equal("35mm (53mm eq. · 1.5×)", apsc.FormattedFocalAndCrop);

        string clip = ff.BuildClipboardSummary("DSC_0001.NEF", 8256, 5504, 48_000_000, "Display P3");
        Assert.Contains("DSC_0001.NEF", clip);
        Assert.Contains("45.4 MP", clip);
        Assert.Contains("Aperture Priority (A)", clip);
        Assert.Contains("Display P3", clip);
        Assert.Contains("12,450", clip);
    }

    [Fact]
    public void TestExtendedBinaryExifTagParsing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinGlanceExif_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Build synthetic APP1 Exif JPEG with IFD0 + ExifIFD
            byte[] makeBytes = Encoding.ASCII.GetBytes("Canon\0");
            byte[] swBytes = Encoding.ASCII.GetBytes("Firmware 1.4\0");
            byte[] serialBytes = Encoding.ASCII.GetBytes("SN998877\0");
            byte[] lensMakeBytes = Encoding.ASCII.GetBytes("Canon Inc.\0");

            // Layout inside TIFF block (starts at offset 0):
            // 0..7: TIFF header ("II", 42, ifd0=8)
            // 8..47: IFD0 (3 entries: Make, Software, ExifIFDPointer) -> 2 + 3*12 + 4 = 42 bytes (ends @ 50)
            // 50..139: ExifIFD (7 entries: ExpProg, Metering, Flash, ColorSpace, WB, BodySerial, LensMake) -> 2 + 7*12 + 4 = 90 bytes (ends @ 140)
            // 140..: string heap
            var tiff = new byte[256];
            tiff[0] = (byte)'I'; tiff[1] = (byte)'I';
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2, 2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4, 4), 8);

            int heap = 140;
            uint offMake = (uint)heap; makeBytes.CopyTo(tiff.AsSpan(heap)); heap += makeBytes.Length;
            uint offSw = (uint)heap; swBytes.CopyTo(tiff.AsSpan(heap)); heap += swBytes.Length;
            uint offSerial = (uint)heap; serialBytes.CopyTo(tiff.AsSpan(heap)); heap += serialBytes.Length;
            uint offLensMake = (uint)heap; lensMakeBytes.CopyTo(tiff.AsSpan(heap));

            // IFD0 @ 8 (3 entries)
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8, 2), 3);
            WriteIfdEntry(tiff.AsSpan(10, 12), 0x010F, 2, (uint)makeBytes.Length, offMake);
            WriteIfdEntry(tiff.AsSpan(22, 12), 0x0131, 2, (uint)swBytes.Length, offSw);
            WriteIfdEntry(tiff.AsSpan(34, 12), 0x8769, 4, 1, 50);

            // ExifIFD @ 50 (7 entries)
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(50, 2), 7);
            WriteIfdEntry(tiff.AsSpan(52, 12), 0x8822, 3, 1, 1);  // ExposureProgram = Manual
            WriteIfdEntry(tiff.AsSpan(64, 12), 0x9207, 3, 1, 3);  // MeteringMode = Spot
            WriteIfdEntry(tiff.AsSpan(76, 12), 0x9209, 3, 1, 25); // Flash = Fired Auto
            WriteIfdEntry(tiff.AsSpan(88, 12), 0xA001, 3, 1, 1);  // ColorSpace = sRGB
            WriteIfdEntry(tiff.AsSpan(100, 12), 0xA403, 3, 1, 1); // WhiteBalance = Manual
            WriteIfdEntry(tiff.AsSpan(112, 12), 0xA431, 2, (uint)serialBytes.Length, offSerial);
            WriteIfdEntry(tiff.AsSpan(124, 12), 0xA433, 2, (uint)lensMakeBytes.Length, offLensMake);

            var baseJpeg = CreateTestJpegBytes(32, 24);
            int app1PayloadLen = 6 + tiff.Length;
            var jpgWithExif = new byte[2 + 4 + app1PayloadLen + (baseJpeg.Length - 2)];
            jpgWithExif[0] = 0xFF; jpgWithExif[1] = 0xD8;
            jpgWithExif[2] = 0xFF; jpgWithExif[3] = 0xE1;
            BinaryPrimitives.WriteUInt16BigEndian(jpgWithExif.AsSpan(4, 2), (ushort)(app1PayloadLen + 2));
            "Exif\0\0"u8.CopyTo(jpgWithExif.AsSpan(6, 6));
            tiff.CopyTo(jpgWithExif.AsSpan(12));
            baseJpeg.AsSpan(2).CopyTo(jpgWithExif.AsSpan(12 + tiff.Length));

            var path = Path.Combine(tempDir, "exif_ext.jpg");
            File.WriteAllBytes(path, jpgWithExif);

            var meta = FastImageDecoder.ReadExifMetadata(path);
            Assert.NotNull(meta);
            Assert.Equal("Canon", meta.CameraMake);
            Assert.Equal("Firmware 1.4", meta.Software);
            Assert.Equal("Manual (M)", meta.FormattedExposureProgram);
            Assert.Equal("Spot", meta.FormattedMeteringMode);
            Assert.Equal("Fired (Auto)", meta.FormattedFlash);
            Assert.Equal("Manual", meta.FormattedWhiteBalance);
            Assert.Equal("SN998877", meta.BodySerialNumber);
            Assert.Equal("Canon Inc.", meta.LensMake);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }

        static void WriteIfdEntry(Span<byte> entry, ushort tag, ushort type, uint count, uint valueOrOffset)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(0, 2), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2, 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(4, 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(8, 4), valueOrOffset);
        }
    }

    [Fact]
    public void TestNavigatorMiniMapGeometryAndHitTest()
    {
        var expanded = NavigatorMiniMapOverlay.ComputeCardBounds(1920f, 1080f, collapsed: false);
        var collapsed = NavigatorMiniMapOverlay.ComputeCardBounds(1920f, 1080f, collapsed: true);
        Assert.True(expanded.Right > expanded.Left && expanded.Bottom > expanded.Top);
        Assert.True(collapsed.Bottom - collapsed.Top < expanded.Bottom - expanded.Top);
        Assert.True(expanded.Bottom <= 1080f - 84f + 0.1f); // Does not collide with bottom HUD

        var hdr = NavigatorMiniMapOverlay.ComputeHeaderBounds(expanded);
        Assert.Equal(NavigatorMiniMapOverlay.HeaderHeight, hdr.Bottom - hdr.Top, 2);

        var landscapeThumb = NavigatorMiniMapOverlay.ComputeImageRect(expanded, 6000, 4000);
        Assert.True(landscapeThumb.Left >= expanded.Left + 8f - 0.1f);
        Assert.True(landscapeThumb.Top >= hdr.Bottom);
        double thumbAspect = (landscapeThumb.Right - landscapeThumb.Left) / (landscapeThumb.Bottom - landscapeThumb.Top);
        Assert.InRange(thumbAspect, 1.48, 1.52);

        // Verify safe sizing on 1280x720
        var smallWin = NavigatorMiniMapOverlay.ComputeCardBounds(1280f, 720f, collapsed: false);
        Assert.True(smallWin.Top >= 64f);
        Assert.True(smallWin.Bottom <= 720f - 84f + 0.1f);
    }

    [Fact]
    public void TestExifDossierHoverAnimationBounds()
    {
        var compact = ExifDossierOverlay.ComputeBounds(1920f, 1080f, 0f);
        var mid = ExifDossierOverlay.ComputeBounds(1920f, 1080f, 0.5f);
        var expanded = ExifDossierOverlay.ComputeBounds(1920f, 1080f, 1f);

        Assert.True(compact.Top >= 64f); // Clears Top HUD
        Assert.True(expanded.Right - expanded.Left > mid.Right - mid.Left);
        Assert.True(mid.Right - mid.Left > compact.Right - compact.Left);
        Assert.True(expanded.Bottom - expanded.Top > compact.Bottom - compact.Top);

        // Verify clamped within smaller 720p viewport
        var smallExpanded = ExifDossierOverlay.ComputeBounds(1280f, 720f, 1f);
        Assert.True(smallExpanded.Left >= 20f);
        Assert.True(smallExpanded.Bottom <= 720f - 80f);
    }

    // =========================================================================
    // Phase 3: Burst & Scene Intelligence (WinGlance 1.5)
    // =========================================================================

    [Fact]
    public void TestSubSecExifCaptureTimeParsing()
    {
        var exSingleDigit = new ExifMetadata(DateTimeOriginal: "2026:07:15 14:30:05", SubSecTimeOriginal: "5");
        Assert.Equal(new DateTime(2026, 7, 15, 14, 30, 5, 500), exSingleDigit.ParsedCaptureTime);

        var exTwoDigits = new ExifMetadata(DateTimeOriginal: "2026:07:15 14:30:05", SubSecTimeOriginal: "05");
        Assert.Equal(new DateTime(2026, 7, 15, 14, 30, 5, 50), exTwoDigits.ParsedCaptureTime);

        var exTrimmed = new ExifMetadata(DateTimeOriginal: "2026:07:15 14:30:05", SubSecTimeOriginal: "250 \0");
        Assert.Equal(new DateTime(2026, 7, 15, 14, 30, 5, 250), exTrimmed.ParsedCaptureTime);

        // End-to-end binary JPEG APP1 EXIF extraction (tag 0x9003 + tag 0x9291)
        string tmp = Path.Combine(Path.GetTempPath(), $"winglance_subsec_{Guid.NewGuid():N}.jpg");
        try
        {
            byte[] jpg = BuildSyntheticJpegWithExif(64, 48, patternMode: 0, blurRadius: 0,
                dateTimeOrig: "2026:07:15 14:30:05", subSec: "250");
            File.WriteAllBytes(tmp, jpg);

            var exif = FastImageDecoder.ReadExifMetadata(tmp);
            Assert.NotNull(exif);
            Assert.Equal("2026:07:15 14:30:05", exif!.DateTimeOriginal);
            Assert.Equal("250", exif.SubSecTimeOriginal);
            Assert.Equal(new DateTime(2026, 7, 15, 14, 30, 5, 250), exif.ParsedCaptureTime);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void TestSceneDetectionPerceptualHashAndClustering()
    {
        var sceneA1 = BuildSyntheticDecodedImage(128, 128, patternMode: 0, blurRadius: 0, brightnessOffset: 0,
            captureTime: new DateTime(2026, 7, 15, 10, 0, 0, 0));
        // Exposure +20 and 2px framing shift: still same burst/scene
        var sceneA2 = BuildSyntheticDecodedImage(128, 128, patternMode: 0, blurRadius: 0, brightnessOffset: 18,
            captureTime: new DateTime(2026, 7, 15, 10, 0, 0, 200));
        // Completely different composition & cool color palette, 45s later
        var sceneB = BuildSyntheticDecodedImage(128, 128, patternMode: 1, blurRadius: 0, brightnessOffset: 0,
            captureTime: new DateTime(2026, 7, 15, 10, 0, 45, 0));

        var fpA1 = SceneDetectionEngine.ExtractFingerprint(sceneA1);
        var fpA2 = SceneDetectionEngine.ExtractFingerprint(sceneA2);
        var fpB = SceneDetectionEngine.ExtractFingerprint(sceneB);

        var cmpSame = SceneDetectionEngine.Compare(fpA1, fpA2);
        var cmpDiff = SceneDetectionEngine.Compare(fpA1, fpB);

        Assert.True(cmpSame.IsSameScene);
        Assert.True(cmpSame.Similarity >= SceneDetectionEngine.SameSceneThreshold);
        Assert.False(cmpDiff.IsSameScene);
        Assert.True(cmpDiff.Similarity < SceneDetectionEngine.SameSceneThreshold);

        // Edge case: solid flat colors have AC DCT = 0; HSV Hellinger histogram still separates distinct colors
        var solidRed = BuildSyntheticDecodedImage(64, 64, patternMode: 2, blurRadius: 0, brightnessOffset: 0, captureTime: null);
        var solidBlue = BuildSyntheticDecodedImage(64, 64, patternMode: 3, blurRadius: 0, brightnessOffset: 0, captureTime: null);
        var cmpSolid = SceneDetectionEngine.Compare(
            SceneDetectionEngine.ExtractFingerprint(solidRed),
            SceneDetectionEngine.ExtractFingerprint(solidBlue));
        Assert.False(cmpSolid.IsSameScene);
        Assert.True(cmpSolid.HistogramDistance > 0.9);
    }

    [Fact]
    public async Task TestSceneBoundaryAndBurstBoundsNavigation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"winglance_scenes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Indices 0,1,2: Burst Scene A (+0.0s, +0.3s, +0.6s)
            // Indices 3,4:   Burst Scene B (+15.0s, +15.4s)
            // Index 5:       Single Shot Scene C (+42.0s)
            var specs = new (string Name, int Pattern, string Date, string SubSec)[]
            {
                ("01_a1.jpg", 0, "2026:07:15 12:00:00", "000"),
                ("02_a2.jpg", 0, "2026:07:15 12:00:00", "300"),
                ("03_a3.jpg", 0, "2026:07:15 12:00:00", "600"),
                ("04_b1.jpg", 1, "2026:07:15 12:00:15", "000"),
                ("05_b2.jpg", 1, "2026:07:15 12:00:15", "400"),
                ("06_c1.jpg", 2, "2026:07:15 12:00:42", "000")
            };

            var items = new List<SlideItem>();
            foreach (var (name, pat, dt, ss) in specs)
            {
                string path = Path.Combine(tempDir, name);
                File.WriteAllBytes(path, BuildSyntheticJpegWithExif(96, 96, pat, blurRadius: 0, dt, ss));
                items.Add(new SlideItem(path, fileSize: 1024));
            }

            var engine = new SceneDetectionEngine();

            // Forward scene leaps: A(0) -> B(3) -> C(5)
            int? nextFrom0 = await engine.FindSceneBoundaryAsync(items, 0, +1);
            Assert.Equal(3, nextFrom0);

            int? nextFrom3 = await engine.FindSceneBoundaryAsync(items, 3, +1);
            Assert.Equal(5, nextFrom3);

            int? nextFrom5 = await engine.FindSceneBoundaryAsync(items, 5, +1);
            Assert.Null(nextFrom5);

            // Backward scene leaps land symmetrically on scene starts: C(5) -> B(3) -> A(0)
            int? prevFrom5 = await engine.FindSceneBoundaryAsync(items, 5, -1);
            Assert.Equal(3, prevFrom5);

            int? prevFrom3 = await engine.FindSceneBoundaryAsync(items, 3, -1);
            Assert.Equal(0, prevFrom3);

            // Burst clustering bounds & elapsed delta formatting
            var burstA = await engine.ComputeBurstBoundsAsync(items, 1);
            Assert.NotNull(burstA);
            Assert.Equal(0, burstA!.GlobalStartIndex);
            Assert.Equal(3, burstA.BurstCount);
            Assert.Equal(1, burstA.CurrentBurstIndex);
            Assert.Equal("+0.3s", burstA.ElapsedSecondsText);

            var singleC = await engine.ComputeBurstBoundsAsync(items, 5);
            Assert.Null(singleC);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TestAestheticsEngineSharpnessAndExposureRanking()
    {
        var sharpBalanced = BuildSyntheticDecodedImage(160, 160, patternMode: 4, blurRadius: 0, brightnessOffset: 0, captureTime: null);
        var blurrySameScene = BuildSyntheticDecodedImage(160, 160, patternMode: 4, blurRadius: 4, brightnessOffset: 0, captureTime: null);
        var clippedBright = BuildSyntheticDecodedImage(160, 160, patternMode: 4, blurRadius: 4, brightnessOffset: 170, captureTime: null);

        var sharpRes = AestheticsEngine.Analyze(sharpBalanced, detectFaces: false);
        var blurryRes = AestheticsEngine.Analyze(blurrySameScene, detectFaces: false);
        var clippedRes = AestheticsEngine.Analyze(clippedBright, detectFaces: false);

        Assert.True(sharpRes.SharpnessNorm > blurryRes.SharpnessNorm + 0.25);
        Assert.True(sharpRes.ExposureNorm > clippedRes.ExposureNorm + 0.25);
        Assert.True(sharpRes.OverallScore > blurryRes.OverallScore);
        Assert.True(blurryRes.OverallScore > clippedRes.OverallScore);

        // 0.3 matchup tie tolerance and culture-invariant formatting
        var a = new AestheticScoreResult(7.8, 0.8, 0.8, 0, false);
        var bTie = new AestheticScoreResult(7.6, 0.75, 0.8, 0, false);
        var bClearLoss = new AestheticScoreResult(7.2, 0.7, 0.8, 0, false);
        Assert.Equal(0, AestheticScoreResult.CompareMatchup(a, bTie));
        Assert.Equal(1, AestheticScoreResult.CompareMatchup(a, bClearLoss));
        Assert.Equal(-1, AestheticScoreResult.CompareMatchup(bClearLoss, a));
        Assert.Equal("7.8", a.FormattedScore);
    }

    [Fact]
    public async Task TestBurstHeroArgmaxSelection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"winglance_hero_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var items = new List<SlideItem>();
            int[] blurs = { 5, 4, 0, 5 }; // Frame index 2 is tack sharp; others motion-blurred
            for (int i = 0; i < blurs.Length; i++)
            {
                string name = $"burst_{i:00}.jpg";
                string path = Path.Combine(tempDir, name);
                File.WriteAllBytes(path, BuildSyntheticJpegWithExif(160, 160, patternMode: 4, blurRadius: blurs[i],
                    dateTimeOrig: "2026:07:15 16:00:00", subSec: (i * 250).ToString("000", System.Globalization.CultureInfo.InvariantCulture)));
                items.Add(new SlideItem(path, fileSize: 2048));
            }

            var engine = new AestheticsEngine();
            var eval = await engine.EvaluateBurstHeroAsync(items);

            Assert.NotNull(eval);
            Assert.Equal(2, eval!.BurstHeroLocalIndex);
            Assert.Equal(items[2].Id, eval.HeroItem.Id);
            Assert.True(eval.HeroScore.OverallScore > eval.FrameScores[0].OverallScore);
            Assert.True(eval.HeroScore.OverallScore > eval.FrameScores[1].OverallScore);
            Assert.True(eval.HeroScore.OverallScore > eval.FrameScores[3].OverallScore);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TestBurstKnockoutCompoundUndo()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"winglance_ko_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var items = new List<SlideItem>();
            for (int i = 0; i < 4; i++)
            {
                string p = Path.Combine(tempDir, $"shot_{i}.jpg");
                File.WriteAllBytes(p, CreateTestJpegBytes(32, 32));
                items.Add(new SlideItem(p, fileSize: 512));
            }

            var service = new CullingService();
            service.SetFolderData(items, new Dictionary<string, CullingMetadata>(), resetUndo: true);

            bool changed = service.ApplyBurstKnockout(items, items[2], onBeforeWrite: null,
                out string toast, out bool anyRemoved);

            Assert.True(changed);
            Assert.False(anyRemoved);
            Assert.Contains("Winner", toast);
            Assert.Equal(PickStatus.Reject, service.GetMetadata(items[0].Id).Pick);
            Assert.Equal(PickStatus.Reject, service.GetMetadata(items[1].Id).Pick);
            Assert.Equal(PickStatus.Pick, service.GetMetadata(items[2].Id).Pick);
            Assert.Equal(PickStatus.Reject, service.GetMetadata(items[3].Id).Pick);

            // Single Undo reverts all 4 Pick/Reject mutations atomically
            var undoRes = await service.UndoAsync();
            Assert.True(undoRes.Applied);
            foreach (var it in items)
            {
                Assert.Equal(PickStatus.Unflagged, service.GetMetadata(it.Id).Pick);
            }

            // Single Redo restores winner Pick and sibling Reject flags atomically
            var redoRes = await service.RedoAsync();
            Assert.True(redoRes.Applied);
            Assert.Equal(PickStatus.Pick, service.GetMetadata(items[2].Id).Pick);
            Assert.Equal(PickStatus.Reject, service.GetMetadata(items[0].Id).Pick);
            Assert.Equal(PickStatus.Reject, service.GetMetadata(items[3].Id).Pick);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static DecodedImage BuildSyntheticDecodedImage(
        int width,
        int height,
        int patternMode,
        int blurRadius,
        int brightnessOffset,
        DateTime? captureTime)
    {
        byte[] bgra = GenerateSyntheticBgraPixels(width, height, patternMode, blurRadius, brightnessOffset);
        ExifMetadata? exif = captureTime is { } dt
            ? new ExifMetadata(
                DateTimeOriginal: dt.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                SubSecTimeOriginal: dt.Millisecond.ToString("000", System.Globalization.CultureInfo.InvariantCulture))
            : null;
        return new DecodedImage(bgra, width, height, width * 4, ImagePixelFormat.Bgra32, width, height, isDownscaled: false, isRaw: false, exif: exif);
    }

    private static byte[] GenerateSyntheticBgraPixels(int w, int h, int patternMode, int blurRadius, int brightnessOffset)
    {
        byte[] raw = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte r, g, b;
                switch (patternMode)
                {
                    case 0: // Warm sunset arch scene
                        bool inArch = Math.Abs(x - w / 2) < w / 5 && y > h / 3;
                        r = (byte)(inArch ? 35 : 225 - y * 80 / Math.Max(1, h));
                        g = (byte)(inArch ? 28 : 135 - y * 50 / Math.Max(1, h));
                        b = (byte)(inArch ? 24 : 55);
                        break;
                    case 1: // Cool blue-cyan lattice scene (high diagonal/vertical frequency)
                        bool bar = ((x / 12) + (y / 12)) % 2 == 0;
                        r = (byte)(bar ? 25 : 60);
                        g = (byte)(bar ? 180 : 95);
                        b = (byte)(bar ? 235 : 210);
                        break;
                    case 2: // Solid crimson
                        r = 215; g = 30; b = 30;
                        break;
                    case 3: // Solid royal blue
                        r = 25; g = 55; b = 220;
                        break;
                    default: // High-frequency central focus test target inside gray surround
                        bool inCenter = x >= w / 5 && x < (w * 4) / 5 && y >= h / 5 && y < (h * 4) / 5;
                        bool fineCheck = ((x / 4) ^ (y / 4)) % 2 == 0;
                        byte v = inCenter ? (byte)(fineCheck ? 230 : 25) : (byte)128;
                        r = v; g = v; b = v;
                        break;
                }

                int off = (y * w + x) * 4;
                raw[off] = (byte)Math.Clamp(b + brightnessOffset, 0, 255);
                raw[off + 1] = (byte)Math.Clamp(g + brightnessOffset, 0, 255);
                raw[off + 2] = (byte)Math.Clamp(r + brightnessOffset, 0, 255);
                raw[off + 3] = 255;
            }
        }

        if (blurRadius <= 0) return raw;

        byte[] blurred = new byte[raw.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sr = 0, sg = 0, sb = 0, cnt = 0;
                for (int dy = -blurRadius; dy <= blurRadius; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1);
                    for (int dx = -blurRadius; dx <= blurRadius; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1);
                        int srcOff = (yy * w + xx) * 4;
                        sb += raw[srcOff];
                        sg += raw[srcOff + 1];
                        sr += raw[srcOff + 2];
                        cnt++;
                    }
                }
                int dstOff = (y * w + x) * 4;
                blurred[dstOff] = (byte)(sb / cnt);
                blurred[dstOff + 1] = (byte)(sg / cnt);
                blurred[dstOff + 2] = (byte)(sr / cnt);
                blurred[dstOff + 3] = 255;
            }
        }
        return blurred;
    }

    private static byte[] BuildSyntheticJpegWithExif(
        int width,
        int height,
        int patternMode,
        int blurRadius,
        string dateTimeOrig,
        string? subSec)
    {
        byte[] bgra = GenerateSyntheticBgraPixels(width, height, patternMode, blurRadius, 0);

        using var ms = new MemoryStream();
        using var wic = new IWICImagingFactory2();
        using (var encoder = wic.CreateEncoder(ContainerFormat.Jpeg, ms, BitmapEncoderCacheOption.NoCache))
        {
            using var frame = encoder.CreateNewFrame(out var props);
            props?.Dispose();
            frame.Initialize();
            frame.SetSize((uint)width, (uint)height);
            Guid fmt = PixelFormat.Format24bppBGR;
            using var bmp = wic.CreateBitmapFromMemory((uint)width, (uint)height, PixelFormat.Format32bppBGRA, bgra, (uint)(width * 4));
            frame.SetPixelFormat(ref fmt);
            frame.WriteSource(bmp);
            frame.Commit();
            encoder.Commit();
        }
        byte[] rawJpeg = ms.ToArray();

        // Build little-endian TIFF with IFD0 -> ExifIFD (0x8769) -> DateTimeOriginal (0x9003) + SubSecTimeOriginal (0x9291)
        byte[] dtBytes = System.Text.Encoding.ASCII.GetBytes(dateTimeOrig + '\0');
        byte[]? ssBytes = string.IsNullOrEmpty(subSec) ? null : System.Text.Encoding.ASCII.GetBytes(subSec + '\0');
        int exifEntries = ssBytes != null ? 2 : 1;
        int exifIfdOffset = 26;
        int dataStart = exifIfdOffset + 2 + exifEntries * 12 + 4;
        int ssExtraLen = (ssBytes != null && ssBytes.Length > 4) ? ssBytes.Length : 0;
        byte[] tiff = new byte[dataStart + dtBytes.Length + ssExtraLen];

        // Header
        tiff[0] = (byte)'I'; tiff[1] = (byte)'I';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2, 2), 42);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4, 4), 8);

        // IFD0: 1 entry (0x8769 ExifIFDPointer)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8, 2), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10, 2), 0x8769);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12, 2), 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(18, 4), (uint)exifIfdOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(22, 4), 0);

        // ExifIFD
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(exifIfdOffset, 2), (ushort)exifEntries);
        int e0 = exifIfdOffset + 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(e0, 2), 0x9003);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(e0 + 2, 2), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(e0 + 4, 4), (uint)dtBytes.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(e0 + 8, 4), (uint)dataStart);
        Buffer.BlockCopy(dtBytes, 0, tiff, dataStart, dtBytes.Length);

        if (ssBytes != null)
        {
            int e1 = e0 + 12;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(e1, 2), 0x9291);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(e1 + 2, 2), 2);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(e1 + 4, 4), (uint)ssBytes.Length);
            if (ssBytes.Length <= 4)
            {
                Buffer.BlockCopy(ssBytes, 0, tiff, e1 + 8, ssBytes.Length);
            }
            else
            {
                int ssOff = dataStart + dtBytes.Length;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(e1 + 8, 4), (uint)ssOff);
                Buffer.BlockCopy(ssBytes, 0, tiff, ssOff, ssBytes.Length);
            }
        }

        // Construct APP1 segment and splice immediately after SOI (FF D8)
        int app1Len = 2 + 6 + tiff.Length;
        byte[] result = new byte[2 + 2 + app1Len + (rawJpeg.Length - 2)];
        result[0] = rawJpeg[0];
        result[1] = rawJpeg[1];
        result[2] = 0xFF;
        result[3] = 0xE1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), (ushort)app1Len);
        System.Text.Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(result, 6);
        Buffer.BlockCopy(tiff, 0, result, 12, tiff.Length);
        Buffer.BlockCopy(rawJpeg, 2, result, 12 + tiff.Length, rawJpeg.Length - 2);
        return result;
    }

    // =========================================================================
    // Phase 4 & 5: Multi-Up, King of the Hill, Face Dock, Filmstrip & Profiles
    // =========================================================================

    [Fact]
    public void TestMultiUpModeCycleAndPhase45CommandMap()
    {
        Assert.Equal(MultiUpMode.TwoUp, MultiUpMode.Single.Next());
        Assert.Equal(MultiUpMode.FourUp, MultiUpMode.TwoUp.Next());
        Assert.Equal(MultiUpMode.Single, MultiUpMode.FourUp.Next());

        Assert.Equal(AppCommand.ToggleFaceDock, CommandMap.Resolve(new KeyInput('A', false, false, false), false));
        Assert.Equal(AppCommand.ToggleFilmstrip, CommandMap.Resolve(new KeyInput('G', false, false, false), false));
        Assert.Equal(AppCommand.CycleCameraProfile, CommandMap.Resolve(new KeyInput('V', false, false, false), false));
        Assert.Equal(AppCommand.ToggleSplitCompare, CommandMap.Resolve(new KeyInput('S', false, false, false), false));
        Assert.Equal(AppCommand.SwapSplitPanes, CommandMap.Resolve(new KeyInput(CommandMap.VK_TAB, false, false, false), false));

        // Repeat suppression on stateful toggles
        Assert.Null(CommandMap.Resolve(new KeyInput('A', false, false, false, IsRepeat: true), false));
        Assert.Null(CommandMap.Resolve(new KeyInput('G', false, false, false, IsRepeat: true), false));
        Assert.Null(CommandMap.Resolve(new KeyInput('V', false, false, false, IsRepeat: true), false));
    }

    [Fact]
    public async Task TestKingOfTheHillCompoundPickRejectAndUndo()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"winglance_koth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var items = new List<SlideItem>();
            for (int i = 0; i < 3; i++)
            {
                string p = Path.Combine(tempDir, $"koth_{i}.jpg");
                File.WriteAllBytes(p, CreateTestJpegBytes(32, 32));
                items.Add(new SlideItem(p, fileSize: 512));
            }

            var service = new CullingService();
            service.SetFolderData(items, new Dictionary<string, CullingMetadata>(), resetUndo: true);
            service.SetFilter(CullingFilter.HideRejects);

            bool changed = service.ApplyKingOfTheHill(items[0], items[1], onBeforeWrite: null, out var toast);
            Assert.True(changed);
            Assert.Contains("Winner", toast);
            Assert.Equal(PickStatus.Pick, service.GetMetadata(items[0].Id).Pick);
            Assert.Equal(PickStatus.Reject, service.GetMetadata(items[1].Id).Pick);

            // Under HideRejects filter, loser disappears and next contender slides in
            service.RebuildFilteredItems();
            Assert.Equal(2, service.FilteredItems.Count);
            Assert.Equal(items[0].Id, service.FilteredItems[0].Id);
            Assert.Equal(items[2].Id, service.FilteredItems[1].Id);

            // Single Undo restores both Pick & Reject and brings back all 3 photos
            var undoRes = await service.UndoAsync();
            Assert.True(undoRes.Applied);
            Assert.Equal(PickStatus.Unflagged, service.GetMetadata(items[0].Id).Pick);
            Assert.Equal(PickStatus.Unflagged, service.GetMetadata(items[1].Id).Pick);
            Assert.Equal(3, service.FilteredItems.Count);

            var redoRes = await service.RedoAsync();
            Assert.True(redoRes.Applied);
            Assert.Equal(PickStatus.Pick, service.GetMetadata(items[0].Id).Pick);
            Assert.Equal(PickStatus.Reject, service.GetMetadata(items[1].Id).Pick);
            Assert.Equal(2, service.FilteredItems.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TestBottomLayoutSpatialSeparationAndHitTesting()
    {
        float dipW = 1920f, dipH = 1080f;

        var filmstrip = BottomLayoutMetrics.ComputeFilmstripBounds(dipW, dipH);
        Assert.True(filmstrip.Bottom <= dipH - 72f); // clears bottom telemetry HUD

        var faceDown = BottomLayoutMetrics.ComputeFaceDockBounds(dipW, dipH, tileCount: 3, filmstripOpacity: 0f);
        var faceUp = BottomLayoutMetrics.ComputeFaceDockBounds(dipW, dipH, tileCount: 3, filmstripOpacity: 1.0f);
        Assert.True(faceUp.Bottom <= filmstrip.Top - 8f);
        Assert.True(faceUp.Top < faceDown.Top);

        // Mini-map also lifts clear of the Filmstrip when open
        var miniLifted = NavigatorMiniMapOverlay.ComputeCardBounds(dipW, dipH, collapsed: false, filmstripOpacity: 1.0f);
        Assert.True(miniLifted.Bottom <= filmstrip.Top);

        // Multi-Up grid partitions
        var twoPanes = BottomLayoutMetrics.ComputeMultiUpPaneRects(MultiUpMode.TwoUp, dipW, dipH, isSoloLoupe: false);
        Assert.Equal(2, twoPanes.Length);
        Assert.True(twoPanes[1].Left > twoPanes[0].Right);

        var fourPanes = BottomLayoutMetrics.ComputeMultiUpPaneRects(MultiUpMode.FourUp, dipW, dipH, isSoloLoupe: false);
        Assert.Equal(4, fourPanes.Length);
        Assert.True(fourPanes[2].Top > fourPanes[0].Bottom);

        var soloPanes = BottomLayoutMetrics.ComputeMultiUpPaneRects(MultiUpMode.FourUp, dipW, dipH, isSoloLoupe: true);
        Assert.Single(soloPanes);
        Assert.Equal(dipW, soloPanes[0].Right);

        // Hit testing on Face Dock tiles
        float tile0CenterX = faceDown.Left + BottomLayoutMetrics.FaceDockHorizontalPad + BottomLayoutMetrics.FaceTileWidth * 0.5f;
        float tile1CenterX = tile0CenterX + BottomLayoutMetrics.FaceTileWidth + BottomLayoutMetrics.FaceTileGap;
        float tileCenterY = (faceDown.Top + faceDown.Bottom) * 0.5f;
        Assert.Equal(0, FaceDockOverlay.HitTestTile(new System.Numerics.Vector2(tile0CenterX, tileCenterY), dipW, dipH, 3, 0f));
        Assert.Equal(1, FaceDockOverlay.HitTestTile(new System.Numerics.Vector2(tile1CenterX, tileCenterY), dipW, dipH, 3, 0f));
        Assert.Equal(-1, FaceDockOverlay.HitTestTile(new System.Numerics.Vector2(10f, 10f), dipW, dipH, 3, 0f));

        // Filmstrip scroll bounds & cell hit test
        float cardW = filmstrip.Right - filmstrip.Left;
        float maxScroll = FilmstripOverlay.ComputeMaxScrollX(cardW, 50);
        Assert.True(maxScroll > 0f);
        float centered = FilmstripOverlay.ComputeCenteredScrollX(25, cardW, 50);
        Assert.InRange(centered, 0f, maxScroll);

        float firstCellX = filmstrip.Left + FilmstripOverlay.SidePadding + FilmstripOverlay.CellWidth * 0.5f;
        float filmY = (filmstrip.Top + filmstrip.Bottom) * 0.5f;
        Assert.Equal(0, FilmstripOverlay.HitTestIndex(new System.Numerics.Vector2(firstCellX, filmY), dipW, dipH, 0f, 50));
    }

    [Fact]
    public void TestFaceInspectionEngineCropPaddingAndSharpness()
    {
        var sharpImg = BuildSyntheticDecodedImage(256, 256, patternMode: 4, blurRadius: 0, brightnessOffset: 0, captureTime: null);
        var blurryImg = BuildSyntheticDecodedImage(256, 256, patternMode: 4, blurRadius: 5, brightnessOffset: 0, captureTime: null);

        var faceBox = new WinGlance.Vision.NormalizedRect(0.35f, 0.25f, 0.30f, 0.40f);
        var eyePt = new WinGlance.Vision.NormalizedPoint(0.50f, 0.45f);

        var crop = FaceInspectionEngine.ExtractCrop(sharpImg, faceBox, outputSize: 128);
        Assert.Equal(128, crop.Width);
        Assert.Equal(128, crop.Height);
        Assert.Equal(ImagePixelFormat.Bgra32, crop.PixelFormat);
        Assert.Equal(255, crop.Pixels[3]); // Opaque alpha

        float sharpVal = WinGlance.Vision.EyeFocusDetector.MeasureLocalSharpness(sharpImg, eyePt);
        float blurVal = WinGlance.Vision.EyeFocusDetector.MeasureLocalSharpness(blurryImg, eyePt);
        Assert.True(sharpVal > FaceInspectionEngine.SharpEyeThreshold);
        Assert.True(sharpVal > blurVal * 3f);
    }

    [Fact]
    public async Task TestMicroThumbnailEngineAsyncDecodeAndCache()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"winglance_mthumb_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var items = new List<SlideItem>();
            for (int i = 0; i < 4; i++)
            {
                string p = Path.Combine(tempDir, $"thumb_{i}.jpg");
                File.WriteAllBytes(p, BuildSyntheticJpegWithExif(320, 240, patternMode: i % 2, blurRadius: 0,
                    dateTimeOrig: "2026:07:15 10:00:00", subSec: "000"));
                items.Add(new SlideItem(p, fileSize: 2048));
            }

            using var engine = new MicroThumbnailEngine();
            var readyIds = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var allDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ThumbnailReady += id =>
            {
                readyIds[id] = 1;
                if (readyIds.Count >= items.Count) allDone.TrySetResult(true);
            };

            engine.RequestWindow(items, centerIndex: 1, radius: 4);
            var finished = await Task.WhenAny(allDone.Task, Task.Delay(5000));
            Assert.Same(allDone.Task, finished);

            foreach (var it in items)
            {
                Assert.True(engine.TryGetThumbnail(it.Id, out var thumb));
                Assert.NotNull(thumb);
                Assert.True(Math.Max(thumb!.Width, thumb.Height) <= MicroThumbnailEngine.ThumbnailMaxSize);
            }

            engine.Invalidate(items[0].Id);
            Assert.False(engine.TryGetThumbnail(items[0].Id, out _));

            engine.Clear();
            Assert.False(engine.TryGetThumbnail(items[1].Id, out _));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TestCameraProfileBrandDetectionAndSupportGating()
    {
        var jpeg = new SlideItem("photo.jpg");
        var jpgCheck = CameraProfileEngine.IsProfileSupported(jpeg);
        Assert.False(jpgCheck.Supported);
        Assert.Equal("Profiles for RAW Files Only", jpgCheck.Reason);

        var canonRaw = new SlideItem("canon.cr3", rawPath: "canon.cr3");
        var canonCheck = CameraProfileEngine.IsProfileSupported(canonRaw, new ExifMetadata(CameraMake: "Canon"));
        Assert.False(canonCheck.Supported);
        Assert.Equal("Profiles for Fuji, Nikon & Sony RAWs", canonCheck.Reason);

        var fujiRaw = new SlideItem("DSCF0001.RAF", rawPath: "DSCF0001.RAF");
        var fujiCheck = CameraProfileEngine.IsProfileSupported(fujiRaw, new ExifMetadata(CameraMake: "FUJIFILM"));
        Assert.True(fujiCheck.Supported);
        Assert.Equal(CameraBrand.Fujifilm, fujiCheck.Brand);
        Assert.Equal("Classic Chrome", CameraProfile.Landscape.GetDisplayName(fujiCheck.Brand));

        var nikonRaw = new SlideItem("DSC_0001.NEF", rawPath: "DSC_0001.NEF");
        Assert.Equal(CameraBrand.Nikon, CameraProfileEngine.DetectBrand(nikonRaw));
        Assert.Equal("Nikon Monochrome", CameraProfile.Monochrome.GetDisplayName(CameraBrand.Nikon));

        var sonyRaw = new SlideItem("DSC00001.ARW", rawPath: "DSC00001.ARW");
        Assert.Equal(CameraBrand.Sony, CameraProfileEngine.DetectBrand(sonyRaw));
        Assert.Equal("Sony FL (Film/Matte)", CameraProfile.Landscape.GetDisplayName(CameraBrand.Sony));
    }

    [Fact]
    public void TestCameraProfileColorMatricesAndMonochromeTransform()
    {
        var asShot = CameraProfileEngine.ComputeColorMatrix(CameraProfile.AsShot, CameraBrand.Fujifilm);
        Assert.Equal(CameraProfileEngine.IdentityMatrix, asShot);

        // Acros / Monochrome must desaturate any color to neutral gray (R == G == B)
        var mono = CameraProfileEngine.ComputeColorMatrix(CameraProfile.Monochrome, CameraBrand.Fujifilm);
        var (mr, mg, mb) = CameraProfileEngine.ApplyMatrixToRgb(in mono, 0.85f, 0.25f, 0.15f);
        Assert.True(MathF.Abs(mr - mg) < 1e-4f);
        Assert.True(MathF.Abs(mg - mb) < 1e-4f);

        // Classic Neg warms reds relative to blues
        var film = CameraProfileEngine.ComputeColorMatrix(CameraProfile.Film, CameraBrand.Fujifilm);
        var (fr, _, fb) = CameraProfileEngine.ApplyMatrixToRgb(in film, 0.50f, 0.50f, 0.50f);
        Assert.True(fr > fb);

        // Velvia boosts saturation relative to Standard on chromatic input
        var vivid = CameraProfileEngine.ComputeColorMatrix(CameraProfile.Vivid, CameraBrand.Fujifilm);
        var std = CameraProfileEngine.ComputeColorMatrix(CameraProfile.Standard, CameraBrand.Fujifilm);
        var (vr, _, vb) = CameraProfileEngine.ApplyMatrixToRgb(in vivid, 0.70f, 0.45f, 0.35f);
        var (sr, _, sb) = CameraProfileEngine.ApplyMatrixToRgb(in std, 0.70f, 0.45f, 0.35f);
        Assert.True((vr - vb) > (sr - sb));
    }

    [Fact]
    public async Task TestXmpCameraProfileRoundtripAndUndoRedo()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"winglance_xmp_prof_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string rawPath = Path.Combine(tempDir, "DSCF1001.RAF");
            File.WriteAllBytes(rawPath, new byte[] { 1, 2, 3, 4 });

            var initialMeta = new CullingMetadata(Rating: 4, Pick: PickStatus.Pick, CameraProfile: CameraProfile.Landscape);
            XmpSidecarManager.Save(initialMeta, rawPath);

            string? sidecar = XmpSidecarManager.ExistingSidecarPath(rawPath);
            Assert.NotNull(sidecar);
            string xmlText = File.ReadAllText(sidecar!);
            Assert.Contains("Camera CLASSIC CHROME", xmlText);

            var readBack = XmpSidecarManager.Read(sidecar!);
            Assert.Equal(4, readBack.Rating);
            Assert.Equal(PickStatus.Pick, readBack.Pick);
            Assert.Equal(CameraProfile.Landscape, readBack.CameraProfile);

            // CullingService ApplyCameraProfile + Undo / Redo roundtrip (synchronous sidecar writer for deterministic cleanup)
            var item = new SlideItem(rawPath, rawPath: rawPath, fileSize: 4);
            var service = new CullingService((meta, path, onBeforeWrite) => XmpSidecarManager.Save(meta, path, onBeforeWrite));
            service.SetFolderData(new[] { item }, new Dictionary<string, CullingMetadata>(), resetUndo: true);

            service.ApplyCameraProfile(item, CameraProfile.Monochrome, CameraBrand.Fujifilm, null, out var toast);
            Assert.Contains("Acros", toast);
            Assert.Equal(CameraProfile.Monochrome, service.GetMetadata(item.Id).CameraProfile);
            Assert.Equal(CameraProfile.Monochrome, XmpSidecarManager.Read(sidecar!).CameraProfile);

            var undoRes = await service.UndoAsync();
            Assert.True(undoRes.Applied);
            Assert.Null(service.GetMetadata(item.Id).CameraProfile);
            Assert.Null(XmpSidecarManager.Read(sidecar!).CameraProfile);

            var redoRes = await service.RedoAsync();
            Assert.True(redoRes.Applied);
            Assert.Equal(CameraProfile.Monochrome, service.GetMetadata(item.Id).CameraProfile);
            Assert.Equal(CameraProfile.Monochrome, XmpSidecarManager.Read(sidecar!).CameraProfile);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
