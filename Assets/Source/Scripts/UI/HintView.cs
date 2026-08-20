using UnityEngine;
using TMPro;

/// <summary>
/// A reference component for the hint/tooltip prefab. Instead of searching for
/// a TMP_Text with GetComponentInChildren, this component holds an explicit
/// reference to the text component. Place it on the tooltip prefab root and
/// assign the text field in the inspector.
///
/// This lets the prefab have any structure — a background panel, a title,
/// padding objects, etc. — without the code needing to know the hierarchy.
/// </summary>
public class HintView : MonoBehaviour
{
    [Tooltip("The TMP_Text component that displays the hint/description text. " +
             "Can be a direct child, a nested child, or the same GameObject.")]
    [SerializeField] private TMP_Text _text;

    /// <summary>The text component for the hint. Null if not assigned.</summary>
    public TMP_Text Text => _text;

    void Awake()
    {
        // Auto-find if not assigned.
        if (_text == null)
            _text = GetComponentInChildren<TMP_Text>(true);
    }

    /// <summary>Sets the hint text.</summary>
    public void SetText(string value)
    {
        if (_text != null)
            _text.text = value;
    }
}
