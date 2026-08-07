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
}
