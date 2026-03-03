# Capstone AR Pickleball — UML Diagrams

> Render these with any Mermaid-compatible viewer (GitHub, Mermaid Live Editor, VS Code extension).

---

## 1. Class Diagram — Full System

```mermaid
classDiagram
    direction TB

    class PlaceTrackedImages {
        -ARTrackedImageManager _trackedImagesManager
        -Dictionary~string,GameObject~ _instantiatedPrefabs
        -bool _courtPlaced
        -bool _gameStarted
        +GameObject[] ArPrefabs
        +ARPlaneGameSpacePlacer gamePlacer
        +string courtAnchorImageName
        +StartGame() void
        +ResetRacket() void
        +ResetCourt() void
        -OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs) void
        -ProcessTrackedImages(IReadOnlyList~ARTrackedImage~) void
    }

    class ARPlaneGameSpacePlacer {
        -Transform gameSpaceRoot
        -ARPlaneManager planeManager
        -ARRaycastManager raycastManager
        -Camera arCamera
        -ARAnchorManager _anchorManager
        -GameObject _anchorGO
        -bool isPlaced
        -bool isAllowed
        -float _floorY
        -bool _hasFloorY
        -Pose? pendingPlanePose
        +bool hideGameSpaceUntilPlaced
        +float playerHeight
        +PlaceAtAnchor(Pose) void
        +AllowPlacement() void
        +ResetPlacement() void
        -PlaceGameSpace(Vector3, Quaternion) void
        -FinalizePlace(Vector3, Quaternion) void
        -GetClosestHorizontalPlane(Vector3) ARPlane
        -CreateFreeAnchor(Pose) void
        -DestroyAnchor() void
    }

    class PlayButtonUI {
        -ARPlaneGameSpacePlacer gamePlacer
        -GameObject canvasGO
        +string buttonText
        +int fontSize
        -CreateUI() void
        -OnPlayPressed() void
    }

    class PaddleHitController {
        +Transform cameraTransform
        +Transform qrTrackedRacket
        +float restitution
        +float frictionCoefficient
        +float maxBallSpeed
        +float spinFromOffCenter
        +float spinFromTangential
        +float hitCooldown
        +Rigidbody trackedBall
        +bool enableProximityFallback
        +float proximityHitDistance
        +bool useMouseInEditor
        -Rigidbody paddleRigidbody
        -Collider[] paddleColliders
        -Vector3 paddleVelocity
        -Vector3 paddleAngularVelocity
        -float lastHitTime
        +ApplyHitImpulse(Rigidbody, Vector3, Vector3) void
        -FixedUpdate() void
        -TryProximityHit() void
        -HandlePaddleCollision(Collision) void
        -GetTargetWorldPosition() Vector3
        -GetTargetWorldRotation(Vector3) Quaternion
    }

    class BallContactDetector {
        +PaddleHitController paddle
        +bool enableOverlapFallback
        +float overlapRadius
        -Rigidbody ballRigidbody
        -Collider[] paddleColliders
        -OnCollisionEnter(Collision) void
        -OnCollisionStay(Collision) void
        -HandleCollision(Collision) void
        -FixedUpdate() void
        -IsPaddleCollider(Collider) bool
    }

    class DeadHangBall {
        +PaddleHitController paddle
        +float detectionRadius
        +bool IsFrozen
        -Rigidbody rb
        -Collider[] paddleColliders
        +Freeze() void
        +Release() void
        -FixedUpdate() void
        -CachePaddleColliders() void
        -IsPaddleCollider(Collider) bool
    }

    class PracticeBallController {
        +Transform servePoint
        +Vector3 courtServeLocalPos
        +bool createGroundPlane
        +KeyCode resetKey
        +bool IsFrozen
        -Rigidbody ballRigidbody
        -Vector3 initialLocalPosition
        -Transform gameSpaceRoot
        -DeadHangBall deadHang
        +ResetBall() void
        +EnableGravity() void
        -PlaceAtServePosition() void
        -EnsureGroundCollider() void
        -OnCollisionEnter(Collision) void
    }

    class BallAerodynamics {
        +float ballMass
        +float dragCoefficient
        +float magnusCoefficient
        +float maxAngularSpeed
        -Rigidbody ballRigidbody
        -FixedUpdate() void
    }

    class BotHitController {
        +Transform ball
        +Transform[] targets
        +float moveSpeed
        +float hitCooldown
        -BotShotProfile shotProfile
        -Animator animator
        -Vector3 startPosition
        -float lastHitTime
        -TrackBall() void
        -TryHit(Collider) void
        -PickTarget() Vector3
        -PickShot() ShotConfig
        -PlayHitAnimation() void
    }

    class BotShotProfile {
        +ShotConfig topSpin
        +ShotConfig flat
    }

    class ShotConfig {
        <<struct>>
        +float upForce
        +float hitForce
    }

    class BotTargeting {
        +Transform ball
        +Transform aimTarget
        +Transform[] targets
        +bool autoAimAtBall
        +float turnSpeed
    }

    class RecalibrateUI {
        -PlaceTrackedImages imageTracker
        -GameObject _canvasGO
        -PracticeBallController _ballController
        -CreateUI() void
        -OnResetBall() void
        -OnCourtRecalibrate() void
        -OnRacketRecalibrate() void
    }

    class ARTrackedPaddleMapper {
        -Transform paddlePivot
        -Transform visualRoot
        -Vector3 localPositionOffset
        -Vector3 localEulerOffset
        -ARTrackedImage trackedImage
        -ApplyCurrentOffsets() void
        -HandleCalibrationHotkeys() void
    }

    class ARPaddleCalibrationOverlay {
        -ARTrackedPaddleMapper mapper
        -OnGUI() void
    }

    class ARHitDiagnostics {
        -PaddleHitController physicsPaddle
        -BallContactDetector ballDetector
        -Transform ballTransform
        -Transform qrRacketTransform
        +float logIntervalSeconds
        +bool showOnScreenHUD
        +bool autoFixIssues
        -int hitCount
        -Discover() void
        -OnGUI() void
    }

    class POVStartRotationReset {
        +bool lockRollAndPitchInUpdate
    }

    %% Relationships

    PlaceTrackedImages --> ARPlaneGameSpacePlacer : gamePlacer\nPlaceAtAnchor()
    PlaceTrackedImages --> PaddleHitController : wires qrTrackedRacket
    PlayButtonUI --> PlaceTrackedImages : StartGame()
    PlayButtonUI --> ARPlaneGameSpacePlacer : gamePlacer ref

    RecalibrateUI --> PlaceTrackedImages : ResetRacket()\nResetCourt()
    RecalibrateUI --> PracticeBallController : ResetBall()

    PracticeBallController --> DeadHangBall : Freeze()\nRelease()
    BallContactDetector --> PaddleHitController : ApplyHitImpulse()
    DeadHangBall --> PaddleHitController : paddle ref\nOverlapSphere detection
    PaddleHitController --> DeadHangBall : Release() on hit

    BotHitController --> BotShotProfile : PickShot()
    BotShotProfile --> ShotConfig : contains
    BotHitController --> BotTargeting : same GO

    ARPaddleCalibrationOverlay --> ARTrackedPaddleMapper : mapper

    ARHitDiagnostics ..> PaddleHitController : discovers
    ARHitDiagnostics ..> BallContactDetector : discovers
```

---

## 2. State Machine Diagram — Application Lifecycle

```mermaid
stateDiagram-v2
    direction TB

    [*] --> APP_LAUNCH

    APP_LAUNCH --> WAITING_FOR_START : Awake/Start, overlay shown, GameSpaceRoot hidden

    WAITING_FOR_START --> TRACKING_ACTIVE : Tap Play, _gameStarted = true, overlay destroyed

    TRACKING_ACTIVE --> COURT_PLACED : Court QR detected, PlaceAtAnchor, GameSpaceRoot activated

    COURT_PLACED --> PLAYING : Racket QR detected, prefab spawned, paddle wired

    PLAYING --> RALLY : Paddle contacts ball, Release, ApplyHitImpulse

    RALLY --> PLAYING : Ball hits Wall, ResetBall, Freeze + reposition

    RALLY --> PLAYING : Reset Ball tapped, ResetBall, Freeze

    PLAYING --> PLAYING : Reset Ball tapped, re-freeze + reposition

    COURT_PLACED --> TRACKING_ACTIVE : Court recalibrate, ResetCourt, ResetPlacement

    COURT_PLACED --> COURT_PLACED : Racket recalibrate, ResetRacket, re-scan needed
```

---

## 3. State Machine Diagram — Ball Physics States

```mermaid
stateDiagram-v2
    direction LR

    [*] --> FROZEN_AT_SERVE : Awake, FreezeAll, no gravity

    FROZEN_AT_SERVE --> ACTIVE_PHYSICS : Paddle overlap OR ApplyHitImpulse, Release, constraints None, gravity on

    ACTIVE_PHYSICS --> FROZEN_AT_SERVE : Wall hit OR Reset button, Freeze FreezeAll, reposition

    ACTIVE_PHYSICS --> ACTIVE_PHYSICS : Normal physics, drag + Magnus, bot hits, bounces

    note right of FROZEN_AT_SERVE
        Dynamic Rigidbody with FreezeAll constraints.
        NO kinematic toggling.
        OverlapSphere checks for paddle each FixedUpdate.
    end note

    note right of ACTIVE_PHYSICS
        constraints = None
        useGravity = true
        ContinuousDynamic collision
        BallAerodynamics applies drag and spin
    end note
```

---

## 4. Sequence Diagram — Game Start to First Hit

```mermaid
sequenceDiagram
    participant User
    participant PlayButtonUI
    participant PlaceTrackedImages
    participant ARTrackedImageMgr as ARTrackedImageManager
    participant ARPlaneGameSpace as ARPlaneGameSpacePlacer
    participant PaddleHitCtrl as PaddleHitController
    participant DeadHangBall
    participant PracticeBallCtrl as PracticeBallController
    participant BallContactDet as BallContactDetector

    Note over User,BallContactDet: PHASE 1 — APP LAUNCH
    PlayButtonUI->>PlayButtonUI: Start() → CreateUI()
    Note right of PlayButtonUI: Full-screen overlay shown

    Note over User,BallContactDet: PHASE 2 — PLAYER TAPS START
    User->>PlayButtonUI: Tap screen
    PlayButtonUI->>PlaceTrackedImages: StartGame()
    PlaceTrackedImages-->>PlaceTrackedImages: _gameStarted = true
    PlayButtonUI->>PlayButtonUI: Destroy(canvasGO)

    Note over User,BallContactDet: PHASE 3 — COURT QR DETECTED
    ARTrackedImageMgr->>PlaceTrackedImages: OnTrackedImagesChanged(court_anchor)
    PlaceTrackedImages->>ARPlaneGameSpace: PlaceAtAnchor(pose)
    ARPlaneGameSpace->>ARPlaneGameSpace: Combine QR X/Z + Plane Y
    ARPlaneGameSpace->>ARPlaneGameSpace: Create ARAnchor
    ARPlaneGameSpace->>ARPlaneGameSpace: Parent GameSpaceRoot under anchor
    ARPlaneGameSpace->>ARPlaneGameSpace: SetActive(true)
    PlaceTrackedImages-->>PlaceTrackedImages: _courtPlaced = true

    Note over User,BallContactDet: PHASE 4 — RACKET QR DETECTED
    ARTrackedImageMgr->>PlaceTrackedImages: OnTrackedImagesChanged(Racket_PickleBall4)
    PlaceTrackedImages->>PlaceTrackedImages: Instantiate(racketPrefab)
    PlaceTrackedImages->>PaddleHitCtrl: qrTrackedRacket = newPrefab.transform

    Note over User,BallContactDet: PHASE 5 — BALL FROZEN, WAITING FOR HIT
    DeadHangBall->>DeadHangBall: Awake() → Freeze()
    Note right of DeadHangBall: constraints = FreezeAll
    PracticeBallCtrl->>PracticeBallCtrl: Start() → PlaceAtServePosition()

    Note over User,BallContactDet: PHASE 6 — PLAYER SWINGS PADDLE INTO BALL
    User->>User: Moves racket QR card toward ball
    PaddleHitCtrl->>PaddleHitCtrl: FixedUpdate() → MovePosition to QR pose
    DeadHangBall->>DeadHangBall: FixedUpdate() → OverlapSphere detects paddle
    DeadHangBall->>DeadHangBall: Release() → constraints = None

    Note over User,BallContactDet: PHASE 7 — COLLISION & IMPULSE
    BallContactDet->>BallContactDet: OnCollisionEnter(paddleCollider)
    BallContactDet->>PaddleHitCtrl: ApplyHitImpulse(ballRb, contactPt, normal)
    PaddleHitCtrl->>PaddleHitCtrl: COR impulse + Coulomb friction
    PaddleHitCtrl->>PaddleHitCtrl: AddForce(VelocityChange) + AddTorque
    Note right of PaddleHitCtrl: Ball flies with realistic arc
```

---

## 5. Sequence Diagram — Ball Reset Flow

```mermaid
sequenceDiagram
    participant User
    participant RecalibrateUI
    participant PracticeBallCtrl as PracticeBallController
    participant DeadHangBall
    participant Rigidbody

    alt Wall Hit (automatic)
        Note over PracticeBallCtrl: OnCollisionEnter(Wall)
        PracticeBallCtrl->>PracticeBallCtrl: ResetBall()
    else Manual Reset Button
        User->>RecalibrateUI: Tap "Reset Ball"
        RecalibrateUI->>RecalibrateUI: OnResetBall()
        RecalibrateUI->>RecalibrateUI: FindFirstObjectByType<PracticeBallController>()
        RecalibrateUI->>PracticeBallCtrl: ResetBall()
    end

    PracticeBallCtrl->>DeadHangBall: Freeze()
    DeadHangBall->>Rigidbody: velocity = zero
    DeadHangBall->>Rigidbody: angularVelocity = zero
    DeadHangBall->>Rigidbody: useGravity = false
    DeadHangBall->>Rigidbody: constraints = FreezeAll
    DeadHangBall-->>DeadHangBall: IsFrozen = true

    PracticeBallCtrl->>PracticeBallCtrl: PlaceAtServePosition()
    PracticeBallCtrl->>PracticeBallCtrl: transform.localPosition = courtServeLocalPos
    Note right of PracticeBallCtrl: Ball is now frozen at (0.44, 0.50, 2.0)\nrelative to GameSpaceRoot
```

---

## 6. Sequence Diagram — Court Recalibration

```mermaid
sequenceDiagram
    participant User
    participant RecalibrateUI
    participant PlaceTrackedImages
    participant ARPlaneGameSpace as ARPlaneGameSpacePlacer
    participant ARPlaneManager
    participant ARTrackedImageMgr as ARTrackedImageManager

    User->>RecalibrateUI: Tap "↻ Court"
    RecalibrateUI->>PlaceTrackedImages: ResetCourt()
    PlaceTrackedImages-->>PlaceTrackedImages: _courtPlaced = false
    PlaceTrackedImages->>ARPlaneGameSpace: ResetPlacement()
    ARPlaneGameSpace->>ARPlaneGameSpace: DestroyAnchor()
    ARPlaneGameSpace->>ARPlaneGameSpace: GameSpaceRoot.SetActive(false)
    ARPlaneGameSpace->>ARPlaneGameSpace: isPlaced = false
    ARPlaneGameSpace->>ARPlaneManager: enabled = true

    Note over User,ARTrackedImageMgr: Player re-scans court QR
    ARTrackedImageMgr->>PlaceTrackedImages: OnTrackedImagesChanged(court_anchor)
    PlaceTrackedImages->>ARPlaneGameSpace: PlaceAtAnchor(newPose)
    ARPlaneGameSpace->>ARPlaneGameSpace: Create new ARAnchor
    ARPlaneGameSpace->>ARPlaneGameSpace: GameSpaceRoot.SetActive(true)
```

---

## 7. Component Diagram — Ball GameObject

```mermaid
classDiagram
    direction LR

    class Ball2_GameObject {
        <<GameObject>>
        Tag: "Ball"
        Parent: GameSpaceRoot
    }

    class Rigidbody_Component {
        <<Component>>
        mass: 0.0265 kg
        drag: 0
        angularDrag: 0.05
        useGravity: variable
        isKinematic: false
        interpolation: Interpolate
        collisionDetection: ContinuousDynamic
        constraints: variable
    }

    class SphereCollider_Component {
        <<Component>>
        radius: 0.037
        isTrigger: false
    }

    class PracticeBallController_Script {
        <<MonoBehaviour>>
        courtServeLocalPos: (0.44, 0.50, 2.0)
        ResetBall()
        EnableGravity()
    }

    class DeadHangBall_Script {
        <<MonoBehaviour>>
        detectionRadius: 0.12
        IsFrozen: bool
        Freeze()
        Release()
    }

    class BallContactDetector_Script {
        <<MonoBehaviour>>
        overlapRadius: 0.10
        OnCollisionEnter()
        FixedUpdate() OverlapSphere
    }

    class BallAerodynamics_Script {
        <<MonoBehaviour>>
        dragCoefficient: 0.040
        magnusCoefficient: 0.00075
        FixedUpdate() drag+magnus
    }

    Ball2_GameObject --> Rigidbody_Component
    Ball2_GameObject --> SphereCollider_Component
    Ball2_GameObject --> PracticeBallController_Script
    Ball2_GameObject --> DeadHangBall_Script
    Ball2_GameObject --> BallContactDetector_Script
    Ball2_GameObject --> BallAerodynamics_Script
```

---

## 8. Component Diagram — Paddle (PlayerPaddle) GameObject

```mermaid
classDiagram
    direction LR

    class PlayerPaddle_GO {
        <<GameObject>>
        Parent: scene root
    }

    class Rigidbody_Paddle {
        <<Component>>
        isKinematic: true
        useGravity: false
        interpolation: Interpolate
        collisionDetection: ContinuousSpeculative
    }

    class BoxCollider_Paddle {
        <<Component>>
        size: (0.22, 0.26, 0.02)
        isTrigger: false
    }

    class PaddleHitController_Script {
        <<MonoBehaviour>>
        restitution: 0.86
        frictionCoefficient: 0.35
        maxBallSpeed: 22 m/s
        spinFromOffCenter: 5
        QR mode: MovePosition to qrTrackedRacket
        Camera mode: mouse/device screen-point
        ApplyHitImpulse()
    }

    PlayerPaddle_GO --> Rigidbody_Paddle
    PlayerPaddle_GO --> BoxCollider_Paddle
    PlayerPaddle_GO --> PaddleHitController_Script
```

---

## 9. Deployment Diagram — AR System Architecture

```mermaid
flowchart TB
    subgraph AndroidDevice
        subgraph UnityRuntime
            ARSession[AR Session]
            ARCam[AR Camera]

            subgraph ARFoundation
                PlaneMgr[ARPlaneManager]
                ImgMgr[ARTrackedImageManager]
                AnchorMgr[ARAnchorManager]
            end

            subgraph GameLogic
                PTI[PlaceTrackedImages]
                AGSP[ARPlaneGameSpacePlacer]
                PHC[PaddleHitController]
                DHB[DeadHangBall]
                PBC[PracticeBallController]
                BCD[BallContactDetector]
                BA[BallAerodynamics]
                BOT[BotHitController]
            end

            subgraph UILayer
                PBU[PlayButtonUI]
                RUI[RecalibrateUI]
                DIAG[ARHitDiagnostics]
            end

            PhysX[Unity PhysX]
        end

        subgraph PhysicalWorld
            CourtQR[Court QR]
            RacketQR[Racket QR]
            Floor[Floor Surface]
        end
    end

    CourtQR -.-> ImgMgr
    RacketQR -.-> ImgMgr
    Floor -.-> PlaneMgr

    ImgMgr --> PTI
    PlaneMgr --> AGSP
    PTI --> AGSP
    PTI --> PHC
    AGSP --> AnchorMgr

    PBU --> PTI
    RUI --> PBC
    RUI --> PTI

    BCD --> PHC
    PHC --> DHB
    PBC --> DHB

    PHC --> PhysX
    BA --> PhysX
    BOT --> PhysX
    DHB --> PhysX
```

---

## 10. Activity Diagram — Hit Detection Pipeline

```mermaid
flowchart TD
    A[Ball moving] --> B{Ball frozen}

    B -->|yes| C[DeadHangBall fixed update]
    C --> D{Paddle overlap}
    D -->|no| C
    D -->|yes| E[Release ball]
    E --> F[Ball dynamic]

    B -->|no| F

    F --> G{Collision path}

    G --> H[Ball contact detector]
    H --> I[Read contact point and normal]
    I --> J[Apply hit impulse]

    G --> K[Overlap fallback]
    K --> L[Closest point on paddle]
    L --> M[Compute normal]
    M --> J

    G --> N[Paddle collision callback]
    N --> O[Flip normal]
    O --> J

    G --> P[Proximity fallback]
    P --> Q[Closest point helper]
    Q --> J

    J --> R{Cooldown passed}
    R -->|no| S[Skip hit]
    R -->|yes| T[Sanitize normal]
    T --> U[Compute paddle contact velocity]
    U --> V[Split normal and tangential velocity]
    V --> W[Compute normal impulse]
    W --> X[Compute tangential impulse]
    X --> Y[Release dead hang if needed]
    Y --> Z[Add velocity change]
    Z --> AA[Add spin torque]
    AA --> AB[Ball flies with arc and spin]
```
