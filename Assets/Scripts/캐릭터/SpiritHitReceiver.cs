using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class SpiritHitReceiver : NetworkBehaviour
{
    public const string ReducedScreenEffectsPreferenceKey =
        "Personal.ReducedScreenEffects";

    public static bool UseReducedScreenEffects =>
        PlayerPrefs.GetInt(
            ReducedScreenEffectsPreferenceKey,
            0
        ) != 0;

    public static void SetReducedScreenEffects(bool reduced)
    {
        PlayerPrefs.SetInt(
            ReducedScreenEffectsPreferenceKey,
            reduced ? 1 : 0
        );
        PlayerPrefs.Save();
    }

    public static SpiritHitReceiver LocalInstance
    {
        get;
        private set;
    }

    public event Action<int, int> HealthChanged;
    public event Action<bool, bool> SuppressionChanged;

    [Header("Health")]
    [SerializeField]
    private int maxHealth = 3;

    [Header("Outside Hit")]
    [SerializeField]
    private float outsideKnockbackDistance =
        0.6f;

    [SerializeField]
    private float outsideActionLockDuration =
        0.15f;

    [SerializeField, Range(0f, 1f)]
    private float outsideHitMoveSpeedMultiplier =
        0.75f;

    [Header("Inside Defeat")]
    [SerializeField]
    private float ownerSuppressionDuration =
        5f;

    [Min(0f)]
    [SerializeField]
    private float postSuppressionInvulnerabilityDuration =
        1.5f;

    [SerializeField]
    private Color suppressedColor =
        new Color(
            0.35f,
            0.45f,
            0.65f,
            1f
        );

    [Range(0f, 1f)]
    [SerializeField]
    private float suppressedColorStrength = 0.75f;

    [Range(0f, 1f)]
    [SerializeField]
    private float suppressedLocalVolumeWeight = 0.72f;

    [Range(0f, 1f)]
    [SerializeField]
    private float suppressedLocalVolumeMinimumWeight = 0.38f;

    [Min(0.1f)]
    [SerializeField]
    private float suppressedLocalVolumePulseDuration = 1.35f;

    [SerializeField]
    private Color suppressedVignetteColor = Color.black;

    [Range(0f, 1f)]
    [SerializeField]
    private float suppressedVignetteIntensity = 0.62f;

    [Range(0f, 1f)]
    [SerializeField]
    private float suppressedVignetteSmoothness = 0.78f;

    [Range(0f, 1f)]
    [SerializeField]
    private float suppressedChromaticIntensity = 0.03f;

    [SerializeField]
    private float suppressedPostExposure = -0.55f;

    [SerializeField]
    private float suppressedSaturation = -30f;

    [SerializeField, Range(0f, 1f)]
    private float hitAlphaMultiplier = 0.5f;

    private bool isHitLocked;
    private Coroutine hitLockCoroutine;
    private Coroutine suppressionStateCoroutine;
    private int outsideCurseDollHitCount;
    private float localOutsideHitSlowUntil;
    private double suppressionEndServerTime;
    private double damageInvulnerabilityEndServerTime;
    private double lastAcceptedHitServerTime =
        double.NegativeInfinity;

    [Header("Hit Visual")]
    [SerializeField]
    private Renderer[] visualRenderers;

    [SerializeField]
    private string colorPropertyName = "_BaseColor";

    [SerializeField]
    private Color hitColor =
        new Color(
            1f,
            0.15f,
            0.15f,
            1f
        );

    [Range(0f, 1f)]
    [SerializeField]
    private float hitColorStrength = 0.65f;

    [SerializeField]
    private float hitFlashDuration = 0.12f;

    [Min(0f)]
    [SerializeField]
    private float roleActionInterruptGraceDuration = 1f;

    [Header("World Hit Sound")]
    [Tooltip("실제 피격이 서버에서 승인됐을 때 모든 클라이언트에서 재생할 월드 적중음 AudioSource입니다.")]
    [SerializeField]
    private AudioSource hitImpactAudioSource;

    [Tooltip("무기가 영체에 실제로 적중했을 때 재생할 공용 적중음입니다.")]
    [SerializeField]
    private AudioClip hitImpactClip;

    [Header("Local Received Hit Feedback")]
    [Tooltip("피격자 본인에게만 활성화할 전용 Global Volume입니다. Profile에 Vignette, Chromatic Aberration 등을 설정합니다.")]
    [SerializeField]
    private Volume receivedHitVolume;

    [Range(0f, 1f)]
    [SerializeField]
    private float receivedHitVolumeWeight = 1f;

    [Min(0f)]
    [SerializeField]
    private float receivedHitVolumeFadeInDuration = 0.025f;

    [Min(0f)]
    [SerializeField]
    private float receivedHitVolumeHoldDuration = 0.035f;

    [Min(0.01f)]
    [SerializeField]
    private float receivedHitVolumeFadeOutDuration = 0.16f;

    private readonly NetworkVariable<int>
        currentHealth =
            new NetworkVariable<int>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<bool>
        suppressionActive =
            new NetworkVariable<bool>(
                false,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<double>
        ownerSuppressionEndServerTime =
            new NetworkVariable<double>(
                0d,
                NetworkVariableReadPermission.Owner,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<double>
        ownerDamageInvulnerabilityEndServerTime =
            new NetworkVariable<double>(
                0d,
                NetworkVariableReadPermission.Owner,
                NetworkVariableWritePermission.Server
            );

    private PlayerSpirit playerSpirit;
    private SpiritRoleActionInteractor roleActionInteractor;
    private SpiritDoorInteraction doorInteraction;

    private int colorPropertyId;

    private Color[] originalColors;
    private bool[] validRenderers;

    private MaterialPropertyBlock
        propertyBlock;

    private Coroutine
        hitFlashCoroutine;

    private Coroutine
        localReceivedHitFeedbackCoroutine;

    private Coroutine
        localSuppressionVolumeCoroutine;

    private Vignette receivedHitVignette;
    private ChromaticAberration receivedHitChromaticAberration;
    private ColorAdjustments receivedHitColorAdjustments;
    private Color originalVignetteColor;
    private float originalVignetteIntensity;
    private float originalVignetteSmoothness;
    private float originalChromaticIntensity;
    private float originalPostExposure;
    private float originalSaturation;
    private bool hasCachedReceivedHitVolumeSettings;

    public int CurrentHealth =>
        currentHealth.Value;

    public int MaxHealth =>
        maxHealth;

    public bool IsSuppressed =>
        suppressionActive.Value;

    public bool IsIncapacitatedForNight =>
        playerSpirit != null &&
        playerSpirit.IsIncapacitatedForNight;

    public bool IsConfinedForNight =>
        playerSpirit != null &&
        playerSpirit.IsConfinedForNight;

    public double LocalSuppressionRemainingSeconds =>
        GetLocalRemainingSeconds(
            ownerSuppressionEndServerTime.Value
        );

    public double
        LocalDamageInvulnerabilityRemainingSeconds =>
            GetLocalRemainingSeconds(
                ownerDamageInvulnerabilityEndServerTime.Value
            );

    public double LocalDoorLockRemainingSeconds =>
        doorInteraction != null
            ? doorInteraction
                .LocalHitDoorLockRemainingSeconds
            : 0d;

    public float LocalMovementSpeedMultiplier =>
        IsOwner &&
        Time.time < localOutsideHitSlowUntil
            ? outsideHitMoveSpeedMultiplier
            : 1f;

    public bool HasRecentAcceptedHitForRoleAction
    {
        get
        {
            if (!IsServer ||
                NetworkManager == null ||
                double.IsNegativeInfinity(
                    lastAcceptedHitServerTime))
            {
                return false;
            }

            return NetworkManager.ServerTime.Time -
                   lastAcceptedHitServerTime <=
                   Mathf.Max(
                       0f,
                       roleActionInterruptGraceDuration
                   );
        }
    }

    public void ResetHealthForFinalDuel()
    {
        if (!IsServer)
            return;

        ClearSuppressionState();

        if (hitLockCoroutine != null)
        {
            StopCoroutine(hitLockCoroutine);
            hitLockCoroutine = null;
        }

        isHitLocked = false;
        outsideCurseDollHitCount = 0;
        lastAcceptedHitServerTime =
            double.NegativeInfinity;
        currentHealth.Value = maxHealth;
    }

    public void ResetHealthForNewNight()
    {
        if (!IsServer)
            return;

        ClearSuppressionState();

        if (hitLockCoroutine != null)
        {
            StopCoroutine(hitLockCoroutine);
            hitLockCoroutine = null;
        }

        isHitLocked = false;
        currentHealth.Value = maxHealth;
        outsideCurseDollHitCount = 0;
        lastAcceptedHitServerTime =
            double.NegativeInfinity;
    }

    public void ClearSuppressionForPhaseEnd()
    {
        if (!IsServer)
            return;

        ClearSuppressionState();
    }

    private void ClearSuppressionState()
    {
        if (!IsServer)
            return;

        if (suppressionStateCoroutine != null)
        {
            StopCoroutine(suppressionStateCoroutine);
            suppressionStateCoroutine = null;
        }

        suppressionEndServerTime = 0d;
        damageInvulnerabilityEndServerTime = 0d;
        ownerSuppressionEndServerTime.Value = 0d;
        ownerDamageInvulnerabilityEndServerTime.Value = 0d;
        suppressionActive.Value = false;
    }

    private void Awake()
    {
        playerSpirit =
            GetComponent<PlayerSpirit>();

        roleActionInteractor =
            GetComponent<SpiritRoleActionInteractor>();

        doorInteraction =
            GetComponent<SpiritDoorInteraction>();

        colorPropertyId =
            Shader.PropertyToID(
                colorPropertyName
            );

        propertyBlock =
            new MaterialPropertyBlock();

        originalColors =
            new Color[
                visualRenderers.Length
            ];

        validRenderers =
            new bool[
                visualRenderers.Length
            ];

        for (int i = 0;
             i < visualRenderers.Length;
             i++)
        {
            Renderer targetRenderer =
                visualRenderers[i];

            if (targetRenderer == null ||
                targetRenderer.sharedMaterial ==
                    null ||
                !targetRenderer.sharedMaterial
                    .HasProperty(
                        colorPropertyId))
            {
                continue;
            }

            validRenderers[i] =
                true;

            originalColors[i] =
                targetRenderer
                    .sharedMaterial
                    .GetColor(
                        colorPropertyId
                    );
        }
    }

    public override void OnNetworkSpawn()
    {
        currentHealth.OnValueChanged +=
            OnCurrentHealthChanged;

        suppressionActive.OnValueChanged +=
            OnSuppressionActiveChanged;

        if (IsOwner)
            LocalInstance = this;

        if (IsServer)
        {
            currentHealth.Value =
                maxHealth;

            outsideCurseDollHitCount = 0;
            suppressionActive.Value = false;
            suppressionEndServerTime = 0d;
            damageInvulnerabilityEndServerTime = 0d;
            ownerSuppressionEndServerTime.Value = 0d;
            ownerDamageInvulnerabilityEndServerTime.Value = 0d;
        }

        localOutsideHitSlowUntil = 0f;

        ConfigureHitFeedback();
        ApplySuppressionPresentation(
            suppressionActive.Value
        );

        if (playerSpirit != null)
        {
            playerSpirit.CurrentHouseChanged +=
                OnCurrentHouseChanged;
        }
    }

    public override void OnNetworkDespawn()
    {
        currentHealth.OnValueChanged -=
            OnCurrentHealthChanged;

        suppressionActive.OnValueChanged -=
            OnSuppressionActiveChanged;

        if (LocalInstance == this)
            LocalInstance = null;

        if (playerSpirit != null)
        {
            playerSpirit.CurrentHouseChanged -=
                OnCurrentHouseChanged;
        }

        if (hitFlashCoroutine != null)
        {
            StopCoroutine(
                hitFlashCoroutine
            );

            hitFlashCoroutine = null;
        }

        if (hitLockCoroutine != null)
        {
            StopCoroutine(
                hitLockCoroutine
            );

            hitLockCoroutine = null;
        }

        if (suppressionStateCoroutine != null)
        {
            StopCoroutine(
                suppressionStateCoroutine
            );

            suppressionStateCoroutine = null;
        }

        StopLocalReceivedHitFeedback();

        if (hitImpactAudioSource != null)
            hitImpactAudioSource.Stop();

        isHitLocked = false;
        outsideCurseDollHitCount = 0;
        localOutsideHitSlowUntil = 0f;
        suppressionEndServerTime = 0d;
        damageInvulnerabilityEndServerTime = 0d;

        ApplyBaseColor(false);
    }

    private IEnumerator HitLockRoutine()
    {
        yield return new WaitForSeconds(
            hitFlashDuration
        );

        isHitLocked = false;
        hitLockCoroutine = null;
    }

    public void ReceiveDamage(
    int damage,
    ulong attackerClientId,
    Vector3 attackerPosition)
    {
        if (!IsServer ||
            damage <= 0 ||
            playerSpirit == null ||
            playerSpirit
                .IsIncapacitatedForNight ||
            isHitLocked ||
            suppressionActive.Value ||
            NetworkManager == null ||
            NetworkManager.ServerTime.Time <
                damageInvulnerabilityEndServerTime)
        {
            return;
        }

        if (MatchManager.Instance == null ||
            !MatchManager.Instance
                .TryGetPlayerSpirit(
                    attackerClientId,
                    out PlayerSpirit
                        attackerSpirit
                ) ||
            !attackerSpirit.CanAct ||
            attackerSpirit
                .IsIncapacitatedForNight)
        {
            return;
        }

        int attackerHouseId =
            attackerSpirit.CurrentHouseId;

        int targetHouseId =
            playerSpirit.CurrentHouseId;

        bool bothOutside =
            attackerHouseId < 0 &&
            targetHouseId < 0;

        bool bothInsideSameHouse =
            attackerHouseId >= 0 &&
            attackerHouseId ==
            targetHouseId;

        if (!bothOutside &&
            !bothInsideSameHouse)
        {
            return;
        }

        isHitLocked = true;
        lastAcceptedHitServerTime =
            NetworkManager.ServerTime.Time;

        if (hitLockCoroutine != null)
        {
            StopCoroutine(
                hitLockCoroutine
            );
        }

        hitLockCoroutine =
            StartCoroutine(
                HitLockRoutine()
            );

        SendAcceptedHitFeedback(
            attackerClientId
        );

        doorInteraction
            ?.ApplyHitDoorLockAfterHit();

        /*
         * 외부 피격:
         * 하나의 네트워크 이벤트에서
         * 붉은 플래시와 넉백을 함께 실행한다.
         */
        if (bothOutside)
        {
            Vector3 knockbackDirection =
                playerSpirit.ApplyOutsideHit(
                    attackerPosition,
                    outsideActionLockDuration
                );

            PlayOutsideHitEffectRpc(
                knockbackDirection,
                outsideKnockbackDistance,
                outsideActionLockDuration
            );

            RegisterOutsideCurseDollCarrierHit();

            bool isFinalDuelHit =
                MatchManager.Instance != null &&
                MatchManager.Instance.IsFinalDuelOpponent(
                    attackerClientId,
                    OwnerClientId
                );

            if (isFinalDuelHit)
            {
                currentHealth.Value =
                    Mathf.Max(
                        0,
                        currentHealth.Value -
                        damage
                    );

                Debug.Log(
                    $"Final Duel Damaged - " +
                    $"Target: {OwnerClientId}, " +
                    $"Attacker: {attackerClientId}, " +
                    $"Health: {currentHealth.Value}/" +
                    $"{maxHealth}"
                );

                if (currentHealth.Value <= 0)
                {
                    MatchManager.Instance
                        .ResolveFinalDuelDefeat(
                            OwnerClientId,
                            attackerClientId
                        );
                }

                return;
            }

            if (!IsOutsideSuppressionTarget())
            {
                Debug.Log(
                    $"Outside Spirit Hit - " +
                    $"Target: {OwnerClientId}, " +
                    $"Attacker: {attackerClientId}"
                );

                return;
            }

            currentHealth.Value =
                Mathf.Max(
                    0,
                    currentHealth.Value -
                    damage
                );

            Debug.Log(
                $"Drunkard Outside Damaged - " +
                $"Target: {OwnerClientId}, " +
                $"Attacker: {attackerClientId}, " +
                $"Health: {currentHealth.Value}/" +
                $"{maxHealth}"
            );

            if (currentHealth.Value > 0)
                return;

            currentHealth.Value = maxHealth;

            BeginSuppression(
                ownerSuppressionDuration
            );

            MatchManager.Instance
                ?.ReturnCurseDollAfterSuppression(
                    OwnerClientId
                );

            Debug.Log(
                $"Drunkard Outside Suppressed - " +
                $"Client: {OwnerClientId}, " +
                $"Duration: {ownerSuppressionDuration}"
            );

            return;
        }

        /*
         * 집 안 피격은 현재 규칙상 넉백 없이
         * 플래시와 체력 피해만 적용한다.
         */

        PlayHitFlashRpc();

        if (MatchManager.Instance != null)
            MatchManager.Instance.RegisterNightBloodEvidence(targetHouseId, OwnerClientId);

        if (currentHealth.Value <= 0)
            return;

        currentHealth.Value =
            Mathf.Max(
                0,
                currentHealth.Value -
                damage
            );

        Debug.Log(
            $"Inside Spirit Damaged - " +
            $"Target: {OwnerClientId}, " +
            $"Attacker: {attackerClientId}, " +
            $"Health: {currentHealth.Value}/" +
            $"{maxHealth}, " +
            $"House: {targetHouseId}"
        );

        if (currentHealth.Value > 0)
        {
            return;
        }

        ResolveInsideDefeat();
    }

    private bool IsOutsideSuppressionTarget()
    {
        return MatchManager.Instance != null &&
               MatchManager.Instance.TryGetPlayerMatchState(
                   OwnerClientId,
                   out PlayerMatchState state
               ) &&
               state.isAlive &&
               state.role == RoleId.Drunkard;
    }

    private void BeginSuppression(
        float duration)
    {
        if (!IsServer ||
            playerSpirit == null ||
            NetworkManager == null)
        {
            return;
        }

        float suppressionDuration =
            Mathf.Max(0f, duration);

        suppressionEndServerTime =
            NetworkManager.ServerTime.Time +
            suppressionDuration;

        damageInvulnerabilityEndServerTime =
            suppressionEndServerTime +
            Mathf.Max(
                0f,
                postSuppressionInvulnerabilityDuration
            );

        ownerSuppressionEndServerTime.Value =
            suppressionEndServerTime;

        ownerDamageInvulnerabilityEndServerTime.Value =
            damageInvulnerabilityEndServerTime;

        suppressionActive.Value = true;

        if (suppressionStateCoroutine != null)
        {
            StopCoroutine(
                suppressionStateCoroutine
            );
        }

        suppressionStateCoroutine =
            StartCoroutine(
                SuppressionStateRoutine()
            );

        playerSpirit.ApplySuppression(
            suppressionDuration
        );

    }

    private IEnumerator SuppressionStateRoutine()
    {
        while (IsSpawned &&
               NetworkManager != null &&
               NetworkManager.ServerTime.Time <
                   suppressionEndServerTime)
        {
            yield return null;
        }

        suppressionStateCoroutine = null;

        if (!IsServer || !IsSpawned)
            yield break;

        suppressionActive.Value = false;

    }

    private void RegisterOutsideCurseDollCarrierHit()
    {
        if (!IsServer ||
            MatchManager.Instance == null ||
            !MatchManager.Instance.IsCurseDollHeldBy(
                OwnerClientId))
        {
            outsideCurseDollHitCount = 0;
            return;
        }

        outsideCurseDollHitCount++;

        if (outsideCurseDollHitCount <
            Mathf.Max(1, maxHealth))
        {
            return;
        }

        outsideCurseDollHitCount = 0;

        MatchManager.Instance
            .ReturnCurseDollAfterOutsideHits(
                OwnerClientId
            );
    }

    private void ResolveInsideDefeat()
    {
        int currentHouseId =
            playerSpirit.CurrentHouseId;

        currentHealth.Value =
            maxHealth;

        /*
         * 자기 집에서 패배하면
         * 기존처럼 현재 위치에서 5초 제압한다.
         */
        if (currentHouseId ==
            playerSpirit.HomeHouseId)
        {
            BeginSuppression(
                ownerSuppressionDuration
            );

            MatchManager.Instance
                ?.ReturnCurseDollAfterSuppression(
                    OwnerClientId
                );

            Debug.Log(
                $"Resident Suppressed - " +
                $"Client: {OwnerClientId}, " +
                $"House: {currentHouseId}, " +
                $"Duration: {ownerSuppressionDuration}"
            );

            return;
        }

        /*
         * 타인의 집에서 패배하면
         * 해당 집 외부 케이지로 이동한다.
         */
        if (MatchManager.Instance == null)
        {
            Debug.LogError(
                "MatchManager가 없습니다."
            );

            return;
        }

        House currentHouse =
            MatchManager.Instance.GetHouse(
                currentHouseId
            );

        if (currentHouse == null)
        {
            Debug.LogError(
                $"House {currentHouseId}를 찾지 못했습니다."
            );

            return;
        }

        Transform cagePoint =
            currentHouse.OutsideCagePoint;

        if (cagePoint == null)
        {
            Debug.LogError(
                $"House {currentHouseId}의 " +
                "OutsideCagePoint가 연결되지 않았습니다."
            );

            return;
        }

        playerSpirit.ExpelForNight(
            cagePoint.position,
            cagePoint.rotation,
            currentHouseId
        );

        Debug.Log(
            $"Intruder Confined - " +
            $"Client: {OwnerClientId}, " +
            $"House: {currentHouseId}"
        );
    }

    private void OnCurrentHouseChanged(
        int previousHouseId,
        int newHouseId)
    {
        if (!IsServer ||
            previousHouseId ==
            newHouseId)
        {
            return;
        }

        // 저주 인형 운반 피격 횟수는
        // 집 안팎의 전투 구간을 서로 이어서 계산하지 않는다.
        outsideCurseDollHitCount = 0;
    }

    private void OnSuppressionActiveChanged(
        bool previous,
        bool current)
    {
        ApplySuppressionPresentation(current);
        SuppressionChanged?.Invoke(
            previous,
            current
        );
    }

    private void OnCurrentHealthChanged(
        int previous,
        int current)
    {
        HealthChanged?.Invoke(
            previous,
            current
        );
    }

    private double GetLocalRemainingSeconds(
        double endServerTime)
    {
        if (!IsOwner ||
            NetworkManager == null)
        {
            return 0d;
        }

        return System.Math.Max(
            0d,
            endServerTime -
            NetworkManager.ServerTime.Time
        );
    }

    private void ApplySuppressionPresentation(
        bool suppressed)
    {
        if (hitFlashCoroutine == null)
            ApplyBaseColor(suppressed);

        if (!IsOwner ||
            receivedHitVolume == null)
        {
            return;
        }

        if (suppressed)
        {
            if (localReceivedHitFeedbackCoroutine !=
                null)
            {
                StopCoroutine(
                    localReceivedHitFeedbackCoroutine
                );

                localReceivedHitFeedbackCoroutine =
                    null;
            }

            StopLocalSuppressionVolumePulse();
            ApplySuppressionVolumeSettings();
            receivedHitVolume.enabled = true;

            localSuppressionVolumeCoroutine =
                StartCoroutine(
                    LocalSuppressionVolumeRoutine()
                );

            return;
        }

        StopLocalSuppressionVolumePulse();
        RestoreReceivedHitVolumeSettings();

        if (localReceivedHitFeedbackCoroutine == null)
            receivedHitVolume.weight = 0f;
    }

    private void SendAcceptedHitFeedback(
        ulong attackerClientId)
    {
        if (!IsServer ||
            NetworkManager == null)
        {
            return;
        }

        PlayAcceptedHitSoundRpc();

        if (NetworkManager.ConnectedClients
                .ContainsKey(OwnerClientId))
        {
            PlayReceivedHitVisualFeedbackRpc(
                RpcTarget.Single(
                    OwnerClientId,
                    RpcTargetUse.Temp
                )
            );
        }

        if (attackerClientId !=
                OwnerClientId &&
            NetworkManager.ConnectedClients
                .ContainsKey(attackerClientId))
        {
            PlayHitConfirmedVisualFeedbackRpc(
                RpcTarget.Single(
                    attackerClientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    [Rpc(SendTo.Everyone)]
    private void PlayAcceptedHitSoundRpc()
    {
        if (hitImpactAudioSource == null ||
            hitImpactClip == null)
        {
            return;
        }

        hitImpactAudioSource.PlayOneShot(
            hitImpactClip
        );
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission =
            RpcInvokePermission.Server
    )]
    private void PlayReceivedHitVisualFeedbackRpc(
        RpcParams rpcParams = default)
    {
        roleActionInteractor
            ?.CancelCurrentRoleActionFromHit();

        PlayLocalReceivedHitVisualFeedback();

        SpiritFirstPersonCamera.LocalInstance
            ?.PlayLocalHitReaction();
    }

    private void ConfigureHitFeedback()
    {
        if (hitImpactAudioSource == null)
        {
            hitImpactAudioSource =
                gameObject.AddComponent<AudioSource>();

            hitImpactAudioSource.spatialBlend = 1f;
            hitImpactAudioSource.minDistance = 1.5f;
            hitImpactAudioSource.maxDistance = 10f;
            hitImpactAudioSource.rolloffMode =
                AudioRolloffMode.Linear;
        }

        hitImpactAudioSource.playOnAwake = false;
        hitImpactAudioSource.loop = false;

        if (receivedHitVolume != null)
        {
            receivedHitVolume.weight = 0f;
            receivedHitVolume.enabled = IsOwner;

            if (IsOwner)
                CacheReceivedHitVolumeSettings();
        }
    }

    private void PlayLocalReceivedHitVisualFeedback()
    {
        if (!IsOwner ||
            receivedHitVolume == null ||
            suppressionActive.Value)
        {
            return;
        }

        if (localReceivedHitFeedbackCoroutine !=
            null)
        {
            StopCoroutine(
                localReceivedHitFeedbackCoroutine
            );
        }

        receivedHitVolume.enabled = true;

        localReceivedHitFeedbackCoroutine =
            StartCoroutine(
                LocalReceivedHitFeedbackRoutine()
            );
    }

    private IEnumerator
        LocalReceivedHitFeedbackRoutine()
    {
        float effectStrength = GetLocalScreenEffectStrength();
        float peakWeight =
            Mathf.Clamp01(
                receivedHitVolumeWeight *
                effectStrength
            );

        float fadeInDuration =
            Mathf.Max(
                0f,
                receivedHitVolumeFadeInDuration
            );

        float holdDuration =
            Mathf.Max(
                0f,
                receivedHitVolumeHoldDuration
            );

        float fadeOutDuration =
            Mathf.Max(
                0.01f,
                receivedHitVolumeFadeOutDuration
            );

        receivedHitVolume.weight = 0f;

        if (fadeInDuration <= 0f)
        {
            receivedHitVolume.weight =
                peakWeight;
        }
        else
        {
            float elapsed = 0f;

            while (elapsed < fadeInDuration)
            {
                elapsed +=
                    Time.unscaledDeltaTime;

                receivedHitVolume.weight =
                    Mathf.Lerp(
                        0f,
                        peakWeight,
                        Mathf.Clamp01(
                            elapsed /
                            fadeInDuration
                        )
                    );

                yield return null;
            }
        }

        receivedHitVolume.weight =
            peakWeight;

        if (holdDuration > 0f)
        {
            yield return
                new WaitForSecondsRealtime(
                    holdDuration
                );
        }

        float fadeOutElapsed = 0f;

        while (fadeOutElapsed <
               fadeOutDuration)
        {
            fadeOutElapsed +=
                Time.unscaledDeltaTime;

            receivedHitVolume.weight =
                Mathf.Lerp(
                    peakWeight,
                    suppressionActive.Value
                        ? Mathf.Clamp01(
                            suppressedLocalVolumeWeight *
                            effectStrength
                        )
                        : 0f,
                    Mathf.Clamp01(
                        fadeOutElapsed /
                        fadeOutDuration
                    )
                );

            yield return null;
        }

        receivedHitVolume.weight =
            suppressionActive.Value
                ? Mathf.Clamp01(
                    suppressedLocalVolumeWeight *
                    effectStrength
                )
                : 0f;

        localReceivedHitFeedbackCoroutine =
            null;
    }

    private void StopLocalReceivedHitFeedback()
    {
        if (localReceivedHitFeedbackCoroutine !=
            null)
        {
            StopCoroutine(
                localReceivedHitFeedbackCoroutine
            );

            localReceivedHitFeedbackCoroutine =
                null;
        }

        StopLocalSuppressionVolumePulse();
        RestoreReceivedHitVolumeSettings();

        if (receivedHitVolume != null)
        {
            receivedHitVolume.weight = 0f;
            receivedHitVolume.enabled = false;
        }

    }

    private void CacheReceivedHitVolumeSettings()
    {
        if (receivedHitVolume == null ||
            hasCachedReceivedHitVolumeSettings)
        {
            return;
        }

        VolumeProfile runtimeProfile =
            receivedHitVolume.profile;

        if (runtimeProfile == null)
            return;

        runtimeProfile.TryGet(
            out receivedHitVignette
        );

        runtimeProfile.TryGet(
            out receivedHitChromaticAberration
        );

        runtimeProfile.TryGet(
            out receivedHitColorAdjustments
        );

        if (receivedHitVignette != null)
        {
            originalVignetteColor =
                receivedHitVignette.color.value;

            originalVignetteIntensity =
                receivedHitVignette.intensity.value;

            originalVignetteSmoothness =
                receivedHitVignette.smoothness.value;
        }

        if (receivedHitChromaticAberration != null)
        {
            originalChromaticIntensity =
                receivedHitChromaticAberration
                    .intensity.value;
        }

        if (receivedHitColorAdjustments != null)
        {
            originalPostExposure =
                receivedHitColorAdjustments
                    .postExposure.value;

            originalSaturation =
                receivedHitColorAdjustments
                    .saturation.value;
        }

        hasCachedReceivedHitVolumeSettings = true;
    }

    private void ApplySuppressionVolumeSettings()
    {
        CacheReceivedHitVolumeSettings();

        if (receivedHitVignette != null)
        {
            receivedHitVignette.color.value =
                suppressedVignetteColor;

            receivedHitVignette.intensity.value =
                Mathf.Clamp01(
                    suppressedVignetteIntensity
                );

            receivedHitVignette.smoothness.value =
                Mathf.Clamp01(
                    suppressedVignetteSmoothness
                );
        }

        if (receivedHitChromaticAberration != null)
        {
            receivedHitChromaticAberration
                .intensity.value =
                    Mathf.Clamp01(
                        suppressedChromaticIntensity
                    );
        }

        if (receivedHitColorAdjustments != null)
        {
            receivedHitColorAdjustments
                .postExposure.value =
                    suppressedPostExposure;

            receivedHitColorAdjustments
                .saturation.value =
                    suppressedSaturation;
        }
    }

    private void RestoreReceivedHitVolumeSettings()
    {
        if (!hasCachedReceivedHitVolumeSettings)
            return;

        if (receivedHitVignette != null)
        {
            receivedHitVignette.color.value =
                originalVignetteColor;

            receivedHitVignette.intensity.value =
                originalVignetteIntensity;

            receivedHitVignette.smoothness.value =
                originalVignetteSmoothness;
        }

        if (receivedHitChromaticAberration != null)
        {
            receivedHitChromaticAberration
                .intensity.value =
                    originalChromaticIntensity;
        }

        if (receivedHitColorAdjustments != null)
        {
            receivedHitColorAdjustments
                .postExposure.value =
                    originalPostExposure;

            receivedHitColorAdjustments
                .saturation.value =
                    originalSaturation;
        }
    }

    private IEnumerator LocalSuppressionVolumeRoutine()
    {
        float minimumWeight =
            Mathf.Clamp01(
                Mathf.Min(
                    suppressedLocalVolumeMinimumWeight,
                    suppressedLocalVolumeWeight
                )
            );

        float maximumWeight =
            Mathf.Clamp01(
                Mathf.Max(
                    suppressedLocalVolumeMinimumWeight,
                    suppressedLocalVolumeWeight
                )
            );

        float pulseDuration =
            Mathf.Max(
                0.1f,
                suppressedLocalVolumePulseDuration
            );

        float elapsed = 0f;

        while (IsOwner &&
               suppressionActive.Value &&
               receivedHitVolume != null)
        {
            float pulse =
                0.5f -
                0.5f * Mathf.Cos(
                    elapsed /
                    pulseDuration *
                    Mathf.PI * 2f
                );

            receivedHitVolume.weight =
                Mathf.Lerp(
                    minimumWeight,
                    maximumWeight,
                    pulse
                ) * GetLocalScreenEffectStrength();

            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (receivedHitVolume != null)
            receivedHitVolume.weight = 0f;

        localSuppressionVolumeCoroutine = null;
    }

    private static float GetLocalScreenEffectStrength()
    {
        return UseReducedScreenEffects ? 0.55f : 1f;
    }

    private void StopLocalSuppressionVolumePulse()
    {
        if (localSuppressionVolumeCoroutine ==
            null)
        {
            return;
        }

        StopCoroutine(
            localSuppressionVolumeCoroutine
        );

        localSuppressionVolumeCoroutine = null;
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission =
            RpcInvokePermission.Server
    )]
    private void PlayHitConfirmedVisualFeedbackRpc(
        RpcParams rpcParams = default)
    {
        SpiritWeaponSwing.LocalInstance
            ?.PlayLocalHitConfirmReaction();
    }

    [Rpc(SendTo.Everyone)]
    private void PlayHitFlashRpc()
    {
        StartHitFlash();
    }

    [Rpc(SendTo.Everyone)]
    private void PlayOutsideHitEffectRpc(
        Vector3 knockbackDirection,
        float knockbackDistance,
        float movementSlowDuration)
    {
        /*
         * 모든 화면에서 피격 색상을 동시에 시작한다.
         */
        StartHitFlash();

        /*
         * 실제 위치 변경은 대상 영체의
         * 소유 클라이언트만 수행한다.
         */
        if (IsOwner && playerSpirit != null)
        {
            localOutsideHitSlowUntil =
                Mathf.Max(
                    localOutsideHitSlowUntil,
                    Time.time +
                    Mathf.Max(
                        0f,
                        movementSlowDuration
                    )
                );

            playerSpirit.ApplyLocalKnockback(knockbackDirection, knockbackDistance);
        }
    }

    private void StartHitFlash()
    {
        if (IsOwner)
        {
            roleActionInteractor
                ?.CancelCurrentRoleActionFromHit();
        }

        if (hitFlashCoroutine != null)
        {
            StopCoroutine(
                hitFlashCoroutine
            );
        }

        hitFlashCoroutine =
            StartCoroutine(
                HitFlashRoutine()
            );
    }

    private IEnumerator HitFlashRoutine()
    {
        SetHitColor();

        yield return new WaitForSeconds(
            hitFlashDuration
        );

        RestoreOriginalColor();

        hitFlashCoroutine = null;
    }

    private void SetHitColor()
    {
        for (int i = 0;
             i < visualRenderers.Length;
             i++)
        {
            if (!validRenderers[i])
            {
                continue;
            }

            Renderer targetRenderer =
                visualRenderers[i];

            Color flashColor =
                Color.Lerp(
                    originalColors[i],
                    hitColor,
                    hitColorStrength
                );

            flashColor.a =
                Mathf.Clamp01(
                    originalColors[i].a *
                    hitAlphaMultiplier
                );

            targetRenderer.GetPropertyBlock(
                propertyBlock
            );

            propertyBlock.SetColor(
                colorPropertyId,
                flashColor
            );

            targetRenderer.SetPropertyBlock(
                propertyBlock
            );
        }
    }

    private void RestoreOriginalColor()
    {
        ApplyBaseColor(
            suppressionActive.Value
        );
    }

    private void ApplyBaseColor(
        bool suppressed)
    {
        for (int i = 0;
             i < visualRenderers.Length;
             i++)
        {
            if (!validRenderers[i])
            {
                continue;
            }

            Renderer targetRenderer =
                visualRenderers[i];

            targetRenderer.GetPropertyBlock(
                propertyBlock
            );

            Color targetColor =
                originalColors[i];

            if (suppressed)
            {
                targetColor =
                    Color.Lerp(
                        originalColors[i],
                        suppressedColor,
                        Mathf.Clamp01(
                            suppressedColorStrength
                        )
                    );

                targetColor.a =
                    originalColors[i].a;
            }

            propertyBlock.SetColor(
                colorPropertyId,
                targetColor
            );

            targetRenderer.SetPropertyBlock(
                propertyBlock
            );
        }
    }
}
