using UnityEngine;

/// <summary>
/// Follows an AR anchor's pose each frame without being parented under it.
/// This provides drift correction while protecting the game world from
/// being destroyed if ARFoundation removes the anchor.
/// </summary>
public class AnchorFollower : MonoBehaviour
{
    private Transform _anchor;
    private Vector3 _localOffset;

    public void SetAnchor(Transform anchor, Vector3 localOffset)
    {
        _anchor = anchor;
        _localOffset = localOffset;
    }

    private void LateUpdate()
    {
        if (_anchor == null)
        {
            // Anchor was destroyed by ARFoundation — court stays in place.
            // No need to disable: the court just stops getting drift updates.
            return;
        }

        transform.SetPositionAndRotation(
            _anchor.position + _anchor.rotation * _localOffset,
            _anchor.rotation);
    }
}
