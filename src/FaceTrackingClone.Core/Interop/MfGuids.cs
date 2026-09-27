namespace FaceTrackingClone.Interop;

/// <summary>
/// GUID constants for Media Foundation. Values are from mfapi.h / mfidl.h / uuids.h.
/// </summary>
internal static class MfGuids
{
    // --- Device enumeration attributes -------------------------------------
    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE =
        new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");

    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID =
        new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");

    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK =
        new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");

    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME =
        new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");

    // --- Media type attributes ---------------------------------------------
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    // --- Major types --------------------------------------------------------
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");

    // --- Source reader ------------------------------------------------------
    public static readonly Guid MF_SOURCE_READER_DISABLE_DXVA =
        new("aa456cfd-3943-4a1e-a77d-1838c0ea2e35");

    public static readonly Guid MF_READWRITE_DISABLE_CONVERTERS =
        new("98d5b065-1374-4847-8d5d-31520fee7156");

    public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING =
        new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    /// <summary>
    /// Set to an IMFSourceReaderCallback to put the reader in asynchronous mode. Essential here:
    /// in synchronous mode ReadSample blocks indefinitely on a wedged device, which is the exact
    /// failure this project exists to survive.
    /// </summary>
    public static readonly Guid MF_SOURCE_READER_ASYNC_CALLBACK =
        new("1e3dbeac-bb43-4c35-b507-cd644464c965");

    /// <summary>
    /// Builds one of the FOURCC-derived video subtype GUIDs
    /// (XXXXXXXX-0000-0010-8000-00AA00389B71).
    /// </summary>
    public static Guid FromFourCc(string fourCc)
    {
        if (fourCc.Length != 4)
            throw new ArgumentException("FOURCC must be exactly 4 characters.", nameof(fourCc));

        uint value = (uint)(fourCc[0] | (fourCc[1] << 8) | (fourCc[2] << 16) | (fourCc[3] << 24));
        return new Guid(value, 0x0000, 0x0010,
            0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    }

    /// <summary>
    /// Reverses <see cref="FromFourCc"/> for display purposes, returning null when the GUID is
    /// not a FOURCC-derived subtype.
    /// </summary>
    public static string? ToFourCc(Guid subtype)
    {
        Span<byte> bytes = stackalloc byte[16];
        subtype.TryWriteBytes(bytes);

        // Tail must match the fixed ...-0000-0010-8000-00AA00389B71 suffix.
        ReadOnlySpan<byte> tail = stackalloc byte[]
        {
            0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
        };
        if (!bytes[4..].SequenceEqual(tail))
            return null;

        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char c = (char)bytes[i];
            if (c is < ' ' or > '~') return null;
            chars[i] = c;
        }
        return new string(chars);
    }
}
