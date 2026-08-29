using System;
using TMPro;
using UnityEngine;

public class House : MonoBehaviour
{
    public const ulong NoOwnerClientId =
        ulong.MaxValue;

    [Header("집 내부 영역")]
    [SerializeField] private BoxCollider interiorArea;
    [SerializeField] private BoxCollider additionalInteriorArea;

    [Header("육체 및 영체 생성 위치")]
    [SerializeField] private Transform bodyPoint;
    [SerializeField] private Transform spiritPoint;

    [Header("집 주인 표시")]
    [SerializeField] private PlayerIdentityLabel ownerIdentityLabel;

    [Tooltip("집 주인이 사망했을 때 표시할 선택적 TMP 텍스트입니다. 비워두면 기존 닉네임 아래에 자동으로 (사망)을 붙입니다.")]
    [SerializeField] private TMP_Text ownerDeathStatusText;

    [Header("정문")]
    [SerializeField] private Transform frontDoorOutsidePoint;
    [SerializeField] private Transform frontDoorInsidePoint;

    [Header("집 앞 전등")]
    [Tooltip("F 상호작용을 받을 전등 Collider입니다.")]
    [SerializeField] private Collider lampInteractionCollider;

    [Tooltip("전등이 켜졌을 때 활성화할 파티클 또는 조명 루트입니다.")]
    [SerializeField] private GameObject lampEffectRoot;

    [Header("주취자")]
    [Tooltip("주취자가 이 집 앞에서 잘 때 사용할 벤치 또는 침대 위치입니다.")]
    [SerializeField] private Transform drunkardSleepPoint;

    [Tooltip("이 집 앞에서 자던 주취자가 사망할 때 시체를 놓을 바닥 위치입니다.")]
    [SerializeField] private Transform drunkardDeathPoint;

    [Header("저주 인형")]
    [Tooltip("이 집 주인의 침대 옆 탁자 등에 배치할 일반 저주 인형 설치 포인트입니다.")]
    [SerializeField]
    private CurseDollInteractionPoint curseDollPoint;

    [Tooltip("이 집 앞에서 자는 주취자의 육체 옆에 배치할 전용 저주 인형 설치 포인트입니다.")]
    [SerializeField]
    private CurseDollInteractionPoint drunkardCurseDollPoint;

    [Header("감옥")]
    [Tooltip("집 외부 감옥 프리팹 안의 CageInsidePoint입니다.")]
    [SerializeField] private Transform outsideCagePoint;

    [Tooltip("집 내부 감옥 프리팹 안의 CageInsidePoint입니다.")]
    [SerializeField] private Transform insideCagePoint;

    [Header("뒷문")]
    [Tooltip("추후 뒷문 시스템을 다시 사용할 경우를 위한 필드입니다.")]
    [SerializeField] private Transform backDoorOutsidePoint;
    [SerializeField] private Transform backDoorInsidePoint;

    public int Id { get; private set; } = -1;

    public ulong OwnerClientId
    {
        get;
        private set;
    } = NoOwnerClientId;

    private string ownerPlayerName =
        string.Empty;

    private bool ownerIsAlive = true;

    public BoxCollider InteriorArea => interiorArea;
    public Transform BodyPoint => bodyPoint;
    public Transform SpiritPoint => spiritPoint;
    public PlayerIdentityLabel OwnerIdentityLabel => ownerIdentityLabel;
    public Transform FrontDoorOutsidePoint => frontDoorOutsidePoint;
    public Transform FrontDoorInsidePoint => frontDoorInsidePoint;
    public Collider LampInteractionCollider => lampInteractionCollider;
    public Vector3 LampInteractionPosition =>
        lampInteractionCollider != null
            ? lampInteractionCollider.bounds.center
            : transform.position;
    public Transform DrunkardSleepPoint => drunkardSleepPoint;
    public Transform DrunkardDeathPoint => drunkardDeathPoint;
    public CurseDollInteractionPoint CurseDollPoint =>
        curseDollPoint;
    public CurseDollInteractionPoint DrunkardCurseDollPoint =>
        drunkardCurseDollPoint;
    public Transform OutsideCagePoint => outsideCagePoint;
    public Transform InsideCagePoint => insideCagePoint;
    public Transform BackDoorOutsidePoint => backDoorOutsidePoint;
    public Transform BackDoorInsidePoint => backDoorInsidePoint;

    private void Awake()
    {
        CacheCurseDollPoints();

        if (ownerIdentityLabel == null)
        {
            ownerIdentityLabel =
                GetComponentInChildren
                    <PlayerIdentityLabel>(true);
        }

        if (ownerDeathStatusText == null)
        {
            ownerDeathStatusText =
                FindOwnerDeathStatusText();
        }

        SetLampActiveLocal(false);
    }

    public void Initialize(int id)
    {
        Id = id;
        CacheCurseDollPoints();
        ClearOwner();
        SetLampActiveLocal(false);
    }

    public CurseDollInteractionPoint
        GetCurseDollPoint(
            CurseDollPointType pointType)
    {
        return pointType ==
               CurseDollPointType.Drunkard
            ? drunkardCurseDollPoint
            : curseDollPoint;
    }

    private void CacheCurseDollPoints()
    {
        CurseDollInteractionPoint[] points =
            GetComponentsInChildren
                <CurseDollInteractionPoint>(true);

        for (int i = 0; i < points.Length; i++)
        {
            CurseDollInteractionPoint point =
                points[i];

            if (point == null)
                continue;

            point.BindHouse(this);

            if (point.PointType ==
                CurseDollPointType.Drunkard)
            {
                if (drunkardCurseDollPoint == null)
                    drunkardCurseDollPoint = point;
            }
            else if (curseDollPoint == null)
            {
                curseDollPoint = point;
            }
        }
    }

    public void AssignOwner(
        ulong clientId,
        string playerName)
    {
        OwnerClientId = clientId;

        ownerPlayerName =
            playerName ?? string.Empty;

        ownerIsAlive = true;

        RefreshOwnerDisplay();
    }

    public void SetOwnerAlive(
        bool isAlive)
    {
        if (OwnerClientId ==
            NoOwnerClientId)
        {
            return;
        }

        ownerIsAlive = isAlive;
        RefreshOwnerDisplay();
    }

    public void ClearOwner()
    {
        OwnerClientId = NoOwnerClientId;
        ownerPlayerName = string.Empty;
        ownerIsAlive = true;

        if (ownerIdentityLabel != null)
            ownerIdentityLabel.Clear();

        if (ownerDeathStatusText != null)
        {
            ownerDeathStatusText.text =
                string.Empty;

            ownerDeathStatusText.gameObject
                .SetActive(false);
        }
    }

    private void RefreshOwnerDisplay()
    {
        if (ownerIdentityLabel == null ||
            OwnerClientId ==
                NoOwnerClientId)
        {
            return;
        }

        /*
         * 별도 사망 텍스트가 연결돼 있으면 이름과 상태를
         * 분리해서 표시한다. 연결되지 않은 기존 프리팹도
         * 즉시 작동하도록 닉네임 줄 아래에 폴백 표시한다.
         */
        if (ownerDeathStatusText != null)
        {
            ownerIdentityLabel.SetPlayer(
                OwnerClientId,
                ownerPlayerName
            );

            ownerDeathStatusText.text =
                "(사망)";

            ownerDeathStatusText.gameObject
                .SetActive(!ownerIsAlive);

            return;
        }

        string displayName =
            ownerIsAlive
                ? ownerPlayerName
                : ownerPlayerName +
                  "\n<color=#C95E5E>(사망)</color>";

        ownerIdentityLabel.SetPlayer(
            OwnerClientId,
            displayName
        );
    }

    private TMP_Text
        FindOwnerDeathStatusText()
    {
        TMP_Text[] texts =
            GetComponentsInChildren
                <TMP_Text>(true);

        for (int i = 0;
             i < texts.Length;
             i++)
        {
            string objectName =
                texts[i].gameObject.name;

            if (objectName.IndexOf(
                    "DeathStatus",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                objectName.IndexOf(
                    "DeadStatus",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return texts[i];
            }
        }

        return null;
    }

    public void SetLampActiveLocal(bool active)
    {
        if (lampEffectRoot != null)
            lampEffectRoot.SetActive(active);
    }

    public bool IsLampInteractionCollider(Collider targetCollider)
    {
        if (lampInteractionCollider == null ||
            targetCollider == null)
        {
            return false;
        }

        return targetCollider == lampInteractionCollider ||
               targetCollider.transform.IsChildOf(
                   lampInteractionCollider.transform);
    }

    public bool IsNearLamp(
        Vector3 worldPosition,
        float maximumDistance)
    {
        if (lampInteractionCollider == null ||
            maximumDistance < 0f)
        {
            return false;
        }

        Vector3 closestPoint =
            lampInteractionCollider.ClosestPoint(
                worldPosition
            );

        return (worldPosition - closestPoint).sqrMagnitude <=
               maximumDistance * maximumDistance;
    }

    public bool ContainsPosition(Vector3 worldPosition)
    {
        return ContainsPosition(
                   interiorArea,
                   worldPosition
               ) ||
               ContainsPosition(
                   additionalInteriorArea,
                   worldPosition
               );
    }

    private static bool ContainsPosition(
        BoxCollider area,
        Vector3 worldPosition)
    {
        if (area == null)
            return false;

        Transform areaTransform = area.transform;
        Vector3 localPosition = areaTransform.InverseTransformPoint(worldPosition);

        localPosition -= area.center;

        Vector3 halfSize = area.size * 0.5f;

        return Mathf.Abs(localPosition.x) <= halfSize.x &&
               Mathf.Abs(localPosition.y) <= halfSize.y &&
               Mathf.Abs(localPosition.z) <= halfSize.z;
    }

    public bool IsNearFrontDoorOutside(Vector3 worldPosition, float maximumDistance)
    {
        if (frontDoorOutsidePoint == null || maximumDistance < 0f)
            return false;

        if (ContainsPosition(worldPosition))
            return false;

        Vector3 playerPosition = worldPosition;
        Vector3 outsidePosition = frontDoorOutsidePoint.position;

        playerPosition.y = 0f;
        outsidePosition.y = 0f;

        float sqrDistance = (playerPosition - outsidePosition).sqrMagnitude;

        return sqrDistance <= maximumDistance * maximumDistance;
    }

    public bool IsNearBackDoorOutside(
        Vector3 worldPosition,
        float maximumDistance)
    {
        return IsNearDoorPoint(
            backDoorOutsidePoint,
            worldPosition,
            maximumDistance,
            false
        );
    }

    public bool IsNearBackDoorInside(
        Vector3 worldPosition,
        float maximumDistance)
    {
        return IsNearDoorPoint(
            backDoorInsidePoint,
            worldPosition,
            maximumDistance,
            true
        );
    }

    private bool IsNearDoorPoint(
        Transform doorPoint,
        Vector3 worldPosition,
        float maximumDistance,
        bool mustBeInside)
    {
        if (doorPoint == null ||
            maximumDistance < 0f ||
            ContainsPosition(worldPosition) != mustBeInside)
        {
            return false;
        }

        Vector3 playerPosition =
            worldPosition;

        Vector3 doorPosition =
            doorPoint.position;

        playerPosition.y = 0f;
        doorPosition.y = 0f;

        return (
            playerPosition -
            doorPosition
        ).sqrMagnitude <=
        maximumDistance *
        maximumDistance;
    }
}
