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
    /// Only caps that are part of a stack (StackBase != null OR StackAbove.Count > 0)
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
            // stacked on top of it (StackAbove.Count > 0). Single caps (no stack)
            // only get the trajectory line + end circle, no ghost.
            bool isStackCap = pred.Cap.StackBase != null || pred.Cap.StackAbove.Count > 0;
            if (!isStackCap) continue;

            GameObject ghost = GetOrCreateGhost(pred.Cap);
            if (ghost == null) continue;

            PositionGhost(ghost, pred);

            // Activate the ghost FIRST, before setting materials. The cap's root
            // mesh renderer IS the ghost GameObject itself, so checking
            // activeSelf before activation would skip it and no material gets applied.
            ghost.SetActive(true);

            Material sourceMat = pred.Cap.GetLandingMaterial(pred.WillLandHeads);
            Material ghostMat = GetTransparentMaterial(sourceMat);

            // Set the transparent material on ALL mesh renderers that are actually
            // enabled. We use renderer.enabled (component-level) rather than
            // gameObject.activeSelf because the ghost GameObject is now active
            // and we only want to skip renderers we explicitly disabled (OutlineRenderer).
            MeshRenderer[] renderers = ghost.GetComponentsInChildren<MeshRenderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                MeshRenderer mr = renderers[r];
                if (mr == null) continue;
                if (!mr.enabled) continue; // skip disabled renderers (OutlineRenderer)
                if (mr.sharedMaterial != ghostMat)
                    mr.sharedMaterial = ghostMat;
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
        // Match Cap.ApplyVisuals() Idle state: position = FromXZ(GroundPosition, 0),
        // rotation = identity. Side is encoded in material, not mesh rotation.
        Vector3 worldPos = CapMath.FromXZ(pred.EndPosition, GhostYOffset);
        ghost.transform.position = worldPos;
        ghost.transform.rotation = Quaternion.identity;
    }

    Material GetTransparentMaterial(Material source)
    {
        if (source == null)
            return GetDefaultTransparent();

        if (_transparentMats.TryGetValue(source, out Material cached) && cached != null)
            return cached;

        Material ghostMat;
        Material template = CapTuning.Instance != null ? CapTuning.Instance.GhostMaterial : null;

        if (template != null)
        {
            // PREFERRED PATH: clone the user-assigned template material.
            // The template is a pre-made material asset with transparent surface
            // type already configured in the inspector. This guarantees the
            // transparent shader variant is compiled into the build (URP strips
            // transparent variants if no material asset uses them). We just copy
            // the texture from the source cap material and set the alpha.
            ghostMat = new Material(template);
            ghostMat.name = source.name + "_Ghost";

            CopyTextureAndColor(source, ghostMat);
        }
        else
        {
            // FALLBACK PATH: no template material assigned. Create one from a
            // shader at runtime. WARNING: this may not work in URP builds because
            // the transparent shader variant may be stripped. For reliable
            // transparency, assign a GhostMaterial in CapTuning.
            Shader ghostShader = FindGhostShader();
            ghostMat = new Material(ghostShader);
            ghostMat.name = source.name + "_Ghost";

            CopyTextureAndColor(source, ghostMat);
            ApplyTransparentSetup(ghostMat);
        }

        _transparentMats[source] = ghostMat;
        return ghostMat;
    }

    /// <summary>
    /// Copy the main texture and RGB color (with forced GhostAlpha) from the
    /// source cap material to the ghost material. Handles both URP property
    /// names (_BaseMap/_BaseColor) and Standard names (_MainTex/_Color).
    /// </summary>
    void CopyTextureAndColor(Material source, Material ghostMat)
    {
        // Copy texture.
        Texture mainTex = null;
        if (source.HasProperty("_BaseMap"))
            mainTex = source.GetTexture("_BaseMap");
        else if (source.HasProperty("_MainTex"))
            mainTex = source.GetTexture("_MainTex");

        if (mainTex != null)
        {
            if (ghostMat.HasProperty("_MainTex"))
                ghostMat.SetTexture("_MainTex", mainTex);
            else if (ghostMat.HasProperty("_BaseMap"))
                ghostMat.SetTexture("_BaseMap", mainTex);
        }

        // Extract only RGB from source — never inherit source alpha (could be 0 or 1).
        Color srcColor = Color.white;
        if (source.HasProperty("_BaseColor"))
            srcColor = source.GetColor("_BaseColor");
        else if (source.HasProperty("_Color"))
            srcColor = source.color;

        Color ghostColor = new Color(srcColor.r, srcColor.g, srcColor.b, GhostAlpha);
        if (ghostMat.HasProperty("_Color"))
            ghostMat.color = ghostColor;
        if (ghostMat.HasProperty("_BaseColor"))
            ghostMat.SetColor("_BaseColor", ghostColor);
    }

    /// <summary>
    /// Find a shader for the fallback path (when no GhostMaterial is assigned).
    /// Tries shaders in order of reliability. NOTE: this fallback may not work
    /// in URP builds due to shader variant stripping. For reliable transparency,
    /// assign a GhostMaterial in CapTuning.
    /// </summary>
    Shader FindGhostShader()
    {
        string[] candidates = {
            "UI/Default",                          // Always available, always transparent
            "Unlit/Transparent",                   // Built-in RP transparent
            "Universal Render Pipeline/Unlit",     // URP unlit
            "Unlit/Color",                         // Always available, alpha via _Color
            "Standard",                            // Last resort
        };

        foreach (string name in candidates)
        {
            Shader s = Shader.Find(name);
            if (s != null) return s;
        }

        return Shader.Find("Standard");
    }

    Material GetDefaultTransparent()
    {
        if (_defaultTransparentMat != null) return _defaultTransparentMat;

        Shader shader = FindGhostShader();

        _defaultTransparentMat = new Material(shader);
        _defaultTransparentMat.name = "Ghost_Default";
        _defaultTransparentMat.color = new Color(1f, 1f, 1f, GhostAlpha);
        _defaultTransparentMat.renderQueue = (int)RenderQueue.Transparent;
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
