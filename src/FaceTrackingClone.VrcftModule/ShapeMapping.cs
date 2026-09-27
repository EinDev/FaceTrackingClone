using FaceTrackingClone.Inference;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace FaceTrackingClone.VrcftModule;

/// <summary>
/// Maps the model's 45 ARKit-style outputs onto VRCFaceTracking's Unified Expressions set.
///
/// The two vocabularies are not the same shape. Unified splits several ARKit shapes into
/// left/right or upper/lower pairs that the model does not distinguish, so one output has to
/// drive several Unified shapes (e.g. a single mouthFunnel feeds all four lip-funnel shapes).
/// That is expected and matches how other ARKit-sourced modules behave.
/// </summary>
internal static class ShapeMapping
{
    public static void Apply(IReadOnlyList<float> babble, UnifiedExpressionShape[] shapes)
    {
        float Get(BabbleShape s) => babble[(int)s];

        void Set(UnifiedExpressions target, float weight) =>
            shapes[(int)target].Weight = weight;

        // --- Jaw -----------------------------------------------------------
        Set(UnifiedExpressions.JawOpen, Get(BabbleShape.JawOpen));
        Set(UnifiedExpressions.JawForward, Get(BabbleShape.JawForward));
        Set(UnifiedExpressions.JawLeft, Get(BabbleShape.JawLeft));
        Set(UnifiedExpressions.JawRight, Get(BabbleShape.JawRight));
        Set(UnifiedExpressions.MouthClosed, Get(BabbleShape.MouthClose));

        // --- Cheeks --------------------------------------------------------
        Set(UnifiedExpressions.CheekPuffLeft, Get(BabbleShape.CheekPuffLeft));
        Set(UnifiedExpressions.CheekPuffRight, Get(BabbleShape.CheekPuffRight));
        Set(UnifiedExpressions.CheekSuckLeft, Get(BabbleShape.CheekSuckLeft));
        Set(UnifiedExpressions.CheekSuckRight, Get(BabbleShape.CheekSuckRight));

        // --- Nose ----------------------------------------------------------
        Set(UnifiedExpressions.NoseSneerLeft, Get(BabbleShape.NoseSneerLeft));
        Set(UnifiedExpressions.NoseSneerRight, Get(BabbleShape.NoseSneerRight));

        // --- Lip funnel / pucker -------------------------------------------
        // One ARKit shape each, fanned out to Unified's four-way split.
        float funnel = Get(BabbleShape.MouthFunnel);
        Set(UnifiedExpressions.LipFunnelUpperLeft, funnel);
        Set(UnifiedExpressions.LipFunnelUpperRight, funnel);
        Set(UnifiedExpressions.LipFunnelLowerLeft, funnel);
        Set(UnifiedExpressions.LipFunnelLowerRight, funnel);

        float pucker = Get(BabbleShape.MouthPucker);
        Set(UnifiedExpressions.LipPuckerUpperLeft, pucker);
        Set(UnifiedExpressions.LipPuckerUpperRight, pucker);
        Set(UnifiedExpressions.LipPuckerLowerLeft, pucker);
        Set(UnifiedExpressions.LipPuckerLowerRight, pucker);

        // --- Lip suck (ARKit "roll") ---------------------------------------
        float rollUpper = Get(BabbleShape.MouthRollUpper);
        Set(UnifiedExpressions.LipSuckUpperLeft, rollUpper);
        Set(UnifiedExpressions.LipSuckUpperRight, rollUpper);

        float rollLower = Get(BabbleShape.MouthRollLower);
        Set(UnifiedExpressions.LipSuckLowerLeft, rollLower);
        Set(UnifiedExpressions.LipSuckLowerRight, rollLower);

        // --- Mouth translation ---------------------------------------------
        float left = Get(BabbleShape.MouthLeft);
        Set(UnifiedExpressions.MouthUpperLeft, left);
        Set(UnifiedExpressions.MouthLowerLeft, left);

        float right = Get(BabbleShape.MouthRight);
        Set(UnifiedExpressions.MouthUpperRight, right);
        Set(UnifiedExpressions.MouthLowerRight, right);

        // --- Shrug / raiser -------------------------------------------------
        Set(UnifiedExpressions.MouthRaiserUpper, Get(BabbleShape.MouthShrugUpper));
        Set(UnifiedExpressions.MouthRaiserLower, Get(BabbleShape.MouthShrugLower));

        // --- Smile / frown ---------------------------------------------------
        // ARKit smile maps onto Unified's corner pull; slant is left at the same value so
        // avatars keyed on either shape still animate.
        float smileLeft = Get(BabbleShape.MouthSmileLeft);
        float smileRight = Get(BabbleShape.MouthSmileRight);
        Set(UnifiedExpressions.MouthCornerPullLeft, smileLeft);
        Set(UnifiedExpressions.MouthCornerPullRight, smileRight);
        Set(UnifiedExpressions.MouthCornerSlantLeft, smileLeft);
        Set(UnifiedExpressions.MouthCornerSlantRight, smileRight);

        Set(UnifiedExpressions.MouthFrownLeft, Get(BabbleShape.MouthFrownLeft));
        Set(UnifiedExpressions.MouthFrownRight, Get(BabbleShape.MouthFrownRight));

        // --- Fine mouth detail ------------------------------------------------
        Set(UnifiedExpressions.MouthDimpleLeft, Get(BabbleShape.MouthDimpleLeft));
        Set(UnifiedExpressions.MouthDimpleRight, Get(BabbleShape.MouthDimpleRight));
        Set(UnifiedExpressions.MouthUpperUpLeft, Get(BabbleShape.MouthUpperUpLeft));
        Set(UnifiedExpressions.MouthUpperUpRight, Get(BabbleShape.MouthUpperUpRight));
        Set(UnifiedExpressions.MouthLowerDownLeft, Get(BabbleShape.MouthLowerDownLeft));
        Set(UnifiedExpressions.MouthLowerDownRight, Get(BabbleShape.MouthLowerDownRight));
        Set(UnifiedExpressions.MouthPressLeft, Get(BabbleShape.MouthPressLeft));
        Set(UnifiedExpressions.MouthPressRight, Get(BabbleShape.MouthPressRight));
        Set(UnifiedExpressions.MouthStretchLeft, Get(BabbleShape.MouthStretchLeft));
        Set(UnifiedExpressions.MouthStretchRight, Get(BabbleShape.MouthStretchRight));

        // --- Tongue ------------------------------------------------------------
        Set(UnifiedExpressions.TongueOut, Get(BabbleShape.TongueOut));
        Set(UnifiedExpressions.TongueUp, Get(BabbleShape.TongueUp));
        Set(UnifiedExpressions.TongueDown, Get(BabbleShape.TongueDown));
        Set(UnifiedExpressions.TongueLeft, Get(BabbleShape.TongueLeft));
        Set(UnifiedExpressions.TongueRight, Get(BabbleShape.TongueRight));
        Set(UnifiedExpressions.TongueRoll, Get(BabbleShape.TongueRoll));
        Set(UnifiedExpressions.TongueBendDown, Get(BabbleShape.TongueBendDown));
        Set(UnifiedExpressions.TongueCurlUp, Get(BabbleShape.TongueCurlUp));
    }
}
