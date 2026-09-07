# 0025 — A screenshot shows the frame before the one you set up

## Context

The capture paths (`--build-shot`, `--belt-shot`, `--uplink-shot`) set the world
up and take the picture in the same `_Process` pass:

```csharp
if (_screenshotCountdown == 2) ShowTheUplink();
if (_screenshotCountdown > 0 && --_screenshotCountdown == 0)
    GetViewport().GetTexture().GetImage().SavePng("user://shot.png");
```

`GetViewport().GetTexture()` returns the frame that has **already been drawn**
when `_Process` runs. A machine placed for the shot is therefore absent from the
image, and the ghost shown for the shot is absent from the image, even though
both are in the tree, visible, synced and inside the frustum by the time the file
is written.

This cost real time. The empty capture was read as a rendering defect and written
on the handoff board as "a machine on real terrain is drawn underneath the
ground", with `TerrainRenderer` lifting tiles by `HeightAt` as the stated cause.
That cause is false — `TerrainRenderer.Write` seats every tile at a fixed
`y = -0.04` and the height value it computes feeds a *colour*. Instrumenting the
pool settled it: hull count 1, `Visible` true, a correct AABB, and the machine
unprojecting to screen (576, 293), dead centre — drawn nowhere.

## Decision

Set the capture up four frames before it fires, not one. The gap has to cover the
world change, the `Sync` that follows it, and the draw that follows that.

Two smaller things fell out of the same investigation and are fixed with it:

- **`CameraRig.Apply` aimed with `LookAt`**, which reads the camera's *global*
  transform. Godot has not recomputed it when the rig was moved earlier in the
  same frame, so a camera pointed at a freshly placed machine aimed at where the
  rig used to be. The camera is now aimed in rig-local space with
  `Basis.LookingAt(-offset, Vector3.Up)`; the rig is never rotated, so local and
  global orientation agree.
- **Zoom was set without applying it.** `ZoomLevel` is the orthographic size and
  nothing re-reads it until `Apply()`, so `FrameTheBelts` framed the belts at the
  default zoom — far enough out that the tunnel it had deliberately chosen was a
  few pixels wide.

The build capture also moves the mouse rather than placing a ghost directly.
`UpdateGhost` re-derives the ghost from the cursor every frame, so a ghost shown
by the capture path is gone by the next one; and the ghost is deliberately hidden
while the pointer is over the build panel, so a cursor at screen centre — inside
that panel's rect — produced a capture with no ghost in it at all.

## Consequences

The captures now show what they claim: an Uplink on open ground, a green
placement ghost clear of the menu, and a belt line with items, a splitter and a
tunnel end at a readable size.

The general lesson is narrower than "screenshots find things". A screenshot is
evidence about a frame, and which frame that is has to be established before the
image is used as evidence about the renderer. An empty picture is the same
picture whether the geometry is missing, hidden, off-camera, or simply not yet
drawn — and three of those four were wrong here.
