using UnityEngine;

public class CameraShake : MonoBehaviour
{
    public static CameraShake Instance { get; private set; }

    public Transform CameraTransform;
    public float DefaultShakeAmount = 0.1f;
    public float DefaultShakeDuration = 0.3f;

    private float _shakeTimer;
    private float _currentShakeAmount;
    private float _currentShakeDuration;

    void Awake()
    {
        Instance = this;
        if (CameraTransform == null)
            CameraTransform = GetComponentInChildren<Camera>()?.transform;
    }

    public void Shake(float amount, float duration)
    {
        if (_shakeTimer > 0f && amount < _currentShakeAmount)
            return;
        _currentShakeAmount = amount;
        _currentShakeDuration = duration;
        _shakeTimer = duration;
    }

    public void ShakeDefault()
    {
        Shake(DefaultShakeAmount, DefaultShakeDuration);
    }

    void LateUpdate()
    {
        if (CameraTransform == null) return;
        if (_shakeTimer <= 0f) return;

        _shakeTimer -= Time.unscaledDeltaTime;
        float percent = _currentShakeDuration > 0f ? _shakeTimer / _currentShakeDuration : 0f;
        float currentAmount = _currentShakeAmount * percent;

        CameraTransform.position += Random.insideUnitSphere * currentAmount;
    }
}