using UnityEngine;
using UnityEngine.Rendering;

/// States:
///   Idle      — sitting still on the field, can be hit.
///   Throwing  — player's thrown cap flying in a visual arc from spawn to landing point.
///   Flying    — launched by an impact: flying to destination + flipping 180°.
///   Pushed    — nudged by the push radius effect: sliding briefly, no flip.

[RequireComponent(typeof(MeshRenderer))]
public class Cap : MonoBehaviour
{
    public enum CapState { Idle, Throwing, Flying, Pushed }

    [Header("Identity")]
    [SerializeField] private int _stableId;
    [SerializeField] private CapOwner _owner = CapOwner.Neutral;
    public int StableId => _stableId;
    public CapOwner Owner => _owner;

    [Header("Team outline")]
    [SerializeField] private MeshRenderer _outlineRenderer;
    [SerializeField, Min(0f)] private float _outlineWidth = 0.035f;
    [SerializeField] private Color _playerOutlineColor = new Color(0.05f, 0.9f, 0.85f, 1f);
    [SerializeField] private Color _opponentOutlineColor = new Color(1f, 0.2f, 0.05f, 1f);

    [Header("Cap parameters")]
    [SerializeField] private CapParameters _parameters = new CapParameters();
    public CapParameters Parameters => _parameters;

    public float GetContactFactor(float normalizedOffset) => _parameters.GetContactFactor(normalizedOffset);

    public Vector2 GroundPosition { get; private set; }
    public bool IsHeads { get; private set; } = true;
    public bool IsBusy => _state != CapState.Idle;
    public CapState CurrentState => _state;
    public int ActivationDepthPlusOne => _activationDepth + 1;

    private bool _isImmutable;

    private CapTuning _tuning;
    private MeshRenderer _meshRenderer;
    private Material _resolvedHeadsMat;
    private Material _resolvedTailsMat;
    private MaterialPropertyBlock _outlineProperties;

    private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
    private static readonly int OutlineWidthId = Shader.PropertyToID("_OutlineWidth");

    private CapState _state = CapState.Idle;

    // Throwing (player's thrown cap — visual arc)
    private Vector3 _throwStart;
    private Vector3 _throwEnd;
    private float _throwElapsed;
    private float _throwDuration;
    private float _throwArcHeight;
    private float _landingForce;

    // Flying (chain-hit cap — straight line + flip)
    private Vector2 _flyStart;
    private Vector2 _flyDirection;
    private float _flyTotalDistance;
    private float _flyElapsed;
    private float _flyDuration;
    private int _activationDepth;
    private bool _fromHeads;

    // Pushed
    private Vector2 _pushStart;
    private Vector2 _pushDirection;
    private float _pushRemaining;
    private float _pushElapsed;
    private float _pushTotalDuration;

    public void Configure(int id, bool isHeads, CapOwner owner = CapOwner.Neutral)
    {
        _stableId = id;
        _owner = owner;
        IsHeads = isHeads;
        GroundPosition = CapMath.ToXZ(transform.position);
        _state = CapState.Idle;
        ResolveMaterials();
        ApplyVisuals();
        ApplyOutline();
    }

    void ResolveMaterials()
    {
        if (_tuning == null) _tuning = CapTuning.Instance;
        if (_tuning != null)
        {
            _resolvedHeadsMat = _parameters.HeadsMaterial != null ? _parameters.HeadsMaterial : _tuning.HeadsMaterial;
            _resolvedTailsMat = _parameters.TailsMaterial != null ? _parameters.TailsMaterial : _tuning.TailsMaterial;
        }
        else
        {
            _resolvedHeadsMat = _parameters.HeadsMaterial;
            _resolvedTailsMat = _parameters.TailsMaterial;
        }
    }

    public void SetImmutable(bool value) => _isImmutable = value;

    public void SetOwner(CapOwner owner)
    {
        _owner = owner;
        ApplyOutline();
    }

    public void BeginThrow(Vector3 start, Vector3 end, float force, float duration, float arcHeight)
    {
        GroundPosition = CapMath.ToXZ(end);
        _throwStart = start;
        _throwEnd = end;
        _throwElapsed = 0f;
        _throwDuration = duration;
        _throwArcHeight = arcHeight;
        _landingForce = force;
        _state = CapState.Throwing;
        ApplyVisuals();
    }

    public bool BeginLaunch(int throwId, int depth, Vector2 direction, float force, float travelDistance, float duration, int ignoredSourceId)
    {
        if (_isImmutable) return false;
        if (_state != CapState.Idle) return false;
        if (float.IsNaN(direction.x) || float.IsNaN(direction.y)) return false;
        if (float.IsNaN(travelDistance) || float.IsNaN(force)) return false;

        _activationDepth = depth;
        _fromHeads = IsHeads;
        _flyStart = GroundPosition;
        _flyDirection = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector2.right;
        _flyTotalDistance = travelDistance;
        _flyElapsed = 0f;
        _flyDuration = duration;
        _landingForce = force;
        _state = CapState.Flying;
        ApplyVisuals();
        return true;
    }

    public void BeginPush(Vector2 direction, float distance, float duration)
    {
        if (_isImmutable) return;
        if (_state != CapState.Idle) return;
        if (distance <= 0.0001f) return;
        _pushStart = GroundPosition;
        _pushDirection = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector2.right;
        _pushRemaining = distance;
        _pushTotalDuration = Mathf.Max(0.01f, duration);
        _pushElapsed = 0f;
        _state = CapState.Pushed;
        ApplyVisuals();
    }

    public void StepSimulation(float deltaTime, System.Action<Cap, Vector2, float> onLanded)
    {
        switch (_state)
        {
            case CapState.Throwing: StepThrow(deltaTime, onLanded); break;
            case CapState.Flying: StepFly(deltaTime, onLanded); break;
            case CapState.Pushed: StepPush(deltaTime); break;
        }
        ApplyVisuals();
    }

    void StepThrow(float dt, System.Action<Cap, Vector2, float> onLanded)
    {
        _throwElapsed = Mathf.Min(_throwElapsed + dt, _throwDuration);
        float t = _throwDuration > 0f ? _throwElapsed / _throwDuration : 1f;

        Vector3 pos = Vector3.Lerp(_throwStart, _throwEnd, t);
        pos.y += _throwArcHeight * Mathf.Sin(t * Mathf.PI);
        transform.position = pos;

        if (_throwElapsed >= _throwDuration)
        {
            _state = CapState.Idle;
            transform.position = _throwEnd;
            onLanded?.Invoke(this, GroundPosition, _landingForce);
        }
    }

    void StepFly(float dt, System.Action<Cap, Vector2, float> onLanded)
    {
        _flyElapsed += dt;
        float t = _flyDuration > 0f ? Mathf.Clamp01(_flyElapsed / _flyDuration) : 1f;
        Vector2 next = _flyStart + _flyDirection * (_flyTotalDistance * t);
        if (float.IsNaN(next.x) || float.IsNaN(next.y))
        {
            _state = CapState.Idle;
            return;
        }
        GroundPosition = next;

        if (_flyElapsed >= _flyDuration)
        {
            GroundPosition = _flyStart + _flyDirection * _flyTotalDistance;
            IsHeads = !IsHeads;
            _state = CapState.Idle;
            onLanded?.Invoke(this, GroundPosition, _landingForce);
        }
    }

    void StepPush(float dt)
    {
        _pushElapsed += dt;
        float t = Mathf.Clamp01(_pushElapsed / _pushTotalDuration);
        float eased = 1f - (1f - t) * (1f - t);
        float travelled = _pushRemaining * eased;
        GroundPosition = _pushStart + _pushDirection * travelled;
        if (t >= 1f) _state = CapState.Idle;
    }

    void ApplyVisuals()
    {
        if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
        if (_tuning == null) _tuning = CapTuning.Instance;

        Vector3 pos;
        Quaternion rot = Quaternion.identity;

        switch (_state)
        {
            case CapState.Throwing:
                pos = transform.position;
                float throwProgress = _throwDuration > 0f ? _throwElapsed / _throwDuration : 1f;
                rot = Quaternion.Euler(0f, throwProgress * _tuning.FlightSpinDegrees, 0f);
                break;

            case CapState.Flying:
                float flyProgress = _flyDuration > 0f ? Mathf.Clamp01(_flyElapsed / _flyDuration) : 1f;
                float hop = Mathf.Sin(flyProgress * Mathf.PI) * _tuning.CapFlipApexHeight;
                pos = CapMath.FromXZ(GroundPosition, hop);
                Vector3 motion3D = new Vector3(_flyDirection.x, 0f, _flyDirection.y);
                Vector3 rotAxis = Vector3.Cross(Vector3.up, motion3D).normalized;
                if (rotAxis.sqrMagnitude < 0.0001f) rotAxis = Vector3.right;
                rot = Quaternion.AngleAxis(flyProgress * 180f, rotAxis);
                break;

            case CapState.Pushed:
                pos = CapMath.FromXZ(GroundPosition, 0f);
                break;

            default:
                pos = CapMath.FromXZ(GroundPosition, 0f);
                break;
        }

        if (IsNaN(pos)) return;
        transform.position = pos;
        transform.rotation = rot;

        if (_meshRenderer != null && _resolvedHeadsMat != null && _resolvedTailsMat != null)
        {
            bool showHeads = _state == CapState.Flying ? _fromHeads : IsHeads;
            _meshRenderer.sharedMaterial = showHeads ? _resolvedHeadsMat : _resolvedTailsMat;
        }
    }

    static bool IsNaN(Vector3 v) => float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z);
    
    void ApplyOutline()
    {
        if (_outlineRenderer == null) return;

        bool hasOutline = _owner != CapOwner.Neutral
            && _outlineWidth > 0f
            && _outlineRenderer.sharedMaterial != null;

        _outlineRenderer.enabled = hasOutline;
        if (!hasOutline) return;

        _outlineProperties ??= new MaterialPropertyBlock();
        _outlineRenderer.GetPropertyBlock(_outlineProperties);
        _outlineProperties.SetColor(
            OutlineColorId,
            _owner == CapOwner.Player ? _playerOutlineColor : _opponentOutlineColor);
        _outlineProperties.SetFloat(OutlineWidthId, _outlineWidth);
        _outlineRenderer.SetPropertyBlock(_outlineProperties);
    }

    void Awake()
    {
        _meshRenderer = GetComponent<MeshRenderer>();
        if (_outlineRenderer == null)
        {
            Transform outlineTransform = transform.Find("OutlineRenderer");
            if (outlineTransform != null)
                _outlineRenderer = outlineTransform.GetComponent<MeshRenderer>();
        }

        if (_outlineRenderer != null)
        {
            _outlineRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _outlineRenderer.receiveShadows = false;
            _outlineRenderer.lightProbeUsage = LightProbeUsage.Off;
            _outlineRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        _tuning = CapTuning.Instance;

        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
        }

        ApplyOutline();
    }

    void OnValidate() => ApplyOutline();

    void OnDestroy()
    {
        CapRegistry.Unregister(this);
    }
}

public static class CapRegistry
{
    public static readonly System.Collections.Generic.List<Cap> AllCaps = new();
    public static void Register(Cap cap) { if (!AllCaps.Contains(cap)) AllCaps.Add(cap); }
    public static void Unregister(Cap cap) { AllCaps.Remove(cap); }
}
