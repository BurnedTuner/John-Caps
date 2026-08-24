using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Pools transparent "ghost" cap previews for the aim trajectory system.
///
/// Each ghost is a clone of a real Cap's visual GameObject with all behavior
/// components stripped (Cap, CapFlipEffect, Rigidbody, Collider, etc.) — only
/// MeshFilter + MeshRenderer remain. The sharedMaterial is swapped for a
/// transparent clone of the source cap's resolved face/back material.
///
/// Ghosts are pooled per-Cap and reused across frames. The transparent-material
/// cache is per-source-material, so each unique material is cloned exactly once
/// per session. Hide via SetActive(false); destroy only on Clear() or when the
/// source cap is destroyed.
/// </summary>
public class GhostCapPool
{
    private readonly Dictionary<Cap, GameObject> _ghosts = new();
    private readonly Dictionary<Material, Material> _transparentMats = new();
    private readonly HashSet<Cap> _usedThisFrame = new();
    private readonly List<Cap> _staleKeys = new();

    private Transform _parent;
    private Material _defaultTransparentMat;

    /// <summary>Alpha (0-1) applied to cloned ghost materials. Read from CapTuning at runtime.</summary>
    public float GhostAlpha =>
        CapTuning.Instance != null ? CapTuning.Instance.GhostAlpha : 0.35f;

    /// <summary>Small Y offset to lift ghosts above the table and avoid z-fighting. Read from CapTuning.</summary>
    public float GhostYOffset =>
        CapTuning.Instance != null ? CapTuning.Instance.GhostYOffset : 0.02f;

    /// <summary>
    /// Initialize the pool. Call once from the owner's Awake/Start.
    /// Ghost material, alpha, and Y offset are read from CapTuning.Instance at
    /// runtime — no need to pass them here.
    /// </summary>
    public void Initialize(Transform parent)
    {
        _parent = parent;
    }

    /// <summary>
    /// Show ghosts for the supplied predictions. Ghosts for caps NOT in the list
    /// are hidden. Creates ghosts and transparent materials on demand (cached).
    /// Safe to call every frame.
    ///
    /// Only caps that are part of a stack (StackBase != null OR StackedAbove.Count > 0)
    /// get ghosts. Single caps only get the trajectory line + end circle.
    /// </summary>
    public void ShowGhosts(IReadOnlyList<CapPrediction> predictions)
    {
        _usedThisFrame.Clear();

        if (predictions == null || predictions.Count == 0)
        {
            HideAll();
            return;
        }

        for (int i = 0; i < predictions.Count; i++)
        {
            CapPrediction pred = predictions[i];
            if (pred.Cap == null || pred.Cap.HasLeftGame) continue;

            // Only show ghosts for caps that are part of a stack — either this cap
            // is stacked on top of another (StackBase != null) or it has caps
            // stacked on top of it (StackedAbove.Count > 0). Single caps (no stack)
            // only get the trajectory line + end circle, no ghost.
            bool isStackCap = pred.Cap.StackBase != null || pred.Cap.StackedAbove.Count > 0;
            if (!isStackCap) continue;

            GameObject ghost = GetOrCreateGhost(pred.Cap);
            if (ghost == null) continue;

            PositionGhost(ghost, pred);

            // Activate the ghost FIRST, before setting materials.
            ghost.SetActive(true);

            // Apply transparent materials to ALL sub-meshes of the ghost.
            // The 3D cap model has 3 material slots (top, bottom, rim). We need
            // to make all of them transparent so the ghost is see-through from
            // any angle. Each slot's material is individually cloned + made transparent.
            MeshRenderer[] renderers = ghost.GetComponentsInChildren<MeshRenderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                MeshRenderer mr = renderers[r];
                if (mr == null) continue;
                if (!mr.enabled) continue; // skip disabled renderers (OutlineRenderer)

                // Get all materials on this renderer and make each one transparent.
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

            _usedThisFrame.Add(pred.Cap);
        }

        // Hide ghosts whose cap didn't appear in this frame's predictions.
        _staleKeys.Clear();
        foreach (var kvp in _ghosts)
        {
            if (kvp.Key == null || kvp.Value == null) { _staleKeys.Add(kvp.Key); continue; }
            if (!_usedThisFrame.Contains(kvp.Key))
                kvp.Value.SetActive(false);
        }
        for (int i = 0; i < _staleKeys.Count; i++)
        {
            if (_staleKeys[i] != null && _ghosts.TryGetValue(_staleKeys[i], out GameObject g) && g != null)
                Object.Destroy(g);
            _ghosts.Remove(_staleKeys[i]);
        }
    }

    /// <summary>Hide all ghosts without destroying them.</summary>
    public void HideAll()
    {
        foreach (var kvp in _ghosts)
        {
            if (kvp.Value != null) kvp.Value.SetActive(false);
        }
    }

    /// <summary>
    /// Destroy all ghosts and all cloned transparent materials. Use on board reset
    /// or owner OnDestroy.
    /// </summary>
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

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    GameObject GetOrCreateGhost(Cap cap)
    {
        if (cap == null || cap.gameObject == null) return null;

        if (_ghosts.TryGetValue(cap, out GameObject existing) && existing != null)
            return existing;

        // Cleanup any stale entries (cap was destroyed but key lingered).
        if (existing == null)
            _ghosts.Remove(cap);

        // Determine a valid scene parent. If _parent is a persistent prefab asset
        // (not a scene instance), Instantiate refuses to use it. Fall back to
        // null parent (root of active scene) in that case.
        Transform validParent = _parent;
        if (validParent != null && !validParent.gameObject.scene.IsValid())
            validParent = null;

        GameObject ghost = Object.Instantiate(cap.gameObject, validParent);
        ghost.name = $"Ghost_{cap.StableId}";
        ghost.SetActive(false); // created hidden; ShowGhosts activates

        // Disable ALL Behaviour components on root and children (Cap, CapFlipEffect,
        // etc.). We don't destroy them — destroying Cap triggers OnDestroy which
        // calls CapRegistry.Unregister (harmless but noisy). Disabling is enough.
        Behaviour[] behaviours = ghost.GetComponentsInChildren<Behaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null)
                behaviours[i].enabled = false;
        }

        // Destroy physics components (Rigidbody, Collider) — ghost is purely visual.
        Rigidbody[] rbs = ghost.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < rbs.Length; i++)
            if (rbs[i] != null) Object.DestroyImmediate(rbs[i]);

        Collider[] colliders = ghost.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
            if (colliders[i] != null) Object.DestroyImmediate(colliders[i]);

        // Disable the OutlineRenderer specifically. We disable the MeshRenderer
        // COMPONENT (not the GameObject) so that the material-setting loop in
        // ShowGhosts can use `mr.enabled` as a filter to skip it.
        Transform outline = ghost.transform.Find("OutlineRenderer");
        if (outline != null)
        {
            MeshRenderer outlineMr = outline.GetComponent<MeshRenderer>();
            if (outlineMr != null)
                outlineMr.enabled = false;
            else
                outline.gameObject.SetActive(false);
        }

        // Configure ALL mesh renderers in the ghost for transparent overlay
        // (no shadows, no lighting). We keep every mesh child intact so the
        // ghost matches the cap's visual structure exactly.
        MeshRenderer[] renderers = ghost.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null) continue;
            renderers[i].shadowCastingMode = ShadowCastingMode.Off;
            renderers[i].receiveShadows = false;
            renderers[i].lightProbeUsage = LightProbeUsage.Off;
            renderers[i].reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        _ghosts[cap] = ghost;
        return ghost;
    }

    void PositionGhost(GameObject ghost, CapPrediction pred)
    {
        // Position at the predicted landing point. Rotation shows the predicted
        // side: identity = face up, 180° X rotation = back up.
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
            // PREFERRED PATH: clone the template material (guaranteed to have
            // the transparent shader variant compiled into the build), then
            // copy ALL textures + properties from the source cap material.
            // CRITICAL: re-apply transparent setup AFTER copying, because
            // copying floats includes _Surface which would override the
            // template's transparent setting back to opaque.
            ghostMat = new Material(template);
            ghostMat.name = source.name + "_Ghost";
            CopyAllTexturesAndColors(source, ghostMat);
            ApplyTransparentSetup(ghostMat); // re-apply: copying _Surface=0 broke transparency
        }
        else
        {
            // FALLBACK: no template. Clone the source directly and apply
            // transparent setup. May not work in URP builds due to shader
            // variant stripping — assign a GhostMaterial in CapTuning for
            // reliable transparency.
            ghostMat = new Material(source);
            ghostMat.name = source.name + "_Ghost";
            ApplyTransparentSetup(ghostMat);
        }

        _transparentMats[source] = ghostMat;
        return ghostMat;
    }

    /// <summary>
    /// Copy ALL textures, colors, floats, vectors, AND texture tiling/offset
    /// from the source material to the ghost material. Iterates every shader
    /// property on the source and copies by name — works regardless of which
    /// properties the shader uses.
    ///
    /// CRITICAL: skips render-state properties (_Surface, _Blend, _AlphaClip,
    /// _SrcBlend, _DstBlend, _ZWrite, _Cull) so the template's transparent
    /// setup is preserved. These would otherwise be copied from the source
    /// (opaque) material and break transparency.
    /// </summary>
    void CopyAllTexturesAndColors(Material source, Material ghostMat)
    {
        if (source == null || ghostMat == null) return;

        // Properties that control render state / transparency. NEVER copy these
        // from the source — they would override the template's transparent setup.
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

            // Skip render-state properties — preserve template's transparent setup.
            if (skipProps.Contains(propName)) continue;

            // Only copy if the ghost material also has this property.
            if (!ghostMat.HasProperty(propName)) continue;

            switch (propType)
            {
                case ShaderPropertyType.Texture:
                    Texture tex = source.GetTexture(propName);
                    if (tex != null)
                    {
                        ghostMat.SetTexture(propName, tex);
                        // Copy texture tiling and offset.
                        Vector2 scale = source.GetTextureScale(propName);
                        Vector2 offset = source.GetTextureOffset(propName);
                        ghostMat.SetTextureScale(propName, scale);
                        ghostMat.SetTextureOffset(propName, offset);
                    }
                    break;

                case ShaderPropertyType.Color:
                    Color c = source.GetColor(propName);
                    // Force alpha to GhostAlpha on the main color property,
                    // keep source alpha on other color properties (e.g. emission).
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

        // Fallback when source material is null. Try URP first, then Standard.
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

        // Detect shader type by its properties, not by the render pipeline.
        // This handles the case where a user assigns a Standard shader in a URP
        // project, or a URP shader in a Built-in project.
        bool hasSurface = mat.HasProperty("_Surface");       // URP Lit/Unlit
        bool hasSrcBlend = mat.HasProperty("_SrcBlend");     // Standard / Legacy

        if (hasSurface)
        {
            // URP Lit or URP Unlit shader — set surface type to Transparent.
            // Must enable the keyword AND set the property, otherwise the shader
            // compiles to opaque-only and ignores alpha.
            mat.SetFloat("_Surface", 1); // 1 = Transparent
            if (mat.HasProperty("_Blend"))
                mat.SetFloat("_Blend", 0); // 0 = Alpha blend
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
            // Standard shader (Built-in RP) fade setup.
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
            // Unknown shader (UI/Default, Unlit/Transparent, custom shaders) —
            // these are usually natively transparent. Just set the render queue.
            mat.renderQueue = (int)RenderQueue.Transparent;
        }

        // Force alpha on whichever color property the shader uses.
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
