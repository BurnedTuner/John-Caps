using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Generates random face/back material combinations for a cap at creation time.
/// Placed on each cap PREFAB alongside the Cap component.
///
/// Structure:
///   - Template materials: one for face (top), one for back (bottom), one for rim.
///   - Back entries: each back sprite has its own pool of face sprites.
///   - Rim materials: optional pool. If empty, owner-based rim system is used.
///
/// When GenerateVisuals() is called (from Cap.Configure):
///   1. Pick a random back entry.
///   2. Pick a random face from that back's face pool.
///   3. Clone template materials, set their _BaseMap/_MainTex to the sprite textures.
///   4. Apply face -> top renderer, back -> bottom renderer, rim -> rim renderer.
/// </summary>
public class CapVisualGenerator : MonoBehaviour
{
    [System.Serializable]
    public struct BackEntry
    {
        [Tooltip("The sprite used as the base map for the BACK (bottom) material.")]
        public Sprite BackSprite;

        [Tooltip("Possible face sprites for this back. One is picked at random.")]
        public Sprite[] FaceSprites;
    }

    [Header("Template materials")]
    [Tooltip("Template material for the FACE (top). Cloned at runtime with _BaseMap/_MainTex set to the chosen sprite.")]
    [SerializeField] private Material _faceTemplateMaterial;

    [Tooltip("Template material for the BACK (bottom). Cloned at runtime.")]
    [SerializeField] private Material _backTemplateMaterial;

    [Header("Back -> Face pools")]
    [Tooltip("Each back has its own pool of possible faces.")]
    [SerializeField] private BackEntry[] _backEntries;

    [Header("Rim materials (optional)")]
    [Tooltip("Pool of rim materials. If non-empty, a random one is picked. If empty, owner-based rim is used.")]
    [SerializeField] private Material[] _rimMaterials;

    [Header("Renderer references (optional)")]
    [Tooltip("Renderer for the TOP face. If null, auto-found by name 'Top' or GetComponentInChildren.")]
    [SerializeField] private MeshRenderer _topRenderer;

    [Tooltip("Renderer for the BOTTOM face. If null, auto-found by name 'Bottom'.")]
    [SerializeField] private MeshRenderer _bottomRenderer;

    [Tooltip("Renderer for the RIM. If null, auto-found by name 'Rim'.")]
    [SerializeField] private MeshRenderer _rimRenderer;

    // The face/back sprites chosen during the last GenerateVisuals() call.
    // Exposed via properties so the deck UI can use the GENERATED face sprite
    // (not the static prefab-assigned DeckSprite).
    public Sprite GeneratedFaceSprite { get; private set; }
    public Sprite GeneratedBackSprite { get; private set; }

    public void GenerateVisuals()
    {
        if (_backEntries == null || _backEntries.Length == 0) return;
        ResolveRenderers();

        int backIndex = Random.Range(0, _backEntries.Length);
        BackEntry backEntry = _backEntries[backIndex];
        Sprite backSprite = backEntry.BackSprite;
        if (backSprite == null) return;

        Sprite faceSprite = null;
        if (backEntry.FaceSprites != null && backEntry.FaceSprites.Length > 0)
        {
            int faceIndex = Random.Range(0, backEntry.FaceSprites.Length);
            faceSprite = backEntry.FaceSprites[faceIndex];
        }

        if (_topRenderer != null && _faceTemplateMaterial != null && faceSprite != null)
            _topRenderer.sharedMaterial = CloneMaterialWithTexture(_faceTemplateMaterial, faceSprite);

        if (_bottomRenderer != null && _backTemplateMaterial != null && backSprite != null)
            _bottomRenderer.sharedMaterial = CloneMaterialWithTexture(_backTemplateMaterial, backSprite);

        // Store the generated sprites so the deck UI can use them.
        GeneratedFaceSprite = faceSprite;
        GeneratedBackSprite = backSprite;

        if (_rimMaterials != null && _rimMaterials.Length > 0 && _rimRenderer != null)
        {
            int rimIndex = Random.Range(0, _rimMaterials.Length);
            _rimRenderer.sharedMaterial = _rimMaterials[rimIndex];
        }
    }

    public void SetRenderers(MeshRenderer top, MeshRenderer bottom, MeshRenderer rim)
    {
        _topRenderer = top;
        _bottomRenderer = bottom;
        _rimRenderer = rim;
    }

    void ResolveRenderers()
    {
        if (_topRenderer == null) { Transform t = transform.Find("Top"); if (t != null) _topRenderer = t.GetComponent<MeshRenderer>(); }
        if (_bottomRenderer == null) { Transform t = transform.Find("Bottom"); if (t != null) _bottomRenderer = t.GetComponent<MeshRenderer>(); }
        if (_rimRenderer == null) { Transform t = transform.Find("Rim"); if (t != null) _rimRenderer = t.GetComponent<MeshRenderer>(); }

        if (_topRenderer == null || _bottomRenderer == null)
        {
            MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>();
            int found = 0;
            for (int i = 0; i < renderers.Length && found < 3; i++)
            {
                if (renderers[i] == null) continue;
                if (renderers[i].gameObject.name.Contains("Outline")) continue;
                if (found == 0 && _topRenderer == null) _topRenderer = renderers[i];
                else if (found == 1 && _bottomRenderer == null) _bottomRenderer = renderers[i];
                else if (found == 2 && _rimRenderer == null) _rimRenderer = renderers[i];
                found++;
            }
        }
    }

    static Material CloneMaterialWithTexture(Material template, Sprite sprite)
    {
        Material mat = new Material(template);
        Texture2D tex = sprite.texture;
        if (tex == null) return mat;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        else if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
        return mat;
    }
}
