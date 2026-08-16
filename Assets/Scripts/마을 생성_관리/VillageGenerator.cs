using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class VillageGenerator : MonoBehaviour
{
    [System.Serializable]
    public struct PlazaPropOption
    {
        [Tooltip("광장에 배치할 프리팹입니다.")]
        public GameObject prefab;

        [Min(0.1f)]
        [Tooltip("다른 광장 구조물과 겹침을 검사할 반지름입니다.")]
        public float placementRadius;

        [Min(1)]
        [Tooltip("무작위 선택 가중치입니다. 1 이하는 모두 1로 처리합니다.")]
        public int weight;

        [Tooltip("적용할 최소·최대 균일 스케일입니다. 둘 다 0이면 1로 처리합니다.")]
        public Vector2 scaleRange;

        [Tooltip("바닥 기준 Y 위치 보정입니다.")]
        public float verticalOffset;

        [Tooltip("켜면 구조물의 정면이 마을 중앙을 향합니다. 끄면 Y 회전이 무작위로 정해집니다.")]
        public bool faceVillageCenter;
    }

    private struct PlazaOccupiedCircle
    {
        public Vector3 localPosition;
        public float radius;

        public PlazaOccupiedCircle(
            Vector3 localPosition,
            float radius)
        {
            this.localPosition = localPosition;
            this.radius = radius;
        }
    }

    private const int MinPlayerCount = 2;
    private const int MaxPlayerCount = 12;
    private const float PlanePrimitiveSize = 10f;
    private const int OuterTreeSeedOffset = 1000;
    private const int MajorPlazaPropSeedOffset = 2000;
    private const int MinorPlazaPropSeedOffset = 3000;

    [Header("연결 참조")]
    [Tooltip("생성된 집들을 등록할 레지스트리")]
    [SerializeField] private VillageHouseRegistry houseRegistry;

    [Header("생성 인원 및 시드")]
    [Range(MinPlayerCount, MaxPlayerCount)]
    [Tooltip("생성할 집의 개수입니다. 기본적으로 플레이어 수와 같습니다.")]
    public int playerCount = 8;

    [Min(1)]
    [Tooltip("집 외형과 외곽 나무 배치에 사용할 시드입니다. 같은 시드는 같은 배치를 생성합니다.")]
    public int currentLayoutSeed = 1;

    public float CurrentHouseRadius =>
        GetHouseRadius();

    public float ReferenceHouseRadius =>
        Mathf.Max(0.1f, minHouseRadius);

    [Header("집 프리팹")]
    [Tooltip("배치할 집 프리팹 목록입니다. 현재 집 하나만 넣어도 같은 집이 인원수만큼 반복 생성됩니다.")]
    public GameObject[] housePrefabs;

    [Tooltip("집 프리팹에서 첫 번째 담장 연결점을 찾을 오브젝트 이름입니다.")]
    public string fenceAnchorAName = "FenceAnchor_A";

    [Tooltip("집 프리팹에서 두 번째 담장 연결점을 찾을 오브젝트 이름입니다.")]
    public string fenceAnchorBName = "FenceAnchor_B";

    [Header("마을 크기")]
    [Min(0f)]
    [Tooltip("4인 기준 집 배치 반지름입니다.")]
    public float minHouseRadius = 16f;

    [Min(0f)]
    [Tooltip("4인을 초과하거나 미달하는 플레이어 1명당 집 배치 반지름에 더할 값입니다.")]
    public float radiusStepPerPlayer = 2.63f;

    [Min(0f)]
    [Tooltip("집 배치 반지름에서 외곽 바리케이드까지 확보할 기본 거리입니다.")]
    public float outerBoundaryBaseOffset = 3.67f;

    [Min(0f)]
    [Tooltip("집과 외곽 바리케이드 사이에 추가로 확보할 여유 공간입니다.")]
    public float alleyWidth = 4.5f;

    [Tooltip("각 House_Parent를 기준으로 실제 집 프리팹을 이동시킬 로컬 위치입니다.")]
    public Vector3 houseOffset = new Vector3(1.9f, 0f, -1.7f);

    [Header("집 사이 바리케이드")]
    [Tooltip("인접한 집 사이를 막을 바리케이드 프리팹입니다.")]
    public GameObject stoneFencePrefab;

    [Min(0.01f)]
    [Tooltip("집 사이 바리케이드 프리팹의 로컬 X축 기준 실제 길이입니다.")]
    public float stoneFenceLength = 2.85f;

    [Min(0f)]
    [Tooltip("집 사이에 나열되는 바리케이드끼리 겹칠 길이입니다.")]
    public float stoneFenceOverlap = 0.2f;

    [Min(0f)]
    [Tooltip("집 모서리와 바리케이드 사이에 남길 여유 거리입니다.")]
    public float sightFenceClearance = 0.05f;

    [Header("중앙 광장 고정 구조물")]
    [Tooltip("마을 중앙에 항상 생성할 큰 나무 또는 중앙 광장 프리팹입니다.")]
    public GameObject centerPlazaPrefab;

    [Tooltip("VillageGenerator 중심을 기준으로 한 로컬 위치 보정입니다.")]
    public Vector3 centerPlazaOffset = Vector3.zero;

    [Tooltip("중앙 광장 프리팹에 적용할 로컬 회전입니다.")]
    public Vector3 centerPlazaEulerAngles = Vector3.zero;

    [Min(0.01f)]
    [Tooltip("중앙 광장 프리팹에 적용할 균일 스케일입니다.")]
    public float centerPlazaScale = 1f;

    [Min(0f)]
    [Tooltip("중앙 광장 주변에 다른 구조물을 배치하지 않을 반지름입니다.")]
    public float centerPlazaClearRadius = 4f;

    [Header("주요 광장 구조물")]
    [Tooltip("마차, 천막, 우물처럼 시야를 확실하게 가릴 구조물 목록입니다.")]
    public PlazaPropOption[] majorPlazaProps;

    [Range(0, 12)]
    [Tooltip("최소 인원일 때 생성할 주요 구조물 수입니다.")]
    public int minimumMajorPlazaPropCount = 3;

    [Range(0, 12)]
    [Tooltip("최대 인원일 때 생성할 주요 구조물 수입니다.")]
    public int maximumMajorPlazaPropCount = 6;

    [Range(0f, 1f)]
    [Tooltip("광장 안쪽과 바깥쪽 사이에서 주요 구조물을 배치할 기본 비율입니다.")]
    public float majorPlazaRingRadiusRatio = 0.55f;

    [Min(0f)]
    [Tooltip("주요 구조물의 기본 원형 슬롯 반지름에 적용할 흔들림입니다.")]
    public float majorPlazaRadialJitter = 1.25f;

    [Range(0f, 45f)]
    [Tooltip("주요 구조물의 균등 슬롯 각도에 적용할 흔들림입니다.")]
    public float majorPlazaAngularJitter = 10f;

    [Header("보조 광장 구조물")]
    [Tooltip("상자, 통, 벤치, 장작더미처럼 빈 공간을 채울 구조물 목록입니다.")]
    public PlazaPropOption[] minorPlazaProps;

    [Range(0, 24)]
    [Tooltip("최소 인원일 때 생성할 보조 구조물 수입니다.")]
    public int minimumMinorPlazaPropCount = 4;

    [Range(0, 24)]
    [Tooltip("최대 인원일 때 생성할 보조 구조물 수입니다.")]
    public int maximumMinorPlazaPropCount = 10;

    [Header("광장 배치 제한")]
    [Min(0f)]
    [Tooltip("집 배치 반지름에서 안쪽으로 확보할 광장 외곽 여백입니다.")]
    public float plazaHouseClearance = 5f;

    [Min(0f)]
    [Tooltip("각 집 정문 바깥 지점 주변에 구조물을 두지 않을 반지름입니다.")]
    public float frontDoorClearRadius = 3f;

    [Min(0f)]
    [Tooltip("정문에서 마을 중앙 방향으로 비워둘 출입 구간 길이입니다.")]
    public float frontDoorPathLength = 4f;

    [Min(0f)]
    [Tooltip("정문 출입 구간의 좌우 절반 너비입니다.")]
    public float frontDoorPathHalfWidth = 1.25f;

    [Min(0f)]
    [Tooltip("광장 구조물끼리 추가로 확보할 간격입니다.")]
    public float plazaPropSpacing = 0.5f;

    [Range(1, 100)]
    [Tooltip("구조물 하나를 배치할 때 시도할 최대 횟수입니다.")]
    public int plazaPlacementAttemptCount = 30;

    [Header("외곽 바리케이드")]
    [Tooltip("외곽에 교대로 배치할 첫 번째 바리케이드 프리팹입니다.")]
    public GameObject outerFencePrefabC;

    [Tooltip("외곽에 교대로 배치할 두 번째 바리케이드 프리팹입니다. 한 종류만 사용할 경우 비워두거나 C와 같은 프리팹을 넣어도 됩니다.")]
    public GameObject outerFencePrefabD;

    [Min(0.01f)]
    [Tooltip("외곽 바리케이드 프리팹의 로컬 X축 기준 실제 길이입니다.")]
    public float outerFenceLength = 2.85f;

    [Min(0f)]
    [Tooltip("외곽에 나열되는 바리케이드끼리 겹칠 길이입니다.")]
    public float outerFenceOverlap = 0.2f;

    [Header("외곽 나무")]
    [Tooltip("외곽 바리케이드 바깥에 배치할 나무 프리팹 목록입니다.")]
    public GameObject[] outerTreePrefabs;

    [Range(0, 5)]
    [Tooltip("외곽 바리케이드 바깥에 생성할 나무 줄의 개수입니다. 0이면 생성하지 않습니다.")]
    public int outerTreeRows = 3;

    [Min(0.1f)]
    [Tooltip("같은 줄에서 나무 사이에 둘 평균 간격입니다.")]
    public float outerTreeSpacing = 3.5f;

    [Min(0f)]
    [Tooltip("외곽 바리케이드에서 첫 번째 나무 줄까지의 거리입니다.")]
    public float outerTreeInnerOffset = 2.5f;

    [Min(0f)]
    [Tooltip("각 나무 줄 사이의 거리입니다.")]
    public float outerTreeRowSpacing = 2.5f;

    [Min(0f)]
    [Tooltip("나무 위치에 적용할 무작위 흔들림 범위입니다.")]
    public float outerTreePositionJitter = 0.75f;

    [Tooltip("나무에 적용할 최소·최대 균일 스케일입니다.")]
    public Vector2 outerTreeScaleRange = new Vector2(0.85f, 1.15f);

    [Header("바닥")]
    [Tooltip("생성할 바닥에 적용할 머티리얼입니다.")]
    public Material groundMaterial;

    [Min(0.1f)]
    [Tooltip("바닥 텍스처가 월드 공간에서 한 번 반복될 대략적인 크기입니다.")]
    public float groundTextureWorldSize = 4f;

    [Min(0f)]
    [Tooltip("외곽 나무 영역 바깥에 추가로 확보할 바닥 여백입니다.")]
    public float groundMargin = 2f;

    private readonly List<House> generatedHouses = new List<House>();

    private readonly List<Transform> houseFenceAnchorA = new List<Transform>();

    private readonly List<Transform> houseFenceAnchorB = new List<Transform>();

    public void Start()
    {
        GenerateVillage();
    }

    [ContextMenu("마을 생성")]
    public void GenerateVillage()
    {
        GenerateVillage(playerCount, currentLayoutSeed);
    }

    public void GenerateVillage(
        int targetPlayerCount)
    {
        GenerateVillage(
            targetPlayerCount,
            currentLayoutSeed
        );
    }

    public void GenerateVillage(
        int targetPlayerCount,
        int layoutSeed)
    {
        if (!HasValidHousePrefab())
        {
            Debug.LogError(
                "House Prefabs 배열에 사용할 수 있는 " +
                "집 프리팹이 없습니다.",
                this
            );

            return;
        }

        ClearVillage();

        playerCount = Mathf.Clamp(
            targetPlayerCount,
            MinPlayerCount,
            MaxPlayerCount
        );

        currentLayoutSeed =
            layoutSeed == 0
                ? 1
                : layoutSeed;

        CreateGround();
        SpawnHouses();
        SpawnSightBlockingFences();
        SpawnPlazaObjects();
        SpawnOuterFences();
        SpawnOuterTrees();

        if (houseRegistry != null)
        {
            houseRegistry.Initialize(
                generatedHouses
            );
        }
        else
        {
            Debug.LogError(
                "VillageHouseRegistry가 연결되지 않았습니다.",
                this
            );
        }

        Debug.Log(
            $"마을 생성 완료 - " +
            $"집 수: {playerCount}, " +
            $"배치 시드: {currentLayoutSeed}",
            this
        );
    }

    [ContextMenu("마을 제거")]
    public void ClearVillage()
    {
        generatedHouses.Clear();
        houseFenceAnchorA.Clear();
        houseFenceAnchorB.Clear();

        if (houseRegistry != null)
        {
            houseRegistry.Clear();
        }

        for (int i = transform.childCount - 1;
             i >= 0;
             i--)
        {
            DestroyGeneratedObject(
                transform.GetChild(i).gameObject
            );
        }
    }

    private bool HasValidHousePrefab()
    {
        if (housePrefabs == null)
        {
            return false;
        }

        for (int i = 0;
             i < housePrefabs.Length;
             i++)
        {
            if (housePrefabs[i] != null)
            {
                return true;
            }
        }

        return false;
    }

    private float GetHouseRadius()
    {
        return
            minHouseRadius +
            radiusStepPerPlayer *
            (playerCount - 4);
    }

    private float GetOuterBoundaryRadius()
    {
        return
            GetHouseRadius() +
            outerBoundaryBaseOffset +
            alleyWidth;
    }

    private float GetOuterTreeMaximumRadius()
    {
        if (outerTreeRows <= 0)
        {
            return GetOuterBoundaryRadius();
        }

        return
            GetOuterBoundaryRadius() +
            outerTreeInnerOffset +
            Mathf.Max(
                0,
                outerTreeRows - 1
            ) *
            outerTreeRowSpacing +
            outerTreePositionJitter;
    }

    private List<GameObject> CreateHousePrefabOrder(
        int targetCount)
    {
        List<GameObject> validPrefabs =
            new List<GameObject>();

        for (int i = 0;
             i < housePrefabs.Length;
             i++)
        {
            GameObject prefab =
                housePrefabs[i];

            if (prefab != null)
            {
                validPrefabs.Add(
                    prefab
                );
            }
        }

        List<GameObject> prefabOrder =
            new List<GameObject>(
                targetCount
            );

        System.Random random =
            new System.Random(
                currentLayoutSeed
            );

        while (prefabOrder.Count <
               targetCount)
        {
            List<GameObject> shuffledBatch =
                new List<GameObject>(
                    validPrefabs
                );

            for (int i =
                     shuffledBatch.Count - 1;
                 i > 0;
                 i--)
            {
                int swapIndex =
                    random.Next(i + 1);

                GameObject temporary =
                    shuffledBatch[i];

                shuffledBatch[i] =
                    shuffledBatch[swapIndex];

                shuffledBatch[swapIndex] =
                    temporary;
            }

            for (int i = 0;
                 i < shuffledBatch.Count &&
                 prefabOrder.Count < targetCount;
                 i++)
            {
                prefabOrder.Add(
                    shuffledBatch[i]
                );
            }
        }

        return prefabOrder;
    }

    private void CreateGround()
    {
        GameObject ground =
            GameObject.CreatePrimitive(
                PrimitiveType.Plane
            );

        ground.name = "Ground";

        ground.transform.SetParent(
            transform,
            false
        );

        float groundRadius =
            GetOuterTreeMaximumRadius() +
            groundMargin;

        float groundDiameter =
            groundRadius * 2f;

        float groundScale =
            groundDiameter /
            PlanePrimitiveSize;

        ground.transform.localScale =
            new Vector3(
                groundScale,
                1f,
                groundScale
            );

        ApplyGroundMaterial(
            ground,
            groundDiameter
        );

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            Undo.RegisterCreatedObjectUndo(
                ground,
                "바닥 생성"
            );
        }
#endif
    }

    private void ApplyGroundMaterial(
        GameObject ground,
        float groundDiameter)
    {
        if (groundMaterial == null)
        {
            return;
        }

        Renderer groundRenderer =
            ground.GetComponent<Renderer>();

        if (groundRenderer == null)
        {
            return;
        }

        groundRenderer.sharedMaterial =
            groundMaterial;

        float tileCount =
            groundDiameter /
            Mathf.Max(
                0.1f,
                groundTextureWorldSize
            );

        MaterialPropertyBlock propertyBlock =
            new MaterialPropertyBlock();

        groundRenderer.GetPropertyBlock(
            propertyBlock
        );

        propertyBlock.SetVector(
            "_BaseMap_ST",
            new Vector4(
                tileCount,
                tileCount,
                0f,
                0f
            )
        );

        groundRenderer.SetPropertyBlock(
            propertyBlock
        );
    }

    private void SpawnHouses()
    {
        List<GameObject> housePrefabOrder =
            CreateHousePrefabOrder(
                playerCount
            );

        GameObject housesParent =
            CreateGeneratedParent(
                "Houses",
                transform
            );

        float houseRadius =
            GetHouseRadius();

        for (int i = 0;
             i < playerCount;
             i++)
        {
            GameObject selectedHousePrefab =
                housePrefabOrder[i];

            float angle =
                i *
                Mathf.PI *
                2f /
                playerCount;

            Vector3 parentPosition =
                new Vector3(
                    Mathf.Cos(angle) *
                    houseRadius,
                    0f,
                    Mathf.Sin(angle) *
                    houseRadius
                );

            GameObject houseParent =
                CreateGeneratedParent(
                    $"House_Parent_{i}",
                    housesParent.transform
                );

            houseParent.transform.localPosition =
                parentPosition;

            houseParent.transform.localRotation =
                Quaternion.LookRotation(
                    -parentPosition.normalized,
                    Vector3.up
                );

            GameObject houseInstance =
                InstantiateGeneratedPrefab(
                    selectedHousePrefab,
                    houseParent.transform,
                    $"집 {i} 생성"
                );

            if (houseInstance == null)
            {
                Debug.LogError(
                    $"집 {i} 생성에 실패했습니다.",
                    this
                );

                houseFenceAnchorA.Add(null);
                houseFenceAnchorB.Add(null);

                continue;
            }

            houseInstance.name =
                $"House_{i}_{selectedHousePrefab.name}";

            houseInstance.transform.localPosition =
                houseOffset;

            houseInstance.transform.localRotation =
                Quaternion.identity;

            RegisterHouse(
                houseInstance
            );

            RegisterHouseFenceAnchors(
                houseInstance
            );
        }
    }

    private void RegisterHouse(
        GameObject houseInstance)
    {
        House house =
            houseInstance.GetComponent<House>();

        if (house == null)
        {
            Debug.LogError(
                $"{houseInstance.name} 루트에 " +
                "House 컴포넌트가 없습니다.",
                houseInstance
            );

            return;
        }

        generatedHouses.Add(
            house
        );
    }

    private void RegisterHouseFenceAnchors(
        GameObject houseInstance)
    {
        Transform anchorA = null;
        Transform anchorB = null;

        Transform[] children =
            houseInstance.GetComponentsInChildren
                <Transform>(true);

        for (int i = 0;
             i < children.Length;
             i++)
        {
            if (anchorA == null &&
                children[i].name == fenceAnchorAName)
            {
                anchorA = children[i];
            }

            if (anchorB == null &&
                children[i].name == fenceAnchorBName)
            {
                anchorB = children[i];
            }

            if (anchorA != null &&
                anchorB != null)
            {
                break;
            }
        }

        houseFenceAnchorA.Add(
            anchorA
        );

        houseFenceAnchorB.Add(
            anchorB
        );

        if (anchorA == null ||
            anchorB == null)
        {
            Debug.LogError(
                $"{houseInstance.name}에서 " +
                $"{fenceAnchorAName} 또는 " +
                $"{fenceAnchorBName}을 찾지 못했습니다.",
                houseInstance
            );
        }
    }

    private bool TryGetClosestFenceAnchors(
        int currentIndex,
        int nextIndex,
        out Vector3 start,
        out Vector3 end)
    {
        start = Vector3.zero;
        end = Vector3.zero;

        if (currentIndex < 0 ||
            nextIndex < 0 ||
            currentIndex >=
                houseFenceAnchorA.Count ||
            nextIndex >=
                houseFenceAnchorA.Count)
        {
            return false;
        }

        Transform currentA =
            houseFenceAnchorA[currentIndex];

        Transform currentB =
            houseFenceAnchorB[currentIndex];

        Transform nextA =
            houseFenceAnchorA[nextIndex];

        Transform nextB =
            houseFenceAnchorB[nextIndex];

        if (currentA == null ||
            currentB == null ||
            nextA == null ||
            nextB == null)
        {
            return false;
        }

        start = currentA.position;
        end = nextA.position;

        float closestDistance =
            Vector3.SqrMagnitude(
                start - end
            );

        UpdateClosestFenceAnchorPair(
            currentA.position,
            nextB.position,
            ref start,
            ref end,
            ref closestDistance
        );

        UpdateClosestFenceAnchorPair(
            currentB.position,
            nextA.position,
            ref start,
            ref end,
            ref closestDistance
        );

        UpdateClosestFenceAnchorPair(
            currentB.position,
            nextB.position,
            ref start,
            ref end,
            ref closestDistance
        );

        return true;
    }

    private void UpdateClosestFenceAnchorPair(
        Vector3 candidateStart,
        Vector3 candidateEnd,
        ref Vector3 currentStart,
        ref Vector3 currentEnd,
        ref float closestDistance)
    {
        float candidateDistance =
            Vector3.SqrMagnitude(
                candidateStart -
                candidateEnd
            );

        if (candidateDistance >=
            closestDistance)
        {
            return;
        }

        currentStart =
            candidateStart;

        currentEnd =
            candidateEnd;

        closestDistance =
            candidateDistance;
    }

    private void SpawnSightBlockingFences()
    {
        if (stoneFencePrefab == null)
        {
            Debug.LogWarning(
                "집 사이 바리케이드 프리팹이 " +
                "연결되지 않아 생성을 건너뜁니다.",
                this
            );

            return;
        }

        if (houseFenceAnchorA.Count !=
                playerCount ||
            houseFenceAnchorB.Count !=
                playerCount)
        {
            Debug.LogError(
                "집 바리케이드 연결점 정보가 " +
                "정상적으로 생성되지 않았습니다.",
                this
            );

            return;
        }

        float fenceStep =
            stoneFenceLength -
            stoneFenceOverlap;

        if (stoneFenceLength <= 0f ||
            fenceStep <= 0f)
        {
            Debug.LogError(
                "집 사이 바리케이드의 길이와 " +
                "겹침 값을 확인하세요.",
                this
            );

            return;
        }

        GameObject fencesParent =
            CreateGeneratedParent(
                "SightBlockingFences",
                transform
            );

        for (int i = 0;
             i < playerCount;
             i++)
        {
            int nextIndex =
                (i + 1) %
                playerCount;

            if (!TryGetClosestFenceAnchors(
                    i,
                    nextIndex,
                    out Vector3 start,
                    out Vector3 end))
            {
                Debug.LogError(
                    $"House {i}와 House {nextIndex} 사이의 " +
                    "바리케이드 연결점을 찾지 못했습니다.",
                    this
                );

                continue;
            }

            SpawnFenceLine(
                stoneFencePrefab,
                fencesParent.transform,
                start,
                end,
                stoneFenceLength,
                stoneFenceOverlap,
                sightFenceClearance,
                $"SightFence_HouseGap_{i}"
            );
        }
    }

    private void SpawnFenceLine(
    GameObject fencePrefab,
    Transform parent,
    Vector3 start,
    Vector3 end,
    float fenceLength,
    float overlap,
    float endClearance,
    string objectNamePrefix)
    {
        Vector3 direction =
            end - start;

        float distance =
            direction.magnitude;

        if (distance <= 0.01f)
        {
            return;
        }

        direction /=
            distance;

        // 앵커에서 의도적으로 떨어뜨리고 싶은 경우에만 사용한다.
        start +=
            direction *
            endClearance;

        end -=
            direction *
            endClearance;

        distance =
            Vector3.Distance(
                start,
                end
            );

        if (distance <= 0.01f)
        {
            return;
        }

        float safeFenceLength =
            Mathf.Max(
                0.01f,
                fenceLength
            );

        float safeOverlap =
            Mathf.Clamp(
                overlap,
                0f,
                safeFenceLength - 0.01f
            );

        float preferredStep =
            safeFenceLength -
            safeOverlap;

        int segmentCount;
        float actualStep;

        if (distance <=
            safeFenceLength)
        {
            // 앵커 사이가 바리케이드 하나보다 짧으면
            // 가운데에 하나를 배치해 양쪽 벽 안으로 걸치게 한다.
            segmentCount = 1;
            actualStep = 0f;
        }
        else
        {
            // 설정된 Overlap을 최소 기준으로 사용해
            // 빈틈 없이 덮을 수 있는 개수를 계산한다.
            segmentCount =
                Mathf.CeilToInt(
                    (
                        distance -
                        safeFenceLength
                    ) /
                    preferredStep
                ) + 1;

            // 첫 바리케이드의 왼쪽 끝은 start,
            // 마지막 바리케이드의 오른쪽 끝은 end에 맞춘다.
            actualStep =
                (
                    distance -
                    safeFenceLength
                ) /
                (
                    segmentCount -
                    1
                );
        }

        Quaternion fenceRotation =
            Quaternion.LookRotation(
                Vector3.Cross(
                    direction,
                    Vector3.up
                ),
                Vector3.up
            );

        for (int i = 0;
             i < segmentCount;
             i++)
        {
            Vector3 spawnPosition;

            if (segmentCount == 1)
            {
                spawnPosition =
                    (
                        start +
                        end
                    ) *
                    0.5f;
            }
            else
            {
                spawnPosition =
                    start +
                    direction *
                    (
                        safeFenceLength *
                        0.5f +
                        actualStep *
                        i
                    );
            }

            GameObject fenceInstance =
                InstantiateGeneratedPrefab(
                    fencePrefab,
                    parent,
                    $"{objectNamePrefix}_{i} 생성"
                );

            if (fenceInstance == null)
            {
                continue;
            }

            fenceInstance.name =
                $"{objectNamePrefix}_Segment_{i}";

            fenceInstance.transform
                .SetPositionAndRotation(
                    spawnPosition,
                    fenceRotation
                );
        }
    }

    private void SpawnPlazaObjects()
    {
        bool hasCenterPrefab =
            centerPlazaPrefab != null;

        bool hasMajorProps =
            HasValidPlazaProp(
                majorPlazaProps
            );

        bool hasMinorProps =
            HasValidPlazaProp(
                minorPlazaProps
            );

        if (!hasCenterPrefab &&
            !hasMajorProps &&
            !hasMinorProps)
        {
            return;
        }

        GameObject plazaParent =
            CreateGeneratedParent(
                "PlazaObjects",
                transform
            );

        List<PlazaOccupiedCircle>
            occupiedCircles =
                new List<PlazaOccupiedCircle>();

        SpawnCenterPlaza(
            plazaParent.transform,
            occupiedCircles
        );

        if (hasMajorProps)
        {
            int majorSeed =
                unchecked(
                    currentLayoutSeed *
                    397 +
                    MajorPlazaPropSeedOffset
                );

            SpawnMajorPlazaProps(
                plazaParent.transform,
                new System.Random(majorSeed),
                occupiedCircles
            );
        }

        if (hasMinorProps)
        {
            int minorSeed =
                unchecked(
                    currentLayoutSeed *
                    397 +
                    MinorPlazaPropSeedOffset
                );

            SpawnMinorPlazaProps(
                plazaParent.transform,
                new System.Random(minorSeed),
                occupiedCircles
            );
        }
    }

    private void SpawnCenterPlaza(
        Transform parent,
        List<PlazaOccupiedCircle>
            occupiedCircles)
    {
        Vector3 reservedPosition =
            centerPlazaOffset;

        reservedPosition.y = 0f;

        float reservedRadius =
            Mathf.Max(
                0f,
                centerPlazaClearRadius
            );

        if (reservedRadius > 0f)
        {
            occupiedCircles.Add(
                new PlazaOccupiedCircle(
                    reservedPosition,
                    reservedRadius
                )
            );
        }

        if (centerPlazaPrefab == null)
            return;

        GameObject centerInstance =
            InstantiateGeneratedPrefab(
                centerPlazaPrefab,
                parent,
                "중앙 광장 생성"
            );

        if (centerInstance == null)
            return;

        centerInstance.name =
            $"CenterPlaza_" +
            $"{centerPlazaPrefab.name}";

        centerInstance.transform.localPosition =
            centerPlazaOffset;

        centerInstance.transform.localRotation =
            Quaternion.Euler(
                centerPlazaEulerAngles
            );

        centerInstance.transform.localScale *=
            Mathf.Max(
                0.01f,
                centerPlazaScale
            );
    }

    private void SpawnMajorPlazaProps(
        Transform parent,
        System.Random random,
        List<PlazaOccupiedCircle>
            occupiedCircles)
    {
        List<PlazaPropOption> validOptions =
            GetValidPlazaPropOptions(
                majorPlazaProps
            );

        if (validOptions.Count == 0)
            return;

        int targetCount =
            GetPlayerScaledCount(
                minimumMajorPlazaPropCount,
                maximumMajorPlazaPropCount
            );

        if (targetCount <= 0)
            return;

        float innerRadius =
            GetPlazaInnerRadius();

        float outerRadius =
            GetPlazaOuterRadius();

        if (outerRadius <= innerRadius)
        {
            Debug.LogWarning(
                "광장 주요 구조물 배치 공간이 부족합니다. " +
                "중앙 여백 또는 집 여백 값을 줄이세요.",
                this
            );

            return;
        }

        float ringRadius =
            Mathf.Lerp(
                innerRadius,
                outerRadius,
                Mathf.Clamp01(
                    majorPlazaRingRadiusRatio
                )
            );

        float startAngle =
            NextRandomFloat(
                random,
                0f,
                Mathf.PI * 2f
            );

        float angleStep =
            Mathf.PI *
            2f /
            targetCount;

        int placedCount = 0;

        for (int slotIndex = 0;
             slotIndex < targetCount;
             slotIndex++)
        {
            float slotAngle =
                startAngle +
                slotIndex *
                angleStep;

            bool placed =
                TrySpawnMajorPlazaProp(
                    validOptions,
                    parent,
                    random,
                    occupiedCircles,
                    slotIndex,
                    slotAngle,
                    ringRadius,
                    innerRadius,
                    outerRadius
                );

            if (placed)
                placedCount++;
        }

        if (placedCount < targetCount)
        {
            Debug.LogWarning(
                $"광장 주요 구조물 {targetCount}개 중 " +
                $"{placedCount}개만 배치했습니다. " +
                "프리팹 반지름과 배치 여백을 확인하세요.",
                this
            );
        }
    }

    private bool TrySpawnMajorPlazaProp(
        IReadOnlyList<PlazaPropOption>
            validOptions,
        Transform parent,
        System.Random random,
        List<PlazaOccupiedCircle>
            occupiedCircles,
        int slotIndex,
        float slotAngle,
        float ringRadius,
        float innerRadius,
        float outerRadius)
    {
        int attemptCount =
            Mathf.Max(
                1,
                plazaPlacementAttemptCount
            );

        for (int attempt = 0;
             attempt < attemptCount;
             attempt++)
        {
            PlazaPropOption option =
                GetWeightedPlazaPropOption(
                    validOptions,
                    random
                );

            float uniformScale =
                GetPlazaPropScale(
                    option,
                    random
                );

            float placementRadius =
                Mathf.Max(
                    0.1f,
                    option.placementRadius *
                    uniformScale
                );

            float angleJitter =
                NextRandomFloat(
                    random,
                    -majorPlazaAngularJitter,
                    majorPlazaAngularJitter
                ) *
                Mathf.Deg2Rad;

            float radialJitter =
                NextRandomFloat(
                    random,
                    -majorPlazaRadialJitter,
                    majorPlazaRadialJitter
                );

            float minimumCandidateRadius =
                innerRadius +
                placementRadius;

            float maximumCandidateRadius =
                outerRadius -
                placementRadius;

            if (maximumCandidateRadius <=
                minimumCandidateRadius)
            {
                continue;
            }

            float candidateRadius =
                Mathf.Clamp(
                    ringRadius +
                    radialJitter,
                    minimumCandidateRadius,
                    maximumCandidateRadius
                );

            float angle =
                slotAngle +
                angleJitter;

            Vector3 localPosition =
                new Vector3(
                    Mathf.Cos(angle) *
                    candidateRadius,
                    option.verticalOffset,
                    Mathf.Sin(angle) *
                    candidateRadius
                );

            if (!IsPlazaPlacementValid(
                    localPosition,
                    placementRadius,
                    occupiedCircles))
            {
                continue;
            }

            if (!SpawnPlazaProp(
                    option,
                    parent,
                    random,
                    localPosition,
                    uniformScale,
                    $"MajorPlazaProp_{slotIndex}"))
            {
                continue;
            }

            occupiedCircles.Add(
                new PlazaOccupiedCircle(
                    localPosition,
                    placementRadius
                )
            );

            return true;
        }

        return false;
    }

    private void SpawnMinorPlazaProps(
        Transform parent,
        System.Random random,
        List<PlazaOccupiedCircle>
            occupiedCircles)
    {
        List<PlazaPropOption> validOptions =
            GetValidPlazaPropOptions(
                minorPlazaProps
            );

        if (validOptions.Count == 0)
            return;

        int targetCount =
            GetPlayerScaledCount(
                minimumMinorPlazaPropCount,
                maximumMinorPlazaPropCount
            );

        if (targetCount <= 0)
            return;

        float innerRadius =
            GetPlazaInnerRadius();

        float outerRadius =
            GetPlazaOuterRadius();

        if (outerRadius <= innerRadius)
            return;

        int placedCount = 0;

        for (int propIndex = 0;
             propIndex < targetCount;
             propIndex++)
        {
            bool placed =
                TrySpawnMinorPlazaProp(
                    validOptions,
                    parent,
                    random,
                    occupiedCircles,
                    propIndex,
                    innerRadius,
                    outerRadius
                );

            if (placed)
                placedCount++;
        }

        if (placedCount < targetCount)
        {
            Debug.LogWarning(
                $"광장 보조 구조물 {targetCount}개 중 " +
                $"{placedCount}개만 배치했습니다.",
                this
            );
        }
    }

    private bool TrySpawnMinorPlazaProp(
        IReadOnlyList<PlazaPropOption>
            validOptions,
        Transform parent,
        System.Random random,
        List<PlazaOccupiedCircle>
            occupiedCircles,
        int propIndex,
        float innerRadius,
        float outerRadius)
    {
        int attemptCount =
            Mathf.Max(
                1,
                plazaPlacementAttemptCount
            );

        for (int attempt = 0;
             attempt < attemptCount;
             attempt++)
        {
            PlazaPropOption option =
                GetWeightedPlazaPropOption(
                    validOptions,
                    random
                );

            float uniformScale =
                GetPlazaPropScale(
                    option,
                    random
                );

            float placementRadius =
                Mathf.Max(
                    0.1f,
                    option.placementRadius *
                    uniformScale
                );

            float minimumRadius =
                innerRadius +
                placementRadius;

            float maximumRadius =
                outerRadius -
                placementRadius;

            if (maximumRadius <=
                minimumRadius)
            {
                continue;
            }

            /*
             * 원의 면적 기준으로 고르게 퍼지도록 반지름의
             * 제곱값을 보간한다.
             */
            float radialSample =
                NextRandomFloat(
                    random,
                    minimumRadius *
                    minimumRadius,
                    maximumRadius *
                    maximumRadius
                );

            float sampleRadius =
                Mathf.Sqrt(
                    radialSample
                );

            float angle =
                NextRandomFloat(
                    random,
                    0f,
                    Mathf.PI * 2f
                );

            Vector3 localPosition =
                new Vector3(
                    Mathf.Cos(angle) *
                    sampleRadius,
                    option.verticalOffset,
                    Mathf.Sin(angle) *
                    sampleRadius
                );

            if (!IsPlazaPlacementValid(
                    localPosition,
                    placementRadius,
                    occupiedCircles))
            {
                continue;
            }

            if (!SpawnPlazaProp(
                    option,
                    parent,
                    random,
                    localPosition,
                    uniformScale,
                    $"MinorPlazaProp_{propIndex}"))
            {
                continue;
            }

            occupiedCircles.Add(
                new PlazaOccupiedCircle(
                    localPosition,
                    placementRadius
                )
            );

            return true;
        }

        return false;
    }

    private bool SpawnPlazaProp(
        PlazaPropOption option,
        Transform parent,
        System.Random random,
        Vector3 localPosition,
        float uniformScale,
        string objectNamePrefix)
    {
        GameObject instance =
            InstantiateGeneratedPrefab(
                option.prefab,
                parent,
                $"{objectNamePrefix} 생성"
            );

        if (instance == null)
            return false;

        instance.name =
            $"{objectNamePrefix}_" +
            $"{option.prefab.name}";

        instance.transform.localPosition =
            localPosition;

        instance.transform.localRotation =
            GetPlazaPropRotation(
                option,
                localPosition,
                random
            );

        instance.transform.localScale *=
            uniformScale;

        return true;
    }

    private Quaternion GetPlazaPropRotation(
        PlazaPropOption option,
        Vector3 localPosition,
        System.Random random)
    {
        float yRotation;

        if (option.faceVillageCenter)
        {
            Vector3 centerDirection =
                -new Vector3(
                    localPosition.x,
                    0f,
                    localPosition.z
                );

            if (centerDirection.sqrMagnitude <=
                0.0001f)
            {
                yRotation = 0f;
            }
            else
            {
                yRotation =
                    Quaternion.LookRotation(
                        centerDirection,
                        Vector3.up
                    ).eulerAngles.y;
            }
        }
        else
        {
            yRotation =
                NextRandomFloat(
                    random,
                    0f,
                    360f
                );
        }

        return Quaternion.Euler(
            0f,
            yRotation,
            0f
        );
    }

    private bool IsPlazaPlacementValid(
        Vector3 localPosition,
        float placementRadius,
        IReadOnlyList<PlazaOccupiedCircle>
            occupiedCircles)
    {
        Vector2 candidatePosition =
            new Vector2(
                localPosition.x,
                localPosition.z
            );

        float outerRadius =
            GetPlazaOuterRadius();

        if (candidatePosition.magnitude +
            placementRadius >
            outerRadius)
        {
            return false;
        }

        for (int i = 0;
             i < occupiedCircles.Count;
             i++)
        {
            PlazaOccupiedCircle occupied =
                occupiedCircles[i];

            Vector2 occupiedPosition =
                new Vector2(
                    occupied.localPosition.x,
                    occupied.localPosition.z
                );

            float requiredDistance =
                placementRadius +
                occupied.radius +
                Mathf.Max(
                    0f,
                    plazaPropSpacing
                );

            if ((candidatePosition -
                 occupiedPosition).sqrMagnitude <
                requiredDistance *
                requiredDistance)
            {
                return false;
            }
        }

        Vector3 candidateWorldPosition =
            transform.TransformPoint(
                new Vector3(
                    localPosition.x,
                    0f,
                    localPosition.z
                )
            );

        for (int i = 0;
             i < generatedHouses.Count;
             i++)
        {
            House house =
                generatedHouses[i];

            if (house == null ||
                house.FrontDoorOutsidePoint ==
                    null)
            {
                continue;
            }

            Vector3 doorPosition =
                house.FrontDoorOutsidePoint
                    .position;

            float doorRequiredDistance =
                placementRadius +
                Mathf.Max(
                    0f,
                    frontDoorClearRadius
                );

            if (GetHorizontalSqrDistance(
                    candidateWorldPosition,
                    doorPosition) <
                doorRequiredDistance *
                doorRequiredDistance)
            {
                return false;
            }

            Vector3 villageCenter =
                transform.position;

            Vector3 toCenter =
                villageCenter -
                doorPosition;

            toCenter.y = 0f;

            if (toCenter.sqrMagnitude <=
                0.0001f)
            {
                continue;
            }

            Vector3 pathEnd =
                doorPosition +
                toCenter.normalized *
                Mathf.Max(
                    0f,
                    frontDoorPathLength
                );

            float pathRequiredDistance =
                placementRadius +
                Mathf.Max(
                    0f,
                    frontDoorPathHalfWidth
                );

            if (GetHorizontalDistanceToSegment(
                    candidateWorldPosition,
                    doorPosition,
                    pathEnd) <
                pathRequiredDistance)
            {
                return false;
            }
        }

        return true;
    }

    private float GetPlazaInnerRadius()
    {
        Vector2 centerOffset =
            new Vector2(
                centerPlazaOffset.x,
                centerPlazaOffset.z
            );

        return
            centerOffset.magnitude +
            Mathf.Max(
                0f,
                centerPlazaClearRadius
            );
    }

    private float GetPlazaOuterRadius()
    {
        return Mathf.Max(
            0f,
            GetHouseRadius() -
            Mathf.Max(
                0f,
                plazaHouseClearance
            )
        );
    }

    private int GetPlayerScaledCount(
        int minimumCount,
        int maximumCount)
    {
        int safeMinimum =
            Mathf.Min(
                minimumCount,
                maximumCount
            );

        int safeMaximum =
            Mathf.Max(
                minimumCount,
                maximumCount
            );

        float playerRatio =
            Mathf.InverseLerp(
                MinPlayerCount,
                MaxPlayerCount,
                playerCount
            );

        return Mathf.RoundToInt(
            Mathf.Lerp(
                safeMinimum,
                safeMaximum,
                playerRatio
            )
        );
    }

    private bool HasValidPlazaProp(
        PlazaPropOption[] options)
    {
        if (options == null)
            return false;

        for (int i = 0;
             i < options.Length;
             i++)
        {
            if (options[i].prefab != null)
                return true;
        }

        return false;
    }

    private List<PlazaPropOption>
        GetValidPlazaPropOptions(
            PlazaPropOption[] options)
    {
        List<PlazaPropOption> result =
            new List<PlazaPropOption>();

        if (options == null)
            return result;

        for (int i = 0;
             i < options.Length;
             i++)
        {
            if (options[i].prefab != null)
            {
                result.Add(
                    options[i]
                );
            }
        }

        return result;
    }

    private PlazaPropOption
        GetWeightedPlazaPropOption(
            IReadOnlyList<PlazaPropOption>
                options,
            System.Random random)
    {
        int totalWeight = 0;

        for (int i = 0;
             i < options.Count;
             i++)
        {
            totalWeight +=
                Mathf.Max(
                    1,
                    options[i].weight
                );
        }

        int selectedWeight =
            random.Next(
                Mathf.Max(
                    1,
                    totalWeight
                )
            );

        for (int i = 0;
             i < options.Count;
             i++)
        {
            selectedWeight -=
                Mathf.Max(
                    1,
                    options[i].weight
                );

            if (selectedWeight < 0)
                return options[i];
        }

        return options[
            options.Count - 1
        ];
    }

    private float GetPlazaPropScale(
        PlazaPropOption option,
        System.Random random)
    {
        float minimumScale =
            Mathf.Min(
                option.scaleRange.x,
                option.scaleRange.y
            );

        float maximumScale =
            Mathf.Max(
                option.scaleRange.x,
                option.scaleRange.y
            );

        if (maximumScale <= 0f)
            return 1f;

        minimumScale =
            Mathf.Max(
                0.01f,
                minimumScale
            );

        maximumScale =
            Mathf.Max(
                minimumScale,
                maximumScale
            );

        return NextRandomFloat(
            random,
            minimumScale,
            maximumScale
        );
    }

    private float GetHorizontalSqrDistance(
        Vector3 first,
        Vector3 second)
    {
        float xDifference =
            first.x - second.x;

        float zDifference =
            first.z - second.z;

        return
            xDifference *
            xDifference +
            zDifference *
            zDifference;
    }

    private float
        GetHorizontalDistanceToSegment(
            Vector3 point,
            Vector3 segmentStart,
            Vector3 segmentEnd)
    {
        Vector2 point2D =
            new Vector2(
                point.x,
                point.z
            );

        Vector2 start2D =
            new Vector2(
                segmentStart.x,
                segmentStart.z
            );

        Vector2 end2D =
            new Vector2(
                segmentEnd.x,
                segmentEnd.z
            );

        Vector2 segment =
            end2D -
            start2D;

        float segmentLengthSqr =
            segment.sqrMagnitude;

        if (segmentLengthSqr <=
            0.0001f)
        {
            return Vector2.Distance(
                point2D,
                start2D
            );
        }

        float t =
            Mathf.Clamp01(
                Vector2.Dot(
                    point2D - start2D,
                    segment
                ) /
                segmentLengthSqr
            );

        Vector2 closestPoint =
            start2D +
            segment *
            t;

        return Vector2.Distance(
            point2D,
            closestPoint
        );
    }

    private void SpawnOuterFences()
    {
        if (outerFencePrefabC == null &&
            outerFencePrefabD == null)
        {
            Debug.LogWarning(
                "외곽 바리케이드 프리팹이 " +
                "연결되지 않아 생성을 건너뜁니다.",
                this
            );

            return;
        }

        float fenceStep =
            outerFenceLength -
            outerFenceOverlap;

        if (outerFenceLength <= 0f ||
            fenceStep <= 0f)
        {
            Debug.LogError(
                "외곽 바리케이드의 길이와 " +
                "겹침 값을 확인하세요.",
                this
            );

            return;
        }

        GameObject fencesParent =
            CreateGeneratedParent(
                "OuterFences",
                transform
            );

        float outerRadius =
            GetOuterBoundaryRadius();

        float circumference =
            2f *
            Mathf.PI *
            outerRadius;

        int fenceCount =
            Mathf.Max(
                3,
                Mathf.CeilToInt(
                    circumference /
                    fenceStep
                )
            );

        float angleStep =
            Mathf.PI *
            2f /
            fenceCount;

        for (int i = 0;
             i < fenceCount;
             i++)
        {
            float angle =
                (
                    i + 0.5f
                ) *
                angleStep;

            Vector3 localPosition =
                new Vector3(
                    Mathf.Cos(angle) *
                    outerRadius,
                    0f,
                    Mathf.Sin(angle) *
                    outerRadius
                );

            Vector3 tangentDirection =
                new Vector3(
                    -Mathf.Sin(angle),
                    0f,
                    Mathf.Cos(angle)
                );

            Quaternion localRotation =
                Quaternion.LookRotation(
                    Vector3.Cross(
                        tangentDirection,
                        Vector3.up
                    ),
                    Vector3.up
                );

            GameObject selectedPrefab =
                GetOuterFencePrefab(i);

            GameObject fenceInstance =
                InstantiateGeneratedPrefab(
                    selectedPrefab,
                    fencesParent.transform,
                    $"외곽 바리케이드 {i} 생성"
                );

            if (fenceInstance == null)
            {
                continue;
            }

            fenceInstance.name =
                $"OuterFence_{i}";

            fenceInstance.transform.localPosition =
                localPosition;

            fenceInstance.transform.localRotation =
                localRotation;
        }
    }

    private GameObject GetOuterFencePrefab(
        int index)
    {
        GameObject preferredPrefab =
            index % 2 == 0
                ? outerFencePrefabC
                : outerFencePrefabD;

        if (preferredPrefab != null)
        {
            return preferredPrefab;
        }

        return
            outerFencePrefabC != null
                ? outerFencePrefabC
                : outerFencePrefabD;
    }

    private void SpawnOuterTrees()
    {
        if (outerTreeRows <= 0 ||
            outerTreePrefabs == null ||
            outerTreePrefabs.Length == 0)
        {
            return;
        }

        List<GameObject> validTreePrefabs =
            new List<GameObject>();

        for (int i = 0;
             i < outerTreePrefabs.Length;
             i++)
        {
            if (outerTreePrefabs[i] != null)
            {
                validTreePrefabs.Add(
                    outerTreePrefabs[i]
                );
            }
        }

        if (validTreePrefabs.Count == 0)
        {
            Debug.LogWarning(
                "외곽 나무 프리팹 목록에 " +
                "사용할 수 있는 프리팹이 없습니다.",
                this
            );

            return;
        }

        GameObject treesParent =
            CreateGeneratedParent(
                "OuterTrees",
                transform
            );

        int treeSeed =
            unchecked(
                currentLayoutSeed *
                397 +
                OuterTreeSeedOffset
            );

        System.Random random =
            new System.Random(
                treeSeed
            );

        float minimumScale =
            Mathf.Min(
                outerTreeScaleRange.x,
                outerTreeScaleRange.y
            );

        float maximumScale =
            Mathf.Max(
                outerTreeScaleRange.x,
                outerTreeScaleRange.y
            );

        float outerRadius =
            GetOuterBoundaryRadius();

        for (int row = 0;
             row < outerTreeRows;
             row++)
        {
            float rowRadius =
                outerRadius +
                outerTreeInnerOffset +
                row *
                outerTreeRowSpacing;

            float circumference =
                2f *
                Mathf.PI *
                rowRadius;

            int treeCount =
                Mathf.Max(
                    3,
                    Mathf.CeilToInt(
                        circumference /
                        Mathf.Max(
                            0.1f,
                            outerTreeSpacing
                        )
                    )
                );

            float angleStep =
                Mathf.PI *
                2f /
                treeCount;

            float rowAngleOffset =
                row % 2 == 0
                    ? 0f
                    : angleStep *
                      0.5f;

            for (int i = 0;
                 i < treeCount;
                 i++)
            {
                SpawnOuterTree(
                    validTreePrefabs,
                    treesParent.transform,
                    random,
                    row,
                    i,
                    rowRadius,
                    angleStep,
                    rowAngleOffset,
                    minimumScale,
                    maximumScale
                );
            }
        }
    }

    private void SpawnOuterTree(
        IReadOnlyList<GameObject> validTreePrefabs,
        Transform parent,
        System.Random random,
        int row,
        int index,
        float rowRadius,
        float angleStep,
        float rowAngleOffset,
        float minimumScale,
        float maximumScale)
    {
        float radialJitter =
            NextRandomFloat(
                random,
                -outerTreePositionJitter,
                outerTreePositionJitter
            );

        float angularJitter =
            NextRandomFloat(
                random,
                -outerTreePositionJitter,
                outerTreePositionJitter
            ) /
            Mathf.Max(
                0.1f,
                rowRadius
            );

        float angle =
            index *
            angleStep +
            rowAngleOffset +
            angularJitter;

        float sampleRadius =
            rowRadius +
            radialJitter;

        Vector3 localPosition =
            new Vector3(
                Mathf.Cos(angle) *
                sampleRadius,
                0f,
                Mathf.Sin(angle) *
                sampleRadius
            );

        GameObject selectedTreePrefab =
            validTreePrefabs[
                random.Next(
                    validTreePrefabs.Count
                )
            ];

        GameObject treeInstance =
            InstantiateGeneratedPrefab(
                selectedTreePrefab,
                parent,
                $"외곽 나무 {row}_{index} 생성"
            );

        if (treeInstance == null)
        {
            return;
        }

        float yRotation =
            NextRandomFloat(
                random,
                0f,
                360f
            );

        float uniformScale =
            NextRandomFloat(
                random,
                minimumScale,
                maximumScale
            );

        treeInstance.name =
            $"OuterTree_Row_{row}" +
            $"_Index_{index}" +
            $"_{selectedTreePrefab.name}";

        treeInstance.transform.localPosition =
            localPosition;

        treeInstance.transform.localRotation =
            Quaternion.Euler(
                0f,
                yRotation,
                0f
            );

        treeInstance.transform.localScale *=
            uniformScale;
    }

    private float NextRandomFloat(
        System.Random random,
        float minimum,
        float maximum)
    {
        return
            minimum +
            (float)random.NextDouble() *
            (
                maximum -
                minimum
            );
    }

    private GameObject CreateGeneratedParent(
        string objectName,
        Transform parent)
    {
        GameObject generatedParent =
            new GameObject(
                objectName
            );

        generatedParent.transform.SetParent(
            parent,
            false
        );

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            Undo.RegisterCreatedObjectUndo(
                generatedParent,
                $"{objectName} 생성"
            );
        }
#endif

        return generatedParent;
    }

    private GameObject InstantiateGeneratedPrefab(
        GameObject prefab,
        Transform parent,
        string undoName)
    {
        if (prefab == null)
        {
            return null;
        }

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            GameObject instance =
                PrefabUtility.InstantiatePrefab(
                    prefab,
                    parent
                ) as GameObject;

            if (instance != null)
            {
                Undo.RegisterCreatedObjectUndo(
                    instance,
                    undoName
                );
            }

            return instance;
        }
#endif

        return Instantiate(
            prefab,
            parent
        );
    }

    private void DestroyGeneratedObject(
        GameObject target)
    {
        if (target == null)
        {
            return;
        }

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            Undo.DestroyObjectImmediate(
                target
            );

            return;
        }
#endif

        Destroy(
            target
        );
    }
}
