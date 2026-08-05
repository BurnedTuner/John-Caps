using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Visualizes the throw trajectory and chain reaction predictions.
/// - Draws a parabolic ARC from the spawn point to the aim point (visual only).
/// - Draws a landing circle at the aim point (thrown cap's landing spot).
/// - For each predicted cap (direct hit + chain):
///   - Draws a line from its start position to its predicted end position (color-coded by depth).
///   - Draws a circle at its predicted end position (using that cap's own radius).
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

    private LineRenderer _arcLine;
    private LineRenderer _landingCircle;
    private readonly List<LineRenderer> _predictionLines = new();
    private readonly List<LineRenderer> _endCircles = new();

    private Transform _lineParent;
    private GameObject _runtimeContainer;
    private static Material _defaultLineMat;

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

    public void Show(
        Vector3 spawnPoint,
        Vector2 aimPoint,
        float slammerRadius,
        CapTuning tuning,
        IReadOnlyList<CapPrediction> directHits,
        IReadOnlyList<CapPrediction> chainPredictions)
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

        // --- Prediction lines + end circles ---
        int totalPredictions = directHits.Count + chainPredictions.Count;
        EnsurePredictionLineCount(totalPredictions);
        EnsureEndCircleCount(totalPredictions);

        int lineIndex = 0;
        for (int i = 0; i < directHits.Count; i++)
        {
            DrawPrediction(directHits[i], lineIndex);
            lineIndex++;
        }
        for (int i = 0; i < chainPredictions.Count; i++)
        {
            DrawPrediction(chainPredictions[i], lineIndex);
            lineIndex++;
        }
        for (int i = lineIndex; i < _predictionLines.Count; i++)
        {
            _predictionLines[i].enabled = false;
        }
        for (int i = lineIndex; i < _endCircles.Count; i++)
        {
            _endCircles[i].enabled = false;
        }
    }

    public void Hide()
    {
        if (_arcLine != null) _arcLine.enabled = false;
        if (_landingCircle != null) _landingCircle.enabled = false;
        foreach (var line in _predictionLines) line.enabled = false;
        foreach (var circle in _endCircles) circle.enabled = false;
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
        bool isInScene = gameObject.scene.IsValid();
        if (isInScene)
        {
            _lineParent = transform;
        }
        else
        {
            _runtimeContainer = new GameObject("TrajectoryPreview_Lines");
            _lineParent = _runtimeContainer.transform;
        }

        EnsureLineRenderers();
        Hide();
    }

    void OnDestroy()
    {
        if (_runtimeContainer != null)
            Destroy(_runtimeContainer);
    }
}