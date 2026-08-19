using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(PlayerSpirit))]
public class SpiritRoleActionInteractor : NetworkBehaviour
{
    [Header("Raycast")]
    [SerializeField] private Camera playerCamera;
    [SerializeField] private LayerMask raycastMask = ~0;
    [SerializeField] private LayerMask spiritHitboxMask;
    [SerializeField] private LayerMask frontDoorRaycastMask;

    [Header("Input")]
    [SerializeField] private Key roleActionKey = Key.F;

    [Tooltip("직무와 직업 행동의 우선순위를 전환합니다. 살해 담당 내통자는 살해와 주민 능력 모드도 함께 순환합니다.")]
    [SerializeField] private Key spyKillerModeToggleKey = Key.R;

    [Header("Cast Durations")]
    [Min(0.1f)][SerializeField] private float mafiaKillCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float doctorProtectCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float bailiffConfineCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float exorcismCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float detectiveInspectCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float alchemistPoisonCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float forensicsInspectCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float hunterTrackCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float undertakerSealCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float mediumCommuneCastDuration = 3f;
    [Min(0.1f)][SerializeField] private float nightDutyCastDuration = 3f;

    [Header("Action Icons")]
    [SerializeField] private Sprite mafiaKillIcon;
    [SerializeField] private Sprite doctorProtectIcon;
    [SerializeField] private Sprite bailiffConfineIcon;
    [SerializeField] private Sprite exorcismIcon;
    [SerializeField] private Sprite detectiveInspectIcon;
    [SerializeField] private Sprite alchemistPoisonIcon;
    [SerializeField] private Sprite forensicsInspectIcon;
    [SerializeField] private Sprite hunterTrackIcon;
    [SerializeField] private Sprite undertakerSealIcon;
    [SerializeField] private Sprite mediumCommuneIcon;
    [SerializeField] private Sprite nightDutyIcon;

    private PlayerSpirit playerSpirit;
    private SpiritInteractionAudio interactionAudio;
    private MatchManager matchManager;

    private RoleActionTarget currentTarget;
    private House currentTargetHouse;
    private NightDutyPoint currentNightDutyPoint;

    private RoleActionType currentActionType;
    private RoleActionType pendingActionType;

    private float currentCastTime;
    private float currentNightDutyCastTime;
    private bool localActionPending;
    private bool localActionCompleted;
    private bool localNightDutyPending;
    private bool localExorcismConsumed;
    private bool interactionAudioActive;
    private bool useSpyCitizenAbilityMode;
    private bool preferRoleActionOverNightDuty;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
            return;

        playerSpirit = GetComponent<PlayerSpirit>();
        interactionAudio = GetComponent<SpiritInteractionAudio>();

        if (playerCamera == null)
            playerCamera = GetComponentInChildren<Camera>(true);

        TryBindMatchManager();
        ResetInteraction(true);
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
            return;

        UnbindMatchManager();
        ResetInteraction(true);
    }

    private void OnDisable()
    {
        if (IsOwner)
            ResetInteraction(true);
    }

    public void CancelCurrentRoleActionFromHit()
    {
        if (!IsOwner ||
            localNightDutyPending ||
            (!localActionPending &&
             currentCastTime <= 0f &&
             currentNightDutyCastTime <= 0f))
        {
            return;
        }

        EndInteractionAudio();

        currentTarget = null;
        currentTargetHouse = null;
        currentActionType = RoleActionType.None;
        currentCastTime = 0f;
        localActionPending = false;
        pendingActionType = RoleActionType.None;
        ResetNightDutyInteraction(false);

        if (RoleActionProgressUI.Instance != null)
            RoleActionProgressUI.Instance.Hide();
    }

    private void Update()
    {
        if (!IsOwner)
            return;

        if (matchManager == null)
            TryBindMatchManager();

        UpdateInteractionModeToggle();

        if (preferRoleActionOverNightDuty &&
            TryProcessRoleAction(true))
        {
            ResetNightDutyInteraction(false);
            return;
        }

        if (TryProcessNightDuty())
            return;

        ResetNightDutyInteraction(false);

        TryProcessRoleAction(false);
    }

    private bool TryProcessRoleAction(
        bool requireAimedTarget)
    {

        if (!CanProcessRoleAction())
        {
            ResetInteraction(true);
            return false;
        }

        if (!TryResolveCurrentRoleAction(
                out RoleActionType actionType,
                out RoleActionTargetType targetType,
                out RoleActionExecutionType executionType))
        {
            ResetInteraction(true);
            return false;
        }

        if (actionType == RoleActionType.Exorcism &&
            localExorcismConsumed)
        {
            ResetInteraction(true);
            return false;
        }

        if (requireAimedTarget &&
            !IsAimingValidRoleActionTarget(
                actionType,
                targetType))
        {
            return false;
        }

        if (targetType == RoleActionTargetType.BodyOrFrontDoor)
        {
            ProcessAlchemistPoisonAction(
                actionType,
                executionType
            );
            return true;
        }

        if (targetType == RoleActionTargetType.FrontDoor)
        {
            ProcessFrontDoorAction(
                actionType,
                executionType
            );
            return true;
        }

        ProcessPlayerTargetAction(
            actionType,
            targetType,
            executionType
        );

        return true;
    }

    private bool IsAimingValidRoleActionTarget(
        RoleActionType actionType,
        RoleActionTargetType targetType)
    {
        if (targetType ==
            RoleActionTargetType.BodyOrFrontDoor)
        {
            return TryGetAimedPlayerTarget(
                       actionType,
                       RoleActionTargetType.Body,
                       out _) ||
                   TryGetAimedFrontDoor(
                       actionType,
                       out _);
        }

        if (targetType == RoleActionTargetType.FrontDoor)
        {
            return TryGetAimedFrontDoor(
                actionType,
                out _
            );
        }

        return TryGetAimedPlayerTarget(
            actionType,
            targetType,
            out _
        );
    }

    private void UpdateInteractionModeToggle()
    {
        if (matchManager == null ||
            playerSpirit == null ||
            matchManager.CurrentPhase !=
                MatchPhase.NightAction ||
            !playerSpirit.CanUseRoleAction ||
            localActionPending ||
            localActionCompleted ||
            localNightDutyPending)
        {
            return;
        }

        Keyboard keyboard = Keyboard.current;

        if (keyboard == null ||
            !keyboard[spyKillerModeToggleKey]
                .wasPressedThisFrame)
        {
            return;
        }

        bool canPerformNightDuty =
            (matchManager.HasLocalNightDutyAssignment &&
             !matchManager.IsLocalNightDutyCompleted) ||
            matchManager.CanLocalPerformFakeNightDuty;

        bool hasDefaultAction =
            matchManager.TryGetLocalNightRoleAction(
                out _,
                out _,
                out _
            );

        RoleActionType spyActionType =
            RoleActionType.None;

        bool canToggleSpyAction =
            matchManager.TryGetLocalPlayerMatchState(
                out PlayerMatchState localState) &&
            localState.role == RoleId.Spy &&
            matchManager.IsLocalNightKiller &&
            matchManager.TryGetLocalSpyCitizenNightRoleAction(
                out spyActionType,
                out _,
                out _
            );

        if (canToggleSpyAction &&
            canPerformNightDuty)
        {
            if (!preferRoleActionOverNightDuty)
            {
                preferRoleActionOverNightDuty = true;
                useSpyCitizenAbilityMode = false;
                ShowInteractionModeNotification("살해 우선");
            }
            else if (!useSpyCitizenAbilityMode)
            {
                useSpyCitizenAbilityMode = true;

                ShowInteractionModeNotification(
                    GetSpyCitizenRoleSwitchMessage(
                        spyActionType
                    ).Replace("전환", "우선")
                );
            }
            else
            {
                preferRoleActionOverNightDuty = false;
                useSpyCitizenAbilityMode = false;
                ShowInteractionModeNotification(
                    GetNightDutyPriorityMessage()
                );
            }

            return;
        }

        if (canToggleSpyAction)
        {
            useSpyCitizenAbilityMode =
                !useSpyCitizenAbilityMode;

            ShowInteractionModeNotification(
                useSpyCitizenAbilityMode
                    ? GetSpyCitizenRoleSwitchMessage(
                        spyActionType
                    )
                    : "살해 전환"
            );

            return;
        }

        if (!canPerformNightDuty ||
            !hasDefaultAction ||
            localExorcismConsumed)
        {
            return;
        }

        preferRoleActionOverNightDuty =
            !preferRoleActionOverNightDuty;

        ShowInteractionModeNotification(
            preferRoleActionOverNightDuty
                ? "직업 능력 우선"
                : GetNightDutyPriorityMessage()
        );
    }

    private string GetNightDutyPriorityMessage()
    {
        return matchManager != null &&
               matchManager.IsLocalMafiaInterferenceDuty
            ? "교란 공작 우선"
            : "직무 우선";
    }

    private void ShowInteractionModeNotification(
        string message)
    {
        ResetInteraction(true);

        if (matchManager != null)
            matchManager.ShowLocalNotification(message);
    }

    private bool TryProcessNightDuty()
    {
        if (localNightDutyPending)
        {
            ResetInteraction(true);
            return true;
        }

        if (matchManager == null ||
            playerSpirit == null ||
            playerCamera == null)
        {
            return false;
        }

        bool canPerformRealDuty =
            matchManager.HasLocalNightDutyAssignment &&
            !matchManager.IsLocalNightDutyCompleted;

        bool canPerformFakeDuty =
            matchManager.CanLocalPerformFakeNightDuty;

        if (!canPerformRealDuty &&
            !canPerformFakeDuty)
        {
            return false;
        }

        if (SpiritFirstPersonCamera
                .IsLocalUIInteractionActive ||
            matchManager.CurrentPhase !=
                MatchPhase.NightAction ||
            !playerSpirit.CanUseRoleAction)
        {
            return false;
        }

        Ray ray = new Ray(
            playerCamera.transform.position,
            playerCamera.transform.forward
        );

        if (!Physics.Raycast(
                ray,
                out RaycastHit hit,
                matchManager.RoleActionDistance,
                raycastMask,
                QueryTriggerInteraction.Collide))
        {
            return false;
        }

        NightDutyPoint dutyPoint =
            hit.collider.GetComponentInParent
                <NightDutyPoint>();

        if (!matchManager
                .IsLocalNightDutyInteractionPoint(dutyPoint))
        {
            return false;
        }

        if (currentNightDutyPoint != dutyPoint)
        {
            ResetInteraction(false);
            currentNightDutyPoint = dutyPoint;
        }

        if (RoleActionProgressUI.Instance != null)
        {
            RoleActionProgressUI.Instance.ShowNightDuty(
                nightDutyIcon
            );

            RoleActionProgressUI.Instance.SetProgress(
                currentNightDutyCastTime /
                nightDutyCastDuration
            );
        }

        if (!IsRoleActionKeyPressed())
        {
            currentNightDutyCastTime = 0f;

            if (RoleActionProgressUI.Instance != null)
            {
                RoleActionProgressUI.Instance
                    .SetProgress(0f);
            }

            return true;
        }

        currentNightDutyCastTime += Time.deltaTime;

        if (RoleActionProgressUI.Instance != null)
        {
            RoleActionProgressUI.Instance.SetProgress(
                currentNightDutyCastTime /
                nightDutyCastDuration
            );
        }

        if (currentNightDutyCastTime <
            nightDutyCastDuration)
        {
            return true;
        }

        localNightDutyPending = true;

        matchManager.SubmitNightDutyCompletion(
            dutyPoint.HouseId,
            dutyPoint.PointId
        );

        ResetInteraction(true);
        return true;
    }

    private bool TryResolveCurrentRoleAction(
        out RoleActionType actionType,
        out RoleActionTargetType targetType,
        out RoleActionExecutionType executionType)
    {
        bool hasDefaultAction =
            matchManager.TryGetLocalNightRoleAction(
                out actionType,
                out targetType,
                out executionType
            );

        RoleActionType spyActionType =
            RoleActionType.None;

        RoleActionTargetType spyTargetType =
            RoleActionTargetType.Body;

        RoleActionExecutionType spyExecutionType =
            RoleActionExecutionType.None;

        bool canToggleSpyAction =
            matchManager.TryGetLocalPlayerMatchState(
                out PlayerMatchState localState) &&
            localState.role == RoleId.Spy &&
            matchManager.IsLocalNightKiller &&
            matchManager.TryGetLocalSpyCitizenNightRoleAction(
                out spyActionType,
                out spyTargetType,
                out spyExecutionType
            );

        if (!canToggleSpyAction)
            useSpyCitizenAbilityMode = false;

        if (canToggleSpyAction &&
            useSpyCitizenAbilityMode)
        {
            actionType = spyActionType;
            targetType = spyTargetType;
            executionType = spyExecutionType;
            return true;
        }

        return hasDefaultAction;
    }

    private string GetSpyCitizenRoleSwitchMessage(
        RoleActionType actionType)
    {
        switch (actionType)
        {
            case RoleActionType.DoctorProtect:
                return "의원 전환";

            case RoleActionType.BailiffConfine:
                return "집행관 전환";

            case RoleActionType.Exorcism:
                return "퇴마사 전환";

            case RoleActionType.DetectiveInspect:
                return "수사관 전환";

            case RoleActionType.ForensicsInspect:
                return "검시관 전환";

            case RoleActionType.MediumCommune:
                return "영매사 전환";

            case RoleActionType.HunterTrack:
                return "사냥꾼 전환";

            case RoleActionType.UndertakerSeal:
                return "장의사 전환";

            default:
                return "주민 능력 전환";
        }
    }

    private bool CanProcessRoleAction()
    {
        if (matchManager == null ||
            playerSpirit == null ||
            playerCamera == null)
        {
            return false;
        }

        if (SpiritFirstPersonCamera
                .IsLocalUIInteractionActive)
        {
            return false;
        }

        if (matchManager.CurrentPhase != MatchPhase.NightAction)
            return false;

        if (localActionPending || localActionCompleted)
            return false;

        return playerSpirit.CanUseRoleAction;
    }

    private void ProcessAlchemistPoisonAction(
        RoleActionType actionType,
        RoleActionExecutionType executionType)
    {
        if (TryGetAimedPlayerTarget(
                actionType,
                RoleActionTargetType.Body,
                out RoleActionTarget target))
        {
            ProcessPlayerTargetAction(
                actionType,
                RoleActionTargetType.Body,
                executionType
            );
            return;
        }

        ProcessFrontDoorAction(
            actionType,
            executionType
        );
    }

    private void ProcessFrontDoorAction(
        RoleActionType actionType,
        RoleActionExecutionType executionType)
    {
        if ((actionType != RoleActionType.AlchemistPoison &&
             actionType != RoleActionType.HunterTrack &&
             actionType != RoleActionType.UndertakerSeal) ||
            !TryGetAimedFrontDoor(
                actionType,
                out House targetHouse))
        {
            ResetInteraction(true);
            return;
        }

        UpdateCurrentHouseTarget(
            targetHouse,
            actionType
        );

        ShowInteractionCursor(actionType);

        if (!IsRoleActionKeyPressed())
        {
            ResetCastProgress();
            return;
        }

        BeginInteractionAudio(
            SpiritInteractionSoundType.Body,
            ulong.MaxValue
        );

        currentCastTime += Time.deltaTime;

        float castDuration =
            GetCastDuration(actionType);

        if (RoleActionProgressUI.Instance != null)
        {
            RoleActionProgressUI.Instance.SetProgress(
                currentCastTime /
                castDuration
            );
        }

        if (currentCastTime < castDuration)
            return;

        CompleteFrontDoorAction(
            actionType,
            targetHouse,
            executionType
        );
    }

    private bool TryGetAimedFrontDoor(
        RoleActionType actionType,
        out House targetHouse)
    {
        targetHouse = null;

        Ray ray = new Ray(
            playerCamera.transform.position,
            playerCamera.transform.forward
        );

        if (!Physics.Raycast(ray, out RaycastHit hit, matchManager.RoleActionDistance, frontDoorRaycastMask, QueryTriggerInteraction.Collide))
        {
            return false;
        }

        targetHouse =
            hit.collider.GetComponentInParent
                <House>();

        if (targetHouse == null ||
            targetHouse.Id < 0 ||
            targetHouse.FrontDoorOutsidePoint == null)
        {
            return false;
        }

        if (actionType !=
                RoleActionType.AlchemistPoison &&
            actionType !=
                RoleActionType.HunterTrack &&
            actionType !=
                RoleActionType.UndertakerSeal)
        {
            return false;
        }

        if (targetHouse.Id ==
            playerSpirit.HomeHouseId)
        {
            return false;
        }

        if (playerSpirit.CurrentHouseId >= 0)
            return false;

        if (actionType == RoleActionType.UndertakerSeal)
        {
            ulong ownerClientId = targetHouse.OwnerClientId;

            if (ownerClientId == House.NoOwnerClientId ||
                matchManager.IsPublicPlayerAlive(ownerClientId) ||
                matchManager.IsFrontDoorSealed(targetHouse.Id))
            {
                return false;
            }
        }

        return targetHouse.IsNearFrontDoorOutside(
            playerSpirit.transform.position,
            matchManager.RoleActionDistance
        );
    }

    private void ProcessPlayerTargetAction(
        RoleActionType actionType,
        RoleActionTargetType targetType,
        RoleActionExecutionType executionType)
    {
        if (!TryGetAimedPlayerTarget(
                actionType,
                targetType,
                out RoleActionTarget target))
        {
            ResetInteraction(true);
            return;
        }

        UpdateCurrentTarget(target, actionType);
        ShowInteractionCursor(actionType);

        if (!IsRoleActionKeyPressed())
        {
            ResetCastProgress();
            return;
        }

        if (!target.TryGetTargetClientId(
                out ulong targetClientId))
        {
            ResetInteraction(true);
            return;
        }

        BeginInteractionAudio(
            actionType == RoleActionType.Exorcism
                ? SpiritInteractionSoundType.Exorcism
                : SpiritInteractionSoundType.Body,
            targetClientId
        );

        currentCastTime += Time.deltaTime;

        float castDuration = GetCastDuration(actionType);

        if (RoleActionProgressUI.Instance != null)
            RoleActionProgressUI.Instance.SetProgress(currentCastTime / castDuration);

        if (currentCastTime < castDuration)
            return;

        CompletePlayerTargetAction(
            actionType,
            target,
            executionType
        );
    }

    private bool TryGetAimedPlayerTarget(
        RoleActionType actionType,
        RoleActionTargetType requiredTargetType,
        out RoleActionTarget target)
    {
        if (requiredTargetType == RoleActionTargetType.Spirit)
            return TryGetAimedSpiritTarget(actionType, out target);

        target = null;

        Ray ray = new Ray(
            playerCamera.transform.position,
            playerCamera.transform.forward
        );

        if (!Physics.Raycast(ray, out RaycastHit hit, matchManager.RoleActionDistance, raycastMask, QueryTriggerInteraction.Collide))
        {
            return false;
        }

        target = hit.collider.GetComponentInParent<RoleActionTarget>();

        if (target == null ||
            target.TargetType != requiredTargetType ||
            !target.TryGetTargetClientId(out ulong targetClientId))
        {
            return false;
        }

        return IsValidPlayerTarget(actionType, target, targetClientId);
    }

    private bool TryGetAimedSpiritTarget(
        RoleActionType actionType,
        out RoleActionTarget target)
    {
        target = null;

        Ray ray = new Ray(
            playerCamera.transform.position,
            playerCamera.transform.forward
        );

        /*
         * Physics.DefaultRaycastLayers는 Ignore Raycast 레이어를
         * 제외한다. 따라서 감옥은 통과하지만, Raycast Mask에
         * 포함된 집 벽이나 일반 구조물은 첫 충돌 지점에서
         * 정상적으로 시야를 막는다.
         */
        int effectiveMask =
            raycastMask.value &
            Physics.DefaultRaycastLayers;

        if (!Physics.Raycast(ray, out RaycastHit hit, matchManager.RoleActionDistance, effectiveMask, QueryTriggerInteraction.Collide))
        {
            return false;
        }

        int hitLayerMask =
            1 << hit.collider.gameObject.layer;

        /*
         * 첫 충돌 대상이 영체 전용 Hitbox 레이어일 때만
         * 퇴마 대상으로 인정한다. 벽 뒤의 영체는 벽이 먼저
         * 충돌하므로 조준할 수 없다.
         */
        if ((spiritHitboxMask.value &
             hitLayerMask) == 0)
        {
            return false;
        }

        RoleActionTarget candidate =
            hit.collider.GetComponentInParent
                <RoleActionTarget>();

        if (candidate == null ||
            candidate.TargetType !=
                RoleActionTargetType.Spirit ||
            !candidate.TryGetTargetClientId(
                out ulong targetClientId) ||
            !IsValidPlayerTarget(
                actionType,
                candidate,
                targetClientId))
        {
            return false;
        }

        target = candidate;
        return true;
    }

    private bool IsValidPlayerTarget(
        RoleActionType actionType,
        RoleActionTarget target,
        ulong targetClientId)
    {
        ulong localClientId = NetworkManager.LocalClientId;
        bool targetAlive = matchManager.IsPublicPlayerAlive(targetClientId);

        switch (actionType)
        {
            case RoleActionType.MafiaKill:
            case RoleActionType.AlchemistPoison:
                return targetAlive &&
                       targetClientId != localClientId &&
                       !matchManager.IsLocalMafiaMember(targetClientId);

            case RoleActionType.CurseDoll:
                return targetAlive &&
                       targetClientId != localClientId;

            case RoleActionType.DoctorProtect:
                return targetAlive;

            case RoleActionType.BailiffConfine:
                return targetAlive &&
                       targetClientId != localClientId;

            case RoleActionType.Exorcism:
            {
                if (!targetAlive || targetClientId == localClientId)
                    return false;

                PlayerSpirit targetSpirit =
                    target.GetComponentInParent<PlayerSpirit>();

                return targetSpirit != null &&
                       targetSpirit.IsConfinedForNight;
            }

            case RoleActionType.DetectiveInspect:
                return targetAlive &&
                       targetClientId != localClientId;

            case RoleActionType.ForensicsInspect:
                return !targetAlive;

            case RoleActionType.MediumCommune:
                return !targetAlive &&
                       targetClientId != localClientId &&
                       matchManager.TryGetPlayerSpirit(
                           targetClientId,
                           out PlayerSpirit deadSpirit
                       ) &&
                       deadSpirit.IsRestrictedDeadSpectator;

            default:
                return false;
        }
    }

    private bool IsRoleActionKeyPressed()
    {
        return Keyboard.current != null &&
               Keyboard.current[roleActionKey].isPressed;
    }

    private bool IsLayerIncluded(LayerMask layerMask, int layer)
    {
        return (layerMask.value & (1 << layer)) != 0;
    }

    private void UpdateCurrentTarget(
        RoleActionTarget target,
        RoleActionType actionType)
    {
        if (currentTarget == target && currentActionType == actionType)
            return;

        EndInteractionAudio();

        currentTargetHouse = null;
        currentTarget = target;
        currentActionType = actionType;
        currentCastTime = 0f;

        if (RoleActionProgressUI.Instance != null)
            RoleActionProgressUI.Instance.SetProgress(0f);
    }

    private void UpdateCurrentHouseTarget(
        House targetHouse,
        RoleActionType actionType)
    {
        if (currentTargetHouse == targetHouse &&
            currentActionType == actionType)
        {
            return;
        }

        EndInteractionAudio();

        currentTarget = null;
        currentTargetHouse = targetHouse;
        currentActionType = actionType;
        currentCastTime = 0f;

        if (RoleActionProgressUI.Instance != null)
            RoleActionProgressUI.Instance.SetProgress(0f);
    }

    private void ShowInteractionCursor(RoleActionType actionType)
    {
        if (RoleActionProgressUI.Instance == null)
            return;

        RoleActionProgressUI.Instance.Show(GetActionIcon(actionType));

        float duration = GetCastDuration(actionType);
        float progress = duration > 0f ? currentCastTime / duration : 0f;

        RoleActionProgressUI.Instance.SetProgress(progress);
    }

    private void ResetCastProgress()
    {
        EndInteractionAudio();

        if (currentCastTime <= 0f)
            return;

        currentCastTime = 0f;

        if (RoleActionProgressUI.Instance != null)
            RoleActionProgressUI.Instance.SetProgress(0f);
    }

    private void CompleteFrontDoorAction(
        RoleActionType actionType,
        House targetHouse,
        RoleActionExecutionType executionType)
    {
        if (targetHouse == null ||
            targetHouse.Id < 0)
        {
            ResetInteraction(true);
            return;
        }

        localActionPending = true;
        pendingActionType = actionType;

        if (executionType == RoleActionExecutionType.Fake)
        {
            matchManager.SubmitFakeRoleAction(
                actionType,
                ulong.MaxValue,
                targetHouse.Id
            );

            ResetInteraction(true);
            return;
        }

        switch (actionType)
        {
            case RoleActionType.AlchemistPoison:
                matchManager.SubmitAlchemistPoisonTarget(
                    targetHouse.Id
                );
                break;

            case RoleActionType.HunterTrack:
                matchManager.SubmitHunterTrackTarget(
                    targetHouse.Id
                );
                break;

            case RoleActionType.UndertakerSeal:
                matchManager.SubmitUndertakerSealTarget(
                    targetHouse.Id
                );
                break;

            default:
                localActionPending = false;
                pendingActionType =
                    RoleActionType.None;

                ResetInteraction(true);
                return;
        }

        Debug.Log(
            $"정문 직업 행동 요청 전송 - " +
            $"Action: {actionType}, " +
            $"House: {targetHouse.Id}, " +
            $"Execution: {executionType}"
        );

        ResetInteraction(true);
    }

    private void CompletePlayerTargetAction(
        RoleActionType actionType,
        RoleActionTarget target,
        RoleActionExecutionType executionType)
    {
        if (!target.TryGetTargetClientId(
                out ulong targetClientId))
        {
            ResetInteraction(true);
            return;
        }

        localActionPending = true;
        pendingActionType = actionType;

        if (executionType == RoleActionExecutionType.Fake)
        {
            matchManager.SubmitFakeRoleAction(
                actionType,
                targetClientId,
                -1
            );

            ResetInteraction(true);
            return;
        }

        switch (actionType)
        {
            case RoleActionType.MafiaKill:
                matchManager.SubmitMafiaKillTarget(
                    targetClientId
                );
                break;

            case RoleActionType.AlchemistPoison:
                matchManager.SubmitAlchemistPoisonBodyTarget(
                    targetClientId
                );
                break;

            case RoleActionType.CurseDoll:
                matchManager.SubmitInitialCurseDollPlacement(
                    targetClientId
                );
                break;

            case RoleActionType.DoctorProtect:
                matchManager.SubmitDoctorProtectTarget(
                    targetClientId
                );
                break;

            case RoleActionType.BailiffConfine:
                matchManager.SubmitBailiffConfineTarget(
                    targetClientId
                );
                break;

            case RoleActionType.Exorcism:
                matchManager.SubmitExorcismTarget(
                    targetClientId
                );
                break;

            case RoleActionType.DetectiveInspect:
                matchManager.SubmitDetectiveInspectTarget(
                    targetClientId
                );
                break;

            case RoleActionType.ForensicsInspect:
                matchManager.SubmitForensicsInspectTarget(
                    targetClientId
                );
                break;

            case RoleActionType.MediumCommune:
                matchManager.SubmitMediumCommuneTarget(
                    targetClientId
                );
                break;

            default:
                localActionPending = false;
                pendingActionType =
                    RoleActionType.None;

                ResetInteraction(true);
                return;
        }

        Debug.Log(
            $"직업 행동 요청 전송 - " +
            $"Action: {actionType}, " +
            $"Target: {targetClientId}, " +
            $"Execution: {executionType}"
        );

        ResetInteraction(true);
    }

    private void OnLocalRoleActionResultReceived(
        RoleActionType actionType,
        bool accepted)
    {
        if (!localActionPending || pendingActionType != actionType)
            return;

        localActionPending = false;
        pendingActionType = RoleActionType.None;

        if (!accepted)
        {
            localActionCompleted = false;

            Debug.LogWarning(
                $"직업 행동 서버 거절 - " +
                $"Action: {actionType}, 다시 시도할 수 있습니다."
            );

            return;
        }

        localActionCompleted = true;

        if (actionType == RoleActionType.Exorcism &&
            matchManager != null &&
            matchManager.TryGetLocalPlayerMatchState(
                out PlayerMatchState localState) &&
            localState.role == RoleId.Exorcist)
        {
            localExorcismConsumed = true;
        }

        ResetInteraction(true);

        Debug.Log($"직업 행동 서버 승인 - Action: {actionType}");
    }

    private float GetCastDuration(RoleActionType actionType)
    {
        switch (actionType)
        {
            case RoleActionType.MafiaKill:
            case RoleActionType.CurseDoll:
                return mafiaKillCastDuration;

            case RoleActionType.DoctorProtect:
                return doctorProtectCastDuration;

            case RoleActionType.BailiffConfine:
                return bailiffConfineCastDuration;

            case RoleActionType.Exorcism:
                return exorcismCastDuration;

            case RoleActionType.DetectiveInspect:
                return detectiveInspectCastDuration;

            case RoleActionType.AlchemistPoison:
                return alchemistPoisonCastDuration;

            case RoleActionType.ForensicsInspect:
                return forensicsInspectCastDuration;

            case RoleActionType.HunterTrack:
                return hunterTrackCastDuration;

            case RoleActionType.UndertakerSeal:
                return undertakerSealCastDuration;

            case RoleActionType.MediumCommune:
                return mediumCommuneCastDuration;

            default:
                return 1f;
        }
    }

    private Sprite GetActionIcon(RoleActionType actionType)
    {
        switch (actionType)
        {
            case RoleActionType.MafiaKill:
            case RoleActionType.CurseDoll:
                return mafiaKillIcon;

            case RoleActionType.DoctorProtect:
                return doctorProtectIcon;

            case RoleActionType.BailiffConfine:
                return bailiffConfineIcon;

            case RoleActionType.Exorcism:
                return exorcismIcon;

            case RoleActionType.DetectiveInspect:
                return detectiveInspectIcon;

            case RoleActionType.AlchemistPoison:
                return alchemistPoisonIcon;

            case RoleActionType.ForensicsInspect:
                return forensicsInspectIcon;

            case RoleActionType.HunterTrack:
                return hunterTrackIcon;

            case RoleActionType.UndertakerSeal:
                return undertakerSealIcon;

            case RoleActionType.MediumCommune:
                return mediumCommuneIcon;

            default:
                return null;
        }
    }

    private void BeginInteractionAudio(
        SpiritInteractionSoundType soundType,
        ulong targetClientId)
    {
        if (interactionAudioActive ||
            interactionAudio == null)
        {
            return;
        }

        interactionAudioActive = true;

        interactionAudio.BeginRoleInteraction(
            soundType,
            targetClientId
        );
    }

    private void EndInteractionAudio()
    {
        if (!interactionAudioActive)
            return;

        interactionAudioActive = false;

        if (interactionAudio != null)
            interactionAudio.EndRoleInteraction();
    }

    private void TryBindMatchManager()
    {
        if (matchManager != null || MatchManager.Instance == null)
            return;

        matchManager = MatchManager.Instance;
        matchManager.PhaseChanged += OnPhaseChanged;
        matchManager.LocalRoleActionResultReceived += OnLocalRoleActionResultReceived;
        matchManager.LocalNightDutyCompletionResultReceived +=
            OnLocalNightDutyCompletionResultReceived;
    }

    private void UnbindMatchManager()
    {
        if (matchManager == null)
            return;

        matchManager.PhaseChanged -= OnPhaseChanged;
        matchManager.LocalRoleActionResultReceived -=
            OnLocalRoleActionResultReceived;
        matchManager.LocalNightDutyCompletionResultReceived -=
            OnLocalNightDutyCompletionResultReceived;

        matchManager = null;
    }

    private void OnPhaseChanged(
        MatchPhase previous,
        MatchPhase current)
    {
        if (current == MatchPhase.NightPreparation)
        {
            localActionPending = false;
            localActionCompleted = false;
            pendingActionType = RoleActionType.None;
            useSpyCitizenAbilityMode = false;
            preferRoleActionOverNightDuty = false;
            localNightDutyPending = false;
        }

        if (current != MatchPhase.NightAction)
            ResetInteraction(true);
    }

    private void OnLocalNightDutyCompletionResultReceived(
        bool accepted)
    {
        if (!localNightDutyPending)
            return;

        localNightDutyPending = false;
        ResetInteraction(true);

        if (!accepted &&
            matchManager != null &&
            !matchManager.IsLocalMafiaInterferenceDuty)
        {
            matchManager.ShowLocalNotification(
                "직무 수행 요청이 거절되었습니다."
            );
        }
    }

    private void ResetNightDutyInteraction(bool hideUI)
    {
        currentNightDutyPoint = null;
        currentNightDutyCastTime = 0f;

        if (hideUI &&
            RoleActionProgressUI.Instance != null)
        {
            RoleActionProgressUI.Instance.Hide();
        }
    }

    private void ResetInteraction(bool hideUI)
    {
        EndInteractionAudio();

        currentTarget = null;
        currentTargetHouse = null;
        currentActionType = RoleActionType.None;
        currentCastTime = 0f;
        ResetNightDutyInteraction(false);

        if (hideUI && RoleActionProgressUI.Instance != null)
            RoleActionProgressUI.Instance.Hide();
    }
}
