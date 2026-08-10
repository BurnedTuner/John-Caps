using UnityEngine;

public class VFXManager : MonoBehaviour
{
    public static VFXManager Instance { get; private set; }
    public float DefaultLifetime = 2f;

    void Awake() => Instance = this;

    public void Spawn(GameObject prefab, Vector3 pos, float lifetime = -1f)
    {
        if (prefab == null) return;
        if (lifetime < 0f) lifetime = DefaultLifetime;
        Destroy(Instantiate(prefab, pos, Quaternion.identity), lifetime);
    }
}
