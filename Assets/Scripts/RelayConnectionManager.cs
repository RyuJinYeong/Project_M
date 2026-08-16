using System;
using System.Collections;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class RelayConnectionManager : MonoBehaviour
{
    private const int MaximumSupportedPlayers = 12;

    public const string TargetFrameRatePreferenceKey =
        "Personal.TargetFrameRate";

    public static RelayConnectionManager Instance
    {
        get;
        private set;
    }

    [Header("연결 참조")]
    [SerializeField]
    private NetworkManager networkManager;

    [SerializeField]
    private UnityTransport unityTransport;

    [SerializeField]
    private LobbyRoomManager lobbyRoomManagerPrefab;

    [Header("씬 복구")]
    [SerializeField] private string lobbySceneName = "LobbyScene";

    [Header("Relay 설정")]
    [Range(2, MaximumSupportedPlayers)]
    [SerializeField] private int maxPlayers =
        MaximumSupportedPlayers;

    [SerializeField] private string connectionType = "dtls";

    [Min(1f)]
    [SerializeField] private float connectionTimeout = 15f;

    [Min(10f)]
    [SerializeField] private float deferredMessageSpawnTimeout = 30f;

    [Header("Rendering Performance")]
    [Range(30, 240)]
    [SerializeField] private int targetFrameRate = 60;

    [Header("Connection Quality")]
    [Min(0.5f)]
    [SerializeField] private float connectionQualitySampleInterval = 2f;

    [Min(1)]
    [SerializeField] private int delayedConnectionThreshold = 180;

    [Min(1)]
    [SerializeField] private int unstableConnectionThreshold = 350;

    [Min(0)]
    [SerializeField] private int connectionQualityRecoveryHysteresis = 30;

    public string CurrentRelayRegion { get; private set; } = string.Empty;

    public event Action<string, bool> StatusChanged;
    public event Action UnexpectedlyDisconnected;
    public event Action<bool> BusyChanged;

    public string CurrentJoinCode
    {
        get;
        private set;
    } = string.Empty;

    public bool IsBusy
    {
        get;
        private set;
    }

    public int MaxPlayers =>
        maxPlayers;

    public int TargetFrameRate => targetFrameRate;

    public int SmoothedRttMilliseconds { get; private set; }

    public bool HasConnectionQualitySample { get; private set; }

    public int ConnectionQualityLevel => connectionQualityLevel;

    public bool TryConsumePendingDisconnectMessage(
        out string message)
    {
        message = pendingDisconnectMessage;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        pendingDisconnectMessage =
            string.Empty;

        return true;
    }

    private bool suppressDisconnectNotification;
    private bool unexpectedDisconnectRecoveryStarted;
    private string pendingDisconnectMessage = string.Empty;
    private NetworkManager callbackNetworkManager;
    private Coroutine leaveSessionCoroutine;
    private bool returnToLobbyAfterLeaveRequested;
    private readonly int[] recentRttSamples = new int[3];
    private int recentRttSampleCount;
    private int recentRttSampleIndex;
    private int connectionQualityLevel;
    private int pendingConnectionQualityLevel = -1;
    private int pendingConnectionQualitySampleCount;
    private float nextConnectionQualitySampleTime;

    private void Awake()
    {
        if (Instance != null &&
            Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        maxPlayers = MaximumSupportedPlayers;

        SetTargetFrameRate(
            PlayerPrefs.GetInt(
                TargetFrameRatePreferenceKey,
                targetFrameRate
            )
        );

        transform.SetParent(null);
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded +=
            OnUnitySceneLoaded;

        if (!TryRefreshNetworkReferences())
        {
            Debug.LogError(
                "RelayConnectionManager에 " +
                "NetworkManager가 연결되지 않았습니다.",
                this
            );

            enabled = false;
            return;
        }

    }

    public void SetTargetFrameRate(int frameRate)
    {
        targetFrameRate = frameRate < 0
            ? -1
            : Mathf.Clamp(frameRate, 30, 240);

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = targetFrameRate;

        PlayerPrefs.SetInt(
            TargetFrameRatePreferenceKey,
            targetFrameRate
        );
        PlayerPrefs.Save();
    }

    private void Update()
    {
        if (networkManager == null ||
            !networkManager.IsListening ||
            !networkManager.IsClient ||
            networkManager.IsServer)
        {
            ResetConnectionQuality();
            return;
        }

        if (Time.unscaledTime < nextConnectionQualitySampleTime)
            return;

        nextConnectionQualitySampleTime =
            Time.unscaledTime +
            Mathf.Max(0.5f, connectionQualitySampleInterval);

        SampleConnectionQuality();
    }

    private void SampleConnectionQuality()
    {
        if (networkManager == null ||
            networkManager.NetworkConfig == null ||
            networkManager.NetworkConfig.NetworkTransport == null)
        {
            return;
        }

        ulong currentRtt =
            networkManager.NetworkConfig.NetworkTransport
                .GetCurrentRtt(NetworkManager.ServerClientId);

        if (currentRtt == 0)
            return;

        recentRttSamples[recentRttSampleIndex] =
            currentRtt > int.MaxValue
                ? int.MaxValue
                : (int)currentRtt;

        recentRttSampleIndex =
            (recentRttSampleIndex + 1) %
            recentRttSamples.Length;

        recentRttSampleCount =
            Mathf.Min(
                recentRttSampleCount + 1,
                recentRttSamples.Length
            );

        long totalRtt = 0;

        for (int i = 0; i < recentRttSampleCount; i++)
            totalRtt += recentRttSamples[i];

        SmoothedRttMilliseconds =
            Mathf.RoundToInt(
                totalRtt /
                (float)recentRttSampleCount
            );

        HasConnectionQualitySample = true;

        int desiredLevel = GetDesiredConnectionQualityLevel();

        if (desiredLevel == connectionQualityLevel)
        {
            pendingConnectionQualityLevel = -1;
            pendingConnectionQualitySampleCount = 0;
            return;
        }

        if (pendingConnectionQualityLevel != desiredLevel)
        {
            pendingConnectionQualityLevel = desiredLevel;
            pendingConnectionQualitySampleCount = 1;
        }
        else
        {
            pendingConnectionQualitySampleCount++;
        }

        int requiredSamples =
            desiredLevel > connectionQualityLevel
                ? 2
                : 3;

        if (pendingConnectionQualitySampleCount < requiredSamples)
            return;

        connectionQualityLevel = desiredLevel;
        pendingConnectionQualityLevel = -1;
        pendingConnectionQualitySampleCount = 0;
    }

    private int GetDesiredConnectionQualityLevel()
    {
        int delayedThreshold =
            Mathf.Max(1, delayedConnectionThreshold);
        int unstableThreshold =
            Mathf.Max(
                delayedThreshold + 1,
                unstableConnectionThreshold
            );
        int recoveryHysteresis =
            Mathf.Max(0, connectionQualityRecoveryHysteresis);

        if (connectionQualityLevel >= 2 &&
            SmoothedRttMilliseconds >=
                unstableThreshold - recoveryHysteresis)
        {
            return 2;
        }

        if (SmoothedRttMilliseconds >= unstableThreshold)
            return 2;

        if (connectionQualityLevel >= 1 &&
            SmoothedRttMilliseconds >=
                delayedThreshold - recoveryHysteresis)
        {
            return 1;
        }

        return SmoothedRttMilliseconds >= delayedThreshold
            ? 1
            : 0;
    }

    private void ResetConnectionQuality()
    {
        SmoothedRttMilliseconds = 0;
        HasConnectionQualitySample = false;
        recentRttSampleCount = 0;
        recentRttSampleIndex = 0;
        connectionQualityLevel = 0;
        pendingConnectionQualityLevel = -1;
        pendingConnectionQualitySampleCount = 0;
        nextConnectionQualitySampleTime = 0f;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            OnUnitySceneLoaded;

        BindNetworkManagerCallbacks(null);

        if (Instance == this)
        {
            Instance = null;
        }
    }

    private bool TryRefreshNetworkReferences()
    {
        NetworkManager candidate = null;

        if (networkManager != null &&
            networkManager.IsListening)
        {
            candidate = networkManager;
        }
        else if (NetworkManager.Singleton != null &&
                 NetworkManager.Singleton.IsListening)
        {
            candidate = NetworkManager.Singleton;
        }

        NetworkManager[] managers =
            FindObjectsByType<NetworkManager>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

        if (candidate == null)
        {
            for (int i = 0;
                 i < managers.Length;
                 i++)
            {
                if (managers[i] != null &&
                    managers[i].IsListening)
                {
                    candidate = managers[i];
                    break;
                }
            }
        }

        if (candidate == null &&
            networkManager != null)
        {
            candidate = networkManager;
        }

        if (candidate == null &&
            NetworkManager.Singleton != null)
        {
            candidate = NetworkManager.Singleton;
        }

        if (candidate == null)
        {
            for (int i = 0;
                 i < managers.Length;
                 i++)
            {
                if (managers[i] != null)
                {
                    candidate = managers[i];
                    break;
                }
            }
        }

        if (candidate == null)
        {
            BindNetworkManagerCallbacks(null);
            networkManager = null;
            unityTransport = null;
            return false;
        }

        networkManager = candidate;
        unityTransport =
            networkManager.GetComponent<UnityTransport>();

        if (unityTransport == null)
        {
            BindNetworkManagerCallbacks(null);
            return false;
        }

        if (!networkManager.IsListening)
        {
            networkManager.NetworkConfig.ConnectionApproval = false;
            networkManager.NetworkConfig.SpawnTimeout =
                Mathf.Max(10f, deferredMessageSpawnTimeout);
        }

        if (NetworkManager.Singleton != networkManager)
            networkManager.SetSingleton();

        BindNetworkManagerCallbacks(networkManager);
        return true;
    }

    private void BindNetworkManagerCallbacks(
        NetworkManager target)
    {
        if (callbackNetworkManager == target)
            return;

        if (callbackNetworkManager != null)
        {
            callbackNetworkManager
                .OnClientConnectedCallback -=
                OnClientConnected;

            callbackNetworkManager
                .OnClientDisconnectCallback -=
                OnClientDisconnected;
        }

        callbackNetworkManager = target;

        if (callbackNetworkManager == null)
            return;

        callbackNetworkManager
            .OnClientConnectedCallback +=
            OnClientConnected;

        callbackNetworkManager
            .OnClientDisconnectCallback +=
            OnClientDisconnected;
    }

    private void OnUnitySceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        TryRefreshNetworkReferences();
    }

    public async Task<string> StartHostWithRelayAsync(string playerName)
    {
        if (IsBusy)
            return null;

        SetBusy(true);

        try
        {
            if (!TryRefreshNetworkReferences())
            {
                throw new InvalidOperationException(
                    "사용할 수 있는 NetworkManager를 찾지 못했습니다."
                );
            }

            if (!await WaitForPendingShutdownAsync())
            {
                throw new TimeoutException(
                    "이전 네트워크 연결 종료 시간이 초과됐습니다."
                );
            }

            if (networkManager.IsListening)
            {
                NotifyStatus(
                    "이미 네트워크 방에 연결되어 있습니다.",
                    true
                );

                return null;
            }

            ResetDisconnectRecoveryState();

            NotifyStatus("Unity Services에 연결 중입니다.", false);

            await InitializeServicesAsync();

            NotifyStatus(
                "최적 Relay 지역을 자동 선택하는 중입니다.",
                false
            );

            int maximumConnections = Mathf.Max(1, maxPlayers - 1);

            var allocation = await RelayService.Instance.CreateAllocationAsync(
                maximumConnections
            );

            CurrentRelayRegion = allocation.Region;

            Debug.Log(
                "Relay 자동 지역 할당 완료 - " +
                $"실제 할당 리전: {allocation.Region}"
            );

            unityTransport.SetRelayServerData(
                AllocationUtils.ToRelayServerData(allocation, connectionType)
            );

            string joinCode = await RelayService.Instance.GetJoinCodeAsync(
                allocation.AllocationId
            );

            if (!networkManager.StartHost())
                throw new InvalidOperationException("NGO Host 시작에 실패했습니다.");

            SpawnLobbyRoomManager();

            if (!await WaitForLobbyRoomManagerAsync())
                throw new TimeoutException("LobbyRoomManager 생성 시간이 초과됐습니다.");

            LobbyRoomManager.Instance.RegisterHostPlayer(playerName);

            if (!await WaitForLocalPlayerRegistrationAsync())
                throw new TimeoutException("호스트의 로비 등록 시간이 초과됐습니다.");

            CurrentJoinCode = joinCode.Trim().ToUpperInvariant();

            NotifyStatus(
                $"Relay 방 생성 완료 - Region: {CurrentRelayRegion}",
                false
            );

            return CurrentJoinCode;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);

            NotifyStatus($"방 생성 실패: {exception.Message}", true);

            await ShutdownAfterFailureAsync();

            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task<bool> StartClientWithRelayAsync(
        string joinCode,
        string playerName)
    {
        if (IsBusy)
        {
            return false;
        }

        joinCode =
            NormalizeJoinCode(
                joinCode
            );

        if (string.IsNullOrEmpty(joinCode))
        {
            NotifyStatus(
                "유효한 방 코드를 입력하세요.",
                true
            );

            return false;
        }

        SetBusy(true);

        try
        {
            if (!TryRefreshNetworkReferences())
            {
                throw new InvalidOperationException(
                    "사용할 수 있는 NetworkManager를 찾지 못했습니다."
                );
            }

            if (!await WaitForPendingShutdownAsync())
            {
                throw new TimeoutException(
                    "이전 네트워크 연결 종료 시간이 초과됐습니다."
                );
            }

            if (networkManager.IsListening)
            {
                NotifyStatus(
                    "이미 네트워크 방에 연결되어 있습니다.",
                    true
                );

                return false;
            }

            ResetDisconnectRecoveryState();

            NotifyStatus(
                "Unity Services에 연결 중입니다.",
                false
            );

            await InitializeServicesAsync();

            NotifyStatus(
                "Relay 방에 참가 중입니다.",
                false
            );

            var allocation =
                await RelayService.Instance
                    .JoinAllocationAsync(
                        joinCode
                    );

            unityTransport.SetRelayServerData(
                AllocationUtils.ToRelayServerData(
                    allocation,
                    connectionType
                )
            );

            if (!networkManager.StartClient())
            {
                throw new InvalidOperationException(
                    "NGO Client 시작에 실패했습니다."
                );
            }

            if (!await WaitForLocalConnectionAsync())
            {
                throw new TimeoutException(
                    "호스트 연결 시간이 초과됐습니다."
                );
            }

            if (!await WaitForLobbyRoomManagerAsync())
            {
                throw new TimeoutException(
                    "LobbyRoomManager 동기화 시간이 초과됐습니다."
                );
            }

            LobbyRoomManager.Instance
                .RegisterPlayerServerRpc(
                    new FixedString64Bytes(
                        playerName
                    )
                );

            if (!await WaitForLocalPlayerRegistrationAsync())
            {
                throw new InvalidOperationException(
                    "플레이어 로비 등록에 실패했습니다. " +
                    "중복된 닉네임이거나 방이 가득 찼을 수 있습니다."
                );
            }

            CurrentJoinCode =
                joinCode;

            NotifyStatus(
                "Relay 방 참가가 완료됐습니다.",
                false
            );

            return true;
        }
        catch (Exception exception)
        {
            Debug.LogException(
                exception
            );

            NotifyStatus(
                $"방 참가 실패: {exception.Message}",
                true
            );

            await ShutdownAfterFailureAsync();

            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void LeaveSession()
    {
        if (leaveSessionCoroutine != null)
            return;

        leaveSessionCoroutine =
            StartCoroutine(
                LeaveSessionRoutine()
            );
    }

    public void LeaveSessionAndReturnToLobby()
    {
        returnToLobbyAfterLeaveRequested = true;
        LeaveSession();
    }

    private IEnumerator LeaveSessionRoutine()
    {
        SetBusy(true);

        CurrentJoinCode = string.Empty;
        CurrentRelayRegion = string.Empty;
        pendingDisconnectMessage = string.Empty;
        unexpectedDisconnectRecoveryStarted = false;

        if (!TryRefreshNetworkReferences())
        {
            bool shouldReturnToLobby =
                returnToLobbyAfterLeaveRequested;

            returnToLobbyAfterLeaveRequested = false;
            leaveSessionCoroutine = null;
            SetBusy(false);

            if (shouldReturnToLobby)
                yield return LoadLobbySceneAfterLeave();

            yield break;
        }

        suppressDisconnectNotification = true;

        if (networkManager.IsServer &&
            LobbyRoomManager.Instance != null &&
            LobbyRoomManager.Instance.IsSpawned)
        {
            LobbyRoomManager.Instance.NetworkObject.Despawn(true);
        }

        if (networkManager.IsListening &&
            !networkManager.ShutdownInProgress)
        {
            networkManager.Shutdown();
        }

        float timeoutTime =
            Time.realtimeSinceStartup +
            connectionTimeout;

        while (networkManager != null &&
               (networkManager.IsListening ||
                networkManager.ShutdownInProgress) &&
               Time.realtimeSinceStartup < timeoutTime)
        {
            yield return null;
        }

        if (networkManager != null &&
            (networkManager.IsListening ||
             networkManager.ShutdownInProgress))
        {
            Debug.LogWarning(
                "네트워크 연결 종료 대기 시간이 초과됐습니다.",
                this
            );
        }

        bool returnToLobby =
            returnToLobbyAfterLeaveRequested;

        returnToLobbyAfterLeaveRequested = false;
        suppressDisconnectNotification = false;
        leaveSessionCoroutine = null;
        SetBusy(false);

        if (returnToLobby)
            yield return LoadLobbySceneAfterLeave();
    }

    private IEnumerator LoadLobbySceneAfterLeave()
    {
        if (LobbyRoomManager.Instance != null)
        {
            Destroy(LobbyRoomManager.Instance.gameObject);
            yield return null;
        }

        if (string.IsNullOrWhiteSpace(lobbySceneName))
        {
            Debug.LogError(
                "Lobby Scene Name is empty. Cannot return to the lobby after leaving the match.",
                this
            );

            yield break;
        }

        if (SceneManager.GetActiveScene().name != lobbySceneName)
        {
            AsyncOperation loadOperation =
                SceneManager.LoadSceneAsync(
                    lobbySceneName,
                    LoadSceneMode.Single
                );

            if (loadOperation != null)
                yield return loadOperation;
        }

        yield return null;
        TryRefreshNetworkReferences();

        if (networkManager != null &&
            !networkManager.IsListening &&
            NetworkManager.Singleton != networkManager)
        {
            networkManager.SetSingleton();
        }
    }

    private async Task InitializeServicesAsync()
    {
        if (UnityServices.State ==
            ServicesInitializationState.Uninitialized)
        {
            InitializationOptions options =
                new InitializationOptions();

            options.SetProfile(
                CreateAuthenticationProfile()
            );

            await UnityServices.InitializeAsync(
                options
            );
        }
        else
        {
            while (UnityServices.State ==
                   ServicesInitializationState.Initializing)
            {
                await Task.Yield();
            }
        }

        if (!AuthenticationService.Instance
                .IsSignedIn)
        {
            await AuthenticationService.Instance
                .SignInAnonymouslyAsync();
        }
    }

    private string CreateAuthenticationProfile()
    {
#if UNITY_EDITOR
        int processId =
            System.Diagnostics.Process
                .GetCurrentProcess()
                .Id;

        return $"Editor_{processId}";
#else
        return "Player";
#endif
    }

    private void SpawnLobbyRoomManager()
    {
        if (!networkManager.IsServer)
        {
            return;
        }

        if (LobbyRoomManager.Instance != null &&
            LobbyRoomManager.Instance.IsSpawned)
        {
            return;
        }

        if (lobbyRoomManagerPrefab == null)
        {
            throw new InvalidOperationException(
                "LobbyRoomManager Prefab이 연결되지 않았습니다."
            );
        }

        LobbyRoomManager lobbyManager =
            Instantiate(
                lobbyRoomManagerPrefab
            );

        NetworkObject networkObject =
            lobbyManager
                .GetComponent<NetworkObject>();

        if (networkObject == null)
        {
            Destroy(
                lobbyManager.gameObject
            );

            throw new InvalidOperationException(
                "LobbyRoomManager Prefab에 " +
                "NetworkObject가 없습니다."
            );
        }

        networkObject.Spawn(false);
    }

    private async Task<bool> WaitForPendingShutdownAsync()
    {
        if (networkManager == null)
            return false;

        float timeoutTime =
            Time.realtimeSinceStartup +
            connectionTimeout;

        while (networkManager != null &&
               networkManager.ShutdownInProgress &&
               Time.realtimeSinceStartup < timeoutTime)
        {
            await Task.Yield();
        }

        return networkManager != null &&
               !networkManager.ShutdownInProgress;
    }

    private async Task<bool> WaitForLocalConnectionAsync()
    {
        float timeoutTime =
            Time.realtimeSinceStartup +
            connectionTimeout;

        while (Time.realtimeSinceStartup <
               timeoutTime)
        {
            if (networkManager.IsConnectedClient)
            {
                return true;
            }

            if (!networkManager.IsListening)
            {
                return false;
            }

            await Task.Yield();
        }

        return false;
    }

    private async Task<bool> WaitForLobbyRoomManagerAsync()
    {
        float timeoutTime =
            Time.realtimeSinceStartup +
            connectionTimeout;

        while (Time.realtimeSinceStartup <
               timeoutTime)
        {
            if (LobbyRoomManager.Instance != null &&
                LobbyRoomManager.Instance.IsSpawned)
            {
                return true;
            }

            if (!networkManager.IsListening)
            {
                return false;
            }

            await Task.Yield();
        }

        return false;
    }

    private async Task<bool> WaitForLocalPlayerRegistrationAsync()
    {
        float timeoutTime =
            Time.realtimeSinceStartup +
            connectionTimeout;

        while (Time.realtimeSinceStartup <
               timeoutTime)
        {
            if (LobbyRoomManager.Instance != null &&
                LobbyRoomManager.Instance.IsSpawned &&
                LobbyRoomManager.Instance.ContainsPlayer(
                    networkManager.LocalClientId
                ))
            {
                return true;
            }

            if (!networkManager.IsListening)
            {
                return false;
            }

            await Task.Yield();
        }

        return false;
    }

    private async Task ShutdownAfterFailureAsync()
    {
        CurrentJoinCode = string.Empty;
        CurrentRelayRegion = string.Empty;

        if (!TryRefreshNetworkReferences())
            return;

        suppressDisconnectNotification = true;

        if (networkManager.IsServer &&
            LobbyRoomManager.Instance != null &&
            LobbyRoomManager.Instance.IsSpawned)
        {
            LobbyRoomManager.Instance.NetworkObject.Despawn(true);
        }

        if (networkManager.IsListening &&
            !networkManager.ShutdownInProgress)
        {
            networkManager.Shutdown();
        }

        await WaitForNetworkStoppedAsync();
    }

    private async Task<bool> WaitForNetworkStoppedAsync()
    {
        if (networkManager == null)
            return true;

        float timeoutTime =
            Time.realtimeSinceStartup +
            connectionTimeout;

        while (networkManager != null &&
               (networkManager.IsListening ||
                networkManager.ShutdownInProgress) &&
               Time.realtimeSinceStartup < timeoutTime)
        {
            await Task.Yield();
        }

        return networkManager == null ||
               (!networkManager.IsListening &&
                !networkManager.ShutdownInProgress);
    }

    private void OnClientConnected(
        ulong clientId)
    {
        if (clientId !=
            networkManager.LocalClientId)
        {
            return;
        }

        Debug.Log(
            $"로컬 클라이언트 연결 완료: {clientId}"
        );

        ResetConnectionQuality();
    }

    private void OnClientDisconnected(
        ulong clientId)
    {
        if (clientId !=
            networkManager.LocalClientId)
        {
            return;
        }

        CurrentJoinCode = string.Empty;
        CurrentRelayRegion = string.Empty;
        ResetConnectionQuality();

        if (suppressDisconnectNotification)
        {
            suppressDisconnectNotification = false;
            return;
        }

        string disconnectReason =
            networkManager.DisconnectReason;

        string message =
            string.IsNullOrWhiteSpace(disconnectReason)
                ? "호스트와의 연결이 종료됐습니다."
                : disconnectReason;

        pendingDisconnectMessage = message;

        NotifyStatus(
            message,
            true
        );

        UnexpectedlyDisconnected?.Invoke();

        if (!unexpectedDisconnectRecoveryStarted)
        {
            StartCoroutine(
                ReturnToLobbyAfterUnexpectedDisconnect()
            );
        }
    }

    private IEnumerator
        ReturnToLobbyAfterUnexpectedDisconnect()
    {
        unexpectedDisconnectRecoveryStarted = true;

        yield return null;

        TryRefreshNetworkReferences();

        if (networkManager != null)
        {
            suppressDisconnectNotification = true;

            if (networkManager.IsListening &&
                !networkManager.ShutdownInProgress)
            {
                networkManager.Shutdown();
            }

            float timeoutTime =
                Time.realtimeSinceStartup +
                connectionTimeout;

            while (networkManager != null &&
                   (networkManager.IsListening ||
                    networkManager.ShutdownInProgress) &&
                   Time.realtimeSinceStartup < timeoutTime)
            {
                yield return null;
            }
        }

        if (LobbyRoomManager.Instance != null)
        {
            Destroy(
                LobbyRoomManager.Instance.gameObject
            );

            yield return null;
        }

        if (string.IsNullOrWhiteSpace(lobbySceneName))
        {
            Debug.LogError(
                "Lobby Scene Name이 비어 있어 " +
                "연결 종료 후 로비로 돌아갈 수 없습니다.",
                this
            );

            unexpectedDisconnectRecoveryStarted = false;
            yield break;
        }

        Scene currentScene =
            SceneManager.GetActiveScene();

        if (currentScene.name != lobbySceneName)
        {
            AsyncOperation loadOperation =
                SceneManager.LoadSceneAsync(
                lobbySceneName,
                LoadSceneMode.Single
            );

            if (loadOperation != null)
                yield return loadOperation;
        }

        yield return null;

        TryRefreshNetworkReferences();

        unexpectedDisconnectRecoveryStarted = false;
    }

    private void ResetDisconnectRecoveryState()
    {
        suppressDisconnectNotification = false;
        unexpectedDisconnectRecoveryStarted = false;
        pendingDisconnectMessage = string.Empty;
    }

    private void SetBusy(
        bool busy)
    {
        if (IsBusy == busy)
            return;

        IsBusy = busy;
        BusyChanged?.Invoke(busy);
    }

    private void NotifyStatus(
        string message,
        bool isError)
    {
        StatusChanged?.Invoke(
            message,
            isError
        );
    }

    private string NormalizeJoinCode(
        string joinCode)
    {
        if (string.IsNullOrWhiteSpace(
                joinCode))
        {
            return string.Empty;
        }

        return joinCode
            .Trim()
            .ToUpperInvariant();
    }
}
