using UnityEngine;

[System.Serializable]
public class CapParameters
{
    [Header("Geometry")]
    [Tooltip("XZ-plane radius of this cap, used for collision and overlap checks.")]
    [Min(0.01f)] public float Radius = 0.5f;

    [Header("Force")]
    [Tooltip("Static throw force when this cap is thrown by the player.")]
    [Range(3f, 10f)] public float ThrowPower = 5f;

    [Tooltip("Force multiplier when this cap is hit by another cap.")]
    [Min(0f)] public float PowerConversion = 1f;

    [Header("Contact")]
    [Tooltip("Force multiplier for dead-center hits.")]
    [Range(0f, 1f)] public float CenterContactFactor = 0f;

    [Tooltip("Force multiplier for edge hits.")]
    [Range(0f, 1f)] public float EdgeContactFactor = 1f;

    [Header("Push")]
    [Min(0f)] public float PushRadius = 1.5f;
    [Min(0f)] public float PushDistance = 0.5f;
    [Min(0.05f)] public float PushDuration = 0.25f;

    [Header("Materials (per-cap)")]
    [Tooltip("Material shown when this cap is heads-up.")]
    public Material HeadsMaterial;

    [Tooltip("Material shown when this cap is tails-up.")]
    public Material TailsMaterial;

    public float GetContactFactor(float normalizedOffset)
    {
        float t = Mathf.Clamp01(normalizedOffset);
        return Mathf.Lerp(CenterContactFactor, EdgeContactFactor, t);
    }
}