# Capstone AR Pickleball Gameplay

Unity iOS AR visualizer for a hardware-integrated pickleball gameplay system. The phone is the real-time visualizer, physics engine, rule engine, MQTT client, UWB drift-correction consumer, and AR opponent renderer.

The current gameplay frame is net-origin based: the physical QR code at the net defines `GameSpaceRoot` local `(0, 0, 0)`. Unity uses that frame for court rendering, ball physics, bounce/scoring decisions, UWB correction, and bot placement. The legacy Ultra96 AI wire format is still supported through `MqttController.aiDepthOffset`, which is applied only at the `/playerBall` and `/opponentBall` MQTT boundary.

## Project Stack

| Area | Technology |
|------|------------|
| Engine | Unity 6 `6000.4.0f1` |
| Render pipeline | Universal Render Pipeline `17.4.0` |
| AR | AR Foundation `6.4.1`, ARKit `6.4.1` |
| Target device | iPhone, intended for head-mounted phone holder |
| Networking | M2Mqtt/M2MqttUnity over Wi-Fi to Mosquitto |
| JSON | `com.unity.nuget.newtonsoft-json` |
| Physics | Unity PhysX, ball drag, Magnus lift, paddle impulse model |
| Main scene | `Assets/Scenes/MainScene.unity` |

## System Overview

The system has four main runtime nodes:

| Node | Role | Main link |
|------|------|-----------|
| iPhone Unity app | AR camera feed, court visualization, ball physics, rules, HUD, MQTT routing | Wi-Fi to MQTT broker |
| FireBeetle ESP32 paddle unit | IMU, hardware buttons, UWB data path, haptic motor | MQTT |
| Laptop | Mosquitto broker and optional TCP-to-MQTT relay | LAN/hotspot |
| Ultra96 FPGA | AI shot prediction for bot return | TCP relay or MQTT path |

High-level data flow:

```text
FireBeetle IMU/buttons ----\
UWB player position --------> Laptop MQTT broker ---> iPhone MqttController ---> Unity gameplay modules
Ultra96 /opponentBall -----/             ^                    |
                                         |                    v
                              iPhone publishes /playerBall and /fpgaTime
```

## Important Runtime Modules

| Module | Main scripts | Responsibility |
|--------|--------------|----------------|
| MQTT routing | `MqttReceiver.cs`, `MqttController.cs` | Subscribes/publishes topics, routes packets, applies Unity/AI coordinate conversion |
| Court placement | `PlaceTrackedImages.cs`, `ARPlaneGameSpacePlacer.cs`, `CourtBoundarySetup.cs` | QR/net anchoring, floor-plane placement, court boundaries |
| Paddle tracking | `ImuPaddleController.cs`, `PaddleHitController.cs` | QR/IMU fusion, hit detection, impulse/spin calculation |
| Ball simulation | `PracticeBallController.cs`, `BallContactDetector.cs`, `BallAerodynamics.cs` | Ball reset, drag, Magnus lift, bounce/out/double-bounce handling |
| Bot opponent | `BotHitController.cs`, `BotShotProfile.cs` | Converts `/opponentBall` prediction into AR bot movement and return shot |
| Game state/HUD | `GameStateManager.cs`, `ScoreboardUI.cs`, `StereoscopicAR.cs` | Rally state, scoring, mode display, headset HUD |

## Setup

1. Open this project with Unity `6000.4.0f1`.
2. Open `Assets/Scenes/MainScene.unity`.
3. Ensure the iPhone and laptop broker are on the same network.
4. Run Mosquitto on the laptop.
5. Set the `MqttReceiver` broker address in the scene to the laptop IP.
6. Place the court QR at the physical net center.
7. Place UWB anchors at the net line if UWB correction is being used.
8. Build to iOS and run on device.

The iOS post-build script at `Assets/Editor/iOSPostBuild.cs` adds local-network permission text required for MQTT access on iOS.

## Build Notes

Use an iPhone for ARKit testing. Editor play mode is useful for script checks and offline fallback testing, but it does not validate real AR camera tracking, iOS local-network permissions, or physical QR/UWB alignment.

Recommended Unity build target:

```text
File > Build Settings > iOS > Switch Platform
```

Then build the Xcode project and deploy to the iPhone through Xcode.

## Physical Startup Flow

1. Start the laptop MQTT broker.
2. Start the Ultra96 relay or direct AI MQTT path if AI predictions are required.
3. Power the FireBeetle paddle unit.
4. Launch the iPhone app.
5. Scan the court QR at the physical net.
6. Use hardware Button 1 to start or continue gameplay.
7. Use Button 3 to reset/release the ball for manual serve without requiring a fresh court respawn.

## Hardware Button Mapping

| Button | Action |
|--------|--------|
| 1 | Start, pause, resume, or continue from pre-play |
| 2 | Full reset and calibration flow |
| 3 | Reset/release ball for manual serve |
| 4 | Cycle mode before game start, or full reset during gameplay |

## MQTT Topics

| Topic | Direction | Payload summary | Purpose |
|-------|-----------|-----------------|---------|
| `/paddle` | ESP32 to Unity | IMU packet or button packet | Paddle orientation, velocity, hardware control |
| `/playerPosition` | UWB/ESP32 to Unity | `{"clientID","position":{"x","y"}}` | Player court position for marker and drift correction |
| `/playerBall` | Unity to Ultra96 | `{"position":{"x","y","z"},"velocity":{"vx","vy","vz"}}` | Ball state after player hit |
| `/opponentBall` | Ultra96 to Unity | position, velocity, `returnSwingType` | AI-predicted bot return |
| `/hitAck` | Unity to ESP32 | `{"hit":true}` | Paddle haptic feedback |
| `/positionCalibration` | Unity to ESP32 | `{"isCalibrated":1}` | UWB calibration acknowledgement |
| `/paddleCalibration` | Unity to ESP32 | `{"isCalibrated":1}` | IMU paddle calibration acknowledgement |
| `/fpgaTime` | Unity to broker | sequence, send time, receive time, latency | Measured latency from `/playerBall` send to `/opponentBall` receive |
| `status/u96` | Ultra96 to broker | `"READY"` | Ultra96 readiness status |
| `system/signal` | broker to Ultra96 | `"START"` | AI/game start signal |

Watch MQTT traffic from the laptop:

```bash
mosquitto_sub -h <broker-ip> -t '#' -v
```

Watch only FPGA/AI latency:

```bash
mosquitto_sub -h <broker-ip> -t /fpgaTime -v
```

Example `/fpgaTime` payload:

```json
{
  "sequence": 3,
  "requestTopic": "/playerBall",
  "responseTopic": "/opponentBall",
  "sentUtcMs": 1713250000000,
  "receivedUtcMs": 1713250000087,
  "latencyMs": 87.4,
  "sentUnityRealtimeSeconds": 123.45,
  "receivedUnityRealtimeSeconds": 123.537,
  "pendingAfterReceive": 0
}
```

`/fpgaTime` currently matches requests and responses in FIFO order because `/playerBall` and `/opponentBall` do not share a request id. For strict matching under duplicate or out-of-order replies, the relay or Ultra96 should echo a `sequence` field back in `/opponentBall`.

## Coordinate Systems

Unity court-local frame:

```text
x = right across the court
y = up
z = forward along the court
net/QR = local (0, 0, 0)
player side = z < 0
bot side = z > 0
```

AI/MQTT spatial frame:

```text
x = right
y = depth
z = height
```

`MqttController` performs the Unity-to-AI conversion at the MQTT boundary:

```text
Unity local position  (x, y, z) -> AI position       (x, z + aiDepthOffset, y)
Unity local velocity  (x, y, z) -> AI velocity       (vx=x, vy=z, vz=y)
AI position           (x, y, z) -> Unity local pos   (x, z, y - aiDepthOffset)
AI velocity           (x, y, z) -> Unity local vel   (x, z, y)
```

Default `aiDepthOffset` is `5.4 m` so the Unity net-origin gameplay frame can interoperate with the older Ultra96 training/wire convention.

## Gameplay Rules

The phone owns the live rally state. `PracticeBallController` and `GameStateManager` handle ball reset, serving state, boundaries, net/contact events, out calls, and scoring. Same-side double bounce is treated as a point event: after two accepted floor bounces on one side, the rally ends and the point is awarded.

Game modes:

| Mode | Scoring | Purpose |
|------|---------|---------|
| Normal | Full scoring | Standard gameplay with AI opponent |
| Tutorial | No scoring | Guided practice and calibration-friendly flow |
| God Mode | No scoring | Slower/easier bot returns for testing |

## Diagnostics

Useful runtime diagnostics:

| Diagnostic | Where |
|------------|-------|
| MQTT connection, latest packets, `/fpgaTime` latency | TMP debug text in scene |
| Latency from player hit to AI return | `/fpgaTime` topic |
| Bounce decisions | `/ballBounceDebug` topic |
| Bot movement target | `/botReposition` topic |
| AI/fallback hit events | `/aiHit`, `/fallbackHit` |
| Unity logs | Xcode device console or Unity editor console |

## Documentation

Detailed project documentation is in `Docs/`:

| File | Purpose |
|------|---------|
| `Docs/System_Architecture.md` | Integrated system architecture, MQTT topics, communication modes |
| `Docs/UML_Diagrams.md` | Mermaid UML diagrams and data-flow diagrams |
| `Docs/AI_Agent_Reference.md` | Detailed codebase reference for implementation work |

## Known Verification Limits

This repository does not include a local command-line Unity build pipeline. Validate final behavior through Unity script reload and iOS play testing. In this environment, standalone C# compilation is not available unless `dotnet`, `mcs`, or `csc` is installed.
