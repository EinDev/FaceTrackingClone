using System.Runtime.InteropServices;

namespace FaceTrackingClone.Interop;

// ---------------------------------------------------------------------------
// Media Foundation COM interop.
//
// Hand-written rather than a NuGet wrapper on purpose: the capture loop has to
// tear down and rebuild a source reader at any moment without leaking native
// objects, which is far easier to reason about when marshalling is explicit and
// every HRESULT is visible.
//
// IMPORTANT: every interface below is declared FLAT -- inherited COM methods are
// redeclared in full rather than relying on C# interface inheritance. The CLR
// does not fold base-interface methods into a ComImport vtable, so writing
// `IMFActivate : IMFAttributes` silently places ActivateObject at slot 3 (where
// IMFAttributes::GetItem lives) and calls land on the wrong function. Order
// within each interface is the vtable order and must not be changed.
// ---------------------------------------------------------------------------

[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
}

[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    // --- IMFAttributes (slots 3..32) ---
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);

    // --- IMFMediaType (slots 33..37) ---
    [PreserveSig] int GetMajorType(out Guid pguidMajorType);
    [PreserveSig] int IsCompressedFormat(out bool pfCompressed);
    [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
    [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
    [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
}

[ComImport]
[Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate
{
    // --- IMFAttributes (slots 3..32) ---
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);

    // --- IMFActivate (slots 33..35) ---
    [PreserveSig] int ActivateObject(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport]
[Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaSource
{
    // --- IMFMediaEventGenerator (slots 3..6) ---
    [PreserveSig] int GetEvent(uint dwFlags, out IntPtr ppEvent);
    [PreserveSig] int BeginGetEvent(IntPtr pCallback, IntPtr punkState);
    [PreserveSig] int EndGetEvent(IntPtr pResult, out IntPtr ppEvent);
    [PreserveSig] int QueueEvent(uint met, in Guid guidExtendedType, int hrStatus, IntPtr pvValue);

    // --- IMFMediaSource (slots 7..12) ---
    [PreserveSig] int GetCharacteristics(out uint pdwCharacteristics);
    [PreserveSig] int CreatePresentationDescriptor(out IntPtr ppPresentationDescriptor);
    [PreserveSig] int Start(IntPtr pPresentationDescriptor, in Guid pguidTimeFormat, IntPtr pvarStartPosition);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Shutdown();
}

[ComImport]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint dwStreamIndex, out bool pfSelected);
    [PreserveSig] int SetStreamSelection(uint dwStreamIndex, bool fSelected);
    [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType? ppMediaType);
    [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType? ppMediaType);
    [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
    [PreserveSig] int SetCurrentPosition(in Guid guidTimeFormat, IntPtr varPosition);
    /// <summary>
    /// The four output parameters are raw pointers rather than `out` values because asynchronous
    /// mode REQUIRES them to be NULL -- passing addresses makes ReadSample fail with E_INVALIDARG.
    /// Use <see cref="Mf.ReadSampleSync"/> for the synchronous case.
    /// </summary>
    [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, IntPtr pdwActualStreamIndex,
        IntPtr pdwStreamFlags, IntPtr pllTimestamp, IntPtr ppSample);
    [PreserveSig] int Flush(uint dwStreamIndex);
    [PreserveSig] int GetServiceForStream(uint dwStreamIndex, in Guid guidService, in Guid riid, out IntPtr ppvObject);
    [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, in Guid guidAttribute, IntPtr pvarAttribute);
}

[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    // --- IMFAttributes (slots 3..32) ---
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);

    // --- IMFSample (slots 33..46) ---
    [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
    [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
    [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer? ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer? ppBuffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
}

/// <summary>
/// Asynchronous source-reader callback. Declaration order below is the vtable order from
/// mfreadwrite.h and must not be reordered.
/// </summary>
[ComImport]
[Guid("deec8d99-fa1d-4d82-84c2-2c8969944867")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReaderCallback
{
    [PreserveSig] int OnReadSample(int hrStatus, uint dwStreamIndex, uint dwStreamFlags,
        long llTimestamp, IMFSample? pSample);
    [PreserveSig] int OnFlush(uint dwStreamIndex);
    [PreserveSig] int OnEvent(uint dwStreamIndex, IntPtr pEvent);
}

[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
    [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
}

internal static class Mf
{
    /// <summary>MF_VERSION for Windows 7 and later: (MF_SDK_VERSION &lt;&lt; 16) | MF_API_VERSION.</summary>
    public const uint MF_VERSION = 0x00020070;

    public const uint MFSTARTUP_NOSOCKET = 1;

    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;

    // MF_SOURCE_READER_FLAG values returned by ReadSample.
    public const uint MF_SOURCE_READERF_ERROR = 0x00000001;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    public const uint MF_SOURCE_READERF_NEWSTREAM = 0x00000004;
    public const uint MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED = 0x00000010;
    public const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000020;
    public const uint MF_SOURCE_READERF_STREAMTICK = 0x00000100;
    public const uint MF_SOURCE_READERF_ALLEFFECTSREMOVED = 0x00000200;

    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
    public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6);
    public const int MF_E_HW_MFT_FAILED_START_STREAMING = unchecked((int)0xC00D3E85);
    public const int MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED = unchecked((int)0xC00DABE1);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mf.dll", ExactSpelling = true)]
    public static extern int MFEnumDeviceSources(IMFAttributes pAttributes, out IntPtr pppSourceActivate,
        out uint pcSourceActivate);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    public static extern int MFCreateSourceReaderFromMediaSource(IMFMediaSource pMediaSource,
        IMFAttributes? pAttributes, out IMFSourceReader ppSourceReader);

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new MediaFoundationException(what, hr);
    }

    /// <summary>
    /// Synchronous ReadSample wrapper. Only valid on a reader that was created WITHOUT an
    /// async callback attribute.
    /// </summary>
    public static unsafe int ReadSampleSync(this IMFSourceReader reader, uint streamIndex,
        out uint streamFlags, out long timestamp, out IMFSample? sample)
    {
        uint actualIndex = 0, flags = 0;
        long ts = 0;
        IntPtr samplePtr = IntPtr.Zero;

        int hr = reader.ReadSample(streamIndex, 0,
            (IntPtr)(&actualIndex), (IntPtr)(&flags), (IntPtr)(&ts), (IntPtr)(&samplePtr));

        streamFlags = flags;
        timestamp = ts;

        if (samplePtr != IntPtr.Zero)
        {
            sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
            Marshal.Release(samplePtr); // GetObjectForIUnknown took its own reference.
        }
        else
        {
            sample = null;
        }

        return hr;
    }

    /// <summary>
    /// Asynchronous ReadSample request. All output parameters must be NULL in this mode.
    /// </summary>
    public static int ReadSampleAsync(this IMFSourceReader reader, uint streamIndex)
        => reader.ReadSample(streamIndex, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Reads a string attribute from an activate, returning null when absent.</summary>
    public static string? GetStringOrNull(this IMFActivate attrs, in Guid key)
    {
        int hr = attrs.GetAllocatedString(key, out IntPtr ptr, out _);
        if (hr < 0) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>Unpacks an MF_MT_FRAME_SIZE / MF_MT_FRAME_RATE style packed UINT64.</summary>
    public static (uint High, uint Low) UnpackRatio(ulong packed)
        => ((uint)(packed >> 32), (uint)(packed & 0xFFFFFFFF));
}

internal sealed class MediaFoundationException : Exception
{
    public int HResult32 { get; }

    public MediaFoundationException(string what, int hr)
        : base($"{what} failed: 0x{hr:X8}{Describe(hr)}")
    {
        HResult32 = hr;
    }

    private static string Describe(int hr) => hr switch
    {
        Mf.MF_E_NO_MORE_TYPES => " (MF_E_NO_MORE_TYPES)",
        Mf.MF_E_INVALIDMEDIATYPE => " (MF_E_INVALIDMEDIATYPE)",
        Mf.MF_E_ATTRIBUTENOTFOUND => " (MF_E_ATTRIBUTENOTFOUND)",
        Mf.MF_E_HW_MFT_FAILED_START_STREAMING => " (MF_E_HW_MFT_FAILED_START_STREAMING)",
        Mf.MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED => " (MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED)",
        unchecked((int)0x80070005) => " (E_ACCESSDENIED - device in use, or camera privacy setting)",
        unchecked((int)0x8007001F) => " (ERROR_GEN_FAILURE - device wedged)",
        _ => ""
    };
}
