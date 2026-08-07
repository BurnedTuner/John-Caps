using UnityEngine;

public class GameManager : MonoBehaviour
{
    [Header("References")]
    public Transform Field;
    public Cap CapPrefab;

    [Tooltip("Optional mixed pool for ambient caps. Non-null entries override CapPrefab and are picked uniformly.")]
    public Cap[] AmbientCapPrefabs;

    [Header("Initial scatter")]
    public int InitialCapCount = 25;
    [Min(0)] public int InitialPlayerCapCount;
    [Min(0)] public int InitialOpponentCapCount;
    public float ScatterRadius = 4.5f;

    [Header("Field detection")]
    public LayerMask FieldMask = ~0;

    [Header("Simulation")]
    [SerializeField] private CapTurnResolver _turnResolver;

    public event System.Action<GameManager> OnBoardReset;

    void Awake()
    {
        ResolveReferences();
    }

    void Start()
    {
        ScatterAmbientCaps();
    }

    void ResolveReferences()
    {
        if (_turnResolver == null)
            _turnResolver = FindFirstObjectByType<CapTurnResolver>();
    }

    public void ScatterAmbientCaps()
    {
        Vector3 center = Field != null ? Field.position : Vector3.zero;
        int playerCaps = Mathf.Clamp(InitialPlayerCapCount, 0, InitialCapCount);
        int opponentCaps = Mathf.Clamp(InitialOpponentCapCount, 0, InitialCapCount - playerCaps);

        for (int i = 0; i < InitialCapCount; i++)
        {
            Vector2 r = Random.insideUnitCircle * ScatterRadius;
            float x = center.x + r.x;
            float z = center.z + r.y;

            float surfaceY = center.y;
            Vector3 rayStart = new Vector3(x, center.y + 10f, z);
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 100f, FieldMask))
                surfaceY = hit.point.y;

            Vector2 groundPos = new Vector2(x, z);
            bool heads = Random.value > 0.5f;
            CapOwner owner = i < playerCaps
                ? CapOwner.Player
                : i < playerCaps + opponentCaps
                    ? CapOwner.Opponent
                    : CapOwner.Neutral;

            CapFactory.Create(GetAmbientCapPrefab(), groundPos, heads, owner);
        }
    }

    public void ResetBoard()
    {
        ResolveReferences();
        _turnResolver?.ResetSimulation();

        Cap[] caps = CapRegistry.Snapshot();
        for (int i = 0; i < caps.Length; i++)
        {
            if (caps[i] != null)
                Destroy(caps[i].gameObject);
        }

        CapRegistry.Clear();
        CapFactory.ResetIdCounter();
        ScatterAmbientCaps();
        OnBoardReset?.Invoke(this);
    }

    Cap GetAmbientCapPrefab()
    {
        int validCount = 0;
        if (AmbientCapPrefabs != null)
        {
            for (int i = 0; i < AmbientCapPrefabs.Length; i++)
            {
                if (AmbientCapPrefabs[i] != null)
                    validCount++;
            }
        }

        if (validCount == 0) return CapPrefab;

        int selected = Random.Range(0, validCount);
        for (int i = 0; i < AmbientCapPrefabs.Length; i++)
        {
            Cap prefab = AmbientCapPrefabs[i];
            if (prefab == null) continue;
            if (selected-- == 0) return prefab;
        }

        return CapPrefab;
    }

}
