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

    public override void BuildCommands(
        in CapFlipEvent flipEvent,
        ICapEffectQuery query,
        ICapEffectCommandSink commands)
    {
        if (flipEvent.Source == null || _radius <= 0f || _force <= 0f)
            return;

        commands.Add(new RadialLaunchCommand(
            flipEvent.Source,
            flipEvent.Position,
            _radius,
            _force));
    }

    void OnValidate()
    {
        _radius = Mathf.Max(0.01f, _radius);
        _force = Mathf.Max(0f, _force);
    }
}
