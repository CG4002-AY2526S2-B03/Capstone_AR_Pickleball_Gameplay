# AI Agent Reference — AR Pickleball Visualizer

> Detailed technical reference for AI coding agents working on this codebase. Covers every script, data flow, message schema, and integration point.

---

## 1. Project Overview

- **Engine**: Unity 6 (6000.4.0f1) with URP 17
- **AR**: AR Foundation 6.4.1 + ARKit 6.4.1 (iOS only)
- **Target**: iPhone (head-mounted in VR goggle holder)
- **Networking**: M2MQTT library over Wi-Fi to Mosquitto broker
- **Physics**: Unity PhysX at 50 Hz (FixedUpdate)
- **Scene**: `Assets/Scenes/MainScene.unity` (single scene)

---

## 2. Script Inventory

### Core Game Logic

| Script | Location | Attached To | Purpose |
|--------|----------|-------------|---------|
| `GameStateManager.cs` | Assets/Scripts/ | GameFlowManager | Rally state machine, scoring, match progression, 3 game modes |
| `MqttController.cs` | Assets/Scripts/ | MqttReceiver | Central MQTT routing, coordinate transforms, UWB drift correction |
| `MqttReceiver.cs` | Assets/Scripts/ | MqttReceiver | Low-level MQTT client (extends M2MqttUnityClient) |

### Paddle Control

| Script | Location | Attached To | Purpose |
|--------|----------|-------------|---------|
| `PaddleHitController.cs` | Assets/Scripts/ | PlayerPaddle | Physics paddle: mode selection, AprilTag/IMU fusion, hit impulse solver |
| `ImuPaddleController.cs` | Assets/Scripts/ | PlayerPaddle | IMU data processing, AprilTag-to-IMU alignment, velocity/rotation output |
| `ARTrackedPaddleMapper.cs` | Assets/Scripts/ | (not in scene) | AprilTag-to-paddle offset calibration (keyboard, editor only) |

### Ball Physics

| Script | Location | Attached To | Purpose |
|--------|----------|-------------|---------|
| `PracticeBallController.cs` | Assets/Scripts/ | Ball2 | Ball lifecycle: spawn, reset, boundary collision → scoring |
| `BallContactDetector.cs` | Assets/Scripts/ | Ball2 | Ball-side collision detection → forwards to PaddleHitController |
| `BallAerodynamics.cs` | Assets/Scripts/ | Ball2 | Quadratic drag + Magnus spin-lift forces |

### Bot Opponent

| Script | Location | Attached To | Purpose |
|--------|----------|-------------|---------|
| `BotHitController.cs` | Assets/Scripts/ | Bot | Bot movement, ML shot execution, God Mode 0.5× speed |
| `BotShotProfile.cs` | Assets/Scripts/ | Bot | Shot physics configs (hitForce, upForce, racquetOffset per type) |
| `BotTargeting.cs` | Assets/Scripts/ | (optional) | Auto-aim at ball (not critical path) |

### AR & Court Placement

| Script | Location | Attached To | Purpose |
|--------|----------|-------------|---------|
| `PlaceTrackedImages.cs` | Assets/ (root) | XR Origin | AprilTag detection → court placement + dual-sided racket spawning |
| `ARPlaneGameSpacePlacer.cs` | Assets/Scripts/ | XR Origin | Anchors GameSpaceRoot to AR plane + AprilTag pose |
| `CourtBoundarySetup.cs` | Assets/Scripts/ | GameFlowManager | Auto-generates net, kitchen, tags walls with CourtBoundary |
| `CourtBoundary.cs` | Assets/Scripts/ | (dynamic) | Enum tag: PlayerBackWall, BotBackWall, SideWall, Net, Kitchen |

### Shot Classification

| Script | Location | Attached To | Purpose |
|--------|----------|-------------|---------|
| `ShotClassifier.cs` | Assets/Scripts/ | (static) | Classifies hits by paddle speed + launch angle |
| `ShotType.cs` | Assets/Scripts/ | (enum) | {Drive=0, Drop=1, Dink=2, Lob=3, SpeedUp=4, HandBattle=5} |

### UI

| Script | Location | Attached To | Purpose |
|--------|----------|-------------|---------|
| `ScoreboardUI.cs` | Assets/Scripts/ | GameFlowManager | Score, state, mode display (world-space canvas) |
| `StereoscopicAR.cs` | Assets/Scripts/ | Main Camera | Stereo split for VR goggle mount |
| `PlayButtonUI.cs` | Assets/Scripts/ | GameFlowManager | Legacy no-op (auto-destroys) |
| `RecalibrateUI.cs` | Assets/Scripts/ | GameFlowManager | Legacy no-op (replaced by hardware buttons) |

### Data Transfer Objects

| Script | Location | Purpose |
|--------|----------|---------|
| `PaddlePayload.cs` | Assets/Scripts/ | DTOs: EulerAngles, Vec3Payload, ButtonState, PaddlePayload, Esp32Packet |

### Diagnostics (not in scene)

| Script | Location | Purpose |
|--------|----------|---------|
| `ARHitDiagnostics.cs` | Assets/Scripts/ | Runtime paddle↔ball distance logging, auto-fix |
| `ARPaddleCalibrationOverlay.cs` | Assets/Scripts/ | Editor-only GUI for paddle offset tuning |
| `POVStartRotationReset.cs` | Assets/Scripts/ | Lock camera pitch/roll (XR fallback) |

### Build

| Script | Location | Purpose |
|--------|----------|---------|
| `iOSPostBuild.cs` | Assets/Editor/ | Adds NSLocalNetworkUsageDescription for MQTT on iOS |

### Legacy (NOT used — candidates for deletion)

| Script | Location | Replaced By |
|--------|----------|-------------|
| `Ball.cs` | Assets/Physics/ | PracticeBallController |
| `Bot.cs` | Assets/Physics/ | BotHitController |
| `Player.cs` | Assets/Physics/ | PaddleHitController |
| `ShotManager.cs` | Assets/Physics/ | BotShotProfile + ShotClassifier |

---

## 3. MQTT Message Schemas

### `/paddle` (ESP32 → Unity)

**IMU packet:**
```json
{
  "type": "imu",
  "position": { "roll": 0.0, "pitch": 0.0, "yaw": 0.0 },
  "velocity": { "x": 0.0, "y": 0.0, "z": 0.0 }
}
```
Note: ESP32 field `position` maps to `PaddlePayload.orientation` (Euler angles). ESP32 field `velocity` maps to `PaddlePayload.linearVelocity`.

**Button packet:**
```json
{ "type": "button", "button": 1 }
```

### `/playerBall` (Unity → Ultra96)

Published by `MqttController.PublishPlayerBall()` after each player hit.
```json
{
  "position": { "x": 0.0, "y": 0.0, "z": 0.0 },
  "velocity": { "vx": 0.0, "vy": 0.0, "vz": 0.0 }
}
```
Coordinates are AI convention (y=depth, z=height) after y↔z swap from Unity.

### `/opponentBall` (Ultra96 → Unity)

Consumed by `MqttController.HandleOpponentBall()` → `BotHitController.SetMLPrediction()`.
```json
{
  "position": { "x": 0.0, "y": 0.0, "z": 0.0 },
  "velocity": { "vx": 0.0, "vy": 0.0, "vz": 0.0 },
  "returnSwingType": 0
}
```
`returnSwingType`: 0=Drive, 1=Drop, 2=Dink, 3=Lob, 4=SpeedUp, 5=HandBattle.

### `/playerPosition` (ESP32 → Unity, via UWB trilateration)

Published by the FireBeetle ESP32 after computing the player's 2D court position from 2× EWM550 UWB sensor distance readings (received over UART).

```json
{
  "clientID": "player1",
  "position": { "x": 3.2, "y": 1.5 }
}
```
x=lateral (metres from court centre), y=depth (metres from net, towards player baseline). Coordinate frame matches the court-floor plane; Unity maps this to `new Vector3(uwb.x, 0f, uwb.y)` (court-local XZ).

### Outbound calibration/feedback

| Topic | Payload | Trigger |
|-------|---------|---------|
| `/positionCalibration` | `{"isCalibrated":1}` | Button 2 |
| `/paddleCalibration` | `{"isCalibrated":1}` | Button 2 |
| `/hitAck` | `{"hit":true}` | Ball-paddle collision confirmed |

---

## 4. Coordinate Transformation Rules

All spatial data crossing the MQTT boundary must be transformed:

```csharp
// Unity → AI (PublishPlayerBall)
Vec3 aiPos = new Vec3 { x = unityLocal.x, y = unityLocal.z, z = unityLocal.y }; // y↔z

// AI → Unity (HandleOpponentBall)
Vector3 unityLocal = new Vector3(aiPos.x, aiPos.z, aiPos.y); // y↔z

// Court-local ↔ World
Vector3 world = gameSpaceRoot.TransformPoint(courtLocal);
Vector3 local = gameSpaceRoot.InverseTransformPoint(world);

// UWB (2D floor) → Unity court-local
Vector3 courtLocal = new Vector3(uwb.x, 0f, uwb.y); // UWB y=depth → Unity z
```

---

## 5. Dual-Sided Paddle AprilTag Tracking (PlaceTrackedImages)

The physical paddle has AprilTag codes on both faces. Both are registered in the AR Reference Image Library and share a single visual paddle instance (`Racket_PickleBall4` prefab):

| Reference Image Name | Texture File | Role |
|----------------------|-------------|------|
| `Racket_PickleBall4` | `racket_marker.png` | Front face (primary) |
| `Racket_Pickleball4_back` | `racket_marker_mirror.png` | Back face (horizontally mirrored) |

**Rotation correction**: When the back AprilTag is detected, ARKit reports a rotation that is 180° inverted around roll (Z) relative to the front. `PlaceTrackedImages` applies `BackFaceFlip = Quaternion.Euler(0, 0, 180)` to cancel this:

```csharp
// Front: trackedImage.rotation × prefabRotOffset
// Back:  trackedImage.rotation × Euler(0,0,180) × prefabRotOffset
```

This produces **identical visual paddle orientation** from either side. The flip-corrected rotation is what gets cached in `lastQrRotation` and fed to `UpdateWorldOffset()`, so the IMU-to-world mapping is consistent regardless of which face is visible.

**Spawn logic**: `SpawnPaddle()` always uses the front prefab (`Racket_PickleBall4`) from `ArPrefabs`, applies the appropriate rotation (with or without flip), instantiates it, and wires `PaddleHitController.qrTrackedRacket` to the instance. Only one paddle instance exists at any time.

---

## 6. Paddle Control Modes (PaddleHitController.FixedUpdate)

Priority order in code (first match wins):

```
1. Fresh AprilTag + IMU (qrAvailable && qrActivelyTracking && imu.IsActive)
   Position:  AprilTag tracked pose (flip-corrected if back AprilTag)
   Rotation:  AprilTag tracked pose (flip-corrected if back AprilTag)
   Velocity:  IMU (PaddleVelocity, PaddleAngularVelocity)
   Action:    auto-calibrate IMU offset via UpdateWorldOffset()

2. Stale AprilTag + IMU (qrAvailable && !qrActivelyTracking && imu.IsActive)
   Position:  stalePosition += v·dt + Cross(ω, forward×0.3)·dt
   Rotation:  imuToWorldOffset × calibratedIMU
   Velocity:  IMU
   Action:    integrate from last AprilTag pose, sync visible racket

3. IMU-only (imu.IsActive, no AprilTag ever detected)
   Position:  camera anchor + IMU displacement
   Rotation:  camera-relative IMU
   Velocity:  IMU

4. Fresh AprilTag only (qrAvailable && qrActivelyTracking, no IMU)
   Position:  AprilTag tracked pose
   Rotation:  AprilTag tracked pose
   Velocity:  finite difference (position − previousPosition) / dt

5. Stale AprilTag only (qrAvailable && !qrActivelyTracking, no IMU)
   Position:  frozen at last AprilTag position
   Rotation:  frozen at last AprilTag rotation
   Velocity:  zero

6. Camera fallback (nothing available)
   Position:  mouse/touch screen-point projected to world
   Rotation:  camera-derived
   Velocity:  finite difference
```

---

## 7. Hit Detection Pipeline

Five detection paths all converge on `PaddleHitController.ApplyHitImpulse()`:

1. **BallContactDetector.OnCollisionEnter** (ball-side, most reliable for kinematic paddle)
2. **PaddleHitController.OnCollisionEnter** (paddle-side, flips contact normal)
3. **BallContactDetector.FixedUpdate OverlapSphere** (fallback for missed collisions)
4. **PaddleHitController.TryProximityHit** (last-resort distance check, `proximityHitDistance` = 0.12 m)
5. **PaddleHitController.TryFlickAssist** (IMU-gated proximity assist, `flickRadius` = 0.2 m)

### Flick Assist (Path 5)

IMU-gated proximity hit assist that compensates for AR positional error. Active in all game modes when IMU is running.

**Trigger:** Ball within 0.2 m of paddle face + swing speed ≥ 0.5 m/s + ball on face side + cooldown elapsed.

**Direction:** Camera forward (horizontal) + `flickUplift` (0.25) — always sends ball toward the bot.

**Key difference from proximity hit:** Proximity (path 4) uses paddle-to-ball normal as impulse direction (accurate but can send ball backwards due to AR error). Flick overrides the direction to camera-forward, guaranteeing the ball heads toward the opponent.

**Ball search:** Uses `GetBallRigidbody()` helper — works independently of `enableProximityFallback` setting. Falls back to tag search then name search (throttled to 1/s).

**ApplyHitImpulse physics:**
- Coefficient of restitution model: `Δv_n = -(1+e)·vN·n` (e=0.86)
- Coulomb friction: `|Δv_t| ≤ μ·|Δv_n|` (μ=0.35)
- Paddle surface velocity includes angular contribution: `v_contact = v_paddle + ω × r`
- Spin from off-center contact + wrist snap
- Max ball speed cap: 22 m/s
- After impulse: classify shot → publish `/playerBall` + `/hitAck` → register with GameStateManager

---

## 8. Ball Physics Parameters

| Parameter | Value | Source |
|-----------|-------|--------|
| Mass | 0.0265 kg | SphereCollider on Ball2 |
| Radius | 0.037 m | SphereCollider |
| Drag coefficient | 0.040 | BallAerodynamics |
| Magnus coefficient | 0.00075 | BallAerodynamics |
| Paddle restitution | 0.86 | PaddleHitController |
| Friction coefficient | 0.35 | PaddleHitController |
| Max ball speed | 22 m/s | PaddleHitController |
| Hit cooldown | 0.15 s | PaddleHitController |
| Serve position (court-local) | (0.44, 0.50, 2.0) | PracticeBallController |

---

## 9. UWB Positioning & Drift Correction

### Hardware pipeline

2× EWM550 UWB sensors are connected to the FireBeetle ESP32 via UART. Each sensor measures time-of-flight distance to a fixed anchor at each end of the net. The ESP32 computes the player's 2D court position and publishes it on `/playerPosition`.

```
EWM550 × 2  ──UART──▶  ESP32 positioning  ──Wi-Fi──▶  /playerPosition  ──▶  MqttController
```

Anchors are placed at: Anchor A (net post left), Anchor B (net post right), co-located with the AprilTag code at the net centre.

### Unity drift correction (MqttController.Update)

Corrects `gameSpaceRoot.position` using UWB ground truth:

```
each frame:
  camCourtLocal = gameSpaceRoot.InverseTransformPoint(Camera.main.position)
  drift = (camCourtLocal.x - uwb.x, 0, camCourtLocal.z - uwb.z)

  if |drift| > 0.05m:
    driftWorld = gameSpaceRoot.TransformDirection(drift)
    driftWorld.y = 0  // never correct vertical
    correction = driftWorld × speed × dt  (clamped to 0.02m/frame)
    gameSpaceRoot.position += correction
```

Inspector parameters: `enableDriftCorrection`, `driftCorrectionSpeed` (0.3), `driftMinThreshold` (0.05m), `driftMaxStepPerFrame` (0.02m).

Falls back to camera X/Z projection when UWB times out (2s without packet).

---

## 10. God Mode Ball Speed Reduction

In `BotHitController.TryHit()`, after setting `ballRb.linearVelocity`:

```csharp
if (gameState.Mode == GameMode.GodMode)
    ballRb.linearVelocity *= godModeSpeedMultiplier; // 0.5f default
```

Direction preserved, magnitude halved. Only affects opponent→player returns. Player→opponent hits use full impulse physics (PaddleHitController is unmodified).

---

## 11. Key Dependencies Between Scripts

```
MqttReceiver
  └→ MqttController
       ├→ ImuPaddleController.SetPayload()
       ├→ BotHitController.SetMLPrediction()
       ├→ GameStateManager.StartOrTogglePause() / CycleMode() / ResetGameplay()
       ├→ ImuPaddleController.Calibrate()
       └→ PracticeBallController.DropBallInFrontOfCamera() / ResetBall()

PlaceTrackedImages
  ├→ ARPlaneGameSpacePlacer.PlaceAtAnchor()
  ├→ PaddleHitController.qrTrackedRacket = spawned prefab
  ├→ PaddleHitController.qrActivelyTracking (set per-frame from trackingState)
  └→ Dual AprilTag: front (Racket_PickleBall4) + back (Racket_Pickleball4_back, 180° Z flip)

PaddleHitController
  ├→ ImuPaddleController (reads PaddleVelocity, PaddleAngularVelocity, WorldRotation)
  ├→ ImuPaddleController.UpdateWorldOffset() (auto-calibrate while AprilTag active)
  ├→ MqttController.PublishPlayerBall() + PublishHitAcknowledge()
  ├→ GameStateManager.RegisterPlayerHit()
  ├→ ShotClassifier.Classify()
  ├→ TryFlickAssist() → ApplyHitImpulse() (IMU-gated proximity assist)
  └→ GetBallRigidbody() (shared ball search for proximity + flick)

BallContactDetector
  └→ PaddleHitController.ApplyHitImpulse()

PracticeBallController
  ├→ GameStateManager (boundary → scoring events)
  └→ FreezeInPlace() / ResetBall() (single authority for ball lifecycle)

BotHitController
  ├→ BotShotProfile.GetShotByType()
  └→ GameStateManager.RegisterBotHit()

GameStateManager
  ├→ PracticeBallController.ResetBall()
  ├→ PracticeBallController.FreezeInPlace()
  └→ PlaceTrackedImages.StartGame()
```

---

## 12. File Structure (Active Project)

```
Assets/
├── Scenes/MainScene.unity              — Single game scene
├── Scripts/                            — 26 active scripts (see inventory above)
├── PlaceTrackedImages.cs               — AprilTag tracking (root level, not in Scripts/)
├── Editor/iOSPostBuild.cs              — iOS build post-processor
├── M2Mqtt/                             — MQTT client library (do not modify)
├── M2MqttUnity/Scripts/                — Unity MQTT wrapper (M2MqttUnityClient.cs)
├── Pickle Ball Collection/             — 3D models (court, rackets, ball)
├── Materials/                          — Shader materials
├── Shaders/                            — Custom shaders
├── Settings/                           — URP, build profiles
├── Samples/XR Interaction Toolkit/     — XR starter assets
├── TextMesh Pro/                       — TMP resources
├── XR/                                 — XR loaders and settings
├── StreamingAssets/                     — MQTT TLS certs (unused, isEncrypted=0)
└── Physics/                            — Animations + legacy scripts (legacy .cs files unused)

Docs/
├── System_Architecture.md              — This project's current architecture (human-readable)
├── AI_Agent_Reference.md               — This file (detailed, for AI agents)
├── UML_Diagrams.md                     — Mermaid UML diagrams (updated)
├── B03_CG4002_Initial_Design_Report.*  — Original capstone design report
└── CG4002 hardware diagrams.pdf        — Hardware schematics and layout

ProjectSettings/                        — Unity project config (iOS target)
Packages/                               — UPM package manifest
```

---

## 13. Known Issues & Pending Work

### Completed
- [x] IMU-driven paddle when AprilTag lost (stale mode with v·dt + swing arc)
- [x] AprilTag-to-IMU auto-calibration (imuToWorldOffset learned per frame)
- [x] Manual IMU calibration (Button 2)
- [x] 3 game modes (Normal, Tutorial, GodMode)
- [x] GodMode: no scoring + 0.5× opponent ball speed
- [x] Visual paddle sync (Racket_Pickleball4 follows PlayerPaddle in all modes)
- [x] Dual-sided paddle AprilTag (front + mirrored back, 180° Z flip, shared paddle instance, IMU-compatible)
- [x] Button 2 full reset + calibrate (gameplay/ball/court/paddle AprilTag reset + IMU + UWB calibration)
- [x] Hardware button remapping (1=Start, 2=Reset+Calibrate, 3=Reset Ball, 4=Mode/Reset)
- [x] Button debounce removed (edge-triggered from ESP32)
- [x] Hit acknowledgment → /hitAck for haptic feedback
- [x] Calibration publishes to /positionCalibration + /paddleCalibration
- [x] MqttReceiver enabled in scene
- [x] Angular velocity from pure IMU deltas (no camera contamination)
- [x] UWB drift correction on GameSpaceRoot
- [x] ScoreboardUI mode-specific display

### Pending
- [ ] MQTT connection testing on hotspot network (was timing out from iPhone → Windows firewall)
- [ ] On-device testing of dual-sided paddle AprilTag (verify 180° Z flip produces correct orientation)
- [ ] On-device testing of IMU stale mode, UWB drift correction, God Mode
- [ ] ML velocity from AI model not used directly by BotHitController (velocity param discarded in SetMLPrediction — uses BotShotProfile instead)
- [ ] UWB coordinate alignment: physical UWB anchors must be calibrated to match AprilTag court origin
- [ ] Capacitive touch sensor integration for serve intent (ESP32 firmware sends it, Unity doesn't consume it yet)
- [ ] Sound effects (deferred per design report)
- [ ] Ball spin visualization (deferred per design report)
