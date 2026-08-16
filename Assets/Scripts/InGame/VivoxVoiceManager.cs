using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Vivox;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class VivoxVoiceManager : MonoBehaviour
{
    public const string MicrophonePreferenceKey =
        "Personal.Voice.MicrophoneEnabled";
    public const string VoiceOutputPreferenceKey =
        "Personal.Voice.OutputEnabled";
    public const string VoiceVolumePreferenceKey =
        "Personal.Voice.OutputVolume";

    public static VivoxVoiceManager Instance
    {
        get;
        private set;
    }

    [Header("Channel")]
    [Tooltip("Relay 방 코드가 없는 로컬 직접 연결 테스트에서 사용할 채널 이름입니다.")]
    [SerializeField]
    private string fallbackChannelName = "mafia-local-test";

    [Header("Default Audio State")]
    [SerializeField]
    private bool microphoneEnabledByDefault = true;

    [SerializeField]
    private bool voiceOutputEnabledByDefault = true;

    [Header("Night Positional Voice")]
    [Tooltip("Vivox에 로컬 영체 위치를 갱신하는 간격입니다.")]
    [Min(0.1f)]
    [SerializeField]
    private float positionalUpdateInterval = 0.25f;

    [Header("Output Volume")]
    [Range(-50, 50)]
    [SerializeField]
    private int enabledOutputVolume = 0;

    [Range(-50, 50)]
    [SerializeField]
    private int mutedOutputVolume = -50;

    private MatchManager matchManager;
    private PlayerSpirit localSpirit;

    private PlayerMatchState localPlayerState;
    private bool hasLocalPlayerState;

    private bool microphoneEnabled;
    private bool voiceOutputEnabled;

    private bool vivoxReady;
    private bool destroyed;
    private bool voiceStateRefreshPending;
    private bool voiceStateRetryPending;
    private bool applyingVoiceState;
    private bool participantEventsBound;

    private string morningGroupChannelName = string.Empty;
    private string nightPositionalChannelName = string.Empty;
    private string requestedMediumChannelName = string.Empty;
    private string activeMediumChannelName = string.Empty;

    private bool morningGroupChannelJoined;
    private bool nightPositionalChannelJoined;

    private readonly HashSet<string> autoMutedPositionalPlayerIds =
        new HashSet<string>();
    private readonly HashSet<string> manuallyMutedDisplayNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private float nextSpiritSearchTime;
    private float nextPositionUpdateTime;
    private float nextVoiceStateRetryTime;

    private const float VoiceStateRetryInterval = 1.5f;

    public event Action AudioStateChanged;
    public event Action<bool> ReadyChanged;
    public event Action ParticipantRosterChanged;

    public bool MicrophoneEnabled => microphoneEnabled;
    public bool VoiceOutputEnabled => voiceOutputEnabled;
    public int OutputVolume => enabledOutputVolume;
    public bool IsReady => vivoxReady;
    public string ChannelName => morningGroupChannelName;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        microphoneEnabled = PlayerPrefs.GetInt(
            MicrophonePreferenceKey,
            microphoneEnabledByDefault ? 1 : 0
        ) != 0;
        voiceOutputEnabled = PlayerPrefs.GetInt(
            VoiceOutputPreferenceKey,
            voiceOutputEnabledByDefault ? 1 : 0
        ) != 0;
        enabledOutputVolume = Mathf.Clamp(
            PlayerPrefs.GetInt(
                VoiceVolumePreferenceKey,
                enabledOutputVolume
            ),
            -50,
            50
        );
    }

    private async void Start()
    {
        await InitializeVivoxAsync();
    }

    private void Update()
    {
        if (!vivoxReady)
            return;

        if (!applyingVoiceState &&
            Time.unscaledTime >= nextVoiceStateRetryTime &&
            (voiceStateRetryPending ||
             !string.IsNullOrWhiteSpace(requestedMediumChannelName) &&
             (!VivoxService.Instance.ActiveChannels.ContainsKey(
                  requestedMediumChannelName) ||
              activeMediumChannelName != requestedMediumChannelName)))
        {
            voiceStateRetryPending = false;
            nextVoiceStateRetryTime =
                Time.unscaledTime + VoiceStateRetryInterval;

            RequestVoiceStateRefresh();
        }

        if (localSpirit == null && Time.unscaledTime >= nextSpiritSearchTime)
        {
            nextSpiritSearchTime = Time.unscaledTime + 0.5f;
            TryBindLocalSpirit();
        }

        if (Time.unscaledTime < nextPositionUpdateTime)
            return;

        nextPositionUpdateTime =
            Time.unscaledTime + Mathf.Max(0.1f, positionalUpdateInterval);

        UpdateNightVoicePosition();
    }

    private void OnDestroy()
    {
        destroyed = true;

        UnbindParticipantEvents();
        UnbindLocalSpirit();
        UnbindMatchManager();

        if (Instance == this)
            Instance = null;

        _ = ShutdownAsync();
    }

    public void SetMicrophoneEnabled(bool enabled)
    {
        microphoneEnabled = enabled;

        PlayerPrefs.SetInt(
            MicrophonePreferenceKey,
            enabled ? 1 : 0
        );
        PlayerPrefs.Save();

        ApplyInputMute();
        RequestVoiceStateRefresh();

        AudioStateChanged?.Invoke();
    }

    public void SetVoiceOutputEnabled(bool enabled)
    {
        voiceOutputEnabled = enabled;

        PlayerPrefs.SetInt(
            VoiceOutputPreferenceKey,
            enabled ? 1 : 0
        );
        PlayerPrefs.Save();

        ApplyOutputVolume();
        AudioStateChanged?.Invoke();
    }

    public void SetOutputVolume(int volume)
    {
        enabledOutputVolume = Mathf.Clamp(
            volume,
            -50,
            50
        );

        PlayerPrefs.SetInt(
            VoiceVolumePreferenceKey,
            enabledOutputVolume
        );
        PlayerPrefs.Save();

        ApplyOutputVolume();
        AudioStateChanged?.Invoke();
    }

    public bool IsPlayerLocallyMuted(string displayName)
    {
        string normalizedName = NormalizeDisplayName(displayName);

        return normalizedName.Length > 0 &&
               manuallyMutedDisplayNames.Contains(normalizedName);
    }

    public bool IsPlayerVoiceConnected(string displayName)
    {
        string normalizedName = NormalizeDisplayName(displayName);

        if (!vivoxReady || normalizedName.Length == 0)
            return false;

        foreach (var channel in VivoxService.Instance.ActiveChannels)
        {
            foreach (VivoxParticipant participant in channel.Value)
            {
                if (participant == null || participant.IsSelf)
                    continue;

                if (string.Equals(
                        NormalizeDisplayName(participant.DisplayName),
                        normalizedName,
                        StringComparison.OrdinalIgnoreCase) &&
                    participant.IsInAudio)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void SetPlayerLocallyMuted(
        string displayName,
        bool muted)
    {
        string normalizedName = NormalizeDisplayName(displayName);

        if (normalizedName.Length == 0)
            return;

        if (muted)
            manuallyMutedDisplayNames.Add(normalizedName);
        else
            manuallyMutedDisplayNames.Remove(normalizedName);

        ApplyManualMuteStates();
        RefreshMediumPositionalAutoMutes();
        ApplyManualMuteStates();
        ParticipantRosterChanged?.Invoke();
    }

    private async Task InitializeVivoxAsync()
    {
        try
        {
            await WaitForLocalMatchStateAsync();

            if (destroyed)
                return;

            await EnsureUnityServicesAsync();

            if (destroyed)
                return;

            await VivoxService.Instance.InitializeAsync();

            LoginOptions loginOptions = new LoginOptions
            {
                DisplayName = BuildDisplayName(localPlayerState.playerName.ToString())
            };

            await VivoxService.Instance.LoginAsync(loginOptions);

            morningGroupChannelName = BuildChannelName();
            nightPositionalChannelName =
                SanitizeIdentifier($"{morningGroupChannelName}-night");

            await VivoxService.Instance.SetChannelTransmissionModeAsync(
                TransmissionMode.None
            );

            if (destroyed)
                return;

            vivoxReady = true;

            BindParticipantEvents();
            ApplyInputMute();
            ApplyOutputVolume();
            TryBindLocalSpirit();
            RequestVoiceStateRefresh();

            ReadyChanged?.Invoke(true);
            AudioStateChanged?.Invoke();

            Debug.Log(
                $"Vivox ready - Morning: {morningGroupChannelName}, " +
                $"Night: {nightPositionalChannelName}"
            );
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "Vivox 초기화 또는 로그인 실패\n" +
                exception
            );
        }
    }

    private async Task WaitForLocalMatchStateAsync()
    {
        while (!destroyed)
        {
            if (matchManager == null)
            {
                MatchManager foundManager =
                    MatchManager.Instance != null
                        ? MatchManager.Instance
                        : FindFirstObjectByType<MatchManager>();

                if (foundManager != null)
                    BindMatchManager(foundManager);
            }

            if (matchManager != null &&
                matchManager.TryGetLocalPlayerMatchState(
                    out PlayerMatchState state))
            {
                localPlayerState = state;
                hasLocalPlayerState = true;
                return;
            }

            await Task.Yield();
        }
    }

    private async Task EnsureUnityServicesAsync()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();

        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    private void BindMatchManager(MatchManager target)
    {
        if (matchManager == target)
            return;

        UnbindMatchManager();

        matchManager = target;
        matchManager.PhaseChanged += OnPhaseChanged;
        matchManager.LocalPlayerMatchStateReceived +=
            OnLocalPlayerMatchStateReceived;
        matchManager.LocalMediumCommunicationStarted +=
            OnLocalMediumCommunicationStarted;
        matchManager.LocalMediumCommunicationEnded +=
            OnLocalMediumCommunicationEnded;
        matchManager.LampUsedPlayersChanged +=
            OnLampUsedPlayersChanged;
    }

    private void UnbindMatchManager()
    {
        if (matchManager == null)
            return;

        matchManager.PhaseChanged -= OnPhaseChanged;
        matchManager.LocalPlayerMatchStateReceived -=
            OnLocalPlayerMatchStateReceived;
        matchManager.LocalMediumCommunicationStarted -=
            OnLocalMediumCommunicationStarted;
        matchManager.LocalMediumCommunicationEnded -=
            OnLocalMediumCommunicationEnded;
        matchManager.LampUsedPlayersChanged -=
            OnLampUsedPlayersChanged;

        matchManager = null;
    }

    private void BindParticipantEvents()
    {
        if (participantEventsBound)
            return;

        VivoxService.Instance.ParticipantAddedToChannel +=
            OnParticipantChanged;
        VivoxService.Instance.ParticipantRemovedFromChannel +=
            OnParticipantChanged;

        participantEventsBound = true;
    }

    private void UnbindParticipantEvents()
    {
        if (!participantEventsBound)
            return;

        VivoxService.Instance.ParticipantAddedToChannel -=
            OnParticipantChanged;
        VivoxService.Instance.ParticipantRemovedFromChannel -=
            OnParticipantChanged;

        participantEventsBound = false;
    }

    private void OnPhaseChanged(
        MatchPhase previous,
        MatchPhase current)
    {
        if (current != MatchPhase.NightAction)
            requestedMediumChannelName = string.Empty;

        RequestVoiceStateRefresh();
    }

    private void OnLocalPlayerMatchStateReceived(
        PlayerMatchState state)
    {
        localPlayerState = state;
        hasLocalPlayerState = true;

        RequestVoiceStateRefresh();
    }

    private void OnLampUsedPlayersChanged()
    {
        RequestVoiceStateRefresh();
    }

    private void OnLocalMediumCommunicationStarted(
        string mediumChannelName,
        ulong partnerClientId)
    {
        if (string.IsNullOrWhiteSpace(mediumChannelName))
            return;

        requestedMediumChannelName =
            SanitizeIdentifier(mediumChannelName);

        RequestVoiceStateRefresh();
    }

    private void OnLocalMediumCommunicationEnded()
    {
        requestedMediumChannelName = string.Empty;
        RequestVoiceStateRefresh();
    }

    private void OnParticipantChanged(
        VivoxParticipant participant)
    {
        RefreshMediumPositionalAutoMutes();
        ApplyManualMuteStates();

        bool participantJoinedMediumChannel =
            participant != null &&
            ((!string.IsNullOrWhiteSpace(activeMediumChannelName) &&
              participant.ChannelName == activeMediumChannelName) ||
             (!string.IsNullOrWhiteSpace(requestedMediumChannelName) &&
              participant.ChannelName == requestedMediumChannelName));

        if (participantJoinedMediumChannel)
            RequestVoiceStateRefresh();

        ParticipantRosterChanged?.Invoke();
    }

    private void RequestVoiceStateRefresh()
    {
        voiceStateRefreshPending = true;

        if (!vivoxReady ||
            applyingVoiceState ||
            destroyed)
        {
            return;
        }

        _ = ApplyVoiceStateLoopAsync();
    }

    private async Task ApplyVoiceStateLoopAsync()
    {
        applyingVoiceState = true;

        try
        {
            while (voiceStateRefreshPending &&
                   vivoxReady &&
                   !destroyed)
            {
                voiceStateRefreshPending = false;

                MatchPhase phase =
                    matchManager != null
                        ? matchManager.CurrentPhase
                        : MatchPhase.None;

                bool isMorningVoicePhase =
                    IsMorningVoicePhase(phase);

                bool isNightAction =
                    phase == MatchPhase.NightAction;

                bool localPlayerAlive =
                    hasLocalPlayerState &&
                    localPlayerState.isAlive;

                bool shouldJoinMorningGroup =
                    isMorningVoicePhase;

                bool shouldJoinNightPositional =
                    isNightAction &&
                    localPlayerAlive &&
                    matchManager != null &&
                    matchManager.HasPlayerUsedHouseLampThisNight(
                        localPlayerState.clientId
                    );

                string desiredMediumChannel =
                    isNightAction
                        ? requestedMediumChannelName
                        : string.Empty;

                SynchronizeJoinedChannelState(
                    desiredMediumChannel
                );

                await VivoxService.Instance.SetChannelTransmissionModeAsync(
                    TransmissionMode.None
                );

                if (destroyed || !vivoxReady)
                    break;

                await ApplyChannelMembershipAsync(
                    shouldJoinMorningGroup,
                    shouldJoinNightPositional,
                    desiredMediumChannel
                );

                if (destroyed || !vivoxReady)
                    break;

                UpdateNightVoicePosition();
                RefreshMediumPositionalAutoMutes();

                await ApplyTransmissionAsync(
                    phase,
                    localPlayerAlive
                );

                nextVoiceStateRetryTime =
                    Time.unscaledTime + VoiceStateRetryInterval;
                voiceStateRetryPending = false;
            }
        }
        catch (Exception exception)
        {
            nextVoiceStateRetryTime =
                Time.unscaledTime + VoiceStateRetryInterval;
            voiceStateRetryPending = true;

            Debug.LogWarning(
                "Vivox 채널 상태 갱신 실패\n" +
                exception
            );
        }
        finally
        {
            applyingVoiceState = false;

            if (voiceStateRefreshPending &&
                vivoxReady &&
                !destroyed)
            {
                _ = ApplyVoiceStateLoopAsync();
            }
        }
    }

    private void SynchronizeJoinedChannelState(
        string desiredMediumChannel)
    {
        morningGroupChannelJoined =
            !string.IsNullOrWhiteSpace(morningGroupChannelName) &&
            VivoxService.Instance.ActiveChannels.ContainsKey(
                morningGroupChannelName
            );

        nightPositionalChannelJoined =
            !string.IsNullOrWhiteSpace(nightPositionalChannelName) &&
            VivoxService.Instance.ActiveChannels.ContainsKey(
                nightPositionalChannelName
            );

        if (!string.IsNullOrWhiteSpace(activeMediumChannelName) &&
            !VivoxService.Instance.ActiveChannels.ContainsKey(
                activeMediumChannelName
            ))
        {
            activeMediumChannelName = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(activeMediumChannelName) &&
            !string.IsNullOrWhiteSpace(desiredMediumChannel) &&
            VivoxService.Instance.ActiveChannels.ContainsKey(
                desiredMediumChannel
            ))
        {
            activeMediumChannelName = desiredMediumChannel;
        }
    }

    private async Task ApplyChannelMembershipAsync(
        bool shouldJoinMorningGroup,
        bool shouldJoinNightPositional,
        string desiredMediumChannel)
    {
        if (morningGroupChannelJoined &&
            !shouldJoinMorningGroup)
        {
            await VivoxService.Instance.LeaveChannelAsync(
                morningGroupChannelName
            );

            morningGroupChannelJoined = false;

            if (destroyed || !vivoxReady)
                return;
        }

        if (!string.IsNullOrWhiteSpace(activeMediumChannelName) &&
            activeMediumChannelName != desiredMediumChannel)
        {
            UnmuteAllAutoMutedPositionalParticipants();

            await VivoxService.Instance.LeaveChannelAsync(
                activeMediumChannelName
            );

            activeMediumChannelName = string.Empty;

            if (destroyed || !vivoxReady)
                return;
        }

        if (nightPositionalChannelJoined &&
            !shouldJoinNightPositional)
        {
            UnmuteAllAutoMutedPositionalParticipants();

            await VivoxService.Instance.LeaveChannelAsync(
                nightPositionalChannelName
            );

            nightPositionalChannelJoined = false;

            if (destroyed || !vivoxReady)
                return;
        }

        /*
         * Vivox 권장 순서에 맞춰 3D 채널을 먼저 참가한 뒤
         * 2D 그룹 채널을 참가한다.
         */
        if (shouldJoinNightPositional &&
            !nightPositionalChannelJoined)
        {
            Channel3DProperties properties =
                new Channel3DProperties();

            await VivoxService.Instance.JoinPositionalChannelAsync(
                nightPositionalChannelName,
                ChatCapability.AudioOnly,
                properties
            );

            nightPositionalChannelJoined = true;

            if (destroyed || !vivoxReady)
                return;
        }

        if (shouldJoinMorningGroup &&
            !morningGroupChannelJoined)
        {
            await VivoxService.Instance.JoinGroupChannelAsync(
                morningGroupChannelName,
                ChatCapability.AudioOnly
            );

            morningGroupChannelJoined = true;

            if (destroyed || !vivoxReady)
                return;
        }

        if (!string.IsNullOrWhiteSpace(desiredMediumChannel) &&
            activeMediumChannelName != desiredMediumChannel)
        {
            await VivoxService.Instance.JoinGroupChannelAsync(
                desiredMediumChannel,
                ChatCapability.AudioOnly
            );

            activeMediumChannelName =
                desiredMediumChannel;
        }
    }

    private async Task ApplyTransmissionAsync(
        MatchPhase phase,
        bool localPlayerAlive)
    {
        if (!microphoneEnabled)
        {
            await VivoxService.Instance.SetChannelTransmissionModeAsync(
                TransmissionMode.None
            );

            return;
        }

        if (IsMorningVoicePhase(phase) &&
            localPlayerAlive &&
            morningGroupChannelJoined)
        {
            await VivoxService.Instance.SetChannelTransmissionModeAsync(
                TransmissionMode.Single,
                morningGroupChannelName
            );

            return;
        }

        if (phase != MatchPhase.NightAction)
        {
            await VivoxService.Instance.SetChannelTransmissionModeAsync(
                TransmissionMode.None
            );

            return;
        }

        bool canTransmitToNightPositional =
            localPlayerAlive &&
            nightPositionalChannelJoined &&
            matchManager != null &&
            matchManager.HasPlayerUsedHouseLampThisNight(
                localPlayerState.clientId
            );

        bool canTransmitToMedium =
            !string.IsNullOrWhiteSpace(
                activeMediumChannelName
            );

        if (canTransmitToNightPositional &&
            canTransmitToMedium)
        {
            await VivoxService.Instance.SetChannelTransmissionModeAsync(
                TransmissionMode.All
            );

            return;
        }

        if (canTransmitToMedium)
        {
            await VivoxService.Instance.SetChannelTransmissionModeAsync(
                TransmissionMode.Single,
                activeMediumChannelName
            );

            return;
        }

        if (canTransmitToNightPositional)
        {
            await VivoxService.Instance.SetChannelTransmissionModeAsync(
                TransmissionMode.Single,
                nightPositionalChannelName
            );

            return;
        }

        await VivoxService.Instance.SetChannelTransmissionModeAsync(
            TransmissionMode.None
        );
    }

    private bool IsMorningVoicePhase(
        MatchPhase phase)
    {
        return phase == MatchPhase.MorningDiscussion ||
               phase == MatchPhase.MorningVote ||
               phase == MatchPhase.MorningVoteResult;
    }

    private void TryBindLocalSpirit()
    {
        PlayerSpirit[] spirits =
            FindObjectsByType<PlayerSpirit>(
                FindObjectsSortMode.None
            );

        for (int i = 0; i < spirits.Length; i++)
        {
            PlayerSpirit candidate = spirits[i];

            if (candidate == null ||
                !candidate.IsOwner)
            {
                continue;
            }

            BindLocalSpirit(candidate);
            return;
        }
    }

    private void BindLocalSpirit(
        PlayerSpirit target)
    {
        if (localSpirit == target)
            return;

        UnbindLocalSpirit();

        localSpirit = target;
        localSpirit.NightVoicePermissionChanged +=
            OnNightVoicePermissionChanged;

        UpdateNightVoicePosition();
        RequestVoiceStateRefresh();
    }

    private void UnbindLocalSpirit()
    {
        if (localSpirit == null)
            return;

        localSpirit.NightVoicePermissionChanged -=
            OnNightVoicePermissionChanged;

        localSpirit = null;
    }

    private void OnNightVoicePermissionChanged(
        bool canUseNightVoice)
    {
        /*
         * 야간 음성 채널 참가 조건은 각 플레이어의 횃불 사용 상태다.
         * 이 이벤트는 상태 갱신 계기로 사용한다.
         */
        RequestVoiceStateRefresh();
    }

    private void UpdateNightVoicePosition()
    {
        if (!vivoxReady ||
            !nightPositionalChannelJoined ||
            localSpirit == null)
        {
            return;
        }

        try
        {
            VivoxService.Instance.Set3DPosition(
                localSpirit.gameObject,
                nightPositionalChannelName
            );
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "Vivox 야간 위치 갱신 실패\n" +
                exception
            );
        }
    }

    private void RefreshMediumPositionalAutoMutes()
    {
        if (!vivoxReady ||
            string.IsNullOrWhiteSpace(
                activeMediumChannelName) ||
            !nightPositionalChannelJoined)
        {
            UnmuteAllAutoMutedPositionalParticipants();
            return;
        }

        if (!VivoxService.Instance.ActiveChannels.ContainsKey(
                activeMediumChannelName) ||
            !VivoxService.Instance.ActiveChannels.ContainsKey(
                nightPositionalChannelName))
        {
            return;
        }

        HashSet<string> mediumParticipantIds =
            new HashSet<string>();

        foreach (VivoxParticipant participant in
                 VivoxService.Instance.ActiveChannels[
                     activeMediumChannelName])
        {
            if (participant == null ||
                participant.IsSelf)
            {
                continue;
            }

            mediumParticipantIds.Add(
                participant.PlayerId
            );
        }

        HashSet<string> stillAutoMuted =
            new HashSet<string>();

        foreach (VivoxParticipant participant in
                 VivoxService.Instance.ActiveChannels[
                     nightPositionalChannelName])
        {
            if (participant == null ||
                participant.IsSelf)
            {
                continue;
            }

            string playerId =
                participant.PlayerId;

            bool shouldAutoMute =
                mediumParticipantIds.Contains(
                    playerId
                );

            if (shouldAutoMute)
            {
                if (!participant.IsMuted &&
                    !autoMutedPositionalPlayerIds.Contains(
                        playerId))
                {
                    participant.MutePlayerLocally();
                    autoMutedPositionalPlayerIds.Add(
                        playerId
                    );
                }

                if (autoMutedPositionalPlayerIds.Contains(
                        playerId))
                {
                    stillAutoMuted.Add(
                        playerId
                    );
                }
            }
            else if (autoMutedPositionalPlayerIds.Contains(
                         playerId))
            {
                if (!IsManuallyMuted(participant))
                    participant.UnmutePlayerLocally();
            }
        }

        autoMutedPositionalPlayerIds.Clear();

        foreach (string playerId in stillAutoMuted)
            autoMutedPositionalPlayerIds.Add(playerId);
    }

    private void UnmuteAllAutoMutedPositionalParticipants()
    {
        if (autoMutedPositionalPlayerIds.Count == 0)
            return;

        if (VivoxService.Instance.ActiveChannels.ContainsKey(
                nightPositionalChannelName))
        {
            foreach (VivoxParticipant participant in
                     VivoxService.Instance.ActiveChannels[
                         nightPositionalChannelName])
            {
                if (participant == null ||
                    participant.IsSelf ||
                    !autoMutedPositionalPlayerIds.Contains(
                        participant.PlayerId))
                {
                    continue;
                }

                if (!IsManuallyMuted(participant))
                    participant.UnmutePlayerLocally();
            }
        }

        autoMutedPositionalPlayerIds.Clear();
    }

    private void ApplyManualMuteStates()
    {
        if (!vivoxReady)
            return;

        foreach (var channel in VivoxService.Instance.ActiveChannels)
        {
            foreach (VivoxParticipant participant in channel.Value)
            {
                if (participant == null || participant.IsSelf)
                    continue;

                if (IsManuallyMuted(participant))
                {
                    participant.MutePlayerLocally();
                    continue;
                }

                bool isAutoMutedInPositionalChannel =
                    channel.Key == nightPositionalChannelName &&
                    autoMutedPositionalPlayerIds.Contains(
                        participant.PlayerId
                    );

                if (!isAutoMutedInPositionalChannel)
                {
                    participant.UnmutePlayerLocally();
                }
            }
        }
    }

    private bool IsManuallyMuted(
        VivoxParticipant participant)
    {
        return participant != null &&
               manuallyMutedDisplayNames.Contains(
                   NormalizeDisplayName(participant.DisplayName));
    }

    private static string NormalizeDisplayName(
        string displayName)
    {
        return string.IsNullOrWhiteSpace(displayName)
            ? string.Empty
            : displayName.Trim();
    }

    private void ApplyInputMute()
    {
        if (!vivoxReady)
            return;

        try
        {
            if (microphoneEnabled)
                VivoxService.Instance.UnmuteInputDevice();
            else
                VivoxService.Instance.MuteInputDevice();
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "Vivox 마이크 상태 변경 실패\n" +
                exception
            );
        }
    }

    private void ApplyOutputVolume()
    {
        if (!vivoxReady)
            return;

        try
        {
            VivoxService.Instance.SetOutputDeviceVolume(
                voiceOutputEnabled
                    ? enabledOutputVolume
                    : mutedOutputVolume
            );
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "Vivox 출력 음량 변경 실패\n" +
                exception
            );
        }
    }

    private string BuildChannelName()
    {
        string roomKey = string.Empty;

        if (RelayConnectionManager.Instance != null)
            roomKey = RelayConnectionManager.Instance.CurrentJoinCode;

        if (string.IsNullOrWhiteSpace(roomKey))
            roomKey = fallbackChannelName;

        return SanitizeIdentifier(
            $"mafia-{roomKey}"
        );
    }

    private string BuildDisplayName(
        string playerName)
    {
        string normalized =
            string.IsNullOrWhiteSpace(playerName)
                ? "Player"
                : playerName.Trim();

        if (normalized.Length > 60)
            normalized = normalized.Substring(0, 60);

        return normalized;
    }

    private string SanitizeIdentifier(
        string value)
    {
        StringBuilder builder =
            new StringBuilder();

        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];

            if (char.IsLetterOrDigit(character) ||
                character == '-' ||
                character == '_')
            {
                builder.Append(
                    char.ToLowerInvariant(character)
                );
            }
        }

        if (builder.Length == 0)
            builder.Append("mafia-local-test");

        return builder.ToString();
    }

    private async Task ShutdownAsync()
    {
        if (!vivoxReady)
            return;

        vivoxReady = false;
        voiceStateRefreshPending = false;
        voiceStateRetryPending = false;

        UnmuteAllAutoMutedPositionalParticipants();

        requestedMediumChannelName = string.Empty;
        activeMediumChannelName = string.Empty;
        morningGroupChannelJoined = false;
        nightPositionalChannelJoined = false;

        try
        {
            await VivoxService.Instance.SetChannelTransmissionModeAsync(
                TransmissionMode.None
            );

            await VivoxService.Instance.LeaveAllChannelsAsync();
            await VivoxService.Instance.LogoutAsync();
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "Vivox 종료 처리 실패\n" +
                exception
            );
        }
        finally
        {
            ReadyChanged?.Invoke(false);
        }
    }
}
