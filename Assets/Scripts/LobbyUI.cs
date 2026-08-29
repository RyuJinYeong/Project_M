using System.Collections;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class LobbyUI : MonoBehaviour
{
    private const int MaximumPlayerNameLength = 8;

    private enum HelpPage
    {
        Controls,
        Rules,
        Audio,
        Graphics
    }

    [System.Serializable]
    private sealed class RequiredRoleToggleBinding
    {
        public RoleId role;
        public Toggle toggle;
    }

    public static string LocalPlayerName
    {
        get;
        private set;
    } = "Player";

    [Header("연결 관리자")]
    [SerializeField]
    private RelayConnectionManager relayConnectionManager;

    [Header("화면")]
    public GameObject loginScreen;
    public GameObject lobbyRoomScreen;
    public GameObject settingsPanel;

    [Header("로그인 화면")]
    public TMP_InputField nameInputField;
    public TMP_InputField inviteCodeInputField;
    public Button createRoomButton;
    public Button joinRoomButton;
    public Button exitGameButton;
    public TextMeshProUGUI statusText;

    [Header("로비 화면")]
    public TextMeshProUGUI inviteCodeText;
    public Transform playerListContainer;
    public GameObject playerEntryPrefab;
    public Button openSettingsButton;
    public Button startGameButton;
    public Button leaveRoomButton;

    [Header("설정 화면")]
    public TextMeshProUGUI mafiaCountText;
    public TextMeshProUGUI citizenCountText;
    public Slider mafiaSlider;
    public Slider citizenSlider;

    [Header("이방인 인원")]
    [SerializeField] private TextMeshProUGUI neutralCountText;
    [SerializeField] private Slider neutralSlider;

    [Header("필수 직업")]
    [SerializeField]
    private RequiredRoleToggleBinding[] requiredRoleToggles;

    [Header("매치 시간")]
    [Tooltip("통합 아침 투표 시간입니다. 초 단위 정수이며 최소 30초입니다.")]
    [SerializeField] private TMP_InputField morningVoteDurationInput;

    [Tooltip("초 단위 정수 입력입니다. 최소 3초.")]
    [SerializeField] private TMP_InputField morningVoteResultDurationInput;

    [Tooltip("초 단위 정수 입력입니다. 최소 10초.")]
    [SerializeField] private TMP_InputField nightPreparationDurationInput;

    [Tooltip("초 단위 정수 입력입니다. 최소 30초.")]
    [SerializeField] private TMP_InputField nightActionDurationInput;

    [Tooltip("초 단위 정수 입력입니다. 최소 3초.")]
    [SerializeField] private TMP_InputField nightResultDurationInput;

    [Header("게임 규칙")]
    [SerializeField] private Toggle allowDuplicateRolesToggle;
    [SerializeField] private Toggle revealExiledTeamToggle;

    [Header("도움말")]
    [SerializeField] private Button openHelpButton;
    [SerializeField] private GameObject helpPanel;
    [SerializeField] private Button helpControlsTabButton;
    [SerializeField] private Button helpRulesTabButton;
    [SerializeField] private Button helpAudioTabButton;
    [SerializeField] private Button helpGraphicsTabButton;
    [SerializeField] private Button closeHelpButton;
    [SerializeField] private GameObject helpControlsPage;
    [SerializeField] private GameObject helpRulesPage;
    [SerializeField] private GameObject helpAudioPage;
    [SerializeField] private GameObject helpGraphicsPage;

    [Header("개인 환경 설정")]
    [Tooltip("게임 플레이 UI와 같은 순서: 마우스 감도, 게임 음량, 음성 채팅 음량, 마이크, 음성 수신, 화면 모드, 해상도, 그래픽 품질, FPS 제한, 화면 효과")]
    [SerializeField] private Button[] personalSettingButtons;
    [SerializeField] private TextMeshProUGUI[] personalSettingValueTexts;
    private RightClickHandler mouseSensitivityRightClickHandler;

    public Button closeSettingsButton;

    private const string MasterVolumePreferenceKey =
        "Personal.MasterVolume";
    private const string FullScreenModePreferenceKey =
        "Personal.FullScreenMode";
    private const string ResolutionWidthPreferenceKey =
        "Personal.ResolutionWidth";
    private const string ResolutionHeightPreferenceKey =
        "Personal.ResolutionHeight";
    private const string QualityLevelPreferenceKey =
        "Personal.QualityLevel";

    private static readonly string[] PersonalSettingButtonNames =
    {
        "MouseSensitivityButton",
        "MasterVolumeButton",
        "VoiceVolumeButton",
        "MicrophoneButton",
        "VoiceOutputButton",
        "FullScreenModeButton",
        "ResolutionButton",
        "QualityButton",
        "FrameRateButton",
        "ScreenEffectButton"
    };

    private LobbyRoomManager
        boundLobbyRoomManager;

    private static string persistentInviteCode =
        string.Empty;

    private string currentInviteCode =
        string.Empty;

    private bool leavingRoom;

    private void Start()
    {
        if (nameInputField != null)
        {
            nameInputField.characterLimit =
                MaximumPlayerNameLength;
        }

        /*
         * 게임에서 LobbyScene으로 돌아오면 씬에 새로 배치된
         * RelayConnectionManager는 영속 싱글턴과 중복되어 파괴된다.
         * 이때 SerializedField가 파괴 예정 인스턴스를 잠깐 참조할 수
         * 있으므로 항상 살아 있는 싱글턴을 우선 사용한다.
         */
        if (RelayConnectionManager.Instance != null)
        {
            relayConnectionManager =
                RelayConnectionManager.Instance;
        }

        if (relayConnectionManager == null)
        {
            Debug.LogError(
                "RelayConnectionManager가 없습니다.",
                this
            );

            enabled = false;
            return;
        }

        LoadSavedPlayerName();
        RestoreNeutralRoleToggleBindings();

        PrepareHelpPanelOverlay();
        CachePersonalSettingsUIReferences();
        ApplySavedPersonalSettings();
        RefreshPersonalSettingsUI();
        RegisterButtonEvents();
        SetHelpPanelVisible(false);

        relayConnectionManager.StatusChanged += OnRelayStatusChanged;

        relayConnectionManager.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
        relayConnectionManager.BusyChanged += OnRelayBusyChanged;

        SetConnectionControlsInteractable(
            !relayConnectionManager.IsBusy
        );

        ShowLoginScreen();

        if (relayConnectionManager
                .TryConsumePendingDisconnectMessage(
                    out string pendingDisconnectMessage))
        {
            currentInviteCode = string.Empty;
            persistentInviteCode = string.Empty;

            SetStatus(
                pendingDisconnectMessage,
                Color.red
            );
        }
        else
        {
            SetStatus(
                string.Empty,
                Color.white
            );
        }

        StartCoroutine(
            RestoreConnectedLobbyRoutine()
        );
    }

    private void Update()
    {
        if (helpPanel == null ||
            !helpPanel.activeSelf ||
            Keyboard.current == null ||
            !Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            return;
        }

        SetHelpPanelVisible(false);
    }

    private void OnDestroy()
    {
        UnbindLobbyEvents();
        UnregisterButtonEvents();

        if (relayConnectionManager != null)
        {
            relayConnectionManager.StatusChanged -= OnRelayStatusChanged;

            relayConnectionManager.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
            relayConnectionManager.BusyChanged -= OnRelayBusyChanged;
        }
    }

    private void LoadSavedPlayerName()
    {
        if (PlayerPrefs.HasKey(
                "PlayerName"))
        {
            nameInputField.text =
                NormalizePlayerName(
                    PlayerPrefs.GetString(
                        "PlayerName"
                    )
                );

            if (!string.IsNullOrEmpty(
                    nameInputField.text))
            {
                return;
            }
        }

        nameInputField.text =
            "Player" +
            Random.Range(
                100,
                1000
            );
    }

    private void RestoreNeutralRoleToggleBindings()
    {
        if (requiredRoleToggles == null)
            return;

        for (int i = 0; i < requiredRoleToggles.Length; i++)
        {
            RequiredRoleToggleBinding binding =
                requiredRoleToggles[i];

            if (binding == null ||
                binding.toggle == null ||
                binding.role != RoleId.Thief)
            {
                continue;
            }

            string toggleName =
                binding.toggle.gameObject.name;

            if (toggleName == "MurderousFiendToggle")
                binding.role = RoleId.SerialKiller;
            else if (toggleName == "MartyrToggle")
                binding.role = RoleId.Martyr;
        }
    }

    private void RegisterButtonEvents()
    {
        createRoomButton.onClick.AddListener(
            OnCreateRoomClicked
        );

        joinRoomButton.onClick.AddListener(
            OnJoinRoomClicked
        );

        exitGameButton.onClick.AddListener(
            OnExitGameClicked
        );

        openSettingsButton.onClick.AddListener(
            OnOpenSettingsClicked
        );

        closeSettingsButton.onClick.AddListener(
            OnCloseSettingsClicked
        );

        leaveRoomButton.onClick.AddListener(
            OnLeaveRoomClicked
        );

        startGameButton.onClick.AddListener(
            OnStartGameClicked
        );

        if (openHelpButton != null)
            openHelpButton.onClick.AddListener(OnOpenHelpClicked);

        if (helpControlsTabButton != null)
            helpControlsTabButton.onClick.AddListener(OnHelpControlsTabClicked);

        if (helpRulesTabButton != null)
            helpRulesTabButton.onClick.AddListener(OnHelpRulesTabClicked);

        if (helpAudioTabButton != null)
            helpAudioTabButton.onClick.AddListener(OnHelpAudioTabClicked);

        if (helpGraphicsTabButton != null)
            helpGraphicsTabButton.onClick.AddListener(OnHelpGraphicsTabClicked);

        if (closeHelpButton != null)
            closeHelpButton.onClick.AddListener(OnCloseHelpClicked);

        RegisterPersonalSettingEvents();

        mafiaSlider.onValueChanged.AddListener(
            OnMafiaSliderChanged
        );

        citizenSlider.onValueChanged.AddListener(
            OnCitizenSliderChanged
        );

        if (neutralSlider != null)
        {
            neutralSlider.onValueChanged.AddListener(
                OnNeutralSliderChanged
            );
        }

        RegisterDurationInputEvents();
        RegisterRequiredRoleToggleEvents();
        RegisterGameRuleToggleEvents();
    }

    private void UnregisterButtonEvents()
    {
        createRoomButton.onClick.RemoveListener(
            OnCreateRoomClicked
        );

        joinRoomButton.onClick.RemoveListener(
            OnJoinRoomClicked
        );

        exitGameButton.onClick.RemoveListener(
            OnExitGameClicked
        );

        openSettingsButton.onClick.RemoveListener(
            OnOpenSettingsClicked
        );

        closeSettingsButton.onClick.RemoveListener(
            OnCloseSettingsClicked
        );

        leaveRoomButton.onClick.RemoveListener(
            OnLeaveRoomClicked
        );

        startGameButton.onClick.RemoveListener(
            OnStartGameClicked
        );

        if (openHelpButton != null)
            openHelpButton.onClick.RemoveListener(OnOpenHelpClicked);

        if (helpControlsTabButton != null)
            helpControlsTabButton.onClick.RemoveListener(OnHelpControlsTabClicked);

        if (helpRulesTabButton != null)
            helpRulesTabButton.onClick.RemoveListener(OnHelpRulesTabClicked);

        if (helpAudioTabButton != null)
            helpAudioTabButton.onClick.RemoveListener(OnHelpAudioTabClicked);

        if (helpGraphicsTabButton != null)
            helpGraphicsTabButton.onClick.RemoveListener(OnHelpGraphicsTabClicked);

        if (closeHelpButton != null)
            closeHelpButton.onClick.RemoveListener(OnCloseHelpClicked);

        UnregisterPersonalSettingEvents();

        mafiaSlider.onValueChanged.RemoveListener(
            OnMafiaSliderChanged
        );

        citizenSlider.onValueChanged.RemoveListener(
            OnCitizenSliderChanged
        );

        if (neutralSlider != null)
        {
            neutralSlider.onValueChanged.RemoveListener(
                OnNeutralSliderChanged
            );
        }

        UnregisterDurationInputEvents();
        UnregisterRequiredRoleToggleEvents();
        UnregisterGameRuleToggleEvents();
    }

    private void RegisterDurationInputEvents()
    {
        RegisterDurationInput(
            morningVoteDurationInput
        );

        RegisterDurationInput(
            morningVoteResultDurationInput
        );

        RegisterDurationInput(
            nightPreparationDurationInput
        );

        RegisterDurationInput(
            nightActionDurationInput
        );

        RegisterDurationInput(
            nightResultDurationInput
        );
    }

    private void UnregisterDurationInputEvents()
    {
        UnregisterDurationInput(
            morningVoteDurationInput
        );

        UnregisterDurationInput(
            morningVoteResultDurationInput
        );

        UnregisterDurationInput(
            nightPreparationDurationInput
        );

        UnregisterDurationInput(
            nightActionDurationInput
        );

        UnregisterDurationInput(
            nightResultDurationInput
        );
    }

    private void RegisterDurationInput(
        TMP_InputField inputField)
    {
        if (inputField == null)
            return;

        ConfigureDurationInputField(
            inputField
        );

        inputField.onEndEdit.AddListener(
            OnDurationInputEndEdit
        );
    }

    private void UnregisterDurationInput(
        TMP_InputField inputField)
    {
        if (inputField == null)
            return;

        inputField.onEndEdit.RemoveListener(
            OnDurationInputEndEdit
        );
    }

    private void RegisterRequiredRoleToggleEvents()
    {
        if (requiredRoleToggles == null)
            return;

        for (int i = 0; i < requiredRoleToggles.Length; i++)
        {
            RequiredRoleToggleBinding binding =
                requiredRoleToggles[i];

            if (binding == null || binding.toggle == null)
                continue;

            if (!System.Enum.IsDefined(
                    typeof(RoleId),
                    binding.role))
            {
                binding.toggle.gameObject.SetActive(false);
                continue;
            }

            binding.toggle.onValueChanged.AddListener(
                OnSettingsToggleChanged
            );
        }
    }

    private void UnregisterRequiredRoleToggleEvents()
    {
        if (requiredRoleToggles == null)
            return;

        for (int i = 0; i < requiredRoleToggles.Length; i++)
        {
            RequiredRoleToggleBinding binding =
                requiredRoleToggles[i];

            if (binding == null || binding.toggle == null)
                continue;

            if (!System.Enum.IsDefined(
                    typeof(RoleId),
                    binding.role))
            {
                continue;
            }

            binding.toggle.onValueChanged.RemoveListener(
                OnSettingsToggleChanged
            );
        }
    }

    private void RegisterGameRuleToggleEvents()
    {
        if (allowDuplicateRolesToggle != null)
        {
            allowDuplicateRolesToggle.onValueChanged.AddListener(
                OnSettingsToggleChanged
            );
        }

        if (revealExiledTeamToggle != null)
        {
            revealExiledTeamToggle.onValueChanged.AddListener(
                OnSettingsToggleChanged
            );
        }
    }

    private void UnregisterGameRuleToggleEvents()
    {
        if (allowDuplicateRolesToggle != null)
        {
            allowDuplicateRolesToggle.onValueChanged.RemoveListener(
                OnSettingsToggleChanged
            );
        }

        if (revealExiledTeamToggle != null)
        {
            revealExiledTeamToggle.onValueChanged.RemoveListener(
                OnSettingsToggleChanged
            );
        }
    }

    private async void OnCreateRoomClicked()
    {
        if (relayConnectionManager.IsBusy)
        {
            return;
        }

        string playerName =
            NormalizePlayerName(
                nameInputField.text
            );

        if (string.IsNullOrEmpty(playerName))
        {
            SetStatus("유효한 닉네임을 입력하세요.", Color.red);

            return;
        }

        SavePlayerName(
            playerName
        );

        SetConnectionControlsInteractable(
            false
        );

        SetStatus(
            "Relay 방을 생성 중입니다.",
            Color.yellow
        );

        string joinCode =
            await relayConnectionManager.StartHostWithRelayAsync(
                    playerName
                );

        SetConnectionControlsInteractable(
            true
        );

        if (string.IsNullOrEmpty(
                joinCode))
        {
            return;
        }

        currentInviteCode =
            joinCode;
        persistentInviteCode =
            joinCode;

        OpenLobbyScreen(
            true
        );
    }

    private async void OnJoinRoomClicked()
    {
        if (relayConnectionManager.IsBusy)
        {
            return;
        }

        string playerName =
            NormalizePlayerName(
                nameInputField.text
            );

        string joinCode =
            NormalizeJoinCode(
                inviteCodeInputField.text
            );

        if (string.IsNullOrEmpty(
                playerName))
        {
            SetStatus(
                "유효한 닉네임을 입력하세요.",
                Color.red
            );

            return;
        }

        if (string.IsNullOrEmpty(
                joinCode))
        {
            SetStatus(
                "방 코드를 입력하세요.",
                Color.red
            );

            return;
        }

        SavePlayerName(
            playerName
        );

        SetConnectionControlsInteractable(
            false
        );

        SetStatus(
            "Relay 방에 참가 중입니다.",
            Color.yellow
        );

        bool connected =
            await relayConnectionManager.StartClientWithRelayAsync(
                    joinCode,
                    playerName
                );

        SetConnectionControlsInteractable(
            true
        );

        if (!connected)
        {
            return;
        }

        currentInviteCode =
            joinCode;
        persistentInviteCode =
            joinCode;

        OpenLobbyScreen(
            false
        );
    }

    private IEnumerator RestoreConnectedLobbyRoutine()
    {
        yield return null;

        if (NetworkManager.Singleton == null ||
            !NetworkManager.Singleton.IsListening)
        {
            yield break;
        }

        float timeout =
            Time.realtimeSinceStartup + 5f;

        while (LobbyRoomManager.Instance == null &&
               Time.realtimeSinceStartup < timeout)
        {
            yield return null;
        }

        if (LobbyRoomManager.Instance == null ||
            !LobbyRoomManager.Instance.IsSpawned ||
            !LobbyRoomManager.Instance.ContainsPlayer(
                NetworkManager.Singleton.LocalClientId
            ))
        {
            yield break;
        }

        string activeJoinCode = NormalizeJoinCode(
            relayConnectionManager.CurrentJoinCode
        );

        if (!string.IsNullOrEmpty(activeJoinCode))
            persistentInviteCode = activeJoinCode;

        currentInviteCode = persistentInviteCode;

        OpenLobbyScreen(
            NetworkManager.Singleton.IsHost
        );
    }

    private void SavePlayerName(
        string playerName)
    {
        LocalPlayerName =
            playerName;

        PlayerPrefs.SetString(
            "PlayerName",
            playerName
        );

        PlayerPrefs.Save();
    }

    private void OpenLobbyScreen(
        bool isHost)
    {
        if (LobbyRoomManager.Instance ==
                null ||
            !LobbyRoomManager.Instance
                .IsSpawned)
        {
            SetStatus(
                "LobbyRoomManager가 준비되지 않았습니다.",
                Color.red
            );

            return;
        }

        string activeJoinCode = NormalizeJoinCode(
            relayConnectionManager.CurrentJoinCode
        );

        if (!string.IsNullOrEmpty(activeJoinCode))
        {
            currentInviteCode = activeJoinCode;
            persistentInviteCode = activeJoinCode;
        }

        BindLobbyEvents();

        loginScreen.SetActive(false);
        lobbyRoomScreen.SetActive(true);
        settingsPanel.SetActive(false);

        SetSettingsInteractable(
            isHost
        );

        startGameButton.gameObject
            .SetActive(isHost);

        inviteCodeText.text =
            $"방 코드: {currentInviteCode}";

        UpdatePlayerListUI();

        UpdateSettingsUI(
            LobbyRoomManager.Instance
                .CurrentSettings
                .Value
        );

        string lobbyNotice =
            LobbyRoomManager.Instance
                .LobbyNotice.Value.ToString();

        if (string.IsNullOrWhiteSpace(lobbyNotice))
        {
            SetStatus(
                "로비에 연결됐습니다.",
                Color.green
            );
        }
        else
        {
            SetStatus(
                lobbyNotice,
                Color.red
            );
        }
    }

    private void ShowLoginScreen()
    {
        loginScreen.SetActive(true);
        lobbyRoomScreen.SetActive(false);
        settingsPanel.SetActive(false);
    }

    private void BindLobbyEvents()
    {
        if (boundLobbyRoomManager ==
            LobbyRoomManager.Instance)
        {
            return;
        }

        UnbindLobbyEvents();

        boundLobbyRoomManager =
            LobbyRoomManager.Instance;

        if (boundLobbyRoomManager == null)
        {
            return;
        }

        boundLobbyRoomManager
            .OnPlayerListChanged +=
            OnLobbyPlayerListChanged;

        boundLobbyRoomManager
            .OnSettingsChanged +=
            UpdateSettingsUI;

        boundLobbyRoomManager
            .OnLobbyNoticeChanged +=
            OnLobbyNoticeChanged;
    }

    private void UnbindLobbyEvents()
    {
        if (boundLobbyRoomManager == null)
        {
            return;
        }

        boundLobbyRoomManager
            .OnPlayerListChanged -=
            OnLobbyPlayerListChanged;

        boundLobbyRoomManager
            .OnSettingsChanged -=
            UpdateSettingsUI;

        boundLobbyRoomManager
            .OnLobbyNoticeChanged -=
            OnLobbyNoticeChanged;

        boundLobbyRoomManager = null;
    }

    private void OnLobbyPlayerListChanged()
    {
        UpdatePlayerListUI();

        if (LobbyRoomManager.Instance != null)
        {
            UpdateSettingsUI(
                LobbyRoomManager.Instance
                    .CurrentSettings
                    .Value
            );
        }
    }

    private void OnLobbyNoticeChanged(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        SetStatus(
            message,
            Color.red
        );
    }

    private void UpdatePlayerListUI()
    {
        if (LobbyRoomManager.Instance ==
                null ||
            playerListContainer == null ||
            playerEntryPrefab == null)
        {
            return;
        }

        for (int i =
                 playerListContainer.childCount - 1;
             i >= 0;
             i--)
        {
            GameObject child =
                playerListContainer
                    .GetChild(i)
                    .gameObject;

            child.SetActive(false);
            Destroy(child);
        }

        bool isLocalHost =
            NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsHost;

        for (int i = 0;
             i <
             LobbyRoomManager.Instance
                 .LobbyPlayers.Count;
             i++)
        {
            LobbyPlayer player =
                LobbyRoomManager.Instance
                    .LobbyPlayers[i];

            GameObject entry =
                Instantiate(
                    playerEntryPrefab,
                    playerListContainer
                );

            entry.SetActive(true);

            TextMeshProUGUI playerNameText =
                entry.transform
                    .Find("PlayerNameText")
                    ?.GetComponent
                        <TextMeshProUGUI>();

            GameObject hostTag =
                entry.transform
                    .Find("HostTag")
                    ?.gameObject;

            Button kickButton =
                entry.transform
                    .Find("KickButton")
                    ?.GetComponent<Button>();

            if (playerNameText != null)
            {
                playerNameText.text =
                    player.playerName
                        .ToString();
            }

            if (hostTag != null)
            {
                hostTag.SetActive(
                    player.isHost
                );
            }

            if (kickButton == null)
            {
                continue;
            }

            bool canKick =
                isLocalHost &&
                !player.isHost;

            kickButton.gameObject
                .SetActive(canKick);

            kickButton.onClick
                .RemoveAllListeners();

            if (!canKick)
            {
                continue;
            }

            ulong targetClientId =
                player.clientId;

            kickButton.onClick.AddListener(
                () =>
                {
                    if (LobbyRoomManager.Instance ==
                        null)
                    {
                        return;
                    }

                    LobbyRoomManager.Instance
                        .KickPlayerServerRpc(
                            targetClientId
                        );
                }
            );
        }
    }

    private void OnOpenSettingsClicked()
    {
        if (LobbyRoomManager.Instance ==
            null)
        {
            return;
        }

        settingsPanel.SetActive(true);

        UpdateSettingsUI(
            LobbyRoomManager.Instance
                .CurrentSettings
                .Value
        );
    }

    private void OnCloseSettingsClicked()
    {
        settingsPanel.SetActive(false);
    }

    private void OnOpenHelpClicked()
    {
        SetHelpPanelVisible(true);
    }

    private void OnCloseHelpClicked()
    {
        SetHelpPanelVisible(false);
    }

    private void OnHelpControlsTabClicked()
    {
        ShowHelpPage(HelpPage.Controls);
    }

    private void OnHelpRulesTabClicked()
    {
        ShowHelpPage(HelpPage.Rules);
    }

    private void OnHelpAudioTabClicked()
    {
        ShowHelpPage(HelpPage.Audio);
    }

    private void OnHelpGraphicsTabClicked()
    {
        ShowHelpPage(HelpPage.Graphics);
    }

    private void SetHelpPanelVisible(bool visible)
    {
        if (helpPanel == null)
            return;

        helpPanel.SetActive(visible);

        if (!visible)
            return;

        PrepareHelpPanelOverlay();

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);

        helpPanel.transform.SetAsLastSibling();
        ShowHelpPage(HelpPage.Controls);
    }

    private void PrepareHelpPanelOverlay()
    {
        if (helpPanel == null)
            return;

        Canvas helpCanvas =
            helpPanel.GetComponent<Canvas>();

        if (helpCanvas == null)
            helpCanvas = helpPanel.AddComponent<Canvas>();

        helpCanvas.overrideSorting = true;
        helpCanvas.sortingOrder = 1000;

        if (helpPanel.GetComponent<GraphicRaycaster>() == null)
            helpPanel.AddComponent<GraphicRaycaster>();
    }

    private void ShowHelpPage(HelpPage page)
    {
        bool showControls = page == HelpPage.Controls;
        bool showRules = page == HelpPage.Rules;
        bool showAudio = page == HelpPage.Audio;
        bool showGraphics = page == HelpPage.Graphics;

        if (helpControlsPage != null)
            helpControlsPage.SetActive(showControls);

        if (helpRulesPage != null)
        {
            helpRulesPage.SetActive(showRules);

            if (showRules &&
                helpRulesPage.TryGetComponent(
                    out ScrollRect rulesScrollRect))
            {
                rulesScrollRect.verticalNormalizedPosition = 1f;
            }
        }

        if (helpAudioPage != null)
            helpAudioPage.SetActive(showAudio);

        if (helpGraphicsPage != null)
            helpGraphicsPage.SetActive(showGraphics);

        if (helpControlsTabButton != null)
            helpControlsTabButton.interactable = !showControls;

        if (helpRulesTabButton != null)
            helpRulesTabButton.interactable = !showRules;

        if (helpAudioTabButton != null)
            helpAudioTabButton.interactable = !showAudio;

        if (helpGraphicsTabButton != null)
            helpGraphicsTabButton.interactable = !showGraphics;

        if (showAudio || showGraphics)
            RefreshPersonalSettingsUI();
    }

    private void CachePersonalSettingsUIReferences()
    {
        if (helpPanel == null)
            return;

        if (helpAudioTabButton == null)
        {
            Transform target = FindDescendantByName(
                helpPanel.transform,
                "HelpAudioTabButton"
            );
            if (target != null)
                helpAudioTabButton = target.GetComponent<Button>();
        }

        if (helpGraphicsTabButton == null)
        {
            Transform target = FindDescendantByName(
                helpPanel.transform,
                "HelpGraphicsTabButton"
            );
            if (target != null)
                helpGraphicsTabButton = target.GetComponent<Button>();
        }

        if (helpAudioPage == null)
        {
            Transform target = FindDescendantByName(
                helpPanel.transform,
                "HelpAudioPage"
            );
            if (target != null)
                helpAudioPage = target.gameObject;
        }

        if (helpGraphicsPage == null)
        {
            Transform target = FindDescendantByName(
                helpPanel.transform,
                "HelpGraphicsPage"
            );
            if (target != null)
                helpGraphicsPage = target.gameObject;
        }

        bool needsButtonCache =
            personalSettingButtons == null ||
            personalSettingButtons.Length <
                PersonalSettingButtonNames.Length;

        if (needsButtonCache)
        {
            personalSettingButtons = new Button[
                PersonalSettingButtonNames.Length
            ];
        }

        if (personalSettingValueTexts == null ||
            personalSettingValueTexts.Length <
                PersonalSettingButtonNames.Length)
        {
            personalSettingValueTexts =
                new TextMeshProUGUI[
                    PersonalSettingButtonNames.Length
                ];
        }

        for (int i = 0;
             i < PersonalSettingButtonNames.Length;
             i++)
        {
            if (personalSettingButtons[i] == null)
            {
                Transform target = FindDescendantByName(
                    helpPanel.transform,
                    PersonalSettingButtonNames[i]
                );

                if (target != null)
                {
                    personalSettingButtons[i] =
                        target.GetComponent<Button>();
                }
            }

            if (personalSettingValueTexts[i] == null &&
                personalSettingButtons[i] != null)
            {
                personalSettingValueTexts[i] =
                    personalSettingButtons[i]
                        .GetComponentInChildren
                            <TextMeshProUGUI>(true);
            }
        }
    }

    private static Transform FindDescendantByName(
        Transform root,
        string objectName)
    {
        if (root == null)
            return null;

        if (root.name == objectName)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform result = FindDescendantByName(
                root.GetChild(i),
                objectName
            );

            if (result != null)
                return result;
        }

        return null;
    }

    private void RegisterPersonalSettingEvents()
    {
        if (personalSettingButtons == null ||
            personalSettingButtons.Length < 10)
        {
            return;
        }

        if (personalSettingButtons[0] != null)
        {
            personalSettingButtons[0].onClick.AddListener(CycleMouseSensitivity);
            mouseSensitivityRightClickHandler =
                personalSettingButtons[0]
                    .GetComponent<RightClickHandler>();

            if (mouseSensitivityRightClickHandler == null)
            {
                mouseSensitivityRightClickHandler =
                    personalSettingButtons[0].gameObject
                        .AddComponent<RightClickHandler>();
            }

            mouseSensitivityRightClickHandler.Clicked -=
                DecreaseMouseSensitivity;
            mouseSensitivityRightClickHandler.Clicked +=
                DecreaseMouseSensitivity;
        }
        if (personalSettingButtons[1] != null)
            personalSettingButtons[1].onClick.AddListener(CycleMasterVolume);
        if (personalSettingButtons[2] != null)
            personalSettingButtons[2].onClick.AddListener(CycleVoiceVolume);
        if (personalSettingButtons[3] != null)
            personalSettingButtons[3].onClick.AddListener(TogglePersonalMicrophone);
        if (personalSettingButtons[4] != null)
            personalSettingButtons[4].onClick.AddListener(TogglePersonalVoiceOutput);
    }

    private void UnregisterPersonalSettingEvents()
    {
        if (personalSettingButtons == null ||
            personalSettingButtons.Length < 10)
        {
            return;
        }

        if (personalSettingButtons[0] != null)
            personalSettingButtons[0].onClick.RemoveListener(CycleMouseSensitivity);

        if (mouseSensitivityRightClickHandler != null)
        {
            mouseSensitivityRightClickHandler.Clicked -=
                DecreaseMouseSensitivity;
        }
        if (personalSettingButtons[1] != null)
            personalSettingButtons[1].onClick.RemoveListener(CycleMasterVolume);
        if (personalSettingButtons[2] != null)
            personalSettingButtons[2].onClick.RemoveListener(CycleVoiceVolume);
        if (personalSettingButtons[3] != null)
            personalSettingButtons[3].onClick.RemoveListener(TogglePersonalMicrophone);
        if (personalSettingButtons[4] != null)
            personalSettingButtons[4].onClick.RemoveListener(TogglePersonalVoiceOutput);
    }

    private void ApplySavedPersonalSettings()
    {
        AudioListener.volume = Mathf.Clamp01(
            PlayerPrefs.GetFloat(
                MasterVolumePreferenceKey,
                AudioListener.volume
            )
        );

        int qualityLevel = Mathf.Clamp(
            PlayerPrefs.GetInt(
                QualityLevelPreferenceKey,
                QualitySettings.GetQualityLevel()
            ),
            0,
            Mathf.Max(0, QualitySettings.names.Length - 1)
        );

        if (QualitySettings.names.Length > 0)
            QualitySettings.SetQualityLevel(qualityLevel, true);

        int width = PlayerPrefs.GetInt(
            ResolutionWidthPreferenceKey,
            Screen.width
        );
        int height = PlayerPrefs.GetInt(
            ResolutionHeightPreferenceKey,
            Screen.height
        );
        FullScreenMode mode = (FullScreenMode)Mathf.Clamp(
            PlayerPrefs.GetInt(
                FullScreenModePreferenceKey,
                (int)Screen.fullScreenMode
            ),
            (int)FullScreenMode.ExclusiveFullScreen,
            (int)FullScreenMode.Windowed
        );

        Screen.SetResolution(width, height, mode);
    }

    private void CycleMouseSensitivity()
    {
        ChangeMouseSensitivity(0.02f);
    }

    private void DecreaseMouseSensitivity()
    {
        ChangeMouseSensitivity(-0.02f);
    }

    private void ChangeMouseSensitivity(float amount)
    {
        float sensitivity = PlayerPrefs.GetFloat(
            SpiritFirstPersonCamera.MouseSensitivityPreferenceKey,
            0.12f
        );
        sensitivity += amount;

        if (sensitivity > 0.401f)
            sensitivity = 0.04f;
        else if (sensitivity < 0.039f)
            sensitivity = 0.40f;

        sensitivity = Mathf.Round(sensitivity * 100f) / 100f;

        PlayerPrefs.SetFloat(
            SpiritFirstPersonCamera.MouseSensitivityPreferenceKey,
            sensitivity
        );
        PlayerPrefs.Save();
        RefreshPersonalSettingsUI();
    }

    private void CycleMasterVolume()
    {
        float volume = AudioListener.volume + 0.1f;

        if (volume > 1.001f)
            volume = 0f;

        AudioListener.volume = Mathf.Clamp01(volume);
        PlayerPrefs.SetFloat(
            MasterVolumePreferenceKey,
            AudioListener.volume
        );
        PlayerPrefs.Save();
        RefreshPersonalSettingsUI();
    }

    private void CycleVoiceVolume()
    {
        VivoxVoiceManager voiceManager =
            VivoxVoiceManager.Instance;
        int volume = voiceManager != null
            ? voiceManager.OutputVolume
            : PlayerPrefs.GetInt(
                VivoxVoiceManager.VoiceVolumePreferenceKey,
                0
            );
        volume += 10;

        if (volume > 50)
            volume = -50;

        if (voiceManager != null)
        {
            voiceManager.SetOutputVolume(volume);
        }
        else
        {
            PlayerPrefs.SetInt(
                VivoxVoiceManager.VoiceVolumePreferenceKey,
                volume
            );
            PlayerPrefs.Save();
        }

        RefreshPersonalSettingsUI();
    }

    private void TogglePersonalMicrophone()
    {
        VivoxVoiceManager voiceManager =
            VivoxVoiceManager.Instance;
        bool enabled = !(voiceManager != null
            ? voiceManager.MicrophoneEnabled
            : PlayerPrefs.GetInt(
                VivoxVoiceManager.MicrophonePreferenceKey,
                1
            ) != 0);

        if (voiceManager != null)
            voiceManager.SetMicrophoneEnabled(enabled);
        else
            PlayerPrefs.SetInt(
                VivoxVoiceManager.MicrophonePreferenceKey,
                enabled ? 1 : 0
            );

        PlayerPrefs.Save();
        RefreshPersonalSettingsUI();
    }

    private void TogglePersonalVoiceOutput()
    {
        VivoxVoiceManager voiceManager =
            VivoxVoiceManager.Instance;
        bool enabled = !(voiceManager != null
            ? voiceManager.VoiceOutputEnabled
            : PlayerPrefs.GetInt(
                VivoxVoiceManager.VoiceOutputPreferenceKey,
                1
            ) != 0);

        if (voiceManager != null)
            voiceManager.SetVoiceOutputEnabled(enabled);
        else
            PlayerPrefs.SetInt(
                VivoxVoiceManager.VoiceOutputPreferenceKey,
                enabled ? 1 : 0
            );

        PlayerPrefs.Save();
        RefreshPersonalSettingsUI();
    }

    private void RefreshPersonalSettingsUI()
    {
        if (personalSettingValueTexts == null ||
            personalSettingValueTexts.Length < 10)
        {
            return;
        }

        VivoxVoiceManager voiceManager =
            VivoxVoiceManager.Instance;
        float sensitivity = PlayerPrefs.GetFloat(
            SpiritFirstPersonCamera.MouseSensitivityPreferenceKey,
            0.12f
        );
        int voiceVolume = voiceManager != null
            ? voiceManager.OutputVolume
            : PlayerPrefs.GetInt(
                VivoxVoiceManager.VoiceVolumePreferenceKey,
                0
            );
        bool microphoneEnabled = voiceManager != null
            ? voiceManager.MicrophoneEnabled
            : PlayerPrefs.GetInt(
                VivoxVoiceManager.MicrophonePreferenceKey,
                1
            ) != 0;
        bool voiceOutputEnabled = voiceManager != null
            ? voiceManager.VoiceOutputEnabled
            : PlayerPrefs.GetInt(
                VivoxVoiceManager.VoiceOutputPreferenceKey,
                1
            ) != 0;
        SetPersonalSettingText(0,
            $"마우스 감도  |  {sensitivity:0.00}");
        SetPersonalSettingText(1,
            "게임 음량  |  " +
            $"{Mathf.RoundToInt(AudioListener.volume * 100f)}%");
        SetPersonalSettingText(2,
            $"음성 채팅 음량  |  {voiceVolume + 50}%");
        SetPersonalSettingText(3,
            "마이크  |  " +
            (microphoneEnabled ? "켜짐" : "꺼짐"));
        SetPersonalSettingText(4,
            "음성 수신  |  " +
            (voiceOutputEnabled ? "켜짐" : "꺼짐"));
        helpPanel
            ?.GetComponentInChildren
                <PersonalGraphicsDropdownController>(true)
            ?.Refresh();
    }

    private void SetPersonalSettingText(int index, string value)
    {
        if (personalSettingValueTexts == null ||
            index < 0 ||
            index >= personalSettingValueTexts.Length ||
            personalSettingValueTexts[index] == null)
        {
            return;
        }

        personalSettingValueTexts[index].SetText(value);
    }

    private void SetSettingsInteractable(
        bool interactable)
    {
        mafiaSlider.interactable =
            interactable;

        citizenSlider.interactable =
            interactable;

        if (neutralSlider != null)
            neutralSlider.interactable = interactable;

        SetDurationInputsInteractable(
            interactable
        );

        if (allowDuplicateRolesToggle != null)
            allowDuplicateRolesToggle.interactable = interactable;

        if (revealExiledTeamToggle != null)
            revealExiledTeamToggle.interactable = interactable;

        if (requiredRoleToggles == null)
            return;

        for (int i = 0; i < requiredRoleToggles.Length; i++)
        {
            RequiredRoleToggleBinding binding =
                requiredRoleToggles[i];

            if (binding != null && binding.toggle != null)
                binding.toggle.interactable = interactable;
        }
    }

    private void SetDurationInputsInteractable(
        bool interactable)
    {
        SetInputFieldInteractable(
            morningVoteDurationInput,
            interactable
        );

        SetInputFieldInteractable(
            morningVoteResultDurationInput,
            interactable
        );

        SetInputFieldInteractable(
            nightPreparationDurationInput,
            interactable
        );

        SetInputFieldInteractable(
            nightActionDurationInput,
            interactable
        );

        SetInputFieldInteractable(
            nightResultDurationInput,
            interactable
        );
    }

    private void SetInputFieldInteractable(
        TMP_InputField inputField,
        bool interactable)
    {
        if (inputField != null)
            inputField.interactable = interactable;
    }

    private void OnMafiaSliderChanged(
        float value)
    {
        SendSettingsToServer(
            LobbyCountChangeSource.Mafia
        );
    }

    private void OnCitizenSliderChanged(
        float value)
    {
        SendSettingsToServer(
            LobbyCountChangeSource.Citizen
        );
    }

    private void OnNeutralSliderChanged(
        float value)
    {
        SendSettingsToServer(
            LobbyCountChangeSource.Neutral
        );
    }

    private void OnDurationInputEndEdit(
        string value)
    {
        SendSettingsToServer(
            LobbyCountChangeSource.Automatic
        );
    }

    private void OnSettingsToggleChanged(
        bool value)
    {
        SendSettingsToServer(
            LobbyCountChangeSource.Automatic
        );
    }

    private void SendSettingsToServer(
        LobbyCountChangeSource changeSource)
    {
        if (NetworkManager.Singleton ==
                null ||
            !NetworkManager.Singleton.IsHost ||
            LobbyRoomManager.Instance ==
                null)
        {
            return;
        }

        LobbySettings currentSettings =
            LobbyRoomManager.Instance
                .CurrentSettings
                .Value;

        LobbySettings updatedSettings =
            new LobbySettings
            {
                mafiaCount =
                    (int)mafiaSlider.value,

                citizenCount =
                    (int)citizenSlider.value,

                neutralCount =
                    neutralSlider != null
                        ? (int)neutralSlider.value
                        : currentSettings.neutralCount,

                requiredRoleMask = 0u,

                morningVoteSeconds =
                    ReadDurationInput(
                        morningVoteDurationInput,
                        currentSettings
                            .morningVoteSeconds,
                        30
                    ),

                morningVoteResultSeconds =
                    ReadDurationInput(
                        morningVoteResultDurationInput,
                        currentSettings
                            .morningVoteResultSeconds,
                        3
                    ),

                nightPreparationSeconds =
                    ReadDurationInput(
                        nightPreparationDurationInput,
                        currentSettings
                            .nightPreparationSeconds,
                        10
                    ),

                nightActionSeconds =
                    ReadDurationInput(
                        nightActionDurationInput,
                        currentSettings
                            .nightActionSeconds,
                        30
                    ),

                nightResultSeconds =
                    ReadDurationInput(
                        nightResultDurationInput,
                        currentSettings
                            .nightResultSeconds,
                        3
                    ),

                allowDuplicateRoles =
                    allowDuplicateRolesToggle != null
                        ? allowDuplicateRolesToggle.isOn
                        : currentSettings.allowDuplicateRoles,

                revealExiledTeam =
                    revealExiledTeamToggle != null
                        ? revealExiledTeamToggle.isOn
                        : currentSettings.revealExiledTeam
            };

        if (requiredRoleToggles != null)
        {
            for (int i = 0; i < requiredRoleToggles.Length; i++)
            {
                RequiredRoleToggleBinding binding =
                    requiredRoleToggles[i];

                if (binding == null ||
                    binding.toggle == null ||
                    !System.Enum.IsDefined(
                        typeof(RoleId),
                        binding.role) ||
                    !binding.toggle.isOn)
                {
                    continue;
                }

                updatedSettings.SetRoleRequired(
                    binding.role,
                    true
                );
            }
        }

        LobbyRoomManager.Instance
            .UpdateSettingsServerRpc(
                updatedSettings,
                changeSource
            );
    }

    private int ReadDurationInput(
        TMP_InputField inputField,
        int fallbackValue,
        int minimumSeconds)
    {
        if (inputField == null)
            return fallbackValue;

        if (!int.TryParse(
                inputField.text,
                out int parsedSeconds))
        {
            inputField.SetTextWithoutNotify(
                fallbackValue.ToString()
            );

            return fallbackValue;
        }

        int normalizedSeconds =
            Mathf.Max(
                minimumSeconds,
                parsedSeconds
            );

        inputField.SetTextWithoutNotify(
            normalizedSeconds.ToString()
        );

        return normalizedSeconds;
    }

    private void UpdateSettingsUI(
        LobbySettings settings)
    {
        int playerCount =
            LobbyRoomManager.Instance != null
                ? LobbyRoomManager.Instance
                    .LobbyPlayers.Count
                : settings.TotalPlayerCount;

        ConfigureCountSliderRanges(
            playerCount,
            settings
        );

        mafiaCountText.text =
            $"마녀: {settings.mafiaCount}";

        citizenCountText.text =
            $"주민: {settings.citizenCount}";

        if (neutralCountText != null)
        {
            neutralCountText.text =
                $"이방인: {settings.neutralCount}";
        }

        mafiaSlider.SetValueWithoutNotify(
            settings.mafiaCount
        );

        citizenSlider.SetValueWithoutNotify(
            settings.citizenCount
        );

        if (neutralSlider != null)
        {
            neutralSlider.SetValueWithoutNotify(
                settings.neutralCount
            );
        }

        UpdateDurationSettingsUI(
            settings
        );

        if (allowDuplicateRolesToggle != null)
        {
            allowDuplicateRolesToggle.SetIsOnWithoutNotify(
                settings.allowDuplicateRoles
            );
        }

        if (revealExiledTeamToggle != null)
        {
            revealExiledTeamToggle.SetIsOnWithoutNotify(
                settings.revealExiledTeam
            );
        }

        if (requiredRoleToggles == null)
            return;

        for (int i = 0; i < requiredRoleToggles.Length; i++)
        {
            RequiredRoleToggleBinding binding =
                requiredRoleToggles[i];

            if (binding == null || binding.toggle == null)
                continue;

            if (!System.Enum.IsDefined(
                    typeof(RoleId),
                    binding.role))
            {
                binding.toggle.gameObject.SetActive(false);
                continue;
            }

            binding.toggle.gameObject.SetActive(true);

            binding.toggle.SetIsOnWithoutNotify(
                settings.IsRoleRequired(binding.role)
            );
        }
    }

    private void UpdateDurationSettingsUI(
        LobbySettings settings)
    {
        SetDurationInputValue(
            morningVoteDurationInput,
            settings.morningVoteSeconds
        );

        SetDurationInputValue(
            morningVoteResultDurationInput,
            settings.morningVoteResultSeconds
        );

        SetDurationInputValue(
            nightPreparationDurationInput,
            settings.nightPreparationSeconds
        );

        SetDurationInputValue(
            nightActionDurationInput,
            settings.nightActionSeconds
        );

        SetDurationInputValue(
            nightResultDurationInput,
            settings.nightResultSeconds
        );
    }

    private void ConfigureDurationInputField(
        TMP_InputField inputField)
    {
        if (inputField == null)
            return;

        inputField.contentType =
            TMP_InputField.ContentType.IntegerNumber;

        inputField.lineType =
            TMP_InputField.LineType.SingleLine;
    }

    private void SetDurationInputValue(
        TMP_InputField inputField,
        int seconds)
    {
        if (inputField == null)
            return;

        ConfigureDurationInputField(
            inputField
        );

        inputField.SetTextWithoutNotify(
            seconds.ToString()
        );
    }

    private void ConfigureCountSliderRanges(
        int playerCount,
        LobbySettings settings)
    {
        playerCount =
            Mathf.Max(
                0,
                playerCount
            );

        mafiaSlider.wholeNumbers = true;
        citizenSlider.wholeNumbers = true;

        if (neutralSlider != null)
            neutralSlider.wholeNumbers = true;

        if (playerCount <= 2)
        {
            mafiaSlider.minValue = 0f;
            mafiaSlider.maxValue = 0f;

            citizenSlider.minValue =
                playerCount;

            citizenSlider.maxValue =
                playerCount;

            if (neutralSlider != null)
            {
                neutralSlider.minValue = 0f;
                neutralSlider.maxValue = 0f;
            }

            return;
        }

        int maximumNeutralCount =
            playerCount - 3;

        if (!settings.allowDuplicateRoles)
        {
            maximumNeutralCount =
                Mathf.Min(
                    maximumNeutralCount,
                    3
                );
        }

        int neutralCount =
            Mathf.Clamp(
                settings.neutralCount,
                0,
                maximumNeutralCount
            );

        int nonNeutralCount =
            playerCount -
            neutralCount;

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
                    4
                );
        }

        int minimumCitizenCount =
            nonNeutralCount -
            maximumMafiaCount;

        int maximumCitizenCount =
            nonNeutralCount - 1;

        mafiaSlider.minValue = 1f;
        mafiaSlider.maxValue =
            maximumMafiaCount;

        citizenSlider.minValue =
            minimumCitizenCount;

        citizenSlider.maxValue =
            maximumCitizenCount;

        if (neutralSlider != null)
        {
            neutralSlider.minValue = 0f;
            neutralSlider.maxValue =
                Mathf.Max(
                    0,
                    maximumNeutralCount
                );
        }
    }

    private void OnLeaveRoomClicked()
    {
        if (!TryRefreshRelayConnectionManager())
        {
            SetStatus(
                "네트워크 연결 관리자를 찾지 못했습니다.",
                Color.red
            );

            return;
        }

        if (relayConnectionManager.IsBusy)
            return;

        leavingRoom = true;

        UnbindLobbyEvents();

        SetConnectionControlsInteractable(
            false
        );

        relayConnectionManager.LeaveSession();

        currentInviteCode =
            string.Empty;
        persistentInviteCode =
            string.Empty;

        ShowLoginScreen();

        SetStatus(
            "네트워크 연결을 종료하는 중입니다.",
            Color.yellow
        );
    }

    private void OnStartGameClicked()
    {
        if (NetworkManager.Singleton ==
                null ||
            !NetworkManager.Singleton.IsHost ||
            LobbyRoomManager.Instance ==
                null)
        {
            return;
        }

        if (!LobbyRoomManager.Instance.TryValidateGameStart(
                out string validationMessage))
        {
            SetStatus(
                validationMessage,
                Color.red
            );

            return;
        }

        LobbyRoomManager.Instance
            .StartGameServerRpc();
    }

    private void OnExitGameClicked()
    {
        if (TryRefreshRelayConnectionManager())
            relayConnectionManager.LeaveSession();

#if UNITY_EDITOR
        UnityEditor.EditorApplication
            .isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private bool TryRefreshRelayConnectionManager()
    {
        RelayConnectionManager activeManager =
            RelayConnectionManager.Instance;

        if (activeManager != null &&
            relayConnectionManager != activeManager)
        {
            relayConnectionManager = activeManager;
        }

        return relayConnectionManager != null;
    }

    private void OnRelayStatusChanged(
        string message,
        bool isError)
    {
        SetStatus(
            message,
            isError
                ? Color.red
                : Color.yellow
        );
    }

    private void OnRelayBusyChanged(
        bool busy)
    {
        SetConnectionControlsInteractable(
            !busy
        );

        if (busy || !leavingRoom)
            return;

        leavingRoom = false;

        SetStatus(
            "로비에서 나왔습니다.",
            Color.yellow
        );
    }

    private void OnUnexpectedlyDisconnected()
    {
        if (leavingRoom)
        {
            return;
        }

        UnbindLobbyEvents();

        relayConnectionManager
            .TryConsumePendingDisconnectMessage(
                out _
            );

        currentInviteCode =
            string.Empty;
        persistentInviteCode =
            string.Empty;

        ShowLoginScreen();

        SetStatus(
            "호스트와의 연결이 종료됐습니다.",
            Color.red
        );
    }

    private void SetConnectionControlsInteractable(
        bool interactable)
    {
        nameInputField.interactable =
            interactable;

        inviteCodeInputField.interactable =
            interactable;

        createRoomButton.interactable =
            interactable;

        joinRoomButton.interactable =
            interactable;
    }

    private void SetStatus(
        string message,
        Color color)
    {
        if (statusText == null)
        {
            return;
        }

        statusText.text =
            message;

        statusText.color =
            color;
    }

    private string NormalizePlayerName(
        string playerName)
    {
        if (string.IsNullOrWhiteSpace(
                playerName))
        {
            return string.Empty;
        }

        playerName =
            playerName.Trim();

        if (playerName.Length >
            MaximumPlayerNameLength)
        {
            playerName =
                playerName.Substring(
                    0,
                    MaximumPlayerNameLength
                );
        }

        return playerName;
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
