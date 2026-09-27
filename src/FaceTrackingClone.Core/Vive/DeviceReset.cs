using System.Runtime.InteropServices;
using System.Text;

namespace FaceTrackingClone.Vive;

/// <summary>
/// Last-resort recovery: a PnP restart of the tracker, equivalent to
/// "Disable device" followed by "Enable device" in Device Manager.
///
/// This is only reached when reopening the capture pipeline repeatedly fails, which means the
/// device itself is wedged rather than the software on top of it. It requires administrator
/// rights, so it is opt-in.
/// </summary>
internal static class DeviceReset
{
    private const int CR_SUCCESS = 0;
    private const int CR_REMOVE_VETOED = 0x17;

    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    private const uint CM_SETUP_DEVNODE_READY = 0x00000000;

    /// <summary>Composite parent of the facial tracker, i.e. the whole USB device.</summary>
    private const string TrackerIdPrefix = @"USB\VID_0BB4&PID_0321\";

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? pszFilter, char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Query_And_Remove_SubTreeW(uint dnAncestor, out uint pVetoType,
        StringBuilder? pszVetoName, uint ulNameLength, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Setup_DevNode(uint dnDevInst, uint ulFlags);

    public static bool TryRestartViveFacialTracker(out string? error)
    {
        error = null;

        string? instanceId = FindTrackerInstanceId();
        if (instanceId is null)
        {
            error = "tracker device node not found";
            return false;
        }

        int cr = CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
        if (cr != CR_SUCCESS)
        {
            error = $"CM_Locate_DevNode failed (CR={cr})";
            return false;
        }

        var veto = new StringBuilder(260);
        cr = CM_Query_And_Remove_SubTreeW(devInst, out uint vetoType, veto, 260, 0);
        if (cr != CR_SUCCESS)
        {
            error = cr == CR_REMOVE_VETOED
                ? $"removal vetoed by '{veto}' (type {vetoType}) - another process is holding the device"
                : $"CM_Query_And_Remove_SubTree failed (CR={cr}) - administrator rights are required";
            return false;
        }

        cr = CM_Setup_DevNode(devInst, CM_SETUP_DEVNODE_READY);
        if (cr != CR_SUCCESS)
        {
            // The node was removed but not brought back. Windows re-enumerates USB on its own
            // shortly, so treat this as recoverable rather than fatal.
            error = $"CM_Setup_DevNode failed (CR={cr}); relying on USB re-enumeration";
            return false;
        }

        return true;
    }

    private static string? FindTrackerInstanceId()
    {
        if (CM_Get_Device_ID_List_SizeW(out uint len, "USB", CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS
            || len == 0)
        {
            return null;
        }

        var buffer = new char[len];
        if (CM_Get_Device_ID_ListW("USB", buffer, len, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS)
            return null;

        // Result is a double-null-terminated list of null-separated strings.
        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(id => id.StartsWith(TrackerIdPrefix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
