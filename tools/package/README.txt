FaceTrackingClone - VIVE Facial Tracker module for VRCFaceTracking
===================================================================

Replaces SRanipal's LIP tracking with a direct camera driver that recovers from
framedrop stalls on its own. Eye tracking still comes from SRanipal.

Requirements
  - VIVE Facial Tracker
  - VRCFaceTracking 5.4.x
  - Internet on first install (the lip model is downloaded from Project Babble)

Install
  1. Close VRCFaceTracking.
  2. Double-click install.cmd.
  3. Start VRCFaceTracking.

Setup notes
  - Keep the SRanipal module installed, but use it for EYE only. Nothing else may
    hold the lip camera - only one program can open it at a time.
  - A small live viewer window opens alongside the module. To turn it off, set
    the environment variable FTCLONE_VIEWER=0.

Uninstall
  Delete %APPDATA%\VRCFaceTracking\CustomLibs\b3f1c0d2-5a44-4e18-9c77-2f8ad1e6b901

Licences
  - FaceTrackingClone: MIT, see LICENSE.
  - ONNX Runtime: MIT, see licenses\.
  - The lip model is downloaded from Project Babble and is under the Babble Software
    Distribution License 1.0, which forbids commercial use:
    https://github.com/Project-Babble/ProjectBabble/blob/main/LICENSE.md

Not affiliated with or endorsed by HTC, VIVE, Tobii, VRCFaceTracking or Project Babble.
