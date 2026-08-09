using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Orbit camera rig. Rotates around a target point.
/// WASD pans the target horizontally (limited by coords).
/// Mouse wheel zooms. Hold RMB to orbit. Press T to reset to top-down.
/// </summary>
public class CameraController : MonoBehaviour
{
    [Header("Target & Orbit")]
    public Transform orbitTarget;
    public Vector3 orbitCenter = Vector3.zero;

    [Tooltip("Starting distance from target")]
    public float distance = 15f;
    public float minDistance = 5f;
    public float maxDistance = 30f;

    [Header("Movement (Panning)")]
    public float panSpeed = 10f;
    [Tooltip("X axis: time (seconds). Y axis: speed multiplier 0-1.")]
    public AnimationCurve accelerationCurve = AnimationCurve.EaseInOut(0, 0, 2, 1);

    [Header("Pan Limits (World Coordinates)")]
    public bool limitPan = true;
    public Vector3 minPan = new Vector3(-15f, 0f, -15f);
    public Vector3 maxPan = new Vector3(15f, 0f, 15f);

    [Header("Rotation (RMB + Mouse)")]
    public float lookSensitivity = 0.3f;
    public float minPitch = 10f;
    public float maxPitch = 85f;

    [Header("Zoom (Mouse Wheel)")]
    public float zoomSpeed = 5f;

    [Header("Reset (T Key)")]
    public float resetDuration = 0.5f;
    public float resetYaw = 0f;
    public float resetPitch = 85f;
    public float resetDistance = 15f;

    private Camera _camera;
    private Vector3 _forward;
    private Vector3 _right;
    private float _accelTimer = 0f;
    private Vector3 _currentInputDir;

    private float _yaw;
    private float _pitch;
    private float _currentDistance;

    private bool _isResetting = false;
    private float _resetTimer = 0f;
    private float _startYaw, _startPitch, _startDistance;
    private Vector3 _startCenter;

    private Vector3 _currentOrbitCenter;

    void Awake()
    {
        _camera = GetComponentInChildren<Camera>();
        if (_camera == null) _camera = Camera.main;

        if (orbitTarget != null)
            _currentOrbitCenter = orbitTarget.position;
        else
            _currentOrbitCenter = orbitCenter;

        Vector3 dir = _camera.transform.position - _currentOrbitCenter;
        _currentDistance = dir.magnitude;
        if (_currentDistance < 0.1f) _currentDistance = distance;

        _yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        _pitch = Mathf.Asin(dir.y / _currentDistance) * Mathf.Rad2Deg;

        UpdateCameraTransform();
        UpdateDirectionVectors();
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (kb.tKey.wasPressedThisFrame)
        {
            StartReset();
        }

        if (_isResetting)
        {
            UpdateReset();
            return;
        }

        if (mouse.rightButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue();
            if (delta.sqrMagnitude > 0.001f)
            {
                _yaw += delta.x * lookSensitivity;
                _pitch -= delta.y * lookSensitivity;
                _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
            }
        }

        Vector2 scroll = mouse.scroll.ReadValue();
        if (Mathf.Abs(scroll.y) > 0.001f)
        {
            float direction = Mathf.Sign(scroll.y);
            _currentDistance -= direction * zoomSpeed * Time.deltaTime;
            _currentDistance = Mathf.Clamp(_currentDistance, minDistance, maxDistance);
        }

        UpdateCameraTransform();
        UpdateDirectionVectors();
    }

    void FixedUpdate()
    {
        if (_isResetting) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        float dt = Time.fixedDeltaTime;

        _currentInputDir = Vector3.zero;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) _currentInputDir.z += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) _currentInputDir.z -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) _currentInputDir.x += 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) _currentInputDir.x -= 1f;

        if (_currentInputDir.sqrMagnitude > 0.001f)
        {
            _currentInputDir.Normalize();
            _accelTimer += dt;
        }
        else
        {
            _accelTimer = 0f;
        }

        if (_accelTimer > 0f)
        {
            float curveTime = Mathf.Min(_accelTimer, accelerationCurve.keys[accelerationCurve.length - 1].time);
            float speedMultiplier = accelerationCurve.Evaluate(curveTime);
            float currentSpeed = panSpeed * speedMultiplier;

            Vector3 move = (_right * _currentInputDir.x + _forward * _currentInputDir.z) * currentSpeed * dt;
            _currentOrbitCenter += move;
        }

        // Apply Pan Limits to the orbit center
        if (limitPan)
        {
            _currentOrbitCenter.x = Mathf.Clamp(_currentOrbitCenter.x, minPan.x, maxPan.x);
            _currentOrbitCenter.z = Mathf.Clamp(_currentOrbitCenter.z, minPan.z, maxPan.z);
        }

        UpdateCameraTransform();
    }

    void UpdateCameraTransform()
    {
        Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
        Vector3 offset = rot * Vector3.forward * _currentDistance;
        _camera.transform.position = _currentOrbitCenter - offset;
        _camera.transform.LookAt(_currentOrbitCenter);
    }

    void UpdateDirectionVectors()
    {
        _forward = _camera.transform.forward;
        _forward.y = 0f;
        if (_forward.sqrMagnitude > 0.001f) _forward.Normalize();
        else _forward = Vector3.forward;

        _right = _camera.transform.right;
        _right.y = 0f;
        if (_right.sqrMagnitude > 0.001f) _right.Normalize();
        else _right = Vector3.right;
    }

    void StartReset()
    {
        _isResetting = true;
        _resetTimer = 0f;
        _startYaw = _yaw;
        _startPitch = _pitch;
        _startDistance = _currentDistance;
        _startCenter = _currentOrbitCenter;
    }

    void UpdateReset()
    {
        _resetTimer += Time.deltaTime;
        float t = Mathf.Clamp01(_resetTimer / resetDuration);
        float easedT = t * t * (3f - 2f * t);

        _yaw = Mathf.LerpAngle(_startYaw, resetYaw, easedT);
        _pitch = Mathf.Lerp(_startPitch, resetPitch, easedT);
        _currentDistance = Mathf.Lerp(_startDistance, resetDistance, easedT);

        Vector3 targetCenter = orbitTarget != null ? orbitTarget.position : orbitCenter;
        _currentOrbitCenter = Vector3.Lerp(_startCenter, targetCenter, easedT);

        UpdateCameraTransform();
        UpdateDirectionVectors();

        if (t >= 1f)
        {
            _isResetting = false;
            _yaw = resetYaw;
            _pitch = resetPitch;
            _currentDistance = resetDistance;
            _currentOrbitCenter = targetCenter;
        }
    }
}