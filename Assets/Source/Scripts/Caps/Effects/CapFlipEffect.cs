using UnityEngine;

/// <summary>
/// Base component for an ability that builds commands after this cap finishes a flip.
/// Add a derived component to a cap prefab. A prefab without one is a normal cap.
/// </summary>
public abstract class CapFlipEffect : MonoBehaviour
{
    public abstract void BuildCommands(
        in CapFlipEvent flipEvent,
        ICapEffectQuery query,
        ICapEffectCommandSink commands);

    /// <summary>
    /// Called by CapTurnResolver after commands are executed.
    /// Override this to play effect-specific VFX/Audio.
    /// </summary>
    public virtual void PlayFeedback(Vector3 position, float force) { }

    /// <summary>
    /// Describes this effect as a radial launch so a headless simulation can reproduce it without
    /// running the command pipeline. Override it in effects that boil down to "push everything in a
    /// circle"; the AI move search reads the values straight off the prefab, with no live cap needed.
    /// Effects that cannot be expressed this way stay invisible to the search and are simply
    /// not accounted for when it evaluates a throw.
    /// </summary>
    public virtual bool TryGetRadialLaunch(out float radius, out float force)
    {
        radius = 0f;
        force = 0f;
        return false;
    }
}
