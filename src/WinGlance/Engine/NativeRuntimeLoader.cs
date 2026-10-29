using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Sdcb.LibRaw;

namespace WinGlance.Engine;

public static class NativeRuntimeLoader
{
    private static readonly object SyncLock = new();
    private static bool _libRawInitialized;

    public static byte[] DecompressEmbeddedResource(string logicalName)
    {
        var asm = typeof(NativeRuntimeLoader).Assembly;
        using var stream = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' not found.");
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        brotli.CopyTo(ms);
        return ms.ToArray();
    }

    public static void EnsureLibRawLoaded()
    {
        if (_libRawInitialized) return;

        lock (SyncLock)
        {
            if (_libRawInitialized) return;

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinGlance", "native", "libraw-0.21.1"
            );
            Directory.CreateDirectory(cacheDir);

            var payload = DecompressEmbeddedResource("WinGlance.libraw_win64.br");
            using var ms = new MemoryStream(payload, writable: false);
            using var br = new BinaryReader(ms);

            var count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var dllName = br.ReadString();
                var len = br.ReadInt32();
                var bytes = br.ReadBytes(len);

                var targetPath = Path.Combine(cacheDir, dllName);
                var needsWrite = !File.Exists(targetPath) || new FileInfo(targetPath).Length != len;
                if (needsWrite)
                {
                    var tmpPath = $"{targetPath}.tmp.{Environment.ProcessId}";
                    File.WriteAllBytes(tmpPath, bytes);
                    try
                    {
                        File.Move(tmpPath, targetPath, overwrite: true);
                    }
                    catch
                    {
                        if (File.Exists(tmpPath))
                        {
                            try { File.Delete(tmpPath); } catch { }
                        }
                    }
                }

                // Load in exact dependency order: zlib1 -> lcms2 -> jpeg8 -> raw_r
                NativeLibrary.Load(targetPath);
            }

            _libRawInitialized = true;
        }
    }
}
