# AR Pickleball — UML Diagrams

> Render with any Mermaid-compatible viewer (GitHub, VS Code extension, Mermaid Live Editor).

---

## 1. Class Diagram — Full System

```mermaid
classDiagram
    direction TB

    class MqttReceiver {
        +string brokerAddress
        +int brokerPort
        +bool isConnected
        +string LastConnectionError
        +Publish(topic, message) void
        +OnMessageArrived event
        +OnConnectionSucceeded event
        +ConnectionFailed event
    }

    class MqttController {
        +Transform gameSpaceRoot
        +ImuPaddleController imuPaddleController
        +BotHitController botHitController
        +GameStateManager gameState
        +Transform playerMarker
        +bool enableDriftCorrection
        +float driftCorrectionSpeed
        +bool IsConnected
        +PublishPlayerBall(worldPos, worldVel) void
        +PublishCalibration() void
        +PublishHitAcknowledge() void
        -HandleOpponentBall(json) void
        -HandlePaddle(json) void
        -HandlePlayerPosition(json) void
        -HandleButtonPacket(raw) void
    }

    class ImuPaddleController {
        +bool IsActive
        +bool IsCalibrated
        +bool HasWorldOffset
        +Vector3 PaddleVelocity
        +Vector3 PaddleAngularVelocity
        +Quaternion WorldRotation
        +Quaternion SmoothedRotation
        +bool ControlsTransform
        +Vector3 eulerSign
        +Vector3 linearVelocitySign
        +SetPayload(PaddlePayload) void
        +Calibrate() void
        +UpdateWorldOffset(qrWorldRotation) void
    }

    class PaddleHitController {
        +Transform qrTrackedRacket
        +Quaternion qrPrefabRotOffset
        +bool qrActivelyTracking
        +float restitution
        +float frictionCoefficient
        +float maxBallSpeed
        +float spinFromOffCenter
        +float spinFromTangential
        +bool enableProximityFallback
        +float imuToFaceDistance
        +ApplyHitImpulse(ballRb, contactPt, normal) void
        +ClearCachedBall() void
        -Vector3 stalePosition
        -Quaternion staleRotation
    }

    class GameStateManager {
        +GameMode Mode
        +RallyState State
        +int PlayerScore
        +int BotScore
        +int PlayerSets
        +int BotSets
        +bool IsStarted
        +bool IsPaused
        +StartOrTogglePause() void
        +CycleMode() void
        +ResetGameplay() void
        +RegisterPlayerHit(shotType) void
        +RegisterBotHit(shotType) void
        +OnBallOutPlayerSide() void
        +OnBallOutBotSide() void
        +OnDoubleBounce(ballLocalZ) void
        +OnKitchenViolation() void
    }

    class PlaceTrackedImages {
        +GameObject[] ArPrefabs
        +ARPlaneGameSpacePlacer gamePlacer
        +string courtAnchorImageName
        +StartGame() void
        +ResetRacket() void
        +ResetCourt() void
    }

    class ARPlaneGameSpacePlacer {
        +float playerHeight
        +PlaceAtAnchor(Pose) void
        +AllowPlacement() void
        +ResetPlacement() void
    }

    class BotHitController {
        +Transform ball
        +Transform[] targets
        +float moveSpeed
        +bool useMLPredictions
        +GameStateManager gameState
        +float godModeSpeedMultiplier
        +SetMLPrediction(pos, vel, swingType) void
    }

    class BotShotProfile {
        +ShotConfig[] shots
        +GetShotByType(type) ShotConfig
    }

    class PracticeBallController {
        +Vector3 courtServeLocalPos
        +GameStateManager gameState
        +ResetBall() void
        +ResetBounceCount() void
        +DropBallInFrontOfCamera() void
    }

    class BallContactDetector {
        +PaddleHitController paddle
        +bool enableOverlapFallback
        +float overlapRadius
    }

    class DeadHangBall {
        +bool IsFrozen
        +float detectionRadius
        +Freeze() void
        +Release() void
    }

    class BallAerodynamics {
        +float dragCoefficient
        +float magnusCoefficient
    }

    class ScoreboardUI {
        +GameStateManager gameState
    }

    class StereoscopicAR {
        +bool stereoEnabled
        +float ipd
        +SetupWorldSpaceCanvas()$ void
    }

    class ShotClassifier {
        +Classify(speed, velocity)$ ShotType
    }

    class CourtBoundarySetup {
        +Transform gameSpaceRoot
    }

    %% Relationships
    MqttReceiver <-- MqttController : _eventSender
    MqttController --> ImuPaddleController : SetPayload / Calibrate
    MqttController --> BotHitController : SetMLPrediction
    MqttController --> GameStateManager : Start / Mode / Reset
    MqttController --> PracticeBallController : Reset / Drop

    PlaceTrackedImages --> ARPlaneGameSpacePlacer : PlaceAtAnchor
    PlaceTrackedImages --> PaddleHitController : wires qrTrackedRacket

    PaddleHitController --> ImuPaddleController : reads velocity / rotation
    PaddleHitController --> MqttController : PublishPlayerBall + PublishHitAck
    PaddleHitController --> GameStateManager : RegisterPlayerHit
    PaddleHitController --> DeadHangBall : Release on hit
    PaddleHitController --> ShotClassifier : Classify

    BallContactDetector --> PaddleHitController : ApplyHitImpulse

    PracticeBallController --> GameStateManager : boundary events
    PracticeBallController --> DeadHangBall : Freeze

    BotHitController --> BotShotProfile : GetShotByType
    BotHitController --> GameStateManager : RegisterBotHit

    ScoreboardUI --> GameStateManager : listens to events
    ScoreboardUI --> StereoscopicAR : SetupWorldSpaceCanvas
```

---

## 2. State Machine — Game Flow

```mermaid
stateDiagram-v2
    [*] --> PreGame : App Launch

    PreGame --> WaitingToServe : Button 1 (Start)\nimage tracking unlocked

    state GameModeChoice <<choice>>
    PreGame --> GameModeChoice : Button 4
    GameModeChoice --> PreGame : Cycle: Normal → Tutorial → GodMode → Normal

    WaitingToServe --> InPlay : Player hits ball\n(RegisterPlayerHit)

    InPlay --> PointScored : Boundary hit\nDouble bounce\nNet fault\nKitchen violation

    state ModeCheck <<choice>>
    PointScored --> ModeCheck : Timer expires
    ModeCheck --> WaitingToServe : Tutorial/GodMode\n(no scoring, reset ball)
    ModeCheck --> WaitingToServe : Normal\n(scores updated, check set)
    ModeCheck --> MatchOver : Normal\n(match won)

    WaitingToServe --> PreGame : Button 4 (Full Reset)
    InPlay --> PreGame : Button 4 (Full Reset)

    MatchOver --> PreGame : Button 4 (Full Reset)
```

---

## 3. State Machine — Paddle Control Modes

```mermaid
stateDiagram-v2
    [*] --> CameraFallback : No sensors

    CameraFallback --> IMUOnly : First /paddle IMU payload
    CameraFallback --> FreshQR : QR detected (no IMU)

    IMUOnly --> FreshQR_IMU : QR detected

    FreshQR --> FreshQR_IMU : IMU activated

    FreshQR_IMU --> StaleQR_IMU : QR tracking lost\n(paddle rotated away)

    StaleQR_IMU --> FreshQR_IMU : QR restored\n(snap back, reset stalePos)

    FreshQR --> StaleQR : QR lost (no IMU)
    StaleQR --> FreshQR : QR restored

    note right of FreshQR_IMU
        Position = QR pose
        Rotation = QR pose
        Velocity = IMU
        Auto-calibrates imuToWorldOffset each frame
    end note

    note right of StaleQR_IMU
        Position = lastQR + Σ(v·dt + swing arc)
        Rotation = imuToWorldOffset × calibratedIMU
        Velocity = IMU
    end note
```

---

## 4. State Machine — Ball Physics

```mermaid
stateDiagram-v2
    direction LR

    [*] --> Frozen : Awake → FreezeAll, no gravity

    Frozen --> Active : Paddle overlap (DeadHangBall)\nOR ApplyHitImpulse → Release

    Active --> Frozen : Wall hit → ResetBall\nOR Button 3 → ResetBall\nOR Point scored → Freeze

    Active --> Active : Normal physics\nDrag + Magnus\nBot hits\nBounces

    note right of Frozen
        Dynamic Rigidbody
        constraints = FreezeAll
        useGravity = false
        OverlapSphere checks for paddle
    end note

    note right of Active
        constraints = None
        useGravity = true
        ContinuousDynamic collision
    end note
```

---

## 5. Sequence Diagram — Full Rally Cycle

```mermaid
sequenceDiagram
    participant ESP as ESP32
    participant MQTT as MqttController
    participant IMU as ImuPaddleController
    participant PAD as PaddleHitController
    participant BALL as PracticeBallController
    participant DH as DeadHangBall
    participant BCD as BallContactDetector
    participant BOT as BotHitController
    participant AI as Ultra96
    participant GS as GameStateManager

    Note over ESP,GS: PHASE 1 — IMU STREAMING
    ESP->>MQTT: /paddle {"type":"imu",...}
    MQTT->>IMU: SetPayload()
    IMU->>PAD: PaddleVelocity, WorldRotation

    Note over ESP,GS: PHASE 2 — SERVE (ball frozen)
    DH->>DH: FixedUpdate → OverlapSphere
    DH->>DH: Paddle detected → Release()
    DH-->>BALL: constraints = None, gravity on

    Note over ESP,GS: PHASE 3 — PLAYER HIT
    BCD->>PAD: OnCollisionEnter → ApplyHitImpulse()
    PAD->>PAD: COR impulse + friction + spin
    PAD->>GS: RegisterPlayerHit(shotType)
    PAD->>MQTT: PublishPlayerBall(pos, vel)
    PAD->>MQTT: PublishHitAcknowledge()
    MQTT->>ESP: /hitAck → vibration motor

    Note over ESP,GS: PHASE 4 — AI RESPONSE
    MQTT->>AI: /playerBall {position, velocity}
    AI->>MQTT: /opponentBall {position, velocity, returnSwingType}
    MQTT->>BOT: SetMLPrediction(worldPos, worldVel, swingType)

    Note over ESP,GS: PHASE 5 — BOT HIT
    BOT->>BOT: TryHit → ball enters trigger
    BOT->>BOT: Apply shot profile velocity
    alt GodMode
        BOT->>BOT: velocity *= 0.5
    end
    BOT->>GS: RegisterBotHit(shotType)

    Note over ESP,GS: PHASE 6 — RALLY CONTINUES OR POINT
    alt Ball hits boundary
        BALL->>GS: OnBallOutPlayerSide / OnBallOutBotSide / OnBallHitNet
        GS->>GS: AwardPoint()
        GS->>DH: Freeze()
        GS-->>BALL: ResetBall() after timer
    else Ball returns to player
        Note over PAD: Player hits again → back to Phase 3
    end
```

---

## 6. Sequence Diagram — IMU Calibration

```mermaid
sequenceDiagram
    participant Player
    participant ESP as ESP32
    participant MQTT as MqttController
    participant IMU as ImuPaddleController

    Player->>Player: Hold paddle horizontal
    Player->>ESP: Press Button 2
    ESP->>MQTT: /paddle {"type":"button","button":2}
    MQTT->>IMU: Calibrate()
    IMU->>IMU: calibrationOffset = Inverse(rawIMU)
    IMU->>IMU: Reset angular velocity state
    MQTT->>MQTT: PublishCalibration()
    MQTT->>ESP: /positionCalibration {"isCalibrated":1}
    MQTT->>ESP: /paddleCalibration {"isCalibrated":1}

    Note over IMU: While QR is active (every frame):
    loop QR Active
        IMU->>IMU: UpdateWorldOffset(qrWorldRotation)
        IMU->>IMU: imuToWorldOffset = qrRot × Inv(calibratedIMU)
    end

    Note over IMU: When QR lost:
    IMU->>IMU: imuToWorldOffset frozen
    IMU->>IMU: WorldRotation = imuToWorldOffset × calibratedIMU
```

---

## 7. Component Diagram — Ball GameObject (Ball2)

```mermaid
classDiagram
    direction LR

    class Ball2 {
        <<GameObject>>
        Tag: Ball
        Parent: GameSpaceRoot
    }

    class Rigidbody {
        mass: 0.0265 kg
        useGravity: variable
        isKinematic: false
        interpolation: Interpolate
        collisionDetection: ContinuousDynamic
    }

    class SphereCollider {
        radius: 0.037 m
        isTrigger: false
    }

    class PracticeBallController {
        courtServeLocalPos: (0.44, 0.50, 2.0)
        ResetBall()
        DropBallInFrontOfCamera()
    }

    class DeadHangBall {
        detectionRadius: 0.12 m
        Freeze() / Release()
    }

    class BallContactDetector {
        overlapRadius: 0.10 m
        OnCollisionEnter → ApplyHitImpulse
    }

    class BallAerodynamics {
        drag: 0.040
        magnus: 0.00075
    }

    Ball2 --> Rigidbody
    Ball2 --> SphereCollider
    Ball2 --> PracticeBallController
    Ball2 --> DeadHangBall
    Ball2 --> BallContactDetector
    Ball2 --> BallAerodynamics
```

---

## 8. Component Diagram — PlayerPaddle

```mermaid
classDiagram
    direction LR

    class PlayerPaddle {
        <<GameObject>>
        Parent: scene root (not in GameSpaceRoot)
    }

    class Rigidbody {
        isKinematic: true
        useGravity: false
        interpolation: Interpolate
        collisionDetection: ContinuousSpeculative
    }

    class BoxCollider {
        size: (0.22, 0.26, 0.02)
        isTrigger: false
    }

    class PaddleHitController {
        restitution: 0.86
        friction: 0.35
        maxBallSpeed: 22 m/s
        6 control modes
        ApplyHitImpulse()
    }

    class ImuPaddleController {
        eulerSign: (1, 1, -1)
        linearVelocitySign: (1, 1, -1)
        Calibrate()
        UpdateWorldOffset()
    }

    PlayerPaddle --> Rigidbody
    PlayerPaddle --> BoxCollider
    PlayerPaddle --> PaddleHitController
    PlayerPaddle --> ImuPaddleController
```

---

## 9. Deployment Diagram — Multi-Device Architecture

```mermaid
flowchart TB
    subgraph ESP32["FireBeetle ESP32 (on paddle)"]
        IMU1["IMU 1 (handle)"]
        IMU2["IMU 2 (face edge)"]
        BUTTONS["4× Buttons"]
        TOUCH["Touch Sensor"]
        MOTOR["Vibration Motor"]
    end

    subgraph UWB["UWB System"]
        ANCHOR_A["UWB Anchor A\n(net post left)"]
        ANCHOR_B["UWB Anchor B\n(net post right)"]
        TAG["UWB Tag\n(player headset)"]
    end

    subgraph Broker["Windows Laptop"]
        MOSQUITTO["Mosquitto MQTT Broker"]
    end

    subgraph Ultra96["Ultra96 FPGA"]
        NN["Neural Network\n(shot prediction)"]
    end

    subgraph iPhone["iPhone (Unity AR)"]
        ARKit["ARKit"]
        GAME["Game Engine"]
        PHYSICS["PhysX"]
    end

    subgraph Court["Physical Court"]
        QR["QR Code\n(net center)"]
    end

    ESP32 -->|"Wi-Fi\n/paddle"| MOSQUITTO
    MOSQUITTO -->|"/paddle"| iPhone
    iPhone -->|"/playerBall"| MOSQUITTO
    MOSQUITTO -->|"/playerBall"| Ultra96
    Ultra96 -->|"/opponentBall"| MOSQUITTO
    MOSQUITTO -->|"/opponentBall"| iPhone
    iPhone -->|"/hitAck"| MOSQUITTO
    MOSQUITTO -->|"/hitAck"| ESP32
    iPhone -->|"/positionCalibration\n/paddleCalibration"| MOSQUITTO
    MOSQUITTO -->|"calibration"| ESP32

    TAG -->|"UWB ranging"| ANCHOR_A
    TAG -->|"UWB ranging"| ANCHOR_B
    TAG -->|"/playerPosition"| MOSQUITTO
    MOSQUITTO -->|"/playerPosition"| iPhone

    QR -.->|"ARKit image tracking"| iPhone

    Ultra96 -->|"SSH tunnel"| Broker
```

---

## 10. Activity Diagram — Hit Detection Pipeline

```mermaid
flowchart TD
    A[Ball approaches paddle] --> B{Ball frozen?}

    B -->|Yes| C[DeadHangBall OverlapSphere]
    C -->|Paddle nearby| D[Release → constraints None]
    D --> E[Ball dynamic]
    C -->|No paddle| C

    B -->|No| E

    E --> F{Collision detection}

    F -->|Path 1| G["BallContactDetector\nOnCollisionEnter"]
    F -->|Path 2| H["PaddleHitController\nOnCollisionEnter"]
    F -->|Path 3| I["BallContactDetector\nOverlapSphere fallback"]
    F -->|Path 4| J["PaddleHitController\nProximity fallback"]

    G --> K[ApplyHitImpulse]
    H --> K
    I --> K
    J --> K

    K --> L{Cooldown check}
    L -->|Too soon| M[Skip]
    L -->|OK| N{Kitchen check}
    N -->|Violation| O[Kitchen fault → Bot point]
    N -->|OK| P[Sanitize normal]
    P --> Q["v_contact = v_paddle + ω × r"]
    Q --> R["Normal: Δv = -(1+e)·vN·n"]
    R --> S["Tangential: Coulomb friction"]
    S --> T[Apply velocity + spin]
    T --> U[Classify shot type]
    U --> V["Publish /playerBall + /hitAck"]
    V --> W[RegisterPlayerHit]
```
