using UnityEngine;

/// <summary>
/// Instantiates a Cap prefab with correct registry hookup.
/// Radius and materials come from the cap's own CapParameters.
/// </summary>
public static class CapFactory
{
    static int _nextStableId = 1;

    public static Cap Create(Cap prefab, Vector2 groundPosition, bool isHeads)
    {
        if (prefab == null)
        {
            Debug.LogError("[CapFactory] Prefab is null.");
            return null;
        }

        Vector3 worldPos = CapMath.FromXZ(groundPosition, 0f);
        Cap instance = Object.Instantiate(prefab, worldPos, Quaternion.identity);

        Vector3 p = instance.transform.position;
        p.y = 0.05f;
        instance.transform.position = p;

        Cap cap = instance.GetComponent<Cap>();
        if (cap == null) cap = instance.gameObject.AddComponent<Cap>();

        cap.Configure(_nextStableId++, isHeads);

        CapRegistry.Register(cap);
        return cap;
    }

    public static void ResetIdCounter(int startId = 1)
    {
        _nextStableId = startId;
    }
}