using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WinGlance.Engine;
using WinGlance.Model;

namespace WinGlance.Vision;

public readonly record struct NormalizedPoint(float X, float Y);

public readonly record struct NormalizedRect(float X, float Y, float Width, float Height)
{
    public float Area => MathF.Max(0f, Width) * MathF.Max(0f, Height);
}

public sealed record SubjectEyeTarget(
    NormalizedRect FaceBox,
    NormalizedPoint PrimaryEye,
    NormalizedPoint SecondaryEye,
    float Score,
    string Backend
);

public readonly record struct EyeJumpStop(
    NormalizedPoint Point,
    NormalizedRect FaceBox,
    string Label,
    string Backend
);

/// <summary>
/// Hardware-accelerated face &amp; eye landmark detector powered by Windows 11's built-in
/// System32\onnxruntime.dll + DirectML (GPU/NPU) and embedded OpenCV Zoo YuNet ONNX model.
/// </summary>
public static unsafe class EyeFocusDetector
{
    private const int InputSize = 640;

    private static readonly object InitLock = new();
    private static bool _initialized;
    private static bool _ortAvailable;
    private static string _backendName = "Salience";

    private static void** _api;
    private static IntPtr _env;
    private static IntPtr _sessionOptions;
    private static IntPtr _session;
    private static IntPtr _cpuMemInfo;
    private static byte[]? _pinnedModelBytes;
    private static GCHandle _modelPin;

    private static readonly string[] OutputTensorNames =
    {
        "cls_8", "cls_16", "cls_32",
        "obj_8", "obj_16", "obj_32",
        "bbox_8", "bbox_16", "bbox_32",
        "kps_8", "kps_16", "kps_32"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct OrtApiBase
    {
        public delegate* unmanaged[Cdecl]<uint, void**> GetApi;
        public delegate* unmanaged[Cdecl]<byte*> GetVersionString;
    }

    public static string ActiveBackend
    {
        get
        {
            EnsureInitialized();
            return _backendName;
        }
    }

    public static bool IsNeuralEngineAvailable
    {
        get
        {
            EnsureInitialized();
            return _ortAvailable;
        }
    }

    public static IReadOnlyList<SubjectEyeTarget> DetectFacesAndEyes(DecodedImage image, float scoreThreshold = 0.35f)
    {
        if (image.Width < 16 || image.Height < 16 || image.Pixels.Length == 0)
        {
            return Array.Empty<SubjectEyeTarget>();
        }

        EnsureInitialized();

        if (_ortAvailable)
        {
            try
            {
                var neuralResults = RunYuNetInference(image, scoreThreshold);
                if (neuralResults.Count > 0)
                {
                    return neuralResults;
                }
                return Array.Empty<SubjectEyeTarget>();
            }
            catch
            {
                // Fall through to salience fallback only if ORT session faults
            }
        }

        return DetectSalientSharpestRegionFallback(image);
    }

    public static IReadOnlyList<EyeJumpStop> BuildJumpSequence(DecodedImage image)
    {
        var faces = DetectFacesAndEyes(image);
        if (faces.Count == 0)
        {
            // Fallback to sharpest salient subject region so Shift+Z always helps focus-check
            var fallback = DetectSalientSharpestRegionFallback(image);
            if (fallback.Count == 0) return Array.Empty<EyeJumpStop>();
            var f = fallback[0];
            return new[]
            {
                new EyeJumpStop(f.PrimaryEye, f.FaceBox, "Sharpest Detail", "Contrast")
            };
        }

        var stops = new List<EyeJumpStop>();
        if (faces.Count == 1)
        {
            var f = faces[0];
            stops.Add(new EyeJumpStop(f.PrimaryEye, f.FaceBox, "Eye 1 (Sharpest)", f.Backend));

            float dx = (f.PrimaryEye.X - f.SecondaryEye.X) * image.Width;
            float dy = (f.PrimaryEye.Y - f.SecondaryEye.Y) * image.Height;
            if (dx * dx + dy * dy > 16f)
            {
                stops.Add(new EyeJumpStop(f.SecondaryEye, f.FaceBox, "Eye 2", f.Backend));
            }
        }
        else
        {
            for (int i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                stops.Add(new EyeJumpStop(f.PrimaryEye, f.FaceBox, $"Subject {i + 1}/{faces.Count} Eye", f.Backend));
            }
        }

        return stops;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;

        lock (InitLock)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                string ortPath = Path.Combine(Environment.SystemDirectory, "onnxruntime.dll");
                if (!NativeLibrary.TryLoad(ortPath, out IntPtr ortLib))
                {
                    return;
                }

                if (!NativeLibrary.TryGetExport(ortLib, "OrtGetApiBase", out IntPtr getApiBasePtr))
                {
                    return;
                }

                var getApiBase = (delegate* unmanaged[Cdecl]<OrtApiBase*>)getApiBasePtr;
                OrtApiBase* apiBase = getApiBase();
                if (apiBase == null) return;

                void** api = apiBase->GetApi(14);
                if (api == null) api = apiBase->GetApi(10);
                if (api == null) api = apiBase->GetApi(8);
                if (api == null) return;

                _api = api;

                var createEnv = (delegate* unmanaged[Cdecl]<int, byte*, IntPtr*, IntPtr>)_api[3];
                var createSessionOptions = (delegate* unmanaged[Cdecl]<IntPtr*, IntPtr>)_api[10];
                var setExecutionMode = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)_api[13];
                var disableMemPattern = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_api[17];
                var setGraphOptLevel = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)_api[23];
                var setIntraThreads = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)_api[24];
                var createSessionFromArray = (delegate* unmanaged[Cdecl]<IntPtr, void*, nuint, IntPtr, IntPtr*, IntPtr>)_api[8];
                var createCpuMemInfo = (delegate* unmanaged[Cdecl]<int, int, IntPtr*, IntPtr>)_api[69];
                var releaseSessionOptions = (delegate* unmanaged[Cdecl]<IntPtr, void>)_api[100];

                fixed (byte* logId = "WinGlanceVision\0"u8)
                {
                    IntPtr env;
                    if (!CheckStatus(createEnv(3, logId, &env))) return;
                    _env = env;
                }

                IntPtr memInfo;
                if (!CheckStatus(createCpuMemInfo(1, 0, &memInfo))) return;
                _cpuMemInfo = memInfo;

                _pinnedModelBytes = NativeRuntimeLoader.DecompressEmbeddedResource("WinGlance.yunet.onnx.br");
                _modelPin = GCHandle.Alloc(_pinnedModelBytes, GCHandleType.Pinned);
                void* pModel = (void*)_modelPin.AddrOfPinnedObject();
                nuint modelLen = (nuint)_pinnedModelBytes.Length;

                // 1. Try DirectML GPU/NPU Execution Provider first
                bool sessionCreated = false;
                if (NativeLibrary.TryGetExport(ortLib, "OrtSessionOptionsAppendExecutionProvider_DML", out IntPtr appendDmlPtr))
                {
                    IntPtr dmlOpts;
                    if (CheckStatus(createSessionOptions(&dmlOpts)))
                    {
                        // DirectML requires sequential execution & disabled memory pattern
                        ReleaseIfError(setExecutionMode(dmlOpts, 0));
                        ReleaseIfError(disableMemPattern(dmlOpts));
                        ReleaseIfError(setGraphOptLevel(dmlOpts, 99));

                        var appendDml = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)appendDmlPtr;
                        if (CheckStatus(appendDml(dmlOpts, 0)))
                        {
                            IntPtr sess;
                            if (CheckStatus(createSessionFromArray(_env, pModel, modelLen, dmlOpts, &sess)))
                            {
                                _sessionOptions = dmlOpts;
                                _session = sess;
                                _backendName = "DirectML GPU";
                                sessionCreated = true;
                            }
                        }

                        if (!sessionCreated)
                        {
                            releaseSessionOptions(dmlOpts);
                        }
                    }
                }

                // 2. Fallback to CPU Execution Provider
                if (!sessionCreated)
                {
                    IntPtr cpuOpts;
                    if (!CheckStatus(createSessionOptions(&cpuOpts))) return;
                    ReleaseIfError(setGraphOptLevel(cpuOpts, 99));
                    ReleaseIfError(setIntraThreads(cpuOpts, Math.Clamp(Environment.ProcessorCount / 2, 1, 4)));

                    IntPtr sess;
                    if (!CheckStatus(createSessionFromArray(_env, pModel, modelLen, cpuOpts, &sess)))
                    {
                        releaseSessionOptions(cpuOpts);
                        return;
                    }

                    _sessionOptions = cpuOpts;
                    _session = sess;
                    _backendName = "ONNX CPU";
                }

                _ortAvailable = true;
            }
            catch
            {
                _ortAvailable = false;
            }
        }
    }

    private static List<SubjectEyeTarget> RunYuNetInference(DecodedImage image, float scoreThreshold)
    {
        float scale = MathF.Min((float)InputSize / image.Width, (float)InputSize / image.Height);
        int resizedW = Math.Clamp((int)MathF.Round(image.Width * scale), 1, InputSize);
        int resizedH = Math.Clamp((int)MathF.Round(image.Height * scale), 1, InputSize);

        var inputBuffer = PreprocessLetterboxBgr(image, resizedW, resizedH);

        var createTensor = (delegate* unmanaged[Cdecl]<IntPtr, void*, nuint, long*, nuint, int, IntPtr*, IntPtr>)_api[49];
        var runFn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte**, IntPtr*, nuint, byte**, nuint, IntPtr*, IntPtr>)_api[9];
        var getTensorData = (delegate* unmanaged[Cdecl]<IntPtr, void**, IntPtr>)_api[51];
        var releaseValue = (delegate* unmanaged[Cdecl]<IntPtr, void>)_api[96];

        long* inputShape = stackalloc long[4] { 1, 3, InputSize, InputSize };
        IntPtr inputVal = IntPtr.Zero;
        IntPtr* outputVals = stackalloc IntPtr[12];
        for (int i = 0; i < 12; i++) outputVals[i] = IntPtr.Zero;

        // Allocate null-terminated ASCII names on stack
        byte* inName = stackalloc byte[8];
        "input\0"u8.CopyTo(new Span<byte>(inName, 8));
        byte** pInputNames = stackalloc byte*[1] { inName };

        byte* namePool = stackalloc byte[12 * 16];
        byte** pOutputNames = stackalloc byte*[12];
        for (int i = 0; i < 12; i++)
        {
            byte* slot = namePool + i * 16;
            int n = Encoding.ASCII.GetBytes(OutputTensorNames[i], new Span<byte>(slot, 15));
            slot[n] = 0;
            pOutputNames[i] = slot;
        }

        lock (InitLock)
        {
            fixed (float* pInput = inputBuffer)
            {
                try
                {
                    nuint byteLen = (nuint)(inputBuffer.Length * sizeof(float));
                    if (!CheckStatus(createTensor(_cpuMemInfo, pInput, byteLen, inputShape, 4, 1, &inputVal)))
                    {
                        return new List<SubjectEyeTarget>();
                    }

                    IntPtr* pInputs = stackalloc IntPtr[1] { inputVal };
                    if (!CheckStatus(runFn(_session, IntPtr.Zero, pInputNames, pInputs, 1, pOutputNames, 12, outputVals)))
                    {
                        return new List<SubjectEyeTarget>();
                    }

                    float** outPtrs = stackalloc float*[12];
                    for (int i = 0; i < 12; i++)
                    {
                        void* rawPtr;
                        if (!CheckStatus(getTensorData(outputVals[i], &rawPtr)))
                        {
                            return new List<SubjectEyeTarget>();
                        }
                        outPtrs[i] = (float*)rawPtr;
                    }

                    return DecodeYuNetOutputs(outPtrs, resizedW, resizedH, image, scoreThreshold);
                }
                finally
                {
                    if (inputVal != IntPtr.Zero) releaseValue(inputVal);
                    for (int i = 0; i < 12; i++)
                    {
                        if (outputVals[i] != IntPtr.Zero) releaseValue(outputVals[i]);
                    }
                }
            }
        }
    }

    private static float[] PreprocessLetterboxBgr(DecodedImage image, int resizedW, int resizedH)
    {
        const int planeSize = InputSize * InputSize;
        var tensor = new float[3 * planeSize];

        fixed (byte* pSrcBase = image.Pixels)
        fixed (float* pDst = tensor)
        {
            float* pB = pDst;
            float* pG = pDst + planeSize;
            float* pR = pDst + 2 * planeSize;

            float scaleX = (float)image.Width / resizedW;
            float scaleY = (float)image.Height / resizedH;

            if (image.PixelFormat == ImagePixelFormat.Rgba64)
            {
                for (int y = 0; y < resizedH; y++)
                {
                    int sy = Math.Min(image.Height - 1, (int)((y + 0.5f) * scaleY));
                    ushort* srcRow = (ushort*)(pSrcBase + (long)sy * image.Stride);
                    int dstRowOffset = y * InputSize;

                    for (int x = 0; x < resizedW; x++)
                    {
                        int sx = Math.Min(image.Width - 1, (int)((x + 0.5f) * scaleX)) * 4;
                        int outIdx = dstRowOffset + x;
                        pR[outIdx] = srcRow[sx] * (1f / 257f);
                        pG[outIdx] = srcRow[sx + 1] * (1f / 257f);
                        pB[outIdx] = srcRow[sx + 2] * (1f / 257f);
                    }
                }
            }
            else
            {
                for (int y = 0; y < resizedH; y++)
                {
                    int sy = Math.Min(image.Height - 1, (int)((y + 0.5f) * scaleY));
                    byte* srcRow = pSrcBase + (long)sy * image.Stride;
                    int dstRowOffset = y * InputSize;

                    for (int x = 0; x < resizedW; x++)
                    {
                        int sx = Math.Min(image.Width - 1, (int)((x + 0.5f) * scaleX)) * 4;
                        int outIdx = dstRowOffset + x;
                        pB[outIdx] = srcRow[sx];
                        pG[outIdx] = srcRow[sx + 1];
                        pR[outIdx] = srcRow[sx + 2];
                    }
                }
            }
        }

        return tensor;
    }

    private sealed record RawCandidate(
        NormalizedRect Box,
        NormalizedPoint EyeA,
        NormalizedPoint EyeB,
        float Score
    );

    private static List<SubjectEyeTarget> DecodeYuNetOutputs(
        float** outPtrs,
        int resizedW,
        int resizedH,
        DecodedImage image,
        float scoreThreshold)
    {
        ReadOnlySpan<int> strides = stackalloc int[3] { 8, 16, 32 };
        var candidates = new List<RawCandidate>();

        float invW = 1f / resizedW;
        float invH = 1f / resizedH;

        for (int sIdx = 0; sIdx < 3; sIdx++)
        {
            int stride = strides[sIdx];
            int cols = InputSize / stride;
            int rows = InputSize / stride;

            float* pCls = outPtrs[0 + sIdx];
            float* pObj = outPtrs[3 + sIdx];
            float* pBbox = outPtrs[6 + sIdx];
            float* pKps = outPtrs[9 + sIdx];

            int idx = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++, idx++)
                {
                    float cls = Math.Clamp(pCls[idx], 0f, 1f);
                    float obj = Math.Clamp(pObj[idx], 0f, 1f);
                    float score = MathF.Sqrt(cls * obj);
                    if (score < scoreThreshold) continue;

                    float cx = (c + pBbox[idx * 4 + 0]) * stride;
                    float cy = (r + pBbox[idx * 4 + 1]) * stride;
                    float bw = MathF.Exp(Math.Clamp(pBbox[idx * 4 + 2], -10f, 10f)) * stride;
                    float bh = MathF.Exp(Math.Clamp(pBbox[idx * 4 + 3], -10f, 10f)) * stride;

                    float x1 = Math.Clamp((cx - bw * 0.5f) * invW, 0f, 1f);
                    float y1 = Math.Clamp((cy - bh * 0.5f) * invH, 0f, 1f);
                    float x2 = Math.Clamp((cx + bw * 0.5f) * invW, 0f, 1f);
                    float y2 = Math.Clamp((cy + bh * 0.5f) * invH, 0f, 1f);

                    if (x2 - x1 < 0.005f || y2 - y1 < 0.005f) continue;

                    float ex0 = Math.Clamp((c + pKps[idx * 10 + 0]) * stride * invW, 0f, 1f);
                    float ey0 = Math.Clamp((r + pKps[idx * 10 + 1]) * stride * invH, 0f, 1f);
                    float ex1 = Math.Clamp((c + pKps[idx * 10 + 2]) * stride * invW, 0f, 1f);
                    float ey1 = Math.Clamp((r + pKps[idx * 10 + 3]) * stride * invH, 0f, 1f);

                    candidates.Add(new RawCandidate(
                        new NormalizedRect(x1, y1, x2 - x1, y2 - y1),
                        new NormalizedPoint(ex0, ey0),
                        new NormalizedPoint(ex1, ey1),
                        score
                    ));
                }
            }
        }

        if (candidates.Count == 0) return new List<SubjectEyeTarget>();

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Greedy IoU NMS (threshold = 0.30)
        var kept = new List<RawCandidate>();
        foreach (var cand in candidates)
        {
            bool suppressed = false;
            foreach (var existing in kept)
            {
                if (ComputeIoU(cand.Box, existing.Box) > 0.30f)
                {
                    suppressed = true;
                    break;
                }
            }
            if (!suppressed)
            {
                kept.Add(cand);
                if (kept.Count >= 16) break;
            }
        }

        // Order subjects by prominence (score * sqrt(face area))
        kept.Sort((a, b) =>
        {
            float rankA = a.Score * MathF.Sqrt(MathF.Max(0.0001f, a.Box.Area));
            float rankB = b.Score * MathF.Sqrt(MathF.Max(0.0001f, b.Box.Area));
            return rankB.CompareTo(rankA);
        });

        var results = new List<SubjectEyeTarget>(kept.Count);
        foreach (var face in kept)
        {
            float sharpA = MeasureLocalSharpness(image, face.EyeA);
            float sharpB = MeasureLocalSharpness(image, face.EyeB);

            var primary = sharpA >= sharpB ? face.EyeA : face.EyeB;
            var secondary = sharpA >= sharpB ? face.EyeB : face.EyeA;

            results.Add(new SubjectEyeTarget(
                FaceBox: face.Box,
                PrimaryEye: primary,
                SecondaryEye: secondary,
                Score: face.Score,
                Backend: _backendName
            ));
        }

        return results;
    }

    private static float ComputeIoU(NormalizedRect a, NormalizedRect b)
    {
        float x1 = MathF.Max(a.X, b.X);
        float y1 = MathF.Max(a.Y, b.Y);
        float x2 = MathF.Min(a.X + a.Width, b.X + b.Width);
        float y2 = MathF.Min(a.Y + a.Height, b.Y + b.Height);

        float interW = MathF.Max(0f, x2 - x1);
        float interH = MathF.Max(0f, y2 - y1);
        float inter = interW * interH;
        if (inter <= 0f) return 0f;

        float union = a.Area + b.Area - inter;
        return union > 0f ? inter / union : 0f;
    }

    private static float MeasureLocalSharpness(DecodedImage image, NormalizedPoint pt)
    {
        int cx = Math.Clamp((int)MathF.Round(pt.X * image.Width), 2, image.Width - 3);
        int cy = Math.Clamp((int)MathF.Round(pt.Y * image.Height), 2, image.Height - 3);
        int radius = Math.Clamp(Math.Min(image.Width, image.Height) / 80, 4, 24);

        int x0 = Math.Max(1, cx - radius);
        int x1 = Math.Min(image.Width - 2, cx + radius);
        int y0 = Math.Max(1, cy - radius);
        int y1 = Math.Min(image.Height - 2, cy + radius);

        float energy = 0f;
        int samples = 0;

        fixed (byte* pBase = image.Pixels)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float gx = SampleLum(pBase, image, x + 1, y) - SampleLum(pBase, image, x - 1, y);
                    float gy = SampleLum(pBase, image, x, y + 1) - SampleLum(pBase, image, x, y - 1);
                    energy += gx * gx + gy * gy;
                    samples++;
                }
            }
        }

        return samples > 0 ? energy / samples : 0f;
    }

    private static List<SubjectEyeTarget> DetectSalientSharpestRegionFallback(DecodedImage image)
    {
        // Divide central 80% of frame into an 8x8 grid and locate highest micro-contrast subject cell
        const int grid = 8;
        float bestEnergy = -1f;
        int bestGx = grid / 2;
        int bestGy = grid / 3;

        fixed (byte* pBase = image.Pixels)
        {
            for (int gy = 1; gy < grid - 1; gy++)
            {
                for (int gx = 1; gx < grid - 1; gx++)
                {
                    int x0 = image.Width * gx / grid;
                    int x1 = image.Width * (gx + 1) / grid;
                    int y0 = image.Height * gy / grid;
                    int y1 = image.Height * (gy + 1) / grid;
                    int step = Math.Max(2, (x1 - x0) / 16);

                    float energy = 0f;
                    int count = 0;
                    for (int y = Math.Max(1, y0); y < Math.Min(image.Height - 1, y1); y += step)
                    {
                        for (int x = Math.Max(1, x0); x < Math.Min(image.Width - 1, x1); x += step)
                        {
                            float dx = SampleLum(pBase, image, x + 1, y) - SampleLum(pBase, image, x - 1, y);
                            float dy = SampleLum(pBase, image, x, y + 1) - SampleLum(pBase, image, x, y - 1);
                            energy += dx * dx + dy * dy;
                            count++;
                        }
                    }

                    if (count > 0)
                    {
                        float avg = energy / count;
                        if (avg > bestEnergy)
                        {
                            bestEnergy = avg;
                            bestGx = gx;
                            bestGy = gy;
                        }
                    }
                }
            }
        }

        float px = (bestGx + 0.5f) / grid;
        float py = (bestGy + 0.5f) / grid;
        var pt = new NormalizedPoint(px, py);
        var box = new NormalizedRect(px - 0.06f, py - 0.06f, 0.12f, 0.12f);
        return new List<SubjectEyeTarget> { new(box, pt, pt, 0.5f, "Contrast") };
    }

    private static float SampleLum(byte* pBase, DecodedImage img, int x, int y)
    {
        if (img.PixelFormat == ImagePixelFormat.Rgba64)
        {
            ushort* px = (ushort*)(pBase + (long)y * img.Stride) + x * 4;
            return (0.2126f * px[0] + 0.7152f * px[1] + 0.0722f * px[2]) * (1f / 257f);
        }
        else
        {
            byte* px = pBase + (long)y * img.Stride + x * 4;
            return 0.0722f * px[0] + 0.7152f * px[1] + 0.2126f * px[2];
        }
    }

    private static bool CheckStatus(IntPtr status)
    {
        if (status == IntPtr.Zero) return true;
        var releaseStatus = (delegate* unmanaged[Cdecl]<IntPtr, void>)_api[93];
        releaseStatus(status);
        return false;
    }

    private static void ReleaseIfError(IntPtr status)
    {
        if (status != IntPtr.Zero)
        {
            var releaseStatus = (delegate* unmanaged[Cdecl]<IntPtr, void>)_api[93];
            releaseStatus(status);
        }
    }
}
