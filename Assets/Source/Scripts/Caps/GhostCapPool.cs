using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Pools transparent "ghost" cap previews for the aim trajectory system.
///
/// Each ghost is a clone of a real Cap's visual GameObject with all behavior
/// components stripped (Cap, CapFlipEffect, Rigidbody, Collider, etc.) — only
/// MeshFilter + MeshRenderer remain. The sharedMaterial is swapped for a
/// transparent clone of the source cap's resolved heads/tails material.
///
/// Ghosts are pooled per-(Cap, index) and reused across frames. A cap can
/// have MULTIPLE ghosts when it appears in multiple predictions (e.g., peeled
/// off then hit by a chain reaction → two flights → two ghosts at different
/// positions). The transparent-material cache is per-source-material, so each
/// unique material is cloned exactly once per session. Hide via SetActive(false);
/// destroy only on Clear() or when the source cap is destroyed.
/// </summary>
public class GhostCapPool
{
    private readonly Dictionary<(Cap, int), GameObject> _ghosts = new();
    private readonly Dictionary<Material, Material> _transparentMats = new();
    private readonly HashSet<(Cap, int)> _usedThisFrame = new();
    private readonly List<(Cap, int)> _staleKeys = new();

    private Transform _parent;
    private Material _defaultTransparentMat;

    public float GhostAlpha =>
        CapTuning.Instance != null ? CapTuning.Instance.GhostAlpha : 0.35f;

    public float GhostYOffset =>
        CapTuning.Instance != null ? CapTuning.Instance.GhostYOffset : 0.02f;

    public void Initialize(Transform parent)
    {
        _parent = parent;
    }

    public void ShowGhosts(IReadOnlyList<CapPrediction> predictions)
    {
        _usedThisFrame.Clear();

        if (predictions == null || predictions.Count == 0)
        {
            HideAll();
            return;
        }

        var perCapCount = new Dictionary<Cap, int>();

        for (int i = 0; i < predictions.Count; i++)
        {
            CapPrediction pred = predictions[i];
            if (pred.Cap == null || pred.Cap.HasLeftGame) continue;

            bool isStackCap = pred.Cap.StackBase != null || pred.Cap.StackedAbove.Count > 0;
            if (!isStackCap) continue;

            if (!perCapCount.TryGetValue(pred.Cap, out int capIndex))
                capIndex = 0;
            perCapCount[pred.Cap] = capIndex + 1;

            var key = (pred.Cap, capIndex);
            GameObject ghost = GetOrCreateGhost(pred.Cap, key);
            if (ghost == null) continue;

            PositionGhost(ghost, pred);

            ghost.SetActive(true);

            MeshRenderer[] renderers = ghost.GetComponentsInChildren<MeshRenderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                MeshRenderer mr = renderers[r];
                if (mr == null) continue;
                if (!mr.enabled) continue;

                Material[] currentMats = mr.sharedMaterials;
                if (currentMats == null || currentMats.Length == 0) continue;

                Material[] transparentMats = new Material[currentMats.Length];
                bool anyChanged = false;
                for (int m = 0; m < currentMats.Length; m++)
                {
                    Material src = currentMats[m];
                    Material ghostMat = GetTransparentMaterial(src);
                    transparentMats[m] = ghostMat;
                    if (ghostMat != src) anyChanged = true;
                }
                if (anyChanged)
                    mr.sharedMaterials = transparentMats;
            }

            _usedThisFrame.Add(key);
        }

        _staleKeys.Clear();
        foreach (var kvp in _ghosts)
        {
            if (kvp.Key.Item1 == null || kvp.Value == null) { _staleKeys.Add(kvp.Key); continue; }
            if (!_usedThisFrame.Contains(kvp.Key))
                kvp.Value.SetActive(false);
        }
        for (int i = 0; i < _staleKeys.Count; i++)
        {
            if (_ghosts.TryGetValue(_staleKeys[i], out GameObject g) && g != null)
                Object.Destroy(g);
            _ghosts.Remove(_staleKeys[i]);
        }
    }

    public void HideAll()
    {
        foreach (var kvp in _ghosts)
        {
            if (kvp.Value != null) kvp.Value.SetActive(false);
        }
    }

    public void Clear()
    {
        foreach (var kvp in _ghosts)
        {
            if (kvp.Value != null) Object.Destroy(kvp.Value);
        }
        _ghosts.Clear();

        foreach (var kvp in _transparentMats)
        {
            if (kvp.Value != null) Object.Destroy(kvp.Value);
        }
        _transparentMats.Clear();

        if (_defaultTransparentMat != null)
        {
            Object.Destroy(_defaultTransparentMat);
            _defaultTransparentMat = null;
        }
        _usedThisFrame.Clear();
        _staleKeys.Clear();
    }

    GameObject GetOrCreateGhost(Cap cap, (Cap, int) key)
    {
        if (cap == null || cap.gameObject == null) return null;

        if (_ghosts.TryGetValue(key, out GameObject existing) && existing != null)
            return existing;

        if (existing == null)
            _ghosts.Remove(key);

        Transform validParent = _parent;
        if (validParent != null && !validParent.gameObject.scene.IsValid())
            validParent = null;

        GameObject ghost = Object.Instantiate(cap.gameObject, validParent);
        ghost.name = $"Ghost_{cap.StableId}_{key.Item2}";
        ghost.SetActive(false);

        Behaviour[] behaviours = ghost.GetComponentsInChildren<Behaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null)
                behaviours[i].enabled = false;
        }

        Rigidbody[] rbs = ghost.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < rbs.Length; i++)
            if (rbs[i] != null) Object.DestroyImmediate(rbs[i]);

        Collider[] colliders = ghost.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
            if (colliders[i] != null) Object.DestroyImmediate(colliders[i]);

        Transform outline = ghost.transform.Find("OutlineRenderer");
        if (outline != null)
        {
            MeshRenderer outlineMr = outline.GetComponent<MeshRenderer>();
            if (outlineMr != null)
                outlineMr.enabled = false;
            else
                outline.gameObject.SetActive(false);
        }

        MeshRenderer[] renderers = ghost.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null) continue;
            renderers[i].shadowCastingMode = ShadowCastingMode.Off;
            renderers[i].receiveShadows = false;
            renderers[i].lightProbeUsage = LightProbeUsage.Off;
            renderers[i].reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        _ghosts[key] = ghost;
        return ghost;
    }

    void PositionGhost(GameObject ghost, CapPrediction pred)
    {
        Vector3 worldPos = CapMath.FromXZ(pred.EndPosition, GhostYOffset);
        ghost.transform.position = worldPos;
        ghost.transform.rotation = pred.Cap != null
            ? pred.Cap.GetLandingRotation(pred.WillLandFace)
            : Quaternion.identity;
    }

    Material GetTransparentMaterial(Material source)
    {
        if (source == null)
            return GetDefaultTransparent();

        if (_transparentMats.TryGetValue(source, out Material cached) && cached != null)
            return cached;

        Material template = CapTuning.Instance != null ? CapTuning.Instance.GhostMaterial : null;

        Material ghostMat;
        if (template != null)
        {
            ghostMat = new Material(template);
            ghostMat.name = source.name + "_Ghost";
            CopyAllTexturesAndColors(source, ghostMat);
            ApplyTransparentSetup(ghostMat);
        }
        else
        {
            ghostMat = new Material(source);
            ghostMat.name = source.name + "_Ghost";
            ApplyTransparentSetup(ghostMat);
        }

        _transparentMats[source] = ghostMat;
        return ghostMat;
    }

    void CopyAllTexturesAndColors(Material source, Material ghostMat)
    {
        if (source == null || ghostMat == null) return;

        System.Collections.Generic.HashSet<string> skipProps = new()
        {
            "_Surface", "_Blend", "_AlphaClip",
            "_SrcBlend", "_DstBlend", "_ZWrite",
            "_Cull", "_ZTest",
            "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON",
            "_SURFACE_TYPE_TRANSPARENT",
        };

        int propCount = source.shader.GetPropertyCount();
        for (int i = 0; i < propCount; i++)
        {
            string propName = source.shader.GetPropertyName(i);
            ShaderPropertyType propType = source.shader.GetPropertyType(i);

            if (skipProps.Contains(propName)) continue;
            if (!ghostMat.HasProperty(propName)) continue;

            switch (propType)
            {
                case ShaderPropertyType.Texture:
                    Texture tex = source.GetTexture(propName);
                    if (tex != null)
                    {
                        ghostMat.SetTexture(propName, tex);
                        Vector2 scale = source.GetTextureScale(propName);
                        Vector2 offset = source.GetTextureOffset(propName);
                        ghostMat.SetTextureScale(propName, scale);
                        ghostMat.SetTextureOffset(propName, offset);
                    }
                    break;

                case ShaderPropertyType.Color:
                    Color c = source.GetColor(propName);
                    if (propName == "_Color" || propName == "_BaseColor")
                        c.a = GhostAlpha;
                    ghostMat.SetColor(propName, c);
                    break;

                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    ghostMat.SetFloat(propName, source.GetFloat(propName));
                    break;

                case ShaderPropertyType.Vector:
                    ghostMat.SetVector(propName, source.GetVector(propName));
                    break;
            }
        }
    }

    Material GetDefaultTransparent()
    {
        if (_defaultTransparentMat != null) return _defaultTransparentMat;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Transparent");

        _defaultTransparentMat = new Material(shader);
        _defaultTransparentMat.name = "Ghost_Default";
        _defaultTransparentMat.color = new Color(1f, 1f, 1f, GhostAlpha);
        ApplyTransparentSetup(_defaultTransparentMat);
        return _defaultTransparentMat;
    }

    void ApplyTransparentSetup(Material mat)
    {
        if (mat == null) return;

        bool hasSurface = mat.HasProperty("_Surface");
        bool hasSrcBlend = mat.HasProperty("_SrcBlend");

        if (hasSurface)
        {
            mat.SetFloat("_Surface", 1);
            if (mat.HasProperty("_Blend"))
                mat.SetFloat("_Blend", 0);
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 0);

            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.DisableKeyword("_ALPHATEST_ON");

            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetShaderPassEnabled("DepthOnly", false);
            mat.SetShaderPassEnabled("SHADOWCASTER", false);
            mat.renderQueue = (int)RenderQueue.Transparent;
        }
        else if (hasSrcBlend)
        {
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)RenderQueue.Transparent;
        }
        else
        {
            mat.renderQueue = (int)RenderQueue.Transparent;
        }

        if (mat.HasProperty("_Color"))
        {
            Color c = mat.color;
            c.a = GhostAlpha;
            mat.color = c;
        }
        if (mat.HasProperty("_BaseColor"))
        {
            Color c = mat.GetColor("_BaseColor");
            c.a = GhostAlpha;
            mat.SetColor("_BaseColor", c);
        }
    }
}
