using UnityEngine;

[DisallowMultipleComponent]
public class NightDutyPoint : MonoBehaviour
{
    [Header("야간 직무 지점")]
    [Tooltip("같은 집 안에서 중복되지 않는 번호입니다. 집 밖 공용 지점끼리도 중복되지 않게 설정해야 합니다.")]
    [Min(0)]
    [SerializeField] private int pointId;

    [Tooltip("플레이어에게 안내할 직무 이름입니다.")]
    [SerializeField] private string dutyName = "야간 직무";

    [Tooltip("거리 판정의 기준이 될 Collider입니다. 비우면 이 오브젝트 또는 자식에서 자동으로 찾습니다.")]
    [SerializeField] private Collider interactionCollider;

    private House house;

    public int PointId => pointId;
    public string DutyName => string.IsNullOrWhiteSpace(dutyName)
        ? "야간 직무"
        : dutyName;
    public int HouseId => house != null ? house.Id : -1;
    public bool CanInteract =>
        isActiveAndEnabled &&
        interactionCollider != null &&
        interactionCollider.enabled &&
        interactionCollider.gameObject.activeInHierarchy;

    private void Awake()
    {
        house = GetComponentInParent<House>();

        if (interactionCollider == null)
            interactionCollider = GetComponent<Collider>();

        if (interactionCollider == null)
        {
            interactionCollider =
                GetComponentInChildren<Collider>(true);
        }
    }

    public bool Matches(int houseId, int targetPointId)
    {
        return HouseId == houseId &&
               pointId == targetPointId;
    }

    public bool IsNear(
        Vector3 worldPosition,
        float maximumDistance)
    {
        if (interactionCollider == null ||
            maximumDistance < 0f)
        {
            return false;
        }

        Vector3 closestPoint =
            interactionCollider.ClosestPoint(
                worldPosition
            );

        return (worldPosition - closestPoint).sqrMagnitude <=
               maximumDistance * maximumDistance;
    }
}
