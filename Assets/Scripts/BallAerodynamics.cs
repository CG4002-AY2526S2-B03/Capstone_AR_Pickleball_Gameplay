using UnityEngine;

// Physics reference (pickleball, per tournament spec):
//   mass   m  = 0.0265 kg
//   radius r  = 0.037 m  → cross-section A = π r² ≈ 0.0043 m²
//   air Cd    ≈ 0.40  (holey ball, measured wind-tunnel data)
//   air ρ     = 1.225 kg/m³
//
// Drag deceleration (ForceMode.Acceleration, i.e. already divided by m):
//   a_drag = (0.5 · Cd · ρ · A · v²) / m
//          = (0.5 · 0.40 · 1.225 · 0.0043) / 0.0265 · v²
//          ≈ 0.040 · v²
//   → F_drag = -v̂ · dragCoefficient · v²  = -velocity · (dragCoefficient · speed)
//
// Magnus lift (ω × v, ForceMode.Acceleration):
//   Cl ≈ 0.20 · (ω r / v)  →  a_magnus = (0.5 · 0.20 · ρ · A · r / m) · ω · v
//          = (0.5 · 0.20 · 1.225 · 0.0043 · 0.037) / 0.0265 · ω · v
//          ≈ 0.00074 · ω · v
//   → F_magnus = Cross(ω, v) · magnusCoefficient

[RequireComponent(typeof(Rigidbody))]
public class BallAerodynamics : MonoBehaviour
{
    [Header("Ball Parameters (match your Rigidbody mass)")]
    // Set this to the same value as the Rigidbody mass (kg) so coefficients stay correct
    // if you scale the ball. Default: 0.0265 kg (regulation pickleball).
    public float ballMass = 0.0265f;

    [Header("Aerodynamics")]
    // Quadratic drag coefficient: a_drag = dragCoefficient * speed²
    // Derived from real Cd=0.40 for a regulation pickleball. Increase for slower decay,
    // decrease for longer carry.
    public float dragCoefficient = 0.040f;

    // Magnus lift coefficient: a_magnus = magnusCoefficient * |ω × v|
    // Derived from Cl ≈ 0.20·(ωr/v) for a pickleball.
    // Lower values = subtler curve; raise above 0.003 for exaggerated arcade feel.
    public float magnusCoefficient = 0.00075f;

    // Maximum angular speed the ball can spin (rad/s).
    // ~80 rad/s ≈ 764 rpm, typical hard topspin in competitive pickleball.
    public float maxAngularSpeed = 80f;

    private Rigidbody ballRigidbody;

    private void Awake()
    {
        ballRigidbody = GetComponent<Rigidbody>();
        ballRigidbody.maxAngularVelocity = maxAngularSpeed;
        ballRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        ballRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    private void FixedUpdate()
    {
        Vector3 velocity = ballRigidbody.velocity;
        float speed = velocity.magnitude;
        if (speed < 0.01f)
        {
            return;
        }

        // Quadratic aerodynamic drag: F = -v̂ · Cd_eff · v²
        // Written as -velocity · (Cd_eff · speed) to avoid an extra normalise.
        Vector3 dragAccel = -velocity * (dragCoefficient * speed);

        // Magnus (spin-lift) force: F = Cl_eff · (ω × v)
        // Direction is perpendicular to both spin axis and velocity,
        // producing topspin dip, backspin float, and sidespin curve.
        Vector3 magnusAccel = Vector3.Cross(ballRigidbody.angularVelocity, velocity) * magnusCoefficient;

        ballRigidbody.AddForce(dragAccel + magnusAccel, ForceMode.Acceleration);
    }
}
