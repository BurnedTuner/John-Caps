using UnityEngine;

public class CameraShake : MonoBehaviour
{
    public static CameraShake Instance { get; private set; }

    public Transform CameraTransform;
    public float DefaultShakeAmount = 0.1f;
    public float DefaultShakeDuration = 0.3f;

    private Vector3 _originalLocalPos;
    private float _shakeTimer;
    private float _currentShakeAmount;
    private float _currentShakeDuration;

    void Awake()
    {
        Instance = this;
        if (CameraTransform == null)
            CameraTransform = GetComponentInChildren<Camera>()?.transform;
        
        if (CameraTransform != null)
            _originalLocalPos = CameraTransform.localPosition;
    }

    public void Shake(float amount, float duration)
    {
        _currentShakeAmount = amount;
        _currentShakeDuration = duration;
        _shakeTimer = duration;
    }

    void Update()
    {
        if (_shakeTimer > 0f && CameraTransform != null)
        {
            _shakeTimer -= Time.unscaledDeltaTime;
            float percent = _currentShakeDuration > 0f ? _shakeTimer / _currentShakeDuration : 0f;
            float currentAmount = _currentShakeAmount * percent;
            CameraTransform.localPosition = _originalLocalPos + Random.insideUnitSphere * currentAmount;
        }
        else if (CameraTransform != null && CameraTransform.localPosition != _originalLocalPos)
        {
            CameraTransform.localPosition = _originalLocalPos;
        }
    }
}
