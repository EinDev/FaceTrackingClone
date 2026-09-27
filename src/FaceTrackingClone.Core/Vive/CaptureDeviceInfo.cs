using System.Runtime.InteropServices;
using FaceTrackingClone.Interop;

namespace FaceTrackingClone.Vive;

/// <summary>
/// A live handle to an enumerated capture device. Holds the underlying IMFActivate so the
/// device can be opened from the very object MF handed us -- round-tripping through a second
/// filtered MFEnumDeviceSources call loses attributes and fails with MF_E_ATTRIBUTENOTFOUND.
/// </summary>
internal sealed class CaptureDeviceHandle : IDisposable
{
    /// <summary>USB VID/PID of the VIVE Facial Tracker (lower-face / lip tracker).</summary>
    public const string ViveFacialTrackerHardwareId = "vid_0bb4&pid_0321";

    private IMFActivate? _activate;

    internal CaptureDeviceHandle(IMFActivate activate, string friendlyName, string symbolicLink)
    {
        _activate = activate;
        FriendlyName = friendlyName;
        SymbolicLink = symbolicLink;
    }

    public string FriendlyName { get; }
    public string SymbolicLink { get; }

    public bool IsViveFacialTracker =>
        SymbolicLink.Contains(ViveFacialTrackerHardwareId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Instantiates the media source. Throws <see cref="MediaFoundationException"/> on failure --
    /// E_ACCESSDENIED here almost always means another process (SRanipal, VRCFT, a browser tab)
    /// already holds the camera.
    /// </summary>
    public IMFMediaSource Open()
    {
        if (_activate is null) throw new ObjectDisposedException(nameof(CaptureDeviceHandle));

        Guid iid = typeof(IMFMediaSource).GUID;
        Mf.Check(_activate.ActivateObject(iid, out object source), $"ActivateObject({FriendlyName})");
        return (IMFMediaSource)source;
    }

    /// <summary>Non-throwing variant, for probing several devices in a row.</summary>
    public int TryOpen(out IMFMediaSource? source)
    {
        source = null;
        if (_activate is null) return unchecked((int)0x80000013); // RO_E_CLOSED

        Guid iid = typeof(IMFMediaSource).GUID;
        int hr = _activate.ActivateObject(iid, out object obj);
        if (hr >= 0) source = (IMFMediaSource)obj;
        return hr;
    }

    /// <summary>
    /// Shuts down and un-caches the media source this handle created.
    ///
    /// IMFActivate caches the object it activates: a second ActivateObject call returns the
    /// *same* pointer rather than a fresh source. Calling IMFMediaSource::Shutdown alone
    /// therefore poisons the cache, and the next open fails with
    /// MF_E_HW_MFT_FAILED_START_STREAMING. Releasing through the activate is what actually
    /// resets it -- which is also exactly what recovery-after-stall will depend on.
    /// </summary>
    public void ShutdownActivated()
    {
        _activate?.ShutdownObject();
    }

    public void Dispose()
    {
        if (_activate is null) return;
        Marshal.ReleaseComObject(_activate);
        _activate = null;
    }

    public override string ToString() => $"{FriendlyName}  [{SymbolicLink}]";
}

internal sealed record MediaTypeInfo(
    uint Index,
    Guid Subtype,
    string SubtypeName,
    uint Width,
    uint Height,
    double FrameRate)
{
    public override string ToString() =>
        $"#{Index,-3} {SubtypeName,-8} {Width}x{Height} @ {FrameRate:0.##}fps";
}

internal static class CaptureDevices
{
    /// <summary>
    /// Enumerates every Media Foundation video capture device. The caller owns the returned
    /// handles and must dispose them.
    /// </summary>
    public static List<CaptureDeviceHandle> EnumerateVideo()
    {
        var results = new List<CaptureDeviceHandle>();

        Mf.Check(Mf.MFCreateAttributes(out IMFAttributes attrs, 1), "MFCreateAttributes");
        try
        {
            Mf.Check(attrs.SetGUID(MfGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                MfGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID), "SetGUID(source type)");

            Mf.Check(Mf.MFEnumDeviceSources(attrs, out IntPtr arrayPtr, out uint count),
                "MFEnumDeviceSources");

            if (arrayPtr == IntPtr.Zero) return results;

            try
            {
                for (uint i = 0; i < count; i++)
                {
                    IntPtr activatePtr = Marshal.ReadIntPtr(arrayPtr, (int)i * IntPtr.Size);
                    if (activatePtr == IntPtr.Zero) continue;

                    // GetObjectForIUnknown adds its own reference; release the array's.
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(activatePtr);
                    Marshal.Release(activatePtr);

                    string name = activate.GetStringOrNull(MfGuids.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME)
                                  ?? "(unnamed)";
                    string link = activate.GetStringOrNull(
                        MfGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK) ?? "";

                    results.Add(new CaptureDeviceHandle(activate, name, link));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(arrayPtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(attrs);
        }

        return results;
    }

    /// <summary>
    /// Finds the VIVE Facial Tracker, disposing every other enumerated handle.
    /// </summary>
    public static CaptureDeviceHandle OpenViveFacialTracker(out List<CaptureDeviceHandle> all)
    {
        all = EnumerateVideo();
        var tracker = all.FirstOrDefault(d => d.IsViveFacialTracker);
        if (tracker is null)
        {
            foreach (var d in all) d.Dispose();
            all.Clear();
            throw new InvalidOperationException(
                "VIVE Facial Tracker (VID_0BB4&PID_0321) is not present as a capture device. " +
                "Check that it is plugged in.");
        }
        return tracker;
    }

    /// <summary>
    /// Lists every native media type the first video stream advertises.
    /// </summary>
    public static List<MediaTypeInfo> EnumerateNativeMediaTypes(IMFSourceReader reader)
    {
        var types = new List<MediaTypeInfo>();

        for (uint i = 0; ; i++)
        {
            int hr = reader.GetNativeMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, i,
                out IMFMediaType? mt);
            if (hr == Mf.MF_E_NO_MORE_TYPES || mt is null) break;
            Mf.Check(hr, $"GetNativeMediaType({i})");

            try
            {
                mt.GetGUID(MfGuids.MF_MT_SUBTYPE, out Guid subtype);
                mt.GetUINT64(MfGuids.MF_MT_FRAME_SIZE, out ulong packedSize);
                (uint w, uint h) = Mf.UnpackRatio(packedSize);

                double fps = 0;
                if (mt.GetUINT64(MfGuids.MF_MT_FRAME_RATE, out ulong packedRate) >= 0)
                {
                    (uint num, uint den) = Mf.UnpackRatio(packedRate);
                    if (den != 0) fps = (double)num / den;
                }

                types.Add(new MediaTypeInfo(i, subtype,
                    MfGuids.ToFourCc(subtype) ?? subtype.ToString(), w, h, fps));
            }
            finally
            {
                Marshal.ReleaseComObject(mt);
            }
        }

        return types;
    }
}
