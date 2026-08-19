using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace MafiaGame.UI
{
    public class MafiaUIController : MonoBehaviour
    {
        public static MafiaUIController Instance { get; private set; }
        public static bool IsTextChatInputFocused =>
            Instance != null &&
            Instance.IsAnyTextChatInputFocused();
        public static bool IsHelpPanelOpen =>
            Instance != null &&
            Instance.helpPanel != null &&
            Instance.helpPanel.activeInHierarchy;
        public static bool IsMatchStatusBoardOpen =>
            Instance != null &&
            Instance.matchStatusBoardRoot != null &&
            Instance.matchStatusBoardRoot.activeInHierarchy;

        private sealed class ChatPanelReferences
        {
            public GameObject root;
            public TMP_InputField inputField;
            public Button sendButton;
            public ScrollRect scrollRect;
            public RectTransform content;
            public GameObject messageTemplate;
            public TextMeshProUGUI titleText;
            public readonly List<GameObject> messages =
                new List<GameObject>();
        }

        private sealed class StatusPlayerRecord
        {
            public bool isAlive;
            public bool wasExiled;
            public int deathDay;
        }

        private sealed class PersonalActionLogEntry
        {
            public MatchManager.PersonalActionRecordData data;
            public string detail;
        }

        private enum VoteMode
        {
            None,
            MorningExile,
            MafiaKiller,
            MafiaDisguise,
            DrunkardSleep
        }

        private enum HelpPage
        {
            Controls,
            Rules,
            Audio,
            Graphics
        }

        [Header("Tabs")]
        [SerializeField] private Button chatTabButton;
        [SerializeField] private Button voteTabButton;
        [SerializeField] private Image chatTabHighlight;
        [SerializeField] private Image voteTabHighlight;

        [Header("Panels")]
        [SerializeField] private GameObject chatPanel;
        [SerializeField] private GameObject votePanel;

        [Header("Text Chat")]
        [SerializeField] private GameObject nightChatPanel;
        [SerializeField] private GameObject chatMessagePrefab;

        [Min(1)]
        [SerializeField] private int maximumVisibleChatMessages = 80;

        [Header("Phase Visibility")]
        [SerializeField] private GameObject morningBackground;

        [Header("Night Duty")]
        [SerializeField] private GameObject nightDutyPanel;
        [SerializeField] private TextMeshProUGUI nightDutyHeaderText;
        [SerializeField] private GameObject firstNightDutyRow;
        [SerializeField] private TextMeshProUGUI firstNightDutyStatusIcon;
        [SerializeField] private TextMeshProUGUI firstNightDutyText;
        [SerializeField] private GameObject secondNightDutyRow;
        [SerializeField] private TextMeshProUGUI secondNightDutyStatusIcon;
        [SerializeField] private TextMeshProUGUI secondNightDutyText;

        [Header("Vote Header")]
        [SerializeField] private TextMeshProUGUI voteTitleText;
        [SerializeField] private TextMeshProUGUI voteSubtitleText;

        [Header("Role Description")]
        [SerializeField] private TextMeshProUGUI roleNameText;
        [SerializeField] private TextMeshProUGUI roleDescriptionText;

        [Header("Header Status")]
        [SerializeField] private TextMeshProUGUI roleText;
        [SerializeField] private TextMeshProUGUI aliveText;
        [SerializeField] private TextMeshProUGUI dayText;

        [Header("Local Spirit Health")]
        [SerializeField] private GameObject spiritHealthPanel;
        [SerializeField] private Image[] spiritHealthIcons;
        [SerializeField] private Sprite spiritHealthFilledSprite;
        [SerializeField] private Sprite spiritHealthEmptySprite;
        [SerializeField] private Color spiritHealthFilledColor = Color.white;
        [SerializeField] private Color spiritHealthEmptyColor =
            Color.white;

        [Header("Local Spirit Status")]
        [SerializeField] private GameObject spiritStatusPanel;
        [SerializeField] private TextMeshProUGUI spiritStatusText;

        [Header("Timers")]
        [SerializeField] private TextMeshProUGUI topMeetingTimerText;
        [SerializeField] private TextMeshProUGUI voteTimerText;

        [Header("Private Role Action Result")]
        [SerializeField] private GameObject privateActionResultRoot;
        [SerializeField] private TextMeshProUGUI privateActionResultText;

        [Min(0.1f)]
        [SerializeField] private float privateActionResultDuration = 5f;

        [Header("Global Notifications")]
        [SerializeField] private GameObject globalNotificationRoot;
        [SerializeField] private TextMeshProUGUI globalNotificationText;

        [Min(0.1f)]
        [SerializeField] private float globalNotificationDuration = 4f;

        [Header("Results")]
        [SerializeField] private TextMeshProUGUI phaseResultText;
        [SerializeField] private Button returnToLobbyButton;

        [Header("Match Transition")]
        [SerializeField] private GameObject matchTransitionOverlay;
        [SerializeField] private TextMeshProUGUI matchTransitionText;
        [SerializeField] private Button matchTransitionRetryButton;
        [SerializeField] private Button matchTransitionCancelButton;

        [Header("Network Status")]
        [SerializeField] private GameObject networkStatusPanel;
        [SerializeField] private TextMeshProUGUI networkStatusText;

        [Header("Help")]
        [SerializeField] private GameObject helpPanel;
        [SerializeField] private Button helpControlsTabButton;
        [SerializeField] private Button helpRulesTabButton;
        [SerializeField] private Button closeHelpButton;
        [SerializeField] private Button leaveMatchButton;
        [SerializeField] private GameObject helpControlsPage;
        [SerializeField] private GameObject helpRulesPage;

        [SerializeField] private Button helpAudioTabButton;
        [SerializeField] private Button helpGraphicsTabButton;
        [SerializeField] private GameObject helpAudioPage;
        [SerializeField] private GameObject helpGraphicsPage;
        [SerializeField] private List<Button> personalSettingButtons =
            new List<Button>();
        [SerializeField] private List<TextMeshProUGUI> personalSettingValueTexts =
            new List<TextMeshProUGUI>();
        [SerializeField] private TextMeshProUGUI voiceConnectionStatusText;
        [SerializeField] private Button[] voiceParticipantButtons;
        [SerializeField] private TextMeshProUGUI[] voiceParticipantRowTexts;

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

        private const float SlowRoleAssignmentNoticeDelay = 8f;
        private const float LobbyReturnTimeout = 20f;

        private static readonly int[] FrameRateChoices =
        {
            30,
            60,
            120,
            240,
            -1
        };

        private GameObject leaveMatchConfirmationPanel;
        private Button confirmLeaveMatchButton;
        private Button cancelLeaveMatchButton;

        private GameObject gameResultRosterPanel;
        private ScrollRect gameResultRosterScrollRect;
        private TextMeshProUGUI gameResultRosterText;
        private Vector2 defaultPhaseResultPosition;
        private Vector2 defaultPhaseResultSize;
        private bool hasDefaultPhaseResultLayout;

        private GameObject matchStatusBoardRoot;
        private TextMeshProUGUI matchStatusSummaryText;
        private TextMeshProUGUI matchStatusRosterText;
        private TextMeshProUGUI matchStatusPublicLogText;
        private TextMeshProUGUI matchStatusPersonalLogText;
        private ScrollRect matchStatusPublicScrollRect;
        private ScrollRect matchStatusPersonalScrollRect;
        private bool matchStatusBoardRestoreCursorLock;

        private readonly Dictionary<ulong, StatusPlayerRecord>
            matchStatusPlayerRecords =
                new Dictionary<ulong, StatusPlayerRecord>();
        private readonly List<string> matchStatusPublicLogs =
            new List<string>();
        private readonly List<PersonalActionLogEntry>
            matchStatusPersonalLogs =
                new List<PersonalActionLogEntry>();
        private readonly Dictionary<RoleActionType, string>
            pendingPersonalActionDetails =
                new Dictionary<RoleActionType, string>();
        private int lastLoggedMorningVoteDay = -1;
        private int lastLoggedNightResultDay = -1;

        [Header("Result Animation")]
        [Min(0.01f)]
        [SerializeField] private float phaseResultAppearDuration = 0.3f;

        [Range(0.1f, 1f)]
        [SerializeField] private float phaseResultStartScale = 0.8f;

        [Range(1f, 1.2f)]
        [SerializeField] private float phaseResultOvershootScale = 1.05f;

        [Header("Audio Toggles")]
        [SerializeField] private VivoxVoiceManager vivoxVoiceManager;
        [SerializeField] private Button micButton;
        [SerializeField] private Image micIconImage;
        [SerializeField] private Button voiceButton;
        [SerializeField] private Image voiceIconImage;

        [Header("Audio Sprites")]
        [SerializeField] private Sprite micOnSprite;
        [SerializeField] private Sprite micOffSprite;
        [SerializeField] private Sprite voiceOnSprite;
        [SerializeField] private Sprite voiceOffSprite;

        [Header("Vote Configuration")]
        [SerializeField] private Button confirmVoteButton;
        [SerializeField] private Button abstainButton;
        [SerializeField] private Toggle[] suspectToggles;

        [Header("Interaction Feedback")]
        [Tooltip("상호작용 가능한 전등 또는 떨어진 도구를 조준했을 때 표시할 공용 화면 중앙 점입니다.")]
        [FormerlySerializedAs("lampAimIndicator")]
        [SerializeField] private GameObject interactionAimIndicator;
        [SerializeField] private TextMeshProUGUI interactionPromptText;

        private MatchManager matchManager;
        private SpiritHitReceiver localSpiritHitReceiver;

        private ChatPanelReferences morningChat;
        private ChatPanelReferences nightChat;
        private bool nightChatInputSelected;
        private readonly Dictionary<Behaviour, bool>
            nightChatBlockedBehaviourStates =
                new Dictionary<Behaviour, bool>();
        private readonly Dictionary<Behaviour, bool>
            helpBlockedBehaviourStates =
                new Dictionary<Behaviour, bool>();

        private GameObject tabSystemRoot;
        private GameObject contentAreaRoot;

        private static readonly Color CitizenRoleNameColor =
            new Color32(107, 255, 244, 255);
        private static readonly Color MafiaRoleNameColor =
            new Color32(220, 76, 76, 255);
        private static readonly Color NeutralRoleNameColor =
            new Color32(211, 220, 76, 255);
        private static readonly Color UnassignedRoleNameColor =
            new Color32(255, 204, 51, 255);

        private ulong[] suspectClientIds;
        private int[] suspectOptionValues;
        private GameObject[] suspectRows;
        private PlayerIdentityLabel[] suspectIdentityLabels;
        private TextMeshProUGUI[] suspectNameTexts;
        private CanvasGroup[] suspectRowCanvasGroups;
        private TextMeshProUGUI abstainButtonText;
        private TextMeshProUGUI confirmVoteButtonText;
        private TextMeshProUGUI chatTabButtonText;
        private TextMeshProUGUI voteTabButtonText;
        private string defaultChatTabLabel = string.Empty;
        private string defaultVoteTabLabel = string.Empty;
        private bool hasUnreadMorningChatMessages;
        private UnityEngine.Events.UnityAction<bool>[] suspectToggleListeners;

        private VoteMode currentVoteMode = VoteMode.None;

        private bool morningAbstainSelected;
        private bool morningVoteLocked;
        private ulong localMafiaKillerVoteTargetClientId = ulong.MaxValue;

        private bool lampAimIndicatorRequested;
        private bool droppedToolAimIndicatorRequested;
        private string droppedToolInteractionPrompt = string.Empty;

        private bool isMicOn = true;
        private bool isVoiceOn = true;
        private bool isLeavingMatch;
        private bool lobbyReturnRequestFailed;
        private bool lobbyReturnRequestDelayed;
        private bool lobbyReturnFromGameResult;
        private float roleAssignmentWaitStartedRealtime;
        private float lobbyReturnRequestStartedRealtime;

        private Coroutine phaseResultAnimationCoroutine;
        private Coroutine privateActionResultCoroutine;
        private Coroutine globalNotificationCoroutine;
        private Coroutine bindVivoxVoiceManagerCoroutine;

        public void SetupFields(
            Button chatTabButton, Button voteTabButton, Image chatTabHighlight, Image voteTabHighlight,
            GameObject chatPanel, GameObject votePanel,
            TextMeshProUGUI topMeetingTimerText, TextMeshProUGUI voteTimerText,
            Button micButton, Image micIconImage, Button voiceButton, Image voiceIconImage,
            Sprite micOnSprite, Sprite micOffSprite, Sprite voiceOnSprite, Sprite voiceOffSprite,
            Button confirmVoteButton, Button abstainButton, Toggle[] suspectToggles)
        {
            this.chatTabButton = chatTabButton;
            this.voteTabButton = voteTabButton;
            this.chatTabHighlight = chatTabHighlight;
            this.voteTabHighlight = voteTabHighlight;
            this.chatPanel = chatPanel;
            this.votePanel = votePanel;
            this.topMeetingTimerText = topMeetingTimerText;
            this.voteTimerText = voteTimerText;
            this.micButton = micButton;
            this.micIconImage = micIconImage;
            this.voiceButton = voiceButton;
            this.voiceIconImage = voiceIconImage;
            this.micOnSprite = micOnSprite;
            this.micOffSprite = micOffSprite;
            this.voiceOnSprite = voiceOnSprite;
            this.voiceOffSprite = voiceOffSprite;
            this.confirmVoteButton = confirmVoteButton;
            this.abstainButton = abstainButton;
            this.suspectToggles = suspectToggles;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            CacheUIRoots();
            CacheTabButtonTexts();
            CacheVoteTexts();
            InitializeVoteRows();
            InitializeTextChatUI();
            PrepareHelpPanelOverlay();
            EnsureLeaveMatchButton();
            EnsureLeaveMatchConfirmation();
            EnsurePersonalSettingsUI();
            EnsureGameResultRosterUI();
            EnsureMatchStatusBoardUI();
            CacheDefaultPhaseResultLayout();
            RegisterButtonEvents();
            SetHelpPanelVisible(false);

            if (morningBackground != null)
                morningBackground.SetActive(false);

            SetMeetingUIVisible(false);
            SetResultText(string.Empty);
            ApplyInputMode(MatchPhase.None);
            UpdateAudioUI();
            UpdateHeaderUI();
            TryBindLocalSpiritHitReceiver();
            RefreshSpiritHealthUI();
            UpdatePhaseTimerUI();
            RefreshNightDutyUI();
            HideGlobalNotification();
            ResetInteractionAimIndicator();
            SetReturnToLobbyButtonVisible(false);
            SetGameResultRosterVisible(false);
            roleAssignmentWaitStartedRealtime = Time.realtimeSinceStartup;
            RefreshMatchTransitionOverlay();
            RefreshNetworkStatusUI();
            TryBindVivoxVoiceManager();

            if (vivoxVoiceManager == null)
            {
                bindVivoxVoiceManagerCoroutine =
                    StartCoroutine(BindVivoxVoiceManagerRoutine());
            }

            StartCoroutine(BindMatchManagerRoutine());
        }

        private void Update()
        {
            UpdatePhaseTimerUI();
            RefreshMatchTransitionOverlay();
            RefreshNetworkStatusUI();
            UpdateHelpInput();
            UpdateMatchStatusBoardInput();

            if (IsHelpPanelOpen)
            {
                SetHelpGameplayInputBlocked(true);
                return;
            }

            UpdateTextChatInput();
            TryBindLocalSpiritHitReceiver();
            RefreshSpiritHealthUI();
        }

        private void OnDestroy()
        {
            RestoreHelpBlockedBehaviours();
            SetNightChatInputActive(false);
            ResetInteractionAimIndicator();

            if (Instance == this)
                Instance = null;
            if (phaseResultAnimationCoroutine != null)
            {
                StopCoroutine(phaseResultAnimationCoroutine);
                phaseResultAnimationCoroutine = null;
            }

            if (privateActionResultCoroutine != null)
            {
                StopCoroutine(privateActionResultCoroutine);
                privateActionResultCoroutine = null;
            }

            if (globalNotificationCoroutine != null)
            {
                StopCoroutine(globalNotificationCoroutine);
                globalNotificationCoroutine = null;
            }

            if (bindVivoxVoiceManagerCoroutine != null)
            {
                StopCoroutine(bindVivoxVoiceManagerCoroutine);
                bindVivoxVoiceManagerCoroutine = null;
            }

            UnbindVivoxVoiceManager();
            UnbindLocalSpiritHitReceiver();
            UnregisterButtonEvents();
            UnregisterSuspectToggleEvents();
            UnbindMatchManager();
        }

        private IEnumerator BindVivoxVoiceManagerRoutine()
        {
            while (vivoxVoiceManager == null)
            {
                TryBindVivoxVoiceManager();

                if (vivoxVoiceManager != null)
                    break;

                yield return null;
            }

            bindVivoxVoiceManagerCoroutine = null;
        }

        private void TryBindVivoxVoiceManager()
        {
            if (vivoxVoiceManager == null)
            {
                vivoxVoiceManager =
                    GetComponent<VivoxVoiceManager>();
            }

            if (vivoxVoiceManager == null)
            {
                vivoxVoiceManager =
                    FindFirstObjectByType<VivoxVoiceManager>();
            }

            if (vivoxVoiceManager == null)
                return;

            vivoxVoiceManager.AudioStateChanged -=
                OnVivoxAudioStateChanged;

            vivoxVoiceManager.AudioStateChanged +=
                OnVivoxAudioStateChanged;
            vivoxVoiceManager.ReadyChanged -=
                OnVivoxReadyChanged;
            vivoxVoiceManager.ReadyChanged +=
                OnVivoxReadyChanged;
            vivoxVoiceManager.ParticipantRosterChanged -=
                OnVivoxParticipantRosterChanged;
            vivoxVoiceManager.ParticipantRosterChanged +=
                OnVivoxParticipantRosterChanged;

            SyncAudioStateFromVivox();
        }

        private void UnbindVivoxVoiceManager()
        {
            if (vivoxVoiceManager == null)
                return;

            vivoxVoiceManager.AudioStateChanged -=
                OnVivoxAudioStateChanged;
            vivoxVoiceManager.ReadyChanged -=
                OnVivoxReadyChanged;
            vivoxVoiceManager.ParticipantRosterChanged -=
                OnVivoxParticipantRosterChanged;
        }

        private void OnVivoxAudioStateChanged()
        {
            SyncAudioStateFromVivox();
            RefreshPersonalSettingsUI();
        }

        private void OnVivoxReadyChanged(bool ready)
        {
            RefreshPersonalSettingsUI();
            RefreshVoiceParticipantRows();
        }

        private void OnVivoxParticipantRosterChanged()
        {
            RefreshVoiceParticipantRows();
        }

        private void SyncAudioStateFromVivox()
        {
            if (vivoxVoiceManager == null)
                return;

            isMicOn =
                vivoxVoiceManager.MicrophoneEnabled;

            isVoiceOn =
                vivoxVoiceManager.VoiceOutputEnabled;

            UpdateAudioUI();
        }

        private void CacheUIRoots()
        {
            if (chatTabButton != null && chatTabButton.transform.parent != null)
                tabSystemRoot = chatTabButton.transform.parent.gameObject;

            if (chatPanel != null && chatPanel.transform.parent != null)
                contentAreaRoot = chatPanel.transform.parent.gameObject;
        }

        private void CacheTabButtonTexts()
        {
            if (chatTabButton != null)
            {
                chatTabButtonText =
                    chatTabButton.GetComponentInChildren
                        <TextMeshProUGUI>(true);

                if (chatTabButtonText != null)
                {
                    defaultChatTabLabel =
                        chatTabButtonText.text;
                }
            }

            if (voteTabButton != null)
            {
                voteTabButtonText =
                    voteTabButton.GetComponentInChildren
                        <TextMeshProUGUI>(true);

                if (voteTabButtonText != null)
                {
                    defaultVoteTabLabel =
                        voteTabButtonText.text;
                }
            }
        }

        private void RestoreDefaultTabButtonLabels()
        {
            RefreshMorningChatUnreadIndicator();

            if (voteTabButtonText != null)
                voteTabButtonText.text = defaultVoteTabLabel;
        }

        private void RefreshMorningChatUnreadIndicator()
        {
            if (chatTabButtonText == null)
                return;

            chatTabButtonText.text =
                hasUnreadMorningChatMessages
                    ? $"{defaultChatTabLabel} <color=#F2D173>●</color>"
                    : defaultChatTabLabel;
        }

        private void SetMafiaPreparationTabLabels()
        {
            if (chatTabButtonText != null)
                chatTabButtonText.text = "살해 담당";

            if (voteTabButtonText != null)
                voteTabButtonText.text = "위장 도구";
        }

        private void CacheVoteTexts()
        {
            if (abstainButton != null)
            {
                abstainButtonText =
                    abstainButton.GetComponentInChildren<TextMeshProUGUI>(true);
            }

            if (confirmVoteButton != null)
            {
                confirmVoteButtonText =
                    confirmVoteButton.GetComponentInChildren
                        <TextMeshProUGUI>(true);
            }

            if (votePanel == null ||
                voteTitleText != null && voteSubtitleText != null)
            {
                return;
            }

            TextMeshProUGUI[] texts =
                votePanel.GetComponentsInChildren<TextMeshProUGUI>(true);

            for (int i = 0; i < texts.Length; i++)
            {
                string objectName = texts[i].gameObject.name;

                if (voteSubtitleText == null &&
                    objectName.IndexOf(
                        "Subtitle",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    voteSubtitleText = texts[i];
                    continue;
                }

                if (voteTitleText == null &&
                    objectName.IndexOf(
                        "Title",
                        StringComparison.OrdinalIgnoreCase) >= 0 &&
                    objectName.IndexOf(
                        "Subtitle",
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    voteTitleText = texts[i];
                }
            }
        }

        private void RegisterButtonEvents()
        {
            if (chatTabButton != null)
                chatTabButton.onClick.AddListener(OnChatTabClicked);

            if (voteTabButton != null)
                voteTabButton.onClick.AddListener(OnVoteTabClicked);

            if (micButton != null)
                micButton.onClick.AddListener(ToggleMic);

            if (voiceButton != null)
                voiceButton.onClick.AddListener(ToggleVoice);

            if (confirmVoteButton != null)
            {
                confirmVoteButton.onClick.AddListener(
                    OnConfirmMorningVote
                );
            }

            if (abstainButton != null)
                abstainButton.onClick.AddListener(OnAbstainVote);

            if (returnToLobbyButton != null)
                returnToLobbyButton.onClick.AddListener(
                    OnReturnToLobbyClicked
                );

            if (matchTransitionRetryButton != null)
            {
                matchTransitionRetryButton.onClick.AddListener(
                    OnMatchTransitionRetryClicked
                );
            }

            if (matchTransitionCancelButton != null)
            {
                matchTransitionCancelButton.onClick.AddListener(
                    OnMatchTransitionCancelClicked
                );
            }

            if (helpControlsTabButton != null)
                helpControlsTabButton.onClick.AddListener(OnHelpControlsTabClicked);

            if (helpRulesTabButton != null)
                helpRulesTabButton.onClick.AddListener(OnHelpRulesTabClicked);

            if (helpAudioTabButton != null)
            {
                helpAudioTabButton.onClick.AddListener(
                    OnHelpAudioTabClicked
                );
            }

            if (helpGraphicsTabButton != null)
            {
                helpGraphicsTabButton.onClick.AddListener(
                    OnHelpGraphicsTabClicked
                );
            }

            if (closeHelpButton != null)
                closeHelpButton.onClick.AddListener(OnCloseHelpClicked);

            if (leaveMatchButton != null)
                leaveMatchButton.onClick.AddListener(OnLeaveMatchClicked);

            if (confirmLeaveMatchButton != null)
            {
                confirmLeaveMatchButton.onClick.AddListener(
                    OnConfirmLeaveMatchClicked
                );
            }

            if (cancelLeaveMatchButton != null)
            {
                cancelLeaveMatchButton.onClick.AddListener(
                    OnCancelLeaveMatchClicked
                );
            }

            RegisterPersonalSettingEvents();
            RegisterTextChatEvents(morningChat, false);
            RegisterTextChatEvents(nightChat, true);
        }

        private void UnregisterButtonEvents()
        {
            if (chatTabButton != null)
                chatTabButton.onClick.RemoveListener(OnChatTabClicked);

            if (voteTabButton != null)
                voteTabButton.onClick.RemoveListener(OnVoteTabClicked);

            if (micButton != null)
                micButton.onClick.RemoveListener(ToggleMic);

            if (voiceButton != null)
                voiceButton.onClick.RemoveListener(ToggleVoice);

            if (confirmVoteButton != null)
            {
                confirmVoteButton.onClick.RemoveListener(
                    OnConfirmMorningVote
                );
            }

            if (abstainButton != null)
                abstainButton.onClick.RemoveListener(OnAbstainVote);

            if (returnToLobbyButton != null)
                returnToLobbyButton.onClick.RemoveListener(
                    OnReturnToLobbyClicked
                );

            if (matchTransitionRetryButton != null)
            {
                matchTransitionRetryButton.onClick.RemoveListener(
                    OnMatchTransitionRetryClicked
                );
            }

            if (matchTransitionCancelButton != null)
            {
                matchTransitionCancelButton.onClick.RemoveListener(
                    OnMatchTransitionCancelClicked
                );
            }

            if (helpControlsTabButton != null)
                helpControlsTabButton.onClick.RemoveListener(OnHelpControlsTabClicked);

            if (helpRulesTabButton != null)
                helpRulesTabButton.onClick.RemoveListener(OnHelpRulesTabClicked);

            if (helpAudioTabButton != null)
            {
                helpAudioTabButton.onClick.RemoveListener(
                    OnHelpAudioTabClicked
                );
            }

            if (helpGraphicsTabButton != null)
            {
                helpGraphicsTabButton.onClick.RemoveListener(
                    OnHelpGraphicsTabClicked
                );
            }

            if (closeHelpButton != null)
                closeHelpButton.onClick.RemoveListener(OnCloseHelpClicked);

            if (leaveMatchButton != null)
                leaveMatchButton.onClick.RemoveListener(OnLeaveMatchClicked);

            if (confirmLeaveMatchButton != null)
            {
                confirmLeaveMatchButton.onClick.RemoveListener(
                    OnConfirmLeaveMatchClicked
                );
            }

            if (cancelLeaveMatchButton != null)
            {
                cancelLeaveMatchButton.onClick.RemoveListener(
                    OnCancelLeaveMatchClicked
                );
            }

            UnregisterPersonalSettingEvents();
            UnregisterTextChatEvents(morningChat, false);
            UnregisterTextChatEvents(nightChat, true);
        }

        private void RegisterPersonalSettingEvents()
        {
            if (personalSettingButtons == null ||
                personalSettingButtons.Count < 10)
            {
                return;
            }

            personalSettingButtons[0].onClick.AddListener(
                CycleMouseSensitivity);
            personalSettingButtons[1].onClick.AddListener(
                CycleMasterVolume);
            personalSettingButtons[2].onClick.AddListener(
                CycleVoiceVolume);
            personalSettingButtons[3].onClick.AddListener(
                TogglePersonalMicrophone);
            personalSettingButtons[4].onClick.AddListener(
                TogglePersonalVoiceOutput);
            personalSettingButtons[5].onClick.AddListener(
                CycleFullScreenMode);
            personalSettingButtons[6].onClick.AddListener(
                CycleResolution);
            personalSettingButtons[7].onClick.AddListener(
                CycleQualityLevel);
            personalSettingButtons[8].onClick.AddListener(
                CycleTargetFrameRate);
            personalSettingButtons[9].onClick.AddListener(
                CycleScreenEffectIntensity);
        }

        private void UnregisterPersonalSettingEvents()
        {
            if (personalSettingButtons == null ||
                personalSettingButtons.Count < 10)
            {
                return;
            }

            personalSettingButtons[0].onClick.RemoveListener(
                CycleMouseSensitivity);
            personalSettingButtons[1].onClick.RemoveListener(
                CycleMasterVolume);
            personalSettingButtons[2].onClick.RemoveListener(
                CycleVoiceVolume);
            personalSettingButtons[3].onClick.RemoveListener(
                TogglePersonalMicrophone);
            personalSettingButtons[4].onClick.RemoveListener(
                TogglePersonalVoiceOutput);
            personalSettingButtons[5].onClick.RemoveListener(
                CycleFullScreenMode);
            personalSettingButtons[6].onClick.RemoveListener(
                CycleResolution);
            personalSettingButtons[7].onClick.RemoveListener(
                CycleQualityLevel);
            personalSettingButtons[8].onClick.RemoveListener(
                CycleTargetFrameRate);
            personalSettingButtons[9].onClick.RemoveListener(
                CycleScreenEffectIntensity);
        }

        private IEnumerator BindMatchManagerRoutine()
        {
            while (matchManager == null)
            {
                if (MatchManager.Instance != null)
                    BindMatchManager(MatchManager.Instance);

                yield return null;
            }
        }

        private void BindMatchManager(MatchManager targetMatchManager)
        {
            matchManager = targetMatchManager;

            matchManager.PhaseChanged += OnPhaseChanged;
            matchManager.DayChanged += OnDayChanged;
            matchManager.LocalPlayerMatchStateReceived += OnLocalPlayerMatchStateReceived;
            matchManager.PublicPlayerStatesChanged += OnPublicPlayerStatesChanged;
            matchManager.MorningVoteResultChanged += OnMorningVoteResultChanged;
            matchManager.MorningVoteTalliesChanged +=
                OnMorningVoteTalliesChanged;
            matchManager.LocalMafiaKillerVotesChanged +=
                OnLocalMafiaKillerVotesChanged;
            matchManager.NightResultChanged += OnNightResultChanged;
            matchManager.LocalDetectiveResultReceived += OnLocalDetectiveResultReceived;
            matchManager.LocalForensicsResultReceived += OnLocalForensicsResultReceived;
            matchManager.LocalHunterRouteResultReceived += OnLocalHunterRouteResultReceived;
            matchManager.LocalMafiaMembersChanged += OnLocalMafiaMembersChanged;
            matchManager.LocalSpectatorPlayerStatesChanged +=
                OnLocalSpectatorPlayerStatesChanged;
            matchManager.LocalMafiaDisguiseOptionsChanged += OnLocalMafiaDisguiseOptionsChanged;
            matchManager.LocalDrunkardSleepTargetChanged += OnLocalDrunkardSleepTargetChanged;
            matchManager.LocalNightDutyChanged += OnLocalNightDutyChanged;
            matchManager.GlobalNotificationReceived += OnGlobalNotificationReceived;
            matchManager.WinnerChanged += OnWinnerChanged;
            matchManager.GameResultMafiaNamesChanged +=
                OnGameResultMafiaNamesChanged;
            matchManager.GameResultThiefWinnerNamesChanged +=
                OnGameResultThiefWinnerNamesChanged;
            matchManager.GameResultPlayerStatesChanged +=
                OnGameResultPlayerStatesChanged;
            matchManager.LocalTextChatMessageReceived +=
                OnLocalTextChatMessageReceived;
            matchManager.LocalMediumCommunicationStarted +=
                OnLocalMediumCommunicationStartedForTextChat;
            matchManager.LocalMediumCommunicationEnded +=
                OnLocalMediumCommunicationEndedForTextChat;
            matchManager.LocalPersonalActionRecorded +=
                OnLocalPersonalActionRecorded;
            matchManager.LocalAbilityFailureChanceChanged +=
                RefreshMatchStatusBoard;
            matchManager.VillageDutySummaryChanged +=
                OnVillageDutySummaryChanged;
            matchManager.VillageFullyLitChanged +=
                OnVillageFullyLitChanged;
            matchManager.LampUsedPlayersChanged +=
                OnLampUsedPlayersChanged;
            matchManager.RoleAssignmentProgressChanged +=
                OnRoleAssignmentProgressChanged;

            InitializeMatchStatusTracking();
            roleAssignmentWaitStartedRealtime = Time.realtimeSinceStartup;
            ApplyPhaseUI(matchManager.CurrentPhase);
            RefreshVotePlayers();
            UpdateHeaderUI();
            RefreshSpiritHealthUI();
            UpdatePhaseTimerUI();
            RefreshNightDutyUI();
            RefreshTextChatUI();
            HidePrivateActionResult();
        }

        private void UnbindMatchManager()
        {
            if (matchManager == null)
                return;

            matchManager.PhaseChanged -= OnPhaseChanged;
            matchManager.DayChanged -= OnDayChanged;
            matchManager.LocalPlayerMatchStateReceived -= OnLocalPlayerMatchStateReceived;
            matchManager.PublicPlayerStatesChanged -= OnPublicPlayerStatesChanged;
            matchManager.MorningVoteResultChanged -= OnMorningVoteResultChanged;
            matchManager.MorningVoteTalliesChanged -=
                OnMorningVoteTalliesChanged;
            matchManager.LocalMafiaKillerVotesChanged -=
                OnLocalMafiaKillerVotesChanged;
            matchManager.NightResultChanged -= OnNightResultChanged;
            matchManager.LocalDetectiveResultReceived -= OnLocalDetectiveResultReceived;
            matchManager.LocalForensicsResultReceived -= OnLocalForensicsResultReceived;
            matchManager.LocalHunterRouteResultReceived -= OnLocalHunterRouteResultReceived;
            matchManager.LocalMafiaMembersChanged -= OnLocalMafiaMembersChanged;
            matchManager.LocalSpectatorPlayerStatesChanged -=
                OnLocalSpectatorPlayerStatesChanged;
            matchManager.LocalMafiaDisguiseOptionsChanged -= OnLocalMafiaDisguiseOptionsChanged;
            matchManager.LocalDrunkardSleepTargetChanged -= OnLocalDrunkardSleepTargetChanged;
            matchManager.LocalNightDutyChanged -= OnLocalNightDutyChanged;
            matchManager.GlobalNotificationReceived -= OnGlobalNotificationReceived;
            matchManager.WinnerChanged -= OnWinnerChanged;
            matchManager.GameResultMafiaNamesChanged -=
                OnGameResultMafiaNamesChanged;
            matchManager.GameResultThiefWinnerNamesChanged -=
                OnGameResultThiefWinnerNamesChanged;
            matchManager.GameResultPlayerStatesChanged -=
                OnGameResultPlayerStatesChanged;
            matchManager.LocalTextChatMessageReceived -=
                OnLocalTextChatMessageReceived;
            matchManager.LocalMediumCommunicationStarted -=
                OnLocalMediumCommunicationStartedForTextChat;
            matchManager.LocalMediumCommunicationEnded -=
                OnLocalMediumCommunicationEndedForTextChat;
            matchManager.LocalPersonalActionRecorded -=
                OnLocalPersonalActionRecorded;
            matchManager.LocalAbilityFailureChanceChanged -=
                RefreshMatchStatusBoard;
            matchManager.VillageDutySummaryChanged -=
                OnVillageDutySummaryChanged;
            matchManager.VillageFullyLitChanged -=
                OnVillageFullyLitChanged;
            matchManager.LampUsedPlayersChanged -=
                OnLampUsedPlayersChanged;
            matchManager.RoleAssignmentProgressChanged -=
                OnRoleAssignmentProgressChanged;

            matchManager = null;
        }

        /*
         * 기존 SpiritLampInteraction이 호출하는 공개 메서드명은
         * 직렬화 및 참조 호환을 위해 유지한다.
         */
        public void SetLampAimIndicatorVisible(
            bool visible)
        {
            lampAimIndicatorRequested = visible;
            RefreshInteractionAimIndicator();
        }

        public void SetDroppedToolAimIndicatorVisible(
            bool visible,
            string prompt = "")
        {
            droppedToolAimIndicatorRequested =
                visible;

            droppedToolInteractionPrompt =
                visible
                    ? prompt
                    : string.Empty;

            RefreshInteractionAimIndicator();
        }

        private void ResetInteractionAimIndicator()
        {
            lampAimIndicatorRequested = false;
            droppedToolAimIndicatorRequested = false;
            droppedToolInteractionPrompt = string.Empty;
            RefreshInteractionAimIndicator();
        }

        private void RefreshInteractionAimIndicator()
        {
            if (interactionAimIndicator == null)
                return;

            bool visible =
                lampAimIndicatorRequested ||
                droppedToolAimIndicatorRequested;

            if (interactionPromptText != null)
            {
                interactionPromptText.text =
                    droppedToolAimIndicatorRequested
                        ? droppedToolInteractionPrompt
                        : lampAimIndicatorRequested
                            ? "F  횃불 켜기"
                            : string.Empty;
            }

            if (interactionAimIndicator.activeSelf !=
                visible)
            {
                interactionAimIndicator.SetActive(
                    visible
                );
            }
        }

        private void OnGlobalNotificationReceived(string message)
        {
            ShowGlobalNotification(message);
        }

        private void OnWinnerChanged(
            MatchManager.MatchWinner previous,
            MatchManager.MatchWinner current)
        {
            if (matchManager != null &&
                matchManager.CurrentPhase ==
                    MatchPhase.GameResult)
            {
                ShowGameResult();
            }
        }

        private void OnGameResultMafiaNamesChanged(
            string mafiaNames)
        {
            if (matchManager != null &&
                matchManager.CurrentPhase ==
                    MatchPhase.GameResult)
            {
                ShowGameResult();
            }
        }

        private void OnGameResultThiefWinnerNamesChanged(
            string thiefWinnerNames)
        {
            if (matchManager != null &&
                matchManager.CurrentPhase ==
                    MatchPhase.GameResult)
            {
                ShowGameResult();
            }
        }

        private void OnGameResultPlayerStatesChanged()
        {
            if (matchManager != null &&
                matchManager.CurrentPhase ==
                    MatchPhase.GameResult)
            {
                ShowGameResult();
            }
        }

        private void ShowGlobalNotification(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (globalNotificationText == null)
            {
                ShowPrivateActionResult(message);
                return;
            }

            if (globalNotificationCoroutine != null)
                StopCoroutine(globalNotificationCoroutine);

            globalNotificationText.text = message;

            GameObject targetRoot =
                globalNotificationRoot != null
                    ? globalNotificationRoot
                    : globalNotificationText.gameObject;

            targetRoot.SetActive(true);
            targetRoot.transform.SetAsLastSibling();

            globalNotificationCoroutine =
                StartCoroutine(
                    HideGlobalNotificationAfterDelay()
                );
        }

        private IEnumerator HideGlobalNotificationAfterDelay()
        {
            yield return new WaitForSecondsRealtime(
                globalNotificationDuration
            );

            globalNotificationCoroutine = null;
            HideGlobalNotification();
        }

        private void HideGlobalNotification()
        {
            if (globalNotificationRoot != null)
            {
                globalNotificationRoot.SetActive(false);
                return;
            }

            if (globalNotificationText != null)
                globalNotificationText.gameObject.SetActive(false);
        }

        private void OnLocalForensicsResultReceived(string resultMessage)
        {
            ShowPrivateActionResult(resultMessage);
            AttachPersonalActionDetail(
                RoleActionType.ForensicsInspect,
                resultMessage
            );
        }

        private void OnLocalHunterRouteResultReceived(string resultMessage)
        {
            ShowPrivateActionResult(resultMessage);
            AttachPersonalActionDetail(
                RoleActionType.HunterTrack,
                resultMessage
            );
        }

        private void OnNightResultChanged(NightResultData previous, NightResultData current)
        {
            RecordNightPublicLog(current);

            if (matchManager != null && matchManager.CurrentPhase == MatchPhase.NightResult)
                ShowNightResult();
        }

        private void ShowNightResult()
        {
            if (matchManager == null)
                return;

            NightResultData result = matchManager.NightResult;

            if (!result.hasResult)
            {
                SetResultText(
                    "<size=46>지난밤의 결과</size>\n\n" +
                    "<size=60><b>결과를 처리하고 있습니다.</b></size>"
                );

                return;
            }

            if (!result.hasDeath)
            {
                SetResultText(
                    "<size=46>지난밤의 결과</size>\n\n" +
                    "<size=68><b>아무도 죽지 않았습니다.</b></size>"
                );

                return;
            }

            string firstDeathMessage =
                GetNightDeathMessage();

            string resultMessage =
                "<size=46>지난밤의 결과</size>\n\n" +
                $"<size=78><b>{result.victimPlayerName}</b></size>\n" +
                $"<size=52>{firstDeathMessage}</size>";

            if (result.hasSecondDeath)
            {
                string secondDeathMessage =
                    GetNightDeathMessage();

                resultMessage +=
                    "\n\n" +
                    $"<size=78><b>{result.secondVictimPlayerName}</b></size>\n" +
                    $"<size=52>{secondDeathMessage}</size>";
            }

            SetResultText(
                resultMessage
            );
        }

        private string GetNightDeathMessage()
        {
            return "살해당했습니다.";
        }

        private void OnPhaseChanged(MatchPhase previous, MatchPhase current)
        {
            if (current == MatchPhase.None)
                roleAssignmentWaitStartedRealtime = Time.realtimeSinceStartup;

            if (IsHelpPanelOpen)
                SetHelpPanelVisible(false);

            if (nightChat != null &&
                nightChat.inputField != null)
            {
                nightChat.inputField.DeactivateInputField();
            }

            SetNightChatInputActive(false);

            if (current != MatchPhase.NightAction &&
                current != MatchPhase.NightResult)
            {
                HidePrivateActionResult();
            }

            if (current == MatchPhase.MorningVote)
            {
                ClearChatMessages(morningChat);
                hasUnreadMorningChatMessages = false;
            }

            if (current == MatchPhase.NightPreparation)
                ClearChatMessages(nightChat);

            ApplyPhaseUI(current);
            UpdateHeaderUI();
            RefreshSpiritHealthUI();
            UpdatePhaseTimerUI();
            RefreshTextChatUI();
            RefreshMatchStatusBoard();
            RefreshMatchTransitionOverlay();
        }

        private void OnDayChanged(int previous, int current)
        {
            UpdateHeaderUI();
            RefreshMatchStatusBoard();
        }

        private void OnLocalPlayerMatchStateReceived(PlayerMatchState state)
        {
            UpdateHeaderUI();
            RefreshSpiritHealthUI();
            RefreshTextChatUI();
            RefreshMatchStatusBoard();

            if (matchManager != null &&
                matchManager.CurrentPhase == MatchPhase.NightPreparation)
            {
                ApplyNightPreparationVoteForLocalRole();
            }
        }

        private void OnPublicPlayerStatesChanged()
        {
            TrackPublicPlayerStateChanges();
            RefreshVotePlayers();
            UpdateHeaderUI();
            RefreshTextChatUI();
            RefreshVoiceParticipantRows();

            if (matchManager != null &&
                matchManager.CurrentPhase == MatchPhase.NightPreparation)
            {
                ApplyNightPreparationVoteForLocalRole();
            }

            RefreshMatchStatusBoard();
        }

        private void OnLocalMafiaMembersChanged()
        {
            RefreshMatchStatusBoard();

            if (matchManager != null &&
                matchManager.CurrentPhase == MatchPhase.NightPreparation)
            {
                ApplyNightPreparationVoteForLocalRole();
                return;
            }

            RefreshVotePlayers();
        }

        private void OnLocalSpectatorPlayerStatesChanged()
        {
            RefreshVotePlayers();
            RefreshMatchStatusBoard();
        }

        private void OnLocalMafiaDisguiseOptionsChanged()
        {
            if (matchManager == null ||
                matchManager.CurrentPhase != MatchPhase.NightPreparation)
            {
                return;
            }

            ApplyNightPreparationVoteForLocalRole();
        }

        private void OnLocalDrunkardSleepTargetChanged(ulong targetClientId)
        {
            if (currentVoteMode != VoteMode.DrunkardSleep)
                return;

            SelectVoteTarget(targetClientId);
        }

        private void OnLocalNightDutyChanged()
        {
            RefreshNightDutyUI();
            RefreshMatchStatusBoard();
        }

        private void OnVillageDutySummaryChanged()
        {
            RefreshMatchStatusBoard();
        }

        private void OnVillageFullyLitChanged(
            bool previous,
            bool current)
        {
            RefreshNightDutyUI();
            RefreshMatchStatusBoard();
        }

        private void OnLampUsedPlayersChanged()
        {
            RefreshMatchStatusBoard();
        }

        private void EnsureMatchStatusBoardUI()
        {
            if (matchStatusBoardRoot != null)
                return;

            Canvas parentCanvas =
                GetComponentInParent<Canvas>();
            Transform parent = parentCanvas != null
                ? parentCanvas.transform
                : transform;

            matchStatusBoardRoot = new GameObject(
                "MatchStatusBoard",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(GraphicRaycaster)
            );
            matchStatusBoardRoot.transform.SetParent(
                parent,
                false
            );

            RectTransform rootRect =
                matchStatusBoardRoot.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            Canvas boardCanvas =
                matchStatusBoardRoot.GetComponent<Canvas>();
            boardCanvas.overrideSorting = true;
            boardCanvas.sortingOrder = 800;

            Image overlay =
                matchStatusBoardRoot.GetComponent<Image>();
            overlay.color = new Color(0f, 0f, 0f, 0.68f);
            overlay.raycastTarget = false;

            GameObject window = CreateStatusBoardPanel(
                "Window",
                matchStatusBoardRoot.transform,
                new Vector2(1740f, 920f),
                Vector2.zero,
                new Color(0.045f, 0.05f, 0.055f, 0.97f)
            );

            Outline outline = window.AddComponent<Outline>();
            outline.effectColor =
                new Color(0.84f, 0.68f, 0.28f, 0.75f);
            outline.effectDistance = new Vector2(2f, -2f);

            TextMeshProUGUI title = CreateStatusBoardText(
                "Title",
                window.transform,
                "마을 현황판",
                38f,
                TextAlignmentOptions.Center,
                new Vector2(1600f, 55f),
                new Vector2(0f, 415f)
            );
            title.color = new Color32(242, 209, 115, 255);
            title.fontStyle = FontStyles.Bold;

            matchStatusSummaryText = CreateStatusBoardText(
                "Summary",
                window.transform,
                string.Empty,
                24f,
                TextAlignmentOptions.MidlineLeft,
                new Vector2(1600f, 105f),
                new Vector2(0f, 335f)
            );

            CreateStatusBoardColumn(
                window.transform,
                "RosterColumn",
                "생존 현황",
                new Vector2(-555f, -45f),
                out matchStatusRosterText,
                out _
            );

            CreateStatusBoardColumn(
                window.transform,
                "PublicLogColumn",
                "마을 공개 기록",
                new Vector2(0f, -45f),
                out matchStatusPublicLogText,
                out matchStatusPublicScrollRect
            );

            CreateStatusBoardColumn(
                window.transform,
                "PersonalLogColumn",
                "개인 기록",
                new Vector2(555f, -45f),
                out matchStatusPersonalLogText,
                out matchStatusPersonalScrollRect
            );

            TextMeshProUGUI hint = CreateStatusBoardText(
                "Hint",
                window.transform,
                "TAB 키를 누르는 동안 표시됩니다.  마우스 드래그: 기록 스크롤",
                20f,
                TextAlignmentOptions.Center,
                new Vector2(1500f, 34f),
                new Vector2(0f, -432f)
            );
            hint.color = new Color(1f, 1f, 1f, 0.62f);

            matchStatusBoardRoot.SetActive(false);
        }

        private GameObject CreateStatusBoardPanel(
            string objectName,
            Transform parent,
            Vector2 size,
            Vector2 anchoredPosition,
            Color color)
        {
            GameObject panel = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image)
            );
            panel.transform.SetParent(parent, false);

            RectTransform rect =
                panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            Image image = panel.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;

            return panel;
        }

        private TextMeshProUGUI CreateStatusBoardText(
            string objectName,
            Transform parent,
            string value,
            float fontSize,
            TextAlignmentOptions alignment,
            Vector2 size,
            Vector2 anchoredPosition)
        {
            GameObject textObject = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI)
            );
            textObject.transform.SetParent(parent, false);

            RectTransform rect =
                textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            TextMeshProUGUI text =
                textObject.GetComponent<TextMeshProUGUI>();
            TextMeshProUGUI source = roleNameText != null
                ? roleNameText
                : phaseResultText;

            if (source != null)
            {
                text.font = source.font;
                text.fontSharedMaterial =
                    source.fontSharedMaterial;
            }

            text.text = value;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;

            return text;
        }

        private void CreateStatusBoardColumn(Transform parent, string objectName, string title, Vector2 anchoredPosition, out TextMeshProUGUI contentText, out ScrollRect scrollRect)
        {
            GameObject panel = CreateStatusBoardPanel(
                objectName,
                parent,
                new Vector2(520f, 650f),
                anchoredPosition,
                new Color(0.09f, 0.1f, 0.11f, 0.95f)
            );

            TextMeshProUGUI header = CreateStatusBoardText(
                "Header",
                panel.transform,
                title,
                28f,
                TextAlignmentOptions.Center,
                new Vector2(480f, 46f),
                new Vector2(0f, 292f)
            );
            header.color = new Color32(242, 209, 115, 255);
            header.fontStyle = FontStyles.Bold;

            GameObject viewport = new GameObject(
                "Viewport",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(RectMask2D),
                typeof(ScrollRect)
            );
            viewport.transform.SetParent(panel.transform, false);

            RectTransform viewportRect =
                viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin =
                new Vector2(0.5f, 0.5f);
            viewportRect.anchorMax =
                new Vector2(0.5f, 0.5f);
            viewportRect.pivot =
                new Vector2(0.5f, 0.5f);
            viewportRect.sizeDelta =
                new Vector2(480f, 560f);
            viewportRect.anchoredPosition =
                new Vector2(0f, -20f);

            Image viewportImage =
                viewport.GetComponent<Image>();
            viewportImage.color = Color.clear;
            viewportImage.raycastTarget = true;

            contentText = CreateStatusBoardText(
                "Content",
                viewport.transform,
                string.Empty,
                22f,
                TextAlignmentOptions.TopLeft,
                new Vector2(455f, 0f),
                Vector2.zero
            );

            RectTransform contentRect =
                contentText.rectTransform;
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(-20f, 0f);

            ContentSizeFitter fitter =
                contentText.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit =
                ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            scrollRect = viewport.GetComponent<ScrollRect>();
            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType =
                ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 35f;
        }

        private void UpdateMatchStatusBoardInput()
        {
            if (matchStatusBoardRoot == null)
                return;

            bool tabHeld =
                Keyboard.current != null &&
                Keyboard.current.tabKey.isPressed;

            bool blocked =
                matchManager == null ||
                matchManager.CurrentPhase == MatchPhase.None ||
                matchManager.CurrentPhase == MatchPhase.GameResult ||
                IsHelpPanelOpen ||
                IsAnyTextChatInputFocused() ||
                (leaveMatchConfirmationPanel != null &&
                 leaveMatchConfirmationPanel.activeInHierarchy);

            bool visible =
                tabHeld &&
                !blocked;

            if (matchStatusBoardRoot.activeSelf != visible)
            {
                matchStatusBoardRoot.SetActive(visible);

                if (visible)
                {
                    RefreshMatchStatusBoard();

                    SpiritFirstPersonCamera localCamera =
                        SpiritFirstPersonCamera.LocalInstance;

                    matchStatusBoardRestoreCursorLock =
                        localCamera != null &&
                        !SpiritFirstPersonCamera
                            .IsLocalUIInteractionActive;

                    if (localCamera != null)
                    {
                        localCamera.SendMessage(
                            "UnlockCursor",
                            SendMessageOptions
                                .DontRequireReceiver
                        );
                    }
                }
                else
                {
                    if (matchStatusBoardRestoreCursorLock &&
                        !blocked)
                    {
                        SpiritFirstPersonCamera localCamera =
                            SpiritFirstPersonCamera.LocalInstance;

                        if (localCamera != null)
                        {
                            localCamera.SendMessage(
                                "LockCursor",
                                SendMessageOptions
                                    .DontRequireReceiver
                            );
                        }
                    }

                    matchStatusBoardRestoreCursorLock = false;
                }
            }

            if (!visible)
                return;

            RefreshMatchStatusBoard();
        }

        private void InitializeMatchStatusTracking()
        {
            matchStatusPlayerRecords.Clear();
            matchStatusPublicLogs.Clear();
            matchStatusPersonalLogs.Clear();
            pendingPersonalActionDetails.Clear();
            lastLoggedMorningVoteDay = -1;
            lastLoggedNightResultDay = -1;

            if (matchManager == null ||
                matchManager.PublicPlayerStates == null)
            {
                return;
            }

            for (int i = 0;
                 i < matchManager.PublicPlayerStates.Count;
                 i++)
            {
                PlayerPublicState state =
                    matchManager.PublicPlayerStates[i];

                matchStatusPlayerRecords[state.clientId] =
                    new StatusPlayerRecord
                    {
                        isAlive = state.isAlive,
                        deathDay = state.isAlive
                            ? 0
                            : matchManager.CurrentDay
                    };
            }

            RefreshMatchStatusBoard();
        }

        private void TrackPublicPlayerStateChanges()
        {
            if (matchManager == null ||
                matchManager.PublicPlayerStates == null)
            {
                return;
            }

            for (int i = 0;
                 i < matchManager.PublicPlayerStates.Count;
                 i++)
            {
                PlayerPublicState state =
                    matchManager.PublicPlayerStates[i];

                if (!matchStatusPlayerRecords.TryGetValue(
                        state.clientId,
                        out StatusPlayerRecord record))
                {
                    record = new StatusPlayerRecord
                    {
                        isAlive = state.isAlive,
                        deathDay = state.isAlive
                            ? 0
                            : matchManager.CurrentDay
                    };
                    matchStatusPlayerRecords[state.clientId] =
                        record;
                    continue;
                }

                if (record.isAlive && !state.isAlive)
                {
                    record.deathDay =
                        Mathf.Max(1, matchManager.CurrentDay);
                }

                record.isAlive = state.isAlive;
            }
        }

        private void RecordMorningVotePublicLog(
            MorningVoteResultData result)
        {
            if (matchManager == null ||
                !result.hasResult ||
                lastLoggedMorningVoteDay ==
                    matchManager.CurrentDay)
            {
                return;
            }

            lastLoggedMorningVoteDay =
                matchManager.CurrentDay;

            string message;

            if (result.hasExiledPlayer)
            {
                message =
                    $"{matchManager.CurrentDay}일차 투표: " +
                    $"{EscapeRichText(result.exiledPlayerName.ToString())} 추방 " +
                    $"({result.voteCount}표, 기권 {result.abstainVoteCount}표)";

                if (!matchStatusPlayerRecords.TryGetValue(
                        result.exiledClientId,
                        out StatusPlayerRecord record))
                {
                    record = new StatusPlayerRecord();
                    matchStatusPlayerRecords[result.exiledClientId] =
                        record;
                }

                record.isAlive = false;
                record.wasExiled = true;
                record.deathDay = matchManager.CurrentDay;
            }
            else if (result.isTie)
            {
                message =
                    $"{matchManager.CurrentDay}일차 투표: 후보 동률로 추방 없음 " +
                    $"(기권 {result.abstainVoteCount}표)";
            }
            else
            {
                message =
                    $"{matchManager.CurrentDay}일차 투표: 추방 없음 " +
                    $"(기권 {result.abstainVoteCount}표)";
            }

            matchStatusPublicLogs.Add(message);
            RefreshMatchStatusBoard();
        }

        private void RecordNightPublicLog(
            NightResultData result)
        {
            if (matchManager == null ||
                !result.hasResult ||
                lastLoggedNightResultDay ==
                    matchManager.CurrentDay)
            {
                return;
            }

            lastLoggedNightResultDay =
                matchManager.CurrentDay;

            StringBuilder builder = new StringBuilder();
            builder.Append("<color=#D5B85E><b>");
            builder.Append(matchManager.CurrentDay);
            builder.Append("일차 밤</b></color>");
            builder.Append("\n• 직무 수행률 ");
            builder.Append(GetLastNightDutyCompletionPercent()
                .ToString("0.00"));
            builder.Append('%');
            builder.Append("\n• 마을 안정도 ");
            builder.Append(matchManager.VillageStabilityPercent);
            builder.Append("%\n• 점등 ");
            builder.Append(matchManager.LitHouseIds != null
                ? matchManager.LitHouseIds.Count
                : 0);
            builder.Append('/');
            builder.Append(matchManager.RequiredLitHouseCount);

            if (!result.hasDeath)
            {
                builder.Append("\n• 사망자 없음");
            }
            else
            {
                builder.Append("\n• 사망 ");
                builder.Append(EscapeRichText(
                    result.victimPlayerName.ToString()
                ));

                if (result.hasSecondDeath)
                {
                    builder.Append(", ");
                    builder.Append(EscapeRichText(
                        result.secondVictimPlayerName.ToString()
                    ));
                }
            }

            matchStatusPublicLogs.Add(builder.ToString());
            RefreshMatchStatusBoard();
        }

        private void OnLocalPersonalActionRecorded(
            MatchManager.PersonalActionRecordData data)
        {
            PersonalActionLogEntry entry =
                new PersonalActionLogEntry
                {
                    data = data,
                    detail = string.Empty
                };

            if (data.kind ==
                    MatchManager.PersonalActionRecordKind.RoleAction &&
                pendingPersonalActionDetails.TryGetValue(
                    data.actionType,
                    out string pendingDetail))
            {
                entry.detail = pendingDetail;
                pendingPersonalActionDetails.Remove(
                    data.actionType
                );
            }

            if (data.kind ==
                MatchManager.PersonalActionRecordKind.DrunkardSleepSelection)
            {
                for (int i = matchStatusPersonalLogs.Count - 1;
                     i >= 0;
                     i--)
                {
                    PersonalActionLogEntry previous =
                        matchStatusPersonalLogs[i];

                    if (previous.data.day == data.day &&
                        previous.data.kind == data.kind)
                    {
                        matchStatusPersonalLogs[i] = entry;
                        RefreshMatchStatusBoard();
                        return;
                    }
                }
            }

            matchStatusPersonalLogs.Add(entry);
            RefreshMatchStatusBoard();
        }

        private void AttachPersonalActionDetail(
            RoleActionType actionType,
            string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
                return;

            detail = NormalizePersonalActionDetail(detail);

            for (int i = matchStatusPersonalLogs.Count - 1;
                 i >= 0;
                 i--)
            {
                PersonalActionLogEntry entry =
                    matchStatusPersonalLogs[i];

                if (entry.data.kind !=
                        MatchManager.PersonalActionRecordKind.RoleAction ||
                    entry.data.actionType != actionType ||
                    !entry.data.effectSucceeded)
                {
                    continue;
                }

                entry.detail = detail;
                RefreshMatchStatusBoard();
                return;
            }

            pendingPersonalActionDetails[actionType] = detail;
        }

        private string NormalizePersonalActionDetail(
            string detail)
        {
            string result = detail
                .Replace("</size>", string.Empty)
                .Replace("<b>", string.Empty)
                .Replace("</b>", string.Empty);

            int startIndex = result.IndexOf(
                "<size=",
                StringComparison.OrdinalIgnoreCase
            );

            while (startIndex >= 0)
            {
                int endIndex = result.IndexOf('>', startIndex);

                if (endIndex < 0)
                    break;

                result = result.Remove(
                    startIndex,
                    endIndex - startIndex + 1
                );
                startIndex = result.IndexOf(
                    "<size=",
                    StringComparison.OrdinalIgnoreCase
                );
            }

            return result.Trim();
        }

        private void RefreshMatchStatusBoard()
        {
            if (matchManager == null ||
                matchStatusBoardRoot == null)
            {
                return;
            }

            RefreshMatchStatusSummary();
            RefreshMatchStatusRoster();
            RefreshMatchStatusPublicLog();
            RefreshMatchStatusPersonalLog();
        }

        private void RefreshMatchStatusSummary()
        {
            if (matchStatusSummaryText == null)
                return;

            int litCount = matchManager.LitHouseIds != null
                ? matchManager.LitHouseIds.Count
                : 0;
            bool localCanUseLamp =
                matchManager.HasLocalPlayerMatchState &&
                matchManager.LocalPlayerMatchState.isAlive;
            int localLampRemaining =
                matchManager.LocalHouseLampRemainingUses;
            int localLampAllowance =
                matchManager.LocalHouseLampUseAllowance;

            matchStatusSummaryText.SetText(
                $"<b>{matchManager.CurrentDay}일차 · " +
                $"{GetStatusPhaseDisplayName(matchManager.CurrentPhase)} · " +
                $"{Mathf.CeilToInt((float)matchManager.PhaseRemainingTime)}초</b>\n" +
                $"마을 안정도 {matchManager.VillageStabilityPercent}% · " +
                $"{matchManager.VillageStabilityStateName} · " +
                $"{GetLocalStabilityVisibilityText()}   |   " +
                $"지난밤 직무 수행률 " +
                $"{GetLastNightDutyCompletionPercent():0.00}%   |   " +
                $"점등 {litCount}/{matchManager.RequiredLitHouseCount}   |   " +
                $"내 횃불 {localLampRemaining}/{localLampAllowance} · " +
                $"{(!localCanUseLamp ? "사용 불가" : localLampRemaining > 0 ? "사용 가능" : "모두 사용")}" 
            );
        }

        private float GetLastNightDutyCompletionPercent()
        {
            if (matchManager == null ||
                matchManager.LastNightTotalDutyCount <= 0)
            {
                return 0f;
            }

            return Mathf.Clamp(
                matchManager.LastNightCompletedDutyCount *
                100f /
                matchManager.LastNightTotalDutyCount,
                0f,
                100f
            );
        }

        private void RefreshMatchStatusRoster()
        {
            if (matchStatusRosterText == null ||
                matchManager.PublicPlayerStates == null)
            {
                return;
            }

            StringBuilder builder = new StringBuilder();
            bool hasLocalState =
                matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState
                );
            bool localIsSpectator =
                hasLocalState && !localState.isAlive;

            for (int i = 0;
                 i < matchManager.PublicPlayerStates.Count;
                 i++)
            {
                PlayerPublicState state =
                    matchManager.PublicPlayerStates[i];
                matchStatusPlayerRecords.TryGetValue(
                    state.clientId,
                    out StatusPlayerRecord record
                );

                builder.Append(i + 1);
                builder.Append(". ");
                string playerName = EscapeRichText(
                    state.playerName.ToString()
                );
                bool isLocalPlayer =
                    hasLocalState &&
                    state.clientId == localState.clientId;
                bool showMafiaMarker =
                    IsLocalAliveMafia() &&
                    IsLocalMafiaMember(state.clientId);

                if (isLocalPlayer)
                {
                    builder.Append("<b><color=#");
                    builder.Append(GetTeamColorHex(localState.team));
                    builder.Append('>');

                    if (localState.team == RoleTeam.Mafia)
                        builder.Append("◆ ");
                    else if (localState.team == RoleTeam.Neutral)
                        builder.Append("● ");

                    builder.Append(playerName);
                    builder.Append("</color></b>");
                }
                else if (showMafiaMarker)
                {
                    builder.Append("<color=#");
                    builder.Append(GetTeamColorHex(RoleTeam.Mafia));
                    builder.Append(">◆ ");
                    builder.Append(playerName);
                    builder.Append("</color>");
                }
                else
                {
                    builder.Append(playerName);
                }

                if (localIsSpectator &&
                    matchManager.TryGetLocalSpectatorPlayerMatchState(
                        state.clientId,
                        out PlayerMatchState spectatorState))
                {
                    builder.Append("  <color=#9FA9B5>[");
                    builder.Append(GetTeamDisplayName(
                        spectatorState.team
                    ));
                    builder.Append(" · ");
                    builder.Append(GetRoleDisplayName(
                        spectatorState.role
                    ));
                    builder.Append("]</color>");
                }

                if (state.isAlive)
                {
                    builder.Append("\n   <color=#73D69A>생존</color>");
                }
                else
                {
                    builder.Append("\n   <color=#A8AFB8>");
                    builder.Append(record != null && record.wasExiled
                        ? "추방"
                        : "사망");

                    if (record != null && record.deathDay > 0)
                    {
                        builder.Append(" · ");
                        builder.Append(record.deathDay);
                        builder.Append("일차");
                    }

                    builder.Append("</color>");
                }

                if (i + 1 < matchManager.PublicPlayerStates.Count)
                    builder.Append("\n\n");
            }

            matchStatusRosterText.SetText(builder);
        }

        private void RefreshMatchStatusPublicLog()
        {
            if (matchStatusPublicLogText == null)
                return;

            if (matchStatusPublicLogs.Count == 0)
            {
                matchStatusPublicLogText.SetText(
                    "아직 공개 기록이 없습니다.\n\n" +
                    "투표 결과와 밤 전체 결과만 이곳에 기록됩니다."
                );
                return;
            }

            StringBuilder builder = new StringBuilder();

            for (int i = 0;
                 i < matchStatusPublicLogs.Count;
                 i++)
            {
                string publicLog = matchStatusPublicLogs[i];

                if (publicLog.IndexOf('\n') < 0)
                    builder.Append("• ");

                builder.Append(publicLog);

                if (i + 1 < matchStatusPublicLogs.Count)
                    builder.Append("\n\n");
            }

            matchStatusPublicLogText.SetText(builder);
        }

        private void RefreshMatchStatusPersonalLog()
        {
            if (matchStatusPersonalLogText == null)
                return;

            StringBuilder builder = new StringBuilder();
            builder.Append("<color=#F2D173><b>");
            builder.Append(matchManager.LocalAbilitySuccessPercent.ToString("0.00"));
            builder.Append("%</b></color>\n");
            bool localAbilityPenaltiesApply =
                matchManager.HasLocalPlayerMatchState &&
                matchManager.LocalPlayerMatchState.team ==
                    RoleTeam.Citizen;

            if (localAbilityPenaltiesApply)
            {
                builder.Append("직무 미완료 패널티 -");
                builder.Append((matchManager.LocalDutyFailureChance * 100f)
                    .ToString("0.00"));
                builder.Append("%\n마을 불안정 패널티 -");
                builder.Append((matchManager.LocalStabilityFailureChance * 100f)
                    .ToString("0.00"));
                builder.Append('%');
            }
            else
            {
                builder.Append("직무 미완료 패널티 영향 없음\n");
                builder.Append("마을 불안정 패널티 영향 없음");
            }

            AppendLocalAbilityUsageStatus(builder);

            bool localIsMafia =
                matchManager.HasLocalPlayerMatchState &&
                matchManager.LocalPlayerMatchState.team ==
                    RoleTeam.Mafia;

            builder.Append("\n\n<color=#F2D173><b>");
            builder.Append(localIsMafia
                ? "내 직업 행동 · 마녀 기록"
                : "내 직업 행동 기록");
            builder.Append("</b></color>");

            if (matchStatusPersonalLogs.Count == 0)
            {
                builder.Append("\n아직 기록된 직업 행동이 없습니다.");
                matchStatusPersonalLogText.SetText(builder);
                return;
            }

            int displayedDay = -1;

            for (int i = 0;
                 i < matchStatusPersonalLogs.Count;
                 i++)
            {
                PersonalActionLogEntry entry =
                    matchStatusPersonalLogs[i];

                if (entry.data.day != displayedDay)
                {
                    displayedDay = entry.data.day;
                    builder.Append("\n\n<color=#D5B85E><b>");
                    builder.Append(displayedDay);
                    builder.Append("일차 밤</b></color>");
                }

                builder.Append("\n• ");
                builder.Append(FormatPersonalActionRecord(entry));

                if (!string.IsNullOrWhiteSpace(entry.detail))
                {
                    builder.Append("\n  <color=#C8D1DC>");
                    builder.Append(entry.detail.Replace("\n", "\n  "));
                    builder.Append("</color>");
                }
            }

            matchStatusPersonalLogText.SetText(builder);
        }

        private void AppendLocalAbilityUsageStatus(
            StringBuilder builder)
        {
            if (!matchManager.HasLocalPlayerMatchState)
                return;

            PlayerMatchState localState =
                matchManager.LocalPlayerMatchState;
            bool hasCurrentDayRoleAction = false;

            for (int i = 0;
                 i < matchStatusPersonalLogs.Count;
                 i++)
            {
                MatchManager.PersonalActionRecordData data =
                    matchStatusPersonalLogs[i].data;

                if (data.day == matchManager.CurrentDay &&
                    data.kind ==
                        MatchManager.PersonalActionRecordKind.RoleAction)
                {
                    hasCurrentDayRoleAction = true;
                    break;
                }
            }

            string actionState;

            if (!localState.isAlive)
            {
                actionState = "사용 불가";
            }
            else if (hasCurrentDayRoleAction)
            {
                actionState = "사용 완료";
            }
            else if (matchManager.CurrentPhase ==
                     MatchPhase.NightPreparation)
            {
                actionState = "행동 대기";
            }
            else
            {
                actionState = "사용 기록 없음";
            }

            builder.Append("\n\n<color=#F2D173><b>현재 능력 상태</b></color>");
            builder.Append("\n이번 밤 직업 행동: ");
            builder.Append(actionState);

            if (localState.role == RoleId.Spy)
            {
                builder.Append("\n주민 능력: ");
                builder.Append(
                    matchManager.LocalSpyCitizenAbilityRemainingCount
                );
                builder.Append('/');
                builder.Append(
                    matchManager.LocalSpyCitizenAbilityMaximumCount
                );
                builder.Append("회 남음");
            }

            if (localState.role == RoleId.Exorcist)
            {
                bool exorcismConsumed = false;

                for (int i = 0;
                     i < matchStatusPersonalLogs.Count;
                     i++)
                {
                    MatchManager.PersonalActionRecordData data =
                        matchStatusPersonalLogs[i].data;

                    if (data.kind ==
                            MatchManager.PersonalActionRecordKind.RoleAction &&
                        data.actionType == RoleActionType.Exorcism)
                    {
                        exorcismConsumed = true;
                        break;
                    }
                }

                builder.Append("\n퇴마 능력: ");
                builder.Append(
                    exorcismConsumed
                        ? "소모됨"
                        : "사용 가능"
                );
            }

            if (localState.team == RoleTeam.Mafia &&
                matchManager.IsLocalMafiaInterferenceDuty)
            {
                builder.Append("\n교란 공작: ");

                if (matchManager.LocalNightKillerClientId ==
                        localState.clientId &&
                    !matchManager.HasLocalNightDutyAssignment)
                {
                    builder.Append("수행 불가");
                }
                else
                {
                    builder.Append(
                        matchManager.IsLocalNightDutyCompleted
                            ? "완료"
                            : "수행 가능"
                    );
                }
            }

            if (localState.role == RoleId.CurseCaster)
            {
                CurseDollStateData dollState =
                    matchManager.CurseDollState;
                string dollStatus;

                if (dollState.location == CurseDollLocation.Held &&
                    dollState.holderClientId == localState.clientId)
                {
                    dollStatus = "손에 듦";
                }
                else if (dollState.location ==
                         CurseDollLocation.Installed)
                {
                    dollStatus =
                        $"{dollState.installedHouseId}번 집 설치";
                }
                else
                {
                    dollStatus = "미설치";
                }

                builder.Append("\n저주 인형: ");
                builder.Append(dollStatus);
            }
        }

        private string FormatPersonalActionRecord(
            PersonalActionLogEntry entry)
        {
            MatchManager.PersonalActionRecordData data =
                entry.data;
            string target = data.targetClientId != ulong.MaxValue
                ? GetPublicPlayerName(data.targetClientId)
                : data.targetHouseId >= 0
                    ? $"{data.targetHouseId}번 집"
                    : "대상 없음";

            switch (data.kind)
            {
                case MatchManager.PersonalActionRecordKind
                    .DrunkardSleepSelection:
                    return $"취침 장소 선택: {EscapeRichText(target)}의 집";

                case MatchManager.PersonalActionRecordKind
                    .ThiefInheritance:
                    return
                        $"{EscapeRichText(target)}의 도구 계승 → " +
                        $"{GetRoleDisplayName(data.inheritedRole)} " +
                        $"({GetTeamDisplayName(data.inheritedTeam)})";

                case MatchManager.PersonalActionRecordKind
                    .CurseDollPickup:
                    return $"저주 인형 습득: {EscapeRichText(target)} 위치";

                case MatchManager.PersonalActionRecordKind
                    .CurseDollInstall:
                    return $"저주 인형 설치: {EscapeRichText(target)} 위치";

                case MatchManager.PersonalActionRecordKind
                    .MafiaTeamKill:
                    string actorName =
                        GetPublicPlayerName(data.actorClientId);
                    string sharedActionName;

                    switch (data.actionType)
                    {
                        case RoleActionType.AlchemistPoison:
                            sharedActionName =
                                "독살 대상 지정";
                            break;

                        case RoleActionType.CurseDoll:
                            sharedActionName =
                                "저주 인형 대상 지정";
                            break;

                        default:
                            sharedActionName =
                                "살해 대상 지정";
                            break;
                    }

                    return
                        $"살해 담당 {EscapeRichText(actorName)} " +
                        $"({GetRoleDisplayName(data.role)})\n  " +
                        $"{sharedActionName}: " +
                        $"{EscapeRichText(target)}";

                case MatchManager.PersonalActionRecordKind
                    .MafiaInterference:
                    return
                        "교란 공작 완료\n  " +
                        EscapeRichText(
                            GetMafiaInterferenceRecordName(
                                data
                            )
                        );

                case MatchManager.PersonalActionRecordKind
                    .MafiaInterferenceSummary:
                    return $"팀 교란 공작 " +
                           $"{Mathf.Max(0, data.targetPointId)}회";
            }

            string actionName = GetRoleActionDisplayName(
                data.actionType,
                data.role
            );
            string resultState;

            if (data.wasFake)
            {
                resultState = data.role == RoleId.Peddler
                    ? "가짜 도구 · 효과 없음"
                    : "위장 행동 완료";
            }
            else if (!data.effectSucceeded)
            {
                resultState = "능력 실패 · 횟수 소비";
            }
            else
            {
                resultState = "확정";
            }

            return
                $"{actionName}: {EscapeRichText(target)} " +
                $"<color=#AEB7C2>[{resultState}]</color>";
        }

        private string GetMafiaInterferenceRecordName(
            MatchManager.PersonalActionRecordData data)
        {
            if (data.targetHouseId < 0)
                return "마을 광장 우물 오염";

            string actionName;

            switch (data.targetPointId)
            {
                case 0:
                    actionName = "정문 잠금장치 무력화";
                    break;
                case 1:
                    actionName = "뒷문 잠금장치 무력화";
                    break;
                case 2:
                    actionName = "침대 프레임 약화";
                    break;
                case 3:
                    actionName = "외부 벤치 지지대 약화";
                    break;
                default:
                    actionName = "교란 공작";
                    break;
            }

            if (data.targetClientId != ulong.MaxValue)
            {
                return
                    $"{GetPublicPlayerName(data.targetClientId)}의 " +
                    actionName;
            }

            return $"{data.targetHouseId}번 집의 {actionName}";
        }

        private string GetRoleActionDisplayName(
            RoleActionType actionType,
            RoleId role)
        {
            switch (actionType)
            {
                case RoleActionType.MafiaKill:
                    return role == RoleId.SerialKiller
                        ? "직접 살해 대상 지정"
                        : "팀 살해 대상 지정";
                case RoleActionType.DoctorProtect:
                    return "보호 대상 선택";
                case RoleActionType.BailiffConfine:
                    return "감금";
                case RoleActionType.Exorcism:
                    return "퇴마";
                case RoleActionType.DetectiveInspect:
                    return "직업 조사";
                case RoleActionType.AlchemistPoison:
                    return "독살 대상 지정";
                case RoleActionType.ForensicsInspect:
                    return "시신 검시";
                case RoleActionType.HunterTrack:
                    return "동선 추적";
                case RoleActionType.UndertakerSeal:
                    return "정문 봉인";
                case RoleActionType.MediumCommune:
                    return "영매 교신 대상 선택";
                case RoleActionType.CurseDoll:
                    return "최초 저주 인형 설치";
                default:
                    return actionType.ToString();
            }
        }

        private string GetStatusPhaseDisplayName(
            MatchPhase phase)
        {
            switch (phase)
            {
                case MatchPhase.MorningVote:
                    return "아침 투표";
                case MatchPhase.MorningVoteResult:
                    return "투표 결과";
                case MatchPhase.NightPreparation:
                    return "밤 준비";
                case MatchPhase.NightAction:
                    return "밤 행동";
                case MatchPhase.NightResult:
                    return "밤 결과";
                case MatchPhase.FinalDuel:
                    return "광장 결투";
                default:
                    return phase.ToString();
            }
        }

        private void RefreshNightDutyUI()
        {
            bool hasAssignment =
                matchManager != null &&
                matchManager.HasLocalNightDutyAssignment;

            bool showStabilityWithoutAssignment =
                matchManager != null &&
                matchManager.HasLocalPlayerMatchState &&
                matchManager.LocalPlayerMatchState.team !=
                    RoleTeam.Citizen;

            bool showNightDutyPanel =
                hasAssignment ||
                showStabilityWithoutAssignment;

            if (nightDutyPanel != null)
                nightDutyPanel.SetActive(showNightDutyPanel);

            if (!showNightDutyPanel)
            {
                if (firstNightDutyRow != null)
                    firstNightDutyRow.SetActive(false);

                if (secondNightDutyRow != null)
                    secondNightDutyRow.SetActive(false);

                return;
            }

            int assignmentCount =
                hasAssignment
                    ? matchManager.LocalNightDutyAssignmentCount
                    : 0;

            if (nightDutyHeaderText != null)
            {
                bool showMafiaInterference =
                    matchManager.HasLocalPlayerMatchState &&
                    matchManager.LocalPlayerMatchState.team ==
                        RoleTeam.Mafia &&
                    matchManager.IsLocalMafiaInterferenceDuty;

                if (showMafiaInterference)
                {
                    nightDutyHeaderText.SetText(
                        $"마을 안정도 " +
                        $"{matchManager.VillageStabilityPercent}%" +
                        (hasAssignment
                            ? " · 교란 공작 택1"
                            : string.Empty)
                    );
                }
                else
                {
                    nightDutyHeaderText.SetText(
                        $"마을 안정도 " +
                        $"{matchManager.VillageStabilityPercent}%"
                    );
                }
            }

            bool hasFirstAssignment =
                assignmentCount > 0;

            if (firstNightDutyRow != null)
            {
                firstNightDutyRow.SetActive(
                    hasFirstAssignment
                );
            }

            if (hasFirstAssignment)
            {
                if (firstNightDutyStatusIcon != null)
                {
                    firstNightDutyStatusIcon.SetText(
                        matchManager
                            .IsLocalNightDutyAssignmentCompleted(0)
                            ? "■"
                            : matchManager
                                .IsLocalNightDutyAssignmentUnavailable(0)
                                ? "×"
                                : "□"
                    );
                }

                if (firstNightDutyText != null)
                {
                    firstNightDutyText.SetText(
                        matchManager.GetLocalNightDutyName(0)
                    );
                }
            }

            bool hasSecondAssignment =
                assignmentCount > 1;

            if (secondNightDutyRow != null)
            {
                secondNightDutyRow.SetActive(
                    hasSecondAssignment
                );
            }

            if (hasSecondAssignment)
            {
                if (secondNightDutyStatusIcon != null)
                {
                    secondNightDutyStatusIcon.SetText(
                        matchManager
                            .IsLocalNightDutyAssignmentCompleted(1)
                            ? "■"
                            : matchManager
                                .IsLocalNightDutyAssignmentUnavailable(1)
                                ? "×"
                                : "□"
                    );
                }

                if (secondNightDutyText != null)
                {
                    secondNightDutyText.SetText(
                        matchManager.GetLocalNightDutyName(1)
                    );
                }
            }
        }

        private string GetLocalStabilityVisibilityText()
        {
            if (matchManager == null)
                return "영체 식별 정보 없음";

            if (matchManager
                .LocalIgnoresVillageStabilityVisibility)
            {
                return "영체 식별 영향 없음";
            }

            float distance =
                matchManager.LocalSpiritVisibilityDistance;

            if (float.IsPositiveInfinity(distance))
                return "영체 식별 제한 없음";

            return matchManager.IsVillageFullyLit
                ? $"영체 식별 {distance:0}m · 횃불"
                : $"영체 식별 {distance:0}m";
        }

        private void OnMorningVoteTalliesChanged()
        {
            RefreshMorningVoteTallyUI();
        }

        private void OnLocalMafiaKillerVotesChanged()
        {
            if (currentVoteMode == VoteMode.MafiaKiller)
            {
                RefreshVotePlayers();
                return;
            }

            RefreshMorningVoteTallyUI();
        }

        private void OnMorningVoteResultChanged(MorningVoteResultData previous, MorningVoteResultData current)
        {
            RecordMorningVotePublicLog(current);

            if (matchManager != null && matchManager.CurrentPhase == MatchPhase.MorningVoteResult)
                ShowMorningVoteResult();
        }

        private void ApplyPhaseUI(MatchPhase phase)
        {
            bool isDiscussion = phase == MatchPhase.MorningDiscussion;
            bool isVote = phase == MatchPhase.MorningVote;
            bool isVoteResult = phase == MatchPhase.MorningVoteResult;
            bool isNightPreparation = phase == MatchPhase.NightPreparation;
            bool isNightResult = phase == MatchPhase.NightResult;
            bool isGameResult = phase == MatchPhase.GameResult;

            bool showBlockingBackground =
                phase != MatchPhase.None &&
                phase != MatchPhase.NightAction &&
                phase != MatchPhase.FinalDuel;

            if (morningBackground != null)
                morningBackground.SetActive(showBlockingBackground);

            ApplyInputMode(phase);

            RestoreDefaultTabButtonLabels();
            currentVoteMode = VoteMode.None;
            localMafiaKillerVoteTargetClientId = ulong.MaxValue;
            ResetMorningVoteLocalState();
            SetReturnToLobbyButtonVisible(false);
            SetMeetingUIVisible(false);
            SetTabButtonsVisible(false, false);
            SetVoteControlsVisible(false, false);
            SetVoteHeader(VoteMode.None);
            ClearSelectedVote();
            ApplyGameResultLayout(false);
            SetGameResultRosterVisible(false);
            RefreshMatchTransitionOverlay();

            if (isDiscussion)
            {
                SetMeetingUIVisible(true);
                SetTabButtonsVisible(true, false);
                SetResultText(string.Empty);
                SetTab(true);
                return;
            }

            if (isVote)
            {
                SetMeetingUIVisible(true);
                SetTabButtonsVisible(true, true);
                SetResultText(string.Empty);

                currentVoteMode = VoteMode.MorningExile;
                SetVoteHeader(currentVoteMode);
                SetVoteControlsVisible(true, true);
                RefreshVotePlayers();
                RefreshMorningVoteTallyUI();
                UpdateMorningVoteInteractionState();
                SetTab(false);
                return;
            }

            if (isVoteResult)
            {
                HideAllVoteRows();
                ShowMorningVoteResult();
                return;
            }

            if (isNightPreparation)
            {
                SetResultText(string.Empty);
                ApplyNightPreparationVoteForLocalRole();
                return;
            }

            if (isNightResult)
            {
                HideAllVoteRows();
                ShowNightResult();
                return;
            }

            if (isGameResult)
            {
                ShowGameResult();
                SetReturnToLobbyButtonVisible(true);
                return;
            }

            HideAllVoteRows();
            SetResultText(string.Empty);
        }

        private void ShowGameResult()
        {
            if (matchManager == null)
                return;

            ApplyGameResultLayout(true);

            string winnerTitle;
            string winnerDescription;

            switch (matchManager.Winner)
            {
                case MatchManager.MatchWinner.Citizen:
                    winnerTitle = "주민 승리";
                    winnerDescription = "모든 마녀가 제거되었습니다.";
                    break;

                case MatchManager.MatchWinner.Mafia:
                    winnerTitle = "마녀 승리";
                    winnerDescription = "모든 주민이 제거되었습니다.";
                    break;

                case MatchManager.MatchWinner.SerialKiller:
                    winnerTitle = "살인귀 승리";
                    winnerDescription = "살인귀 혼자 살아남았습니다.";
                    break;

                case MatchManager.MatchWinner.Martyr:
                    winnerTitle = "순교자 승리";
                    winnerDescription = "순교자가 아침 투표로 추방되었습니다.";
                    break;

                case MatchManager.MatchWinner.Thief:
                    winnerTitle = "도둑 승리";
                    winnerDescription = "도둑들만 살아남았습니다.";
                    break;

                default:
                    winnerTitle = "게임 종료";
                    winnerDescription = "승리 진영을 확인할 수 없습니다.";
                    break;
            }

            string individualWinnerSection = string.Empty;
            ulong individualWinnerClientId =
                matchManager.GameResultIndividualWinnerClientId;

            if (matchManager.Winner == MatchManager.MatchWinner.Thief)
            {
                string thiefWinnerNames =
                    EscapeRichText(matchManager.GameResultThiefWinnerNames);

                if (!string.IsNullOrWhiteSpace(thiefWinnerNames))
                {
                    individualWinnerSection =
                        $"\n<size=28>공동 승리: {thiefWinnerNames}</size>";
                }
            }
            else if (individualWinnerClientId != ulong.MaxValue)
            {
                string individualWinnerName = string.Empty;
                matchManager.TryGetPublicPlayerName(
                    individualWinnerClientId,
                    out individualWinnerName
                );

                MorningVoteResultData voteResult =
                    matchManager.MorningVoteResult;

                if (string.IsNullOrWhiteSpace(individualWinnerName) &&
                    voteResult.hasExiledPlayer &&
                    voteResult.exiledClientId == individualWinnerClientId)
                {
                    individualWinnerName =
                        voteResult.exiledPlayerName.ToString();
                }

                if (!string.IsNullOrWhiteSpace(individualWinnerName))
                {
                    individualWinnerSection =
                        $"\n<size=28>개인 승리: " +
                        $"{EscapeRichText(individualWinnerName)}</size>";
                }
            }

            string localResultSection = string.Empty;

            if (matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState))
            {
                bool localWon = DidPlayerWin(localState);

                localResultSection =
                    $"\n<size=28><b>나의 결과: " +
                    $"{(localWon ? "승리" : "패배")}</b> · " +
                    $"{GetTeamDisplayName(localState.team)} · " +
                    $"{GetRoleDisplayName(localState.role)}</size>";
            }

            SetResultText(
                "<size=42>게임 결과</size>\n" +
                $"<size=66><b>{winnerTitle}</b></size>\n" +
                $"<size=32>{winnerDescription}</size>" +
                individualWinnerSection +
                localResultSection
            );

            RefreshGameResultRoster();
        }

        private void ShowLegacyGameResult()
        {
            if (matchManager == null)
                return;

            ApplyGameResultLayout(true);

            string winnerTitle;
            string winnerDescription;

            switch (matchManager.Winner)
            {
                case MatchManager.MatchWinner.Citizen:
                    winnerTitle = "주민 승리";
                    winnerDescription =
                        "모든 마녀가 제거되었습니다.";
                    break;

                case MatchManager.MatchWinner.Mafia:
                    winnerTitle = "마녀 승리";
                    winnerDescription =
                        "모든 주민이 제거되었습니다.";
                    break;

                case MatchManager.MatchWinner.SerialKiller:
                    winnerTitle = "살인귀 승리";
                    winnerDescription =
                        "살인귀 혼자 살아남았습니다.";
                    break;

                case MatchManager.MatchWinner.Martyr:
                    winnerTitle = "순교자 승리";
                    winnerDescription =
                        "순교자가 아침 투표로 추방되었습니다.";
                    break;

                case MatchManager.MatchWinner.Thief:
                    winnerTitle = "도둑 승리";
                    winnerDescription =
                        "도둑들만 살아남았습니다.";
                    break;

                default:
                    winnerTitle = "게임 종료";
                    winnerDescription =
                        "승리 진영을 확인할 수 없습니다.";
                    break;
            }

            string mafiaNames =
                EscapeRichText(
                    matchManager.GameResultMafiaNames
                );

            if (string.IsNullOrWhiteSpace(mafiaNames))
                mafiaNames = "확인 중";

            string individualWinnerSection =
                string.Empty;

            ulong individualWinnerClientId =
                matchManager
                    .GameResultIndividualWinnerClientId;

            string individualWinnerName =
                string.Empty;

            if (matchManager.Winner ==
                MatchManager.MatchWinner.Thief)
            {
                string thiefWinnerNames =
                    EscapeRichText(
                        matchManager
                            .GameResultThiefWinnerNames
                    );

                if (string.IsNullOrWhiteSpace(
                        thiefWinnerNames))
                {
                    thiefWinnerNames = "확인 중";
                }

                individualWinnerSection =
                    "\n\n<size=34><b>도둑 공동 승리자</b></size>\n" +
                    $"<size=32>{thiefWinnerNames}</size>";
            }
            else if (individualWinnerClientId != ulong.MaxValue)
            {
                matchManager.TryGetPublicPlayerName(
                    individualWinnerClientId,
                    out individualWinnerName
                );

                MorningVoteResultData voteResult =
                    matchManager.MorningVoteResult;

                if (string.IsNullOrWhiteSpace(
                        individualWinnerName) &&
                    voteResult.hasExiledPlayer &&
                    voteResult.exiledClientId ==
                        individualWinnerClientId)
                {
                    individualWinnerName =
                        voteResult.exiledPlayerName
                            .ToString();
                }

                if (string.IsNullOrWhiteSpace(
                        individualWinnerName))
                {
                    individualWinnerName = "확인 중";
                }

                individualWinnerSection =
                    "\n\n<size=34><b>개인 승리자</b></size>\n" +
                    $"<size=32>{EscapeRichText(individualWinnerName)}</size>";
            }

            string localResultSection =
                string.Empty;

            if (matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState))
            {
                bool localWon =
                    (matchManager.Winner ==
                         MatchManager.MatchWinner.Citizen &&
                     localState.team == RoleTeam.Citizen) ||
                    (matchManager.Winner ==
                         MatchManager.MatchWinner.Mafia &&
                     localState.team == RoleTeam.Mafia) ||
                    ((matchManager.Winner ==
                          MatchManager.MatchWinner.SerialKiller ||
                       matchManager.Winner ==
                           MatchManager.MatchWinner.Martyr) &&
                      localState.clientId ==
                          individualWinnerClientId) ||
                    (matchManager.Winner ==
                         MatchManager.MatchWinner.Thief &&
                     localState.isAlive &&
                     localState.team == RoleTeam.Neutral &&
                     localState.role == RoleId.Thief);

                localResultSection =
                    "\n\n<size=34><b>내 결과</b></size>\n" +
                    $"<size=32>{(localWon ? "승리" : "패배")} · " +
                    $"{GetTeamDisplayName(localState.team)} · " +
                    $"{GetRoleDisplayName(localState.role)}</size>";
            }

            SetResultText(
                "<size=46>게임 결과</size>\n\n" +
                $"<size=80><b>{winnerTitle}</b></size>\n" +
                $"<size=38>{winnerDescription}</size>\n\n" +
                "<size=34><b>마녀 명단</b></size>\n" +
                $"<size=32>{mafiaNames}</size>" +
                individualWinnerSection +
                localResultSection
            );

            RefreshGameResultRoster();
        }

        private bool DidPlayerWin(
            PlayerMatchState playerState)
        {
            if (matchManager == null)
                return false;

            switch (matchManager.Winner)
            {
                case MatchManager.MatchWinner.Citizen:
                    return playerState.team == RoleTeam.Citizen;

                case MatchManager.MatchWinner.Mafia:
                    return playerState.team == RoleTeam.Mafia;

                case MatchManager.MatchWinner.SerialKiller:
                case MatchManager.MatchWinner.Martyr:
                    return playerState.clientId ==
                           matchManager.GameResultIndividualWinnerClientId;

                case MatchManager.MatchWinner.Thief:
                    return playerState.isAlive &&
                           playerState.team == RoleTeam.Neutral &&
                           playerState.role == RoleId.Thief;

                default:
                    return false;
            }
        }

        private void RefreshGameResultRoster()
        {
            if (matchManager == null ||
                gameResultRosterText == null ||
                matchManager.GameResultPlayerStates == null)
            {
                SetGameResultRosterVisible(false);
                return;
            }

            StringBuilder builder = new StringBuilder(1024);
            builder.AppendLine("<size=30><b>최종 역할 공개</b></size>");

            for (int i = 0;
                 i < matchManager.GameResultPlayerStates.Count;
                 i++)
            {
                PlayerMatchState playerState =
                    matchManager.GameResultPlayerStates[i];

                string teamColor =
                    GetTeamColorHex(playerState.team);
                string lifeState =
                    playerState.isAlive ? "생존" : "사망";
                string resultState =
                    DidPlayerWin(playerState) ? "승리" : "패배";
                string resultColor =
                    DidPlayerWin(playerState) ? "FFCC33" : "A9A9A9";

                builder.Append(i + 1);
                builder.Append(". ");
                builder.Append(EscapeRichText(
                    playerState.playerName.ToString()));
                builder.Append("  |  <color=#");
                builder.Append(teamColor);
                builder.Append('>');
                builder.Append(GetTeamDisplayName(playerState.team));
                builder.Append(" · ");
                builder.Append(GetRoleDisplayName(playerState.role));
                builder.Append("</color>  |  ");
                builder.Append(lifeState);
                builder.Append("  |  <color=#");
                builder.Append(resultColor);
                builder.Append('>');
                builder.Append(resultState);
                builder.AppendLine("</color>");
            }

            if (matchManager.GameResultPlayerStates.Count == 0)
            {
                builder.Append("최종 역할 정보를 불러오는 중입니다.");
            }

            gameResultRosterText.SetText(builder.ToString());
            SetGameResultRosterVisible(true);

            if (gameResultRosterScrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                gameResultRosterScrollRect.verticalNormalizedPosition = 1f;
            }
        }

        private void SetGameResultRosterVisible(bool visible)
        {
            if (gameResultRosterPanel == null)
                return;

            gameResultRosterPanel.SetActive(visible);

            if (visible)
                gameResultRosterPanel.transform.SetAsLastSibling();
        }

        private void CacheDefaultPhaseResultLayout()
        {
            if (phaseResultText == null ||
                hasDefaultPhaseResultLayout)
            {
                return;
            }

            RectTransform resultRect =
                phaseResultText.rectTransform;

            defaultPhaseResultPosition =
                resultRect.anchoredPosition;
            defaultPhaseResultSize =
                resultRect.sizeDelta;
            hasDefaultPhaseResultLayout = true;
        }

        private void ApplyGameResultLayout(bool gameResultLayout)
        {
            if (phaseResultText == null)
                return;

            CacheDefaultPhaseResultLayout();

            if (!hasDefaultPhaseResultLayout)
                return;

            RectTransform resultRect =
                phaseResultText.rectTransform;

            if (!gameResultLayout)
            {
                resultRect.anchoredPosition =
                    defaultPhaseResultPosition;
                resultRect.sizeDelta =
                    defaultPhaseResultSize;
                return;
            }

            resultRect.anchoredPosition =
                new Vector2(
                    defaultPhaseResultPosition.x,
                    255f
                );
            resultRect.sizeDelta =
                new Vector2(
                    defaultPhaseResultSize.x,
                    330f
                );
        }

        private void EnsureGameResultRosterUI()
        {
            if (gameResultRosterPanel != null ||
                phaseResultText == null)
            {
                return;
            }

            gameResultRosterPanel =
                new GameObject(
                    "GameResultRosterPanel",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image),
                    typeof(ScrollRect)
                );

            gameResultRosterPanel.transform.SetParent(
                transform,
                false
            );

            RectTransform panelRect =
                gameResultRosterPanel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.17f, 0.24f);
            panelRect.anchorMax = new Vector2(0.83f, 0.54f);
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            Image panelImage =
                gameResultRosterPanel.GetComponent<Image>();
            panelImage.color = new Color(0.04f, 0.04f, 0.04f, 0.92f);

            GameObject viewport =
                new GameObject(
                    "Viewport",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image),
                    typeof(RectMask2D)
                );
            viewport.transform.SetParent(
                gameResultRosterPanel.transform,
                false
            );

            RectTransform viewportRect =
                viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(28f, 20f);
            viewportRect.offsetMax = new Vector2(-28f, -20f);

            Image viewportImage = viewport.GetComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);

            GameObject content =
                new GameObject(
                    "Content",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(TextMeshProUGUI),
                    typeof(ContentSizeFitter)
                );
            content.transform.SetParent(viewport.transform, false);

            RectTransform contentRect =
                content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = Vector2.zero;

            gameResultRosterText =
                content.GetComponent<TextMeshProUGUI>();
            gameResultRosterText.font = phaseResultText.font;
            gameResultRosterText.fontSharedMaterial =
                phaseResultText.fontSharedMaterial;
            gameResultRosterText.fontSize = 26f;
            gameResultRosterText.color = Color.white;
            gameResultRosterText.alignment =
                TextAlignmentOptions.TopLeft;
            gameResultRosterText.richText = true;
            gameResultRosterText.textWrappingMode =
                TextWrappingModes.NoWrap;
            gameResultRosterText.overflowMode =
                TextOverflowModes.Overflow;
            gameResultRosterText.raycastTarget = false;

            ContentSizeFitter contentSizeFitter =
                content.GetComponent<ContentSizeFitter>();
            contentSizeFitter.horizontalFit =
                ContentSizeFitter.FitMode.Unconstrained;
            contentSizeFitter.verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            gameResultRosterScrollRect =
                gameResultRosterPanel.GetComponent<ScrollRect>();
            gameResultRosterScrollRect.viewport = viewportRect;
            gameResultRosterScrollRect.content = contentRect;
            gameResultRosterScrollRect.horizontal = false;
            gameResultRosterScrollRect.vertical = true;
            gameResultRosterScrollRect.movementType =
                ScrollRect.MovementType.Clamped;
            gameResultRosterScrollRect.scrollSensitivity = 30f;

            gameResultRosterPanel.SetActive(false);
        }

        private static string EscapeRichText(
            string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private void SetReturnToLobbyButtonVisible(
            bool visible)
        {
            if (returnToLobbyButton == null)
                return;

            bool showForHost =
                visible &&
                NetworkManager.Singleton != null &&
                NetworkManager.Singleton.IsHost;

            returnToLobbyButton.gameObject.SetActive(
                showForHost
            );

            returnToLobbyButton.interactable =
                showForHost;

            if (showForHost)
            {
                returnToLobbyButton.transform
                    .SetAsLastSibling();
            }
        }

        private void OnReturnToLobbyClicked()
        {
            TryStartGameResultLobbyReturn();
        }

        private void TryStartGameResultLobbyReturn()
        {
            if (matchManager == null ||
                matchManager.CurrentPhase !=
                    MatchPhase.GameResult)
            {
                ShowLobbyReturnFailure(true);
                return;
            }

            if (!matchManager.RequestReturnToLobby())
            {
                ShowLobbyReturnFailure(true);
                return;
            }

            BeginLobbyReturnTransition(true);

            if (returnToLobbyButton != null)
                returnToLobbyButton.interactable = false;
        }

        private void ApplyNightPreparationVoteForLocalRole()
        {
            VoteMode voteMode = VoteMode.None;

            if (matchManager != null &&
                matchManager.CurrentPhase == MatchPhase.NightPreparation &&
                matchManager.TryGetLocalPlayerMatchState(out PlayerMatchState localState) &&
                localState.isAlive)
            {
                if (localState.team == RoleTeam.Mafia)
                {
                    voteMode = currentVoteMode == VoteMode.MafiaDisguise &&
                               matchManager.CanLocalSelectMafiaDisguise
                        ? VoteMode.MafiaDisguise
                        : VoteMode.MafiaKiller;
                }
                else if (localState.role == RoleId.Drunkard)
                {
                    voteMode = VoteMode.DrunkardSleep;
                }
            }

            SetNightPreparationVoteMode(voteMode);
        }

        private void SetNightPreparationVoteMode(VoteMode voteMode)
        {
            currentVoteMode = voteMode;

            bool visible =
                voteMode != VoteMode.None;

            bool showMafiaTabs =
                CanUseMafiaPreparationModes();

            bool canSelectDisguise =
                showMafiaTabs &&
                matchManager.CanLocalSelectMafiaDisguise;

            if (showMafiaTabs)
                SetMafiaPreparationTabLabels();
            else
                RestoreDefaultTabButtonLabels();

            if (tabSystemRoot != null)
                tabSystemRoot.SetActive(showMafiaTabs);

            SetTabButtonsVisible(
                showMafiaTabs,
                canSelectDisguise
            );

            if (contentAreaRoot != null)
                contentAreaRoot.SetActive(visible);

            if (chatPanel != null)
                chatPanel.SetActive(false);

            if (votePanel != null)
                votePanel.SetActive(visible);

            bool killerModeSelected =
                showMafiaTabs &&
                voteMode == VoteMode.MafiaKiller;

            bool disguiseModeSelected =
                showMafiaTabs &&
                voteMode == VoteMode.MafiaDisguise;

            if (chatTabHighlight != null)
            {
                chatTabHighlight.gameObject.SetActive(
                    killerModeSelected
                );
            }

            if (voteTabHighlight != null)
            {
                voteTabHighlight.gameObject.SetActive(
                    disguiseModeSelected
                );
            }

            SetTabTextColor(
                chatTabButton,
                killerModeSelected
            );

            SetTabTextColor(
                voteTabButton,
                disguiseModeSelected
            );

            SetVoteHeader(voteMode);
            SetVoteControlsVisible(
                visible,
                voteMode != VoteMode.MafiaDisguise
            );
            ClearSelectedVote();

            if (visible)
                RefreshVotePlayers();
            else
                HideAllVoteRows();
        }

        private void SetVoteHeader(VoteMode voteMode)
        {
            string title = string.Empty;
            string subtitle = string.Empty;
            string cancelLabel = "기권";

            switch (voteMode)
            {
                case VoteMode.MorningExile:
                    title = "추방 투표";
                    subtitle = "추방할 플레이어를 선택하세요";
                    cancelLabel = "기권";
                    break;

                case VoteMode.MafiaKiller:
                    title = "살해 담당자 투표";
                    subtitle = "오늘 밤 살해를 담당할 마녀 진영 구성원을 선택하세요";
                    cancelLabel = "선택 취소";
                    break;

                case VoteMode.MafiaDisguise:
                    title = "위장 도구 선택";
                    subtitle = "보유한 위장 도구 중 오늘 밤 사용할 도구를 선택하세요";
                    cancelLabel = string.Empty;
                    break;

                case VoteMode.DrunkardSleep:
                    title = "취침 장소 선택";
                    subtitle = "오늘 밤 잠들 집의 주인을 선택하세요";
                    cancelLabel = "선택 취소";
                    break;
            }

            if (voteTitleText != null)
                voteTitleText.text = title;

            if (voteSubtitleText != null)
                voteSubtitleText.text = subtitle;

            if (abstainButtonText != null)
                abstainButtonText.text = cancelLabel;

            RefreshMorningVoteTallyUI();
        }

        private void ApplyInputMode(MatchPhase phase)
        {
            bool isActionPhase =
                !IsHelpPanelOpen &&
                (phase == MatchPhase.NightAction ||
                 phase == MatchPhase.FinalDuel);

            Cursor.lockState = isActionPhase ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !isActionPhase;
        }

        private void UpdateHelpInput()
        {
            Keyboard keyboard = Keyboard.current;

            if (keyboard == null ||
                !keyboard.escapeKey.wasPressedThisFrame)
            {
                return;
            }

            if (leaveMatchConfirmationPanel != null &&
                leaveMatchConfirmationPanel.activeSelf)
            {
                SetLeaveMatchConfirmationVisible(false);
                return;
            }

            if (IsHelpPanelOpen)
            {
                SetHelpPanelVisible(false);
                return;
            }

            if (nightChatInputSelected)
                return;

            if (morningChat != null &&
                morningChat.inputField != null &&
                morningChat.inputField.isFocused)
            {
                morningChat.inputField.DeactivateInputField();
                return;
            }

            if (IsAnyTextChatInputFocused() ||
                matchManager == null ||
                matchManager.CurrentPhase == MatchPhase.None)
            {
                return;
            }

            SetHelpPanelVisible(true);
        }

        private void OnCloseHelpClicked()
        {
            SetHelpPanelVisible(false);
        }

        private void OnLeaveMatchClicked()
        {
            if (matchManager == null)
                return;

            SetLeaveMatchConfirmationVisible(true);
        }

        private void OnCancelLeaveMatchClicked()
        {
            SetLeaveMatchConfirmationVisible(false);
        }

        private void OnConfirmLeaveMatchClicked()
        {
            TryStartConfirmedLobbyReturn();
        }

        private void TryStartConfirmedLobbyReturn()
        {
            if (matchManager == null)
            {
                ShowLobbyReturnFailure(false);
                return;
            }

            SetLobbyReturnSourceButtonsInteractable(false);

            NetworkManager networkManager =
                NetworkManager.Singleton;

            if (networkManager != null && networkManager.IsHost)
            {
                if (matchManager
                        .RequestAbandonMatchAndReturnToLobby())
                {
                    BeginLobbyReturnTransition(false);
                }
                else
                {
                    ShowLobbyReturnFailure(false);
                }

                return;
            }

            RelayConnectionManager relayManager =
                RelayConnectionManager.Instance;

            if (relayManager == null)
            {
                ShowLobbyReturnFailure(false);
                return;
            }

            BeginLobbyReturnTransition(false);
            relayManager.LeaveSessionAndReturnToLobby();
        }

        private void BeginLobbyReturnTransition(
            bool fromGameResult)
        {
            isLeavingMatch = true;
            lobbyReturnRequestFailed = false;
            lobbyReturnRequestDelayed = false;
            lobbyReturnFromGameResult = fromGameResult;
            lobbyReturnRequestStartedRealtime =
                Time.realtimeSinceStartup;
            RefreshMatchTransitionOverlay();
        }

        private void ShowLobbyReturnFailure(
            bool fromGameResult)
        {
            isLeavingMatch = true;
            lobbyReturnRequestFailed = true;
            lobbyReturnRequestDelayed = false;
            lobbyReturnFromGameResult = fromGameResult;
            RefreshMatchTransitionOverlay();
        }

        private void OnMatchTransitionRetryClicked()
        {
            if (!isLeavingMatch)
                return;

            if (lobbyReturnRequestDelayed)
            {
                lobbyReturnRequestDelayed = false;
                lobbyReturnRequestStartedRealtime =
                    Time.realtimeSinceStartup;
                RefreshMatchTransitionOverlay();
                return;
            }

            if (!lobbyReturnRequestFailed)
                return;

            if (lobbyReturnFromGameResult)
                TryStartGameResultLobbyReturn();
            else
                TryStartConfirmedLobbyReturn();
        }

        private void OnMatchTransitionCancelClicked()
        {
            if (!isLeavingMatch)
                return;

            if (lobbyReturnRequestDelayed)
            {
                RelayConnectionManager relayManager =
                    RelayConnectionManager.Instance;

                if (relayManager == null)
                {
                    ShowLobbyReturnFailure(
                        lobbyReturnFromGameResult
                    );
                    return;
                }

                BeginLobbyReturnTransition(
                    lobbyReturnFromGameResult
                );
                relayManager.LeaveSessionAndReturnToLobby();
                return;
            }

            if (!lobbyReturnRequestFailed)
                return;

            isLeavingMatch = false;
            lobbyReturnRequestFailed = false;
            SetLobbyReturnSourceButtonsInteractable(true);
            SetLeaveMatchConfirmationVisible(false);
            RefreshMatchTransitionOverlay();
        }

        private void SetLobbyReturnSourceButtonsInteractable(
            bool interactable)
        {
            if (leaveMatchButton != null)
                leaveMatchButton.interactable = interactable;

            if (confirmLeaveMatchButton != null)
                confirmLeaveMatchButton.interactable = interactable;

            if (cancelLeaveMatchButton != null)
                cancelLeaveMatchButton.interactable = interactable;

            if (returnToLobbyButton != null &&
                returnToLobbyButton.gameObject.activeSelf)
            {
                returnToLobbyButton.interactable = interactable;
            }
        }

        private void OnHelpControlsTabClicked()
        {
            ShowHelpPage(true);
        }

        private void OnHelpRulesTabClicked()
        {
            ShowHelpPage(false);
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
            if (!visible)
                SetLeaveMatchConfirmationVisible(false);

            if (helpPanel == null ||
                helpPanel.activeSelf == visible)
            {
                return;
            }

            if (visible)
            {
                PrepareHelpPanelOverlay();

                if (morningChat != null &&
                    morningChat.inputField != null)
                {
                    morningChat.inputField.DeactivateInputField();
                }

                if (nightChat != null &&
                    nightChat.inputField != null)
                {
                    nightChat.inputField.DeactivateInputField();
                }

                if (nightChatInputSelected)
                    SetNightChatInputActive(false);

                helpPanel.SetActive(true);
                helpPanel.transform.SetAsLastSibling();
                ShowHelpPage(true);
                SetHelpGameplayInputBlocked(true);

                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }

            helpPanel.SetActive(false);
            SetHelpGameplayInputBlocked(false);
            StartCoroutine(RestoreGameplayCursorAfterHelpClosed());
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

        private void EnsurePersonalSettingsUI()
        {
            ApplySavedPersonalSettings();
            RefreshPersonalSettingsUI();
            RefreshVoiceParticipantRows();

            if (helpAudioPage != null)
                helpAudioPage.SetActive(false);

            if (helpGraphicsPage != null)
                helpGraphicsPage.SetActive(false);
        }


        private void RefreshVoiceParticipantRows()
        {
            if (voiceConnectionStatusText != null)
            {
                voiceConnectionStatusText.SetText(
                    vivoxVoiceManager == null
                        ? "음성 서비스: 준비 중"
                        : vivoxVoiceManager.IsReady
                            ? "음성 서비스: 연결됨"
                            : "음성 서비스: 연결 중"
                );
            }

            if (voiceParticipantButtons == null ||
                voiceParticipantRowTexts == null)
            {
                return;
            }

            int rowCount = Mathf.Min(
                voiceParticipantButtons.Length,
                voiceParticipantRowTexts.Length
            );

            for (int i = 0; i < rowCount; i++)
            {
                if (voiceParticipantButtons[i] == null)
                    continue;

                voiceParticipantButtons[i].onClick.RemoveAllListeners();
                voiceParticipantButtons[i].gameObject.SetActive(false);
            }

            if (matchManager == null ||
                matchManager.PublicPlayerStates == null)
            {
                return;
            }

            ulong localClientId = NetworkManager.Singleton != null
                ? NetworkManager.Singleton.LocalClientId
                : ulong.MaxValue;
            int rowIndex = 0;

            for (int i = 0;
                 i < matchManager.PublicPlayerStates.Count &&
                 rowIndex < rowCount;
                 i++)
            {
                PlayerPublicState state =
                    matchManager.PublicPlayerStates[i];

                if (state.clientId == localClientId)
                    continue;

                string playerName = state.playerName.ToString();
                bool connected =
                    vivoxVoiceManager != null &&
                    vivoxVoiceManager.IsPlayerVoiceConnected(playerName);
                bool muted =
                    vivoxVoiceManager != null &&
                    vivoxVoiceManager.IsPlayerLocallyMuted(playerName);
                Button rowButton = voiceParticipantButtons[rowIndex];
                TextMeshProUGUI rowText =
                    voiceParticipantRowTexts[rowIndex];

                if (rowButton == null || rowText == null)
                {
                    rowIndex++;
                    continue;
                }

                rowButton.gameObject.SetActive(true);
                rowButton.interactable = vivoxVoiceManager != null;
                rowText.richText = false;
                rowText.color = connected
                    ? new Color(0.8f, 0.9f, 0.85f, 1f)
                    : new Color(0.65f, 0.65f, 0.65f, 1f);
                rowText.SetText(
                    $"{playerName} · " +
                    $"{(connected ? "연결됨" : "대기")}" +
                    $"        {(muted ? "음소거 해제" : "음소거")}"
                );

                string capturedPlayerName = playerName;
                rowButton.onClick.AddListener(() =>
                {
                    if (vivoxVoiceManager == null)
                        return;

                    bool currentlyMuted =
                        vivoxVoiceManager.IsPlayerLocallyMuted(
                            capturedPlayerName
                        );
                    vivoxVoiceManager.SetPlayerLocallyMuted(
                        capturedPlayerName,
                        !currentlyMuted
                    );
                });

                rowIndex++;
            }
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
            float sensitivity = PlayerPrefs.GetFloat(
                SpiritFirstPersonCamera.MouseSensitivityPreferenceKey,
                SpiritFirstPersonCamera.LocalInstance != null
                    ? SpiritFirstPersonCamera.LocalInstance.MouseSensitivity
                    : 0.12f
            );
            sensitivity += 0.02f;

            if (sensitivity > 0.401f)
                sensitivity = 0.04f;

            PlayerPrefs.SetFloat(
                SpiritFirstPersonCamera.MouseSensitivityPreferenceKey,
                sensitivity
            );
            PlayerPrefs.Save();

            if (SpiritFirstPersonCamera.LocalInstance != null)
            {
                SpiritFirstPersonCamera.LocalInstance
                    .SetMouseSensitivity(sensitivity);
            }

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
            TryBindVivoxVoiceManager();

            int volume = vivoxVoiceManager != null
                ? vivoxVoiceManager.OutputVolume
                : PlayerPrefs.GetInt(
                    VivoxVoiceManager.VoiceVolumePreferenceKey,
                    0
                );
            volume += 10;

            if (volume > 50)
                volume = -50;

            if (vivoxVoiceManager != null)
            {
                vivoxVoiceManager.SetOutputVolume(volume);
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
            TryBindVivoxVoiceManager();
            bool enabled = !(vivoxVoiceManager != null
                ? vivoxVoiceManager.MicrophoneEnabled
                : PlayerPrefs.GetInt(
                    VivoxVoiceManager.MicrophonePreferenceKey,
                    1
                ) != 0);

            if (vivoxVoiceManager != null)
                vivoxVoiceManager.SetMicrophoneEnabled(enabled);
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
            TryBindVivoxVoiceManager();
            bool enabled = !(vivoxVoiceManager != null
                ? vivoxVoiceManager.VoiceOutputEnabled
                : PlayerPrefs.GetInt(
                    VivoxVoiceManager.VoiceOutputPreferenceKey,
                    1
                ) != 0);

            if (vivoxVoiceManager != null)
                vivoxVoiceManager.SetVoiceOutputEnabled(enabled);
            else
                PlayerPrefs.SetInt(
                    VivoxVoiceManager.VoiceOutputPreferenceKey,
                    enabled ? 1 : 0
                );

            PlayerPrefs.Save();
            RefreshPersonalSettingsUI();
        }

        private void CycleFullScreenMode()
        {
            FullScreenMode mode;

            switch (Screen.fullScreenMode)
            {
                case FullScreenMode.FullScreenWindow:
                    mode = FullScreenMode.Windowed;
                    break;
                case FullScreenMode.Windowed:
                    mode = FullScreenMode.ExclusiveFullScreen;
                    break;
                default:
                    mode = FullScreenMode.FullScreenWindow;
                    break;
            }

            Screen.SetResolution(
                Screen.width,
                Screen.height,
                mode
            );
            PlayerPrefs.SetInt(
                FullScreenModePreferenceKey,
                (int)mode
            );
            PlayerPrefs.Save();
            RefreshPersonalSettingsUI();
        }

        private void CycleResolution()
        {
            Resolution[] resolutions = Screen.resolutions;

            if (resolutions == null || resolutions.Length == 0)
                return;

            int currentIndex = 0;

            for (int i = 0; i < resolutions.Length; i++)
            {
                if (resolutions[i].width == Screen.width &&
                    resolutions[i].height == Screen.height)
                {
                    currentIndex = i;
                }
            }

            Resolution next = resolutions[
                (currentIndex + 1) % resolutions.Length
            ];
            Screen.SetResolution(
                next.width,
                next.height,
                Screen.fullScreenMode
            );
            PlayerPrefs.SetInt(
                ResolutionWidthPreferenceKey,
                next.width
            );
            PlayerPrefs.SetInt(
                ResolutionHeightPreferenceKey,
                next.height
            );
            PlayerPrefs.Save();
            RefreshPersonalSettingsUI();
        }

        private void CycleQualityLevel()
        {
            int qualityCount = QualitySettings.names.Length;

            if (qualityCount == 0)
                return;

            int nextLevel =
                (QualitySettings.GetQualityLevel() + 1) % qualityCount;
            QualitySettings.SetQualityLevel(nextLevel, true);
            PlayerPrefs.SetInt(
                QualityLevelPreferenceKey,
                nextLevel
            );
            PlayerPrefs.Save();
            RefreshPersonalSettingsUI();
        }

        private void CycleTargetFrameRate()
        {
            RelayConnectionManager relayManager =
                RelayConnectionManager.Instance;
            int current = relayManager != null
                ? relayManager.TargetFrameRate
                : PlayerPrefs.GetInt(
                    RelayConnectionManager.TargetFrameRatePreferenceKey,
                    60
                );
            int index = Array.IndexOf(FrameRateChoices, current);
            int next = FrameRateChoices[
                (Mathf.Max(-1, index) + 1) %
                FrameRateChoices.Length
            ];

            if (relayManager != null)
                relayManager.SetTargetFrameRate(next);
            else
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = next;
                PlayerPrefs.SetInt(
                    RelayConnectionManager.TargetFrameRatePreferenceKey,
                    next
                );
                PlayerPrefs.Save();
            }

            RefreshPersonalSettingsUI();
        }

        private void CycleScreenEffectIntensity()
        {
            bool useReducedEffects =
                !SpiritHitReceiver.UseReducedScreenEffects;

            SpiritHitReceiver.SetReducedScreenEffects(
                useReducedEffects
            );
            RefreshPersonalSettingsUI();
        }

        private void RefreshPersonalSettingsUI()
        {
            if (personalSettingValueTexts.Count < 10)
                return;

            float sensitivity = PlayerPrefs.GetFloat(
                SpiritFirstPersonCamera.MouseSensitivityPreferenceKey,
                SpiritFirstPersonCamera.LocalInstance != null
                    ? SpiritFirstPersonCamera.LocalInstance.MouseSensitivity
                    : 0.12f
            );
            int voiceVolume = vivoxVoiceManager != null
                ? vivoxVoiceManager.OutputVolume
                : PlayerPrefs.GetInt(
                    VivoxVoiceManager.VoiceVolumePreferenceKey,
                    0
                );
            bool microphoneEnabled = vivoxVoiceManager != null
                ? vivoxVoiceManager.MicrophoneEnabled
                : PlayerPrefs.GetInt(
                    VivoxVoiceManager.MicrophonePreferenceKey,
                    1
                ) != 0;
            bool voiceOutputEnabled = vivoxVoiceManager != null
                ? vivoxVoiceManager.VoiceOutputEnabled
                : PlayerPrefs.GetInt(
                    VivoxVoiceManager.VoiceOutputPreferenceKey,
                    1
                ) != 0;
            string qualityName = QualitySettings.names.Length > 0
                ? QualitySettings.names[
                    Mathf.Clamp(
                        QualitySettings.GetQualityLevel(),
                        0,
                        QualitySettings.names.Length - 1
                    )
                ]
                : "기본";
            int frameRate = RelayConnectionManager.Instance != null
                ? RelayConnectionManager.Instance.TargetFrameRate
                : Application.targetFrameRate;

            personalSettingValueTexts[0].SetText(
                $"마우스 감도  |  {sensitivity:0.00}"
            );
            personalSettingValueTexts[1].SetText(
                "게임 음량  |  " +
                $"{Mathf.RoundToInt(AudioListener.volume * 100f)}%"
            );
            personalSettingValueTexts[2].SetText(
                $"음성 채팅 음량  |  {voiceVolume + 50}%"
            );
            personalSettingValueTexts[3].SetText(
                "마이크  |  " +
                (microphoneEnabled ? "켜짐" : "꺼짐")
            );
            personalSettingValueTexts[4].SetText(
                "음성 수신  |  " +
                (voiceOutputEnabled ? "켜짐" : "꺼짐")
            );
            personalSettingValueTexts[5].SetText(
                "화면 모드  |  " +
                GetFullScreenModeLabel(Screen.fullScreenMode)
            );
            personalSettingValueTexts[6].SetText(
                $"해상도  |  {Screen.width} x {Screen.height}"
            );
            personalSettingValueTexts[7].SetText(
                $"그래픽 품질  |  {qualityName}"
            );
            personalSettingValueTexts[8].SetText(
                "FPS 제한  |  " +
                (frameRate < 0
                    ? "제한 없음"
                    : $"{frameRate} FPS")
            );
            personalSettingValueTexts[9].SetText(
                "화면 효과  |  " +
                (SpiritHitReceiver.UseReducedScreenEffects
                    ? "낮음"
                    : "보통")
            );
        }

        private static string GetFullScreenModeLabel(
            FullScreenMode mode)
        {
            switch (mode)
            {
                case FullScreenMode.ExclusiveFullScreen:
                    return "전체 화면";
                case FullScreenMode.Windowed:
                    return "창 모드";
                default:
                    return "테두리 없는 창";
            }
        }

        private void EnsureLeaveMatchButton()
        {
            if (leaveMatchButton != null ||
                closeHelpButton == null)
            {
                return;
            }

            GameObject buttonObject =
                Instantiate(
                    closeHelpButton.gameObject,
                    closeHelpButton.transform.parent
                );

            buttonObject.name = "LeaveMatchButton";
            leaveMatchButton = buttonObject.GetComponent<Button>();

            if (leaveMatchButton == null)
            {
                Destroy(buttonObject);
                return;
            }

            RectTransform closeRect =
                closeHelpButton.GetComponent<RectTransform>();
            RectTransform leaveRect =
                leaveMatchButton.GetComponent<RectTransform>();

            if (closeRect != null && leaveRect != null)
            {
                float spacing = Mathf.Max(
                    150f,
                    closeRect.rect.width * 0.6f
                );
                Vector2 originalPosition =
                    closeRect.anchoredPosition;

                closeRect.anchoredPosition =
                    originalPosition + Vector2.right * spacing;
                leaveRect.anchoredPosition =
                    originalPosition + Vector2.left * spacing;
            }

            TextMeshProUGUI buttonText =
                leaveMatchButton.GetComponentInChildren<TextMeshProUGUI>(
                    true
                );

            if (buttonText != null)
                buttonText.SetText("로비로 돌아가기");
        }

        private void EnsureLeaveMatchConfirmation()
        {
            if (leaveMatchConfirmationPanel != null ||
                helpPanel == null ||
                closeHelpButton == null)
            {
                return;
            }

            leaveMatchConfirmationPanel =
                new GameObject(
                    "LeaveMatchConfirmationPanel",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image)
                );

            leaveMatchConfirmationPanel.layer =
                helpPanel.layer;
            leaveMatchConfirmationPanel.transform.SetParent(
                helpPanel.transform,
                false
            );

            RectTransform overlayRect =
                leaveMatchConfirmationPanel
                    .GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            Image overlayImage =
                leaveMatchConfirmationPanel
                    .GetComponent<Image>();
            overlayImage.color =
                new Color(0f, 0f, 0f, 0.72f);
            overlayImage.raycastTarget = true;

            GameObject windowObject =
                new GameObject(
                    "ConfirmationWindow",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image)
                );

            windowObject.layer = helpPanel.layer;
            windowObject.transform.SetParent(
                leaveMatchConfirmationPanel.transform,
                false
            );

            RectTransform windowRect =
                windowObject.GetComponent<RectTransform>();
            windowRect.anchorMin = new Vector2(0.5f, 0.5f);
            windowRect.anchorMax = new Vector2(0.5f, 0.5f);
            windowRect.anchoredPosition = Vector2.zero;
            windowRect.sizeDelta = new Vector2(680f, 280f);

            Image windowImage =
                windowObject.GetComponent<Image>();
            windowImage.color =
                new Color(0.07f, 0.07f, 0.07f, 0.99f);
            windowImage.raycastTarget = true;

            if (closeHelpButton.targetGraphic is Image buttonImage)
            {
                windowImage.sprite = buttonImage.sprite;
                windowImage.type = buttonImage.type;
                windowImage.material = buttonImage.material;
            }

            TextMeshProUGUI textTemplate =
                closeHelpButton.GetComponentInChildren
                    <TextMeshProUGUI>(true);

            if (textTemplate != null)
            {
                GameObject messageObject =
                    Instantiate(
                        textTemplate.gameObject,
                        windowRect
                    );

                messageObject.name = "ConfirmationText";

                RectTransform messageRect =
                    messageObject.GetComponent<RectTransform>();
                messageRect.anchorMin =
                    new Vector2(0.5f, 0.5f);
                messageRect.anchorMax =
                    new Vector2(0.5f, 0.5f);
                messageRect.anchoredPosition =
                    new Vector2(0f, 55f);
                messageRect.sizeDelta =
                    new Vector2(580f, 100f);

                TextMeshProUGUI messageText =
                    messageObject.GetComponent<TextMeshProUGUI>();
                messageText.SetText(
                    "로비로 돌아가시겠습니까?"
                );
                messageText.fontSize = 32f;
                messageText.fontStyle = FontStyles.Bold;
                messageText.alignment =
                    TextAlignmentOptions.Center;
                messageText.richText = false;
            }

            confirmLeaveMatchButton =
                CreateLeaveConfirmationButton(
                    leaveMatchButton != null
                        ? leaveMatchButton
                        : closeHelpButton,
                    windowRect,
                    "ConfirmLeaveMatchButton",
                    "예",
                    new Vector2(-120f, -75f)
                );

            cancelLeaveMatchButton =
                CreateLeaveConfirmationButton(
                    closeHelpButton,
                    windowRect,
                    "CancelLeaveMatchButton",
                    "아니오",
                    new Vector2(120f, -75f)
                );

            leaveMatchConfirmationPanel.SetActive(false);
        }

        private static Button CreateLeaveConfirmationButton(
            Button template,
            RectTransform parent,
            string objectName,
            string label,
            Vector2 anchoredPosition)
        {
            if (template == null || parent == null)
                return null;

            GameObject buttonObject =
                Instantiate(
                    template.gameObject,
                    parent
                );

            buttonObject.name = objectName;

            RectTransform buttonRect =
                buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin =
                new Vector2(0.5f, 0.5f);
            buttonRect.anchorMax =
                new Vector2(0.5f, 0.5f);
            buttonRect.anchoredPosition = anchoredPosition;
            buttonRect.sizeDelta = new Vector2(180f, 60f);

            Button button = buttonObject.GetComponent<Button>();

            if (button == null)
            {
                Destroy(buttonObject);
                return null;
            }

            button.onClick.RemoveAllListeners();

            TextMeshProUGUI buttonText =
                button.GetComponentInChildren
                    <TextMeshProUGUI>(true);

            if (buttonText != null)
                buttonText.SetText(label);

            return button;
        }

        private void SetLeaveMatchConfirmationVisible(
            bool visible)
        {
            if (leaveMatchConfirmationPanel == null)
                EnsureLeaveMatchConfirmation();

            if (leaveMatchConfirmationPanel == null)
                return;

            if (visible)
            {
                if (leaveMatchButton != null)
                    leaveMatchButton.interactable = true;

                if (confirmLeaveMatchButton != null)
                    confirmLeaveMatchButton.interactable = true;

                if (cancelLeaveMatchButton != null)
                    cancelLeaveMatchButton.interactable = true;

                leaveMatchConfirmationPanel.SetActive(true);
                leaveMatchConfirmationPanel.transform
                    .SetAsLastSibling();
                return;
            }

            leaveMatchConfirmationPanel.SetActive(false);
        }

        private void ShowHelpPage(bool showControls)
        {
            ShowHelpPage(
                showControls
                    ? HelpPage.Controls
                    : HelpPage.Rules
            );
        }

        private void ShowHelpPage(HelpPage page)
        {
            if (helpControlsPage != null)
                helpControlsPage.SetActive(page == HelpPage.Controls);

            if (helpRulesPage != null)
            {
                helpRulesPage.SetActive(page == HelpPage.Rules);

                if (page == HelpPage.Rules && helpRulesPage.TryGetComponent(out ScrollRect rulesScrollRect))
                    rulesScrollRect.verticalNormalizedPosition = 1f;
            }

            if (helpAudioPage != null)
            {
                helpAudioPage.SetActive(page == HelpPage.Audio);

                if (page == HelpPage.Audio)
                {
                    RefreshPersonalSettingsUI();
                    RefreshVoiceParticipantRows();
                }
            }

            if (helpGraphicsPage != null)
            {
                helpGraphicsPage.SetActive(page == HelpPage.Graphics);

                if (page == HelpPage.Graphics)
                    RefreshPersonalSettingsUI();
            }

            if (helpControlsTabButton != null)
            {
                helpControlsTabButton.interactable =
                    page != HelpPage.Controls;
            }

            if (helpRulesTabButton != null)
            {
                helpRulesTabButton.interactable =
                    page != HelpPage.Rules;
            }

            if (helpAudioTabButton != null)
            {
                helpAudioTabButton.interactable =
                    page != HelpPage.Audio;
            }

            if (helpGraphicsTabButton != null)
            {
                helpGraphicsTabButton.interactable =
                    page != HelpPage.Graphics;
            }
        }

        private void SetHelpGameplayInputBlocked(bool blocked)
        {
            if (!blocked)
            {
                RestoreHelpBlockedBehaviours();
                return;
            }

            SpiritFirstPersonCamera localCamera =
                SpiritFirstPersonCamera.LocalInstance;

            if (localCamera == null)
                return;

            CacheAndDisableHelpBehaviour(
                localCamera.GetComponent<PlayerSpiritMovement>()
            );
            CacheAndDisableHelpBehaviour(
                localCamera.GetComponent<SpiritDoorInteraction>()
            );
            CacheAndDisableHelpBehaviour(
                localCamera.GetComponent<SpiritLampInteraction>()
            );
            CacheAndDisableHelpBehaviour(
                localCamera.GetComponent<SpiritRoleActionInteractor>()
            );
            CacheAndDisableHelpBehaviour(
                localCamera.GetComponent<SpiritWeaponSwing>()
            );
            CacheAndDisableHelpBehaviour(localCamera);
        }

        private void CacheAndDisableHelpBehaviour(
            Behaviour behaviour)
        {
            if (behaviour == null ||
                helpBlockedBehaviourStates.ContainsKey(behaviour))
            {
                return;
            }

            helpBlockedBehaviourStates[behaviour] = behaviour.enabled;
            behaviour.enabled = false;
        }

        private void RestoreHelpBlockedBehaviours()
        {
            foreach (KeyValuePair<Behaviour, bool> pair in
                     helpBlockedBehaviourStates)
            {
                if (pair.Key != null)
                    pair.Key.enabled = pair.Value;
            }

            helpBlockedBehaviourStates.Clear();
        }

        private IEnumerator RestoreGameplayCursorAfterHelpClosed()
        {
            yield return null;

            if (IsHelpPanelOpen || matchManager == null)
                yield break;

            ApplyInputMode(matchManager.CurrentPhase);

            if (matchManager.CurrentPhase != MatchPhase.NightAction &&
                matchManager.CurrentPhase != MatchPhase.FinalDuel)
            {
                yield break;
            }

            SpiritFirstPersonCamera localCamera =
                SpiritFirstPersonCamera.LocalInstance;

            if (localCamera != null)
            {
                localCamera.SendMessage(
                    "LockCursor",
                    SendMessageOptions.DontRequireReceiver
                );
            }
        }

        private void InitializeTextChatUI()
        {
            morningChat = CacheTextChatPanel(
                chatPanel,
                chatMessagePrefab
            );

            if (nightChatPanel == null &&
                chatPanel != null)
            {
                nightChatPanel =
                    Instantiate(
                        chatPanel,
                        transform
                    );

                nightChatPanel.name =
                    "NightChatPanel";

                RectTransform rectTransform =
                    nightChatPanel.transform as RectTransform;

                if (rectTransform != null)
                {
                    rectTransform.anchorMin =
                        Vector2.zero;

                    rectTransform.anchorMax =
                        Vector2.zero;

                    rectTransform.pivot =
                        Vector2.zero;

                    rectTransform.anchoredPosition =
                        new Vector2(24f, 24f);

                    rectTransform.sizeDelta =
                        new Vector2(1200f, 650f);

                    rectTransform.localScale =
                        Vector3.one * 0.5f;
                }

                nightChatPanel.transform
                    .SetAsLastSibling();
            }

            nightChat = CacheTextChatPanel(
                nightChatPanel,
                chatMessagePrefab
            );

            if (nightChatPanel != null)
                nightChatPanel.SetActive(false);

            ConfigureChatPanel(morningChat);
            ConfigureChatPanel(nightChat);

            if ((morningChat == null ||
                 morningChat.messageTemplate == null) &&
                (nightChat == null ||
                 nightChat.messageTemplate == null))
            {
                Debug.LogError(
                    "채팅 메시지 프리팹이 연결되지 않았습니다. " +
                    "MafiaUIController의 Chat Message Prefab을 확인하십시오."
                );
            }

            ClearChatMessages(morningChat);
            ClearChatMessages(nightChat);
        }

        private static ChatPanelReferences
            CacheTextChatPanel(
                GameObject root,
                GameObject messagePrefab)
        {
            if (root == null)
                return null;

            ChatPanelReferences panel =
                new ChatPanelReferences
                {
                    root = root,
                    inputField = FindComponentByName
                        <TMP_InputField>(
                            root.transform,
                            "ChatInputField"
                        ),
                    sendButton = FindComponentByName
                        <Button>(
                            root.transform,
                            "SendButton"
                        ),
                    messageTemplate = messagePrefab,
                    scrollRect = FindComponentByName
                        <ScrollRect>(
                            root.transform,
                            "ChatScrollRect"
                        )
                };

            if (panel.scrollRect != null)
            {
                panel.content =
                    panel.scrollRect.content;
            }

            if (panel.messageTemplate == null &&
                panel.content != null &&
                panel.content.childCount > 0)
            {
                panel.messageTemplate =
                    panel.content.GetChild(0)
                        .gameObject;
            }

            Transform header = FindTransformByName(
                root.transform,
                "ChatHeader"
            );

            if (header != null)
            {
                panel.titleText =
                    FindComponentByName
                        <TextMeshProUGUI>(
                            header,
                            "Title"
                        );
            }

            return panel;
        }

        private static T FindComponentByName<T>(
            Transform root,
            string objectName)
            where T : Component
        {
            Transform target = FindTransformByName(
                root,
                objectName
            );

            return target != null
                ? target.GetComponent<T>()
                : null;
        }

        private static Transform FindTransformByName(
            Transform root,
            string objectName)
        {
            if (root == null)
                return null;

            if (string.Equals(
                    root.name,
                    objectName,
                    StringComparison.Ordinal))
            {
                return root;
            }

            for (int i = 0;
                 i < root.childCount;
                 i++)
            {
                Transform found =
                    FindTransformByName(
                        root.GetChild(i),
                        objectName
                    );

                if (found != null)
                    return found;
            }

            return null;
        }

        private static void ConfigureChatPanel(
            ChatPanelReferences panel)
        {
            if (panel == null)
                return;

            if (panel.content != null)
            {
                VerticalLayoutGroup messageLayout =
                    panel.content.GetComponent
                        <VerticalLayoutGroup>();

                if (messageLayout != null)
                {
                    messageLayout.childControlWidth = true;
                    messageLayout.childForceExpandWidth = true;
                    messageLayout.childControlHeight = true;
                    messageLayout.childForceExpandHeight = false;
                }
            }

            if (panel.inputField != null)
            {
                panel.inputField.characterLimit = 120;
                panel.inputField.lineType =
                    TMP_InputField.LineType.SingleLine;
                panel.inputField.richText = false;
            }

            if (panel.messageTemplate != null &&
                panel.messageTemplate.scene.IsValid())
            {
                panel.messageTemplate.SetActive(false);
            }
        }

        private void RegisterTextChatEvents(
            ChatPanelReferences panel,
            bool isNightChat)
        {
            if (panel == null)
                return;

            if (panel.sendButton != null)
            {
                if (isNightChat)
                {
                    panel.sendButton.onClick.AddListener(
                        OnNightChatSendClicked
                    );
                }
                else
                {
                    panel.sendButton.onClick.AddListener(
                        OnMorningChatSendClicked
                    );
                }
            }

            if (panel.inputField == null)
                return;

            if (isNightChat)
            {
                panel.inputField.onSubmit.AddListener(
                    OnNightChatSubmitted
                );

                panel.inputField.onSelect.AddListener(
                    OnNightChatInputSelected
                );

                panel.inputField.onDeselect.AddListener(
                    OnNightChatInputDeselected
                );
            }
            else
            {
                panel.inputField.onSubmit.AddListener(
                    OnMorningChatSubmitted
                );
            }
        }

        private void UnregisterTextChatEvents(
            ChatPanelReferences panel,
            bool isNightChat)
        {
            if (panel == null)
                return;

            if (panel.sendButton != null)
            {
                if (isNightChat)
                {
                    panel.sendButton.onClick.RemoveListener(
                        OnNightChatSendClicked
                    );
                }
                else
                {
                    panel.sendButton.onClick.RemoveListener(
                        OnMorningChatSendClicked
                    );
                }
            }

            if (panel.inputField == null)
                return;

            if (isNightChat)
            {
                panel.inputField.onSubmit.RemoveListener(
                    OnNightChatSubmitted
                );

                panel.inputField.onSelect.RemoveListener(
                    OnNightChatInputSelected
                );

                panel.inputField.onDeselect.RemoveListener(
                    OnNightChatInputDeselected
                );
            }
            else
            {
                panel.inputField.onSubmit.RemoveListener(
                    OnMorningChatSubmitted
                );
            }
        }

        private void OnMorningChatSendClicked()
        {
            SubmitChatInput(
                morningChat,
                TextChatChannel.MorningPublic,
                false
            );
        }

        private void OnNightChatSendClicked()
        {
            if (!TryGetLocalNightChatChannel(
                    out TextChatChannel channel))
            {
                return;
            }

            SubmitChatInput(
                nightChat,
                channel,
                true
            );
        }

        private void OnMorningChatSubmitted(
            string value)
        {
            OnMorningChatSendClicked();
        }

        private void OnNightChatSubmitted(
            string value)
        {
            OnNightChatSendClicked();
        }

        private void SubmitChatInput(
            ChatPanelReferences panel,
            TextChatChannel channel,
            bool closeAfterSend)
        {
            if (panel == null ||
                panel.inputField == null ||
                matchManager == null)
            {
                return;
            }

            string message = panel.inputField.text;

            if (string.IsNullOrWhiteSpace(message))
                return;

            matchManager.SubmitTextChatMessage(
                channel,
                message
            );

            panel.inputField.text = string.Empty;

            if (closeAfterSend)
            {
                panel.inputField.DeactivateInputField();
                SetNightChatInputActive(false);
            }
            else
            {
                panel.inputField.ActivateInputField();
            }
        }

        private void OnNightChatInputSelected(
            string value)
        {
            SetNightChatInputActive(true);
        }

        private void OnNightChatInputDeselected(
            string value)
        {
            StartCoroutine(
                FinishNightChatInputDeselection()
            );
        }

        private IEnumerator FinishNightChatInputDeselection()
        {
            yield return null;

            bool nightFocused =
                nightChat != null &&
                nightChat.inputField != null &&
                nightChat.inputField.isFocused;

            if (!nightFocused)
                SetNightChatInputActive(false);
        }

        private void SetNightChatInputActive(bool active)
        {
            if (nightChatInputSelected == active)
            {
                if (!active &&
                    nightChatBlockedBehaviourStates.Count > 0)
                {
                    SetNightChatGameplayInputBlocked(
                        SpiritFirstPersonCamera.LocalInstance,
                        false
                    );
                }

                return;
            }

            nightChatInputSelected = active;

            SpiritFirstPersonCamera localCamera =
                SpiritFirstPersonCamera.LocalInstance;

            if (localCamera != null)
            {
                bool shouldLockForGameplay =
                    !active &&
                    matchManager != null &&
                    (matchManager.CurrentPhase ==
                         MatchPhase.NightAction ||
                     matchManager.CurrentPhase ==
                         MatchPhase.FinalDuel);

                if (active || shouldLockForGameplay)
                {
                    localCamera.SendMessage(
                        active
                            ? "UnlockCursor"
                            : "LockCursor",
                        SendMessageOptions
                            .DontRequireReceiver
                    );
                }
            }

            SetNightChatGameplayInputBlocked(
                localCamera,
                active
            );
        }

        private void SetNightChatGameplayInputBlocked(
            SpiritFirstPersonCamera localCamera,
            bool blocked)
        {
            if (!blocked)
            {
                foreach (KeyValuePair<Behaviour, bool> pair in
                         nightChatBlockedBehaviourStates)
                {
                    if (pair.Key != null)
                        pair.Key.enabled = pair.Value;
                }

                nightChatBlockedBehaviourStates.Clear();
                return;
            }

            if (localCamera == null)
                return;

            CacheAndDisableNightChatBehaviour(
                localCamera.GetComponent
                    <PlayerSpiritMovement>()
            );

            CacheAndDisableNightChatBehaviour(
                localCamera.GetComponent
                    <SpiritDoorInteraction>()
            );

            CacheAndDisableNightChatBehaviour(
                localCamera.GetComponent
                    <SpiritLampInteraction>()
            );

            CacheAndDisableNightChatBehaviour(
                localCamera.GetComponent
                    <SpiritRoleActionInteractor>()
            );

            CacheAndDisableNightChatBehaviour(
                localCamera.GetComponent
                    <SpiritWeaponSwing>()
            );
        }

        private void CacheAndDisableNightChatBehaviour(
            Behaviour behaviour)
        {
            if (behaviour == null ||
                nightChatBlockedBehaviourStates
                    .ContainsKey(behaviour))
            {
                return;
            }

            nightChatBlockedBehaviourStates[
                behaviour
            ] = behaviour.enabled;

            behaviour.enabled = false;
        }

        private void UpdateTextChatInput()
        {
            Keyboard keyboard = Keyboard.current;

            if (keyboard == null)
                return;

            if (nightChatInputSelected &&
                keyboard.escapeKey.wasPressedThisFrame)
            {
                if (nightChat != null &&
                    nightChat.inputField != null)
                {
                    nightChat.inputField
                        .DeactivateInputField();
                }


                SetNightChatInputActive(false);
                return;
            }

            if (!keyboard.enterKey.wasPressedThisFrame ||
                IsAnyTextChatInputFocused())
            {
                return;
            }

            if (nightChat != null &&
                nightChat.root != null &&
                nightChat.root.activeInHierarchy &&
                nightChat.inputField != null &&
                nightChat.inputField.interactable)
            {
                SetNightChatInputActive(true);
                nightChat.inputField.Select();
                nightChat.inputField.ActivateInputField();
                return;
            }


            if (morningChat != null &&
                morningChat.root != null &&
                morningChat.root.activeInHierarchy &&
                morningChat.inputField != null &&
                morningChat.inputField.interactable)
            {
                morningChat.inputField.Select();
                morningChat.inputField.ActivateInputField();
            }
        }

        private bool IsAnyTextChatInputFocused()
        {
            return morningChat != null &&
                   morningChat.inputField != null &&
                   morningChat.inputField.isFocused ||
                   nightChat != null &&
                   nightChat.inputField != null &&
                   nightChat.inputField.isFocused;
        }

        private void OnLocalTextChatMessageReceived(
            TextChatChannel channel,
            ulong senderClientId,
            string senderName,
            string message)
        {
            ChatPanelReferences panel;

            if (channel == TextChatChannel.MorningPublic)
                panel = morningChat;
            else
                panel = nightChat;

            AddChatMessage(
                panel,
                senderClientId,
                senderName,
                message
            );

            bool isLocalSender =
                NetworkManager.Singleton != null &&
                NetworkManager.Singleton.LocalClientId ==
                    senderClientId;

            if (channel == TextChatChannel.MorningPublic &&
                !isLocalSender &&
                (chatPanel == null ||
                 !chatPanel.activeInHierarchy))
            {
                hasUnreadMorningChatMessages = true;
                RefreshMorningChatUnreadIndicator();
            }
        }

        private void AddChatMessage(
            ChatPanelReferences panel,
            ulong senderClientId,
            string senderName,
            string message)
        {
            if (panel == null ||
                panel.content == null ||
                panel.messageTemplate == null)
            {
                return;
            }

            GameObject messageObject =
                Instantiate(
                    panel.messageTemplate,
                    panel.content
                );

            messageObject.name = "ChatMessage";
            messageObject.SetActive(true);

            PlayerIdentityLabel identityLabel =
                messageObject.GetComponent
                    <PlayerIdentityLabel>();

            if (identityLabel != null)
            {
                identityLabel.SetPlayer(
                    senderClientId,
                    senderName
                );
            }

            TextMeshProUGUI messageText =
                FindComponentByName
                    <TextMeshProUGUI>(
                        messageObject.transform,
                        "MessageText"
                    );

            if (messageText != null)
            {
                messageText.richText = false;
                messageText.text = message;
            }

            TextMeshProUGUI timeText =
                FindComponentByName
                    <TextMeshProUGUI>(
                        messageObject.transform,
                        "TimeText"
                    );

            if (timeText != null)
            {
                timeText.text =
                    DateTime.Now.ToString("HH:mm");
            }

            ResizeChatMessageForWrappedText(
                panel,
                messageObject,
                messageText
            );

            panel.messages.Add(messageObject);

            while (panel.messages.Count >
                   maximumVisibleChatMessages)
            {
                GameObject oldestMessage =
                    panel.messages[0];

                panel.messages.RemoveAt(0);

                if (oldestMessage != null)
                    Destroy(oldestMessage);
            }

            Canvas.ForceUpdateCanvases();

            LayoutRebuilder.ForceRebuildLayoutImmediate(
                panel.content
            );

            Canvas.ForceUpdateCanvases();

            if (panel.scrollRect != null)
                panel.scrollRect.verticalNormalizedPosition = 0f;
        }

        private static void ResizeChatMessageForWrappedText(
            ChatPanelReferences panel,
            GameObject messageObject,
            TextMeshProUGUI messageText)
        {
            if (panel == null ||
                panel.content == null ||
                messageObject == null ||
                messageText == null)
            {
                return;
            }

            RectTransform rowRect =
                messageObject.transform as RectTransform;
            RectTransform textRect =
                messageText.rectTransform;

            if (rowRect == null || textRect == null)
                return;

            float defaultRowHeight = rowRect.rect.height;
            float defaultTextHeight = textRect.rect.height;

            LayoutRebuilder.ForceRebuildLayoutImmediate(
                panel.content
            );

            LayoutRebuilder.ForceRebuildLayoutImmediate(
                rowRect
            );

            HorizontalLayoutGroup rowGroup =
                messageObject.GetComponent
                    <HorizontalLayoutGroup>();

            float occupiedWidth = 0f;
            int activeLayoutChildCount = 0;

            if (rowGroup != null)
            {
                occupiedWidth =
                    rowGroup.padding.left +
                    rowGroup.padding.right;

                for (int i = 0;
                     i < rowRect.childCount;
                     i++)
                {
                    RectTransform childRect =
                        rowRect.GetChild(i) as RectTransform;

                    if (childRect == null ||
                        !childRect.gameObject.activeSelf)
                    {
                        continue;
                    }

                    LayoutElement childLayout =
                        childRect.GetComponent<LayoutElement>();

                    if (childLayout != null &&
                        childLayout.ignoreLayout)
                    {
                        continue;
                    }

                    activeLayoutChildCount++;

                    if (childRect != textRect)
                    {
                        occupiedWidth +=
                            Mathf.Max(0f, childRect.rect.width);
                    }
                }

                occupiedWidth +=
                    Mathf.Max(0, activeLayoutChildCount - 1) *
                    rowGroup.spacing;
            }

            float availableWidth = Mathf.Max(
                1f,
                rowRect.rect.width - occupiedWidth
            );

            textRect.SetSizeWithCurrentAnchors(
                RectTransform.Axis.Horizontal,
                availableWidth
            );

            messageText.textWrappingMode =
                TextWrappingModes.Normal;

            messageText.ForceMeshUpdate();

            float preferredTextHeight = Mathf.Ceil(
                messageText.preferredHeight
            );

            float resolvedTextHeight = Mathf.Max(
                defaultTextHeight,
                preferredTextHeight
            );
            float verticalPadding = Mathf.Max(
                0f,
                defaultRowHeight - defaultTextHeight
            );

            textRect.SetSizeWithCurrentAnchors(
                RectTransform.Axis.Vertical,
                resolvedTextHeight
            );
            rowRect.SetSizeWithCurrentAnchors(
                RectTransform.Axis.Vertical,
                resolvedTextHeight + verticalPadding
            );

            LayoutElement rowLayout =
                messageObject.GetComponent<LayoutElement>();

            if (rowLayout == null)
            {
                rowLayout =
                    messageObject.AddComponent<LayoutElement>();
            }

            rowLayout.minHeight =
                resolvedTextHeight + verticalPadding;
            rowLayout.preferredHeight =
                resolvedTextHeight + verticalPadding;
            rowLayout.flexibleHeight = 0f;

            LayoutRebuilder.ForceRebuildLayoutImmediate(
                panel.content
            );
        }

        private static void ClearChatMessages(
            ChatPanelReferences panel)
        {
            if (panel == null)
                return;

            for (int i = 0;
                 i < panel.messages.Count;
                 i++)
            {
                if (panel.messages[i] != null)
                    Destroy(panel.messages[i]);
            }

            panel.messages.Clear();

            if (panel.inputField != null)
                panel.inputField.text = string.Empty;
        }

        private void OnLocalMediumCommunicationStartedForTextChat(
            string channelName,
            ulong partnerClientId)
        {
            ClearChatMessages(nightChat);
            RefreshTextChatUI();
        }

        private void OnLocalMediumCommunicationEndedForTextChat()
        {
            SetNightChatInputActive(false);
            RefreshTextChatUI();
        }

        private void RefreshTextChatUI()
        {
            PlayerMatchState localState = default;

            bool hasLocalState =
                matchManager != null &&
                matchManager.TryGetLocalPlayerMatchState(
                    out localState
                );

            bool canSendMorning =
                hasLocalState &&
                localState.isAlive &&
                matchManager.CurrentPhase ==
                    MatchPhase.MorningVote;

            SetChatInputInteractable(
                morningChat,
                canSendMorning
            );

            bool showNightChat =
                TryGetLocalNightChatChannel(
                    out TextChatChannel nightChannel
                );

            if (nightChat != null &&
                nightChat.root != null)
            {
                nightChat.root.SetActive(
                    showNightChat
                );
            }

            SetChatInputInteractable(
                nightChat,
                showNightChat
            );

            if (nightChat != null &&
                nightChat.titleText != null &&
                showNightChat)
            {
                switch (nightChannel)
                {
                    case TextChatChannel.MafiaNight:
                        nightChat.titleText.text =
                            "야간 마녀 채팅";
                        break;

                    case TextChatChannel.MediumNight:
                        nightChat.titleText.text =
                            "영매사 채팅";
                        break;

                    case TextChatChannel.Dead:
                        nightChat.titleText.text =
                            "사망자 채팅";
                        break;
                }
            }

            if (!showNightChat)
                SetNightChatInputActive(false);
        }

        private static void SetChatInputInteractable(
            ChatPanelReferences panel,
            bool interactable)
        {
            if (panel == null)
                return;

            if (panel.inputField != null)
                panel.inputField.interactable = interactable;

            if (panel.sendButton != null)
                panel.sendButton.interactable = interactable;
        }

        private bool TryGetLocalNightChatChannel(
            out TextChatChannel channel)
        {
            channel = TextChatChannel.MafiaNight;

            if (matchManager == null ||
                !matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState))
            {
                return false;
            }

            /*
             * 영매사 교신 중에는 생존 영매사와 선택된
             * 사망자 모두 같은 좌하단 채팅 UI를 영매사
             * 전용 채널로 사용한다. 선택된 사망자는 이
             * 동안 사망자 채팅으로 전환할 수 없다.
             */
            if (matchManager.CurrentPhase ==
                    MatchPhase.NightAction &&
                matchManager.HasLocalMediumCommunication)
            {
                channel = TextChatChannel.MediumNight;
                return true;
            }

            /*
             * 영매사가 아직 교신 대상으로 선택할 수 있는
             * RestrictedDeadSpectator 기간에는 사망자 채팅을
             * 사용할 수 없다. 그렇지 않으면 다른 사망자에게
             * 얻은 정보를 영매사에게 전달할 수 있기 때문이다.
             * 실제 교신이 시작되면 위 분기에서 MediumNight가
             * 활성화되고, 교신 가능 기간이 끝난 뒤부터는
             * 밤/낮 구분 없이 사망자 채팅을 사용한다.
             */
            if (!localState.isAlive &&
                matchManager.CurrentPhase != MatchPhase.None &&
                matchManager.CurrentPhase != MatchPhase.GameResult)
            {
                if (!matchManager.TryGetPlayerSpirit(
                        localState.clientId,
                        out PlayerSpirit localSpirit) ||
                    !localSpirit.IsFreeDeadSpectator)
                {
                    return false;
                }

                channel = TextChatChannel.Dead;
                return true;
            }

            bool isNightPhase =
                matchManager.CurrentPhase ==
                    MatchPhase.NightPreparation ||
                matchManager.CurrentPhase ==
                    MatchPhase.NightAction;

            if (isNightPhase &&
                localState.isAlive &&
                localState.team == RoleTeam.Mafia)
            {
                channel = TextChatChannel.MafiaNight;
                return true;
            }

            return false;
        }

        private void SetMeetingUIVisible(bool visible)
        {
            if (tabSystemRoot != null)
                tabSystemRoot.SetActive(visible);

            if (contentAreaRoot != null)
                contentAreaRoot.SetActive(visible);

            if (tabSystemRoot != null && contentAreaRoot != null)
                return;

            if (chatTabButton != null)
                chatTabButton.gameObject.SetActive(visible);

            if (voteTabButton != null)
                voteTabButton.gameObject.SetActive(visible);

            if (!visible)
            {
                if (chatPanel != null)
                    chatPanel.SetActive(false);

                if (votePanel != null)
                    votePanel.SetActive(false);
            }
        }

        private void SetTabButtonsVisible(bool showChat, bool showVote)
        {
            if (chatTabButton != null)
                chatTabButton.gameObject.SetActive(showChat);

            if (voteTabButton != null)
                voteTabButton.gameObject.SetActive(showVote);
        }

        private void SetVoteControlsVisible(
            bool visible,
            bool showAbstain)
        {
            bool showMorningConfirm =
                visible &&
                currentVoteMode ==
                    VoteMode.MorningExile &&
                !IsLocalDeadSpectator();

            if (confirmVoteButton != null)
            {
                confirmVoteButton.gameObject
                    .SetActive(showMorningConfirm);
            }

            if (abstainButton != null)
            {
                bool abstainVisible =
                    visible && showAbstain;

                abstainButton.gameObject
                    .SetActive(abstainVisible);
            }

            if (voteTimerText != null)
            {
                voteTimerText.gameObject
                    .SetActive(visible);
            }

            UpdateMorningVoteInteractionState();
        }

        private void OnChatTabClicked()
        {
            if (matchManager == null)
                return;

            if (matchManager.CurrentPhase ==
                    MatchPhase.NightPreparation &&
                CanUseMafiaPreparationModes())
            {
                SetNightPreparationVoteMode(
                    VoteMode.MafiaKiller
                );

                return;
            }

            if (matchManager.CurrentPhase != MatchPhase.MorningDiscussion &&
                matchManager.CurrentPhase != MatchPhase.MorningVote)
            {
                return;
            }

            SetTab(true);
        }

        private void OnVoteTabClicked()
        {
            if (matchManager == null)
                return;

            if (matchManager.CurrentPhase ==
                    MatchPhase.NightPreparation &&
                CanUseMafiaPreparationModes() &&
                matchManager.CanLocalSelectMafiaDisguise)
            {
                SetNightPreparationVoteMode(
                    VoteMode.MafiaDisguise
                );

                return;
            }

            if (matchManager.CurrentPhase !=
                MatchPhase.MorningVote)
            {
                return;
            }

            SetTab(false);
        }

        private void SetTab(bool isChat)
        {
            if (isChat)
            {
                hasUnreadMorningChatMessages = false;
                RefreshMorningChatUnreadIndicator();
            }

            if (chatPanel != null)
                chatPanel.SetActive(isChat);

            if (votePanel != null)
                votePanel.SetActive(!isChat);

            if (chatTabHighlight != null)
                chatTabHighlight.gameObject.SetActive(isChat);

            if (voteTabHighlight != null)
                voteTabHighlight.gameObject.SetActive(!isChat);

            SetTabTextColor(chatTabButton, isChat);
            SetTabTextColor(voteTabButton, !isChat);
        }

        private void SetTabTextColor(Button targetButton, bool selected)
        {
            if (targetButton == null)
                return;

            TextMeshProUGUI buttonText = targetButton.GetComponentInChildren<TextMeshProUGUI>();

            if (buttonText == null)
                return;

            buttonText.color = selected ? new Color(1f, 0.8f, 0f) : Color.white;
        }

        private void UpdateHeaderUI()
        {
            if (matchManager == null)
            {
                if (roleText != null)
                    roleText.text = "직업: ???";

                if (aliveText != null)
                    aliveText.text = "생존: 0";

                if (dayText != null)
                    dayText.text = "0일차";

                UpdateRoleDescriptionUI();
                return;
            }

            if (roleText != null)
            {
                roleText.text = matchManager.TryGetLocalPlayerMatchState(out PlayerMatchState localState)
                    ? $"직업: {GetRoleDisplayName(localState.role)}"
                    : "직업: ???";
            }

            if (aliveText != null)
                aliveText.text = $"생존: {GetAlivePlayerCount()}";

            if (dayText != null)
                dayText.text = $"{matchManager.CurrentDay}일차";

            UpdateRoleDescriptionUI();
        }

        private void TryBindLocalSpiritHitReceiver()
        {
            SpiritHitReceiver target =
                SpiritHitReceiver.LocalInstance;

            if (localSpiritHitReceiver == target)
                return;

            UnbindLocalSpiritHitReceiver();
            localSpiritHitReceiver = target;

            if (localSpiritHitReceiver == null)
                return;

            localSpiritHitReceiver.HealthChanged +=
                OnLocalSpiritHealthChanged;

            localSpiritHitReceiver.SuppressionChanged +=
                OnLocalSpiritSuppressionChanged;
        }

        private void UnbindLocalSpiritHitReceiver()
        {
            if (localSpiritHitReceiver == null)
                return;

            localSpiritHitReceiver.HealthChanged -=
                OnLocalSpiritHealthChanged;

            localSpiritHitReceiver.SuppressionChanged -=
                OnLocalSpiritSuppressionChanged;

            localSpiritHitReceiver = null;
        }

        private void OnLocalSpiritHealthChanged(
            int previous,
            int current)
        {
            RefreshSpiritHealthUI();
        }

        private void OnLocalSpiritSuppressionChanged(
            bool previous,
            bool current)
        {
            RefreshSpiritHealthUI();
        }

        private void RefreshSpiritHealthUI()
        {
            bool isSpiritPhase =
                matchManager != null &&
                (matchManager.CurrentPhase ==
                    MatchPhase.NightAction ||
                 matchManager.CurrentPhase ==
                    MatchPhase.FinalDuel);

            bool localPlayerAlive =
                matchManager != null &&
                matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState
                ) &&
                localState.isAlive;

            bool visible =
                isSpiritPhase &&
                localPlayerAlive &&
                localSpiritHitReceiver != null;

            if (spiritHealthPanel != null &&
                spiritHealthPanel.activeSelf != visible)
            {
                spiritHealthPanel.SetActive(visible);
            }

            RefreshLocalSpiritStatusUI(visible);

            if (!visible || spiritHealthIcons == null)
                return;

            bool showAsDepleted =
                localSpiritHitReceiver.IsSuppressed ||
                localSpiritHitReceiver
                    .IsIncapacitatedForNight;

            int maximumVisibleHealth =
                Mathf.Min(
                    localSpiritHitReceiver.MaxHealth,
                    spiritHealthIcons.Length
                );

            int displayedHealth =
                showAsDepleted
                    ? 0
                    : Mathf.Clamp(
                        localSpiritHitReceiver.CurrentHealth,
                        0,
                        maximumVisibleHealth
                    );

            for (int i = 0;
                 i < spiritHealthIcons.Length;
                 i++)
            {
                Image healthIcon =
                    spiritHealthIcons[i];

                if (healthIcon == null)
                    continue;

                bool iconVisible =
                    i < maximumVisibleHealth;

                if (healthIcon.gameObject.activeSelf !=
                    iconVisible)
                {
                    healthIcon.gameObject.SetActive(
                        iconVisible
                    );
                }

                if (!iconVisible)
                    continue;

                bool isFilled =
                    i < displayedHealth;

                Sprite targetSprite =
                    isFilled
                        ? spiritHealthFilledSprite
                        : spiritHealthEmptySprite;

                if (targetSprite != null &&
                    healthIcon.sprite != targetSprite)
                {
                    healthIcon.sprite = targetSprite;
                }

                Color targetColor =
                    isFilled
                        ? spiritHealthFilledColor
                        : spiritHealthEmptyColor;

                if (healthIcon.color != targetColor)
                    healthIcon.color = targetColor;
            }
        }

        private void RefreshLocalSpiritStatusUI(
            bool canShowStatus)
        {
            string status = string.Empty;

            if (canShowStatus &&
                localSpiritHitReceiver != null)
            {
                if (localSpiritHitReceiver
                        .IsConfinedForNight)
                {
                    status = "[감금]";
                }
                else if (localSpiritHitReceiver
                             .IsSuppressed)
                {
                    status = FormatTimedSpiritStatus(
                        "제압",
                        localSpiritHitReceiver
                            .LocalSuppressionRemainingSeconds
                    );
                }
                else
                {
                    double invulnerabilityRemaining =
                        localSpiritHitReceiver
                            .LocalDamageInvulnerabilityRemainingSeconds;

                    if (invulnerabilityRemaining > 0d)
                    {
                        status = FormatTimedSpiritStatus(
                            "피격 면역",
                            invulnerabilityRemaining
                        );
                    }
                    else
                    {
                        double doorLockRemaining =
                            localSpiritHitReceiver
                                .LocalDoorLockRemainingSeconds;

                        if (doorLockRemaining > 0d)
                        {
                            status = FormatTimedSpiritStatus(
                                "출입 제한",
                                doorLockRemaining
                            );
                        }
                    }
                }
            }

            bool visible =
                !string.IsNullOrEmpty(status);

            if (spiritStatusPanel != null &&
                spiritStatusPanel.activeSelf != visible)
            {
                spiritStatusPanel.SetActive(visible);
            }

            if (spiritStatusText != null &&
                spiritStatusText.text != status)
            {
                spiritStatusText.text = status;
            }
        }

        private static string FormatTimedSpiritStatus(
            string statusName,
            double remainingSeconds)
        {
            float displayedSeconds =
                Mathf.Ceil(
                    Mathf.Max(
                        0f,
                        (float)remainingSeconds
                    ) * 10f
                ) / 10f;

            return
                $"[{statusName}] {displayedSeconds:0.0}";
        }

        private void UpdateRoleDescriptionUI()
        {
            if (roleNameText == null &&
                roleDescriptionText == null)
            {
                return;
            }

            if (matchManager == null ||
                !matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState))
            {
                if (roleNameText != null)
                {
                    roleNameText.text = "직업 배정 중";
                    roleNameText.color = UnassignedRoleNameColor;
                }

                if (roleDescriptionText != null)
                {
                    int readyCount =
                        matchManager != null
                            ? matchManager
                                .RoleAssignmentReadyCount
                            : 0;

                    int expectedCount =
                        matchManager != null
                            ? matchManager
                                .RoleAssignmentExpectedCount
                            : 0;

                    roleDescriptionText.text =
                        expectedCount > 0
                            ? $"준비 완료 {readyCount} / " +
                              $"{expectedCount}\n" +
                              "게임 정보를 기다리고 있습니다."
                            : "게임 정보를 기다리고 있습니다.";
                }

                return;
            }

            if (roleNameText != null)
            {
                roleNameText.text =
                    GetRoleDisplayName(localState.role);
                roleNameText.color =
                    GetRoleNameColor(localState.team);
            }

            if (roleDescriptionText != null)
            {
                roleDescriptionText.text =
                    GetRoleDescription(localState.role);
            }
        }

        private void OnRoleAssignmentProgressChanged()
        {
            if (matchManager == null ||
                matchManager.CurrentPhase != MatchPhase.None)
            {
                return;
            }

            UpdateRoleDescriptionUI();
            RefreshMatchTransitionOverlay();
        }

        private void RefreshMatchTransitionOverlay()
        {
            if (matchTransitionOverlay == null)
                return;

            if (isLeavingMatch &&
                !lobbyReturnRequestFailed &&
                !lobbyReturnRequestDelayed &&
                Time.realtimeSinceStartup -
                    lobbyReturnRequestStartedRealtime >=
                    LobbyReturnTimeout)
            {
                lobbyReturnRequestDelayed = true;
            }

            bool isAssigningRoles =
                !isLeavingMatch &&
                (matchManager == null ||
                 matchManager.CurrentPhase == MatchPhase.None);
            bool isFinalDuelPreparation =
                !isLeavingMatch &&
                matchManager != null &&
                matchManager.IsFinalDuelPreparing;
            bool visible =
                isLeavingMatch ||
                isAssigningRoles ||
                isFinalDuelPreparation;

            if (matchTransitionOverlay.activeSelf != visible)
                matchTransitionOverlay.SetActive(visible);

            if (!visible)
            {
                SetMatchTransitionActionButtonsVisible(
                    false,
                    string.Empty,
                    string.Empty
                );
                return;
            }

            matchTransitionOverlay.transform.SetAsLastSibling();

            if (matchTransitionText == null)
                return;

            if (isLeavingMatch)
            {
                if (lobbyReturnRequestFailed)
                {
                    matchTransitionText.SetText(
                        "<size=42><b>로비 복귀 실패</b></size>\n" +
                        "<size=24>로비 복귀 요청을 시작하지 못했습니다.\n" +
                        "잠시 후 다시 시도해 주세요.</size>"
                    );
                    SetMatchTransitionActionButtonsVisible(
                        true,
                        "다시 시도",
                        "닫기"
                    );
                    return;
                }

                if (lobbyReturnRequestDelayed)
                {
                    matchTransitionText.SetText(
                        "<size=42><b>로비 복귀 지연</b></size>\n" +
                        "<size=24>네트워크 응답이 지연되고 있습니다.\n" +
                        "다시 시도하거나 현재 방에서 나갈 수 있습니다.</size>"
                    );
                    SetMatchTransitionActionButtonsVisible(
                        true,
                        "다시 시도",
                        "나가기"
                    );
                    return;
                }

                matchTransitionText.SetText(
                    "<size=42><b>로비로 돌아가는 중</b></size>\n" +
                    "<size=24>네트워크 연결을 정리하고 있습니다.</size>"
                );
                SetMatchTransitionActionButtonsVisible(
                    false,
                    string.Empty,
                    string.Empty
                );
                return;
            }

            if (isFinalDuelPreparation)
            {
                SetMatchTransitionActionButtonsVisible(
                    false,
                    string.Empty,
                    string.Empty
                );

                int remainingSeconds =
                    Mathf.Max(
                        1,
                        Mathf.CeilToInt(
                            (float)matchManager
                                .FinalDuelPreparationRemainingTime
                        )
                    );

                matchTransitionText.SetText(
                    "<size=42><b>최종 결투</b></size>\n" +
                    "<size=24>살인귀와 마녀만 남았습니다.\n" +
                    "직업 능력과 출입은 봉인됩니다.\n" +
                    "기본 무기로 상대를 3회 공격하십시오.</size>\n" +
                    "<size=26>결투 시작까지</size>\n" +
                    $"<size=54><b>{remainingSeconds}</b></size>"
                );
                return;
            }

            SetMatchTransitionActionButtonsVisible(
                false,
                string.Empty,
                string.Empty
            );

            int readyCount = matchManager != null
                ? matchManager.RoleAssignmentReadyCount
                : 0;
            int expectedCount = matchManager != null
                ? matchManager.RoleAssignmentExpectedCount
                : 0;
            bool isSlow =
                Time.realtimeSinceStartup -
                roleAssignmentWaitStartedRealtime >=
                SlowRoleAssignmentNoticeDelay;
            string progress = expectedCount > 0
                ? $"{readyCount} / {expectedCount}"
                : "서버 응답 대기 중";
            string notice = isSlow
                ? "일부 플레이어의 연결을 기다리고 있습니다."
                : "플레이어 정보를 동기화하고 있습니다.";
            string connectionNotice =
                GetLocalConnectionWarningText(true);

            matchTransitionText.SetText(
                "<size=42><b>게임 준비 중</b></size>\n" +
                $"<size=24>{notice}\n{progress}" +
                $"{connectionNotice}</size>"
            );
        }

        private void RefreshNetworkStatusUI()
        {
            if (networkStatusPanel == null)
                return;

            RelayConnectionManager relayManager =
                RelayConnectionManager.Instance;
            int qualityLevel =
                relayManager != null &&
                relayManager.HasConnectionQualitySample
                    ? relayManager.ConnectionQualityLevel
                    : 0;
            bool visible = qualityLevel > 0;

            if (networkStatusPanel.activeSelf != visible)
                networkStatusPanel.SetActive(visible);

            if (!visible || networkStatusText == null)
                return;

            if (qualityLevel >= 2)
            {
                networkStatusText.SetText("연결 불안정");
                networkStatusText.color =
                    new Color(1f, 0.32f, 0.28f, 1f);
            }
            else
            {
                networkStatusText.SetText("연결 지연");
                networkStatusText.color =
                    new Color(1f, 0.78f, 0.2f, 1f);
            }
        }

        private static string GetLocalConnectionWarningText(
            bool includeLeadingLineBreak)
        {
            RelayConnectionManager relayManager =
                RelayConnectionManager.Instance;

            if (relayManager == null ||
                !relayManager.HasConnectionQualitySample ||
                relayManager.ConnectionQualityLevel <= 0)
            {
                return string.Empty;
            }

            string prefix = includeLeadingLineBreak
                ? "\n"
                : string.Empty;

            return relayManager.ConnectionQualityLevel >= 2
                ? prefix +
                  "<color=#FF5247>현재 연결이 불안정합니다.</color>"
                : prefix +
                  "<color=#FFC733>현재 연결이 지연되고 있습니다.</color>";
        }

        private void SetMatchTransitionActionButtonsVisible(
            bool visible,
            string retryLabel,
            string cancelLabel)
        {
            if (matchTransitionRetryButton != null)
            {
                matchTransitionRetryButton.gameObject.SetActive(
                    visible
                );
                matchTransitionRetryButton.interactable = visible;

                TextMeshProUGUI label =
                    matchTransitionRetryButton
                        .GetComponentInChildren<TextMeshProUGUI>(true);

                if (label != null && visible)
                    label.SetText(retryLabel);
            }

            if (matchTransitionCancelButton != null)
            {
                matchTransitionCancelButton.gameObject.SetActive(
                    visible
                );
                matchTransitionCancelButton.interactable = visible;

                TextMeshProUGUI label =
                    matchTransitionCancelButton
                        .GetComponentInChildren<TextMeshProUGUI>(true);

                if (label != null && visible)
                    label.SetText(cancelLabel);
            }
        }

        private int GetAlivePlayerCount()
        {
            if (matchManager == null || matchManager.PublicPlayerStates == null)
                return 0;

            int aliveCount = 0;

            for (int i = 0; i < matchManager.PublicPlayerStates.Count; i++)
            {
                if (matchManager.PublicPlayerStates[i].isAlive)
                    aliveCount++;
            }

            return aliveCount;
        }

        private void UpdatePhaseTimerUI()
        {
            if (topMeetingTimerText == null)
                return;

            if (matchManager == null)
            {
                topMeetingTimerText.text = "준비 중";
                UpdateVoteTimerText(0);
                return;
            }

            MatchPhase phase = matchManager.CurrentPhase;
            int remainingSeconds = Mathf.CeilToInt((float)matchManager.PhaseRemainingTime);

            topMeetingTimerText.text = GetPhaseDisplayName(phase, remainingSeconds);

            if (currentVoteMode != VoteMode.None)
                UpdateVoteTimerText(remainingSeconds);
        }

        private string GetPhaseDisplayName(MatchPhase phase, int remainingSeconds)
        {
            switch (phase)
            {
                case MatchPhase.MorningDiscussion:
                    return $"회의 : {remainingSeconds}s";

                case MatchPhase.MorningVote:
                    return $"아침 투표 : {remainingSeconds}s";

                case MatchPhase.MorningVoteResult:
                    return $"투표 결과 : {remainingSeconds}s";

                case MatchPhase.NightPreparation:
                    return $"밤 준비 : {remainingSeconds}s";

                case MatchPhase.NightAction:
                    return $"밤 : {remainingSeconds}s";

                case MatchPhase.NightResult:
                    return $"밤 결과 : {remainingSeconds}s";

                case MatchPhase.GameResult:
                    return "게임 종료";

                case MatchPhase.FinalDuel:
                    return "최종 결투";

                default:
                    return "준비 중";
            }
        }

        private void UpdateVoteTimerText(int remainingSeconds)
        {
            if (voteTimerText == null)
                return;

            int minutes = remainingSeconds / 60;
            int seconds = remainingSeconds % 60;

            voteTimerText.text = $"{minutes:00}:{seconds:00}";
        }

        private void ShowMorningVoteResult()
        {
            if (matchManager == null)
                return;

            MorningVoteResultData result = matchManager.MorningVoteResult;

            if (!result.hasResult)
            {
                SetResultText(
                    "<size=46>투표 결과</size>\n\n" +
                    "<size=60><b>결과를 집계하고 있습니다.</b></size>"
                );

                return;
            }

            if (result.abstainPreventedExile)
            {
                SetResultText(
                    "<size=46>투표 결과</size>\n\n" +
                    "<size=68><b>아무도 추방되지 않았습니다.</b></size>\n\n" +
                    $"<size=34>기권: {result.abstainVoteCount}표</size>"
                );

                return;
            }

            if (result.isTie)
            {
                SetResultText(
                    "<size=46>투표 결과</size>\n\n" +
                    "<size=54>후보의 최다 득표가 동률입니다.</size>\n" +
                    "<size=68><b>아무도 추방되지 않았습니다.</b></size>\n\n" +
                    $"<size=34>기권: {result.abstainVoteCount}표</size>"
                );

                return;
            }

            if (!result.hasExiledPlayer)
            {
                SetResultText(
                    "<size=46>투표 결과</size>\n\n" +
                    "<size=68><b>아무도 추방되지 않았습니다.</b></size>"
                );

                return;
            }

            string revealedTeamText = string.Empty;

            if (result.revealedExiledTeam)
            {
                switch (result.exiledTeam)
                {
                    case RoleTeam.Mafia:
                        revealedTeamText =
                            $"{result.exiledPlayerName}님은 마녀였습니다.";
                        break;

                    case RoleTeam.Neutral:
                        revealedTeamText =
                            $"{result.exiledPlayerName}님은 이방인이었습니다.";
                        break;

                    case RoleTeam.Citizen:
                        revealedTeamText =
                            $"{result.exiledPlayerName}님은 마녀가 아니었습니다.";
                        break;
                }
            }

            string revealedTeamLine =
                string.IsNullOrEmpty(revealedTeamText)
                    ? string.Empty
                    : $"<size=44><b>{revealedTeamText}</b></size>\n\n";

            SetResultText(
                "<size=46>투표 결과</size>\n\n" +
                $"<size=78><b>{result.exiledPlayerName}</b></size>\n" +
                "<size=52>추방되었습니다.</size>\n\n" +
                revealedTeamLine +
                $"<size=34>획득 표: {result.voteCount} · 기권: {result.abstainVoteCount}</size>"
            );
        }

        private void SetResultText(string message)
        {
            if (phaseResultText == null)
                return;

            if (string.IsNullOrEmpty(message))
            {
                HideResultText();
                return;
            }

            if (phaseResultAnimationCoroutine != null)
            {
                StopCoroutine(phaseResultAnimationCoroutine);
                phaseResultAnimationCoroutine = null;
            }

            phaseResultText.text = message;
            phaseResultText.alpha = 0f;
            phaseResultText.rectTransform.localScale = Vector3.one * phaseResultStartScale;
            phaseResultText.gameObject.SetActive(true);
            phaseResultText.transform.SetAsLastSibling();

            phaseResultAnimationCoroutine = StartCoroutine(PlayResultTextAnimation());
        }

        private void HideResultText()
        {
            if (phaseResultAnimationCoroutine != null)
            {
                StopCoroutine(phaseResultAnimationCoroutine);
                phaseResultAnimationCoroutine = null;
            }

            if (phaseResultText == null)
                return;

            phaseResultText.alpha = 0f;
            phaseResultText.rectTransform.localScale = Vector3.one;
            phaseResultText.text = string.Empty;
            phaseResultText.gameObject.SetActive(false);
        }

        private IEnumerator PlayResultTextAnimation()
        {
            float elapsedTime = 0f;

            while (elapsedTime < phaseResultAppearDuration)
            {
                elapsedTime += Time.unscaledDeltaTime;

                float progress = Mathf.Clamp01(elapsedTime / phaseResultAppearDuration);
                float easedProgress = Mathf.SmoothStep(0f, 1f, progress);

                phaseResultText.alpha = easedProgress;

                float scale;

                if (easedProgress < 0.75f)
                {
                    float expandProgress = easedProgress / 0.75f;
                    scale = Mathf.Lerp(phaseResultStartScale, phaseResultOvershootScale, expandProgress);
                }
                else
                {
                    float settleProgress = (easedProgress - 0.75f) / 0.25f;
                    scale = Mathf.Lerp(phaseResultOvershootScale, 1f, settleProgress);
                }

                phaseResultText.rectTransform.localScale = Vector3.one * scale;

                yield return null;
            }

            phaseResultText.alpha = 1f;
            phaseResultText.rectTransform.localScale = Vector3.one;
            phaseResultAnimationCoroutine = null;
        }

        private void InitializeVoteRows()
        {
            int toggleCount = suspectToggles != null ? suspectToggles.Length : 0;

            suspectClientIds = new ulong[toggleCount];
            suspectOptionValues = new int[toggleCount];
            suspectRows = new GameObject[toggleCount];
            suspectIdentityLabels = new PlayerIdentityLabel[toggleCount];
            suspectNameTexts = new TextMeshProUGUI[toggleCount];
            suspectRowCanvasGroups = new CanvasGroup[toggleCount];
            suspectToggleListeners =
                new UnityEngine.Events.UnityAction<bool>[toggleCount];

            for (int i = 0; i < toggleCount; i++)
            {
                Toggle suspectToggle = suspectToggles[i];

                suspectClientIds[i] = ulong.MaxValue;
                suspectOptionValues[i] = -1;

                if (suspectToggle == null)
                    continue;

                GameObject rowObject = FindSuspectRow(suspectToggle);

                suspectRows[i] = rowObject;
                suspectIdentityLabels[i] =
                    rowObject != null
                        ? rowObject.GetComponent<PlayerIdentityLabel>()
                        : null;

                suspectNameTexts[i] =
                    FindSuspectNameText(rowObject);

                if (rowObject != null)
                {
                    suspectRowCanvasGroups[i] =
                        rowObject.GetComponent<CanvasGroup>();

                    if (suspectRowCanvasGroups[i] == null)
                    {
                        suspectRowCanvasGroups[i] =
                            rowObject.AddComponent<CanvasGroup>();
                    }
                }

                if (suspectNameTexts[i] != null)
                {
                    suspectNameTexts[i].textWrappingMode =
                        TextWrappingModes.NoWrap;

                    suspectNameTexts[i].overflowMode =
                        TextOverflowModes.Overflow;
                }

                int toggleIndex = i;
                suspectToggleListeners[i] =
                    isOn => OnSuspectToggleChanged(toggleIndex, isOn);

                suspectToggle.onValueChanged.AddListener(
                    suspectToggleListeners[i]
                );

                suspectToggle.SetIsOnWithoutNotify(false);

                if (rowObject != null)
                    rowObject.SetActive(false);
            }
        }

        private void UnregisterSuspectToggleEvents()
        {
            if (suspectToggles == null ||
                suspectToggleListeners == null)
            {
                return;
            }

            int count = Mathf.Min(
                suspectToggles.Length,
                suspectToggleListeners.Length
            );

            for (int i = 0; i < count; i++)
            {
                if (suspectToggles[i] == null ||
                    suspectToggleListeners[i] == null)
                {
                    continue;
                }

                suspectToggles[i].onValueChanged.RemoveListener(
                    suspectToggleListeners[i]
                );
            }
        }

        private void RefreshVotePlayers()
        {
            ulong previouslySelectedClientId =
                GetSelectedVoteTargetClientId();

            HideAllVoteRows();

            if (currentVoteMode == VoteMode.None ||
                matchManager == null ||
                suspectToggles == null)
            {
                return;
            }

            if (currentVoteMode == VoteMode.MafiaDisguise)
            {
                RefreshMafiaDisguiseOptions();
                return;
            }

            if (matchManager.PublicPlayerStates == null)
                return;

            int rowIndex = 0;

            for (int i = 0; i < matchManager.PublicPlayerStates.Count; i++)
            {
                PlayerPublicState playerState = matchManager.PublicPlayerStates[i];

                if (!ShouldIncludePlayerInCurrentVote(playerState))
                    continue;

                if (rowIndex >= suspectToggles.Length)
                    break;

                suspectClientIds[rowIndex] = playerState.clientId;

                if (suspectRows[rowIndex] != null)
                    suspectRows[rowIndex].SetActive(true);

                if (suspectRowCanvasGroups != null &&
                    rowIndex < suspectRowCanvasGroups.Length &&
                    suspectRowCanvasGroups[rowIndex] != null)
                {
                    suspectRowCanvasGroups[rowIndex].alpha =
                        playerState.isAlive ? 1f : 0.55f;
                }

                string displayText =
                    GetVotePlayerDisplayText(playerState);

                if (suspectIdentityLabels[rowIndex] != null)
                {
                    suspectIdentityLabels[rowIndex].SetPlayer(
                        playerState.clientId,
                        displayText
                    );
                }
                else if (suspectNameTexts[rowIndex] != null)
                {
                    suspectNameTexts[rowIndex].text = displayText;
                }

                if (suspectToggles[rowIndex] != null)
                    suspectToggles[rowIndex].SetIsOnWithoutNotify(false);

                rowIndex++;
            }

            if (currentVoteMode == VoteMode.DrunkardSleep &&
                matchManager.HasLocalDrunkardSleepTarget)
            {
                SelectVoteTarget(
                    matchManager.LocalDrunkardSleepTargetClientId
                );
                return;
            }

            ulong targetToRestore =
                currentVoteMode == VoteMode.MafiaKiller &&
                localMafiaKillerVoteTargetClientId != ulong.MaxValue
                    ? localMafiaKillerVoteTargetClientId
                    : previouslySelectedClientId;

            SelectVoteTarget(targetToRestore);
            RefreshMorningVoteTallyUI();
            UpdateMorningVoteInteractionState();
        }

        private string GetVotePlayerDisplayText(
            PlayerPublicState playerState)
        {
            string playerName = playerState.playerName.ToString();
            bool isLocalPlayer =
                matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState) &&
                localState.clientId == playerState.clientId;
            string localMarker = isLocalPlayer
                ? $"<color=#{GetTeamColorHex(localState.team)}>▶</color> "
                : string.Empty;

            if (IsLocalDeadSpectator() &&
                matchManager.TryGetLocalSpectatorPlayerMatchState(
                    playerState.clientId,
                    out PlayerMatchState spectatorState))
            {
                string lifeSuffix =
                    playerState.isAlive ? string.Empty : " (사망)";
                string teamColor =
                    GetTeamColorHex(spectatorState.team);

                return
                    $"{localMarker}{playerName}{lifeSuffix}\n" +
                    $"<size=80%><color=#{teamColor}>" +
                    $"{GetTeamDisplayName(spectatorState.team)} · " +
                    $"{GetRoleDisplayName(spectatorState.role)}" +
                    "</color></size>";
            }

            if (IsLocalAliveMafia() &&
                IsLocalMafiaMember(playerState.clientId))
            {
                string mafiaMarker =
                    isLocalPlayer
                        ? localMarker
                        : $"<color=#{GetTeamColorHex(RoleTeam.Mafia)}>◆</color> ";

                string voterNames =
                    currentVoteMode == VoteMode.MafiaKiller
                        ? matchManager
                            .GetLocalMafiaKillerVoterNames(
                                playerState.clientId
                            )
                        : string.Empty;

                string voterSuffix =
                    string.IsNullOrWhiteSpace(voterNames)
                        ? string.Empty
                        : $"  <size=80%>← {voterNames}</size>";

                return
                    $"{mafiaMarker}{playerName}{voterSuffix}";
            }

            return $"{localMarker}{playerName}";
        }

        private bool ShouldIncludePlayerInCurrentVote(
            PlayerPublicState playerState)
        {
            if (!playerState.isAlive)
            {
                return
                    currentVoteMode == VoteMode.MorningExile &&
                    IsLocalDeadSpectator();
            }

            switch (currentVoteMode)
            {
                case VoteMode.MorningExile:
                    return true;

                case VoteMode.MafiaKiller:
                    return IsLocalMafiaMember(playerState.clientId);

                case VoteMode.DrunkardSleep:
                    return matchManager.TryGetLocalPlayerMatchState(
                               out PlayerMatchState localState) &&
                           playerState.clientId != localState.clientId;

                default:
                    return false;
            }
        }

        private bool IsLocalDeadSpectator()
        {
            return
                matchManager != null &&
                matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState) &&
                !localState.isAlive;
        }

        private bool IsLocalAliveMafia()
        {
            return
                matchManager != null &&
                matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState) &&
                localState.isAlive &&
                localState.team == RoleTeam.Mafia;
        }

        private bool IsLocalMafiaMember(ulong clientId)
        {
            if (matchManager == null ||
                matchManager.LocalMafiaMembers == null)
            {
                return false;
            }

            for (int i = 0;
                 i < matchManager.LocalMafiaMembers.Count;
                 i++)
            {
                if (matchManager.LocalMafiaMembers[i].clientId ==
                    clientId)
                {
                    return true;
                }
            }

            return false;
        }

        private ulong GetSelectedVoteTargetClientId()
        {
            if (suspectToggles == null ||
                suspectClientIds == null)
            {
                return ulong.MaxValue;
            }

            for (int i = 0; i < suspectToggles.Length; i++)
            {
                if (suspectToggles[i] != null &&
                    suspectToggles[i].isOn)
                {
                    return suspectClientIds[i];
                }
            }

            return ulong.MaxValue;
        }

        private void SelectVoteTarget(ulong targetClientId)
        {
            if (suspectToggles == null ||
                suspectClientIds == null)
            {
                return;
            }

            for (int i = 0; i < suspectToggles.Length; i++)
            {
                if (suspectToggles[i] == null)
                    continue;

                bool selected =
                    targetClientId != ulong.MaxValue &&
                    suspectClientIds[i] == targetClientId;

                suspectToggles[i].SetIsOnWithoutNotify(selected);
            }
        }

        private void HideAllVoteRows()
        {
            if (suspectToggles == null)
                return;

            for (int i = 0; i < suspectToggles.Length; i++)
            {
                suspectClientIds[i] = ulong.MaxValue;

                if (suspectOptionValues != null && i < suspectOptionValues.Length)
                    suspectOptionValues[i] = -1;

                if (suspectToggles[i] != null)
                    suspectToggles[i].SetIsOnWithoutNotify(false);

                if (suspectIdentityLabels != null &&
                    i < suspectIdentityLabels.Length &&
                    suspectIdentityLabels[i] != null)
                {
                    suspectIdentityLabels[i].Clear();
                }

                if (suspectRows[i] != null)
                    suspectRows[i].SetActive(false);

                if (suspectRowCanvasGroups != null &&
                    i < suspectRowCanvasGroups.Length &&
                    suspectRowCanvasGroups[i] != null)
                {
                    suspectRowCanvasGroups[i].alpha = 1f;
                }
            }
        }

        private GameObject FindSuspectRow(Toggle suspectToggle)
        {
            Transform current = suspectToggle.transform;

            for (int i = 0; i < 3 && current != null; i++)
            {
                Toggle[] childToggles = current.GetComponentsInChildren<Toggle>(true);
                TextMeshProUGUI[] childTexts = current.GetComponentsInChildren<TextMeshProUGUI>(true);

                if (childToggles.Length == 1 && childTexts.Length > 0)
                    return current.gameObject;

                current = current.parent;
            }

            return suspectToggle.gameObject;
        }

        private TextMeshProUGUI FindSuspectNameText(GameObject rowObject)
        {
            if (rowObject == null)
                return null;

            TextMeshProUGUI[] texts = rowObject.GetComponentsInChildren<TextMeshProUGUI>(true);

            for (int i = 0; i < texts.Length; i++)
            {
                if (texts[i].gameObject.name.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0)
                    return texts[i];
            }

            for (int i = 0; i < texts.Length; i++)
            {
                if (texts[i].text.StartsWith("Player", StringComparison.OrdinalIgnoreCase))
                    return texts[i];
            }

            return texts.Length > 0 ? texts[texts.Length - 1] : null;
        }

        private void OnSuspectToggleChanged(
            int toggleIndex,
            bool isOn)
        {
            if (matchManager == null ||
                currentVoteMode == VoteMode.None ||
                suspectClientIds == null ||
                toggleIndex < 0 ||
                toggleIndex >= suspectClientIds.Length)
            {
                return;
            }

            if (currentVoteMode ==
                VoteMode.MorningExile)
            {
                if (morningVoteLocked || !isOn)
                    return;

                ulong morningTargetClientId =
                    suspectClientIds[toggleIndex];

                if (morningTargetClientId ==
                    ulong.MaxValue)
                {
                    return;
                }

                SelectVoteTarget(
                    morningTargetClientId
                );

                morningAbstainSelected = false;

                matchManager.SetMorningVoteDraft(
                    morningTargetClientId
                );

                UpdateMorningVoteInteractionState();
                RefreshMorningVoteTallyUI();
                return;
            }

            if (!isOn)
                return;

            if (currentVoteMode == VoteMode.MafiaDisguise)
            {
                if (suspectOptionValues == null ||
                    toggleIndex >= suspectOptionValues.Length ||
                    suspectOptionValues[toggleIndex] < 0)
                {
                    return;
                }

                matchManager.SubmitMafiaDisguiseWeapon(
                    (RoleWeaponId)suspectOptionValues[toggleIndex]
                );
                return;
            }

            ulong targetClientId =
                suspectClientIds[toggleIndex];

            if (targetClientId ==
                ulong.MaxValue)
            {
                return;
            }

            switch (currentVoteMode)
            {
                case VoteMode.MafiaKiller:
                    localMafiaKillerVoteTargetClientId =
                        targetClientId;

                    SelectVoteTarget(
                        targetClientId
                    );

                    matchManager
                        .SubmitMafiaKillerVote(
                            targetClientId
                        );
                    break;

                case VoteMode.DrunkardSleep:
                    matchManager
                        .SubmitDrunkardSleepTarget(
                            targetClientId
                        );
                    break;
            }
        }

        private void OnAbstainVote()
        {
            if (matchManager == null ||
                currentVoteMode == VoteMode.None)
            {
                return;
            }

            switch (currentVoteMode)
            {
                case VoteMode.MorningExile:
                    if (morningVoteLocked ||
                        matchManager.CurrentPhase !=
                            MatchPhase.MorningVote)
                    {
                        return;
                    }

                    ClearSelectedVote();
                    morningAbstainSelected = true;
                    matchManager.SetMorningAbstainDraft();
                    UpdateMorningVoteInteractionState();
                    RefreshMorningVoteTallyUI();
                    break;

                case VoteMode.MafiaKiller:
                    localMafiaKillerVoteTargetClientId =
                        ulong.MaxValue;

                    ClearSelectedVote();
                    matchManager.ClearMafiaKillerVote();
                    break;

                case VoteMode.DrunkardSleep:
                    ClearSelectedVote();
                    matchManager.ClearDrunkardSleepTarget();
                    break;
            }
        }

        private void OnConfirmMorningVote()
        {
            if (matchManager == null ||
                currentVoteMode !=
                    VoteMode.MorningExile ||
                matchManager.CurrentPhase !=
                    MatchPhase.MorningVote ||
                morningVoteLocked)
            {
                return;
            }

            ulong selectedTarget =
                GetSelectedVoteTargetClientId();

            bool hasCandidate =
                selectedTarget != ulong.MaxValue;

            if (!hasCandidate &&
                !morningAbstainSelected)
            {
                return;
            }

            morningVoteLocked = true;
            UpdateMorningVoteInteractionState();
            RefreshMorningVoteTallyUI();

            if (morningAbstainSelected)
            {
                matchManager.SubmitMorningAbstain();
                return;
            }

            matchManager.SubmitMorningVote(
                selectedTarget
            );
        }

        private void ResetMorningVoteLocalState()
        {
            morningAbstainSelected = false;
            morningVoteLocked = false;

            if (confirmVoteButtonText != null)
            {
                confirmVoteButtonText.text =
                    "투표 확정";
            }
        }

        private void UpdateMorningVoteInteractionState()
        {
            bool isMorningMode =
                currentVoteMode ==
                    VoteMode.MorningExile;

            if (!isMorningMode)
            {
                bool canUseMode =
                    currentVoteMode != VoteMode.None;

                if (suspectToggles != null)
                {
                    for (int i = 0;
                         i < suspectToggles.Length;
                         i++)
                    {
                        if (suspectToggles[i] == null)
                            continue;

                        bool hasSelectableValue = HasSelectableValueAt(i);

                        suspectToggles[i].interactable =
                            canUseMode && hasSelectableValue;
                    }
                }

                if (abstainButton != null)
                {
                    abstainButton.interactable =
                        canUseMode;
                }

                if (confirmVoteButton != null)
                {
                    confirmVoteButton.interactable =
                        false;
                }

                return;
            }

            bool isMorningVote =
                matchManager != null &&
                matchManager.CurrentPhase ==
                    MatchPhase.MorningVote;

            bool canEdit =
                isMorningVote &&
                !morningVoteLocked &&
                matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState localState) &&
                localState.isAlive;

            if (suspectToggles != null)
            {
                for (int i = 0;
                     i < suspectToggles.Length;
                     i++)
                {
                    if (suspectToggles[i] == null)
                        continue;

                    bool hasPlayer =
                        suspectClientIds != null &&
                        i < suspectClientIds.Length &&
                        suspectClientIds[i] !=
                            ulong.MaxValue;

                    suspectToggles[i].interactable =
                        canEdit && hasPlayer;
                }
            }

            if (abstainButton != null)
            {
                abstainButton.interactable =
                    canEdit;
            }

            if (confirmVoteButton != null)
            {
                bool hasSelection =
                    morningAbstainSelected ||
                    GetSelectedVoteTargetClientId() !=
                        ulong.MaxValue;

                confirmVoteButton.interactable =
                    canEdit && hasSelection;
            }

            if (confirmVoteButtonText != null)
            {
                confirmVoteButtonText.text =
                    morningVoteLocked
                        ? "투표 완료"
                        : "투표 확정";
            }
        }

        private void RefreshMorningVoteTallyUI()
        {
            bool showMorningCounts =
                matchManager != null &&
                currentVoteMode ==
                    VoteMode.MorningExile &&
                matchManager.CurrentPhase ==
                    MatchPhase.MorningVote;

            bool showMafiaCounts =
                matchManager != null &&
                currentVoteMode ==
                    VoteMode.MafiaKiller &&
                matchManager.CurrentPhase ==
                    MatchPhase.NightPreparation &&
                IsLocalAliveMafia();

            if (suspectIdentityLabels != null)
            {
                for (int i = 0;
                     i < suspectIdentityLabels.Length;
                     i++)
                {
                    PlayerIdentityLabel label =
                        suspectIdentityLabels[i];

                    if (label == null)
                        continue;

                    bool hasPlayer =
                        (showMorningCounts ||
                         showMafiaCounts) &&
                        suspectClientIds != null &&
                        i < suspectClientIds.Length &&
                        suspectClientIds[i] !=
                            ulong.MaxValue;

                    int voteCount = 0;

                    if (hasPlayer)
                    {
                        voteCount =
                            showMafiaCounts
                                ? matchManager
                                    .GetLocalMafiaKillerVoteCount(
                                        suspectClientIds[i]
                                    )
                                : matchManager
                                    .GetMorningVoteCount(
                                        suspectClientIds[i]
                                    );
                    }

                    label.SetVoteCount(
                        voteCount,
                        hasPlayer
                    );
                }
            }

            if (abstainButtonText == null ||
                !showMorningCounts)
            {
                return;
            }

            string selectionState =
                string.Empty;

            if (morningAbstainSelected)
            {
                selectionState =
                    morningVoteLocked
                        ? " · 확정"
                        : " · 선택";
            }

            abstainButtonText.text =
                $"기권 {matchManager.MorningAbstainVoteCount}표" +
                selectionState;
        }

        private void ClearSelectedVote()
        {
            if (suspectToggles == null)
                return;

            for (int i = 0; i < suspectToggles.Length; i++)
            {
                if (suspectToggles[i] != null)
                    suspectToggles[i].SetIsOnWithoutNotify(false);
            }
        }

        private void ToggleMic()
        {
            TryBindVivoxVoiceManager();

            if (vivoxVoiceManager != null)
            {
                vivoxVoiceManager.SetMicrophoneEnabled(
                    !vivoxVoiceManager.MicrophoneEnabled
                );

                SyncAudioStateFromVivox();
                return;
            }

            isMicOn = !isMicOn;
            UpdateAudioUI();
        }

        private void ToggleVoice()
        {
            TryBindVivoxVoiceManager();

            if (vivoxVoiceManager != null)
            {
                vivoxVoiceManager.SetVoiceOutputEnabled(
                    !vivoxVoiceManager.VoiceOutputEnabled
                );

                SyncAudioStateFromVivox();
                return;
            }

            isVoiceOn = !isVoiceOn;
            UpdateAudioUI();
        }

        private void UpdateAudioUI()
        {
            if (micIconImage != null)
            {
                micIconImage.sprite = isMicOn ? micOnSprite : micOffSprite;
                micIconImage.color = isMicOn ? Color.white : new Color(0.85f, 0.35f, 0.35f);
            }

            if (voiceIconImage != null)
            {
                voiceIconImage.sprite = isVoiceOn ? voiceOnSprite : voiceOffSprite;
                voiceIconImage.color = isVoiceOn ? Color.white : new Color(0.85f, 0.35f, 0.35f);
            }
        }

        private void OnLocalDetectiveResultReceived(ulong targetClientId, RoleId revealedRole)
        {
            string targetName = GetPublicPlayerName(targetClientId);
            string roleName = GetRoleDisplayName(revealedRole);

            AttachPersonalActionDetail(
                RoleActionType.DetectiveInspect,
                $"조사 결과: {targetName} - {roleName}"
            );

            ShowPrivateActionResult(
                $"<size=30>조사 결과</size>\n" +
                $"<size=42><b>{targetName}</b></size>\n" +
                $"<size=36>{roleName}</size>"
            );
        }

        private void ShowPrivateActionResult(string message)
        {
            if (privateActionResultRoot == null || privateActionResultText == null)
                return;

            if (privateActionResultCoroutine != null)
                StopCoroutine(privateActionResultCoroutine);

            privateActionResultText.text = message;
            privateActionResultRoot.SetActive(true);
            privateActionResultRoot.transform.SetAsLastSibling();

            privateActionResultCoroutine = StartCoroutine(HidePrivateActionResultRoutine());
        }

        private IEnumerator HidePrivateActionResultRoutine()
        {
            yield return new WaitForSecondsRealtime(privateActionResultDuration);

            privateActionResultCoroutine = null;
            HidePrivateActionResult();
        }

        private void HidePrivateActionResult()
        {
            if (privateActionResultCoroutine != null)
            {
                StopCoroutine(privateActionResultCoroutine);
                privateActionResultCoroutine = null;
            }

            if (privateActionResultText != null)
                privateActionResultText.text = string.Empty;

            if (privateActionResultRoot != null)
                privateActionResultRoot.SetActive(false);
        }

        private bool CanUseMafiaPreparationModes()
        {
            return matchManager != null &&
                   matchManager.CurrentPhase == MatchPhase.NightPreparation &&
                   matchManager.TryGetLocalPlayerMatchState(out PlayerMatchState localState) &&
                   localState.isAlive &&
                   localState.team == RoleTeam.Mafia;
        }

        private void RefreshMafiaDisguiseOptions()
        {
            if (matchManager == null || suspectToggles == null)
                return;

            int rowCount = Mathf.Min(
                suspectToggles.Length,
                matchManager.LocalMafiaDisguiseOptions.Count
            );

            for (int i = 0; i < rowCount; i++)
            {
                RoleWeaponId weaponId = matchManager.LocalMafiaDisguiseOptions[i];
                suspectOptionValues[i] = (int)weaponId;

                if (suspectRows[i] != null)
                    suspectRows[i].SetActive(true);

                string optionName = GetMafiaDisguiseOptionName(weaponId);

                if (suspectIdentityLabels[i] != null)
                    suspectIdentityLabels[i].SetPlainText(optionName);
                else if (suspectNameTexts[i] != null)
                    suspectNameTexts[i].text = optionName;

                if (suspectToggles[i] != null)
                {
                    suspectToggles[i].SetIsOnWithoutNotify(
                        weaponId == matchManager.LocalMafiaDisguiseWeapon
                    );
                }
            }

            UpdateMorningVoteInteractionState();
        }

        private bool HasSelectableValueAt(int index)
        {
            if (index < 0)
                return false;

            if (currentVoteMode == VoteMode.MafiaDisguise)
            {
                return suspectOptionValues != null &&
                       index < suspectOptionValues.Length &&
                       suspectOptionValues[index] >= 0;
            }

            return suspectClientIds != null &&
                   index < suspectClientIds.Length &&
                   suspectClientIds[index] != ulong.MaxValue;
        }

        private string GetMafiaDisguiseOptionName(
            RoleWeaponId weaponId)
        {
            switch (weaponId)
            {
                case RoleWeaponId.Pitchfork:
                    return "쇠스랑";

                case RoleWeaponId.Broom:
                    return "빗자루";

                case RoleWeaponId.Hammer:
                    return "망치";

                case RoleWeaponId.Mace:
                    return "철퇴";

                case RoleWeaponId.LargeSyringe:
                    return "대형 주사기";

                case RoleWeaponId.WoodenStaff:
                    return "나무 지팡이";

                case RoleWeaponId.Cross:
                    return "십자가";

                case RoleWeaponId.Torch:
                    return "횃불";

                case RoleWeaponId.MediumStaff:
                    return "영매 지팡이";

                case RoleWeaponId.HuntingAxe:
                    return "사냥 도끼";

                case RoleWeaponId.Shovel:
                    return "삽";

                default:
                    return weaponId.ToString();
            }
        }

        private string GetPublicPlayerName(ulong clientId)
        {
            if (matchManager == null || matchManager.PublicPlayerStates == null)
                return $"Player {clientId}";

            for (int i = 0; i < matchManager.PublicPlayerStates.Count; i++)
            {
                PlayerPublicState state = matchManager.PublicPlayerStates[i];

                if (state.clientId == clientId)
                    return state.playerName.ToString();
            }

            return $"Player {clientId}";
        }

        private string GetRoleDescription(RoleId role)
        {
            switch (role)
            {
                case RoleId.Farmer:
                    return "쇠스랑을 사용하는 주민입니다.\n밤마다 두 가지 직무를 수행합니다.";

                case RoleId.Cleaner:
                    return "빗자루를 사용하는 주민입니다.\n밤마다 두 가지 직무를 수행합니다.";

                case RoleId.Blacksmith:
                    return "망치를 사용하는 주민입니다.\n밤마다 두 가지 직무를 수행합니다.";

                case RoleId.Bailiff:
                    return "밤에 대상을 감금합니다.\n이동·공격·직업 행동을 제한합니다.";

                case RoleId.Doctor:
                    return "밤에 한 대상을 보호합니다.\n일반 살해와 독살만 막습니다.";

                case RoleId.Detective:
                    return "생존자의 육체를 조사합니다.\n대상의 직업을 확인합니다.";

                case RoleId.Drunkard:
                    return "밤 준비에 잠들 집을 고릅니다.\n미선택 시 자기 집에서 잡니다.";

                case RoleId.Exorcist:
                    return "감금된 영체를 처형합니다.\n게임 전체에서 한 번만 사용합니다.";

                case RoleId.Forensics:
                    return "사망자의 육체를 조사합니다.\n침입·피격·사인·도구를 확인합니다.";

                case RoleId.Medium:
                    return "최근 사망자 한 명을 선택합니다.\n밤에 비공개 음성으로 교신합니다.";

                case RoleId.Hunter:
                    return "생존자의 정문을 추적합니다.\n방문한 집과 출입 경로를 확인합니다.";

                case RoleId.Undertaker:
                    return "사망자의 집 정문을 봉인합니다.\n그날 밤 정문 출입을 막습니다.";

                case RoleId.Peddler:
                    return "밤마다 무작위 주민 도구를 받습니다.\n사용 후에만 진짜 여부를 압니다.";

                case RoleId.CurseCaster:
                    return "담당이면 저주 인형으로 살해합니다.\n비담당이면 가짜 주민 행동을 합니다.";

                case RoleId.Spy:
                    return "주민 직업과 도구로 위장합니다.\n담당이면 살해와 주민 능력 중 택일합니다.";

                case RoleId.Alchemist:
                    return "담당이면 육체나 정문에 독을 씁니다.\n비담당이면 가짜 주민 행동을 합니다.";

                case RoleId.Infiltrator:
                    return "뒷문과 뒷골목으로 침입합니다.\n담당이면 F키로 직접 살해합니다.";

                case RoleId.Thief:
                    return "사망자의 도구를 E키로 훔칩니다.\n실제 직업과 진영을 그대로 계승합니다.";

                case RoleId.SerialKiller:
                    return "매 밤 F키로 한 명을 직접 살해합니다.\n위장 도구를 쓰며 주민 1대1에서 승리합니다.";

                case RoleId.Martyr:
                    return "아침 투표로 추방되면 승리합니다.\n밤 사망이나 다른 처형은 인정되지 않습니다.";

                default:
                    return string.Empty;
            }
        }

        public static string GetRoleDisplayName(RoleId role)
        {
            switch (role)
            {
                case RoleId.Farmer:
                    return "농부";

                case RoleId.Cleaner:
                    return "잡역부";

                case RoleId.Blacksmith:
                    return "대장장이";

                case RoleId.Bailiff:
                    return "집행관";

                case RoleId.Doctor:
                    return "의원";

                case RoleId.Detective:
                    return "수사관";

                case RoleId.Drunkard:
                    return "주정뱅이";

                case RoleId.Exorcist:
                    return "퇴마사";

                case RoleId.Forensics:
                    return "검시관";

                case RoleId.Medium:
                    return "영매사";

                case RoleId.Hunter:
                    return "사냥꾼";

                case RoleId.Undertaker:
                    return "장의사";

                case RoleId.Peddler:
                    return "행상인";

                case RoleId.CurseCaster:
                    return "저주술사";

                case RoleId.Spy:
                    return "내통자";

                case RoleId.Alchemist:
                    return "마녀";

                case RoleId.Infiltrator:
                    return "잠입자";

                case RoleId.Thief:
                    return "도둑";

                case RoleId.SerialKiller:
                    return "살인귀";

                case RoleId.Martyr:
                    return "순교자";

                default:
                    return role.ToString();
            }
        }

        public static string GetTeamDisplayName(
            RoleTeam team)
        {
            switch (team)
            {
                case RoleTeam.Citizen:
                    return "주민";

                case RoleTeam.Mafia:
                    return "마녀";

                case RoleTeam.Neutral:
                    return "이방인";

                default:
                    return team.ToString();
            }
        }

        public static string GetTeamColorHex(
            RoleTeam team)
        {
            switch (team)
            {
                case RoleTeam.Citizen:
                    return "6BFFF4";

                case RoleTeam.Mafia:
                    return "DC4C4C";

                case RoleTeam.Neutral:
                    return "D3DC4C";

                default:
                    return "FFCC33";
            }
        }

        private Color GetRoleNameColor(
            RoleTeam team)
        {
            switch (team)
            {
                case RoleTeam.Citizen:
                    return CitizenRoleNameColor;

                case RoleTeam.Mafia:
                    return MafiaRoleNameColor;

                case RoleTeam.Neutral:
                    return NeutralRoleNameColor;

                default:
                    return UnassignedRoleNameColor;
            }
        }
    }
}
