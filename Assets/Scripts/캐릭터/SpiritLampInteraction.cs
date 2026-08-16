using MafiaGame.UI;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(PlayerSpirit))]
public class SpiritLampInteraction : NetworkBehaviour
{
    [Header("Raycast")]
    [SerializeField] private Camera playerCamera;
    [SerializeField] private LayerMask lampRaycastMask;

    [Header("Interaction")]
    [Min(0.1f)]
    [SerializeField] private float interactionDistance = 2f;

    [Min(0f)]
    [SerializeField] private float useCooldown = 0.35f;

    [SerializeField] private Key interactionKey = Key.F;

    [Header("Aim Feedback")]
    [Tooltip("자기 집의 꺼진 전등을 정확히 조준했을 때만 표시할 화면 중앙 아이콘입니다.")]
    [SerializeField] private GameObject validAimIndicator;

    private PlayerSpirit playerSpirit;
    private SpiritInteractionAudio interactionAudio;
    private double nextLocalUseTime;

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

        SetAimIndicatorVisible(false);
    }

    private void OnDisable()
    {
        if (IsOwner)
            SetAimIndicatorVisible(false);
    }

    private void Update()
    {
        bool canCheckLamp =
            IsOwner &&
            IsSpawned &&
            playerSpirit != null &&
            playerSpirit.CanUseHouseLamp &&
            playerSpirit.CurrentHouseId < 0 &&
            MatchManager.Instance != null &&
            !MatchManager.Instance.IsVillageFullyLit &&
            !MatchManager.Instance
                .HasPlayerUsedHouseLamp(
                    playerSpirit.LinkedClientId
                ) &&
            MatchManager.Instance.CurrentPhase ==
                MatchPhase.NightAction;

        if (!canCheckLamp)
        {
            SetAimIndicatorVisible(false);
            return;
        }

        bool hasValidTarget =
            TryGetAimedOwnHouseLamp(
                out House targetHouse
            );

        SetAimIndicatorVisible(
            hasValidTarget
        );

        Keyboard keyboard = Keyboard.current;

        if (!hasValidTarget ||
            keyboard == null ||
            !keyboard[interactionKey]
                .wasPressedThisFrame)
        {
            return;
        }

        double currentTime =
            NetworkManager.LocalTime.Time;

        if (currentTime < nextLocalUseTime)
            return;

        nextLocalUseTime =
            currentTime +
            useCooldown;

        SetAimIndicatorVisible(false);

        RequestLightLampRpc(
            targetHouse.Id
        );
    }

    private void SetAimIndicatorVisible(
        bool visible)
    {
        if (validAimIndicator != null &&
            validAimIndicator.activeSelf != visible)
        {
            validAimIndicator.SetActive(visible);
        }

        if (IsOwner && MafiaUIController.Instance != null)
        {
            MafiaUIController.Instance
                .SetLampAimIndicatorVisible(visible);
        }
    }

    private bool TryGetAimedOwnHouseLamp(
        out House targetHouse)
    {
        targetHouse = null;

        if (playerCamera == null ||
            playerSpirit == null ||
            playerSpirit.CurrentHouseId >= 0)
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
                interactionDistance,
                lampRaycastMask,
                QueryTriggerInteraction.Collide))
        {
            return false;
        }

        House house =
            hit.collider.GetComponentInParent<House>();

        if (house == null ||
            house.Id < 0 ||
            house.Id != playerSpirit.HomeHouseId ||
            !house.IsLampInteractionCollider(
                hit.collider) ||
            MatchManager.Instance.IsVillageFullyLit ||
            MatchManager.Instance
                .HasPlayerUsedHouseLamp(
                    playerSpirit.LinkedClientId
                ) ||
            MatchManager.Instance.IsHouseLampLit(
                house.Id))
        {
            return false;
        }

        targetHouse = house;
        return true;
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Owner
    )]
    private void RequestLightLampRpc(int houseId)
    {
        if (!IsServer ||
            playerSpirit == null ||
            !playerSpirit.CanUseHouseLamp ||
            playerSpirit.CurrentHouseId >= 0 ||
            MatchManager.Instance == null)
        {
            return;
        }

        if (MatchManager.Instance.IsVillageFullyLit)
        {
            MatchManager.Instance
                .SendPrivateNotification(
                    playerSpirit.LinkedClientId,
                    "마을이 이미 밝아져 횃불을 사용할 필요가 없습니다."
                );

            return;
        }

        if (MatchManager.Instance
                .HasPlayerUsedHouseLamp(
                    playerSpirit.LinkedClientId
                ))
        {
            MatchManager.Instance
                .SendPrivateNotification(
                    playerSpirit.LinkedClientId,
                    "횃불은 한 게임에 한 번만 켤 수 있습니다."
                );

            return;
        }

        if (!MatchManager.Instance.TryLightHouseLamp(
                playerSpirit.LinkedClientId,
                houseId,
                transform.position,
                out Vector3 lampPosition))
        {
            return;
        }

        if (interactionAudio != null)
        {
            interactionAudio.PlayServerSpatialSound(
                SpiritInteractionSoundType.Lamp,
                lampPosition
            );
        }
    }
}
