using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(PlayerSpirit))]
public class CurseDollCarryView : NetworkBehaviour
{
    [Header("저주 인형 소지 외형")]
    [Tooltip("소지 중일 때 표시할 인형 모델 루트입니다. 영체 프리팹 안에 미리 배치한 비활성 오브젝트를 연결합니다.")]
    [SerializeField]
    private GameObject dollVisualRoot;

    [Tooltip("로컬 소유자가 보는 1인칭 위치입니다. 영체 카메라의 자식으로 두고 화면 하단에 맞춥니다.")]
    [SerializeField]
    private Transform firstPersonAnchor;

    [Tooltip("다른 플레이어가 보는 위치입니다. 영체 모델의 전면 또는 손 위치에 둡니다.")]
    [SerializeField]
    private Transform worldAnchor;

    private PlayerSpirit playerSpirit;
    private Transform currentAnchor;
    private bool isVisible;

    private void Awake()
    {
        playerSpirit =
            GetComponent<PlayerSpirit>();

        SetVisible(false, null);
    }

    public override void OnNetworkSpawn()
    {
        RefreshPresentation(true);
    }

    public override void OnNetworkDespawn()
    {
        SetVisible(false, null);
    }

    private void OnDisable()
    {
        if (!IsSpawned)
            SetVisible(false, null);
    }

    private void Update()
    {
        RefreshPresentation(false);
    }

    private void RefreshPresentation(
        bool forceRefresh)
    {
        if (!IsSpawned ||
            playerSpirit == null ||
            MatchManager.Instance == null)
        {
            SetVisible(false, null);
            return;
        }

        CurseDollStateData state =
            MatchManager.Instance.CurseDollState;

        bool shouldShow =
            state.location ==
                CurseDollLocation.Held &&
            state.holderClientId ==
                playerSpirit.LinkedClientId;

        Transform targetAnchor = null;

        if (shouldShow)
        {
            targetAnchor =
                IsOwner && firstPersonAnchor != null
                    ? firstPersonAnchor
                    : worldAnchor;

            shouldShow = targetAnchor != null;
        }

        if (!forceRefresh &&
            shouldShow == isVisible &&
            targetAnchor == currentAnchor)
        {
            return;
        }

        SetVisible(
            shouldShow,
            targetAnchor
        );
    }

    private void SetVisible(
        bool visible,
        Transform targetAnchor)
    {
        isVisible = visible;
        currentAnchor = targetAnchor;

        if (dollVisualRoot == null)
            return;

        if (visible && targetAnchor != null)
        {
            Transform dollTransform =
                dollVisualRoot.transform;

            if (targetAnchor == dollTransform ||
                targetAnchor.IsChildOf(dollTransform))
            {
                Debug.LogError(
                    "CurseDollCarryView의 앵커는 인형 외형 자신의 자식일 수 없습니다."
                );

                dollVisualRoot.SetActive(false);
                isVisible = false;
                currentAnchor = null;
                return;
            }

            if (dollTransform.parent != targetAnchor)
            {
                dollTransform.SetParent(
                    targetAnchor,
                    false
                );
            }

            dollTransform.localPosition =
                Vector3.zero;
            dollTransform.localRotation =
                Quaternion.identity;
        }

        dollVisualRoot.SetActive(visible);
    }
}
