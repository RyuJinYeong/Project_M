using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using Unity.Multiplayer.PlayMode;


#if UNITY_EDITOR

#endif

public class DirectConnectionTestLauncher : MonoBehaviour
{
    [Header("연결 참조")]
    [SerializeField] private NetworkManager networkManager;
    [SerializeField] private UnityTransport unityTransport;
    [SerializeField] private LobbyRoomManager lobbyRoomManagerPrefab;

    [Header("직접 연결 설정")]
    [SerializeField] private string directAddress = "127.0.0.1";
    [SerializeField] private ushort directPort = 7777;
    [SerializeField] private float clientConnectDelay = 1f;

    [SerializeField] private float connectionTimeout = 15f;

    [Min(10f)]
    [SerializeField] private float deferredMessageSpawnTimeout = 30f;

    private IEnumerator Start()
    {
#if UNITY_EDITOR
        IReadOnlyList<string> tags = CurrentPlayer.Tags;

        if (HasPlayerTag(tags, "Host"))
        {
            yield return StartHostRoutine();
            yield break;
        }

        if (HasPlayerTag(tags, "Client"))
        {
            yield return StartClientRoutine();
            yield break;
        }

        Debug.LogWarning("MPPM 플레이어에 Host 또는 Client 태그가 없습니다.");
#else
        Debug.LogWarning("DirectConnectionTestLauncher는 MPPM 테스트 전용입니다.");
#endif

        yield break;
    }

    private bool HasPlayerTag(IReadOnlyList<string> tags, string targetTag)
    {
        if (tags == null || string.IsNullOrEmpty(targetTag))
            return false;

        for (int i = 0; i < tags.Count; i++)
        {
            if (string.Equals(tags[i], targetTag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private IEnumerator StartHostRoutine()
    {
        if (!ValidateReferences())
            yield break;

        networkManager.NetworkConfig.ConnectionApproval = false;
        networkManager.NetworkConfig.SpawnTimeout =
            Mathf.Max(10f, deferredMessageSpawnTimeout);

        unityTransport.SetConnectionData(
            directAddress,
            directPort,
            "0.0.0.0"
        );

        Debug.Log($"직접 연결 Host 시작 - Address: {directAddress}, Port: {directPort}");

        if (!networkManager.StartHost())
        {
            Debug.LogError("직접 연결 Host 시작에 실패했습니다.");
            yield break;
        }

        SpawnLobbyRoomManager();

        yield return WaitForLobbyRoomManager();

        if (LobbyRoomManager.Instance == null || !LobbyRoomManager.Instance.IsSpawned)
        {
            Debug.LogError("LobbyRoomManager 스폰에 실패했습니다.");
            yield break;
        }

        LobbyRoomManager.Instance.RegisterHostPlayer(CreatePlayerName("Host"));

        yield return WaitForLocalPlayerRegistration();

        if (!LobbyRoomManager.Instance.ContainsPlayer(networkManager.LocalClientId))
        {
            Debug.LogError("Host 플레이어 등록에 실패했습니다.");
            yield break;
        }

        Debug.Log(
            "직접 연결 Host 준비 완료 - " +
            "로비 설정 후 시작 버튼을 눌러주세요."
        );
    }

    private IEnumerator StartClientRoutine()
    {
        if (!ValidateReferences())
            yield break;

        yield return new WaitForSeconds(clientConnectDelay);

        networkManager.NetworkConfig.ConnectionApproval = false;
        networkManager.NetworkConfig.SpawnTimeout =
            Mathf.Max(10f, deferredMessageSpawnTimeout);

        unityTransport.SetConnectionData(
            directAddress,
            directPort
        );

        Debug.Log($"직접 연결 Client 시작 - Address: {directAddress}, Port: {directPort}");

        if (!networkManager.StartClient())
        {
            Debug.LogError("직접 연결 Client 시작에 실패했습니다.");
            yield break;
        }

        yield return WaitForLocalConnection();

        if (!networkManager.IsConnectedClient)
        {
            Debug.LogError("직접 연결 Host 접속 시간이 초과됐습니다.");
            yield break;
        }

        yield return WaitForLobbyRoomManager();

        if (LobbyRoomManager.Instance == null || !LobbyRoomManager.Instance.IsSpawned)
        {
            Debug.LogError("LobbyRoomManager 동기화에 실패했습니다.");
            yield break;
        }

        LobbyRoomManager.Instance.RegisterPlayerServerRpc(
            new FixedString64Bytes(CreatePlayerName("Client"))
        );

        yield return WaitForLocalPlayerRegistration();

        if (!LobbyRoomManager.Instance.ContainsPlayer(networkManager.LocalClientId))
        {
            Debug.LogError("Client 플레이어 등록에 실패했습니다.");
            yield break;
        }

        Debug.Log($"직접 연결 Client 준비 완료 - ClientId: {networkManager.LocalClientId}");
    }

    private void SpawnLobbyRoomManager()
    {
        if (!networkManager.IsServer)
            return;

        if (LobbyRoomManager.Instance != null && LobbyRoomManager.Instance.IsSpawned)
            return;

        LobbyRoomManager lobbyRoomManager = Instantiate(lobbyRoomManagerPrefab);

        NetworkObject networkObject = lobbyRoomManager.GetComponent<NetworkObject>();

        if (networkObject == null)
        {
            Debug.LogError("LobbyRoomManager 프리팹에 NetworkObject가 없습니다.");

            Destroy(lobbyRoomManager.gameObject);
            return;
        }

        networkObject.Spawn(false);
    }

    private IEnumerator WaitForLocalConnection()
    {
        float timeoutTime = Time.realtimeSinceStartup + connectionTimeout;

        while (Time.realtimeSinceStartup < timeoutTime)
        {
            if (networkManager.IsConnectedClient)
                yield break;

            if (!networkManager.IsListening)
                yield break;

            yield return null;
        }
    }

    private IEnumerator WaitForLobbyRoomManager()
    {
        float timeoutTime = Time.realtimeSinceStartup + connectionTimeout;

        while (Time.realtimeSinceStartup < timeoutTime)
        {
            if (LobbyRoomManager.Instance != null && LobbyRoomManager.Instance.IsSpawned)
                yield break;

            if (!networkManager.IsListening)
                yield break;

            yield return null;
        }
    }

    private IEnumerator WaitForLocalPlayerRegistration()
    {
        float timeoutTime = Time.realtimeSinceStartup + connectionTimeout;

        while (Time.realtimeSinceStartup < timeoutTime)
        {
            if (LobbyRoomManager.Instance != null &&
                LobbyRoomManager.Instance.IsSpawned &&
                LobbyRoomManager.Instance.ContainsPlayer(networkManager.LocalClientId))
            {
                yield break;
            }

            if (!networkManager.IsListening)
                yield break;

            yield return null;
        }
    }

    private bool ValidateReferences()
    {
        if (networkManager == null)
        {
            Debug.LogError("NetworkManager가 연결되지 않았습니다.");
            return false;
        }

        if (unityTransport == null)
        {
            Debug.LogError("UnityTransport가 연결되지 않았습니다.");
            return false;
        }

        if (lobbyRoomManagerPrefab == null)
        {
            Debug.LogError("LobbyRoomManager Prefab이 연결되지 않았습니다.");
            return false;
        }

        return true;
    }

    private string CreatePlayerName(string prefix)
    {
        int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
        string shortPrefix =
            string.Equals(prefix, "Host", System.StringComparison.Ordinal)
                ? "H"
                : "C";

        return shortPrefix +
               (processId % 10000000).ToString("D7");
    }
}
