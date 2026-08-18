using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Visualizes the throw trajectory and chain reaction predictions.
/// - Draws a parabolic ARC from the spawn point to the aim point (visual only).
/// - Draws a landing circle at the aim point (thrown cap's landing spot).
/// - For each FULL prediction (within PredictionDepth):
///   - Draws a line from its start position to its predicted end position (color-coded by depth).
///   - Draws a circle at its predicted end position (using that cap's own radius).
/// - For each CONTINUATION prediction (the N+1 cap beyond PredictionDepth):
///   - Draws a HALF-LENGTH line in ContinuationColor (placeholder for a dotted line).
///   - No end circle, no ghost.
/// </summary>
public class TrajectoryPreview : MonoBehaviour
{
    [Header("Line appearance")]
    public float LineWidth = 0.05f;
    public Material LineMaterial;

    [Header("Colors")]
    public Color ArcColor = new Color(0.5f, 0.8f, 1f, 0.7f);
    public Color DirectHitColor = new Color(0.28f, 1f, 0.52f, 0.95f);
    public Color DeepChainColor = new Color(1f, 0.42f, 0.18f, 0.95f);
    public Color LandingCircleColor = new Color(1f, 0.68f, 0.18f, 0.55f);

    [Tooltip("Color for the half-length continuation indicator (the N+1 cap beyond PredictionDepth). " +
             "Placeholder for a future dotted-line style.")]
    public Color ContinuationColor = new Color(1f, 1f, 1f, 0.4f);

    [Tooltip("Color for the bomb explosion radius circles.")]
    public Color BombRadiusColor = new Color(1f, 0.2f, 0.1f, 0.35f);

    private LineRenderer _arcLine;
    private LineRenderer _landingCircle;
    private readonly List<LineRenderer> _predictionLines = new();
    private readonly List<LineRenderer> _endCircles = new();
    private readonly List<LineRenderer> _continuationLines = new();
    private readonly List<LineRenderer> _bombRadiusCircles = new();

    private Transform _lineParent;
    private GameObject _runtimeContainer;
    private static Material _defaultLineMat;

    private GhostCapPool _ghostPool;


    LineRenderer CreateLineRenderer(string name, Color color)
    {
        var obj = new GameObject(name);
        if (_lineParent != null)
            obj.transform.SetParent(_lineParent, false);
        var lr = obj.AddComponent<LineRenderer>();

        if (LineMaterial != null)
            lr.material = LineMaterial;
        else
        {
            if (_defaultLineMat == null)
            {
                var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Hidden/Internal-Colored");
                _defaultLineMat = new Material(shader);
                _defaultLineMat.hideFlags = HideFlags.HideAndDontSave;
            }
            lr.material = _defaultLineMat;
        }

        lr.startWidth = LineWidth;
        lr.endWidth = LineWidth;
        lr.startColor = color;
        lr.endColor = color;
        lr.useWorldSpace = true;
        lr.enabled = false;
        return lr;
    }

    void EnsureLineRenderers()
    {
        if (_arcLine == null)
            _arcLine = CreateLineRenderer("ArcLine", ArcColor);
        if (_landingCircle == null)
            _landingCircle = CreateLineRenderer("LandingCircle", LandingCircleColor);
    }

    void EnsurePredictionLineCount(int count)
    {
        while (_predictionLines.Count < count)
        {
            var line = CreateLineRenderer($"Prediction_{_predictionLines.Count}", DirectHitColor);
            _predictionLines.Add(line);
        }
    }

    void EnsureEndCircleCount(int count)
    {
        while (_endCircles.Count < count)
        {
            var circle = CreateLineRenderer($"EndCircle_{_endCircles.Count}", DirectHitColor);
            _endCircles.Add(circle);
        }
    }

    void EnsureContinuationLineCount(int count)
    {
        while (_continuationLines.Count < count)
        {
            var line = CreateLineRenderer($"Continuation_{_continuationLines.Count}", ContinuationColor);
            _continuationLines.Add(line);
        }
    }

    void EnsureBombRadiusCount(int count)
    {
        while (_bombRadiusCircles.Count < count)
        {
            var line = CreateLineRenderer($"BombRadius_{_bombRadiusCircles.Count}", BombRadiusColor);
            _bombRadiusCircles.Add(line);
        }
    }

    public void Show(
        Vector3 spawnPoint,
        Vector2 aimPoint,
        float slammerRadius,
        CapTuning tuning,
        IReadOnlyList<CapPrediction> fullPredictions,
        IReadOnlyList<CapPrediction> continuationPredictions,
        IReadOnlyList<(Vector3 center, float radius, Color color)> bombZones)
    {
        EnsureLineRenderers();

        Vector3 aim3D = CapMath.FromXZ(aimPoint, 0f);

        // --- Arc (parabolic from spawn to aim) ---
        int samples = Mathf.Max(2, tuning.ArcSamples);
        _arcLine.enabled = true;
        _arcLine.startColor = ArcColor;
        _arcLine.endColor = ArcColor;
        _arcLine.positionCount = samples;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1);
            Vector3 p = Vector3.Lerp(spawnPoint, aim3D, t);
            p.y += tuning.ArcHeight * Mathf.Sin(t * Mathf.PI);
            _arcLine.SetPosition(i, p);
        }

        // --- Landing circle (thrown cap's landing spot) ---
        DrawCircle(_landingCircle, aim3D, slammerRadius, LandingCircleColor);

        // --- Full prediction lines + end circles ---
        int fullCount = fullPredictions.Count;
        EnsurePredictionLineCount(fullCount);
        EnsureEndCircleCount(fullCount);

        for (int i = 0; i < fullCount; i++)
        {
            DrawPrediction(fullPredictions[i], i);
        }
        for (int i = fullCount; i < _predictionLines.Count; i++)
            _predictionLines[i].enabled = false;
        for (int i = fullCount; i < _endCircles.Count; i++)
            _endCircles[i].enabled = false;

        // --- Continuation lines (half-length, no end circle, no ghost) ---
        int contCount = continuationPredictions != null ? continuationPredictions.Count : 0;
        EnsureContinuationLineCount(contCount);
        // If there are full predictions, stack continuations draw the second half
        // to avoid overlapping the previous cap's full trajectory. If there are
        // NO full predictions (N=0), stack continuations draw the first half —
        // there's nothing to overlap, and the first half shows the cap launching
        // from the stack instead of a disconnected floating line.
        bool hasPrecedingFull = fullPredictions.Count > 0;
        for (int i = 0; i < contCount; i++)
        {
            DrawContinuation(continuationPredictions[i], i, hasPrecedingFull);
        }
        for (int i = contCount; i < _continuationLines.Count; i++)
            _continuationLines[i].enabled = false;

        // --- Effect radius circles (bomb, defender, etc.) ---
        int bombCount = bombZones != null ? bombZones.Count : 0;
        EnsureBombRadiusCount(bombCount);
        for (int i = 0; i < bombCount; i++)
        {
            DrawCircle(_bombRadiusCircles[i], bombZones[i].center, bombZones[i].radius, bombZones[i].color);
        }
        for (int i = bombCount; i < _bombRadiusCircles.Count; i++)
            _bombRadiusCircles[i].enabled = false;
    }

    public void Hide()
    {
        if (_arcLine != null) _arcLine.enabled = false;
        if (_landingCircle != null) _landingCircle.enabled = false;
        foreach (var line in _predictionLines) line.enabled = false;
        foreach (var circle in _endCircles) circle.enabled = false;
        foreach (var line in _continuationLines) line.enabled = false;
        foreach (var circle in _bombRadiusCircles) circle.enabled = false;
        _ghostPool?.HideAll();
    }

    /// <summary>
    /// Show transparent ghost-cap previews at each predicted cap's landing position.
    /// Each ghost shows the side (heads/tails) the cap will land on, using a
    /// transparent clone of the cap's own material. Adds on top of the existing
    /// line-based trajectory preview — call after Show().
    /// </summary>
    public void ShowGhosts(IReadOnlyList<CapPrediction> predictions)
    {
        // Lazy-init in case Awake didn't run (e.g., component added at runtime).
        if (_ghostPool == null)
        {
            if (_lineParent == null || !_lineParent.gameObject.scene.IsValid())
            {
                _runtimeContainer = new GameObject("TrajectoryPreview_Ghosts");
                _lineParent = _runtimeContainer.transform;
            }
            _ghostPool = new GhostCapPool();
            _ghostPool.Initialize(_lineParent);
        }
        _ghostPool.ShowGhosts(predictions);
    }

    /// <summary>Clear all pooled ghost GameObjects and cloned materials. Call on board reset.</summary>
    public void ClearGhosts()
    {
        _ghostPool?.Clear();
    }

    void DrawPrediction(CapPrediction prediction, int lineIndex)
    {
        float depthBlend = Mathf.Clamp01(prediction.Depth / 5f);
        Color color = Color.Lerp(DirectHitColor, DeepChainColor, depthBlend);
        Color fadedColor = new Color(color.r, color.g, color.b, 0.2f);

        // --- Prediction line ---
        var line = _predictionLines[lineIndex];
        line.enabled = true;
        line.startColor = color;
        line.endColor = fadedColor;

        Vector3 start = CapMath.FromXZ(prediction.StartPosition, 0.05f);
        Vector3 end = CapMath.FromXZ(prediction.EndPosition, 0.05f);
        line.positionCount = 2;
        line.SetPosition(0, start);
        line.SetPosition(1, end);

        // --- End circle (where this cap will land) ---
        var circle = _endCircles[lineIndex];
        float capRadius = prediction.Cap != null ? prediction.Cap.Parameters.Radius : 0.5f;
        DrawCircle(circle, end, capRadius, color);
    }

    /// <summary>
    /// Draw a HALF-LENGTH continuation line for the N+1 prediction beyond
    /// PredictionDepth. Signals "the chain/stack continues, but we're not
    /// showing full detail." No end circle, no ghost. Placeholder for a
    /// future dotted-line style.
    ///
    /// Which half is drawn depends on the prediction's source and whether
    /// there are preceding full predictions:
    ///   - Stack + hasPrecedingFull: SECOND HALF (midpoint → end). All peel-off
    ///     caps share the same StartPosition (the stack), so the first half
    ///     would overlap exactly with the previous cap's full trajectory.
    ///     The second half appears AFTER the last ghost.
    ///   - Stack + no preceding full (N=0): FIRST HALF (start → midpoint).
    ///     There's nothing to overlap, and the first half shows the cap
    ///     launching from the stack instead of a disconnected floating line.
    ///   - Chain/Direct: FIRST HALF (start → midpoint). Each chain cap has
    ///     its own distinct start position, so the first half is meaningful.
    /// </summary>
    void DrawContinuation(CapPrediction prediction, int lineIndex, bool hasPrecedingFull)
    {
        var line = _continuationLines[lineIndex];
        line.enabled = true;
        line.startColor = ContinuationColor;
        line.endColor = ContinuationColor;

        Vector3 start = CapMath.FromXZ(prediction.StartPosition, 0.05f);
        Vector3 mid = CapMath.FromXZ(
            prediction.StartPosition + prediction.Direction * (prediction.TravelDistance * 0.5f),
            0.05f);
        Vector3 end = CapMath.FromXZ(prediction.EndPosition, 0.05f);
        line.positionCount = 2;

        bool drawSecondHalf = prediction.Source == PredictionSource.Stack && hasPrecedingFull;

        if (drawSecondHalf)
        {
            // Second half: midpoint → end
            line.SetPosition(0, mid);
            line.SetPosition(1, end);
        }
        else
        {
            // First half: start → midpoint
            line.SetPosition(0, start);
            line.SetPosition(1, mid);
        }
    }

    void DrawCircle(LineRenderer line, Vector3 center, float radius, Color color, int segments = 32)
    {
        line.enabled = true;
        line.startColor = color;
        line.endColor = color;
        line.positionCount = segments + 1;
        for (int i = 0; i <= segments; i++)
        {
            float a = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 p = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            line.SetPosition(i, p);
        }
    }

    void Awake()
    {
        // Always create a runtime container for ghost/line parents. Even when
        // TrajectoryPreview is in a scene, its transform can be treated as
        // "persistent" by Instantiate in prefab-instance scenarios, which would
        // cause ghost creation to fail with "Cannot instantiate objects with a
        // parent which is persistent".
        _runtimeContainer = new GameObject("TrajectoryPreview_Runtime");
        _runtimeContainer.transform.SetParent(transform, false);
        _lineParent = _runtimeContainer.transform;

        EnsureLineRenderers();

        _ghostPool = new GhostCapPool();
        _ghostPool.Initialize(_lineParent);

        Hide();
    }

    void OnDestroy()
    {
        if (_runtimeContainer != null)
            Destroy(_runtimeContainer);
        _ghostPool?.Clear();
    }
}
