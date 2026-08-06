using UnityEngine;

/// <summary>
/// All tunable parameters for the cap prototype, plus a fallback source for cap materials.
/// Throw force and cap radius are NOT here — they come from each cap's own CapParameters.
/// </summary>
public class CapTuning : MonoBehaviour
{
    public static CapTuning Instance { get; private set; }

    [Header("Materials (fallback — used when a cap's own material is null)")]
    public Material HeadsMaterial;
    public Material TailsMaterial;

    [Header("Cap geometry")]
    [Min(0.01f)] public float CapThickness = 0.1f;

    [Header("Throw / Spawn")]
    public Transform SpawnPoint;
    [Min(0f)] public float MinimumDragDistance = 0.5f;
    [Tooltip("Screen-space radius (in pixels) around the waiting cap where the player must press to start a drag-throw.")]
    [Min(10f)] public float CapGrabRadiusPixels = 80f;
    [Tooltip("How high the waiting cap lifts (world units) when grabbed.")]
    [Min(0f)] public float GrabLiftHeight = 1f;
    [Tooltip("How fast the cap lifts (units/second).")]
    [Min(0.01f)] public float GrabLiftSpeed = 5f;
    [Min(0f)] public float ArcHeight = 3f;
    [Min(0.05f)] public float FlightDuration = 0.8f;
    [Min(0f)] public float FlightSpinDegrees = 540f;

    [Header("Force → motion")]
    [Min(0.01f)] public float ForceToTravelDistance = 1f;
    [Min(0.01f)] public float CapMoveSpeed = 5f;
    [Min(0f)] public float MinimumFlightLength = 0.3f;
    [Min(0.05f)] public float ChainFlightDuration = 0.4f;

    [Header("Chain reaction")]
    [Range(0f, 1f)] public float ChainDeflection = 0.65f;
    [Min(0f)] public float ChainContactDelay = 0.08f;
    [Range(1, 64)] public int MaximumChainLength = 24;

    [Header("Flip animation (launched cap)")]
    [Min(0.05f)] public float CapFlipDuration = 0.52f;
    [Min(0f)] public float CapFlipApexHeight = 0.65f;

    [Header("Settle")]
    [Min(0f)] public float SettleDelay = 0.22f;

    [Header("Prediction / preview")]
    [Range(0, 16)] public int PredictionDepth = 4;
    [Range(8, 64)] public int ArcSamples = 24;

    void Awake() => Instance = this;

    public Vector3 SpawnPosition => SpawnPoint != null ? SpawnPoint.position : new Vector3(0f, 0f, -8f);
}