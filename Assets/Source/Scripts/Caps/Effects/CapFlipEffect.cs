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
}
