using UnityEngine;
using UnityEngine.InputSystem;

public class GameManager : MonoBehaviour
{
    [Header("References")]
    public Transform Field;
    public Cap CapPrefab;

    [Header("Initial scatter")]
    public int InitialCapCount = 25;
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
            CapFactory.Create(CapPrefab, groundPos, heads);
        }
    }

}