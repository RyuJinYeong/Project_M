using UnityEngine;

[DisallowMultipleComponent]
public class CurseDollInteractionPoint : MonoBehaviour
{
    [Header("저주 인형 설치 포인트")]
    [SerializeField]
    private CurseDollPointType pointType =
        CurseDollPointType.Home;

    [Tooltip("인형이 표시될 정확한 위치입니다. 비우면 이 오브젝트의 Transform을 사용합니다.")]
    [SerializeField]
    private Transform dollAnchor;

    [Tooltip("설치 상태일 때만 활성화할 인형 외형입니다. 상호작용 Collider와 분리된 자식 오브젝트를 연결해야 합니다.")]
    [SerializeField]
    private GameObject dollVisualRoot;

    [Tooltip("서버 거리 검증에 사용할 Collider입니다. 비우면 이 오브젝트 또는 자식에서 자동으로 찾습니다.")]
    [SerializeField]
    private Collider interactionCollider;

    private House house;
    private bool lastVisibleState;

    public CurseDollPointType PointType => pointType;
    public House House => house;
    public int HouseId => house != null ? house.Id : -1;
    public Vector3 InteractionPosition =>
        dollAnchor != null
            ? dollAnchor.position
            : transform.position;

    public Quaternion InteractionRotation =>
        dollAnchor != null
            ? dollAnchor.rotation
            : transform.rotation;

    private void Awake()
    {
        house = GetComponentInParent<House>();

        if (interactionCollider == null)
        {
            interactionCollider =
                GetComponent<Collider>();
        }

        if (interactionCollider == null)
        {
            interactionCollider =
                GetComponentInChildren<Collider>(true);
        }

        SetDollVisible(false);
    }

    private void Update()
    {
        bool visible =
            MatchManager.Instance != null &&
            HouseId >= 0 &&
            MatchManager.Instance
                .IsCurseDollInstalledAtPoint(
                    HouseId,
                    pointType
                );

        if (visible != lastVisibleState)
            SetDollVisible(visible);
    }

    public void BindHouse(House targetHouse)
    {
        if (targetHouse != null)
            house = targetHouse;
    }

    public bool IsNear(
        Vector3 worldPosition,
        float maximumDistance)
    {
        if (maximumDistance < 0f)
            return false;

        Vector3 closestPoint =
            interactionCollider != null
                ? interactionCollider.ClosestPoint(
                    worldPosition
                )
                : InteractionPosition;

        return (worldPosition - closestPoint).sqrMagnitude <=
               maximumDistance * maximumDistance;
    }

    private void SetDollVisible(bool visible)
    {
        lastVisibleState = visible;

        if (dollVisualRoot != null &&
            dollVisualRoot != gameObject)
        {
            dollVisualRoot.SetActive(visible);
        }
    }
}
