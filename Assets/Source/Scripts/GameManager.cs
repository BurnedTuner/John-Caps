using UnityEngine;
using UnityEngine.InputSystem;

public class GameManager : MonoBehaviour
{
    [Header("References")]
    public Transform Field;
    public Cap CapPrefab;

    [Header("Initial scatter")]
    public int InitialCapCount = 25;
    [Min(0)] public int InitialPlayerCapCount;
    [Min(0)] public int InitialOpponentCapCount;
    public float ScatterRadius = 4.5f;

    [Header("Field detection")]
    public LayerMask FieldMask = ~0;


    void Start()
    {
        ScatterAmbientCaps();
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

            CapFactory.Create(CapPrefab, groundPos, heads, owner);
        }
    }

}
