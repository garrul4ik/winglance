using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using WinGlance.Model;

namespace WinGlance.Engine;

public static class XmpSidecarManager
{
    private static readonly XNamespace XmpMetaNs = "adobe:ns:meta/";
    private static readonly XNamespace RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace XmpNs = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace CrsNs = "http://ns.adobe.com/camera-raw/1.0/";
    private static readonly XNamespace PhotoshopNs = "http://ns.adobe.com/photoshop/1.0/";
    private static readonly XNamespace TiffNs = "http://ns.adobe.com/tiff/1.0/";

    public static string? ExistingSidecarPath(string imagePath)
    {
        var dir = Path.GetDirectoryName(imagePath) ?? "";
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
        var fileName = Path.GetFileName(imagePath);

        // 1. Standard Adobe convention: photo.CR3 -> photo.xmp
        var standardPath = Path.Combine(dir, $"{fileNameWithoutExt}.xmp");
        if (File.Exists(standardPath)) return standardPath;

        // 2. Suffix convention: photo.CR3 -> photo.CR3.xmp
        var altPath = Path.Combine(dir, $"{fileName}.xmp");
        if (File.Exists(altPath)) return altPath;

        return null;
    }

    public static string TargetSidecarPath(string imagePath)
    {
        return ExistingSidecarPath(imagePath) ??
               Path.Combine(
                   Path.GetDirectoryName(imagePath) ?? "",
                   $"{Path.GetFileNameWithoutExtension(imagePath)}.xmp"
               );
    }

    public static CullingMetadata Read(string sidecarPath)
    {
        try
        {
            if (!File.Exists(sidecarPath)) return new CullingMetadata();
            using var stream = File.OpenRead(sidecarPath);
            var doc = XDocument.Load(stream);
            return Parse(doc);
        }
        catch
        {
            return new CullingMetadata();
        }
    }

    public static CullingMetadata? ReadMetadata(string imagePath)
    {
        var sidecar = ExistingSidecarPath(imagePath);
        return sidecar != null ? Read(sidecar) : null;
    }

    public static async Task<Dictionary<string, CullingMetadata>> LoadAllSidecarsAsync(IReadOnlyList<SlideItem> items)
    {
        return await Task.Run(() =>
        {
            var dict = new Dictionary<string, CullingMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var meta = ReadMetadata(item.Path);
                if (meta.HasValue)
                {
                    dict[item.Id] = meta.Value;
                }
            }
            return dict;
        });
    }

    public static CullingMetadata Parse(XDocument doc)
    {
        var rating = 0;
        var pick = PickStatus.Unflagged;
        var color = ColorLabel.None;
        int? orientation = null;

        var descriptions = doc.Descendants().Where(e => e.Name.LocalName == "Description");

        foreach (var desc in descriptions)
        {
            // Check attributes
            foreach (var attr in desc.Attributes())
            {
                ApplyField(attr.Name.LocalName, attr.Value.Trim(), ref rating, ref pick, ref color, ref orientation);
            }

            // Check child elements
            foreach (var child in desc.Elements())
            {
                ApplyField(child.Name.LocalName, child.Value.Trim(), ref rating, ref pick, ref color, ref orientation);
            }
        }

        return new CullingMetadata(rating, pick, color, orientation);
    }

    private static void ApplyField(
        string local,
        string val,
        ref int rating,
        ref PickStatus pick,
        ref ColorLabel color,
        ref int? orientation)
    {
        if (local == "Rating")
        {
            if (int.TryParse(val, out var r))
            {
                if (r == -1) pick = PickStatus.Reject;
                else rating = Math.Clamp(r, 0, 5);
            }
        }
        else if (local == "PickStatus")
        {
            if (val == "1") pick = PickStatus.Pick;
            else if (val == "-1") pick = PickStatus.Reject;
            else if (val == "0") pick = PickStatus.Unflagged;
        }
        else if (local == "Label")
        {
            var c = ParseColor(val);
            if (c.HasValue) color = c.Value;
        }
        else if (local == "Urgency" && color == ColorLabel.None)
        {
            var c = ParseColor(val);
            if (c.HasValue) color = c.Value;
        }
        else if (local == "Orientation")
        {
            if (int.TryParse(val, out var o) && o is >= 1 and <= 8)
            {
                orientation = o;
            }
        }
    }

    private static ColorLabel? ParseColor(string val)
    {
        return val.Trim().ToLowerInvariant() switch
        {
            "red" or "6" or "1" => ColorLabel.Red,
            "yellow" or "7" or "2" => ColorLabel.Yellow,
            "green" or "8" or "3" => ColorLabel.Green,
            "blue" or "9" or "4" => ColorLabel.Blue,
            "none" or "0" or "" => ColorLabel.None,
            _ => null
        };
    }

    public static void Save(CullingMetadata metadata, string imagePath)
    {
        var sidecarPath = TargetSidecarPath(imagePath);
        XDocument doc;

        if (File.Exists(sidecarPath))
        {
            try
            {
                using var readStream = File.OpenRead(sidecarPath);
                doc = XDocument.Load(readStream);
            }
            catch
            {
                doc = CreateNewXmpDocument();
            }
        }
        else
        {
            doc = CreateNewXmpDocument();
        }

        var rdfDesc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Description");
        if (rdfDesc == null)
        {
            var rdf = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "RDF");
            if (rdf == null)
            {
                rdf = new XElement(RdfNs + "RDF");
                doc.Root?.Add(rdf);
            }
            rdfDesc = new XElement(RdfNs + "Description", new XAttribute(RdfNs + "about", ""));
            rdf.Add(rdfDesc);
        }

        // Remove old attributes & child elements for these fields
        var targetNames = new HashSet<string> { "Rating", "PickStatus", "Label", "Urgency", "Orientation" };
        foreach (var attr in rdfDesc.Attributes().Where(a => targetNames.Contains(a.Name.LocalName)).ToList())
        {
            attr.Remove();
        }
        foreach (var elem in rdfDesc.Elements().Where(e => targetNames.Contains(e.Name.LocalName)).ToList())
        {
            elem.Remove();
        }

        // Apply updated attributes
        if (metadata.Pick == PickStatus.Reject)
        {
            rdfDesc.SetAttributeValue(XmpNs + "Rating", "-1");
            rdfDesc.SetAttributeValue(CrsNs + "PickStatus", "-1");
        }
        else
        {
            rdfDesc.SetAttributeValue(XmpNs + "Rating", metadata.Rating);
            rdfDesc.SetAttributeValue(CrsNs + "PickStatus", metadata.Pick == PickStatus.Pick ? "1" : "0");
        }

        if (metadata.Color != ColorLabel.None)
        {
            rdfDesc.SetAttributeValue(XmpNs + "Label", metadata.Color.GetTitle());
            rdfDesc.SetAttributeValue(PhotoshopNs + "Urgency", ((int)metadata.Color).ToString());
        }

        if (metadata.Orientation.HasValue)
        {
            rdfDesc.SetAttributeValue(TiffNs + "Orientation", metadata.Orientation.Value);
        }

        var tempPath = $"{sidecarPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            doc.Save(tempPath);
            File.Move(tempPath, sidecarPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static XDocument CreateNewXmpDocument()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XProcessingInstruction("xpacket", "begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\""),
            new XElement(XmpMetaNs + "xmpmeta",
                new XAttribute(XNamespace.Xmlns + "x", XmpMetaNs.NamespaceName),
                new XElement(RdfNs + "RDF",
                    new XAttribute(XNamespace.Xmlns + "rdf", RdfNs.NamespaceName),
                    new XElement(RdfNs + "Description",
                        new XAttribute(RdfNs + "about", ""),
                        new XAttribute(XNamespace.Xmlns + "xmp", XmpNs.NamespaceName),
                        new XAttribute(XNamespace.Xmlns + "crs", CrsNs.NamespaceName),
                        new XAttribute(XNamespace.Xmlns + "photoshop", PhotoshopNs.NamespaceName),
                        new XAttribute(XNamespace.Xmlns + "tiff", TiffNs.NamespaceName)
                    )
                )
            ),
            new XProcessingInstruction("xpacket", "end=\"w\"")
        );
    }
}
