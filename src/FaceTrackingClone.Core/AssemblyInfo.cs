using System.Runtime.CompilerServices;

// The capture pipeline is intentionally internal -- it is not a public API surface, just shared
// implementation. Both the CLI and the VRCFT module consume it directly.
[assembly: InternalsVisibleTo("ftclone")]
[assembly: InternalsVisibleTo("FaceTrackingClone.VrcftModule")]
