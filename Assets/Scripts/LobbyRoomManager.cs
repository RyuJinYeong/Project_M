using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

[RequireComponent(typeof(NetworkObject))]
public class LobbyRoomManager : NetworkBehaviour
{
    private const int MaximumPlayerNameLength = 8;
    private const int MaximumSupportedPlayers = 12;
    private const int UniqueMafiaRoleCount = 4;
    private const int UniqueNeutralRoleCount = 3;

    public static LobbyRoomManager Instance { get; private set; }

    [Header("게임 시작")]
    [SerializeField] private string gameSceneName = "GameScene";
    [SerializeField] private string lobbySceneName = "LobbyScene";

    [Range(1, MaximumSupportedPlayers)]
    [SerializeField] private int minimumPlayersToStart = 2;

    [Range(2, MaximumSupportedPlayers)]
    [SerializeField] private int maxPlayers =
        MaximumSupportedPlayers;

    public NetworkList<LobbyPlayer> LobbyPlayers;

    public NetworkVariable<LobbySettings> CurrentSettings =
        new NetworkVariable<LobbySettings>(
            new LobbySettings
            {
                mafiaCount = 1,
                citizenCount = 1,
                neutralCount = 0,
                requiredRoleMask = 0u,
                morningDiscussionSeconds = 60,
                morningVoteSeconds = 60,
                morningVoteResultSeconds = 5,
                nightPreparationSeconds = 15,
                nightActionSeconds = 60,
                nightResultSeconds = 5,
                allowDuplicateRoles = false,
                revealExiledTeam = false
            },
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    public NetworkVariable<FixedString128Bytes> LobbyNotice =
        new NetworkVariable<FixedString128Bytes>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    public event Action OnPlayerListChanged;
    public event Action<LobbySettings> OnSettingsChanged;
    public event Action<string> OnLobbyNoticeChanged;

    public bool GameStarted => gameStarted;

    private bool gameStarted;
    private bool returningToLobby;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        maxPlayers = MaximumSupportedPlayers;
        minimumPlayersToStart = Mathf.Clamp(
            minimumPlayersToStart,
            1,
            maxPlayers
        );

        LobbyPlayers = new NetworkList<LobbyPlayer>(
            null,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );
    }

    public override void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        NetworkObject.DestroyWithScene = false;

        transform.SetParent(null);

        DontDestroyOnLoad(gameObject);

        LobbyPlayers.OnListChanged += OnLobbyPlayersChanged;
        CurrentSettings.OnValueChanged += OnLobbySettingsChanged;
        LobbyNotice.OnValueChanged += OnLobbyNoticeValueChanged;
        SceneManager.sceneLoaded += OnSceneLoaded;

        if (IsServer)
        {
            gameStarted = false;

            LobbyPlayers.Clear();

            NetworkManager.OnClientDisconnectCallback += OnServerClientDisconnected;
        }

        OnPlayerListChanged?.Invoke();
        OnSettingsChanged?.Invoke(CurrentSettings.Value);
    }

    public override void OnNetworkDespawn()
    {
        LobbyPlayers.OnListChanged -= OnLobbyPlayersChanged;
        CurrentSettings.OnValueChanged -= OnLobbySettingsChanged;
        LobbyNotice.OnValueChanged -= OnLobbyNoticeValueChanged;
        SceneManager.sceneLoaded -= OnSceneLoaded;

        if (NetworkManager != null && IsServer)
            NetworkManager.OnClientDisconnectCallback -= OnServerClientDisconnected;
    }

    public bool RegisterHostPlayer(string playerName)
    {
        if (!IsServer)
            return false;

        return TryRegisterPlayer(NetworkManager.ServerClientId, playerName);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void RegisterPlayerServerRpc(FixedString64Bytes requestedName, RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong senderClientId = rpcParams.Receive.SenderClientId;

        TryRegisterPlayer(senderClientId, requestedName.ToString());
    }

    public bool ContainsPlayer(ulong clientId)
    {
        for (int i = 0; i < LobbyPlayers.Count; i++)
        {
            if (LobbyPlayers[i].clientId == clientId)
                return true;
        }

        return false;
    }

    public bool IsPlayerNameInUse(string playerName)
    {
        return IsPlayerNameInUse(playerName, ulong.MaxValue);
    }

    private bool TryRegisterPlayer(ulong clientId, string playerName)
    {
        if (!IsServer)
            return false;

        if (ContainsPlayer(clientId))
            return true;

        if (gameStarted)
        {
            RejectConnectedPlayer(clientId, "이미 게임이 시작됐습니다.");
            return false;
        }

        if (LobbyPlayers.Count >= maxPlayers)
        {
            RejectConnectedPlayer(clientId, "방이 가득 찼습니다.");
            return false;
        }

        playerName = NormalizePlayerName(playerName);

        bool isHost = clientId == NetworkManager.ServerClientId;

        if (string.IsNullOrEmpty(playerName))
        {
            if (isHost)
            {
                playerName = "Host";
            }
            else
            {
                RejectConnectedPlayer(clientId, "유효하지 않은 닉네임입니다.");
                return false;
            }
        }

        if (IsPlayerNameInUse(playerName, clientId))
        {
            RejectConnectedPlayer(clientId, "이미 사용 중인 닉네임입니다.");
            return false;
        }

        LobbyPlayers.Add(
            new LobbyPlayer
            {
                clientId = clientId,
                playerName = new FixedString64Bytes(playerName),
                isHost = isHost
            }
        );

        RebalanceSettingsForPlayerCount();

        Debug.Log(
            $"로비 플레이어 등록 - " +
            $"ClientId: {clientId}, " +
            $"Name: {playerName}, " +
            $"Host: {isHost}"
        );

        return true;
    }

    private bool IsPlayerNameInUse(string playerName, ulong ignoredClientId)
    {
        for (int i = 0; i < LobbyPlayers.Count; i++)
        {
            LobbyPlayer player = LobbyPlayers[i];

            if (player.clientId == ignoredClientId)
                continue;

            if (string.Equals(
                    player.playerName.ToString(),
                    playerName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void RejectConnectedPlayer(ulong clientId, string reason)
    {
        Debug.LogWarning(
            $"로비 등록 거절 - " +
            $"ClientId: {clientId}, " +
            $"Reason: {reason}"
        );

        if (clientId == NetworkManager.ServerClientId)
            return;

        if (NetworkManager.ConnectedClients.ContainsKey(clientId))
            NetworkManager.DisconnectClient(clientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void UpdateSettingsServerRpc(
        LobbySettings newSettings,
        LobbyCountChangeSource changeSource,
        RpcParams rpcParams = default)
    {
        if (!IsHostRequest(rpcParams))
            return;

        CurrentSettings.Value =
            NormalizeSettings(
                newSettings,
                changeSource
            );
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void KickPlayerServerRpc(ulong clientIdToKick, RpcParams rpcParams = default)
    {
        if (!IsHostRequest(rpcParams))
            return;

        if (clientIdToKick == NetworkManager.ServerClientId)
            return;

        if (!NetworkManager.ConnectedClients.ContainsKey(clientIdToKick))
            return;

        NetworkManager.DisconnectClient(clientIdToKick);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void StartGameServerRpc(RpcParams rpcParams = default)
    {
        if (!IsHostRequest(rpcParams) || gameStarted)
            return;

        if (!TryValidateGameStart(
                out string validationMessage))
        {
            Debug.LogWarning(validationMessage);
            return;
        }

        if (string.IsNullOrWhiteSpace(gameSceneName))
        {
            Debug.LogError("Game Scene Name이 비어 있습니다.");
            return;
        }

        if (NetworkManager.SceneManager == null)
        {
            Debug.LogError("NGO Scene Management가 활성화되지 않았습니다.");
            return;
        }

        CleanupResidualMatchObjectsBeforeGameStart();

        returningToLobby = false;
        gameStarted = true;
        LobbyNotice.Value = default;

        SceneEventProgressStatus result = NetworkManager.SceneManager.LoadScene(
            gameSceneName,
            LoadSceneMode.Single
        );

        if (result == SceneEventProgressStatus.Started)
            return;

        gameStarted = false;

        Debug.LogError($"GameScene 로드 시작 실패: {result}");
    }

    public bool ReturnToLobbyFromGame()
    {
        if (!IsServer || returningToLobby)
            return false;

        if (string.IsNullOrWhiteSpace(lobbySceneName))
        {
            Debug.LogError(
                "Lobby Scene Name이 비어 있습니다."
            );

            return false;
        }

        if (NetworkManager.SceneManager == null)
        {
            Debug.LogError(
                "NGO Scene Management가 활성화되지 않았습니다."
            );

            return false;
        }

        if (MatchManager.Instance != null)
            MatchManager.Instance.PrepareForLobbyReturn();

        returningToLobby = true;
        gameStarted = false;

        SceneEventProgressStatus result =
            NetworkManager.SceneManager.LoadScene(
                lobbySceneName,
                LoadSceneMode.Single
            );

        if (result == SceneEventProgressStatus.Started)
            return true;

        returningToLobby = false;
        gameStarted = true;

        Debug.LogError(
            $"LobbyScene 로드 시작 실패: {result}"
        );

        return false;
    }

    public void SetLobbyNotice(string message)
    {
        if (!IsServer)
            return;

        LobbyNotice.Value = string.IsNullOrWhiteSpace(message)
            ? default
            : new FixedString128Bytes(message);
    }

    private void OnSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        if (scene.name == lobbySceneName)
        {
            gameStarted = false;
            returningToLobby = false;

            OnPlayerListChanged?.Invoke();
            OnSettingsChanged?.Invoke(
                CurrentSettings.Value
            );

            return;
        }

        if (scene.name == gameSceneName)
        {
            gameStarted = true;
            returningToLobby = false;
        }
    }

    private void OnLobbyNoticeValueChanged(
        FixedString128Bytes previous,
        FixedString128Bytes current)
    {
        OnLobbyNoticeChanged?.Invoke(
            current.ToString()
        );
    }

    private void CleanupResidualMatchObjectsBeforeGameStart()
    {
        if (!IsServer)
            return;

        NetworkObject[] networkObjects =
            FindObjectsByType<NetworkObject>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

        int cleanupCount = 0;

        for (int i = 0;
             i < networkObjects.Length;
             i++)
        {
            NetworkObject networkObject =
                networkObjects[i];

            if (networkObject == null ||
                networkObject == NetworkObject)
            {
                continue;
            }

            bool isMatchObject =
                networkObject.GetComponent<PlayerSpirit>() !=
                    null ||
                networkObject.GetComponent<PlayerBody>() !=
                    null ||
                networkObject.GetComponent<DroppedRoleTool>() !=
                    null;

            if (!isMatchObject)
                continue;

            if (networkObject.IsSpawned)
                networkObject.Despawn(true);
            else
                Destroy(networkObject.gameObject);

            cleanupCount++;
        }

        if (cleanupCount > 0)
        {
            Debug.LogWarning(
                $"게임 시작 전 이전 매치 객체 " +
                $"{cleanupCount}개를 정리했습니다."
            );
        }
    }

    public bool TryValidateGameStart(
        out string validationMessage)
    {
        validationMessage = string.Empty;

        int effectiveMinimumPlayers =
            Mathf.Max(
                3,
                minimumPlayersToStart
            );

        if (LobbyPlayers.Count < effectiveMinimumPlayers)
        {
            validationMessage =
                $"게임 시작에는 최소 {effectiveMinimumPlayers}명이 필요합니다.";

            return false;
        }

        LobbySettings settings =
            NormalizeSettings(
                CurrentSettings.Value,
                LobbyCountChangeSource.Automatic
            );

        if (LobbyPlayers.Count != settings.TotalPlayerCount)
        {
            validationMessage =
                $"현재 참가자는 {LobbyPlayers.Count}명입니다. " +
                $"설정 인원 {settings.TotalPlayerCount}명과 맞춰주세요.";

            return false;
        }

        if (settings.mafiaCount < 1)
        {
            validationMessage =
                "마녀는 최소 1명이어야 합니다.";

            return false;
        }

        if (settings.citizenCount <=
            settings.mafiaCount)
        {
            validationMessage =
                "주민 수는 마녀 수보다 최소 1명 많아야 합니다.";

            return false;
        }

        int requiredMafiaCount = 0;
        int requiredCitizenCount = 0;
        int requiredNeutralCount = 0;

        Array roleValues =
            Enum.GetValues(typeof(RoleId));

        for (int i = 0; i < roleValues.Length; i++)
        {
            RoleId role =
                (RoleId)roleValues.GetValue(i);

            if (!settings.IsRoleRequired(role))
                continue;

            RoleTeam team =
                GetSettingsRoleTeam(role);

            if (team == RoleTeam.Mafia)
                requiredMafiaCount++;
            else if (team == RoleTeam.Citizen)
                requiredCitizenCount++;
            else if (team == RoleTeam.Neutral)
                requiredNeutralCount++;
        }

        if (requiredMafiaCount > settings.mafiaCount)
        {
            validationMessage =
                $"필수 마녀 직업이 {requiredMafiaCount}개지만 " +
                $"마녀 인원은 {settings.mafiaCount}명입니다.";

            return false;
        }

        if (requiredCitizenCount > settings.citizenCount)
        {
            validationMessage =
                $"필수 주민 직업이 {requiredCitizenCount}개지만 " +
                $"주민 인원은 {settings.citizenCount}명입니다.";

            return false;
        }

        if (requiredNeutralCount > settings.neutralCount)
        {
            validationMessage =
                $"필수 이방인 직업이 {requiredNeutralCount}개지만 " +
                $"이방인 인원은 {settings.neutralCount}명입니다.";

            return false;
        }

        return true;
    }

    private LobbySettings NormalizeSettings(
        LobbySettings settings,
        LobbyCountChangeSource changeSource)
    {
        int playerCount =
            Mathf.Clamp(
                LobbyPlayers.Count,
                0,
                maxPlayers
            );

        settings.requiredRoleMask =
            SanitizeRequiredRoleMask(
                settings.requiredRoleMask
            );

        NormalizePhaseDurations(
            ref settings
        );

        if (playerCount <= 0)
        {
            settings.mafiaCount = 0;
            settings.citizenCount = 0;
            settings.neutralCount = 0;

            return settings;
        }

        if (playerCount <= 2)
        {
            settings.mafiaCount = 0;
            settings.citizenCount = playerCount;
            settings.neutralCount = 0;

            return settings;
        }

        /*
         * 마녀 1명과 그보다 최소 1명 많은 주민을
         * 항상 남겨야 하므로 이방인은 최대 전체 인원 - 3명이다.
         */
        int maximumNeutralCount =
            playerCount - 3;

        if (!settings.allowDuplicateRoles)
        {
            maximumNeutralCount =
                Mathf.Min(
                    maximumNeutralCount,
                    UniqueNeutralRoleCount
                );
        }

        settings.neutralCount =
            Mathf.Clamp(
                settings.neutralCount,
                0,
                maximumNeutralCount
            );

        int nonNeutralCount =
            playerCount -
            settings.neutralCount;

        /*
         * citizenCount >= mafiaCount + 1
         * 을 만족하는 최대 마녀 수.
         *
         * 8명 -> 최대 마녀 3, 주민 5
         * 7명 -> 최대 마녀 3, 주민 4
         */
        int maximumMafiaCount =
            Mathf.Max(
                1,
                (nonNeutralCount - 1) / 2
            );

        if (!settings.allowDuplicateRoles)
        {
            maximumMafiaCount =
                Mathf.Min(
                    maximumMafiaCount,
                    UniqueMafiaRoleCount
                );
        }

        switch (changeSource)
        {
            case LobbyCountChangeSource.Citizen:
            {
                int minimumCitizenCount =
                    nonNeutralCount -
                    maximumMafiaCount;

                int maximumCitizenCount =
                    nonNeutralCount - 1;

                settings.citizenCount =
                    Mathf.Clamp(
                        settings.citizenCount,
                        minimumCitizenCount,
                        maximumCitizenCount
                    );

                settings.mafiaCount =
                    nonNeutralCount -
                    settings.citizenCount;

                break;
            }

            case LobbyCountChangeSource.Mafia:
            case LobbyCountChangeSource.Neutral:
            case LobbyCountChangeSource.Automatic:
            default:
            {
                settings.mafiaCount =
                    Mathf.Clamp(
                        settings.mafiaCount,
                        1,
                        maximumMafiaCount
                    );

                settings.citizenCount =
                    nonNeutralCount -
                    settings.mafiaCount;

                break;
            }
        }

        return settings;
    }

    private void NormalizePhaseDurations(
        ref LobbySettings settings)
    {
        settings.morningDiscussionSeconds =
            NormalizePhaseDuration(
                settings.morningDiscussionSeconds,
                30
            );

        settings.morningVoteSeconds =
            NormalizePhaseDuration(
                settings.morningVoteSeconds,
                30
            );

        settings.morningVoteResultSeconds =
            NormalizePhaseDuration(
                settings.morningVoteResultSeconds,
                3
            );

        settings.nightPreparationSeconds =
            NormalizePhaseDuration(
                settings.nightPreparationSeconds,
                10
            );

        settings.nightActionSeconds =
            NormalizePhaseDuration(
                settings.nightActionSeconds,
                30
            );

        settings.nightResultSeconds =
            NormalizePhaseDuration(
                settings.nightResultSeconds,
                3
            );
    }

    private int NormalizePhaseDuration(
        int requestedSeconds,
        int minimumSeconds)
    {
        return Mathf.Max(
            minimumSeconds,
            requestedSeconds
        );
    }

    private void RebalanceSettingsForPlayerCount()
    {
        if (!IsServer)
            return;

        LobbySettings normalizedSettings =
            NormalizeSettings(
                CurrentSettings.Value,
                LobbyCountChangeSource.Automatic
            );

        if (!CurrentSettings.Value.Equals(
                normalizedSettings))
        {
            CurrentSettings.Value =
                normalizedSettings;
        }
    }

    private uint SanitizeRequiredRoleMask(
        uint requiredRoleMask)
    {
        uint sanitizedMask = 0u;
        Array roleValues =
            Enum.GetValues(typeof(RoleId));

        for (int i = 0; i < roleValues.Length; i++)
        {
            RoleId role =
                (RoleId)roleValues.GetValue(i);

            int bitIndex = (int)role;

            if (bitIndex < 0 || bitIndex >= 32)
                continue;

            uint roleBit = 1u << bitIndex;

            if ((requiredRoleMask & roleBit) == 0u)
                continue;

            RoleTeam team =
                GetSettingsRoleTeam(role);

            bool isSelectableRole =
                team == RoleTeam.Citizen ||
                team == RoleTeam.Neutral ||
                team == RoleTeam.Mafia;

            if (isSelectableRole)
                sanitizedMask |= roleBit;
        }

        return sanitizedMask;
    }

    private RoleTeam GetSettingsRoleTeam(RoleId role)
    {
        switch (role)
        {
            case RoleId.CurseCaster:
            case RoleId.Spy:
            case RoleId.Alchemist:
            case RoleId.Infiltrator:
                return RoleTeam.Mafia;

            case RoleId.Thief:
            case RoleId.SerialKiller:
            case RoleId.Martyr:
                return RoleTeam.Neutral;

            default:
                return RoleTeam.Citizen;
        }
    }

    private bool IsHostRequest(RpcParams rpcParams)
    {
        return IsServer &&
               rpcParams.Receive.SenderClientId == NetworkManager.ServerClientId;
    }

    private void OnServerClientDisconnected(ulong clientId)
    {
        if (!IsServer)
            return;

        for (int i = LobbyPlayers.Count - 1; i >= 0; i--)
        {
            if (LobbyPlayers[i].clientId != clientId)
                continue;

            LobbyPlayers.RemoveAt(i);
            RebalanceSettingsForPlayerCount();
            break;
        }
    }

    private void OnLobbyPlayersChanged(NetworkListEvent<LobbyPlayer> changeEvent)
    {
        OnPlayerListChanged?.Invoke();
    }

    private void OnLobbySettingsChanged(LobbySettings previous, LobbySettings current)
    {
        OnSettingsChanged?.Invoke(current);
    }

    private string NormalizePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        playerName = playerName.Trim();

        if (playerName.Length > MaximumPlayerNameLength)
        {
            playerName = playerName.Substring(
                0,
                MaximumPlayerNameLength
            );
        }

        return playerName;
    }
}
