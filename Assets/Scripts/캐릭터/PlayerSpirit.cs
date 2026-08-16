using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using MafiaGame.UI;

public enum SpiritLifeState : byte
{
    Alive,
    RestrictedDeadSpectator,
    FreeDeadSpectator
}

[RequireComponent(typeof(NetworkObject))]
public class PlayerSpirit : NetworkBehaviour
{
    [Header("Control")]
    [SerializeField] private Behaviour[] controlBehaviours;
    [SerializeField] private CharacterController characterController;

    [Header("Local Weapon")]
    [SerializeField] private GameObject weaponRoot;

    [Header("Spirit Visibility")]
    [Tooltip("영체 본체 Renderer입니다. 비어 있으면 Weapon Root 하위 Renderer를 제외하고 자동으로 찾습니다.")]
    [SerializeField] private Renderer[] spiritBodyRenderers;

    [Tooltip("영체 머리 위의 닉네임 표시 컴포넌트입니다.")]
    [SerializeField] private PlayerIdentityLabel overheadIdentityLabel;

    [Tooltip("안정도에 따른 원격 영체 거리 표시를 갱신하는 간격입니다.")]
    [Min(0.02f)]
    [SerializeField] private float distanceVisibilityUpdateInterval = 0.1f;

    [Tooltip("식별 거리 끝에서 URP 디더 크로스페이드가 진행되는 거리입니다.")]
    [Min(0.5f)]
    [SerializeField] private float distanceVisibilityFadeWidth = 4f;

    [Tooltip("거리 밖에서 숨겨진 이름표가 다시 나타날 때 사용하는 완충 거리입니다.")]
    [Min(0f)]
    [SerializeField] private float distanceVisibilityHysteresis = 2f;

    private readonly NetworkVariable<ulong> linkedClientId = new NetworkVariable<ulong>(
        ulong.MaxValue,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<int> homeHouseId = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<int> currentHouseId = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> canAct = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> finalDuelCombatant = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> outsideHitMovementAllowed = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> incapacitatedForNight = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<SpiritConfinementType> confinementType = new NetworkVariable<SpiritConfinementType>(
        SpiritConfinementType.None,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<int> confinedHouseId = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<SpiritLifeState> lifeState = new NetworkVariable<SpiritLifeState>(
        SpiritLifeState.Alive,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<int> spectatorReleaseDay = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> nightIdentityRevealed = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Coroutine controlLockCoroutine;
    private Coroutine bindMatchManagerCoroutine;
    private MatchManager boundMatchManager;
    private double controlLockedUntil;
    private LODGroup distanceVisibilityLodGroup;
    private Renderer[] distanceVisibilityRenderers;
    private float nextDistanceVisibilityUpdateTime;
    private float configuredDistanceVisibilityRange = -1f;
    private float configuredDistanceVisibilityFieldOfView = -1f;
    private bool distanceVisibilityLimited;
    private bool distanceIdentityVisible = true;

    public event Action<int, int> CurrentHouseChanged;
    public event Action<SpiritLifeState, SpiritLifeState> LifeStateChanged;
    public event Action<bool, bool> NightIdentityRevealedChanged;
    public event Action<bool> NightVoicePermissionChanged;

    public ulong LinkedClientId => linkedClientId.Value;
    public int HomeHouseId => homeHouseId.Value;
    public int CurrentHouseId => currentHouseId.Value;
    public bool CanAct => canAct.Value;
    public bool IsFinalDuelCombatant => finalDuelCombatant.Value;
    public bool IsIncapacitatedForNight =>
        incapacitatedForNight.Value ||
        IsDeadSpectator;
    public bool IsConfinedForNight => confinementType.Value != SpiritConfinementType.None;
    public SpiritConfinementType ConfinementType => confinementType.Value;
    public int ConfinedHouseId => confinedHouseId.Value;
    public bool IsInsideHouse => currentHouseId.Value >= 0;
    public bool IsInsideOwnHouse => currentHouseId.Value >= 0 && currentHouseId.Value == homeHouseId.Value;

    public SpiritLifeState LifeState => lifeState.Value;
    public int SpectatorReleaseDay => spectatorReleaseDay.Value;
    public bool IsAlive => lifeState.Value == SpiritLifeState.Alive;
    public bool IsDeadSpectator => lifeState.Value != SpiritLifeState.Alive;
    public bool IsRestrictedDeadSpectator => lifeState.Value == SpiritLifeState.RestrictedDeadSpectator;
    public bool IsFreeDeadSpectator => lifeState.Value == SpiritLifeState.FreeDeadSpectator;
    public bool IsNightIdentityRevealed => nightIdentityRevealed.Value;

    public bool CanMove =>
        canAct.Value ||
        outsideHitMovementAllowed.Value;
    public bool CanAttack =>
        canAct.Value &&
        IsAlive &&
        !incapacitatedForNight.Value;

    public bool CanUseDoor =>
        canAct.Value &&
        !finalDuelCombatant.Value &&
        (
            IsFreeDeadSpectator ||
            IsAlive && !incapacitatedForNight.Value
        );

    public bool CanUseRoleAction =>
        canAct.Value &&
        !finalDuelCombatant.Value &&
        IsAlive &&
        !incapacitatedForNight.Value;

    public bool CanUseHouseLamp => CanUseRoleAction;
    public bool CanUseNightVoice => IsAlive && nightIdentityRevealed.Value;
    public bool IsLocallyVisibleByDistance =>
        !distanceVisibilityLimited ||
        distanceIdentityVisible;

    public override void OnNetworkSpawn()
    {
        currentHouseId.OnValueChanged += OnCurrentHouseChanged;
        canAct.OnValueChanged += OnCanActChanged;
        finalDuelCombatant.OnValueChanged += OnFinalDuelCombatantChanged;
        outsideHitMovementAllowed.OnValueChanged += OnOutsideHitMovementAllowedChanged;
        lifeState.OnValueChanged += OnLifeStateChanged;
        nightIdentityRevealed.OnValueChanged += OnNightIdentityRevealedChanged;

        CacheSpiritBodyRenderers();
        SetControlBehaviours(canAct.Value);
        SetWeaponVisible(false);
        RefreshLocalPresentation();

        bindMatchManagerCoroutine =
            StartCoroutine(BindMatchManagerRoutine());
    }

    public override void OnNetworkDespawn()
    {
        SetControlBehaviours(false);

        currentHouseId.OnValueChanged -= OnCurrentHouseChanged;
        canAct.OnValueChanged -= OnCanActChanged;
        finalDuelCombatant.OnValueChanged -= OnFinalDuelCombatantChanged;
        outsideHitMovementAllowed.OnValueChanged -= OnOutsideHitMovementAllowedChanged;
        lifeState.OnValueChanged -= OnLifeStateChanged;
        nightIdentityRevealed.OnValueChanged -= OnNightIdentityRevealedChanged;

        UnbindMatchManager();
        StopControlLock();

        if (bindMatchManagerCoroutine != null)
        {
            StopCoroutine(bindMatchManagerCoroutine);
            bindMatchManagerCoroutine = null;
        }
    }

    private void Update()
    {
        if (!IsSpawned || IsOwner)
            return;

        if (Time.unscaledTime <
            nextDistanceVisibilityUpdateTime)
        {
            return;
        }

        nextDistanceVisibilityUpdateTime =
            Time.unscaledTime +
            Mathf.Max(
                0.02f,
                distanceVisibilityUpdateInterval
            );

        RefreshDistanceVisibility();
    }

    public void Initialize(ulong targetClientId, int assignedHouseId)
    {
        if (!IsServer)
            return;

        linkedClientId.Value = targetClientId;
        homeHouseId.Value = assignedHouseId;
        currentHouseId.Value = assignedHouseId;
        incapacitatedForNight.Value = false;
        confinementType.Value = SpiritConfinementType.None;
        confinedHouseId.Value = -1;
        lifeState.Value = SpiritLifeState.Alive;
        spectatorReleaseDay.Value = -1;
        nightIdentityRevealed.Value = false;
        finalDuelCombatant.Value = false;
        outsideHitMovementAllowed.Value = false;
        canAct.Value = false;
    }

    public void SetCurrentHouse(int newHouseId)
    {
        if (!IsServer ||
            currentHouseId.Value == newHouseId)
        {
            return;
        }

        currentHouseId.Value =
            newHouseId;
    }

    public void SetPhaseControlEnabled(bool enabled)
    {
        if (!IsServer)
            return;

        StopControlLock();
        finalDuelCombatant.Value = false;

        if (enabled)
        {
            incapacitatedForNight.Value = false;
            ClearConfinementState();
            canAct.Value = true;
            SetWeaponVisibleRpc(IsAlive);
            return;
        }

        canAct.Value = false;
        SetWeaponVisibleRpc(false);
    }

    public void SetFinalDuelControlEnabled(bool enabled)
    {
        if (!IsServer)
            return;

        StopControlLock();
        finalDuelCombatant.Value = enabled;

        if (enabled)
        {
            incapacitatedForNight.Value = false;
            ClearConfinementState();
            canAct.Value = true;
            SetWeaponVisibleRpc(IsAlive);
            return;
        }

        canAct.Value = false;
        SetWeaponVisibleRpc(false);
    }

    public void SetFinalDuelPreparationState()
    {
        if (!IsServer)
            return;

        StopControlLock();
        finalDuelCombatant.Value = true;
        incapacitatedForNight.Value = false;
        ClearConfinementState();
        canAct.Value = false;
        SetWeaponVisibleRpc(false);
    }

    public void ReturnHomeForMorning(Vector3 position, Quaternion rotation)
    {
        if (!IsServer)
            return;

        StopControlLock();

        incapacitatedForNight.Value = false;
        ClearConfinementState();
        canAct.Value = false;
        nightIdentityRevealed.Value = false;

        ForceTeleportRpc(position, rotation);
        SetCurrentHouse(homeHouseId.Value);
        SetWeaponVisibleRpc(false);
    }

    public void EnterDeadSpectator(
        Vector3 position,
        Quaternion rotation,
        int releaseDay,
        bool enableMovementNow)
    {
        if (!IsServer)
            return;

        StopControlLock();

        incapacitatedForNight.Value = false;
        ClearConfinementState();
        lifeState.Value = SpiritLifeState.RestrictedDeadSpectator;
        spectatorReleaseDay.Value = Mathf.Max(1, releaseDay);
        nightIdentityRevealed.Value = false;
        canAct.Value = enableMovementNow;

        ForceTeleportRpc(position, rotation);
        SetCurrentHouse(homeHouseId.Value);
        SetWeaponVisibleRpc(false);
    }

    public bool TryReleaseDeadSpectator(int day)
    {
        if (!IsServer ||
            lifeState.Value != SpiritLifeState.RestrictedDeadSpectator ||
            spectatorReleaseDay.Value < 0 ||
            day < spectatorReleaseDay.Value)
        {
            return false;
        }

        lifeState.Value = SpiritLifeState.FreeDeadSpectator;
        spectatorReleaseDay.Value = -1;
        return true;
    }

    public void SetNightIdentityRevealed(bool revealed)
    {
        if (!IsServer)
            return;

        bool validReveal =
            revealed &&
            lifeState.Value == SpiritLifeState.Alive;

        if (nightIdentityRevealed.Value != validReveal)
            nightIdentityRevealed.Value = validReveal;
    }

    public Vector3 ApplyOutsideHit(
        Vector3 attackerPosition,
        float actionLockDuration)
    {
        if (!IsServer || IsDeadSpectator || incapacitatedForNight.Value)
            return Vector3.zero;

        Vector3 knockbackDirection = transform.position - attackerPosition;
        knockbackDirection.y = 0f;

        if (knockbackDirection.sqrMagnitude < 0.001f)
            knockbackDirection = -transform.forward;

        knockbackDirection.Normalize();

        outsideHitMovementAllowed.Value =
            actionLockDuration > 0f;
        LockControl(actionLockDuration);

        return knockbackDirection;
    }

    public void ApplyLocalKnockback(Vector3 direction, float distance)
    {
        if (!IsOwner || distance <= 0f)
            return;

        direction.y = 0f;

        if (direction.sqrMagnitude < 0.001f)
            return;

        direction.Normalize();

        Vector3 movement = direction * distance;

        if (characterController != null && characterController.enabled)
        {
            characterController.Move(movement);
            return;
        }

        transform.position += movement;
    }

    public void ApplySuppression(float duration)
    {
        if (!IsServer || IsDeadSpectator || incapacitatedForNight.Value)
            return;

        outsideHitMovementAllowed.Value = false;
        LockControl(duration);
    }

    public void ExpelForNight(Vector3 outsidePosition, Quaternion outsideRotation, int confinedAtHouseId)
    {
        ConfineForNight(
            outsidePosition,
            outsideRotation,
            confinedAtHouseId,
            SpiritConfinementType.ExternalCage
        );
    }

    public void ConfineInsideForNight(Vector3 position, Quaternion rotation, int confinedAtHouseId)
    {
        ConfineForNight(
            position,
            rotation,
            confinedAtHouseId,
            SpiritConfinementType.InternalCage
        );
    }

    private void ConfineForNight(
        Vector3 position,
        Quaternion rotation,
        int confinedAtHouseId,
        SpiritConfinementType newConfinementType)
    {
        if (!IsServer ||
            IsDeadSpectator ||
            newConfinementType == SpiritConfinementType.None)
        {
            return;
        }

        incapacitatedForNight.Value = true;
        outsideHitMovementAllowed.Value = false;
        confinementType.Value = newConfinementType;
        confinedHouseId.Value = confinedAtHouseId;

        /*
         * 감옥 안에서 이동은 가능하게 유지한다.
         * 공격·문·귀환·직업 행동은 각 컴포넌트에서
         * IsIncapacitatedForNight를 확인해 차단한다.
         */
        canAct.Value = true;
        controlLockedUntil = double.PositiveInfinity;

        if (controlLockCoroutine != null)
        {
            StopCoroutine(controlLockCoroutine);
            controlLockCoroutine = null;
        }

        ForceTeleportRpc(position, rotation);
        SetCurrentHouse(
            newConfinementType == SpiritConfinementType.InternalCage
                ? confinedAtHouseId
                : -1
        );
    }

    public void TeleportThroughFrontDoor(Vector3 targetPosition, Quaternion targetRotation, int targetHouseId)
    {
        if (!IsServer || incapacitatedForNight.Value)
            return;

        ForceTeleportRpc(targetPosition, targetRotation);
        SetCurrentHouse(targetHouseId);
    }

    public void ResetForNewNight()
    {
        if (!IsServer)
            return;

        StopControlLock();

        incapacitatedForNight.Value = false;
        outsideHitMovementAllowed.Value = false;
        ClearConfinementState();
        canAct.Value = true;

        SetWeaponVisibleRpc(IsAlive);
    }

    private void ClearConfinementState()
    {
        confinementType.Value = SpiritConfinementType.None;
        confinedHouseId.Value = -1;
    }

    private void LockControl(float duration)
    {
        if (!IsServer || duration <= 0f)
            return;

        double requestedEndTime = NetworkManager.ServerTime.Time + duration;

        if (requestedEndTime > controlLockedUntil)
            controlLockedUntil = requestedEndTime;

        canAct.Value = false;

        if (controlLockCoroutine == null)
            controlLockCoroutine = StartCoroutine(ControlLockRoutine());
    }

    private IEnumerator ControlLockRoutine()
    {
        while (NetworkManager.ServerTime.Time < controlLockedUntil)
            yield return null;

        controlLockCoroutine = null;
        outsideHitMovementAllowed.Value = false;

        if (!incapacitatedForNight.Value)
            canAct.Value = true;
    }

    private void StopControlLock()
    {
        controlLockedUntil = 0d;

        if (IsServer &&
            IsSpawned &&
            outsideHitMovementAllowed.Value)
        {
            outsideHitMovementAllowed.Value = false;
        }

        if (controlLockCoroutine == null)
            return;

        StopCoroutine(controlLockCoroutine);
        controlLockCoroutine = null;
    }

    private void OnCurrentHouseChanged(int previousHouseId, int newHouseId)
    {
        CurrentHouseChanged?.Invoke(previousHouseId, newHouseId);

        Debug.Log($"Spirit {OwnerClientId} House: {previousHouseId} -> {newHouseId}");
    }

    private void OnCanActChanged(bool previous, bool current)
    {
        SetControlBehaviours(current);
    }

    private void OnFinalDuelCombatantChanged(bool previous, bool current)
    {
        SetControlBehaviours(canAct.Value);
    }

    private void OnOutsideHitMovementAllowedChanged(
        bool previous,
        bool current)
    {
        SetControlBehaviours(canAct.Value);
    }

    private void OnLifeStateChanged(
        SpiritLifeState previous,
        SpiritLifeState current)
    {
        SetControlBehaviours(canAct.Value);
        RefreshLocalPresentation();

        LifeStateChanged?.Invoke(previous, current);
        NightVoicePermissionChanged?.Invoke(CanUseNightVoice);
    }

    private void OnNightIdentityRevealedChanged(
        bool previous,
        bool current)
    {
        RefreshOverheadIdentity();

        NightIdentityRevealedChanged?.Invoke(previous, current);
        NightVoicePermissionChanged?.Invoke(CanUseNightVoice);
    }

    private void SetControlBehaviours(bool enabledState)
    {
        if (controlBehaviours == null)
            return;

        for (int i = 0; i < controlBehaviours.Length; i++)
        {
            Behaviour targetBehaviour = controlBehaviours[i];

            if (targetBehaviour == null)
                continue;

            if (targetBehaviour is SpiritFirstPersonCamera)
            {
                /*
                 * 카메라는 행동 가능 여부와 무관하게
                 * 로컬 소유 영체에서 항상 유지한다.
                 */
                targetBehaviour.enabled =
                    IsOwner;
                continue;
            }

            bool behaviourEnabled = enabledState;

            if (targetBehaviour is SpiritWeaponSwing)
                behaviourEnabled = CanAttack;
            else if (targetBehaviour is SpiritDoorInteraction)
                behaviourEnabled = CanUseDoor;
            else if (targetBehaviour is SpiritRoleActionInteractor)
                behaviourEnabled = CanUseRoleAction;
            else if (targetBehaviour is SpiritLampInteraction)
                behaviourEnabled = CanUseHouseLamp;
            else if (targetBehaviour is PlayerSpiritMovement)
                behaviourEnabled = CanMove;

            targetBehaviour.enabled = behaviourEnabled;
        }
    }

    private void SetWeaponVisible(bool visible)
    {
        if (weaponRoot == null)
            return;

        weaponRoot.SetActive(visible);
    }

    [Rpc(SendTo.Everyone)]
    private void SetWeaponVisibleRpc(bool visible)
    {
        SetWeaponVisible(visible);
    }

    private IEnumerator BindMatchManagerRoutine()
    {
        while (IsSpawned && MatchManager.Instance == null)
            yield return null;

        bindMatchManagerCoroutine = null;

        if (IsSpawned && MatchManager.Instance != null)
            BindMatchManager(MatchManager.Instance);
    }

    private void BindMatchManager(MatchManager matchManager)
    {
        if (boundMatchManager == matchManager)
            return;

        UnbindMatchManager();

        boundMatchManager = matchManager;
        boundMatchManager.PhaseChanged += OnMatchPhaseChanged;
        boundMatchManager.LocalPlayerMatchStateReceived +=
            OnLocalPlayerMatchStateReceived;
        boundMatchManager.PublicPlayerStatesChanged +=
            OnPublicPlayerStatesChanged;
        boundMatchManager.LocalMafiaMembersChanged +=
            OnLocalMafiaMembersChanged;
        boundMatchManager.LocalSpectatorPlayerStatesChanged +=
            OnLocalSpectatorPlayerStatesChanged;

        RefreshLocalPresentation();
    }

    private void UnbindMatchManager()
    {
        if (boundMatchManager == null)
            return;

        boundMatchManager.PhaseChanged -= OnMatchPhaseChanged;
        boundMatchManager.LocalPlayerMatchStateReceived -=
            OnLocalPlayerMatchStateReceived;
        boundMatchManager.PublicPlayerStatesChanged -=
            OnPublicPlayerStatesChanged;
        boundMatchManager.LocalMafiaMembersChanged -=
            OnLocalMafiaMembersChanged;
        boundMatchManager.LocalSpectatorPlayerStatesChanged -=
            OnLocalSpectatorPlayerStatesChanged;

        boundMatchManager = null;
    }

    private void OnMatchPhaseChanged(
        MatchPhase previous,
        MatchPhase current)
    {
        RefreshDistanceVisibility(true);
        RefreshOverheadIdentity();
    }

    private void OnLocalMafiaMembersChanged()
    {
        RefreshDistanceVisibility(true);
        RefreshOverheadIdentity();
    }

    private void OnLocalSpectatorPlayerStatesChanged()
    {
        RefreshDistanceVisibility(true);
        RefreshOverheadIdentity();
    }

    private void OnLocalPlayerMatchStateReceived(
        PlayerMatchState state)
    {
        RefreshDistanceVisibility(true);
        RefreshLocalPresentation();
    }

    private void OnPublicPlayerStatesChanged()
    {
        RefreshOverheadIdentity();
    }

    private void RefreshLocalPresentation()
    {
        RefreshDistanceVisibility(true);
        RefreshSpiritBodyVisibility();
        RefreshOverheadIdentity();
    }

    private void RefreshDistanceVisibility(
        bool forceRefresh = false)
    {
        if (IsOwner)
            return;

        Camera localCamera =
            SpiritFirstPersonCamera.LocalInstance != null
                ? SpiritFirstPersonCamera.LocalInstance
                    .PlayerCamera
                : null;

        bool shouldLimit =
            boundMatchManager != null &&
            boundMatchManager.CurrentPhase ==
                MatchPhase.NightAction &&
            !boundMatchManager
                .LocalIgnoresVillageStabilityVisibility;

        float visibilityRange = shouldLimit
            ? boundMatchManager
                .LocalSpiritVisibilityDistance
            : float.PositiveInfinity;

        shouldLimit =
            shouldLimit &&
            !float.IsPositiveInfinity(visibilityRange) &&
            localCamera != null;

        if (!shouldLimit)
        {
            SetDistanceVisibilityLimited(false);
            return;
        }

        EnsureDistanceVisibilityLodGroup();

        if (distanceVisibilityLodGroup == null)
            return;

        distanceVisibilityLimited = true;
        distanceVisibilityLodGroup.enabled = true;
        distanceVisibilityLodGroup.ForceLOD(-1);

        if (forceRefresh ||
            !Mathf.Approximately(
                configuredDistanceVisibilityRange,
                visibilityRange
            ) ||
            !Mathf.Approximately(
                configuredDistanceVisibilityFieldOfView,
                localCamera.fieldOfView
            ))
        {
            ConfigureDistanceVisibilityLod(
                localCamera,
                visibilityRange
            );
        }

        float distance = Vector3.Distance(
            localCamera.transform.position,
            transform.position
        );

        bool nextIdentityVisible =
            distanceIdentityVisible;

        if (distanceIdentityVisible &&
            distance > visibilityRange)
        {
            nextIdentityVisible = false;
        }
        else if (!distanceIdentityVisible &&
                 distance < Mathf.Max(
                     0f,
                     visibilityRange -
                     distanceVisibilityHysteresis
                 ))
        {
            nextIdentityVisible = true;
        }

        if (nextIdentityVisible ==
            distanceIdentityVisible)
        {
            return;
        }

        distanceIdentityVisible =
            nextIdentityVisible;

        RefreshOverheadIdentity();
    }

    private void EnsureDistanceVisibilityLodGroup()
    {
        if (distanceVisibilityLodGroup != null)
            return;

        distanceVisibilityRenderers =
            GetComponentsInChildren<Renderer>(true);

        System.Collections.Generic.List<Renderer>
            visualRenderers =
                new System.Collections.Generic.List<Renderer>();

        for (int i = 0;
             i < distanceVisibilityRenderers.Length;
             i++)
        {
            Renderer targetRenderer =
                distanceVisibilityRenderers[i];

            if (targetRenderer == null)
                continue;

            if (overheadIdentityLabel != null &&
                targetRenderer.transform.IsChildOf(
                    overheadIdentityLabel.transform
                ))
            {
                continue;
            }

            visualRenderers.Add(targetRenderer);
        }

        distanceVisibilityRenderers =
            visualRenderers.ToArray();

        distanceVisibilityLodGroup =
            gameObject.AddComponent<LODGroup>();

        distanceVisibilityLodGroup.fadeMode =
            LODFadeMode.CrossFade;
        distanceVisibilityLodGroup.animateCrossFading =
            false;
        distanceVisibilityLodGroup.localReferencePoint =
            new Vector3(0f, 1f, 0f);
        distanceVisibilityLodGroup.size = 2f;
    }

    private void ConfigureDistanceVisibilityLod(
        Camera localCamera,
        float visibilityRange)
    {
        float cullHeight =
            CalculateScreenRelativeHeight(
                localCamera,
                visibilityRange
            );

        float fadeStartDistance =
            Mathf.Max(
                0.5f,
                visibilityRange -
                distanceVisibilityFadeWidth
            );

        float fadeStartHeight =
            CalculateScreenRelativeHeight(
                localCamera,
                fadeStartDistance
            );

        LOD visibleLod = new LOD(
            cullHeight,
            distanceVisibilityRenderers
        );

        visibleLod.fadeTransitionWidth =
            Mathf.Clamp(
                (fadeStartHeight - cullHeight) /
                Mathf.Max(
                    fadeStartHeight,
                    0.0001f
                ),
                0.01f,
                0.9f
            );

        distanceVisibilityLodGroup.SetLODs(
            new[] { visibleLod }
        );

        configuredDistanceVisibilityRange =
            visibilityRange;
        configuredDistanceVisibilityFieldOfView =
            localCamera.fieldOfView;
    }

    private float CalculateScreenRelativeHeight(
        Camera localCamera,
        float distance)
    {
        float worldSize =
            distanceVisibilityLodGroup.size *
            Mathf.Max(
                Mathf.Abs(transform.lossyScale.x),
                Mathf.Abs(transform.lossyScale.y),
                Mathf.Abs(transform.lossyScale.z)
            );

        float relativeHeight;

        if (localCamera.orthographic)
        {
            relativeHeight =
                worldSize * 0.5f /
                Mathf.Max(
                    localCamera.orthographicSize,
                    0.01f
                );
        }
        else
        {
            float halfFieldOfView =
                localCamera.fieldOfView *
                0.5f *
                Mathf.Deg2Rad;

            relativeHeight =
                worldSize * 0.5f /
                Mathf.Max(
                    distance *
                    Mathf.Tan(halfFieldOfView),
                    0.01f
                );
        }

        return Mathf.Clamp(
            relativeHeight *
            Mathf.Max(QualitySettings.lodBias, 0.01f),
            0.0001f,
            0.99f
        );
    }

    private void SetDistanceVisibilityLimited(
        bool limited)
    {
        bool identityChanged =
            !distanceIdentityVisible;

        distanceVisibilityLimited = limited;
        distanceIdentityVisible = true;
        configuredDistanceVisibilityRange = -1f;
        configuredDistanceVisibilityFieldOfView = -1f;

        if (distanceVisibilityLodGroup != null)
        {
            distanceVisibilityLodGroup.ForceLOD(-1);
            distanceVisibilityLodGroup.enabled = limited;
        }

        if (identityChanged)
            RefreshOverheadIdentity();
    }

    private void RefreshSpiritBodyVisibility()
    {
        if (IsOwner)
            return;

        bool visible = IsAlive;

        if (!visible &&
            boundMatchManager != null &&
            boundMatchManager.TryGetLocalPlayerMatchState(
                out PlayerMatchState localState))
        {
            visible = !localState.isAlive;
        }

        SetSpiritBodyRenderersVisible(visible);
    }

    private void RefreshOverheadIdentity()
    {
        if (overheadIdentityLabel == null)
            return;

        if (IsOwner ||
            boundMatchManager == null ||
            boundMatchManager.CurrentPhase != MatchPhase.NightAction ||
            !IsLocallyVisibleByDistance ||
            !boundMatchManager.TryGetPublicPlayerName(
                linkedClientId.Value,
                out string playerName))
        {
            overheadIdentityLabel.Clear();
            return;
        }

        if (boundMatchManager.TryGetLocalPlayerMatchState(
                out PlayerMatchState localState) &&
            !localState.isAlive &&
            boundMatchManager.TryGetLocalSpectatorPlayerMatchState(
                linkedClientId.Value,
                out PlayerMatchState targetState))
        {
            string lifeSuffix =
                IsAlive ? string.Empty : " (사망)";
            string teamColor =
                MafiaUIController.GetTeamColorHex(targetState.team);

            overheadIdentityLabel.SetPlainText(
                $"{playerName}{lifeSuffix}\n" +
                $"<color=#{teamColor}>" +
                $"{MafiaUIController.GetTeamDisplayName(targetState.team)} · " +
                $"{MafiaUIController.GetRoleDisplayName(targetState.role)}" +
                "</color>"
            );
            return;
        }

        bool isLocalAliveMafia =
            localState.isAlive &&
            localState.team == RoleTeam.Mafia;
        bool isVisibleMafiaAlly =
            IsAlive &&
            isLocalAliveMafia &&
            boundMatchManager.IsLocalMafiaMember(
                linkedClientId.Value);

        if (isVisibleMafiaAlly)
        {
            string mafiaMarker =
                $"<color=#{MafiaUIController.GetTeamColorHex(RoleTeam.Mafia)}>◆</color>";

            if (nightIdentityRevealed.Value)
            {
                overheadIdentityLabel.SetPlainText(
                    $"{mafiaMarker}\n{playerName}"
                );
            }
            else
            {
                overheadIdentityLabel.SetPlainText(mafiaMarker);
            }

            return;
        }

        if (IsAlive && nightIdentityRevealed.Value)
        {
            overheadIdentityLabel.SetPlayer(
                linkedClientId.Value,
                playerName
            );
            return;
        }

        overheadIdentityLabel.Clear();
    }

    private void CacheSpiritBodyRenderers()
    {
        if (spiritBodyRenderers != null &&
            spiritBodyRenderers.Length > 0)
        {
            return;
        }

        Renderer[] renderers =
            GetComponentsInChildren<Renderer>(true);

        System.Collections.Generic.List<Renderer> cached =
            new System.Collections.Generic.List<Renderer>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer targetRenderer = renderers[i];

            if (targetRenderer == null)
                continue;

            if (weaponRoot != null &&
                targetRenderer.transform.IsChildOf(
                    weaponRoot.transform))
            {
                continue;
            }

            if (overheadIdentityLabel != null &&
                targetRenderer.transform.IsChildOf(
                    overheadIdentityLabel.transform))
            {
                continue;
            }

            cached.Add(targetRenderer);
        }

        spiritBodyRenderers = cached.ToArray();
    }

    private void SetSpiritBodyRenderersVisible(bool visible)
    {
        CacheSpiritBodyRenderers();

        if (spiritBodyRenderers == null)
            return;

        for (int i = 0; i < spiritBodyRenderers.Length; i++)
        {
            if (spiritBodyRenderers[i] != null)
                spiritBodyRenderers[i].enabled = visible;
        }
    }

    [Rpc(SendTo.Everyone)]
    private void ForceTeleportRpc(Vector3 position, Quaternion rotation)
    {
        ApplyTeleport(position, rotation);
    }

    public bool TryRecoverToHomeSpiritPoint()
    {
        if (!IsOwner || !IsSpawned)
            return false;

        if (!TryGetHomeSpiritPoint(out Transform spiritPoint))
            return false;

        ApplyTeleport(spiritPoint.position, spiritPoint.rotation);
        RequestHomeRecoveryRpc();

        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void RequestHomeRecoveryRpc()
    {
        if (!IsServer)
            return;

        if (!TryGetHomeSpiritPoint(out Transform spiritPoint))
            return;

        ForceTeleportRpc(spiritPoint.position, spiritPoint.rotation);
        SetCurrentHouse(homeHouseId.Value);
    }

    private bool TryGetHomeSpiritPoint(out Transform spiritPoint)
    {
        spiritPoint = null;

        if (homeHouseId.Value < 0)
            return false;

        if (MatchManager.Instance == null)
            return false;

        House homeHouse = MatchManager.Instance.GetHouse(homeHouseId.Value);

        if (homeHouse == null || homeHouse.SpiritPoint == null)
            return false;

        spiritPoint = homeHouse.SpiritPoint;

        return true;
    }

    private void ApplyTeleport(Vector3 position, Quaternion rotation)
    {
        bool controllerWasEnabled = characterController != null && characterController.enabled;

        if (controllerWasEnabled)
            characterController.enabled = false;

        transform.SetPositionAndRotation(position, rotation);

        if (controllerWasEnabled)
            characterController.enabled = true;
    }
}
