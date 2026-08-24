using UnityEngine;

/// <summary>
/// Instantiates a Cap prefab with correct registry hookup.
/// Radius and materials come from the cap's own CapParameters.
/// </summary>
public static class CapFactory
{
    static int _nextStableId = 1;

    public static Cap Create(
        Cap prefab,
        Vector2 groundPosition,
        bool isFace,
        CapOwner owner = CapOwner.Neutral)
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

        cap.Configure(_nextStableId++, isFace, owner);

        // Awake ran during Instantiate and may have set _isScenePlaced = true
        // (it checks _stableId == 0, but Configure hadn't run yet). Clear it —
        // this cap was factory-created, not scene-placed.
        cap.MarkFactoryCreated();

        CapRegistry.Register(cap);
        return cap;
    }

    public static void ResetIdCounter(int startId = 1)
    {
        _nextStableId = startId;
    }

    /// <summary>
    /// Returns the next stable ID and increments the counter. Used by Cap.Awake
    /// to assign IDs to caps placed in the scene editor (which don't go through
    /// CapFactory.Create).
    /// </summary>
    public static int NextStableId()
    {
        return _nextStableId++;
    }
}
