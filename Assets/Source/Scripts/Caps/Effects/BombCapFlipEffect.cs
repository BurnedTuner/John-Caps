using UnityEngine;

/// <summary>
/// Launches nearby caps away from this cap when it finishes a flip.
/// Bomb levels are prefab variants with different Radius and Force values.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Cap))]
public sealed class BombCapFlipEffect : CapFlipEffect
{
    [SerializeField, Min(0.01f)]
    [Tooltip("Explosion radius on the XZ plane, measured between cap centres.")]
    private float _radius = 3f;

    [SerializeField, Min(0f)]
    [Tooltip("Flat launch force applied equally to every available cap inside Radius.")]
    private float _force = 3f;

    public float Radius => _radius;
    public float Force => _force;

    public override void Activate(CapFlipEffectContext context)
    {
        if (context == null || context.Source == null || _radius <= 0f || _force <= 0f)
            return;

        for (int i = 0; i < context.Caps.Count; i++)
        {
            Cap target = context.Caps[i];
            if (target == null || target == context.Source || target.IsBusy) continue;

            Vector2 offset = target.GroundPosition - context.Position;
            float distance = offset.magnitude;
            if (distance >= _radius) continue;

            Vector2 direction = offset.sqrMagnitude > 0.000001f
                ? offset / distance
                : Vector2.right;

            context.TryLaunch(target, direction, _force);
        }
    }

    void OnValidate()
    {
        _radius = Mathf.Max(0.01f, _radius);
        _force = Mathf.Max(0f, _force);
    }
}
