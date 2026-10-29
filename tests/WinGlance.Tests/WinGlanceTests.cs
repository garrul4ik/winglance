using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Sdcb.LibRaw;
using Vortice.WIC;
using WinGlance.Engine;
using WinGlance.Model;
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
}
