using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinGlance.Model;

namespace WinGlance.Engine;

public sealed record TrashedFileRecord(string OriginalPath, string? TrashedPath);

public static class TrashManager
{
    private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IID_IFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IFileOperationProgressSink = new("04b0f1a7-9490-44bc-96e1-4296a31252e2");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");

    private const uint COINIT_APARTMENTTHREADED = 0x2u;
    private const uint CLSCTX_ALL = 0x17u;
    private const uint SIGDN_FILESYSPATH = 0x80058000u;

    private const uint FOF_SILENT = 0x0004u;
    private const uint FOF_NOCONFIRMATION = 0x0010u;
    private const uint FOF_ALLOWUNDO = 0x0040u;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200u;
    private const uint FOF_NOERRORUI = 0x0400u;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000u;

    private sealed class SinkState
    {
        public string? TrashedPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SinkComObject
    {
        public void** Vtbl;
        public int RefCount;
        public IntPtr StateHandle;
    }

    private static unsafe readonly void** s_sinkVtbl = AllocateSinkVtbl();

    private static unsafe void** AllocateSinkVtbl()
    {
        var vtbl = (void**)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(TrashManager), sizeof(void*) * 19);
        vtbl[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&SinkQueryInterface;
        vtbl[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&SinkAddRef;
        vtbl[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&SinkRelease;
        vtbl[3] = (delegate* unmanaged[Stdcall]<void*, int>)&SinkNoOp0;
        vtbl[4] = (delegate* unmanaged[Stdcall]<void*, int, int>)&SinkNoOp1Int;
        vtbl[5] = (delegate* unmanaged[Stdcall]<void*, uint, void*, char*, int>)&SinkPreRename;
        vtbl[6] = (delegate* unmanaged[Stdcall]<void*, uint, void*, char*, int, void*, int>)&SinkPostRename;
        vtbl[7] = (delegate* unmanaged[Stdcall]<void*, uint, void*, void*, char*, int>)&SinkPreMoveCopy;
        vtbl[8] = (delegate* unmanaged[Stdcall]<void*, uint, void*, void*, char*, int, void*, int>)&SinkPostMoveCopy;
        vtbl[9] = (delegate* unmanaged[Stdcall]<void*, uint, void*, void*, char*, int>)&SinkPreMoveCopy;
        vtbl[10] = (delegate* unmanaged[Stdcall]<void*, uint, void*, void*, char*, int, void*, int>)&SinkPostMoveCopy;
        vtbl[11] = (delegate* unmanaged[Stdcall]<void*, uint, void*, int>)&SinkPreDelete;
        vtbl[12] = (delegate* unmanaged[Stdcall]<void*, uint, void*, int, void*, int>)&SinkPostDeleteItem;
        vtbl[13] = (delegate* unmanaged[Stdcall]<void*, uint, void*, char*, int>)&SinkPreRename;
        vtbl[14] = (delegate* unmanaged[Stdcall]<void*, uint, void*, char*, char*, uint, int, void*, int>)&SinkPostNew;
        vtbl[15] = (delegate* unmanaged[Stdcall]<void*, uint, uint, int>)&SinkUpdateProgress;
        vtbl[16] = (delegate* unmanaged[Stdcall]<void*, int>)&SinkNoOp0;
        vtbl[17] = (delegate* unmanaged[Stdcall]<void*, int>)&SinkNoOp0;
        vtbl[18] = (delegate* unmanaged[Stdcall]<void*, int>)&SinkNoOp0;
        return vtbl;
    }

    private static unsafe SinkComObject* CreateSink(SinkState state)
    {
        var obj = (SinkComObject*)NativeMemory.Alloc((nuint)sizeof(SinkComObject));
        obj->Vtbl = s_sinkVtbl;
        obj->RefCount = 1;
        obj->StateHandle = GCHandle.ToIntPtr(GCHandle.Alloc(state));
        return obj;
    }

    private static unsafe uint SinkAddRefCore(void* pThis)
    {
        var obj = (SinkComObject*)pThis;
        return (uint)Interlocked.Increment(ref obj->RefCount);
    }

    private static unsafe uint SinkReleaseCore(void* pThis)
    {
        var obj = (SinkComObject*)pThis;
        int count = Interlocked.Decrement(ref obj->RefCount);
        if (count == 0)
        {
            if (obj->StateHandle != IntPtr.Zero)
            {
                var handle = GCHandle.FromIntPtr(obj->StateHandle);
                if (handle.IsAllocated) handle.Free();
                obj->StateHandle = IntPtr.Zero;
            }
            NativeMemory.Free(obj);
        }
        return (uint)Math.Max(0, count);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkQueryInterface(void* pThis, Guid* riid, void** ppvObject)
    {
        if (ppvObject == null) return unchecked((int)0x80004003); // E_POINTER
        if (riid != null && (*riid == IID_IUnknown || *riid == IID_IFileOperationProgressSink))
        {
            *ppvObject = pThis;
            SinkAddRefCore(pThis);
            return 0;
        }
        *ppvObject = null;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe uint SinkAddRef(void* pThis) => SinkAddRefCore(pThis);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe uint SinkRelease(void* pThis) => SinkReleaseCore(pThis);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkNoOp0(void* pThis) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkNoOp1Int(void* pThis, int hr) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkPreRename(void* pThis, uint dwFlags, void* psiItem, char* pszNewName) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkPostRename(void* pThis, uint dwFlags, void* psiItem, char* pszNewName, int hr, void* psiNew) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkPreMoveCopy(void* pThis, uint dwFlags, void* psiItem, void* psiDest, char* pszNewName) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkPostMoveCopy(void* pThis, uint dwFlags, void* psiItem, void* psiDest, char* pszNewName, int hr, void* psiNew) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkPreDelete(void* pThis, uint dwFlags, void* psiItem) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkPostDeleteItem(void* pThis, uint dwFlags, void* psiItem, int hrDelete, void* psiNewlyCreated)
    {
        if (hrDelete >= 0 && psiNewlyCreated != null)
        {
            var obj = (SinkComObject*)pThis;
            if (obj->StateHandle != IntPtr.Zero)
            {
                var handle = GCHandle.FromIntPtr(obj->StateHandle);
                if (handle.Target is SinkState state)
                {
                    char* pszPath = null;
                    int hrName = ShellItemGetDisplayName(psiNewlyCreated, SIGDN_FILESYSPATH, &pszPath);
                    if (hrName >= 0 && pszPath != null)
                    {
                        try
                        {
                            state.TrashedPath = Marshal.PtrToStringUni((IntPtr)pszPath);
                        }
                        finally
                        {
                            CoTaskMemFree(pszPath);
                        }
                    }
                }
            }
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkPostNew(void* pThis, uint dwFlags, void* psiDest, char* pszNewName, char* pszTpl, uint dwAttr, int hr, void* psiNew) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SinkUpdateProgress(void* pThis, uint iWorkTotal, uint iWorkSoFar) => 0;

    public static Task<TrashedFileRecord?> MoveToTrashAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Task.FromResult<TrashedFileRecord?>(null);

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return Task.FromResult<TrashedFileRecord?>(null);
        }

        return RunOnStaAsync(() => MoveToTrashCore(fullPath));
    }

    public static Task<bool> RestoreAsync(TrashedFileRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.TrashedPath) ||
            (!File.Exists(record.TrashedPath) && !Directory.Exists(record.TrashedPath)))
        {
            return Task.FromResult(false);
        }

        return RunOnStaAsync(() => RestoreCore(record));
    }

    public static async Task<IReadOnlyList<TrashedFileRecord>> MoveItemAndSidecarsToTrashAsync(
        SlideItem item,
        Action<string>? onBeforeDelete = null)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in item.AllPaths)
        {
            if (File.Exists(p) && seen.Add(p)) paths.Add(p);
        }
        foreach (var sc in XmpSidecarManager.GetAllExistingSidecarPaths(item))
        {
            if (File.Exists(sc) && seen.Add(sc)) paths.Add(sc);
        }

        var records = new List<TrashedFileRecord>(paths.Count);
        foreach (var p in paths)
        {
            onBeforeDelete?.Invoke(p);
            var rec = await MoveToTrashAsync(p);
            if (rec != null)
            {
                records.Add(rec);
            }
        }
        return records;
    }

    private static unsafe TrashedFileRecord? MoveToTrashCore(string fullPath)
    {
        void* pFileOp = null;
        void* pShellItem = null;
        SinkComObject* pSink = null;
        var sinkState = new SinkState();

        try
        {
            Guid clsid = CLSID_FileOperation;
            Guid iidOp = IID_IFileOperation;
            int hr = CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_ALL, &iidOp, &pFileOp);
            if (hr < 0 || pFileOp == null)
            {
                File.Delete(fullPath);
                return new TrashedFileRecord(fullPath, null);
            }

            hr = FileOpSetOperationFlags(
                pFileOp,
                FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOFX_RECYCLEONDELETE
            );
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            Guid iidItem = IID_IShellItem;
            fixed (char* pszPath = fullPath)
            {
                hr = SHCreateItemFromParsingName(pszPath, IntPtr.Zero, &iidItem, &pShellItem);
            }

            if (hr < 0 || pShellItem == null)
            {
                File.Delete(fullPath);
                return new TrashedFileRecord(fullPath, null);
            }

            pSink = CreateSink(sinkState);
            hr = FileOpDeleteItem(pFileOp, pShellItem, pSink);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            hr = FileOpPerformOperations(pFileOp);
            if (hr < 0 && File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return new TrashedFileRecord(fullPath, null);
            }

            return new TrashedFileRecord(fullPath, sinkState.TrashedPath);
        }
        catch
        {
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                    return new TrashedFileRecord(fullPath, null);
                }
                catch
                {
                    return null;
                }
            }
            return new TrashedFileRecord(fullPath, sinkState.TrashedPath);
        }
        finally
        {
            if (pSink != null) SinkReleaseCore(pSink);
            if (pShellItem != null) ComRelease(pShellItem);
            if (pFileOp != null) ComRelease(pFileOp);
        }
    }

    private static unsafe bool RestoreCore(TrashedFileRecord record)
    {
        if (string.IsNullOrEmpty(record.TrashedPath)) return false;

        string origFull = Path.GetFullPath(record.OriginalPath);
        string? parentDir = Path.GetDirectoryName(origFull);
        string origName = Path.GetFileName(origFull);
        if (string.IsNullOrEmpty(parentDir) || string.IsNullOrEmpty(origName)) return false;

        Directory.CreateDirectory(parentDir);

        void* pFileOp = null;
        void* pRecycledItem = null;
        void* pDestFolder = null;

        try
        {
            Guid clsid = CLSID_FileOperation;
            Guid iidOp = IID_IFileOperation;
            int hr = CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_ALL, &iidOp, &pFileOp);
            if (hr >= 0 && pFileOp != null)
            {
                FileOpSetOperationFlags(pFileOp, FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR);

                Guid iidItem = IID_IShellItem;
                fixed (char* pszRecycled = record.TrashedPath)
                {
                    hr = SHCreateItemFromParsingName(pszRecycled, IntPtr.Zero, &iidItem, &pRecycledItem);
                }

                if (hr >= 0 && pRecycledItem != null)
                {
                    fixed (char* pszDest = parentDir)
                    {
                        hr = SHCreateItemFromParsingName(pszDest, IntPtr.Zero, &iidItem, &pDestFolder);
                    }
                }

                if (hr >= 0 && pRecycledItem != null && pDestFolder != null)
                {
                    fixed (char* pszName = origName)
                    {
                        hr = FileOpMoveItem(pFileOp, pRecycledItem, pDestFolder, pszName, null);
                    }
                    if (hr >= 0)
                    {
                        hr = FileOpPerformOperations(pFileOp);
                        if (hr >= 0 && File.Exists(origFull))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall back to direct file move if shell MoveItem fails
        }
        finally
        {
            if (pDestFolder != null) ComRelease(pDestFolder);
            if (pRecycledItem != null) ComRelease(pRecycledItem);
            if (pFileOp != null) ComRelease(pFileOp);
        }

        try
        {
            if (!File.Exists(origFull) && File.Exists(record.TrashedPath))
            {
                File.Move(record.TrashedPath, origFull, overwrite: false);
            }
            return File.Exists(origFull);
        }
        catch
        {
            return false;
        }
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            int hrCo = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                if (hrCo >= 0) CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "WinGlance.ShellTrashSTA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    // Raw COM vtable dispatchers (NativeAOT-safe)
    private static unsafe uint ComRelease(void* punk)
    {
        var vtbl = *(void***)punk;
        return ((delegate* unmanaged[Stdcall]<void*, uint>)vtbl[2])(punk);
    }

    private static unsafe int ShellItemGetDisplayName(void* psi, uint sigdnName, char** ppszName)
    {
        var vtbl = *(void***)psi;
        return ((delegate* unmanaged[Stdcall]<void*, uint, char**, int>)vtbl[5])(psi, sigdnName, ppszName);
    }

    private static unsafe int FileOpSetOperationFlags(void* pOp, uint flags)
    {
        var vtbl = *(void***)pOp;
        return ((delegate* unmanaged[Stdcall]<void*, uint, int>)vtbl[5])(pOp, flags);
    }

    private static unsafe int FileOpMoveItem(void* pOp, void* psiItem, void* psiDestFolder, char* pszNewName, void* pfopsItem)
    {
        var vtbl = *(void***)pOp;
        return ((delegate* unmanaged[Stdcall]<void*, void*, void*, char*, void*, int>)vtbl[14])(pOp, psiItem, psiDestFolder, pszNewName, pfopsItem);
    }

    private static unsafe int FileOpDeleteItem(void* pOp, void* psiItem, void* pfopsItem)
    {
        var vtbl = *(void***)pOp;
        return ((delegate* unmanaged[Stdcall]<void*, void*, void*, int>)vtbl[18])(pOp, psiItem, pfopsItem);
    }

    private static unsafe int FileOpPerformOperations(void* pOp)
    {
        var vtbl = *(void***)pOp;
        return ((delegate* unmanaged[Stdcall]<void*, int>)vtbl[21])(pOp);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static unsafe extern int CoCreateInstance(Guid* rclsid, IntPtr pUnkOuter, uint dwClsContext, Guid* riid, void** ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static unsafe extern void CoTaskMemFree(void* pv);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static unsafe extern int SHCreateItemFromParsingName(char* pszPath, IntPtr pbc, Guid* riid, void** ppv);
}
