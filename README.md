# FaceTrackingClone

Replaces SRanipal's lip-tracking path with a self-healing driver that talks straight to the
VIVE Facial Tracker over UVC, so a framedrop-induced stall recovers on its own instead of
requiring the usual `taskkill` dance.

## Why this shape

The two trackers are not equally replaceable, so the design is deliberately asymmetric.

| | Device | Access | Verdict |
|---|---|---|---|
| **Lip** | `VID_0BB4&PID_0321` | Stock Microsoft `usbvideo` UVC driver | **Replaced.** Plain Media Foundation capture, no SRanipal, no driver swap |
| **Eye** | Tobii `EyeChip` `VID_2104&PID_020F` | Vendor protocol, no public docs | **Not replaceable.** SRanipal stays, but gets contained |

Measured on the real hardware: one native mode, `YUY2 400x400 @60`, 320000 bytes/frame, sustained
60.3 fps. The frame is a **stereo pair** — two 200x400 views side by side; only the left is used.

Notably, **no vendor activation is required**. Babble's `0x14` / `0xab` control transfers are
needed when driving the device through libusb, but the stock UVC driver already starts the sensor
and IR illuminator.

## Usage

```
ftclone probe             # enumerate devices, formats, and whether they open
ftclone grab [frames]     # capture N frames, report timing/luma stats, dump BMPs
ftclone watch [seconds]   # run the self-healing driver, print health once a second
    --allow-device-reset  # permit PnP device restart on repeated failure (needs admin)
ftclone view              # live video window with stats
    --attach              # attach to a feed another process already owns
ftclone model [path]      # print ONNX model input/output metadata
ftclone infer [seconds]   # live blendshapes from the tracker
```

The inference model is not committed (~23MB, belongs to Project Babble). Fetch it with
`tools/fetch-model.ps1`. It takes a single-channel `224x224` input and emits 45 blendshapes;
inference measures ~10ms on this hardware.

## Viewer

`ftclone view` shows four panes: the IR camera, a **face render**, per-shape **bars**, and
pipeline stats.

The face render is a schematic 2D vector rig — *not* your avatar. It has no mesh, no textures and
no relation to any specific model; it exists to answer "is tracking working and plausible" at a
glance. Jaw, lip corners, pucker/funnel, stretch, cheek puff/suck, jaw sway, tongue and nose
sneer all move. The eyes are drawn greyed and inert, because this hardware does not track them.

Because a UVC device can only be opened once, the viewer publishes and consumes frames through a
memory-mapped seqlock:

* **Attach mode** (something else owns the camera) — reuses the publisher's weights. Cheap.
* **Owner mode** (camera free) — drives the tracker *and* runs the model itself, so it costs
  noticeably more. That is the price of running inference twice.

Minimised, the viewer does no work at all: no shared-memory read, no drawing, ~0% CPU.

The shared block has a fixed name and a versioned magic number, so a stale module reports
`VERSION MISMATCH` rather than silently failing to connect. **Reinstall the module and rebuild
the CLI together** whenever the IPC layout changes.

## Environment variables

| Variable | Default | Effect |
|---|---|---|
| `FTCLONE_TRACE` | `0` | Fine-grained capture-pipeline tracing |
| `FTCLONE_THREADS` | `1` | ONNX intra-op threads. More threads = lower latency, **higher total CPU** |
| `FTCLONE_INFERENCE_HZ` | `30` | Inference rate cap in the module |
| `FTCLONE_VIEWER` | `1` | Auto-launch the viewer alongside the module |
| `FTCLONE_PREVIEW_HZ` | `0` | VRCFT Hardware Debug card feed. Off because VRCFT 5.4.5's module host never forwards image frames |

## VRCFaceTracking module

**Just want to use it?** Grab the zip from Releases, close VRCFaceTracking, and run
`install.cmd`. It downloads the lip model from Project Babble on first install.

**Building from source** needs the .NET 10 SDK and a VRCFaceTracking install to compile
against. The standard Steam location is found automatically; otherwise point `VrcftPath` at it,
either with `-p:VrcftPath=...` or in a `Directory.Build.local.props` (gitignored):

```xml
<Project>
  <PropertyGroup>
    <VrcftPath>D:\SteamLibrary\steamapps\common\VRCFaceTracking</VrcftPath>
  </PropertyGroup>
</Project>
```

No VRCFaceTracking install? `.\tools\fetch-vrcft.ps1 [-Version 5.4.5]` builds the reference
assemblies from VRCFT's source into `.vrcft\<version>\` and prints the path.

```powershell
.\tools\install-module.ps1        # builds and installs to %APPDATA%\VRCFaceTracking\CustomLibs\
.\tools\package.ps1               # builds out\FaceTrackingClone-<version>.zip
```

## Supported VRCFaceTracking versions

`tools/vrcft-versions.json` lists the VRCFT releases CI compiles against, each pinned to the
commit it was cut from (VRCFT stopped tagging after v4). The first entry is what release zips are
built against. To support a new VRCFT release, add it at the top and drop the oldest.

## Releasing

Releases are cut by [release-please](https://github.com/googleapis/release-please), so commit
messages on `main` must follow [Conventional Commits](https://www.conventionalcommits.org/):
`fix:` bumps the patch version, `feat:` the minor, and `feat!:` or a `BREAKING CHANGE:` footer the
major (the minor, while below 1.0). release-please keeps a release PR open that bumps the version in
`module.json` and updates `CHANGELOG.md`. Merging it tags the release, and the zip is built and
attached automatically.

Installs as **VIVE Facial Tracker (FaceTrackingClone)**, providing **expression only**. Keep the
SRanipal module installed for **eye** — that half genuinely cannot be replaced. The two run side
by side, and VRCFT 5.4 hosts modules out-of-process, so they are already isolated from each other.

Nothing else may hold the lip camera while the module runs; only one process can own a UVC device.

The module logs a health line every 30s (state, fps, frames, recoveries, inference time). That is
deliberate: the stall this project targets takes hours to surface, so the log has to be sufficient
to diagnose it without a reproduction.

## Recovery ladder

The watchdog is the only thread allowed to open, close, or rebuild the pipeline.

1. **Soft stall** (no frame for 400 ms) — flush the reader and re-arm.
2. **Hard stall** (1500 ms) or stream fault — rebuild the whole pipeline. Backoff grows
   300 ms → 3 s across consecutive failures.
3. **Every 3rd failed cycle** — PnP device restart via CfgMgr32 (opt-in, needs admin).

## Design notes worth keeping

These are all load-bearing; each one caused a real hang during development.

- **The source reader runs in async mode.** Synchronous `ReadSample` blocks forever on a wedged
  device, leaving the watchdog unable to act — the exact failure being fixed. Async mode also
  requires all four output parameters to be `NULL`, or it returns `E_INVALIDARG`.
- **`ComImport` vtables are not flattened by the CLR.** Inherited COM methods must be redeclared
  in every derived interface, or calls land on the wrong slot (`IMFActivate::ActivateObject`
  silently becomes `IMFAttributes::GetItem`).
- **`IMFActivate` caches its activated object.** Teardown must go through
  `IMFActivate::ShutdownObject`, not `IMFMediaSource::Shutdown`, or the next open fails with
  `MF_E_HW_MFT_FAILED_START_STREAMING`.
- **Teardown never runs on the watchdog thread and never holds the frame lock.** Both
  `ShutdownObject` and `ReleaseComObject` block on in-flight callbacks; the callback needs the
  frame lock to publish. Either mistake deadlocks recovery permanently.
- **Backoff timing uses integer milliseconds.** A fractional remainder truncated to a zero-length
  sleep slice that never decremented, spinning the watchdog at 100% CPU while tracking stayed
  frozen.

## Layout

```
src/FaceTrackingClone/
  Interop/      Media Foundation COM declarations
  Vive/         Capture device discovery, the self-healing driver, PnP reset
  Ipc/          Shared-memory frame transport
  Ui/           Live viewer window
  Imaging/      Greyscale BMP writer
tools/ApiDump/  Reflects over VRCFT's net10 assemblies from a net7 host
tools/package/  Installer and readme shipped inside the release zip
.github/        CI (VRCFT version matrix) and release-please
```

## Licence

This project's code is MIT, see `LICENSE`. Two things it uses are licensed separately:

- **The lip model** belongs to [Project Babble](https://github.com/Project-Babble/ProjectBabble)
  and is under the [Babble Software Distribution License 1.0](https://github.com/Project-Babble/ProjectBabble/blob/main/LICENSE.md),
  which **forbids commercial use**. It is not redistributed here; the installer downloads it from
  Babble. The MIT licence covers this code only, not the model: commercial use would need a
  different model.
- **ONNX Runtime** (MIT, Microsoft) ships in the release zip; its licence and third-party notices
  are included under `licenses/`.

Not affiliated with or endorsed by HTC, VIVE, Tobii, VRCFaceTracking or Project Babble. Product
names are used only to describe compatible hardware and software.
