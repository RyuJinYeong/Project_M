using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(NetworkObject))]
public class SpiritFirstPersonCamera : NetworkBehaviour
{
    public const string MouseSensitivityPreferenceKey =
        "Personal.MouseSensitivity";

    public static SpiritFirstPersonCamera
        LocalInstance
    {
        get;
        private set;
    }

    public static bool
        IsLocalUIInteractionActive =>
            LocalInstance != null &&
            !LocalInstance.cursorLocked;

    public Camera PlayerCamera => playerCamera;

    [Header("카메라 참조")]
    [SerializeField]
    private Transform cameraPitchPivot;

    [SerializeField]
    private Camera playerCamera;

    [SerializeField]
    private AudioListener audioListener;

    [Header("시점 조작")]
    [SerializeField]
    private float mouseSensitivity = 0.12f;

    [SerializeField]
    private float minimumPitch = -80f;

    [SerializeField]
    private float maximumPitch = 80f;

    [Header("1인칭 표시")]
    [Tooltip("자기 시점에서 숨길 영체 몸체 Renderer입니다. 무기 Renderer는 넣지 않습니다.")]
    [SerializeField]
    private Renderer[] localHiddenRenderers;

    [Header("밤 가시거리")]
    [SerializeField]
    private bool useShortNightVisibility = true;

    [Tooltip("ExponentialSquared가 거리 경계를 더 자연스럽게 감춥니다.")]
    [SerializeField]
    private FogMode nightFogMode =
        FogMode.ExponentialSquared;

    [Min(0f)]
    [SerializeField]
    private float nightFogStartDistance = 8f;

    [Min(0.1f)]
    [SerializeField]
    private float nightFogEndDistance = 28f;

    [Range(0.001f, 0.2f)]
    [SerializeField]
    private float nightFogDensity = 0.035f;

    [Tooltip("밤에도 월드 외곽 바리게이트가 실제로 렌더링되도록 보장할 최소 원거리 클리핑 거리입니다.")]
    [Min(10f)]
    [SerializeField]
    private float nightMinimumFarClipPlane = 80f;

    [Tooltip("밤에는 스카이박스 대신 안개색 배경을 사용해 원거리 경계가 드러나는 것을 막습니다.")]
    [SerializeField]
    private bool hideSkyboxAtNight = true;

    [SerializeField]
    private Color nightFogColor =
        new Color(
            0.015f,
            0.018f,
            0.025f,
            1f
        );

    [Header("피격 반응")]
    [Min(0.01f)]
    [SerializeField]
    private float hitShakeDuration = 0.12f;

    [Min(0f)]
    [SerializeField]
    private float hitShakeDistance = 0.035f;

    private float currentPitch;
    private bool cursorLocked;
    private bool altUiInteractionHeld;

    private Coroutine cameraActivationCoroutine;
    private Coroutine hitShakeCoroutine;

    private Vector3 originalCameraLocalPosition;
    private bool originalCameraPositionCached;

    private MatchManager matchManager;

    private bool originalVisibilityCached;
    private float originalFarClipPlane;
    private bool originalFogEnabled;
    private Color originalFogColor;
    private FogMode originalFogMode;
    private float originalFogStartDistance;
    private float originalFogEndDistance;
    private float originalFogDensity;

    private CameraClearFlags
        originalCameraClearFlags;

    private Color
        originalCameraBackgroundColor;

    private void Awake()
    {
        mouseSensitivity = Mathf.Clamp(
            PlayerPrefs.GetFloat(
                MouseSensitivityPreferenceKey,
                mouseSensitivity
            ),
            0.04f,
            0.4f
        );

        CacheCameraReferences();
        CacheOriginalVisibilitySettings();
        SetCameraActive(false);
    }

    public float MouseSensitivity => mouseSensitivity;

    public void SetMouseSensitivity(float sensitivity)
    {
        mouseSensitivity = Mathf.Clamp(
            sensitivity,
            0.04f,
            0.4f
        );

        PlayerPrefs.SetFloat(
            MouseSensitivityPreferenceKey,
            mouseSensitivity
        );
        PlayerPrefs.Save();
    }

    private void OnEnable()
    {
        if (!IsSpawned)
            return;

        ApplyOwnershipState();

        if (IsOwner)
        {
            LocalInstance = this;

            TryBindMatchManager();
            ApplyCurrentVisibilityState();
        }
    }

    private void OnDisable()
    {
        if (!IsOwner)
            return;

        StopHitShake();
        altUiInteractionHeld = false;

        if (LocalInstance == this)
            LocalInstance = null;

        UnbindMatchManager();
        RestoreOriginalVisibilitySettings();
    }

    public override void OnNetworkSpawn()
    {
        ApplyOwnershipState();

        if (IsOwner)
        {
            LocalInstance = this;

            TryBindMatchManager();
            ApplyCurrentVisibilityState();

            cameraActivationCoroutine =
                StartCoroutine(
                    EnsureLocalCameraRoutine()
                );
        }
    }

    public override void OnNetworkDespawn()
    {
        if (cameraActivationCoroutine != null)
        {
            StopCoroutine(cameraActivationCoroutine);
            cameraActivationCoroutine = null;
        }

        SetCameraActive(false);
        SetLocalBodyVisible(true);

        if (IsOwner)
        {
            StopHitShake();

            altUiInteractionHeld = false;

            if (LocalInstance == this)
                LocalInstance = null;

            UnbindMatchManager();
            RestoreOriginalVisibilitySettings();
            UnlockCursor();
        }
    }

    private IEnumerator EnsureLocalCameraRoutine()
    {
        for (int i = 0; i < 3; i++)
        {
            yield return null;

            if (!IsSpawned || !IsOwner)
                yield break;

            ApplyOwnershipState();
            TryBindMatchManager();
            ApplyCurrentVisibilityState();
        }

        cameraActivationCoroutine = null;
    }

    private void CacheCameraReferences()
    {
        if (playerCamera == null)
        {
            playerCamera =
                GetComponentInChildren<Camera>(true);
        }

        if (audioListener == null &&
            playerCamera != null)
        {
            audioListener =
                playerCamera.GetComponent<AudioListener>();
        }

        if (playerCamera != null &&
            !originalCameraPositionCached)
        {
            originalCameraLocalPosition =
                playerCamera.transform.localPosition;

            originalCameraPositionCached = true;
        }
    }

    private void ApplyOwnershipState()
    {
        CacheCameraReferences();

        bool isLocalPlayer = IsOwner;

        SetCameraActive(isLocalPlayer);
        SetLocalBodyVisible(!isLocalPlayer);

        if (!isLocalPlayer)
            return;

        DisableExistingMainCamera();

        if (playerCamera != null)
            playerCamera.gameObject.tag = "MainCamera";

        LockCursor();
    }

    private void Update()
    {
        if (!IsOwner ||
            !IsSpawned)
        {
            return;
        }

        if (matchManager == null)
        {
            TryBindMatchManager();
            ApplyCurrentVisibilityState();
        }

        HandleCursorInput();

        if (!cursorLocked)
        {
            return;
        }

        UpdateCameraRotation();
    }

    private void HandleCursorInput()
    {
        Keyboard keyboard =
            Keyboard.current;

        Mouse mouse =
            Mouse.current;

        if (MafiaGame.UI.MafiaUIController
            .IsMatchStatusBoardOpen)
        {
            if (cursorLocked)
                UnlockCursor();

            return;
        }

        bool altPressed =
            keyboard != null &&
            (
                keyboard.leftAltKey.isPressed ||
                keyboard.rightAltKey.isPressed
            );

        if (altPressed)
        {
            if (!altUiInteractionHeld)
            {
                altUiInteractionHeld = true;
                UnlockCursor();
            }

            return;
        }

        if (altUiInteractionHeld)
        {
            altUiInteractionHeld = false;
            LockCursor();
            return;
        }

        if (keyboard != null &&
            keyboard.escapeKey
                .wasPressedThisFrame)
        {
            UnlockCursor();
            return;
        }

        if (!cursorLocked &&
            mouse != null &&
            mouse.leftButton
                .wasPressedThisFrame)
        {
            LockCursor();
        }
    }

    private void UpdateCameraRotation()
    {
        if (Mouse.current == null ||
            cameraPitchPivot == null)
        {
            return;
        }

        Vector2 mouseDelta =
            Mouse.current.delta
                .ReadValue();

        float yawAmount =
            mouseDelta.x *
            mouseSensitivity;

        float pitchAmount =
            mouseDelta.y *
            mouseSensitivity;

        transform.Rotate(
            0f,
            yawAmount,
            0f,
            Space.World
        );

        currentPitch =
            Mathf.Clamp(
                currentPitch -
                pitchAmount,
                minimumPitch,
                maximumPitch
            );

        cameraPitchPivot.localRotation =
            Quaternion.Euler(
                currentPitch,
                0f,
                0f
            );
    }

    public void PlayLocalHitReaction()
    {
        if (!IsOwner ||
            playerCamera == null ||
            hitShakeDistance <= 0f ||
            hitShakeDuration <= 0f)
        {
            return;
        }

        if (hitShakeCoroutine != null)
        {
            StopCoroutine(
                hitShakeCoroutine
            );
        }

        hitShakeCoroutine =
            StartCoroutine(
                HitShakeRoutine()
            );
    }

    private IEnumerator HitShakeRoutine()
    {
        CacheCameraReferences();

        float elapsed = 0f;

        while (elapsed < hitShakeDuration)
        {
            elapsed += Time.unscaledDeltaTime;

            float normalizedTime =
                Mathf.Clamp01(
                    elapsed /
                    hitShakeDuration
                );

            float strength =
                1f - normalizedTime;

            Vector2 randomOffset =
                Random.insideUnitCircle *
                hitShakeDistance *
                strength;

            if (playerCamera != null)
            {
                playerCamera.transform.localPosition =
                    originalCameraLocalPosition +
                    new Vector3(
                        randomOffset.x,
                        randomOffset.y,
                        0f
                    );
            }

            yield return null;
        }

        RestoreCameraLocalPosition();
        hitShakeCoroutine = null;
    }

    private void StopHitShake()
    {
        if (hitShakeCoroutine != null)
        {
            StopCoroutine(
                hitShakeCoroutine
            );

            hitShakeCoroutine = null;
        }

        RestoreCameraLocalPosition();
    }

    private void RestoreCameraLocalPosition()
    {
        if (playerCamera != null &&
            originalCameraPositionCached)
        {
            playerCamera.transform.localPosition =
                originalCameraLocalPosition;
        }
    }

    private void CacheOriginalVisibilitySettings()
    {
        if (originalVisibilityCached)
            return;

        CacheCameraReferences();

        if (playerCamera != null)
        {
            originalFarClipPlane =
                playerCamera.farClipPlane;

            originalCameraClearFlags =
                playerCamera.clearFlags;

            originalCameraBackgroundColor =
                playerCamera.backgroundColor;
        }

        originalFogEnabled =
            RenderSettings.fog;

        originalFogColor =
            RenderSettings.fogColor;

        originalFogMode =
            RenderSettings.fogMode;

        originalFogStartDistance =
            RenderSettings.fogStartDistance;

        originalFogEndDistance =
            RenderSettings.fogEndDistance;

        originalFogDensity =
            RenderSettings.fogDensity;

        originalVisibilityCached = true;
    }

    private void TryBindMatchManager()
    {
        if (!IsOwner)
            return;

        MatchManager target =
            MatchManager.Instance != null
                ? MatchManager.Instance
                : FindFirstObjectByType
                    <MatchManager>();

        if (target == null ||
            matchManager == target)
        {
            return;
        }

        UnbindMatchManager();

        matchManager = target;
        matchManager.PhaseChanged +=
            OnMatchPhaseChanged;
    }

    private void UnbindMatchManager()
    {
        if (matchManager == null)
            return;

        matchManager.PhaseChanged -=
            OnMatchPhaseChanged;

        matchManager = null;
    }

    private void OnMatchPhaseChanged(
        MatchPhase previous,
        MatchPhase current)
    {
        ApplyVisibilityForPhase(current);
    }

    private void ApplyCurrentVisibilityState()
    {
        if (!IsOwner)
            return;

        if (matchManager == null)
        {
            RestoreOriginalVisibilitySettings();
            return;
        }

        ApplyVisibilityForPhase(
            matchManager.CurrentPhase
        );
    }

    private void ApplyVisibilityForPhase(
        MatchPhase phase)
    {
        if (!IsOwner ||
            !useShortNightVisibility)
        {
            RestoreOriginalVisibilitySettings();
            return;
        }

        bool isNight =
            phase == MatchPhase.NightPreparation ||
            phase == MatchPhase.NightAction ||
            phase == MatchPhase.NightResult;

        if (!isNight)
        {
            RestoreOriginalVisibilitySettings();
            return;
        }

        CacheOriginalVisibilitySettings();

        float fogStart =
            Mathf.Max(
                0f,
                nightFogStartDistance
            );

        float fogEnd =
            Mathf.Max(
                fogStart + 0.1f,
                nightFogEndDistance
            );

        /*
         * 기존 문제는 Far Clip을 20여 미터로 줄여
         * 바리게이트가 안개에 가려지기 전에 렌더링에서
         * 잘리고, 그 뒤의 스카이박스가 노출된 것이었다.
         *
         * 밤에도 원래 카메라 거리보다 짧아지지 않게 하고,
         * 최소 거리도 충분히 확보한다.
         */
        float safeFarClip =
            Mathf.Max(
                originalFarClipPlane,
                nightMinimumFarClipPlane,
                fogEnd + 20f
            );

        if (playerCamera != null)
        {
            playerCamera.farClipPlane =
                safeFarClip;

            if (hideSkyboxAtNight)
            {
                playerCamera.clearFlags =
                    CameraClearFlags.SolidColor;

                playerCamera.backgroundColor =
                    nightFogColor;
            }
        }

        RenderSettings.fog = true;
        RenderSettings.fogMode =
            nightFogMode;

        RenderSettings.fogColor =
            nightFogColor;

        if (nightFogMode ==
            FogMode.Linear)
        {
            RenderSettings.fogStartDistance =
                fogStart;

            RenderSettings.fogEndDistance =
                fogEnd;
        }
        else
        {
            RenderSettings.fogDensity =
                Mathf.Max(
                    0.001f,
                    nightFogDensity
                );
        }
    }

    private void RestoreOriginalVisibilitySettings()
    {
        if (!originalVisibilityCached)
            return;

        if (playerCamera != null)
        {
            playerCamera.farClipPlane =
                originalFarClipPlane;

            playerCamera.clearFlags =
                originalCameraClearFlags;

            playerCamera.backgroundColor =
                originalCameraBackgroundColor;
        }

        RenderSettings.fog =
            originalFogEnabled;

        RenderSettings.fogColor =
            originalFogColor;

        RenderSettings.fogMode =
            originalFogMode;

        RenderSettings.fogStartDistance =
            originalFogStartDistance;

        RenderSettings.fogEndDistance =
            originalFogEndDistance;

        RenderSettings.fogDensity =
            originalFogDensity;
    }

    private void SetCameraActive(
        bool active)
    {
        if (playerCamera != null)
        {
            playerCamera.enabled =
                active;
        }

        if (audioListener != null)
        {
            audioListener.enabled =
                active;
        }
    }

    private void SetLocalBodyVisible(
        bool visible)
    {
        if (localHiddenRenderers == null)
        {
            return;
        }

        for (int i = 0;
             i < localHiddenRenderers.Length;
             i++)
        {
            Renderer targetRenderer =
                localHiddenRenderers[i];

            if (targetRenderer != null)
            {
                targetRenderer.enabled =
                    visible;
            }
        }
    }

    private void DisableExistingMainCamera()
    {
        Camera[] cameras =
            FindObjectsByType<Camera>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

        for (int i = 0;
             i < cameras.Length;
             i++)
        {
            Camera targetCamera =
                cameras[i];

            if (targetCamera == null ||
                targetCamera ==
                playerCamera ||
                !targetCamera.CompareTag(
                    "MainCamera"))
            {
                continue;
            }

            targetCamera.enabled =
                false;

            AudioListener targetListener =
                targetCamera
                    .GetComponent<AudioListener>();

            if (targetListener != null)
            {
                targetListener.enabled =
                    false;
            }
        }
    }

    private void LockCursor()
    {
        Cursor.lockState =
            CursorLockMode.Locked;

        Cursor.visible =
            false;

        cursorLocked =
            true;
    }

    private void UnlockCursor()
    {
        Cursor.lockState =
            CursorLockMode.None;

        Cursor.visible =
            true;

        cursorLocked =
            false;
    }
}
