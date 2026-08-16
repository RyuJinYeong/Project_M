using MafiaGame.UI;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

[RequireComponent(typeof(PlayerSpirit))]
public class SpiritDoorInteraction : NetworkBehaviour
{
    private const float
        MinimumDoorUseCooldown = 2f;

    private enum DoorType : byte
    {
        Front,
        Back
    }

    [Header("상호작용 조준")]
    [Tooltip("떨어진 도구를 조준할 로컬 플레이어 카메라입니다. 비우면 자식 카메라를 자동으로 찾습니다.")]
    [SerializeField]
    private Camera playerCamera;

    [Tooltip("떨어진 도구와 그 앞을 가릴 벽, 집, 구조물이 포함된 레이어입니다.")]
    [FormerlySerializedAs("droppedToolRaycastMask")]
    [SerializeField]
    private LayerMask interactionRaycastMask = ~0;

    [Header("문 및 도구 사용")]
    [Tooltip("문 안팎 포인트 및 떨어진 도구를 사용할 수 있는 거리입니다.")]
    [SerializeField]
    private float interactionDistance = 1.5f;

    [Tooltip(
        "서버에 도착한 위치 동기화가 조금 늦을 수 있으므로 " +
        "거리 검증에 추가할 여유값입니다."
    )]
    [SerializeField]
    private float serverDistanceTolerance = 0.5f;

    [Tooltip("정문과 뒷문을 포함한 모든 문 사용 후 다시 문을 사용할 수 있기까지의 시간입니다.")]
    [Min(MinimumDoorUseCooldown)]
    [SerializeField]
    private float useCooldown = 2f;

    private float EffectiveUseCooldown =>
        Mathf.Max(
            MinimumDoorUseCooldown,
            useCooldown
        );

    public double LocalHitDoorLockRemainingSeconds
    {
        get
        {
            if (!IsOwner ||
                NetworkManager == null)
            {
                return 0d;
            }

            return System.Math.Max(
                0d,
                nextLocalHitDoorLockTime -
                NetworkManager.ServerTime.Time
            );
        }
    }

    private PlayerSpirit playerSpirit;
    private SpiritInteractionAudio interactionAudio;

    private double nextLocalUseTime;
    private double nextServerUseTime;
    private double nextLocalHitDoorLockTime;
    private double nextServerHitDoorLockTime;

    private void Awake()
    {
        playerSpirit =
            GetComponent<PlayerSpirit>();

        interactionAudio =
            GetComponent<SpiritInteractionAudio>();

        if (playerCamera == null)
        {
            playerCamera =
                GetComponentInChildren<Camera>(true);
        }

        SetDroppedToolAimIndicatorVisible(false);
    }

    private void OnDisable()
    {
        if (IsOwner)
        {
            SetDroppedToolAimIndicatorVisible(
                false
            );
        }
    }

    public void ApplyHitDoorLockAfterHit()
    {
        if (!IsServer ||
            NetworkManager == null)
        {
            return;
        }

        double lockUntil =
            NetworkManager.ServerTime.Time +
            EffectiveUseCooldown;

        nextServerHitDoorLockTime =
            System.Math.Max(
                nextServerHitDoorLockTime,
                lockUntil
            );

        if (!NetworkManager.ConnectedClients
                .ContainsKey(OwnerClientId))
        {
            return;
        }

        ApplyHitDoorLockRpc(
            lockUntil,
            RpcTarget.Single(
                OwnerClientId,
                RpcTargetUse.Temp
            )
        );
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ApplyHitDoorLockRpc(
        double lockUntil,
        RpcParams rpcParams = default)
    {
        if (!IsOwner)
            return;

        nextLocalHitDoorLockTime =
            System.Math.Max(
                nextLocalHitDoorLockTime,
                lockUntil
            );
    }

    private void Update()
    {
        bool canCheckInteraction =
            IsOwner &&
            IsSpawned &&
            playerSpirit != null &&
            playerSpirit.CanUseDoor &&
            MatchManager.Instance != null;

        if (!canCheckInteraction)
        {
            SetDroppedToolAimIndicatorVisible(
                false
            );

            return;
        }

        bool hasAimedInteraction =
            TryGetAimedPickupTool(
                out DroppedRoleTool targetTool,
                out CurseDollInteractionPoint
                    targetCurseDollPoint
            );

        bool hasAimedPickupTool =
            targetTool != null;

        bool hasAimedCurseDollPoint =
            targetCurseDollPoint != null;

        string interactionPrompt =
            string.Empty;

        if (hasAimedCurseDollPoint)
        {
            interactionPrompt =
                MatchManager.Instance.CurseDollState
                    .location ==
                    CurseDollLocation.Installed
                    ? "E  저주 인형 집기"
                    : "E  저주 인형 놓기";
        }
        else if (hasAimedPickupTool)
        {
            interactionPrompt =
                "E  도구 줍기";
        }

        SetDroppedToolAimIndicatorVisible(
            hasAimedInteraction,
            interactionPrompt
        );

        Keyboard keyboard =
            Keyboard.current;

        if (keyboard == null ||
            !keyboard.eKey.wasPressedThisFrame)
        {
            return;
        }

        if (hasAimedCurseDollPoint &&
            TryInteractCurseDoll(
                targetCurseDollPoint))
        {
            return;
        }

        if (hasAimedPickupTool &&
            TryPickupDroppedRoleTool(targetTool))
        {
            return;
        }

        TryUseDoor();
    }

    private bool TryPickupDroppedRoleTool(
        DroppedRoleTool targetTool)
    {
        if (targetTool == null ||
            MatchManager.Instance == null)
        {
            return false;
        }

        double currentTime =
            NetworkManager.LocalTime.Time;

        if (currentTime <
            nextLocalUseTime)
        {
            return false;
        }

        nextLocalUseTime =
            currentTime +
            EffectiveUseCooldown;

        SetDroppedToolAimIndicatorVisible(
            false
        );

        RequestPickupDroppedRoleToolRpc(
            targetTool.NetworkObjectId
        );

        return true;
    }

    private bool TryGetAimedPickupTool(
        out DroppedRoleTool targetTool,
        out CurseDollInteractionPoint
            targetCurseDollPoint)
    {
        targetTool = null;
        targetCurseDollPoint = null;

        if (playerCamera == null ||
            MatchManager.Instance == null)
        {
            return false;
        }

        Ray ray = new Ray(
            playerCamera.transform.position,
            playerCamera.transform.forward
        );

        if (!Physics.Raycast(ray, out RaycastHit hit, interactionDistance, interactionRaycastMask, QueryTriggerInteraction.Collide))
        {
            return false;
        }

        CurseDollInteractionPoint
            curseDollInteractionPoint =
                hit.collider.GetComponentInParent
                    <CurseDollInteractionPoint>();

        if (curseDollInteractionPoint != null &&
            MatchManager.Instance
                .CanLocalInteractWithCurseDollPoint(
                    curseDollInteractionPoint))
        {
            targetCurseDollPoint =
                curseDollInteractionPoint;

            return true;
        }

        DroppedRoleTool droppedRoleTool =
            hit.collider.GetComponentInParent<DroppedRoleTool>();

        if (droppedRoleTool == null ||
            !MatchManager.Instance.CanLocalPickupDroppedRoleTool(droppedRoleTool))
        {
            return false;
        }

        targetTool = droppedRoleTool;
        return true;
    }

    private bool TryInteractCurseDoll(
        CurseDollInteractionPoint targetPoint)
    {
        if (targetPoint == null ||
            targetPoint.HouseId < 0 ||
            MatchManager.Instance == null)
        {
            return false;
        }

        double currentTime =
            NetworkManager.LocalTime.Time;

        if (currentTime < nextLocalUseTime)
            return false;

        nextLocalUseTime =
            currentTime +
            EffectiveUseCooldown;

        SetDroppedToolAimIndicatorVisible(false);

        RequestCurseDollInteractionRpc(
            targetPoint.HouseId,
            targetPoint.PointType
        );

        return true;
    }

    private void
        SetDroppedToolAimIndicatorVisible(
            bool visible,
            string prompt = "")
    {
        if (!IsOwner ||
            MafiaUIController.Instance == null)
        {
            return;
        }

        MafiaUIController.Instance
            .SetDroppedToolAimIndicatorVisible(
                visible,
                prompt
            );
    }

    [Rpc(
        SendTo.Server,
        InvokePermission =
            RpcInvokePermission.Owner
    )]
    private void
        RequestPickupDroppedRoleToolRpc(
            ulong droppedToolNetworkObjectId)
    {
        if (!IsServer ||
            playerSpirit == null ||
            !playerSpirit.CanUseDoor ||
            MatchManager.Instance == null)
        {
            return;
        }

        double currentTime =
            NetworkManager.ServerTime.Time;

        if (currentTime <
            nextServerUseTime)
        {
            return;
        }

        float maximumDistance =
            interactionDistance +
            serverDistanceTolerance;

        bool pickedUp =
            MatchManager.Instance
                .TryPickupDroppedRoleToolServer(
                    playerSpirit.LinkedClientId,
                    droppedToolNetworkObjectId,
                    maximumDistance
                );

        if (!pickedUp)
            return;

        nextServerUseTime =
            currentTime +
            EffectiveUseCooldown;
    }

    [Rpc(
        SendTo.Server,
        InvokePermission =
            RpcInvokePermission.Owner
    )]
    private void RequestCurseDollInteractionRpc(
        int houseId,
        CurseDollPointType pointType)
    {
        if (!IsServer ||
            playerSpirit == null ||
            !playerSpirit.CanUseDoor ||
            MatchManager.Instance == null)
        {
            return;
        }

        double currentTime =
            NetworkManager.ServerTime.Time;

        if (currentTime < nextServerUseTime)
            return;

        float maximumDistance =
            interactionDistance +
            serverDistanceTolerance;

        bool interacted =
            MatchManager.Instance
                .TryInteractCurseDollServer(
                    playerSpirit.LinkedClientId,
                    houseId,
                    pointType,
                    maximumDistance
                );

        if (!interacted)
            return;

        nextServerUseTime =
            currentTime +
            EffectiveUseCooldown;
    }

    private void TryUseDoor()
    {
        double currentLocalTime =
            NetworkManager.LocalTime.Time;

        double currentServerTime =
            NetworkManager.ServerTime.Time;

        if (!TryFindUsableDoor(
                out House targetHouse,
                out DoorType doorType))
        {
            return;
        }

        bool isHitDoorLocked =
            currentServerTime <
            nextLocalHitDoorLockTime;

        if (currentLocalTime <
                nextLocalUseTime ||
            isHitDoorLocked)
        {
            return;
        }

        if (doorType == DoorType.Front &&
            MatchManager.Instance.IsFrontDoorSealed(
                targetHouse.Id))
        {
            nextLocalUseTime =
                currentLocalTime +
                EffectiveUseCooldown;

            MatchManager.Instance.ShowLocalNotification(
                "문이 잠겨있습니다."
            );

            return;
        }

        nextLocalUseTime =
            currentLocalTime +
            EffectiveUseCooldown;

        RequestUseDoorRpc(
            targetHouse.Id,
            doorType
        );
    }

    private bool TryFindUsableDoor(
        out House targetHouse,
        out DoorType doorType)
    {
        targetHouse = null;
        doorType = DoorType.Front;

        if (MatchManager.Instance == null)
            return false;

        bool canUseBackDoor =
            CanLocalPlayerUseBackDoor();

        float closestDistanceSquared =
            interactionDistance *
            interactionDistance;

        int currentHouseId =
            playerSpirit.CurrentHouseId;

        if (currentHouseId >= 0)
        {
            House currentHouse =
                MatchManager.Instance.GetHouse(
                    currentHouseId
                );

            if (currentHouse == null)
                return false;

            TrySetDoorCandidate(
                currentHouse,
                currentHouse.FrontDoorInsidePoint,
                DoorType.Front,
                ref targetHouse,
                ref doorType,
                ref closestDistanceSquared
            );

            if (canUseBackDoor)
            {
                TrySetDoorCandidate(
                    currentHouse,
                    currentHouse.BackDoorInsidePoint,
                    DoorType.Back,
                    ref targetHouse,
                    ref doorType,
                    ref closestDistanceSquared
                );
            }

            return targetHouse != null;
        }

        for (int i = 0;
             i < MatchManager.Instance.HouseCount;
             i++)
        {
            House house =
                MatchManager.Instance.GetHouse(i);

            if (house == null)
                continue;

            TrySetDoorCandidate(
                house,
                house.FrontDoorOutsidePoint,
                DoorType.Front,
                ref targetHouse,
                ref doorType,
                ref closestDistanceSquared
            );

            if (canUseBackDoor)
            {
                TrySetDoorCandidate(
                    house,
                    house.BackDoorOutsidePoint,
                    DoorType.Back,
                    ref targetHouse,
                    ref doorType,
                    ref closestDistanceSquared
                );
            }
        }

        return targetHouse != null;
    }

    private void TrySetDoorCandidate(
        House house,
        Transform sourcePoint,
        DoorType candidateDoorType,
        ref House closestHouse,
        ref DoorType closestDoorType,
        ref float closestDistanceSquared)
    {
        if (house == null ||
            sourcePoint == null)
        {
            return;
        }

        float distanceSquared =
            GetHorizontalDistanceSquared(
                transform.position,
                sourcePoint.position
            );

        if (distanceSquared >
            closestDistanceSquared)
        {
            return;
        }

        closestDistanceSquared =
            distanceSquared;

        closestHouse =
            house;

        closestDoorType =
            candidateDoorType;
    }

    private bool CanLocalPlayerUseBackDoor()
    {
        if (MatchManager.Instance == null ||
            !MatchManager.Instance
                .TryGetLocalPlayerMatchState(
                    out PlayerMatchState state))
        {
            return false;
        }

        return state.isAlive &&
               state.role ==
               RoleId.Infiltrator;
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Owner
    )]
    private void RequestUseDoorRpc(
        int houseId,
        DoorType doorType)
    {
        if (!IsServer ||
            playerSpirit == null ||
            !playerSpirit.CanUseDoor ||
            MatchManager.Instance == null)
        {
            return;
        }

        double currentTime =
            NetworkManager.ServerTime.Time;

        if (currentTime <
            nextServerHitDoorLockTime)
        {
            return;
        }

        if (currentTime <
            nextServerUseTime)
        {
            return;
        }

        House house =
            MatchManager.Instance.GetHouse(
                houseId
            );

        if (house == null)
            return;

        if (doorType == DoorType.Back &&
            !CanServerPlayerUseBackDoor())
        {
            return;
        }

        if (doorType == DoorType.Front &&
            MatchManager.Instance.IsFrontDoorSealed(
                house.Id))
        {
            nextServerUseTime =
                currentTime +
                EffectiveUseCooldown;

            MatchManager.Instance.SendPrivateNotification(
                playerSpirit.LinkedClientId,
                "문이 잠겨있습니다."
            );

            return;
        }

        if (!TryGetDoorTravelPoints(
                house,
                doorType,
                out Transform sourcePoint,
                out Transform targetPoint,
                out int targetHouseId))
        {
            return;
        }

        float serverUseDistance =
            interactionDistance +
            serverDistanceTolerance;

        float distanceSquared =
            GetHorizontalDistanceSquared(
                transform.position,
                sourcePoint.position
            );

        if (distanceSquared >
            serverUseDistance *
            serverUseDistance)
        {
            return;
        }

        nextServerUseTime =
            currentTime +
            EffectiveUseCooldown;

        playerSpirit.TeleportThroughFrontDoor(
            targetPoint.position,
            targetPoint.rotation,
            targetHouseId
        );

        /*
         * 실제 문을 통해 집 안으로 들어간 경우만
         * 사냥꾼의 방문 기록에 남긴다.
         * 정문과 잠입자 뒷문을 동일하게 처리한다.
         */
        if (targetHouseId >= 0)
        {
            MatchManager.Instance
                .RegisterNightHouseVisit(
                    playerSpirit.LinkedClientId,
                    targetHouseId
                );
        }

        if (interactionAudio != null)
        {
            interactionAudio.PlayServerSpatialSound(
                SpiritInteractionSoundType.Door,
                sourcePoint.position
            );
        }
    }

    private bool CanServerPlayerUseBackDoor()
    {
        if (MatchManager.Instance == null ||
            !MatchManager.Instance
                .TryGetPlayerMatchState(
                    playerSpirit.LinkedClientId,
                    out PlayerMatchState state))
        {
            return false;
        }

        return state.isAlive &&
               state.role ==
               RoleId.Infiltrator;
    }

    private bool TryGetDoorTravelPoints(
        House house,
        DoorType doorType,
        out Transform sourcePoint,
        out Transform targetPoint,
        out int targetHouseId)
    {
        sourcePoint = null;
        targetPoint = null;
        targetHouseId = -1;

        bool isLeaving =
            playerSpirit.CurrentHouseId ==
            house.Id;

        bool isEntering =
            playerSpirit.CurrentHouseId < 0;

        if (!isLeaving &&
            !isEntering)
        {
            return false;
        }

        if (doorType == DoorType.Back)
        {
            sourcePoint =
                isLeaving
                    ? house.BackDoorInsidePoint
                    : house.BackDoorOutsidePoint;

            targetPoint =
                isLeaving
                    ? house.BackDoorOutsidePoint
                    : house.BackDoorInsidePoint;
        }
        else
        {
            sourcePoint =
                isLeaving
                    ? house.FrontDoorInsidePoint
                    : house.FrontDoorOutsidePoint;

            targetPoint =
                isLeaving
                    ? house.FrontDoorOutsidePoint
                    : house.FrontDoorInsidePoint;
        }

        if (sourcePoint == null ||
            targetPoint == null)
        {
            return false;
        }

        targetHouseId =
            isLeaving
                ? -1
                : house.Id;

        return true;
    }

    private float GetHorizontalDistanceSquared(
        Vector3 firstPosition,
        Vector3 secondPosition)
    {
        Vector3 difference =
            firstPosition -
            secondPosition;

        difference.y = 0f;

        return difference.sqrMagnitude;
    }
}
