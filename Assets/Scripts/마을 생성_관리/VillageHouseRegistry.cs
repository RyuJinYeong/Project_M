using System.Collections.Generic;
using UnityEngine;

public class VillageHouseRegistry : MonoBehaviour
{
    private readonly List<House> houses =
        new List<House>();

    public IReadOnlyList<House> Houses =>
        houses;

    public int HouseCount =>
        houses.Count;

    public void Initialize(
        IReadOnlyList<House> generatedHouses)
    {
        houses.Clear();

        for (int i = 0;
             i < generatedHouses.Count;
             i++)
        {
            House house =
                generatedHouses[i];

            if (house == null)
            {
                Debug.LogError(
                    $"House {i}가 null입니다."
                );

                continue;
            }

            house.Initialize(i);
            houses.Add(house);
        }

        Debug.Log(
            $"집 {houses.Count}채 등록 완료"
        );
    }

    public House GetHouse(
        int houseId)
    {
        if (houseId < 0 ||
            houseId >= houses.Count)
        {
            return null;
        }

        return houses[houseId];
    }

    public House GetHouseContainingPosition(
        Vector3 worldPosition)
    {
        for (int i = 0;
             i < houses.Count;
             i++)
        {
            House house =
                houses[i];

            if (house != null &&
                house.ContainsPosition(
                    worldPosition))
            {
                return house;
            }
        }

        return null;
    }

    public void Clear()
    {
        houses.Clear();
    }
}