using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class WeaponSwingTestSpawner : MonoBehaviour
{
    [SerializeField] private NetworkManager networkManager;
    [SerializeField] private GameObject playerSpiritPrefab;

    [Header("Spawn")]
    [SerializeField] private Transform spawnCenter;
    [SerializeField] private float spawnSpacing = 3f;

    private readonly Dictionary<ulong, NetworkObject> spawnedSpirits =
        new Dictionary<ulong, NetworkObject>();

    private void Awake()
    {
        networkManager.OnClientConnectedCallback +=
            OnClientConnected;
    }

    private void OnDestroy()
    {
        if (networkManager != null)
        {
            networkManager.OnClientConnectedCallback -=
                OnClientConnected;
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!networkManager.IsServer)
        {
            return;
        }

        if (spawnedSpirits.ContainsKey(clientId))
        {
            return;
        }

        int spawnIndex = spawnedSpirits.Count;

        Vector3 center = spawnCenter != null
            ? spawnCenter.position
            : Vector3.zero;

        Vector3 spawnPosition =
            center + Vector3.right * (spawnIndex * spawnSpacing);

        GameObject spiritObject = Instantiate(
            playerSpiritPrefab,
            spawnPosition,
            Quaternion.identity
        );

        NetworkObject networkObject =
            spiritObject.GetComponent<NetworkObject>();

        if (networkObject == null)
        {
            Debug.LogError(
                "PlayerSpirit 프리팹에 NetworkObject가 없습니다."
            );

            Destroy(spiritObject);
            return;
        }

        networkObject.SpawnWithOwnership(clientId, true);

        spawnedSpirits.Add(clientId, networkObject);

        Debug.Log(
            $"Spirit spawned. ClientId: {clientId}, " +
            $"Position: {spawnPosition}"
        );
    }
}