using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Placed on the sticker image prefab. Holds references to the x2/x3 badge
/// GameObjects so the StickerManager can toggle them without searching by name.
///
/// Assign the badge GameObjects in the inspector. They start inactive.
/// </summary>
public class StickerView : MonoBehaviour
{
    [Tooltip("GameObject shown when ability level == 2.")]
    [SerializeField] private GameObject _x2Badge;

    [Tooltip("GameObject shown when ability level == 3.")]
    [SerializeField] private GameObject _x3Badge;

    /// <summary>Sets the badge visibility based on the ability level (1-3).</summary>
    public void SetLevel(int level)
    {
        if (_x2Badge != null)
            _x2Badge.SetActive(level == 2);
        if (_x3Badge != null)
            _x3Badge.SetActive(level == 3);
    }
}
