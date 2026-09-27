namespace FaceTrackingClone.Inference;

/// <summary>
/// The 45 model outputs, in the order the network emits them.
///
/// Order is load-bearing: it is the index mapping used by the reference implementation, and the
/// values are meaningless if it drifts. Do not reorder, and do not "tidy" the casing -- these
/// names match the upstream OSC parameter names so they stay greppable against that project.
/// </summary>
internal enum BabbleShape
{
    CheekPuffLeft = 0,
    CheekPuffRight = 1,
    CheekSuckLeft = 2,
    CheekSuckRight = 3,
    JawOpen = 4,
    JawForward = 5,
    JawLeft = 6,
    JawRight = 7,
    NoseSneerLeft = 8,
    NoseSneerRight = 9,
    MouthFunnel = 10,
    MouthPucker = 11,
    MouthLeft = 12,
    MouthRight = 13,
    MouthRollUpper = 14,
    MouthRollLower = 15,
    MouthShrugUpper = 16,
    MouthShrugLower = 17,
    MouthClose = 18,
    MouthSmileLeft = 19,
    MouthSmileRight = 20,
    MouthFrownLeft = 21,
    MouthFrownRight = 22,
    MouthDimpleLeft = 23,
    MouthDimpleRight = 24,
    MouthUpperUpLeft = 25,
    MouthUpperUpRight = 26,
    MouthLowerDownLeft = 27,
    MouthLowerDownRight = 28,
    MouthPressLeft = 29,
    MouthPressRight = 30,
    MouthStretchLeft = 31,
    MouthStretchRight = 32,
    TongueOut = 33,
    TongueUp = 34,
    TongueDown = 35,
    TongueLeft = 36,
    TongueRight = 37,
    TongueRoll = 38,
    TongueBendDown = 39,
    TongueCurlUp = 40,
    TongueSquish = 41,
    TongueFlat = 42,
    TongueTwistLeft = 43,
    TongueTwistRight = 44
}

internal static class BabbleShapes
{
    public const int Count = 45;

    public static readonly string[] Names =
    {
        "cheekPuffLeft", "cheekPuffRight", "cheekSuckLeft", "cheekSuckRight",
        "jawOpen", "jawForward", "jawLeft", "jawRight",
        "noseSneerLeft", "noseSneerRight",
        "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight",
        "mouthRollUpper", "mouthRollLower", "mouthShrugUpper", "mouthShrugLower",
        "mouthClose", "mouthSmileLeft", "mouthSmileRight",
        "mouthFrownLeft", "mouthFrownRight", "mouthDimpleLeft", "mouthDimpleRight",
        "mouthUpperUpLeft", "mouthUpperUpRight", "mouthLowerDownLeft", "mouthLowerDownRight",
        "mouthPressLeft", "mouthPressRight", "mouthStretchLeft", "mouthStretchRight",
        "tongueOut", "tongueUp", "tongueDown", "tongueLeft", "tongueRight",
        "tongueRoll", "tongueBendDown", "tongueCurlUp", "tongueSquish", "tongueFlat",
        "tongueTwistLeft", "tongueTwistRight"
    };

    public static string NameOf(int index) =>
        index >= 0 && index < Names.Length ? Names[index] : $"#{index}";
}
