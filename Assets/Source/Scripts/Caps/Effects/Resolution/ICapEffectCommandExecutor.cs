using UnityEngine;

internal interface ICapEffectCommandExecutor
{
    bool TryLaunch(Cap source, Cap target, Vector2 direction, float rawForce);
    bool TryPush(Cap source, Cap target, Vector2 direction, float rawForce);
}
