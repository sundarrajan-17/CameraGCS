# Epsilon GCS

A Windows ground control station for Redwire **Epsilon** gyro-stabilized EO/IR gimbals.

- **Protocol:** it implements the *Epsilon General Communication Protocol v4.0*.
- **Layout:** it follows *Epsilon Control 4.0.4.1*, as shown in the reference recording:
  - Left rail with mode and function buttons.
  - Live video with the gimbal's own OSD.
  - Geo map.
  - Vertical settings tabs.
  - MTI, Enhancement and Controls panels.
  - Status badges.
- **RTSP recast:** this is new. The camera video can be served or pushed over RTSP to any IP, without re-encoding. The original Recaster only supports loopback and UDP multicast.

```
EpsilonGCS/
├── build.bat                     ← one-step build → release\
├── EpsilonGCS.sln                (open in Visual Studio 2022)
├── src/
│   ├── Epsilon.Core/             protocol (CRC-8, packets, parser, all commands, status/version/error decoding),
│   │                             UDP + RS-232 transports, GimbalClient (50 ms pacing, rate-control keep-alive)
│   ├── Epsilon.Video/            UDP fan-out, RTSP restreamer (FFmpeg copy + MediaMTX)
│   ├── EpsilonGCS/               WPF application (UI, map, flyout pages, tool windows)
│   └── Epsilon.Simulator/        console gimbal simulator (+ optional test video)
├── tests/Epsilon.Core.Tests/     xUnit tests incl. the checksum examples from the protocol document
└── tools/get-tools.ps1           downloads ffmpeg.exe + mediamtx.exe (needed only for RTSP restream)
```

## 1. Build the release folder

You need:

- Windows 10 or 11 (x64).
- The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- Internet access during the first build, for NuGet packages and the RTSP tools.

To build:

1. Double-click **`build.bat`**.
2. It restores packages, runs the unit tests and publishes into `release\`:

```
release\
├── EpsilonGCS\EpsilonGCS.exe          ← the application (self-contained, copy this folder to any PC)
├── EpsilonGCS\tools\ffmpeg.exe, mediamtx.exe
├── Simulator\EpsilonSimulator.exe
└── run-simulator-with-video.bat
```

The published folder is self-contained, so the target PC does not need .NET installed.

**Visual Studio alternative:**

1. Open `EpsilonGCS.sln`.
2. Set **EpsilonGCS** as the startup project.
3. Press F5.
4. Run `tools\get-tools.ps1` once if you want RTSP.

## 2. Test on your desktop without a gimbal

1. Run `release\run-simulator-with-video.bat`. The simulator listens on UDP 4001. It sends a 1280×720 H.264 test pattern to `udp://127.0.0.1:15004`.
2. Start `release\EpsilonGCS\EpsilonGCS.exe`.
3. Open **Network** (right-hand tab) and connect to the simulator:
   1. Set **Gimbal IP** to `127.0.0.1`.
   2. Click **Connect**.
4. Check the expected results:
   - The status bar turns green (Connected, VP Init OK, GPS).
   - Video appears.
   - The aircraft orbits on the map.
   - Rail buttons go green as you use them: RATE/VHCL/SCN/GEO/STOW, STAB, REC, IR/EO, LSR, ...
   - Arrow keys or the Controls pad move the simulated gimbal.
   - **Track** + click on the video starts tracking at that pixel.
   - **Right-click on the map → Geo lock here** points the gimbal at that location.

## 3. Connect a real Epsilon

| Item | Default |
|---|---|
| Gimbal IP / input port | `192.168.1.197` : `4001` |
| GCS listen port (gimbal destination port) | `4002` |
| Serial | `COM3`, 115200 8N1 (baud rate is not given in the protocol document, so it is configurable) |
| Video from gimbal | UDP `15004`, MPEG-TS |

1. Give the PC an address in the gimbal subnet, for example `192.168.1.10/24`.
2. Under **Network**:
   1. Set the gimbal IP.
   2. Click **Connect**.
3. In **Network → Gimbal video output (0x95)**:
   1. Choose protocol **1 – H.264 MPEG2-TS** (or **8 – H.265 MPEG2-TS**).
   2. Enter this PC's IP and port `15004`.
   3. Press **Send to this PC**.
4. If the gimbal's destination IP for control replies is not this PC, set it in **Gimbal network settings (0x12)**.

## 4. RTSP recast to a specific IP

Open the **RTSP Restream** tab.

- **Serve** (default): this PC runs an RTSP server.
  1. Put the receiver's IP in **Allowed client IPs**.
  2. Optionally set a username and password.
  3. Press **Start**. The page shows the URL.
  4. The receiver opens `rtsp://<this-PC-IP>:8554/epsilon` (for example in VLC). Other IPs are refused.
- **Push:** this PC publishes to an RTSP server at the destination (for example MediaMTX on that machine).
  1. Set the destination URL, e.g. `rtsp://192.168.1.50:8554/epsilon`.
  2. Press **Start**.

How the restream works:

- Video is copied without re-encoding (`ffmpeg -c copy`), so it adds no quality loss and little latency.
- The gimbal's UDP stream is received once and fanned out locally (port 15010 for display, 15011 for the restreamer). Display and restream therefore run at the same time.
- **Start restream automatically** starts the restream with the application.
- Allow the RTSP port (8554 TCP) in Windows Firewall when Windows asks.

## 5. Video, map and simulator indicators

- **Bar under the video:**
  - The status line shows `Status: Running`, `Klv Packets <n>`, codec, bitrate and RTSP state.
  - The KLV track is found automatically from the MPEG-TS program table.
- **Video Player tab:** plays recordings copied from the gimbal SD card in the same video view. You can play, pause, stop, change speed and seek on the timeline.
  - **LIVE** returns to the live stream.
  - The live receiver and RTSP restream keep running while a recording plays.
- **Fullscreen video:** press **F11** or double-click the video (when Track is off).
- **Map toolbar** (same order as Epsilon Control):

  | Button | Action |
  |---|---|
  | Center / follow gimbal | Centers the map on the gimbal and follows it |
  | Center on target | Centers the map on the geo target |
  | Zoom in / out | Changes map zoom |
  | Layers | Street, Topographic or Satellite |
  | POI | Click to place pins |
  | Ruler | Measures distance and bearing |
  | Pan | Drags the map |

- **Map right-click menu:** geo lock, copy coordinates, center, center on home / set home, and clear track / POIs / ruler.
- **Home position:** the first gimbal position of the session is marked **H** on the map. Use "Set home here" to move it.
- **Controls panel:** the centre of the pad (or the **C** key) points the gimbal to its pilot-view direction. **LSR** is greyed out when the gimbal reports no laser pointer.
- **Track and scale:** the aircraft track is drawn as a trail. The scale bar shows nm and km.
- **Simulated data:** when connected to the bundled simulator, an orange **SIMULATOR - simulated data** badge appears in the status bar. The Telemetry tab also shows "Data source: SIMULATOR". This way simulated values are never mistaken for real gimbal data.

## 5a. Targets and splashes

Open the **Targets** tab (right-hand side, or Window → Targets & splashes). The panel opens on the left of the map.

- **Add a target:** right-click the map → **Add target here**, or press **+ Add target (camera geo point)** to use the point the camera is looking at.
- **Click a target on the map** to select it in the list and show its details (name, position, status, notes, range). Selecting a row in the list highlights and centres it on the map. **Geo lock** points the gimbal at the selected target.
- **Splash [+]:** records a splash at the camera's current GEO latitude / longitude (GEO_LATITUDE / GEO_LONGITUDE of the 0x80 status). It is refused, with the reason, when the gimbal has no fresh, valid geo solution. A splash is linked to the target selected at that moment.
- Targets are red bullseyes, splashes pink diamonds with a cross. Targets and splashes are kept in memory for the session.

## 6. Keyboard

| Key | Action |
|---|---|
| Arrow keys | Pan/tilt (or move the track box when NUDGE is on) |
| Z / X | Zoom out / in |
| F / G | Focus |
| Space | Track at the cross |
| Esc | RATE mode |
| 1–7 | RATE, AID, SCN, VHCL, GEO, PILOT, STOW |
| I | EO/IR |
| S | Snapshot |
| R | Record |
| N | NUC |
| C | Center gimbal (pilot view) |
| F11 | Fullscreen video |
| F5 | Re-synchronise live video |

**J.STICK** enables an Xbox-compatible gamepad.

## 7. Notes and known limitations

- **Checksum:** the protocol text calls the checksum "XOR". The table, pseudo code and examples are actually CRC-8 Dallas/Maxim, seeded with 0x01. That is what is implemented, and the unit tests use the document's example packets.
- **Not implemented:**
  - The *Artillery / Coordinate Measurement* fire-adjustment plugin (manual chapter 10).
  - Firmware upload and the bootloader.
  - The Elevation map button from Epsilon Control. It needs a terrain elevation (DEM) database, which is not available here.
  - UAV autopilot data such as battery, armed state, airspeed, waypoints and flight mode. The Epsilon protocol does not carry these values. They would need a separate autopilot link (for example MAVLink), so they are not shown rather than shown with invented values.
- **Map tiles:**
  - Tiles come from OpenStreetMap and are cached in `%LocalAppData%\EpsilonGCS\tiles`.
  - Areas you have viewed once also work offline.
  - For operational or high-volume use, point `TileUrl` in `settings.json` at your own tile server.
- **KLV:** the KLV track in RTSP is marked experimental. It depends on FFmpeg/receiver support.
- **File locations:**
  - Settings: `%AppData%\EpsilonGCS\settings.json`.
  - Log: `%LocalAppData%\EpsilonGCS\gcs.log` (Window → Log).
- **Build status:** this source was written without access to a Windows build machine. If `build.bat` reports a compile error, send the error text and it can be fixed quickly.

## 8. Architecture

```
Gimbal (UDP 4001 / RS-232)                Gimbal video (UDP 15004, MPEG-TS)
        |                                          |
 IGimbalTransport (UdpTransport, SerialTransport)  UdpFanout --> TsInspector (KLV counter, codec)
        |                                          |        \--> 127.0.0.1:15010 --> LibVLC (Video tab)
 GimbalClient  - 50 ms pacing, RATE_CONTROL keep-alive,      \--> 127.0.0.1:15011 --> FFmpeg --> MediaMTX (RTSP)
               - parser, link watchdog, LastStatus/Version (single telemetry state)
        |
 GimbalController - operator commands (modes, track-at-pixel, geo lock, center, motion,
                    zoom/focus, camera, REC/SNAP, laser, MTI, enhancement) + state queries
        |
 WPF UI (MainWindow, flyout pages, tool windows) - events marshalled to the UI thread
```

- **Network is off the UI thread.** All network I/O runs on background tasks. UI updates go through the dispatcher.
- **One telemetry state.** Every view reads the same `EPSILON_GLOBAL_STATUS`, held by `GimbalClient`.
- **No raw packets in the main window.** Operator buttons, keys and the gamepad call `GimbalController`. Settings pages send their configuration packets through the same controller, so a missing link is reported in one place.
- **Values are never invented.** The Epsilon protocol has no battery, armed state, flight mode or airspeed, so the GCS does not display them. Every value shown comes from the gimbal: its GPS/INS position, attitude, LOS, range, temperatures and GPS quality.
- **Simulated data is labelled.** The simulator reports a fixed unique ID. The GCS then shows an orange **SIMULATOR – simulated data** badge, so test data can't be mistaken for real telemetry.
