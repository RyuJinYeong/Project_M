using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using System.Text;
using Unity.Collections;

public enum TextChatChannel : byte
{
    MorningPublic,
    MafiaNight,
    MediumNight,
    Dead
}

[RequireComponent(typeof(NetworkObject))]
public class MatchManager : NetworkBehaviour
{
    public enum PersonalActionRecordKind : byte
    {
        RoleAction,
        DrunkardSleepSelection,
        ThiefInheritance,
        CurseDollPickup,
        CurseDollInstall,
        MafiaTeamKill,
        MafiaInterference,
        MafiaInterferenceSummary
    }

    public struct PersonalActionRecordData :
        INetworkSerializable
    {
        public int day;
        public PersonalActionRecordKind kind;
        public ulong actorClientId;
        public RoleId role;
        public RoleActionType actionType;
        public ulong targetClientId;
        public int targetHouseId;
        public int targetPointId;
        public RoleId inheritedRole;
        public RoleTeam inheritedTeam;
        public bool effectSucceeded;
        public bool wasFake;
        public float dutyFailureChance;
        public float stabilityFailureChance;
        public float finalFailureChance;

        public void NetworkSerialize<T>(
            BufferSerializer<T> serializer)
            where T : IReaderWriter
        {
            serializer.SerializeValue(ref day);
            serializer.SerializeValue(ref kind);
            serializer.SerializeValue(ref actorClientId);
            serializer.SerializeValue(ref role);
            serializer.SerializeValue(ref actionType);
            serializer.SerializeValue(ref targetClientId);
            serializer.SerializeValue(ref targetHouseId);
            serializer.SerializeValue(ref targetPointId);
            serializer.SerializeValue(ref inheritedRole);
            serializer.SerializeValue(ref inheritedTeam);
            serializer.SerializeValue(ref effectSucceeded);
            serializer.SerializeValue(ref wasFake);
            serializer.SerializeValue(ref dutyFailureChance);
            serializer.SerializeValue(ref stabilityFailureChance);
            serializer.SerializeValue(ref finalFailureChance);
        }
    }

    public enum MatchWinner : byte
    {
        None,
        Citizen,
        Mafia,
        SerialKiller,
        Martyr,
        Thief
    }

    private struct HouseOwnerDisplayState
    {
        public ulong clientId;
        public FixedString64Bytes playerName;
    }

    private readonly Dictionary<int, HouseOwnerDisplayState> houseOwnerDisplayStates = new Dictionary<int, HouseOwnerDisplayState>();

    private enum NightKillMethod : byte
    {
        None,
        Direct,
        Poison,
        CurseDoll,
        Exorcism
    }

    private struct NightDeathEvidence
    {
        public NightKillMethod killMethod;
        public int weaponIndex;
    }

    private sealed class HunterRouteRecord
    {
        public ulong targetClientId;
    }

    private struct NightDutyAssignment
    {
        public int houseId;
        public int pointId;
        public string dutyName;
        public bool completed;
    }

    public static MatchManager Instance { get; private set; }

    private const ulong NoClientId = ulong.MaxValue;
    private const ulong AbstainVoteTarget = ulong.MaxValue;
    private const int MaximumSpyCitizenAbilityUses = 2;
    private const int NightDutyAssignmentsPerPlayer = 2;
    private const int MaximumFakeNightDutyUsesPerNight = 1;
    private const int InitialVillageStabilityPercent = 75;
    private const float MaximumVillageStabilityFailureChance = 0.2f;
    private const float NormalSpiritVisibilityDistance = 26f;
    private const float UnstableSpiritVisibilityDistance = 18f;
    private const float CollapsedSpiritVisibilityDistance = 11f;
    private const float FullyLitSpiritVisibilityBonus = 6f;
    private const int DimmedHouseOwnerLabelStability = 50;
    private const int LocalOnlyHouseOwnerLabelStability = 25;
    private const float DimmedHouseOwnerLabelOpacity = 0.5f;
    private const float MorningVoteAllConfirmedCountdown = 5f;
    private const float MinimumTextChatInterval = 0.75f;
    private const int MaximumTextChatCharacterCount = 120;
    private const int MaximumTextChatUtf8ByteCount = 480;

    private static readonly RoleWeaponId[] citizenWeaponPool =
    {
        RoleWeaponId.Pitchfork,
        RoleWeaponId.Broom,
        RoleWeaponId.Hammer,
        RoleWeaponId.Mace,
        RoleWeaponId.LargeSyringe,
        RoleWeaponId.WoodenStaff,
        RoleWeaponId.Bottle,
        RoleWeaponId.Cross,
        RoleWeaponId.Torch,
        RoleWeaponId.MediumStaff,
        RoleWeaponId.HuntingAxe,
        RoleWeaponId.Shovel
    };

    /*
     * 마녀 진영 위장 무기는 술병을 포함한
     * 모든 고정 주민 무기를 사용한다.
     * 내통자는 이 위장 시스템을 사용하지 않는다.
     */
    private static readonly RoleWeaponId[]
        mafiaDisguiseWeaponPool =
            citizenWeaponPool;

    private static readonly RoleId[] randomMafiaRolePool =
    {
        RoleId.CurseCaster,
        RoleId.Spy,
        RoleId.Alchemist,
        RoleId.Infiltrator
    };

    private static readonly RoleId[] randomNeutralRolePool =
    {
        RoleId.Thief,
        RoleId.SerialKiller,
        RoleId.Martyr
    };

    private static readonly RoleId[] randomCitizenRolePool =
    {
        RoleId.Farmer,
        RoleId.Cleaner,
        RoleId.Blacksmith,
        RoleId.Doctor,
        RoleId.Bailiff,
        RoleId.Exorcist,
        RoleId.Detective,
        RoleId.Forensics,
        RoleId.Medium,
        RoleId.Hunter,
        RoleId.Undertaker,
        RoleId.Peddler,
        RoleId.Drunkard
    };

    [Header("Dropped Tool")]
    [SerializeField] private GameObject droppedRoleToolPrefab;

    [Header("Role Actions")]
    [Min(0.1f)]
    [SerializeField] private float roleActionDistance = 3f;

    [Min(0f)]
    [SerializeField] private float roleActionServerTolerance = 2f;

    public float RoleActionDistance => roleActionDistance;

    private readonly HashSet<ulong> completedNightRoleActions = new HashSet<ulong>();
    private readonly HashSet<ulong> consumedExorcismPlayers = new HashSet<ulong>();

    private readonly Dictionary<ulong, int>
        spyCitizenAbilityUseCountByClient =
            new Dictionary<ulong, int>();

    /*
     * 행상인의 도구 진위는 서버만 보관한다.
     * true는 진짜, false는 가짜다.
     */
    private readonly Dictionary<ulong, bool>
        peddlerRealActionByClient =
            new Dictionary<ulong, bool>();

    public event Action<RoleActionType, bool> LocalRoleActionResultReceived;
    public event Action<PersonalActionRecordData>
        LocalPersonalActionRecorded;
    public event Action LocalAbilityFailureChanceChanged;

    public event Action<ulong, RoleId> LocalDetectiveResultReceived;

    public event Action<string> LocalForensicsResultReceived;
    public event Action<string> LocalHunterRouteResultReceived;
    public event Action<ulong> LocalDrunkardSleepTargetChanged;

    public event Action<string, ulong>
        LocalMediumCommunicationStarted;

    public event Action
        LocalMediumCommunicationEnded;

    public event Action<
        TextChatChannel,
        ulong,
        string,
        string>
        LocalTextChatMessageReceived;

    public event Action
        LocalMafiaDisguiseOptionsChanged;

    public event Action
        LocalMafiaKillerVotesChanged;

    [Header("Village")]
    [SerializeField] private VillageGenerator villageGenerator;
    [SerializeField] private VillageHouseRegistry houseRegistry;

    [Header("Player Prefabs")]
    [SerializeField] private GameObject playerBodyPrefab;
    [SerializeField] private GameObject playerSpiritPrefab;

    [Header("House Tracking")]
    [SerializeField] private float houseCheckInterval = 0.1f;

    [Header("House Lamp")]
    [Min(0.1f)]
    [SerializeField] private float lampInteractionDistance = 2f;

    [Min(0f)]
    [SerializeField] private float lampServerDistanceTolerance = 0.5f;

    [Header("Phase Durations")]
    [SerializeField] private float morningDiscussionDuration = 60f;
    [SerializeField] private float morningVoteDuration = 60f;
    [SerializeField] private float morningVoteResultDuration = 5f;
    [SerializeField] private float nightPreparationDuration = 15f;
    [SerializeField] private float nightActionDuration = 60f;
    [SerializeField] private float nightResultDuration = 5f;

    [Min(1f)]
    [SerializeField] private float finalDuelPreparationDuration = 5f;

    [Header("Role Assignment")]
    [Min(10f)]
    [SerializeField]
    private float roleAssignmentConfirmationTimeout = 45f;

    [Header("Debug")]
    [SerializeField] private bool showRoleAssignmentLogs = true;
    [SerializeField] private bool showNightActionLogs = true;

    private readonly NetworkVariable<int> villagePlayerCount = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> villageLayoutSeed = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> currentDay = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<MatchPhase> currentPhase = new NetworkVariable<MatchPhase>(MatchPhase.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<double> phaseEndServerTime = new NetworkVariable<double>(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<double> finalDuelCombatStartServerTime = new NetworkVariable<double>(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<MorningVoteResultData> morningVoteResult = new NetworkVariable<MorningVoteResultData>(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<int>
        morningAbstainVoteCount =
            new NetworkVariable<int>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<NightResultData> nightResult = new NetworkVariable<NightResultData>(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<CurseDollStateData>
        curseDollState =
            new NetworkVariable<CurseDollStateData>(
                default,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<MatchWinner> matchWinner = new NetworkVariable<MatchWinner>(MatchWinner.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<FixedString512Bytes> gameResultMafiaNames =
        new NetworkVariable<FixedString512Bytes>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    private readonly NetworkVariable<FixedString512Bytes>
        gameResultThiefWinnerNames =
            new NetworkVariable<FixedString512Bytes>(
                default,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<ulong>
        gameResultIndividualWinnerClientId =
            new NetworkVariable<ulong>(
                NoClientId,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<bool> villageFullyLit = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<int>
        houseLampUseAllowance =
            new NetworkVariable<int>(
                1,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<int>
        consecutivePerfectStabilityNights =
            new NetworkVariable<int>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<int>
        villageStabilityPercent =
            new NetworkVariable<int>(
                InitialVillageStabilityPercent,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<int>
        lastNightCompletedDutyCount =
            new NetworkVariable<int>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<int>
        lastNightTotalDutyCount =
            new NetworkVariable<int>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<int>
        roleAssignmentReadyCount =
            new NetworkVariable<int>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly NetworkVariable<int>
        roleAssignmentExpectedCount =
            new NetworkVariable<int>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

    private readonly Dictionary<ulong, ulong> morningVotes = new Dictionary<ulong, ulong>();
    private readonly Dictionary<ulong, ulong> morningVoteDrafts = new Dictionary<ulong, ulong>();
    private readonly Dictionary<ulong, ulong> mafiaKillerVotes = new Dictionary<ulong, ulong>();

    private readonly HashSet<ulong>
        expectedRoleAssignmentClientIds =
            new HashSet<ulong>();

    private readonly HashSet<ulong>
        confirmedRoleAssignmentClientIds =
            new HashSet<ulong>();

    private readonly Dictionary<ulong, RoleWeaponId>
        mafiaDisguiseWeaponByClient =
            new Dictionary<ulong, RoleWeaponId>();

    private readonly Dictionary<
        ulong,
        HashSet<RoleWeaponId>>
        ownedMafiaDisguiseWeaponsByClient =
            new Dictionary<
                ulong,
                HashSet<RoleWeaponId>>();

    private readonly Dictionary<ulong, ulong> doctorProtectTargets = new Dictionary<ulong, ulong>();
    private readonly Dictionary<ulong, ulong> drunkardSleepTargets = new Dictionary<ulong, ulong>();

    private readonly Dictionary<ulong, NetworkObject> spawnedBodies = new Dictionary<ulong, NetworkObject>();
    private readonly Dictionary<ulong, NetworkObject> spawnedSpirits = new Dictionary<ulong, NetworkObject>();
    private readonly HashSet<NetworkObject> spawnedDroppedRoleTools = new HashSet<NetworkObject>();
    private readonly Dictionary<ulong, PlayerMatchState> playerMatchStates = new Dictionary<ulong, PlayerMatchState>();

    private readonly Dictionary<int, HashSet<ulong>> nightVisitorsByHouse = new Dictionary<int, HashSet<ulong>>();
    private readonly Dictionary<int, HashSet<ulong>> nightBloodPlayersByHouse = new Dictionary<int, HashSet<ulong>>();

    private readonly Dictionary<ulong, int> forensicsIntruderCountByVictim = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, List<string>> forensicsDamagedPlayerNamesByVictim = new Dictionary<ulong, List<string>>();
    private readonly Dictionary<ulong, NightDeathEvidence> forensicsDeathEvidenceByVictim = new Dictionary<ulong, NightDeathEvidence>();

    private readonly Dictionary<ulong, HunterRouteRecord> hunterRouteRecordsByHunter =
        new Dictionary<ulong, HunterRouteRecord>();

    private readonly Dictionary<ulong, List<int>>
        nightHouseVisitsByClient =
            new Dictionary<ulong, List<int>>();

    private readonly Dictionary<ulong, int> activeDrunkardSleepHouseByClient =
        new Dictionary<ulong, int>();

    private readonly Dictionary<ulong, List<NightDutyAssignment>>
        nightDutyAssignmentsByClient =
            new Dictionary<ulong, List<NightDutyAssignment>>();

    private readonly Dictionary<ulong, List<NightDutyAssignment>>
        mafiaInterferenceAssignmentsByClient =
            new Dictionary<ulong, List<NightDutyAssignment>>();

    private int mafiaInterferenceCompletedCount;
    private int mafiaInterferenceMaximumCount;

    private readonly Dictionary<ulong, int>
        previousNightMissedDutyCountByClient =
            new Dictionary<ulong, int>();

    private readonly Dictionary<ulong, int>
        fakeNightDutyUseCountByClient =
            new Dictionary<ulong, int>();

    private readonly Dictionary<ulong, ulong>
        activeMediumTargetByMedium =
            new Dictionary<ulong, ulong>();

    private readonly Dictionary<ulong, HashSet<ulong>>
        activeMediumsByDeadTarget =
            new Dictionary<ulong, HashSet<ulong>>();

    private readonly Dictionary<ulong, double>
        lastTextChatServerTimeByClient =
            new Dictionary<ulong, double>();

    private NightKillMethod currentNightKillMethod = NightKillMethod.None;
    private bool curseDollInitialPlacementPending;

    private int currentNightKillerWeaponIndex = -1;
    private int serialKillerWeaponIndex = -1;

    private readonly NetworkVariable<bool> lastNightDeathWasPoisoned = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public bool LastNightDeathWasPoisoned => lastNightDeathWasPoisoned.Value;

    private readonly List<MafiaMemberData> localMafiaMembers = new List<MafiaMemberData>();

    /*
     * 밤 준비의 살해 담당 투표 상세는 마녀 진영 클라이언트에게만
     * 개별 RPC로 전달한다. 일반 공개 NetworkList에는 올리지 않는다.
     */
    private readonly Dictionary<ulong, ulong>
        localMafiaKillerVotes =
            new Dictionary<ulong, ulong>();
    private readonly Dictionary<ulong, PlayerMatchState>
        localSpectatorPlayerStates =
            new Dictionary<ulong, PlayerMatchState>();

    private readonly List<RoleWeaponId>
        localMafiaDisguiseOptions =
            new List<RoleWeaponId>();

    private RoleWeaponId localMafiaDisguiseWeapon =
        RoleWeaponId.None;

    private bool localCanSelectMafiaDisguise;
    private int localSpyCitizenAbilityUseCount;
    private int localFakeNightDutyUseCount;
    private bool localNightDutyIsMafiaInterference;
    private int localMafiaInterferenceCompletedCount;
    private int localMafiaInterferenceMaximumCount;
    private float localDutyFailureChance;
    private float localStabilityFailureChance;
    private float localAbilityFailureChance;
    private readonly List<NightDutyAssignment>
        localNightDutyAssignments =
            new List<NightDutyAssignment>(
                NightDutyAssignmentsPerPlayer
            );

    private int generatedPlayerCount = -1;
    private int generatedLayoutSeed;

    private bool matchStarted;
    private bool matchCleanupStarted;
    private bool waitingForRoleAssignmentConfirmations;
    private bool roleAssignmentFailureReturnStarted;
    private bool hasLocalPlayerMatchState;
    private double roleAssignmentConfirmationDeadline;
    private bool hasLocalMediumCommunication;
    private float nextHouseCheckTime;
    private MatchWinner pendingWinner = MatchWinner.None;
    private ulong pendingIndividualWinnerClientId = NoClientId;
    private bool pendingFinalDuel;
    private ulong pendingFinalDuelSerialKillerClientId = NoClientId;
    private ulong pendingFinalDuelMafiaClientId = NoClientId;
    private ulong finalDuelSerialKillerClientId = NoClientId;
    private ulong finalDuelMafiaClientId = NoClientId;
    private bool finalDuelCombatStarted;

    private ulong currentNightKillerClientId = NoClientId;
    private ulong mafiaKillTargetClientId = NoClientId;
    private ulong serialKillerTargetClientId = NoClientId;
    private ulong localNightKillerClientId = NoClientId;
    private ulong localDrunkardSleepTargetClientId = NoClientId;

    private PlayerMatchState localPlayerMatchState;

    public NetworkList<PlayerPublicState> PublicPlayerStates { get; private set; }

    public NetworkList<PlayerMatchState>
        GameResultPlayerStates
    {
        get;
        private set;
    }

    public NetworkList<MorningVoteTallyData>
        MorningVoteTallies
    {
        get;
        private set;
    }

    public NetworkList<int> LitHouseIds { get; private set; }

    public NetworkList<ulong>
        LampUsedClientIds
    {
        get;
        private set;
    }

    public NetworkList<int> SealedFrontDoorHouseIds { get; private set; }

    public event Action<MatchPhase, MatchPhase> PhaseChanged;
    public event Action<int, int> DayChanged;
    public event Action<PlayerMatchState> LocalPlayerMatchStateReceived;
    public event Action PublicPlayerStatesChanged;
    public event Action GameResultPlayerStatesChanged;
    public event Action<MorningVoteResultData, MorningVoteResultData> MorningVoteResultChanged;
    public event Action MorningVoteTalliesChanged;
    public event Action<NightResultData, NightResultData> NightResultChanged;
    public event Action<bool, bool> LastNightDeathWasPoisonedChanged;
    public event Action LocalMafiaMembersChanged;
    public event Action LocalSpectatorPlayerStatesChanged;
    public event Action<ulong> LocalNightKillerChanged;
    public event Action<string> GlobalNotificationReceived;
    public event Action<MatchWinner, MatchWinner> WinnerChanged;
    public event Action<string> GameResultMafiaNamesChanged;
    public event Action<string> GameResultThiefWinnerNamesChanged;
    public event Action<bool, bool> VillageFullyLitChanged;
    public event Action LampUsedPlayersChanged;
    public event Action LocalNightDutyChanged;
    public event Action<bool> LocalNightDutyCompletionResultReceived;
    public event Action VillageDutySummaryChanged;
    public event Action<int, int> VillageStabilityChanged;
    public event Action RoleAssignmentProgressChanged;

    public int CurrentDay => currentDay.Value;
    public MatchPhase CurrentPhase => currentPhase.Value;
    public double PhaseEndServerTime => phaseEndServerTime.Value;
    public double FinalDuelPreparationRemainingTime =>
        currentPhase.Value == MatchPhase.FinalDuel &&
        NetworkManager != null
            ? Math.Max(
                0d,
                finalDuelCombatStartServerTime.Value -
                NetworkManager.ServerTime.Time
            )
            : 0d;
    public bool IsFinalDuelPreparing =>
        FinalDuelPreparationRemainingTime > 0d;
    public bool HasLocalPlayerMatchState => hasLocalPlayerMatchState;
    public int RoleAssignmentReadyCount =>
        roleAssignmentReadyCount.Value;
    public int RoleAssignmentExpectedCount =>
        roleAssignmentExpectedCount.Value;
    public bool HasLocalMediumCommunication =>
        hasLocalMediumCommunication;
    public PlayerMatchState LocalPlayerMatchState => localPlayerMatchState;
    public MorningVoteResultData MorningVoteResult => morningVoteResult.Value;

    public int MorningAbstainVoteCount =>
        morningAbstainVoteCount.Value;

    public NightResultData NightResult => nightResult.Value;
    public CurseDollStateData CurseDollState =>
        curseDollState.Value;
    public MatchWinner Winner => matchWinner.Value;
    public string GameResultMafiaNames =>
        gameResultMafiaNames.Value.ToString();
    public string GameResultThiefWinnerNames =>
        gameResultThiefWinnerNames.Value.ToString();
    public ulong GameResultIndividualWinnerClientId =>
        gameResultIndividualWinnerClientId.Value;
    public IReadOnlyList<MafiaMemberData> LocalMafiaMembers => localMafiaMembers;

    public IReadOnlyList<RoleWeaponId> LocalMafiaDisguiseOptions =>
        localMafiaDisguiseOptions;

    public RoleWeaponId LocalMafiaDisguiseWeapon =>
        localMafiaDisguiseWeapon;

    public bool CanLocalSelectMafiaDisguise =>
        localCanSelectMafiaDisguise;

    public int LocalSpyCitizenAbilityUseCount =>
        localSpyCitizenAbilityUseCount;

    public int LocalSpyCitizenAbilityRemainingCount =>
        Mathf.Max(
            0,
            MaximumSpyCitizenAbilityUses -
            localSpyCitizenAbilityUseCount
        );

    public int LocalSpyCitizenAbilityMaximumCount =>
        MaximumSpyCitizenAbilityUses;

    public int LocalFakeNightDutyRemainingCount =>
        Mathf.Max(
            0,
            MaximumFakeNightDutyUsesPerNight -
            localFakeNightDutyUseCount
        );

    public int LocalFakeNightDutyMaximumCount =>
        MaximumFakeNightDutyUsesPerNight;

    public bool IsLocalMafiaInterferenceDuty =>
        localNightDutyIsMafiaInterference;

    public int LocalMafiaInterferenceCompletedCount =>
        localMafiaInterferenceCompletedCount;

    public int LocalMafiaInterferenceMaximumCount =>
        localMafiaInterferenceMaximumCount;

    public ulong LocalNightKillerClientId => localNightKillerClientId;
    public ulong LocalDrunkardSleepTargetClientId => localDrunkardSleepTargetClientId;
    public bool HasLocalDrunkardSleepTarget =>
        localDrunkardSleepTargetClientId != NoClientId;

    public bool IsVillageFullyLit => villageFullyLit.Value;
    public bool CanLocalPerformFakeNightDuty =>
        hasLocalPlayerMatchState &&
        localPlayerMatchState.isAlive &&
        localPlayerMatchState.team == RoleTeam.Mafia &&
        localNightDutyIsMafiaInterference &&
        localNightDutyAssignments.Count > 0 &&
        currentPhase.Value == MatchPhase.NightAction &&
        localFakeNightDutyUseCount <
            MaximumFakeNightDutyUsesPerNight;
    public bool HasLocalNightDutyAssignment =>
        localNightDutyAssignments.Count > 0;
    public int LocalNightDutyAssignmentCount =>
        localNightDutyAssignments.Count;
    public int LocalNightDutyCompletedCount
    {
        get
        {
            int completedCount = 0;

            for (int i = 0;
                 i < localNightDutyAssignments.Count;
                 i++)
            {
                if (localNightDutyAssignments[i].completed)
                    completedCount++;
            }

            return completedCount;
        }
    }
    public int LocalNightDutyHouseId =>
        localNightDutyAssignments.Count > 0
            ? localNightDutyAssignments[0].houseId
            : -1;
    public int LocalNightDutyPointId =>
        localNightDutyAssignments.Count > 0
            ? localNightDutyAssignments[0].pointId
            : -1;
    public string LocalNightDutyName =>
        localNightDutyAssignments.Count > 0
            ? localNightDutyAssignments[0].dutyName
            : string.Empty;
    public bool IsLocalNightDutyCompleted =>
        localNightDutyAssignments.Count > 0 &&
        (localNightDutyIsMafiaInterference
            ? LocalNightDutyCompletedCount > 0
            : LocalNightDutyCompletedCount ==
              localNightDutyAssignments.Count);
    public int VillageStabilityPercent =>
        villageStabilityPercent.Value;

    public string VillageStabilityStateName
    {
        get
        {
            int stability = VillageStabilityPercent;

            if (stability >= 75)
                return "안정";

            if (stability >= 50)
                return "보통";

            if (stability >= 25)
                return "불안";

            return "붕괴";
        }
    }

    public bool LocalIgnoresVillageStabilityVisibility
    {
        get
        {
            if (!hasLocalPlayerMatchState)
                return true;

            return localPlayerMatchState.team == RoleTeam.Mafia ||
                   !localPlayerMatchState.isAlive ||
                   currentPhase.Value == MatchPhase.FinalDuel;
        }
    }

    public float LocalSpiritVisibilityDistance
    {
        get
        {
            if (LocalIgnoresVillageStabilityVisibility ||
                VillageStabilityPercent >= 75)
            {
                return float.PositiveInfinity;
            }

            int stability = VillageStabilityPercent;
            float distance;

            if (stability >= 50)
                distance = NormalSpiritVisibilityDistance;
            else if (stability >= 25)
                distance = UnstableSpiritVisibilityDistance;
            else
                distance = CollapsedSpiritVisibilityDistance;

            if (villageFullyLit.Value)
            {
                distance = Mathf.Min(
                    NormalSpiritVisibilityDistance,
                    distance + FullyLitSpiritVisibilityBonus
                );
            }

            float villageScale = 1f;

            if (villageGenerator != null)
            {
                villageScale = Mathf.Clamp(
                    villageGenerator.CurrentHouseRadius /
                    villageGenerator.ReferenceHouseRadius,
                    0.65f,
                    2.5f
                );
            }

            return distance * villageScale;
        }
    }
    public int LocalHouseLampUseAllowance =>
        Mathf.Max(1, houseLampUseAllowance.Value);
    public int LocalHouseLampUsedCount
    {
        get
        {
            if (NetworkManager.Singleton == null)
                return 0;

            return GetPlayerHouseLampUseCount(
                NetworkManager.Singleton.LocalClientId
            );
        }
    }
    public int LocalHouseLampRemainingUses =>
        Mathf.Max(
            0,
            LocalHouseLampUseAllowance -
            LocalHouseLampUsedCount
        );
    public int LastNightCompletedDutyCount =>
        lastNightCompletedDutyCount.Value;
    public int LastNightTotalDutyCount =>
        lastNightTotalDutyCount.Value;
    public float LocalDutyFailureChance =>
        localDutyFailureChance;
    public float LocalStabilityFailureChance =>
        localStabilityFailureChance;
    public float LocalAbilityFailureChance =>
        localAbilityFailureChance;
    public float LocalAbilitySuccessPercent =>
        (1f - localAbilityFailureChance) * 100f;
    public int RequiredLitHouseCount
    {
        get
        {
            int aliveCount = 0;

            if (PublicPlayerStates != null)
            {
                for (int i = 0; i < PublicPlayerStates.Count; i++)
                {
                    if (PublicPlayerStates[i].isAlive)
                        aliveCount++;
                }
            }

            return aliveCount > 0
                ? aliveCount / 2 + 1
                : 0;
        }
    }

    public string GetLocalNightDutyName(int index)
    {
        return index >= 0 &&
               index < localNightDutyAssignments.Count
            ? localNightDutyAssignments[index].dutyName
            : string.Empty;
    }

    public bool IsLocalNightDutyAssignmentCompleted(
        int index)
    {
        return index >= 0 &&
               index < localNightDutyAssignments.Count &&
               localNightDutyAssignments[index].completed;
    }

    public bool IsLocalNightDutyAssignmentUnavailable(
        int index)
    {
        return localNightDutyIsMafiaInterference &&
               IsLocalNightDutyCompleted &&
               index >= 0 &&
               index < localNightDutyAssignments.Count &&
               !localNightDutyAssignments[index].completed;
    }

    public bool IsLocalNightKiller
    {
        get
        {
            return NetworkManager != null &&
                   localNightKillerClientId != NoClientId &&
                   localNightKillerClientId == NetworkManager.LocalClientId;
        }
    }

    public double PhaseRemainingTime
    {
        get
        {
            if (!IsSpawned || NetworkManager == null || currentPhase.Value == MatchPhase.None)
                return 0d;

            return Math.Max(0d, phaseEndServerTime.Value - NetworkManager.ServerTime.Time);
        }
    }

    private void Awake()
    {
        PublicPlayerStates = new NetworkList<PlayerPublicState>(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        GameResultPlayerStates =
            new NetworkList<PlayerMatchState>(
                null,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

        MorningVoteTallies =
            new NetworkList<MorningVoteTallyData>(
                null,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

        LitHouseIds = new NetworkList<int>(
            null,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

        LampUsedClientIds =
            new NetworkList<ulong>(
                null,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

        SealedFrontDoorHouseIds = new NetworkList<int>(
            null,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public override void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        matchCleanupStarted = false;
        waitingForRoleAssignmentConfirmations = false;
        roleAssignmentFailureReturnStarted = false;
        roleAssignmentConfirmationDeadline = 0d;
        expectedRoleAssignmentClientIds.Clear();
        confirmedRoleAssignmentClientIds.Clear();
        spawnedBodies.Clear();
        spawnedSpirits.Clear();
        spawnedDroppedRoleTools.Clear();

        completedNightRoleActions.Clear();
        consumedExorcismPlayers.Clear();
        spyCitizenAbilityUseCountByClient.Clear();
        peddlerRealActionByClient.Clear();
        localSpyCitizenAbilityUseCount = 0;

        nightVisitorsByHouse.Clear();
        nightBloodPlayersByHouse.Clear();
        forensicsIntruderCountByVictim.Clear();
        forensicsDamagedPlayerNamesByVictim.Clear();
        forensicsDeathEvidenceByVictim.Clear();

        hunterRouteRecordsByHunter.Clear();
        nightHouseVisitsByClient.Clear();
        drunkardSleepTargets.Clear();
        activeDrunkardSleepHouseByClient.Clear();
        nightDutyAssignmentsByClient.Clear();
        mafiaInterferenceAssignmentsByClient.Clear();
        mafiaInterferenceCompletedCount = 0;
        mafiaInterferenceMaximumCount = 0;
        previousNightMissedDutyCountByClient.Clear();
        fakeNightDutyUseCountByClient.Clear();
        activeMediumTargetByMedium.Clear();
        activeMediumsByDeadTarget.Clear();
        lastTextChatServerTimeByClient.Clear();
        localMafiaKillerVotes.Clear();
        hasLocalMediumCommunication = false;

        localDrunkardSleepTargetClientId = NoClientId;
        localFakeNightDutyUseCount = 0;
        localNightDutyIsMafiaInterference = false;
        localMafiaInterferenceCompletedCount = 0;
        localMafiaInterferenceMaximumCount = 0;
        ClearLocalNightDutyAssignment();
        pendingFinalDuel = false;
        pendingFinalDuelSerialKillerClientId = NoClientId;
        pendingFinalDuelMafiaClientId = NoClientId;
        finalDuelSerialKillerClientId = NoClientId;
        finalDuelMafiaClientId = NoClientId;
        finalDuelCombatStarted = false;

        if (IsServer)
            finalDuelCombatStartServerTime.Value = 0d;

        if (IsServer)
            ResetCurseDollState();

        villagePlayerCount.OnValueChanged += OnVillagePlayerCountChanged;
        villageLayoutSeed.OnValueChanged += OnVillageLayoutSeedChanged;
        currentDay.OnValueChanged += OnCurrentDayChanged;
        currentPhase.OnValueChanged += OnCurrentPhaseChanged;
        morningVoteResult.OnValueChanged += OnMorningVoteResultChanged;
        morningAbstainVoteCount.OnValueChanged +=
            OnMorningAbstainVoteCountChanged;
        nightResult.OnValueChanged += OnNightResultChanged;
        matchWinner.OnValueChanged += OnMatchWinnerChanged;
        gameResultMafiaNames.OnValueChanged +=
            OnGameResultMafiaNamesChanged;
        gameResultThiefWinnerNames.OnValueChanged +=
            OnGameResultThiefWinnerNamesChanged;
        lastNightDeathWasPoisoned.OnValueChanged +=
            OnLastNightDeathWasPoisonedChanged;
        villageFullyLit.OnValueChanged +=
            OnVillageFullyLitChanged;
        houseLampUseAllowance.OnValueChanged +=
            OnHouseLampUseAllowanceChanged;
        villageStabilityPercent.OnValueChanged +=
            OnVillageStabilityPercentChanged;
        lastNightCompletedDutyCount.OnValueChanged +=
            OnLastNightDutySummaryChanged;
        lastNightTotalDutyCount.OnValueChanged +=
            OnLastNightDutySummaryChanged;
        roleAssignmentReadyCount.OnValueChanged +=
            OnRoleAssignmentProgressValueChanged;
        roleAssignmentExpectedCount.OnValueChanged +=
            OnRoleAssignmentProgressValueChanged;

        PublicPlayerStates.OnListChanged += OnPublicPlayerStatesChanged;
        GameResultPlayerStates.OnListChanged +=
            OnGameResultPlayerStatesChanged;
        MorningVoteTallies.OnListChanged +=
            OnMorningVoteTalliesChanged;
        LitHouseIds.OnListChanged += OnLitHouseIdsChanged;
        LampUsedClientIds.OnListChanged +=
            OnLampUsedClientIdsChanged;

        TryGenerateVillageLocal();
        ApplyAllLampStatesLocal();

        if (IsServer)
        {
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            StartCoroutine(StartMatchWhenReady());
        }

        if (IsClient)
            StartCoroutine(RequestLocalMatchStateRoutine());
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
            DespawnAllMatchNetworkObjects();

        villagePlayerCount.OnValueChanged -= OnVillagePlayerCountChanged;
        villageLayoutSeed.OnValueChanged -= OnVillageLayoutSeedChanged;
        currentDay.OnValueChanged -= OnCurrentDayChanged;
        currentPhase.OnValueChanged -= OnCurrentPhaseChanged;
        morningVoteResult.OnValueChanged -= OnMorningVoteResultChanged;
        morningAbstainVoteCount.OnValueChanged -=
            OnMorningAbstainVoteCountChanged;
        nightResult.OnValueChanged -= OnNightResultChanged;
        matchWinner.OnValueChanged -= OnMatchWinnerChanged;
        gameResultMafiaNames.OnValueChanged -=
            OnGameResultMafiaNamesChanged;
        gameResultThiefWinnerNames.OnValueChanged -=
            OnGameResultThiefWinnerNamesChanged;
        lastNightDeathWasPoisoned.OnValueChanged -=
            OnLastNightDeathWasPoisonedChanged;
        villageFullyLit.OnValueChanged -=
            OnVillageFullyLitChanged;
        houseLampUseAllowance.OnValueChanged -=
            OnHouseLampUseAllowanceChanged;
        villageStabilityPercent.OnValueChanged -=
            OnVillageStabilityPercentChanged;
        lastNightCompletedDutyCount.OnValueChanged -=
            OnLastNightDutySummaryChanged;
        lastNightTotalDutyCount.OnValueChanged -=
            OnLastNightDutySummaryChanged;
        roleAssignmentReadyCount.OnValueChanged -=
            OnRoleAssignmentProgressValueChanged;
        roleAssignmentExpectedCount.OnValueChanged -=
            OnRoleAssignmentProgressValueChanged;

        PublicPlayerStates.OnListChanged -= OnPublicPlayerStatesChanged;
        GameResultPlayerStates.OnListChanged -=
            OnGameResultPlayerStatesChanged;
        MorningVoteTallies.OnListChanged -=
            OnMorningVoteTalliesChanged;
        LitHouseIds.OnListChanged -= OnLitHouseIdsChanged;
        LampUsedClientIds.OnListChanged -=
            OnLampUsedClientIdsChanged;

        if (NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;

        StopAllCoroutines();

        houseOwnerDisplayStates.Clear();

        if (IsServer && LitHouseIds != null)
            LitHouseIds.Clear();

        if (IsServer &&
            LampUsedClientIds != null)
        {
            LampUsedClientIds.Clear();
        }

        if (IsServer && SealedFrontDoorHouseIds != null)
            SealedFrontDoorHouseIds.Clear();

        if (IsServer && GameResultPlayerStates != null)
            GameResultPlayerStates.Clear();

        morningVotes.Clear();
        morningVoteDrafts.Clear();

        if (IsServer &&
            MorningVoteTallies != null)
        {
            MorningVoteTallies.Clear();
            morningAbstainVoteCount.Value = 0;
        }

        mafiaKillerVotes.Clear();
        localMafiaKillerVotes.Clear();
        mafiaDisguiseWeaponByClient.Clear();
        ownedMafiaDisguiseWeaponsByClient.Clear();
        doctorProtectTargets.Clear();
        drunkardSleepTargets.Clear();
        completedNightRoleActions.Clear();
        consumedExorcismPlayers.Clear();
        spyCitizenAbilityUseCountByClient.Clear();
        peddlerRealActionByClient.Clear();
        localSpyCitizenAbilityUseCount = 0;

        nightVisitorsByHouse.Clear();
        nightBloodPlayersByHouse.Clear();
        forensicsIntruderCountByVictim.Clear();
        forensicsDamagedPlayerNamesByVictim.Clear();
        forensicsDeathEvidenceByVictim.Clear();

        hunterRouteRecordsByHunter.Clear();
        nightHouseVisitsByClient.Clear();
        activeDrunkardSleepHouseByClient.Clear();
        nightDutyAssignmentsByClient.Clear();
        mafiaInterferenceAssignmentsByClient.Clear();
        mafiaInterferenceCompletedCount = 0;
        mafiaInterferenceMaximumCount = 0;
        previousNightMissedDutyCountByClient.Clear();
        fakeNightDutyUseCountByClient.Clear();
        activeMediumTargetByMedium.Clear();
        activeMediumsByDeadTarget.Clear();
        lastTextChatServerTimeByClient.Clear();
        hasLocalMediumCommunication = false;

        playerMatchStates.Clear();
        localMafiaMembers.Clear();
        localSpectatorPlayerStates.Clear();

        currentNightKillerClientId = NoClientId;
        mafiaKillTargetClientId = NoClientId;
        serialKillerTargetClientId = NoClientId;
        localNightKillerClientId = NoClientId;
        localDrunkardSleepTargetClientId = NoClientId;
        localFakeNightDutyUseCount = 0;
        localNightDutyIsMafiaInterference = false;
        localMafiaInterferenceCompletedCount = 0;
        localMafiaInterferenceMaximumCount = 0;
        localDutyFailureChance = 0f;
        localStabilityFailureChance = 0f;
        localAbilityFailureChance = 0f;
        ClearLocalNightDutyAssignment();

        currentNightKillMethod = NightKillMethod.None;
        currentNightKillerWeaponIndex = -1;
        serialKillerWeaponIndex = -1;
        curseDollInitialPlacementPending = false;

        spawnedBodies.Clear();
        spawnedSpirits.Clear();
        spawnedDroppedRoleTools.Clear();

        matchStarted = false;
        matchCleanupStarted = false;
        waitingForRoleAssignmentConfirmations = false;
        roleAssignmentFailureReturnStarted = false;
        roleAssignmentConfirmationDeadline = 0d;
        expectedRoleAssignmentClientIds.Clear();
        confirmedRoleAssignmentClientIds.Clear();
        hasLocalPlayerMatchState = false;
        localPlayerMatchState = default;
        pendingWinner = MatchWinner.None;
        pendingIndividualWinnerClientId = NoClientId;
        pendingFinalDuel = false;
        pendingFinalDuelSerialKillerClientId = NoClientId;
        pendingFinalDuelMafiaClientId = NoClientId;
        finalDuelSerialKillerClientId = NoClientId;
        finalDuelMafiaClientId = NoClientId;
        finalDuelCombatStarted = false;
    }

    private void Update()
    {
        if (!IsServer || !matchStarted)
            return;

        if (waitingForRoleAssignmentConfirmations &&
            !roleAssignmentFailureReturnStarted &&
            currentPhase.Value == MatchPhase.None &&
            roleAssignmentConfirmationDeadline > 0d &&
            NetworkManager.ServerTime.Time >=
                roleAssignmentConfirmationDeadline)
        {
            BeginRoleAssignmentFailureReturn(
                "일부 플레이어의 정보 수신이 지연되어 " +
                "게임 시작을 취소했습니다."
            );

            return;
        }

        if (Time.time >= nextHouseCheckTime)
        {
            nextHouseCheckTime =
                Time.time + houseCheckInterval;

            UpdateSpiritHouseLocations();
            ValidateActiveMediumCommunications();
            ValidateCurseDollHolderState();
        }

        if (currentPhase.Value == MatchPhase.FinalDuel &&
            !finalDuelCombatStarted &&
            NetworkManager.ServerTime.Time >=
                finalDuelCombatStartServerTime.Value)
        {
            StartFinalDuelCombat();
        }

        if (currentPhase.Value != MatchPhase.None &&
            currentPhase.Value != MatchPhase.GameResult &&
            currentPhase.Value != MatchPhase.FinalDuel &&
            NetworkManager.ServerTime.Time >= phaseEndServerTime.Value)
        {
            AdvancePhase();
        }
    }

    private IEnumerator RequestLocalMatchStateRoutine()
    {
        float warningTime = Time.realtimeSinceStartup + 10f;
        bool warningLogged = false;

        while (IsSpawned &&
               NetworkManager != null &&
               NetworkManager.IsConnectedClient &&
               currentPhase.Value == MatchPhase.None)
        {
            if (!hasLocalPlayerMatchState)
            {
                RequestLocalMatchStateRpc();
            }
            else
            {
                ConfirmRoleAssignmentReceivedRpc(
                    villageLayoutSeed.Value
                );
            }

            if (!hasLocalPlayerMatchState &&
                !warningLogged &&
                Time.realtimeSinceStartup >= warningTime)
            {
                warningLogged = true;
                Debug.LogWarning(
                    "내 직업 정보 수신이 지연되고 있습니다. " +
                    "연결이 유지되는 동안 재요청합니다."
                );
            }

            yield return new WaitForSecondsRealtime(
                !hasLocalPlayerMatchState && !warningLogged
                    ? 0.25f
                    : 1f
            );
        }
    }

    private IEnumerator StartMatchWhenReady()
    {
        float timeout = Time.realtimeSinceStartup + 10f;

        while (Time.realtimeSinceStartup < timeout)
        {
            if (LobbyRoomManager.Instance != null &&
                LobbyRoomManager.Instance.IsSpawned &&
                LobbyRoomManager.Instance.LobbyPlayers.Count > 0)
            {
                StartMatch();
                yield break;
            }

            yield return null;
        }

        Debug.LogError("LobbyRoomManager 준비 시간을 초과했습니다.");
    }

    public void StartMatch()
    {
        if (!IsServer || matchStarted)
            return;

        if (LobbyRoomManager.Instance == null)
        {
            Debug.LogError("LobbyRoomManager가 없습니다.");
            return;
        }

        NetworkList<LobbyPlayer> lobbyPlayers = LobbyRoomManager.Instance.LobbyPlayers;
        List<LobbyPlayer> players = new List<LobbyPlayer>(lobbyPlayers.Count);

        for (int i = 0; i < lobbyPlayers.Count; i++)
            players.Add(lobbyPlayers[i]);

        if (players.Count == 0)
        {
            Debug.LogError("로비 플레이어가 없습니다.");
            return;
        }

        ApplyLobbyPhaseDurations();

        matchStarted = true;
        waitingForRoleAssignmentConfirmations = false;
        roleAssignmentFailureReturnStarted = false;
        roleAssignmentConfirmationDeadline = 0d;
        expectedRoleAssignmentClientIds.Clear();
        confirmedRoleAssignmentClientIds.Clear();
        roleAssignmentReadyCount.Value = 0;
        roleAssignmentExpectedCount.Value = 0;
        pendingWinner = MatchWinner.None;
        pendingIndividualWinnerClientId = NoClientId;
        pendingFinalDuel = false;
        pendingFinalDuelSerialKillerClientId = NoClientId;
        pendingFinalDuelMafiaClientId = NoClientId;
        finalDuelSerialKillerClientId = NoClientId;
        finalDuelMafiaClientId = NoClientId;
        finalDuelCombatStarted = false;
        finalDuelCombatStartServerTime.Value = 0d;
        matchWinner.Value = MatchWinner.None;
        gameResultMafiaNames.Value = default;
        gameResultThiefWinnerNames.Value = default;
        gameResultIndividualWinnerClientId.Value = NoClientId;
        GameResultPlayerStates.Clear();
        ResetCurseDollState();
        villageStabilityPercent.Value =
            InitialVillageStabilityPercent;
        houseLampUseAllowance.Value = 1;
        consecutivePerfectStabilityNights.Value = 0;
        lastNightCompletedDutyCount.Value = 0;
        lastNightTotalDutyCount.Value = 0;
        previousNightMissedDutyCountByClient.Clear();
        fakeNightDutyUseCountByClient.Clear();
        mafiaInterferenceAssignmentsByClient.Clear();
        mafiaInterferenceCompletedCount = 0;
        mafiaInterferenceMaximumCount = 0;

        /*
         * 등불 사용 횟수는 밤마다가 아니라
         * 한 게임 전체에서 플레이어당 1회다.
         */
        if (LampUsedClientIds != null)
        {
            LampUsedClientIds.Clear();
        }

        villageLayoutSeed.Value = UnityEngine.Random.Range(1, int.MaxValue);
        villagePlayerCount.Value = players.Count;

        TryGenerateVillageLocal();
        StartCoroutine(AssignPlayers(players));
    }

    private void ApplyLobbyPhaseDurations()
    {
        if (!IsServer ||
            LobbyRoomManager.Instance == null)
        {
            return;
        }

        LobbySettings settings =
            LobbyRoomManager.Instance
                .CurrentSettings.Value;

        morningVoteDuration =
            Mathf.Max(
                30f,
                settings.morningVoteSeconds
            );

        morningVoteResultDuration =
            Mathf.Max(
                3f,
                settings.morningVoteResultSeconds
            );

        nightPreparationDuration =
            Mathf.Max(
                10f,
                settings.nightPreparationSeconds
            );

        nightActionDuration =
            Mathf.Max(
                30f,
                settings.nightActionSeconds
            );

        nightResultDuration =
            Mathf.Max(
                3f,
                settings.nightResultSeconds
            );
    }

    private void OnMatchWinnerChanged(
        MatchWinner previous,
        MatchWinner current)
    {
        WinnerChanged?.Invoke(
            previous,
            current
        );
    }

    private void OnGameResultMafiaNamesChanged(
        FixedString512Bytes previous,
        FixedString512Bytes current)
    {
        GameResultMafiaNamesChanged?.Invoke(
            current.ToString()
        );
    }

    private void OnGameResultThiefWinnerNamesChanged(
        FixedString512Bytes previous,
        FixedString512Bytes current)
    {
        GameResultThiefWinnerNamesChanged?.Invoke(
            current.ToString()
        );
    }

    private void OnGameResultPlayerStatesChanged(
        NetworkListEvent<PlayerMatchState> changeEvent)
    {
        GameResultPlayerStatesChanged?.Invoke();
    }

    private void OnVillagePlayerCountChanged(int previous, int current)
    {
        TryGenerateVillageLocal();
    }

    private void OnVillageLayoutSeedChanged(int previous, int current)
    {
        TryGenerateVillageLocal();
    }

    private void TryGenerateVillageLocal()
    {
        int playerCount = villagePlayerCount.Value;
        int layoutSeed = villageLayoutSeed.Value;

        if (playerCount <= 0 || layoutSeed == 0)
            return;

        GenerateVillageLocal(playerCount, layoutSeed);
    }

    private void GenerateVillageLocal(int playerCount, int layoutSeed)
    {
        if (generatedPlayerCount == playerCount &&
            generatedLayoutSeed == layoutSeed)
        {
            return;
        }

        if (villageGenerator == null)
        {
            Debug.LogError("VillageGenerator가 연결되지 않았습니다.");
            return;
        }

        villageGenerator.GenerateVillage(
            playerCount,
            layoutSeed
        );

        generatedPlayerCount = playerCount;
        generatedLayoutSeed = layoutSeed;

        /*
         * GenerateVillage 과정에서 House.Initialize()가
         * 이름표를 초기화하므로 생성이 끝난 뒤 다시 적용한다.
         */
        ApplyAllCachedHouseOwners();
        ApplyAllHouseOwnerLifeStates();
        RefreshAllHouseOwnerPresentationsLocal();
        ApplyAllLampStatesLocal();
    }

    private IEnumerator AssignPlayers(List<LobbyPlayer> players)
    {
        yield return null;

        if (houseRegistry == null)
        {
            Debug.LogError(
                "VillageHouseRegistry가 연결되지 않았습니다."
            );

            BeginRoleAssignmentFailureReturn(
                "마을 정보를 준비하지 못해 " +
                "게임 시작을 취소했습니다."
            );

            yield break;
        }

        if (houseRegistry.HouseCount < players.Count)
        {
            Debug.LogError(
                $"집 수가 부족합니다. " +
                $"Players: {players.Count}, " +
                $"Houses: {houseRegistry.HouseCount}"
            );

            BeginRoleAssignmentFailureReturn(
                "플레이어 배치를 완료하지 못해 " +
                "게임 시작을 취소했습니다."
            );

            yield break;
        }

        List<LobbyPlayer> assignedPlayers =
            new List<LobbyPlayer>(players.Count);

        for (int i = 0; i < players.Count; i++)
        {
            LobbyPlayer lobbyPlayer = players[i];
            ulong clientId = lobbyPlayer.clientId;

            if (!NetworkManager.ConnectedClients.ContainsKey(
                    clientId))
            {
                continue;
            }

            House house = houseRegistry.GetHouse(i);

            if (house == null)
            {
                Debug.LogError(
                    $"House {i}를 찾지 못했습니다."
                );

                continue;
            }

            if (!ValidateHousePoints(house))
                continue;

            SetHouseOwnerDisplayLocal(
                house.Id,
                clientId,
                lobbyPlayer.playerName
            );

            SyncHouseOwnerRpc(
                house.Id,
                clientId,
                lobbyPlayer.playerName
            );

            NetworkObject body =
                SpawnBody(clientId, house);

            NetworkObject spirit =
                SpawnSpirit(clientId, house);

            if (body == null || spirit == null)
                continue;

            assignedPlayers.Add(lobbyPlayer);

            Debug.Log(
                $"Player Assigned - " +
                $"Client: {clientId}, " +
                $"Name: {lobbyPlayer.playerName}, " +
                $"House: {house.Id}"
            );
        }

        if (assignedPlayers.Count == 0)
        {
            Debug.LogError(
                "배정에 성공한 플레이어가 없습니다."
            );

            BeginRoleAssignmentFailureReturn(
                "플레이어 배치를 완료하지 못해 " +
                "게임 시작을 취소했습니다."
            );

            yield break;
        }

        if (assignedPlayers.Count != players.Count)
        {
            Debug.LogError(
                "일부 플레이어 배정에 실패했습니다. " +
                $"Assigned: {assignedPlayers.Count}, " +
                $"Expected: {players.Count}"
            );

            BeginRoleAssignmentFailureReturn(
                "일부 플레이어의 배치를 완료하지 못해 " +
                "게임 시작을 취소했습니다."
            );

            yield break;
        }

        expectedRoleAssignmentClientIds.Clear();
        confirmedRoleAssignmentClientIds.Clear();

        for (int i = 0; i < assignedPlayers.Count; i++)
        {
            expectedRoleAssignmentClientIds.Add(
                assignedPlayers[i].clientId
            );
        }

        waitingForRoleAssignmentConfirmations = true;
        roleAssignmentConfirmationDeadline =
            NetworkManager.ServerTime.Time +
            Mathf.Max(
                10f,
                roleAssignmentConfirmationTimeout
            );

        roleAssignmentReadyCount.Value = 0;
        roleAssignmentExpectedCount.Value =
            expectedRoleAssignmentClientIds.Count;

        if (!AssignRoles(assignedPlayers))
        {
            BeginRoleAssignmentFailureReturn(
                "역할 및 도구 배정 검증에 실패해 " +
                "게임 시작을 취소했습니다."
            );

            yield break;
        }

        TryStartPhaseAfterRoleAssignmentConfirmations();
    }

    [Rpc(SendTo.Everyone)]
    private void SyncHouseOwnerRpc(
    int houseId,
    ulong clientId,
    FixedString64Bytes playerName)
    {
        /*
         * 서버와 호스트는 AssignPlayers에서 이미 로컬 적용했다.
         */
        if (IsServer)
            return;

        SetHouseOwnerDisplayLocal(
            houseId,
            clientId,
            playerName
        );
    }

    private void SetHouseOwnerDisplayLocal(
        int houseId,
        ulong clientId,
        FixedString64Bytes playerName)
    {
        houseOwnerDisplayStates[houseId] =
            new HouseOwnerDisplayState
            {
                clientId = clientId,
                playerName = playerName
            };

        House house = GetHouse(houseId);

        /*
         * 아직 마을이 생성되지 않았다면 캐시만 보관한다.
         * GenerateVillageLocal()이 끝난 뒤 다시 적용된다.
         */
        if (house == null)
            return;

        house.AssignOwner(
            clientId,
            playerName.ToString()
        );

        ApplyHouseOwnerPresentationLocal(
            house,
            houseOwnerDisplayStates[houseId]
        );
    }

    private void ApplyAllCachedHouseOwners()
    {
        foreach (KeyValuePair<
                     int,
                     HouseOwnerDisplayState> pair
                 in houseOwnerDisplayStates)
        {
            House house = GetHouse(pair.Key);

            if (house == null)
                continue;

            HouseOwnerDisplayState state =
                pair.Value;

            house.AssignOwner(
                state.clientId,
                state.playerName.ToString()
            );
        }
    }

    private void RefreshAllHouseOwnerPresentationsLocal()
    {
        if (houseRegistry == null)
            return;

        foreach (KeyValuePair<
                     int,
                     HouseOwnerDisplayState> pair
                 in houseOwnerDisplayStates)
        {
            House house = GetHouse(pair.Key);

            if (house == null)
                continue;

            ApplyHouseOwnerPresentationLocal(
                house,
                pair.Value
            );
        }
    }

    private void ApplyHouseOwnerPresentationLocal(
        House house,
        HouseOwnerDisplayState ownerState)
    {
        if (house == null ||
            house.OwnerIdentityLabel == null)
        {
            return;
        }

        bool isNightPhase =
            currentPhase.Value ==
                MatchPhase.NightPreparation ||
            currentPhase.Value ==
                MatchPhase.NightAction ||
            currentPhase.Value ==
                MatchPhase.NightResult;

        bool hasLocalState =
            hasLocalPlayerMatchState;

        bool isLocalHouse =
            hasLocalState &&
            ownerState.clientId ==
                localPlayerMatchState.clientId;

        bool ignoresStabilityRestriction =
            !hasLocalState ||
            localPlayerMatchState.team ==
                RoleTeam.Mafia ||
            !localPlayerMatchState.isAlive ||
            currentPhase.Value ==
                MatchPhase.FinalDuel;

        bool visible = true;
        float opacity = 1f;

        if (isNightPhase &&
            !ignoresStabilityRestriction)
        {
            int stability =
                villageStabilityPercent.Value;

            if (stability <= 0)
            {
                visible = false;
            }
            else if (stability <
                     LocalOnlyHouseOwnerLabelStability)
            {
                visible = isLocalHouse;
            }
            else if (stability <
                     DimmedHouseOwnerLabelStability &&
                     !isLocalHouse)
            {
                opacity =
                    DimmedHouseOwnerLabelOpacity;
            }
        }

        string playerName =
            ownerState.playerName.ToString();

        if (isNightPhase &&
            isLocalHouse &&
            visible)
        {
            string teamColor =
                GetTeamColorHexForHouseLabel(
                    localPlayerMatchState.team
                );

            playerName =
                $"<b><color=#{teamColor}>" +
                $"▶ {playerName}</color></b>";
        }

        PlayerIdentityLabel identityLabel =
            house.OwnerIdentityLabel;

        identityLabel.SetPlayer(
            ownerState.clientId,
            playerName
        );

        CanvasGroup canvasGroup =
            identityLabel.GetComponent<CanvasGroup>();

        if (canvasGroup == null)
        {
            canvasGroup =
                identityLabel.gameObject
                    .AddComponent<CanvasGroup>();

            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }

        canvasGroup.alpha = opacity;
        identityLabel.gameObject.SetActive(visible);
    }

    private static string GetTeamColorHexForHouseLabel(
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

    private void ApplyAllHouseOwnerLifeStates()
    {
        if (houseRegistry == null ||
            PublicPlayerStates == null)
        {
            return;
        }

        foreach (House house in
                 houseRegistry.Houses)
        {
            if (house == null ||
                house.OwnerClientId ==
                    House.NoOwnerClientId)
            {
                continue;
            }

            bool isAlive =
                TryGetPublicPlayerAliveState(
                    house.OwnerClientId,
                    out bool alive
                )
                    ? alive
                    : true;

            house.SetOwnerAlive(isAlive);
        }
    }

    private bool TryGetPublicPlayerAliveState(
        ulong clientId,
        out bool isAlive)
    {
        isAlive = true;

        if (PublicPlayerStates == null)
            return false;

        for (int i = 0;
             i < PublicPlayerStates.Count;
             i++)
        {
            PlayerPublicState state =
                PublicPlayerStates[i];

            if (state.clientId !=
                clientId)
            {
                continue;
            }

            isAlive = state.isAlive;
            return true;
        }

        return false;
    }

    private void SendHouseOwnerDisplaySnapshot(
        ulong targetClientId)
    {
        if (!IsServer)
            return;

        foreach (KeyValuePair<
                     int,
                     HouseOwnerDisplayState> pair
                 in houseOwnerDisplayStates)
        {
            HouseOwnerDisplayState state = pair.Value;

            ReceiveHouseOwnerDisplaySnapshotRpc(
                pair.Key,
                state.clientId,
                state.playerName,
                RpcTarget.Single(
                    targetClientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveHouseOwnerDisplaySnapshotRpc(
        int houseId,
        ulong clientId,
        FixedString64Bytes playerName,
        RpcParams rpcParams = default)
    {
        SetHouseOwnerDisplayLocal(
            houseId,
            clientId,
            playerName
        );
    }

    private void ClearHouseOwnerDisplayLocal(
        int houseId)
    {
        houseOwnerDisplayStates.Remove(houseId);

        House house = GetHouse(houseId);

        if (house != null)
            house.ClearOwner();
    }

    [Rpc(SendTo.Everyone)]
    private void ClearHouseOwnerRpc(int houseId)
    {
        if (IsServer)
            return;

        ClearHouseOwnerDisplayLocal(houseId);
    }


    private void OnLitHouseIdsChanged(
        NetworkListEvent<int> changeEvent)
    {
        ApplyAllLampStatesLocal();
    }

    private void OnLampUsedClientIdsChanged(
        NetworkListEvent<ulong> changeEvent)
    {
        LampUsedPlayersChanged?.Invoke();
    }

    private void OnVillageFullyLitChanged(
        bool previous,
        bool current)
    {
        VillageFullyLitChanged?.Invoke(previous, current);
    }

    private void OnHouseLampUseAllowanceChanged(
        int previous,
        int current)
    {
        LampUsedPlayersChanged?.Invoke();
    }

    private void OnVillageStabilityPercentChanged(
        int previous,
        int current)
    {
        RefreshAllHouseOwnerPresentationsLocal();

        VillageStabilityChanged?.Invoke(
            previous,
            current
        );

        LocalNightDutyChanged?.Invoke();
    }

    private void OnLastNightDutySummaryChanged(
        int previous,
        int current)
    {
        VillageDutySummaryChanged?.Invoke();
    }

    public bool IsFrontDoorSealed(int houseId)
    {
        if (SealedFrontDoorHouseIds == null || houseId < 0)
            return false;

        for (int i = 0; i < SealedFrontDoorHouseIds.Count; i++)
        {
            if (SealedFrontDoorHouseIds[i] == houseId)
                return true;
        }

        return false;
    }

    public void ShowLocalNotification(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        GlobalNotificationReceived?.Invoke(message);
    }

    public void SendPrivateNotification(ulong targetClientId, string message)
    {
        if (!IsServer ||
            string.IsNullOrWhiteSpace(message) ||
            NetworkManager == null ||
            !NetworkManager.ConnectedClients.ContainsKey(targetClientId))
        {
            return;
        }

        ReceivePrivateNotificationRpc(
            new FixedString512Bytes(message),
            RpcTarget.Single(targetClientId, RpcTargetUse.Temp)
        );
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void ReceivePrivateNotificationRpc(
        FixedString512Bytes message,
        RpcParams rpcParams = default)
    {
        ShowLocalNotification(message.ToString());
    }

    public bool HasPlayerUsedHouseLamp(
        ulong clientId)
    {
        return GetPlayerHouseLampUseCount(clientId) >=
               Mathf.Max(1, houseLampUseAllowance.Value);
    }

    public bool HasPlayerEverUsedHouseLamp(
        ulong clientId)
    {
        return GetPlayerHouseLampUseCount(clientId) > 0;
    }

    public bool HasPlayerUsedHouseLampThisNight(
        ulong clientId)
    {
        int houseId = GetOwnedHouseId(clientId);

        return houseId >= 0 &&
               IsHouseLampLit(houseId);
    }

    public int GetPlayerHouseLampUseCount(
        ulong clientId)
    {
        if (LampUsedClientIds == null)
            return 0;

        int usedCount = 0;

        for (int i = 0;
             i < LampUsedClientIds.Count;
             i++)
        {
            if (LampUsedClientIds[i] ==
                clientId)
            {
                usedCount++;
            }
        }

        return usedCount;
    }

    public bool IsHouseLampLit(int houseId)
    {
        if (LitHouseIds == null || houseId < 0)
            return false;

        for (int i = 0; i < LitHouseIds.Count; i++)
        {
            if (LitHouseIds[i] == houseId)
                return true;
        }

        return false;
    }

    private void ApplyAllLampStatesLocal()
    {
        if (houseRegistry == null)
            return;

        foreach (House house in houseRegistry.Houses)
        {
            if (house == null)
                continue;

            house.SetLampActiveLocal(
                IsHouseLampLit(house.Id)
            );
        }
    }

    private void ResetNightLampState()
    {
        if (!IsServer)
            return;

        LitHouseIds.Clear();
        villageFullyLit.Value = false;

        foreach (NetworkObject spiritObject in
                 spawnedSpirits.Values)
        {
            if (spiritObject == null ||
                !spiritObject.IsSpawned)
            {
                continue;
            }

            PlayerSpirit spirit =
                spiritObject.GetComponent<PlayerSpirit>();

            if (spirit != null)
                spirit.SetNightIdentityRevealed(false);
        }
    }

    public bool TryLightHouseLamp(
        ulong actorClientId,
        int houseId,
        Vector3 actorPosition,
        out Vector3 lampPosition)
    {
        lampPosition = actorPosition;

        if (!IsServer ||
            currentPhase.Value != MatchPhase.NightAction ||
            villageFullyLit.Value ||
            !IsAlivePlayer(actorClientId) ||
            HasPlayerUsedHouseLamp(actorClientId) ||
            IsHouseLampLit(houseId))
        {
            return false;
        }

        if (!TryGetPlayerSpirit(
                actorClientId,
                out PlayerSpirit actorSpirit) ||
            !actorSpirit.CanUseHouseLamp ||
            actorSpirit.CurrentHouseId >= 0)
        {
            return false;
        }

        House house = GetHouse(houseId);

        if (house == null ||
            house.OwnerClientId != actorClientId ||
            house.LampInteractionCollider == null)
        {
            return false;
        }

        float maximumDistance =
            lampInteractionDistance +
            lampServerDistanceTolerance;

        if (!house.IsNearLamp(
                actorPosition,
                maximumDistance))
        {
            return false;
        }

        LitHouseIds.Add(houseId);

        LampUsedClientIds.Add(
            actorClientId
        );

        lampPosition =
            house.LampInteractionPosition;

        int aliveCount = GetAlivePlayerCountServer();
        int requiredLampCount =
            aliveCount > 0
                ? aliveCount / 2 + 1
                : int.MaxValue;

        bool becameFullyLit =
            !villageFullyLit.Value &&
            LitHouseIds.Count >= requiredLampCount;

        if (becameFullyLit)
            villageFullyLit.Value = true;

        RefreshNightIdentityRevealStates();

        string notification =
            $"마을의 횃불이 하나 켜졌습니다. " +
            $"({LitHouseIds.Count} / {requiredLampCount})";

        if (becameFullyLit)
        {
            notification +=
                "\n마을이 밝아졌습니다. " +
                "모든 생존자의 신원이 공개됩니다.";
        }

        SendGlobalNotification(notification);
        return true;
    }

    private int GetAlivePlayerCountServer()
    {
        int aliveCount = 0;

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (state.isAlive)
                aliveCount++;
        }

        return aliveCount;
    }

    private void RefreshNightIdentityRevealStates()
    {
        if (!IsServer)
            return;

        bool isNightAction =
            currentPhase.Value == MatchPhase.NightAction;

        if (isNightAction &&
            !villageFullyLit.Value)
        {
            int aliveCount =
                GetAlivePlayerCountServer();

            int requiredLampCount =
                aliveCount > 0
                    ? aliveCount / 2 + 1
                    : int.MaxValue;

            if (LitHouseIds.Count >= requiredLampCount)
            {
                villageFullyLit.Value = true;

                SendGlobalNotification(
                    "마을이 밝아졌습니다. " +
                    "모든 생존자의 신원이 공개됩니다."
                );
            }
        }

        foreach (KeyValuePair<ulong, NetworkObject> pair in
                 spawnedSpirits)
        {
            NetworkObject spiritObject = pair.Value;

            if (spiritObject == null ||
                !spiritObject.IsSpawned)
            {
                continue;
            }

            PlayerSpirit spirit =
                spiritObject.GetComponent<PlayerSpirit>();

            if (spirit == null)
                continue;

            bool revealed = false;

            if (isNightAction &&
                playerMatchStates.TryGetValue(
                    pair.Key,
                    out PlayerMatchState state) &&
                state.isAlive)
            {
                revealed =
                    villageFullyLit.Value ||
                    IsHouseLampLit(
                        spirit.HomeHouseId
                    ) ||
                    IsHouseLampLit(
                        spirit.CurrentHouseId
                    ) ||
                    (
                        spirit.ConfinementType ==
                            SpiritConfinementType.ExternalCage &&
                        IsHouseLampLit(
                            spirit.ConfinedHouseId
                        )
                    );
            }

            spirit.SetNightIdentityRevealed(revealed);
        }
    }

    private void SendGlobalNotification(string message)
    {
        if (!IsServer ||
            string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ReceiveGlobalNotificationRpc(
            new FixedString512Bytes(message)
        );
    }

    [Rpc(SendTo.Everyone)]
    private void ReceiveGlobalNotificationRpc(
        FixedString512Bytes message)
    {
        GlobalNotificationReceived?.Invoke(
            message.ToString()
        );
    }


    private bool AssignRoles(List<LobbyPlayer> players)
    {
        if (!IsServer || LobbyRoomManager.Instance == null)
            return false;

        LobbySettings settings = LobbyRoomManager.Instance.CurrentSettings.Value;
        List<RoleId> rolePool = BuildRolePool(players.Count, settings);

        ShuffleRoles(rolePool);

        HashSet<RoleWeaponId>
            unavailableCitizenCoverWeapons = null;

        if (!settings.allowDuplicateRoles)
        {
            unavailableCitizenCoverWeapons =
                new HashSet<RoleWeaponId>();

            for (int i = 0; i < rolePool.Count; i++)
            {
                if (TryGetFixedCitizenWeapon(
                        rolePool[i],
                        out RoleWeaponId assignedWeapon))
                {
                    unavailableCitizenCoverWeapons.Add(
                        assignedWeapon
                    );
                }
            }
        }

        playerMatchStates.Clear();
        PublicPlayerStates.Clear();
        consumedExorcismPlayers.Clear();
        spyCitizenAbilityUseCountByClient.Clear();
        peddlerRealActionByClient.Clear();
        mafiaDisguiseWeaponByClient.Clear();
        ownedMafiaDisguiseWeaponsByClient.Clear();

        nightVisitorsByHouse.Clear();
        nightBloodPlayersByHouse.Clear();
        forensicsIntruderCountByVictim.Clear();
        forensicsDamagedPlayerNamesByVictim.Clear();
        forensicsDeathEvidenceByVictim.Clear();

        hunterRouteRecordsByHunter.Clear();
        nightHouseVisitsByClient.Clear();
        drunkardSleepTargets.Clear();
        activeDrunkardSleepHouseByClient.Clear();

        for (int i = 0; i < players.Count; i++)
        {
            LobbyPlayer lobbyPlayer = players[i];
            RoleId role = rolePool[i];
            RoleTeam team = GetRoleTeam(role);

            PlayerMatchState state = new PlayerMatchState(
                lobbyPlayer.clientId,
                lobbyPlayer.playerName,
                role,
                team
            );

            playerMatchStates[state.clientId] = state;
            PublicPlayerStates.Add(new PlayerPublicState(state.clientId, state.playerName));

            AssignInitialRoleWeapon(
                state.clientId,
                state.role,
                unavailableCitizenCoverWeapons
            );
            InitializeMafiaDisguiseOwnership(state);
            SendLocalMatchState(state);

            if (showRoleAssignmentLogs)
            {
                Debug.Log(
                    $"Role Assigned - Client: {state.clientId}, " +
                    $"Name: {state.playerName}, Role: {state.role}, Team: {state.team}"
                );
            }
        }

        if (!ValidateRoleAndWeaponAssignments(
                players,
                settings))
        {
            return false;
        }

        SendMafiaRosterToPlayers();
        return true;
    }

    private bool ValidateRoleAndWeaponAssignments(
        List<LobbyPlayer> players,
        LobbySettings settings)
    {
        if (!IsServer)
            return false;

        List<string> errors = new List<string>();

        if (players == null)
        {
            errors.Add("검증할 로비 플레이어 목록이 없습니다.");
        }
        else
        {
            if (players.Count != settings.TotalPlayerCount)
            {
                errors.Add(
                    $"설정 인원과 실제 배정 인원이 다릅니다. " +
                    $"Settings: {settings.TotalPlayerCount}, " +
                    $"Assigned: {players.Count}"
                );
            }

            if (playerMatchStates.Count != players.Count)
            {
                errors.Add(
                    $"역할 상태 수가 실제 배정 인원과 다릅니다. " +
                    $"States: {playerMatchStates.Count}, " +
                    $"Assigned: {players.Count}"
                );
            }

            if (PublicPlayerStates.Count != players.Count)
            {
                errors.Add(
                    $"공개 플레이어 상태 수가 실제 배정 인원과 다릅니다. " +
                    $"PublicStates: {PublicPlayerStates.Count}, " +
                    $"Assigned: {players.Count}"
                );
            }

            HashSet<ulong> assignedClientIds =
                new HashSet<ulong>();
            HashSet<RoleId> assignedRoles =
                new HashSet<RoleId>();
            HashSet<RoleWeaponId> identityWeapons =
                new HashSet<RoleWeaponId>();
            int assignedMafiaCount = 0;
            int assignedCitizenCount = 0;
            int assignedNeutralCount = 0;

            for (int i = 0; i < players.Count; i++)
            {
                LobbyPlayer player = players[i];

                if (!assignedClientIds.Add(player.clientId))
                {
                    errors.Add(
                        $"클라이언트가 중복 배정되었습니다. " +
                        $"Client: {player.clientId}"
                    );

                    continue;
                }

                if (!playerMatchStates.TryGetValue(
                        player.clientId,
                        out PlayerMatchState state))
                {
                    errors.Add(
                        $"플레이어 역할 상태가 없습니다. " +
                        $"Client: {player.clientId}"
                    );

                    continue;
                }

                if (!Enum.IsDefined(typeof(RoleId), state.role))
                {
                    errors.Add(
                        $"정의되지 않은 역할이 배정되었습니다. " +
                        $"Client: {state.clientId}, Role: {(byte)state.role}"
                    );

                    continue;
                }

                RoleTeam expectedTeam = GetRoleTeam(state.role);

                switch (expectedTeam)
                {
                    case RoleTeam.Mafia:
                        assignedMafiaCount++;
                        break;
                    case RoleTeam.Citizen:
                        assignedCitizenCount++;
                        break;
                    case RoleTeam.Neutral:
                        assignedNeutralCount++;
                        break;
                }

                if (state.team != expectedTeam)
                {
                    errors.Add(
                        $"역할과 진영이 일치하지 않습니다. " +
                        $"Client: {state.clientId}, Role: {state.role}, " +
                        $"Team: {state.team}, Expected: {expectedTeam}"
                    );
                }

                bool roleWasNew = assignedRoles.Add(state.role);

                if (!settings.allowDuplicateRoles &&
                    !roleWasNew)
                {
                    errors.Add(
                        $"중복 직업 비허용 설정에서 역할이 중복되었습니다. " +
                        $"Role: {state.role}"
                    );
                }

                if (!TryGetPlayerSpirit(
                        state.clientId,
                        out PlayerSpirit playerSpirit))
                {
                    errors.Add(
                        $"역할이 배정된 영체를 찾지 못했습니다. " +
                        $"Client: {state.clientId}"
                    );

                    continue;
                }

                SpiritWeaponView weaponView =
                    playerSpirit.GetComponentInChildren
                        <SpiritWeaponView>(true);

                if (weaponView == null)
                {
                    errors.Add(
                        $"역할이 배정된 영체에 무기 표시 컴포넌트가 없습니다. " +
                        $"Client: {state.clientId}"
                    );

                    continue;
                }

                RoleWeaponId weaponId =
                    (RoleWeaponId)weaponView.CurrentWeaponIndex;

                if (!Enum.IsDefined(typeof(RoleWeaponId), weaponId) ||
                    weaponId == RoleWeaponId.None)
                {
                    errors.Add(
                        $"유효한 초기 역할 도구가 배정되지 않았습니다. " +
                        $"Client: {state.clientId}, Role: {state.role}, " +
                        $"Weapon: {weaponView.CurrentWeaponIndex}"
                    );

                    continue;
                }

                bool usesIdentityWeapon = false;

                if (TryGetFixedCitizenWeapon(
                        state.role,
                        out RoleWeaponId expectedWeapon))
                {
                    usesIdentityWeapon = true;

                    if (weaponId != expectedWeapon)
                    {
                        errors.Add(
                            $"주민 역할과 초기 도구가 일치하지 않습니다. " +
                            $"Client: {state.clientId}, Role: {state.role}, " +
                            $"Weapon: {weaponId}, Expected: {expectedWeapon}"
                        );
                    }
                }
                else if (state.role == RoleId.Thief)
                {
                    if (weaponId != RoleWeaponId.PryBar)
                    {
                        errors.Add(
                            $"도둑의 초기 도구가 쇠지렛대가 아닙니다. " +
                            $"Client: {state.clientId}, Weapon: {weaponId}"
                        );
                    }
                }
                else if (state.role == RoleId.Peddler)
                {
                    if (!IsMafiaDisguiseWeapon(weaponId))
                    {
                        errors.Add(
                            $"행상인에게 주민 도구 풀 밖의 도구가 배정되었습니다. " +
                            $"Client: {state.clientId}, Weapon: {weaponId}"
                        );
                    }
                }
                else
                {
                    usesIdentityWeapon = true;

                    if (!IsMafiaDisguiseWeapon(weaponId))
                    {
                        errors.Add(
                            $"위장 역할에 유효하지 않은 주민 도구가 배정되었습니다. " +
                            $"Client: {state.clientId}, Role: {state.role}, " +
                            $"Weapon: {weaponId}"
                        );
                    }
                }

                if (!settings.allowDuplicateRoles &&
                    usesIdentityWeapon &&
                    !identityWeapons.Add(weaponId))
                {
                    errors.Add(
                        $"중복 직업 비허용 설정에서 식별 도구가 중복되었습니다. " +
                        $"Weapon: {weaponId}"
                    );
                }

                bool hasPublicState = false;

                for (int publicIndex = 0;
                     publicIndex < PublicPlayerStates.Count;
                     publicIndex++)
                {
                    if (PublicPlayerStates[publicIndex].clientId ==
                        state.clientId)
                    {
                        hasPublicState = true;
                        break;
                    }
                }

                if (!hasPublicState)
                {
                    errors.Add(
                        $"플레이어의 공개 상태가 없습니다. " +
                        $"Client: {state.clientId}"
                    );
                }
            }

            if (assignedMafiaCount != settings.mafiaCount ||
                assignedCitizenCount != settings.citizenCount ||
                assignedNeutralCount != settings.neutralCount)
            {
                errors.Add(
                    $"진영별 배정 인원이 설정과 다릅니다. " +
                    $"Mafia: {assignedMafiaCount}/{settings.mafiaCount}, " +
                    $"Citizen: {assignedCitizenCount}/{settings.citizenCount}, " +
                    $"Neutral: {assignedNeutralCount}/{settings.neutralCount}"
                );
            }

            Array roleValues = Enum.GetValues(typeof(RoleId));

            for (int i = 0; i < roleValues.Length; i++)
            {
                RoleId requiredRole =
                    (RoleId)roleValues.GetValue(i);

                if (settings.IsRoleRequired(requiredRole) &&
                    !assignedRoles.Contains(requiredRole))
                {
                    errors.Add(
                        $"필수 역할이 실제 배정 결과에 없습니다. " +
                        $"Role: {requiredRole}"
                    );
                }
            }
        }

        if (errors.Count == 0)
        {
            Debug.Log(
                $"Role assignment validation completed. " +
                $"Players: {players.Count}, " +
                $"DuplicateRoles: {settings.allowDuplicateRoles}"
            );

            return true;
        }

        for (int i = 0; i < errors.Count; i++)
        {
            Debug.LogError(
                $"Role assignment validation failed - {errors[i]}"
            );
        }

        return false;
    }

    private List<RoleId> BuildRolePool(
        int playerCount,
        LobbySettings settings)
    {
        List<RoleId> roles =
            new List<RoleId>(playerCount);

        if (playerCount <= 0)
            return roles;

        int mafiaSlotCount =
            Mathf.Clamp(
                settings.mafiaCount,
                0,
                playerCount
            );

        int neutralSlotCount =
            Mathf.Clamp(
                settings.neutralCount,
                0,
                playerCount -
                mafiaSlotCount
            );

        int citizenSlotCount =
            playerCount -
            mafiaSlotCount -
            neutralSlotCount;

        List<RoleId> mafiaRoles =
            new List<RoleId>(mafiaSlotCount);

        List<RoleId> citizenRoles =
            new List<RoleId>(citizenSlotCount);

        List<RoleId> neutralRoles =
            new List<RoleId>(neutralSlotCount);

        AddRequiredRoles(
            settings,
            mafiaRoles,
            citizenRoles,
            neutralRoles
        );

        List<RoleId> availableMafiaRoles =
            new List<RoleId>(
                randomMafiaRolePool
            );

        if (!settings.allowDuplicateRoles)
        {
            for (int i = 0; i < mafiaRoles.Count; i++)
                availableMafiaRoles.Remove(mafiaRoles[i]);
        }

        while (mafiaRoles.Count <
               mafiaSlotCount)
        {
            if (settings.allowDuplicateRoles)
            {
                mafiaRoles.Add(
                    randomMafiaRolePool[
                        UnityEngine.Random.Range(
                            0,
                            randomMafiaRolePool.Length
                        )
                    ]
                );

                continue;
            }

            int randomIndex =
                UnityEngine.Random.Range(
                    0,
                    availableMafiaRoles.Count
                );

            mafiaRoles.Add(
                availableMafiaRoles[randomIndex]
            );

            availableMafiaRoles.RemoveAt(
                randomIndex
            );
        }

        if (settings.allowDuplicateRoles)
        {
            bool hasDrunkard =
                citizenRoles.Contains(
                    RoleId.Drunkard
                );

            while (citizenRoles.Count <
                   citizenSlotCount)
            {
                RoleId randomRole =
                    GetRandomCitizenRole(
                        hasDrunkard
                    );

                citizenRoles.Add(
                    randomRole
                );

                if (randomRole ==
                    RoleId.Drunkard)
                {
                    hasDrunkard = true;
                }
            }
        }
        else
        {
            List<RoleId> availableCitizenRoles =
                new List<RoleId>(
                    randomCitizenRolePool
                );

            for (int i = 0; i < citizenRoles.Count; i++)
                availableCitizenRoles.Remove(citizenRoles[i]);

            while (citizenRoles.Count <
                   citizenSlotCount)
            {
                int randomIndex =
                    UnityEngine.Random.Range(
                        0,
                        availableCitizenRoles.Count
                    );

                citizenRoles.Add(
                    availableCitizenRoles[randomIndex]
                );

                availableCitizenRoles.RemoveAt(
                    randomIndex
                );
            }
        }

        while (neutralRoles.Count <
               neutralSlotCount)
        {
            List<RoleId> availableNeutralRoles =
                new List<RoleId>();

            for (int i = 0;
                 i < randomNeutralRolePool.Length;
                 i++)
            {
                RoleId candidate =
                    randomNeutralRolePool[i];

                if (settings.allowDuplicateRoles)
                {
                    if (candidate == RoleId.Thief ||
                        !neutralRoles.Contains(candidate))
                    {
                        availableNeutralRoles.Add(candidate);
                    }
                }
                else if (!neutralRoles.Contains(candidate))
                {
                    availableNeutralRoles.Add(candidate);
                }
            }

            neutralRoles.Add(
                availableNeutralRoles[
                    UnityEngine.Random.Range(
                        0,
                        availableNeutralRoles.Count
                    )
                ]
            );
        }

        roles.AddRange(mafiaRoles);
        roles.AddRange(citizenRoles);
        roles.AddRange(neutralRoles);

        return roles;
    }

    private void AddRequiredRoles(
        LobbySettings settings,
        List<RoleId> mafiaRoles,
        List<RoleId> citizenRoles,
        List<RoleId> neutralRoles)
    {
        Array roleValues =
            Enum.GetValues(typeof(RoleId));

        for (int i = 0; i < roleValues.Length; i++)
        {
            RoleId role =
                (RoleId)roleValues.GetValue(i);

            if (!settings.IsRoleRequired(role))
                continue;

            RoleTeam team =
                GetRoleTeam(role);

            if (team == RoleTeam.Mafia)
            {
                mafiaRoles.Add(role);
            }
            else if (team == RoleTeam.Citizen)
            {
                citizenRoles.Add(role);
            }
            else if (team == RoleTeam.Neutral)
            {
                neutralRoles.Add(role);
            }
        }
    }

    private RoleId GetRandomCitizenRole(
        bool hasDrunkard)
    {
        while (true)
        {
            int randomIndex =
                UnityEngine.Random.Range(
                    0,
                    randomCitizenRolePool.Length
                );

            RoleId selectedRole =
                randomCitizenRolePool[
                    randomIndex
                ];

            if (selectedRole !=
                    RoleId.Drunkard ||
                !hasDrunkard)
            {
                return selectedRole;
            }
        }
    }

    private void ShuffleRoles(List<RoleId> roles)
    {
        for (int i = roles.Count - 1; i > 0; i--)
        {
            int randomIndex = UnityEngine.Random.Range(0, i + 1);

            RoleId temp = roles[i];
            roles[i] = roles[randomIndex];
            roles[randomIndex] = temp;
        }
    }

    private RoleTeam GetRoleTeam(RoleId role)
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

    private void SendMafiaRosterToPlayers()
    {
        if (!IsServer)
            return;

        List<MafiaMemberData> mafiaMembers = new List<MafiaMemberData>();

        foreach (PlayerMatchState state in playerMatchStates.Values)
        {
            ClearLocalMafiaDataRpc(RpcTarget.Single(state.clientId, RpcTargetUse.Temp));

            if (state.team == RoleTeam.Mafia)
                mafiaMembers.Add(new MafiaMemberData(state.clientId, state.playerName));
        }

        foreach (PlayerMatchState receiverState in playerMatchStates.Values)
        {
            if (receiverState.team != RoleTeam.Mafia)
                continue;

            for (int i = 0; i < mafiaMembers.Count; i++)
            {
                AddLocalMafiaMemberRpc(
                    mafiaMembers[i],
                    RpcTarget.Single(receiverState.clientId, RpcTargetUse.Temp)
                );
            }
        }
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void ClearLocalMafiaDataRpc(RpcParams rpcParams = default)
    {
        localMafiaMembers.Clear();
        localNightKillerClientId = NoClientId;

        localMafiaDisguiseOptions.Clear();
        localMafiaDisguiseWeapon = RoleWeaponId.None;
        localCanSelectMafiaDisguise = false;

        LocalMafiaMembersChanged?.Invoke();
        LocalNightKillerChanged?.Invoke(localNightKillerClientId);
        LocalMafiaDisguiseOptionsChanged?.Invoke();
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void AddLocalMafiaMemberRpc(MafiaMemberData member, RpcParams rpcParams = default)
    {
        localMafiaMembers.Add(member);
        LocalMafiaMembersChanged?.Invoke();
    }

    private void SendSpectatorRoleSnapshot(
        ulong spectatorClientId)
    {
        if (!IsServer ||
            !playerMatchStates.TryGetValue(
                spectatorClientId,
                out PlayerMatchState spectatorState) ||
            spectatorState.isAlive)
        {
            return;
        }

        ClearLocalSpectatorRoleDataRpc(
            RpcTarget.Single(
                spectatorClientId,
                RpcTargetUse.Temp
            )
        );

        if (!TryGetPlayerSpirit(
                spectatorClientId,
                out PlayerSpirit spectatorSpirit) ||
            !spectatorSpirit.IsFreeDeadSpectator)
        {
            return;
        }

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            AddLocalSpectatorRoleDataRpc(
                state,
                RpcTarget.Single(
                    spectatorClientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    private void SendSpectatorRoleSnapshotsToDeadPlayers()
    {
        if (!IsServer)
            return;

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (!state.isAlive)
                SendSpectatorRoleSnapshot(state.clientId);
        }
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ClearLocalSpectatorRoleDataRpc(
        RpcParams rpcParams = default)
    {
        localSpectatorPlayerStates.Clear();
        LocalSpectatorPlayerStatesChanged?.Invoke();
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void AddLocalSpectatorRoleDataRpc(
        PlayerMatchState state,
        RpcParams rpcParams = default)
    {
        localSpectatorPlayerStates[state.clientId] = state;
        LocalSpectatorPlayerStatesChanged?.Invoke();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestLocalMatchStateRpc(RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong senderClientId = rpcParams.Receive.SenderClientId;

        if (!playerMatchStates.TryGetValue(senderClientId, out PlayerMatchState state))
            return;

        SendLocalMatchState(state);
        if (!state.isAlive)
            SendSpectatorRoleSnapshot(senderClientId);

        SendHouseOwnerDisplaySnapshot(senderClientId);
        SendNightDutyAssignmentToClient(senderClientId);

        if (state.team == RoleTeam.Mafia)
            SendCurrentNightKillerToClient(senderClientId);

        if (currentPhase.Value == MatchPhase.NightPreparation &&
            (state.team == RoleTeam.Mafia ||
             state.role == RoleId.SerialKiller))
        {
            SendMafiaDisguiseOptions(senderClientId);
        }
    }

    private void SendLocalMatchState(PlayerMatchState state)
    {
        if (!IsServer)
            return;

        ReceiveLocalMatchStateRpc(
            state,
            RpcTarget.Single(
                state.clientId,
                RpcTargetUse.Temp
            )
        );

        SendSpyCitizenAbilityUsage(
            state.clientId
        );
        SendFakeNightDutyUsage(
            state.clientId
        );
        SendAbilityFailureChance(
            state.clientId
        );
    }

    private void SendAbilityFailureChance(
        ulong clientId)
    {
        if (!IsServer ||
            NetworkManager == null ||
            !NetworkManager.ConnectedClients.ContainsKey(clientId))
        {
            return;
        }

        float finalFailureChance =
            GetCitizenAbilityFailureChance(
                clientId,
                out float dutyFailureChance,
                out float stabilityFailureChance
            );

        ReceiveAbilityFailureChanceRpc(
            dutyFailureChance,
            stabilityFailureChance,
            finalFailureChance,
            RpcTarget.Single(
                clientId,
                RpcTargetUse.Temp
            )
        );
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveAbilityFailureChanceRpc(
        float dutyFailureChance,
        float stabilityFailureChance,
        float finalFailureChance,
        RpcParams rpcParams = default)
    {
        localDutyFailureChance = Mathf.Clamp01(
            dutyFailureChance
        );
        localStabilityFailureChance = Mathf.Clamp01(
            stabilityFailureChance
        );
        localAbilityFailureChance = Mathf.Clamp01(
            finalFailureChance
        );

        LocalAbilityFailureChanceChanged?.Invoke();
    }

    private void SendFakeNightDutyUsage(
        ulong clientId)
    {
        if (!IsServer)
            return;

        int useCount =
            fakeNightDutyUseCountByClient.TryGetValue(
                clientId,
                out int savedUseCount)
                ? savedUseCount
                : 0;

        ReceiveFakeNightDutyUsageRpc(
            useCount,
            RpcTarget.Single(
                clientId,
                RpcTargetUse.Temp
            )
        );
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveFakeNightDutyUsageRpc(
        int useCount,
        RpcParams rpcParams = default)
    {
        localFakeNightDutyUseCount =
            Mathf.Clamp(
                useCount,
                0,
                MaximumFakeNightDutyUsesPerNight
            );
    }

    private void SendSpyCitizenAbilityUsage(
        ulong clientId)
    {
        if (!IsServer)
            return;

        int useCount =
            spyCitizenAbilityUseCountByClient.TryGetValue(
                clientId,
                out int savedUseCount)
                ? savedUseCount
                : 0;

        ReceiveSpyCitizenAbilityUsageRpc(
            useCount,
            RpcTarget.Single(
                clientId,
                RpcTargetUse.Temp
            )
        );
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveSpyCitizenAbilityUsageRpc(
        int useCount,
        RpcParams rpcParams = default)
    {
        localSpyCitizenAbilityUseCount =
            Mathf.Clamp(
                useCount,
                0,
                MaximumSpyCitizenAbilityUses
            );
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void ReceiveLocalMatchStateRpc(PlayerMatchState state, RpcParams rpcParams = default)
    {
        if (NetworkManager == null || state.clientId != NetworkManager.LocalClientId)
            return;

        localPlayerMatchState = state;
        hasLocalPlayerMatchState = true;

        RefreshAllHouseOwnerPresentationsLocal();

        ConfirmRoleAssignmentReceivedRpc(
            villageLayoutSeed.Value
        );

        LocalPlayerMatchStateReceived?.Invoke(state);

        Debug.Log($"내 직업 수신 - Client: {state.clientId}, Role: {state.role}, Team: {state.team}, Alive: {state.isAlive}");
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Everyone
    )]
    private void ConfirmRoleAssignmentReceivedRpc(
        int assignmentToken,
        RpcParams rpcParams = default)
    {
        if (!IsServer ||
            !waitingForRoleAssignmentConfirmations ||
            assignmentToken != villageLayoutSeed.Value)
        {
            return;
        }

        ulong clientId =
            rpcParams.Receive.SenderClientId;

        if (!expectedRoleAssignmentClientIds.Contains(
                clientId) ||
            !playerMatchStates.ContainsKey(clientId))
        {
            return;
        }

        if (confirmedRoleAssignmentClientIds.Add(clientId))
        {
            roleAssignmentReadyCount.Value =
                confirmedRoleAssignmentClientIds.Count;

            Debug.Log(
                "Role Assignment Confirmed - " +
                $"Client: {clientId}, " +
                $"Ready: {confirmedRoleAssignmentClientIds.Count}/" +
                $"{expectedRoleAssignmentClientIds.Count}"
            );
        }

        TryStartPhaseAfterRoleAssignmentConfirmations();
    }

    private void TryStartPhaseAfterRoleAssignmentConfirmations()
    {
        if (!IsServer ||
            !waitingForRoleAssignmentConfirmations ||
            currentPhase.Value != MatchPhase.None ||
            expectedRoleAssignmentClientIds.Count == 0)
        {
            return;
        }

        foreach (ulong clientId in
                 expectedRoleAssignmentClientIds)
        {
            if (!confirmedRoleAssignmentClientIds.Contains(
                    clientId))
            {
                return;
            }
        }

        waitingForRoleAssignmentConfirmations = false;
        roleAssignmentConfirmationDeadline = 0d;

        Debug.Log(
            "All role assignments confirmed. " +
            $"Starting match with " +
            $"{expectedRoleAssignmentClientIds.Count} players."
        );

        StartPhaseFlow();
    }

    private void BeginRoleAssignmentFailureReturn(
        string message)
    {
        if (!IsServer ||
            roleAssignmentFailureReturnStarted ||
            currentPhase.Value != MatchPhase.None)
        {
            return;
        }

        roleAssignmentFailureReturnStarted = true;
        waitingForRoleAssignmentConfirmations = false;
        roleAssignmentConfirmationDeadline = 0d;

        Debug.LogError(message);

        StartCoroutine(
            ReturnToLobbyAfterRoleAssignmentFailure(
                message
            )
        );
    }

    private IEnumerator
        ReturnToLobbyAfterRoleAssignmentFailure(
            string message)
    {
        yield return null;

        LobbyRoomManager lobbyRoomManager =
            LobbyRoomManager.Instance;

        if (lobbyRoomManager == null ||
            !lobbyRoomManager.IsSpawned)
        {
            Debug.LogError(
                "직업 배정 실패 후 돌아갈 " +
                "LobbyRoomManager가 없습니다."
            );

            yield break;
        }

        lobbyRoomManager.SetLobbyNotice(message);

        if (!lobbyRoomManager.ReturnToLobbyFromGame())
        {
            Debug.LogError(
                "직업 배정 실패 후 LobbyScene 로드를 " +
                "시작하지 못했습니다."
            );
        }
    }

    private void StartPhaseFlow()
    {
        if (!IsServer)
            return;

        currentDay.Value = 1;
        BeginPhase(MatchPhase.MorningVote);
    }

    private void BeginPhase(MatchPhase phase)
    {
        if (!IsServer)
            return;

        if (currentPhase.Value == MatchPhase.NightAction &&
            phase != MatchPhase.NightAction)
        {
            EndAllMediumCommunications();
        }

        if (phase == MatchPhase.MorningVote)
        {
            morningVoteResult.Value = default;
            nightResult.Value = default;
        }

        if (phase == MatchPhase.MorningVote)
        {
            morningVotes.Clear();
            morningVoteDrafts.Clear();
            InitializeMorningVoteTallies();
        }

        if (phase == MatchPhase.NightPreparation)
            PrepareNightState();

        bool applyGameplayStateAfterPhaseChange =
            phase == MatchPhase.NightAction ||
            phase == MatchPhase.FinalDuel;

        if (!applyGameplayStateAfterPhaseChange)
            ApplyPhaseGameplayState(phase);

        float duration = GetPhaseDuration(phase);

        phaseEndServerTime.Value = duration > 0f ? NetworkManager.ServerTime.Time + duration : 0d;
        currentPhase.Value = phase;

        if (applyGameplayStateAfterPhaseChange)
            ApplyPhaseGameplayState(phase);

        RefreshNightIdentityRevealStates();

        Debug.Log($"Phase Started - Day: {currentDay.Value}, Phase: {phase}, Duration: {duration:0.##}");
    }

    private void PrepareNightState()
    {
        mafiaKillerVotes.Clear();
        SendMafiaKillerVoteSnapshotToMafia();
        doctorProtectTargets.Clear();
        drunkardSleepTargets.Clear();
        completedNightRoleActions.Clear();

        EndAllMediumCommunications();
        activeMediumTargetByMedium.Clear();
        activeMediumsByDeadTarget.Clear();

        if (SealedFrontDoorHouseIds != null)
            SealedFrontDoorHouseIds.Clear();

        /*
         * 이번 밤에 새로 쌓을 현장 기록만 초기화한다.
         * 사망자별 감식 증거는 시체가 남아 있는 동안 유지한다.
         */
        nightVisitorsByHouse.Clear();
        nightBloodPlayersByHouse.Clear();
        hunterRouteRecordsByHunter.Clear();
        nightHouseVisitsByClient.Clear();

        currentNightKillerClientId = NoClientId;
        mafiaKillTargetClientId = NoClientId;
        serialKillerTargetClientId = NoClientId;
        currentNightKillMethod = NightKillMethod.None;
        currentNightKillerWeaponIndex = -1;
        serialKillerWeaponIndex = -1;
        curseDollInitialPlacementPending = false;

        lastNightDeathWasPoisoned.Value = false;

        ResetNightLampState();
        PrepareMafiaDisguiseSelections();
        RefreshNightlyRoleWeapons();
        SendCurrentNightKillerToMafia();
        ResetDrunkardSleepSelectionsForClients();
        AssignNightDuties();
    }

    private void RefreshNightlyRoleWeapons()
    {
        peddlerRealActionByClient.Clear();

        foreach (PlayerMatchState state in playerMatchStates.Values)
        {
            if (!state.isAlive ||
                state.role != RoleId.Peddler)
            {
                continue;
            }

            RoleWeaponId randomWeapon =
                GetRandomCitizenWeapon();

            if (!SetPlayerWeapon(
                    state.clientId,
                    randomWeapon))
            {
                continue;
            }

            bool isReal =
                UnityEngine.Random.value < 0.5f;

            peddlerRealActionByClient[
                state.clientId
            ] = isReal;

            if (showRoleAssignmentLogs)
            {
                Debug.Log(
                    $"Peddler Night Tool - " +
                    $"Client: {state.clientId}, " +
                    $"Weapon: {randomWeapon}, " +
                    $"Real: {isReal}"
                );
            }
        }
    }

    private void AssignNightDuties()
    {
        nightDutyAssignmentsByClient.Clear();
        fakeNightDutyUseCountByClient.Clear();

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (NetworkManager != null &&
                NetworkManager.ConnectedClients
                    .ContainsKey(state.clientId))
            {
                SendNightDutyAssignmentToClient(
                    state.clientId
                );
                SendFakeNightDutyUsage(
                    state.clientId
                );
            }
        }

        List<NightDutyPoint> dutyPoints =
            GetAvailableNightDutyPoints();

        if (dutyPoints.Count == 0)
        {
            if (showNightActionLogs)
            {
                Debug.LogWarning(
                    "Night duty assignment skipped - " +
                    "No active NightDutyPoint was found."
                );
            }

            return;
        }

        List<ulong> eligibleClientIds =
            new List<ulong>();

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (state.isAlive &&
                state.team == RoleTeam.Citizen)
            {
                eligibleClientIds.Add(state.clientId);
            }
        }

        eligibleClientIds.Sort();

        for (int i = dutyPoints.Count - 1;
             i > 0;
             i--)
        {
            int swapIndex =
                UnityEngine.Random.Range(0, i + 1);

            NightDutyPoint savedPoint =
                dutyPoints[i];

            dutyPoints[i] =
                dutyPoints[swapIndex];

            dutyPoints[swapIndex] =
                savedPoint;
        }

        int pointIndex = 0;

        for (int i = 0;
             i < eligibleClientIds.Count;
             i++)
        {
            ulong clientId = eligibleClientIds[i];
            int assignmentCount = Mathf.Min(
                NightDutyAssignmentsPerPlayer,
                dutyPoints.Count
            );

            List<NightDutyAssignment> assignments =
                new List<NightDutyAssignment>(
                    assignmentCount
                );

            for (int dutyIndex = 0;
                 dutyIndex < assignmentCount;
                 dutyIndex++)
            {
                NightDutyPoint point =
                    dutyPoints[
                        pointIndex % dutyPoints.Count
                    ];

                pointIndex++;

                string dutyName = point.DutyName;

                if (point.HouseId >= 0 &&
                    houseOwnerDisplayStates.TryGetValue(
                        point.HouseId,
                        out HouseOwnerDisplayState
                            ownerState))
                {
                    string ownerName =
                        ownerState.playerName.ToString();

                    if (!string.IsNullOrWhiteSpace(
                            ownerName))
                    {
                        dutyName =
                            $"{ownerName}의 {dutyName}";
                    }
                }

                NightDutyAssignment assignment =
                    new NightDutyAssignment
                    {
                        houseId = point.HouseId,
                        pointId = point.PointId,
                        dutyName = dutyName,
                        completed = false
                    };

                assignments.Add(assignment);

                if (showNightActionLogs)
                {
                    Debug.Log(
                        $"Night Duty Assigned - " +
                        $"Client: {clientId}, " +
                        $"Slot: {dutyIndex}, " +
                        $"House: {assignment.houseId}, " +
                        $"Point: {assignment.pointId}, " +
                        $"Name: {assignment.dutyName}"
                    );
                }
            }

            nightDutyAssignmentsByClient[clientId] =
                assignments;

            SendNightDutyAssignmentToClient(clientId);

        }
    }

    private void AssignMafiaInterferenceDuties()
    {
        if (!IsServer)
            return;

        mafiaInterferenceAssignmentsByClient.Clear();
        fakeNightDutyUseCountByClient.Clear();
        mafiaInterferenceCompletedCount = 0;
        mafiaInterferenceMaximumCount = 0;

        List<NightDutyPoint> dutyPoints =
            GetAvailableNightDutyPoints();

        for (int i = dutyPoints.Count - 1;
             i > 0;
             i--)
        {
            int swapIndex =
                UnityEngine.Random.Range(0, i + 1);
            NightDutyPoint savedPoint = dutyPoints[i];
            dutyPoints[i] = dutyPoints[swapIndex];
            dutyPoints[swapIndex] = savedPoint;
        }

        List<ulong> aliveMafiaClientIds =
            new List<ulong>();

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (state.isAlive &&
                state.team == RoleTeam.Mafia)
            {
                aliveMafiaClientIds.Add(state.clientId);
            }
        }

        List<ulong> eligibleMafiaClientIds =
            new List<ulong>();

        for (int i = 0;
             i < aliveMafiaClientIds.Count;
             i++)
        {
            ulong clientId = aliveMafiaClientIds[i];

            if (aliveMafiaClientIds.Count == 1 ||
                clientId != currentNightKillerClientId)
            {
                eligibleMafiaClientIds.Add(clientId);
            }
        }

        eligibleMafiaClientIds.Sort();

        int pointIndex = 0;

        for (int i = 0;
             i < eligibleMafiaClientIds.Count;
             i++)
        {
            ulong clientId = eligibleMafiaClientIds[i];
            int assignmentCount = Mathf.Min(
                NightDutyAssignmentsPerPlayer,
                dutyPoints.Count
            );

            List<NightDutyAssignment> assignments =
                new List<NightDutyAssignment>(
                    assignmentCount
                );

            for (int assignmentIndex = 0;
                 assignmentIndex < assignmentCount;
                 assignmentIndex++)
            {
                NightDutyPoint point =
                    dutyPoints[
                        pointIndex % dutyPoints.Count
                    ];

                pointIndex++;

                NightDutyAssignment assignment =
                    new NightDutyAssignment
                    {
                        houseId = point.HouseId,
                        pointId = point.PointId,
                        dutyName =
                            BuildMafiaInterferenceDutyName(
                                point
                            ),
                        completed = false
                    };

                assignments.Add(assignment);

                if (showNightActionLogs)
                {
                    Debug.Log(
                        $"Mafia Interference Assigned - " +
                        $"Client: {clientId}, " +
                        $"Slot: {assignmentIndex}, " +
                        $"House: {assignment.houseId}, " +
                        $"Point: {assignment.pointId}, " +
                        $"Name: {assignment.dutyName}"
                    );
                }
            }

            mafiaInterferenceAssignmentsByClient[
                clientId
            ] = assignments;

            if (assignments.Count > 0)
                mafiaInterferenceMaximumCount++;
        }

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (state.team != RoleTeam.Mafia)
                continue;

            SendNightDutyAssignmentToClient(
                state.clientId
            );
            SendFakeNightDutyUsage(state.clientId);
        }
    }

    private string BuildMafiaInterferenceDutyName(
        NightDutyPoint point)
    {
        if (point == null)
            return "교란 공작";

        string actionName;

        if (point.HouseId < 0)
        {
            actionName = "마을 광장 우물 오염";
        }
        else
        {
            switch (point.PointId)
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
                    actionName = $"{point.DutyName} 교란";
                    break;
            }
        }

        if (point.HouseId >= 0 &&
            houseOwnerDisplayStates.TryGetValue(
                point.HouseId,
                out HouseOwnerDisplayState ownerState))
        {
            string ownerName =
                ownerState.playerName.ToString();

            if (!string.IsNullOrWhiteSpace(ownerName))
                return $"{ownerName}의 {actionName}";
        }

        return actionName;
    }

    private void SettleNightDutyResults()
    {
        if (!IsServer)
            return;

        previousNightMissedDutyCountByClient.Clear();

        int totalAssignmentCount = 0;
        int completedAssignmentCount = 0;

        foreach (KeyValuePair<ulong, List<NightDutyAssignment>>
                 pair in nightDutyAssignmentsByClient)
        {
            List<NightDutyAssignment> assignments = pair.Value;

            if (assignments == null || assignments.Count == 0)
                continue;

            int playerCompletedCount = 0;

            for (int i = 0; i < assignments.Count; i++)
            {
                if (assignments[i].completed)
                    playerCompletedCount++;
            }

            int missedCount = Mathf.Clamp(
                assignments.Count - playerCompletedCount,
                0,
                NightDutyAssignmentsPerPlayer
            );

            previousNightMissedDutyCountByClient[pair.Key] =
                missedCount;

            totalAssignmentCount += assignments.Count;
            completedAssignmentCount += playerCompletedCount;
        }

        if (totalAssignmentCount > 0)
        {
            int effectiveCompletedAssignmentCount =
                Mathf.Max(
                    0,
                    completedAssignmentCount -
                    mafiaInterferenceCompletedCount
                );

            float completionRatio =
                effectiveCompletedAssignmentCount /
                (float)totalAssignmentCount;

            villageStabilityPercent.Value =
                Mathf.Clamp(
                    villageStabilityPercent.Value +
                    GetVillageStabilityDelta(
                        completionRatio
                    ),
                    0,
                    100
                );

            if (villageStabilityPercent.Value == 100)
            {
                consecutivePerfectStabilityNights.Value++;

                if (consecutivePerfectStabilityNights.Value >= 2 &&
                    houseLampUseAllowance.Value < 2)
                {
                    houseLampUseAllowance.Value = 2;

                    SendGlobalNotification(
                        "마을의 안정이 유지되어 " +
                        "모든 생존자의 횃불 사용 가능 횟수가 " +
                        "1회 증가했습니다."
                    );
                }
            }
            else
            {
                consecutivePerfectStabilityNights.Value = 0;
            }
        }

        int settledCompletedAssignmentCount =
            Mathf.Max(
                0,
                completedAssignmentCount -
                mafiaInterferenceCompletedCount
            );

        lastNightCompletedDutyCount.Value =
            settledCompletedAssignmentCount;
        lastNightTotalDutyCount.Value =
            totalAssignmentCount;

        SendMafiaInterferenceSummaryRecords();

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            SendAbilityFailureChance(state.clientId);
        }

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Night Duty Settled - " +
                $"Completed: {completedAssignmentCount}/" +
                $"{totalAssignmentCount}, " +
                $"MafiaInterference: " +
                $"{mafiaInterferenceCompletedCount}, " +
                $"Effective: {settledCompletedAssignmentCount}/" +
                $"{totalAssignmentCount}, " +
                $"VillageStability: " +
                $"{villageStabilityPercent.Value}%"
            );
        }
    }

    private static int GetVillageStabilityDelta(
        float completionRatio)
    {
        if (completionRatio >= 1f)
            return 25;

        if (completionRatio >= 0.75f)
            return 15;

        if (completionRatio > 0.5f)
            return 5;

        if (completionRatio > 0.25f)
            return -5;

        if (completionRatio > 0f)
            return -15;

        return -25;
    }

    private List<NightDutyPoint>
        GetAvailableNightDutyPoints()
    {
        NightDutyPoint[] foundPoints =
            FindObjectsByType<NightDutyPoint>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );

        List<NightDutyPoint> result =
            new List<NightDutyPoint>();

        HashSet<long> registeredKeys =
            new HashSet<long>();

        for (int i = 0;
             i < foundPoints.Length;
             i++)
        {
            NightDutyPoint point = foundPoints[i];

            if (point == null ||
                point.PointId < 0 ||
                !point.CanInteract)
            {
                continue;
            }

            long key =
                GetNightDutyPointKey(
                    point.HouseId,
                    point.PointId
                );

            if (!registeredKeys.Add(key))
            {
                Debug.LogWarning(
                    $"Duplicate NightDutyPoint ignored - " +
                    $"House: {point.HouseId}, " +
                    $"Point: {point.PointId}"
                );

                continue;
            }

            result.Add(point);
        }

        result.Sort(
            (left, right) =>
            {
                int houseComparison =
                    left.HouseId.CompareTo(
                        right.HouseId
                    );

                return houseComparison != 0
                    ? houseComparison
                    : left.PointId.CompareTo(
                        right.PointId
                    );
            }
        );

        return result;
    }

    private static long GetNightDutyPointKey(
        int houseId,
        int pointId)
    {
        return ((long)houseId << 32) |
               (uint)pointId;
    }

    private NightDutyPoint FindNightDutyPoint(
        int houseId,
        int pointId)
    {
        NightDutyPoint[] foundPoints =
            FindObjectsByType<NightDutyPoint>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );

        for (int i = 0;
             i < foundPoints.Length;
             i++)
        {
            NightDutyPoint point = foundPoints[i];

            if (point != null &&
                point.CanInteract &&
                point.Matches(houseId, pointId))
            {
                return point;
            }
        }

        return null;
    }

    public bool IsLocalAssignedNightDutyPoint(
        NightDutyPoint point)
    {
        if (point == null ||
            (localNightDutyIsMafiaInterference &&
             IsLocalNightDutyCompleted))
            return false;

        for (int i = 0;
             i < localNightDutyAssignments.Count;
             i++)
        {
            NightDutyAssignment assignment =
                localNightDutyAssignments[i];

            if (!assignment.completed &&
                point.Matches(
                    assignment.houseId,
                    assignment.pointId
                ))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsLocalNightDutyInteractionPoint(
        NightDutyPoint point)
    {
        if (point == null || !point.CanInteract)
            return false;

        return IsLocalAssignedNightDutyPoint(point);
    }

    public void SubmitNightDutyCompletion(
        int houseId,
        int pointId)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction)
        {
            return;
        }

        SubmitNightDutyCompletionRpc(
            houseId,
            pointId
        );
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Everyone
    )]
    private void SubmitNightDutyCompletionRpc(
        int houseId,
        int pointId,
        RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong clientId =
            rpcParams.Receive.SenderClientId;

        bool accepted = false;
        int fakeUseCount =
            fakeNightDutyUseCountByClient.TryGetValue(
                clientId,
                out int savedFakeUseCount)
                ? savedFakeUseCount
                : 0;

        if (currentPhase.Value == MatchPhase.NightAction &&
            playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state) &&
            state.isAlive &&
            TryGetPlayerSpirit(
                clientId,
                out PlayerSpirit playerSpirit) &&
            playerSpirit.CanUseRoleAction)
        {
            NightDutyPoint point =
                FindNightDutyPoint(
                    houseId,
                    pointId
                );

            float maximumDistance =
                roleActionDistance +
                roleActionServerTolerance;

            bool validPoint =
                point != null &&
                point.IsNear(
                    playerSpirit.transform.position,
                    maximumDistance
                );

            if (validPoint &&
                state.team == RoleTeam.Citizen &&
                nightDutyAssignmentsByClient.TryGetValue(
                    clientId,
                    out List<NightDutyAssignment>
                        assignments))
            {
                int assignmentIndex = -1;

                for (int i = 0;
                     i < assignments.Count;
                     i++)
                {
                    NightDutyAssignment candidate =
                        assignments[i];

                    if (!candidate.completed &&
                        candidate.houseId == houseId &&
                        candidate.pointId == pointId)
                    {
                        assignmentIndex = i;
                        break;
                    }
                }

                if (assignmentIndex >= 0)
                {
                    NightDutyAssignment assignment =
                        assignments[assignmentIndex];

                    assignment.completed = true;
                    assignments[assignmentIndex] =
                        assignment;

                    int completedCount = 0;

                    for (int i = 0;
                         i < assignments.Count;
                         i++)
                    {
                        if (assignments[i].completed)
                            completedCount++;
                    }

                    SendNightDutyAssignmentToClient(
                        clientId
                    );
                    SendPrivateNotification(
                        clientId,
                        $"직무 완료 " +
                        $"({completedCount}/" +
                        $"{assignments.Count}): " +
                        assignment.dutyName
                    );

                    accepted = true;

                    if (showNightActionLogs)
                    {
                        Debug.Log(
                            $"Night Duty Completed - " +
                            $"Client: {clientId}, " +
                            $"House: {houseId}, " +
                            $"Point: {pointId}, " +
                            $"Progress: {completedCount}/" +
                            $"{assignments.Count}"
                        );
                    }
                }
            }
            else if (validPoint &&
                     state.team == RoleTeam.Mafia &&
                     fakeUseCount <
                         MaximumFakeNightDutyUsesPerNight &&
                     mafiaInterferenceAssignmentsByClient
                         .TryGetValue(
                             clientId,
                             out List<NightDutyAssignment>
                                 interferenceAssignments))
            {
                int assignmentIndex = -1;

                for (int i = 0;
                     i < interferenceAssignments.Count;
                     i++)
                {
                    NightDutyAssignment candidate =
                        interferenceAssignments[i];

                    if (!candidate.completed &&
                        candidate.houseId == houseId &&
                        candidate.pointId == pointId)
                    {
                        assignmentIndex = i;
                        break;
                    }
                }

                if (assignmentIndex >= 0)
                {
                    NightDutyAssignment assignment =
                        interferenceAssignments[
                            assignmentIndex
                        ];

                    assignment.completed = true;
                    interferenceAssignments[
                        assignmentIndex
                    ] = assignment;

                    fakeUseCount++;
                    fakeNightDutyUseCountByClient[clientId] =
                        fakeUseCount;
                    mafiaInterferenceCompletedCount++;

                    SendMafiaInterferenceProgressToMafia();
                    SendMafiaInterferencePersonalRecord(
                        clientId,
                        assignment
                    );

                    SendPrivateNotification(
                        clientId,
                        "교란 공작을 완료했습니다."
                    );

                    accepted = true;

                    if (showNightActionLogs)
                    {
                        Debug.Log(
                            $"Mafia Interference Completed - " +
                            $"Client: {clientId}, " +
                            $"House: {houseId}, " +
                            $"Point: {pointId}, " +
                            $"TeamProgress: " +
                            $"{mafiaInterferenceCompletedCount}/" +
                            $"{mafiaInterferenceMaximumCount}"
                        );
                    }
                }
            }
        }

        if (!accepted &&
            playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState rejectedState) &&
            rejectedState.team == RoleTeam.Mafia)
        {
            string rejectionMessage;

            if (currentPhase.Value != MatchPhase.NightAction)
            {
                rejectionMessage =
                    "밤 행동 시간이 끝나 교란 공작 요청이 거절되었습니다.";
            }
            else if (!rejectedState.isAlive ||
                     !TryGetPlayerSpirit(
                         clientId,
                         out PlayerSpirit rejectedSpirit) ||
                     !rejectedSpirit.CanUseRoleAction)
            {
                rejectionMessage =
                    "현재 상태에서는 교란 공작을 수행할 수 없습니다.";
            }
            else if (clientId == currentNightKillerClientId &&
                     !mafiaInterferenceAssignmentsByClient
                         .ContainsKey(clientId))
            {
                rejectionMessage =
                    "살해 담당은 교란 공작을 수행할 수 없습니다.";
            }
            else if (fakeUseCount >=
                     MaximumFakeNightDutyUsesPerNight)
            {
                rejectionMessage =
                    "이미 오늘 밤 교란 공작을 완료했습니다.";
            }
            else if (FindNightDutyPoint(
                         houseId,
                         pointId) == null)
            {
                rejectionMessage =
                    "교란 목표를 찾을 수 없습니다.";
            }
            else if (!FindNightDutyPoint(
                         houseId,
                         pointId).IsNear(
                         rejectedSpirit.transform.position,
                         roleActionDistance +
                         roleActionServerTolerance))
            {
                rejectionMessage =
                    "교란 목표와의 거리가 너무 멉니다.";
            }
            else
            {
                rejectionMessage =
                    "배정된 교란 목표가 아닙니다.";
            }

            SendPrivateNotification(
                clientId,
                rejectionMessage
            );
        }

        ReceiveNightDutyCompletionResultRpc(
            accepted,
            fakeUseCount,
            RpcTarget.Single(
                clientId,
                RpcTargetUse.Temp
            )
        );
    }

    private void SendNightDutyAssignmentToClient(
        ulong clientId)
    {
        if (!IsServer ||
            NetworkManager == null ||
            !NetworkManager.ConnectedClients
                .ContainsKey(clientId))
        {
            return;
        }

        bool isMafiaInterference =
            playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state) &&
            state.team == RoleTeam.Mafia &&
            currentNightKillerClientId != NoClientId;

        Dictionary<ulong, List<NightDutyAssignment>>
            assignmentSource = isMafiaInterference
                ? mafiaInterferenceAssignmentsByClient
                : nightDutyAssignmentsByClient;

        if (!assignmentSource.TryGetValue(
                clientId,
                out List<NightDutyAssignment> assignments) ||
            assignments.Count == 0)
        {
            ReceiveLocalNightDutyAssignmentsRpc(
                0,
                isMafiaInterference,
                mafiaInterferenceCompletedCount,
                mafiaInterferenceMaximumCount,
                -1,
                -1,
                default,
                false,
                -1,
                -1,
                default,
                false,
                RpcTarget.Single(
                    clientId,
                    RpcTargetUse.Temp
                )
            );

            return;
        }

        NightDutyAssignment firstAssignment =
            assignments[0];
        bool hasSecondAssignment =
            assignments.Count > 1;
        NightDutyAssignment secondAssignment =
            hasSecondAssignment
                ? assignments[1]
                : default;

        ReceiveLocalNightDutyAssignmentsRpc(
            (byte)Mathf.Min(
                assignments.Count,
                NightDutyAssignmentsPerPlayer
            ),
            isMafiaInterference,
            mafiaInterferenceCompletedCount,
            mafiaInterferenceMaximumCount,
            firstAssignment.houseId,
            firstAssignment.pointId,
            new FixedString64Bytes(
                LimitUtf8Text(
                    firstAssignment.dutyName,
                    60
                )
            ),
            firstAssignment.completed,
            hasSecondAssignment
                ? secondAssignment.houseId
                : -1,
            hasSecondAssignment
                ? secondAssignment.pointId
                : -1,
            hasSecondAssignment
                ? new FixedString64Bytes(
                    LimitUtf8Text(
                        secondAssignment.dutyName,
                        60
                    )
                )
                : default,
            hasSecondAssignment &&
                secondAssignment.completed,
            RpcTarget.Single(
                clientId,
                RpcTargetUse.Temp
            )
        );
    }

    private void SendMafiaInterferenceProgressToMafia()
    {
        if (!IsServer)
            return;

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (state.isAlive &&
                state.team == RoleTeam.Mafia)
            {
                SendNightDutyAssignmentToClient(
                    state.clientId
                );
            }
        }
    }

    private void SendMafiaInterferencePersonalRecord(
        ulong clientId,
        NightDutyAssignment assignment)
    {
        if (!IsServer ||
            !playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state))
        {
            return;
        }

        ulong targetClientId = NoClientId;
        House targetHouse = GetHouse(assignment.houseId);

        if (targetHouse != null &&
            targetHouse.OwnerClientId !=
                House.NoOwnerClientId)
        {
            targetClientId = targetHouse.OwnerClientId;
        }

        PersonalActionRecordData record =
            new PersonalActionRecordData
            {
                day = currentDay.Value,
                kind = PersonalActionRecordKind
                    .MafiaInterference,
                actorClientId = clientId,
                role = state.role,
                actionType = RoleActionType.None,
                targetClientId = targetClientId,
                targetHouseId = assignment.houseId,
                targetPointId = assignment.pointId,
                inheritedRole = (RoleId)byte.MaxValue,
                inheritedTeam = (RoleTeam)byte.MaxValue,
                effectSucceeded = true,
                wasFake = false,
                dutyFailureChance = 0f,
                stabilityFailureChance = 0f,
                finalFailureChance = 0f
            };

        ReceivePersonalActionRecordRpc(
            record,
            RpcTarget.Single(
                clientId,
                RpcTargetUse.Temp
            )
        );
    }

    private void SendMafiaInterferenceSummaryRecords()
    {
        if (!IsServer ||
            mafiaInterferenceMaximumCount <= 0)
        {
            return;
        }

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (!state.isAlive ||
                state.team != RoleTeam.Mafia ||
                NetworkManager == null ||
                !NetworkManager.ConnectedClients.ContainsKey(
                    state.clientId))
            {
                continue;
            }

            PersonalActionRecordData record =
                new PersonalActionRecordData
                {
                    day = currentDay.Value,
                    kind = PersonalActionRecordKind
                        .MafiaInterferenceSummary,
                    actorClientId = NoClientId,
                    role = (RoleId)byte.MaxValue,
                    actionType = RoleActionType.None,
                    targetClientId = NoClientId,
                    targetHouseId = -1,
                    targetPointId =
                        mafiaInterferenceCompletedCount,
                    inheritedRole = (RoleId)byte.MaxValue,
                    inheritedTeam = (RoleTeam)byte.MaxValue,
                    effectSucceeded = true,
                    wasFake = false,
                    dutyFailureChance = 0f,
                    stabilityFailureChance = 0f,
                    finalFailureChance = 0f
                };

            ReceivePersonalActionRecordRpc(
                record,
                RpcTarget.Single(
                    state.clientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveLocalNightDutyAssignmentsRpc(
        byte assignmentCount,
        bool isMafiaInterference,
        int interferenceCompletedCount,
        int interferenceMaximumCount,
        int firstHouseId,
        int firstPointId,
        FixedString64Bytes firstDutyName,
        bool firstCompleted,
        int secondHouseId,
        int secondPointId,
        FixedString64Bytes secondDutyName,
        bool secondCompleted,
        RpcParams rpcParams = default)
    {
        localNightDutyAssignments.Clear();
        localNightDutyIsMafiaInterference =
            isMafiaInterference;
        localMafiaInterferenceCompletedCount =
            Mathf.Max(0, interferenceCompletedCount);
        localMafiaInterferenceMaximumCount =
            Mathf.Max(0, interferenceMaximumCount);

        if (assignmentCount > 0)
        {
            localNightDutyAssignments.Add(
                new NightDutyAssignment
                {
                    houseId = firstHouseId,
                    pointId = firstPointId,
                    dutyName = firstDutyName.ToString(),
                    completed = firstCompleted
                }
            );
        }

        if (assignmentCount > 1)
        {
            localNightDutyAssignments.Add(
                new NightDutyAssignment
                {
                    houseId = secondHouseId,
                    pointId = secondPointId,
                    dutyName = secondDutyName.ToString(),
                    completed = secondCompleted
                }
            );
        }

        LocalNightDutyChanged?.Invoke();
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveNightDutyCompletionResultRpc(
        bool accepted,
        int fakeUseCount,
        RpcParams rpcParams = default)
    {
        localFakeNightDutyUseCount =
            Mathf.Clamp(
                fakeUseCount,
                0,
                MaximumFakeNightDutyUsesPerNight
            );

        LocalNightDutyCompletionResultReceived?.Invoke(
            accepted
        );
    }

    private void ClearLocalNightDutyAssignment()
    {
        localNightDutyAssignments.Clear();
        localNightDutyIsMafiaInterference = false;
        localMafiaInterferenceCompletedCount = 0;
        localMafiaInterferenceMaximumCount = 0;
    }

    private void ApplyPhaseGameplayState(MatchPhase phase)
    {
        if (phase == MatchPhase.MorningDiscussion ||
            phase == MatchPhase.MorningVote)
        {
            ResetNightLampState();
            ReleaseEligibleDeadSpectatorsForMorning();
            ReturnDrunkardBodiesHomeForMorning();
            ReturnAllSpiritsHomeForMorning();
            RefreshNightIdentityRevealStates();
            return;
        }

        if (phase == MatchPhase.FinalDuel)
        {
            ApplyFinalDuelGameplayState();
            RefreshNightIdentityRevealStates();
            return;
        }

        SetAllSpiritPhaseControl(
            phase == MatchPhase.NightAction
        );

        RefreshNightIdentityRevealStates();
    }

    private void ApplyFinalDuelGameplayState()
    {
        SetAllSpiritPhaseControl(false);

        foreach (KeyValuePair<ulong, NetworkObject> pair in spawnedSpirits)
        {
            NetworkObject spiritObject = pair.Value;

            if (spiritObject == null || !spiritObject.IsSpawned)
            {
                continue;
            }

            PlayerSpirit playerSpirit = spiritObject.GetComponent<PlayerSpirit>();

            if (playerSpirit != null && playerSpirit.IsDeadSpectator)
            {
                playerSpirit.SetPhaseControlEnabled(true);
            }
        }

        PrepareFinalDuelParticipant(
            finalDuelSerialKillerClientId
        );

        PrepareFinalDuelParticipant(
            finalDuelMafiaClientId
        );
    }

    private void PrepareFinalDuelParticipant(ulong clientId)
    {
        if (clientId == NoClientId ||
            !TryGetPlayerSpirit(
                clientId,
                out PlayerSpirit playerSpirit))
        {
            return;
        }

        playerSpirit.SetFinalDuelPreparationState();

        SpiritHitReceiver hitReceiver =
            playerSpirit.GetComponent<SpiritHitReceiver>();

        if (hitReceiver != null)
            hitReceiver.ResetHealthForFinalDuel();

        House homeHouse =
            GetHouse(playerSpirit.HomeHouseId);

        if (homeHouse == null ||
            homeHouse.FrontDoorOutsidePoint == null)
        {
            Debug.LogError(
                $"Final Duel spawn point missing - " +
                $"Client: {clientId}, " +
                $"House: {playerSpirit.HomeHouseId}"
            );

            return;
        }

        playerSpirit.TeleportThroughFrontDoor(
            homeHouse.FrontDoorOutsidePoint.position,
            homeHouse.FrontDoorOutsidePoint.rotation,
            -1
        );
    }

    private void StartFinalDuelCombat()
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.FinalDuel ||
            finalDuelCombatStarted)
        {
            return;
        }

        finalDuelCombatStarted = true;

        EnableFinalDuelParticipantControl(
            finalDuelSerialKillerClientId
        );

        EnableFinalDuelParticipantControl(
            finalDuelMafiaClientId
        );

        Debug.Log(
            $"Final Duel Combat Enabled - " +
            $"SerialKiller: {finalDuelSerialKillerClientId}, " +
            $"Mafia: {finalDuelMafiaClientId}"
        );
    }

    private void EnableFinalDuelParticipantControl(
        ulong clientId)
    {
        if (clientId == NoClientId ||
            !TryGetPlayerSpirit(
                clientId,
                out PlayerSpirit playerSpirit))
        {
            return;
        }

        playerSpirit.SetFinalDuelControlEnabled(true);
    }

    private void SetAllSpiritPhaseControl(bool enabled)
    {
        foreach (KeyValuePair<ulong, NetworkObject> pair in spawnedSpirits)
        {
            NetworkObject spiritObject = pair.Value;

            if (spiritObject == null || !spiritObject.IsSpawned)
                continue;

            PlayerSpirit playerSpirit =
                spiritObject.GetComponent<PlayerSpirit>();

            if (playerSpirit == null)
                continue;

            playerSpirit.SetPhaseControlEnabled(enabled);

            SpiritHitReceiver hitReceiver =
                spiritObject.GetComponent<SpiritHitReceiver>();

            if (enabled)
            {
                hitReceiver?.ResetHealthForNewNight();
            }
            else
            {
                hitReceiver?.ClearSuppressionForPhaseEnd();
            }
        }
    }

    private void ReleaseEligibleDeadSpectatorsForMorning()
    {
        if (!IsServer)
            return;

        foreach (KeyValuePair<ulong, NetworkObject> pair in
                 spawnedSpirits)
        {
            if (!playerMatchStates.TryGetValue(
                    pair.Key,
                    out PlayerMatchState state) ||
                state.isAlive)
            {
                continue;
            }

            NetworkObject spiritObject = pair.Value;

            if (spiritObject == null ||
                !spiritObject.IsSpawned)
            {
                continue;
            }

            PlayerSpirit spirit =
                spiritObject.GetComponent<PlayerSpirit>();

            if (spirit != null &&
                spirit.TryReleaseDeadSpectator(
                    currentDay.Value
                ))
            {
                SendSpectatorRoleSnapshot(pair.Key);
            }
        }
    }

    private void ReturnAllSpiritsHomeForMorning()
    {
        foreach (KeyValuePair<ulong, NetworkObject> pair in spawnedSpirits)
        {
            NetworkObject spiritObject = pair.Value;

            if (spiritObject == null || !spiritObject.IsSpawned)
                continue;

            PlayerSpirit playerSpirit = spiritObject.GetComponent<PlayerSpirit>();

            if (playerSpirit == null)
                continue;

            House homeHouse = GetHouse(playerSpirit.HomeHouseId);

            if (homeHouse == null || homeHouse.SpiritPoint == null)
            {
                Debug.LogWarning($"Client {playerSpirit.OwnerClientId}의 아침 귀환 지점을 찾지 못했습니다.");
                continue;
            }

            playerSpirit.ReturnHomeForMorning(homeHouse.SpiritPoint.position, homeHouse.SpiritPoint.rotation);
        }
    }

    private void ResolveDrunkardSleepSelections()
    {
        activeDrunkardSleepHouseByClient.Clear();

        foreach (KeyValuePair<ulong, ulong> selection in drunkardSleepTargets)
        {
            ulong drunkardClientId = selection.Key;
            ulong targetClientId = selection.Value;

            if (!IsAliveRole(drunkardClientId, RoleId.Drunkard) ||
                !IsAlivePlayer(targetClientId) ||
                drunkardClientId == targetClientId)
            {
                continue;
            }

            int targetHouseId = GetOwnedHouseId(targetClientId);
            House targetHouse = GetHouse(targetHouseId);

            if (targetHouse == null ||
                targetHouse.DrunkardSleepPoint == null ||
                targetHouse.FrontDoorOutsidePoint == null)
            {
                Debug.LogWarning(
                    $"Drunkard Sleep Point Missing - " +
                    $"Drunkard: {drunkardClientId}, Target: {targetClientId}, " +
                    $"House: {targetHouseId}"
                );

                continue;
            }

            if (!TryGetPlayerSpirit(drunkardClientId, out PlayerSpirit drunkardSpirit))
            {
                Debug.LogWarning(
                    $"Drunkard Spirit Missing - Drunkard: {drunkardClientId}"
                );

                continue;
            }

            if (!MovePlayerBody(
                    drunkardClientId,
                    targetHouse.DrunkardSleepPoint.position,
                    targetHouse.DrunkardSleepPoint.rotation))
            {
                continue;
            }

            /*
             * 영체는 새로 Spawn하지 않는다.
             * 경기 시작 때 생성된 기존 영체를 선택한 집의 정문 바깥으로 이동한다.
             * CurrentHouseId는 -1로 유지되므로 집 밖 판정이다.
             */
            drunkardSpirit.TeleportThroughFrontDoor(
                targetHouse.FrontDoorOutsidePoint.position,
                targetHouse.FrontDoorOutsidePoint.rotation,
                -1
            );

            activeDrunkardSleepHouseByClient[drunkardClientId] =
                targetHouseId;

            if (showNightActionLogs)
            {
                Debug.Log(
                    $"Drunkard Sleep Resolved - Drunkard: {drunkardClientId}, " +
                    $"Target: {targetClientId}, House: {targetHouseId}, " +
                    $"BodyPoint: {targetHouse.DrunkardSleepPoint.position}, " +
                    $"SpiritPoint: {targetHouse.FrontDoorOutsidePoint.position}"
                );
            }
        }
    }

    private void ReturnDrunkardBodiesHomeForMorning()
    {
        if (activeDrunkardSleepHouseByClient.Count == 0)
            return;

        List<ulong> sleepingDrunkards =
            new List<ulong>(activeDrunkardSleepHouseByClient.Keys);

        for (int i = 0; i < sleepingDrunkards.Count; i++)
        {
            ulong drunkardClientId = sleepingDrunkards[i];

            if (!playerMatchStates.TryGetValue(
                    drunkardClientId,
                    out PlayerMatchState state) ||
                !state.isAlive)
            {
                continue;
            }

            int homeHouseId = GetOwnedHouseId(drunkardClientId);
            House homeHouse = GetHouse(homeHouseId);

            if (homeHouse == null || homeHouse.BodyPoint == null)
                continue;

            MovePlayerBody(
                drunkardClientId,
                homeHouse.BodyPoint.position,
                homeHouse.BodyPoint.rotation
            );
        }

        activeDrunkardSleepHouseByClient.Clear();
    }

    private bool MovePlayerBody(
        ulong clientId,
        Vector3 position,
        Quaternion rotation)
    {
        if (!IsServer ||
            !spawnedBodies.TryGetValue(
                clientId,
                out NetworkObject bodyObject) ||
            bodyObject == null ||
            !bodyObject.IsSpawned)
        {
            return false;
        }

        bodyObject.transform.SetPositionAndRotation(
            position,
            rotation
        );

        SyncPlayerBodyTransformRpc(
            new NetworkObjectReference(bodyObject),
            position,
            rotation
        );

        return true;
    }

    [Rpc(SendTo.Everyone)]
    private void SyncPlayerBodyTransformRpc(
        NetworkObjectReference bodyReference,
        Vector3 position,
        Quaternion rotation)
    {
        if (IsServer)
            return;

        if (!bodyReference.TryGet(out NetworkObject bodyObject) ||
            bodyObject == null)
        {
            return;
        }

        bodyObject.transform.SetPositionAndRotation(
            position,
            rotation
        );
    }


    private void AdvancePhase()
    {
        if (!IsServer)
            return;

        switch (currentPhase.Value)
        {
            case MatchPhase.MorningDiscussion:
                BeginPhase(MatchPhase.MorningVote);
                break;

            case MatchPhase.MorningVote:
                ResolveMorningVote();
                BeginPhase(MatchPhase.MorningVoteResult);
                break;

            case MatchPhase.MorningVoteResult:
                if (!TryBeginPendingGameResult() &&
                    !TryBeginPendingFinalDuel())
                {
                    BeginPhase(MatchPhase.NightPreparation);
                }

                break;

            case MatchPhase.NightPreparation:
                ResolveMafiaKillerVote();
                AssignMafiaInterferenceDuties();
                ResolveDrunkardSleepSelections();
                PrepareCurseDollForNight();
                BeginPhase(MatchPhase.NightAction);
                break;

            case MatchPhase.NightAction:
                ResolveNightActions();
                BeginPhase(MatchPhase.NightResult);
                ResolveHunterRouteResults();
                break;

            case MatchPhase.NightResult:
                if (!TryBeginPendingGameResult() &&
                    !TryBeginPendingFinalDuel())
                {
                    currentDay.Value++;

                    foreach (PlayerMatchState state in
                             playerMatchStates.Values)
                    {
                        SendAbilityFailureChance(
                            state.clientId
                        );
                    }

                    BeginPhase(MatchPhase.MorningVote);
                }
                break;
        }
    }

    private string BuildForensicsResultMessage(ulong victimClientId)
    {
        string victimName = playerMatchStates.TryGetValue(
            victimClientId,
            out PlayerMatchState victimState
        )
            ? victimState.playerName.ToString()
            : $"Player {victimClientId}";

        int intruderCount = forensicsIntruderCountByVictim.TryGetValue(
            victimClientId,
            out int savedIntruderCount
        )
            ? savedIntruderCount
            : 0;

        List<string> damagedPlayerNames = forensicsDamagedPlayerNamesByVictim.TryGetValue(
            victimClientId,
            out List<string> savedDamagedPlayerNames
        )
            ? savedDamagedPlayerNames
            : null;

        StringBuilder builder = new StringBuilder();

        builder.AppendLine("<size=30>감식 결과</size>");
        builder.AppendLine($"<size=40><b>{victimName}</b></size>");
        builder.AppendLine();
        builder.AppendLine($"침입한 사람 수: {intruderCount}명");

        if (damagedPlayerNames == null || damagedPlayerNames.Count == 0)
        {
            builder.AppendLine("집 안에서 피해받은 사람: 없음");
        }
        else
        {
            builder.AppendLine(
                $"집 안에서 피해받은 사람: {string.Join(", ", damagedPlayerNames)}"
            );
        }

        if (!forensicsDeathEvidenceByVictim.TryGetValue(
                victimClientId,
                out NightDeathEvidence deathEvidence))
        {
            builder.AppendLine("사인: 확인할 수 없음");
            return builder.ToString();
        }

        switch (deathEvidence.killMethod)
        {
            case NightKillMethod.Poison:
                builder.AppendLine("<color=#9ADB5B><b>사인: 독살</b></color>");
                break;

            case NightKillMethod.Direct:
                builder.AppendLine("<b>사인: 직접 살해</b>");
                builder.AppendLine(
                    $"살해 당시 범인이 들고 있던 도구: " +
                    $"{GetWeaponDisplayName(deathEvidence.weaponIndex)}"
                );

                break;

            case NightKillMethod.CurseDoll:
                builder.AppendLine(
                    "<color=#B68CFF><b>사인: 저주 인형</b></color>"
                );

                break;

            case NightKillMethod.Exorcism:
                builder.AppendLine(
                    "<color=#77CFEA><b>사인: 영체 소멸</b></color>"
                );

                break;

            default:
                builder.AppendLine("사인: 확인할 수 없음");
                break;
        }

        return builder.ToString();
    }

    private string GetWeaponDisplayName(int weaponIndex)
    {
        switch ((RoleWeaponId)weaponIndex)
        {
            case RoleWeaponId.Pitchfork:
                return "쇠스랑";

            case RoleWeaponId.Broom:
                return "빗자루";

            case RoleWeaponId.Hammer:
                return "망치";

            case RoleWeaponId.Mace:
                return "메이스";

            case RoleWeaponId.LargeSyringe:
                return "대형 주사기";

            case RoleWeaponId.WoodenStaff:
                return "나무 지팡이";

            case RoleWeaponId.Bottle:
                return "술병";

            case RoleWeaponId.Cross:
                return "십자가";

            case RoleWeaponId.Torch:
                return "횃불";

            case RoleWeaponId.MediumStaff:
                return "영매사 지팡이";

            case RoleWeaponId.HuntingAxe:
                return "사냥용 도끼";

            case RoleWeaponId.Shovel:
                return "삽";

            case RoleWeaponId.PryBar:
                return "쇠지렛대";

            default:
                return "알 수 없는 도구";
        }
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void ReceiveForensicsResultRpc(
        FixedString4096Bytes resultMessage,
        RpcParams rpcParams = default)
    {
        LocalForensicsResultReceived?.Invoke(resultMessage.ToString());
    }

    private float GetPhaseDuration(MatchPhase phase)
    {
        switch (phase)
        {
            case MatchPhase.MorningDiscussion:
                return morningDiscussionDuration;

            case MatchPhase.MorningVote:
                return morningVoteDuration;

            case MatchPhase.MorningVoteResult:
                return morningVoteResultDuration;

            case MatchPhase.NightPreparation:
                return nightPreparationDuration;

            case MatchPhase.NightAction:
                return nightActionDuration;

            case MatchPhase.NightResult:
                return nightResultDuration;

            default:
                return 0f;
        }
    }

    public void SubmitBailiffConfineTarget(ulong targetClientId)
    {
        if (!IsClient || currentPhase.Value != MatchPhase.NightAction)
            return;

        SubmitBailiffConfineTargetRpc(targetClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitBailiffConfineTargetRpc(ulong targetClientId, RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong bailiffClientId = rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(bailiffClientId, RoleActionType.BailiffConfine, "현재 밤 행동 페이즈가 아닙니다.");
            return;
        }

        if (!CanUseCitizenAction(
                bailiffClientId,
                RoleId.Bailiff))
        {
            RejectRoleAction(
                bailiffClientId,
                RoleActionType.BailiffConfine,
                "집행관 능력을 사용할 수 없습니다."
            );
            return;
        }

        if (bailiffClientId == targetClientId)
        {
            RejectRoleAction(bailiffClientId, RoleActionType.BailiffConfine, "자기 육체에는 사용할 수 없습니다.");
            return;
        }

        if (!IsAlivePlayer(targetClientId))
        {
            RejectRoleAction(bailiffClientId, RoleActionType.BailiffConfine, "대상이 생존 상태가 아닙니다.");
            return;
        }

        if (completedNightRoleActions.Contains(bailiffClientId))
        {
            RejectRoleAction(bailiffClientId, RoleActionType.BailiffConfine, "이미 이번 밤 직업 행동을 완료했습니다.");
            return;
        }

        if (!CanPlayerStartRoleAction(bailiffClientId))
        {
            RejectRoleAction(bailiffClientId, RoleActionType.BailiffConfine, "행동 불가 또는 감금 상태입니다.");
            return;
        }

        if (!ValidateBodyRoleAction(bailiffClientId, targetClientId, out string rejectionReason))
        {
            RejectRoleAction(bailiffClientId, RoleActionType.BailiffConfine, rejectionReason);
            return;
        }

        if (!TryGetPlayerSpirit(targetClientId, out PlayerSpirit targetSpirit))
        {
            RejectRoleAction(bailiffClientId, RoleActionType.BailiffConfine, "대상의 영체를 찾지 못했습니다.");
            return;
        }

        if (targetSpirit.IsConfinedForNight)
        {
            RejectRoleAction(bailiffClientId, RoleActionType.BailiffConfine, "대상 영체가 이미 감옥에 갇혀 있습니다.");
            return;
        }

        int targetHouseId = GetOwnedHouseId(targetClientId);
        House targetHouse = GetHouse(targetHouseId);

        if (targetHouse == null || targetHouse.InsideCagePoint == null)
        {
            RejectRoleAction(bailiffClientId, RoleActionType.BailiffConfine, "대상 집의 내부 감옥 위치를 찾지 못했습니다.");
            return;
        }

        if (CompletePeddlerFakeActionIfNeeded(
                bailiffClientId,
                RoleActionType.BailiffConfine,
                targetClientId,
                targetHouseId))
        {
            return;
        }

        if (!TryCompleteRealCitizenAction(
                bailiffClientId,
                RoleActionType.BailiffConfine,
                targetClientId,
                targetHouseId))
        {
            return;
        }

        targetSpirit.ConfineInsideForNight(
            targetHouse.InsideCagePoint.position,
            targetHouse.InsideCagePoint.rotation,
            targetHouse.Id
        );

        ReturnCurseDollIfHeld(
            targetClientId,
            "감금"
        );

        AcceptRoleAction(
            bailiffClientId,
            RoleActionType.BailiffConfine,
            targetClientId,
            targetHouseId
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Bailiff Confine Success - Bailiff: {bailiffClientId}, " +
                $"Target: {targetClientId}, House: {targetHouse.Id}"
            );
        }
    }

    public void SubmitExorcismTarget(ulong targetClientId)
    {
        if (!IsClient || currentPhase.Value != MatchPhase.NightAction)
            return;

        SubmitExorcismTargetRpc(targetClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitExorcismTargetRpc(ulong targetClientId, RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong exorcistClientId = rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(exorcistClientId, RoleActionType.Exorcism, "현재 밤 행동 페이즈가 아닙니다.");
            return;
        }

        if (!CanUseCitizenAction(
                exorcistClientId,
                RoleId.Exorcist))
        {
            RejectRoleAction(
                exorcistClientId,
                RoleActionType.Exorcism,
                "퇴마사 능력을 사용할 수 없습니다."
            );
            return;
        }

        bool isActualExorcist =
            playerMatchStates.TryGetValue(
                exorcistClientId,
                out PlayerMatchState exorcistState) &&
            exorcistState.role == RoleId.Exorcist;

        if (exorcistClientId == targetClientId || !IsAlivePlayer(targetClientId))
        {
            RejectRoleAction(exorcistClientId, RoleActionType.Exorcism, "유효한 퇴마 대상이 아닙니다.");
            return;
        }

        if (isActualExorcist &&
            consumedExorcismPlayers.Contains(exorcistClientId))
        {
            RejectRoleAction(exorcistClientId, RoleActionType.Exorcism, "퇴마 능력을 이미 사용했습니다.");
            return;
        }

        if (completedNightRoleActions.Contains(exorcistClientId))
        {
            RejectRoleAction(exorcistClientId, RoleActionType.Exorcism, "이미 이번 밤 직업 행동을 완료했습니다.");
            return;
        }

        if (!CanPlayerStartRoleAction(exorcistClientId))
        {
            RejectRoleAction(exorcistClientId, RoleActionType.Exorcism, "행동 불가 또는 감금 상태입니다.");
            return;
        }

        if (!ValidateSpiritRoleAction(
                exorcistClientId,
                targetClientId,
                out PlayerSpirit targetSpirit,
                out string rejectionReason))
        {
            RejectRoleAction(exorcistClientId, RoleActionType.Exorcism, rejectionReason);
            return;
        }

        /*
         * 집 소유자, 현재 집, 감옥 종류는 검사하지 않는다.
         * 내부 또는 외부 감옥에 갇혀 있기만 하면 퇴마 가능하다.
         */
        if (!targetSpirit.IsConfinedForNight)
        {
            RejectRoleAction(exorcistClientId, RoleActionType.Exorcism, "감옥에 갇힌 영체가 아닙니다.");
            return;
        }

        if (CompletePeddlerFakeActionIfNeeded(
                exorcistClientId,
                RoleActionType.Exorcism,
                targetClientId,
                targetSpirit.ConfinedHouseId))
        {
            return;
        }

        if (!TryCompleteRealCitizenAction(
                exorcistClientId,
                RoleActionType.Exorcism,
                targetClientId,
                targetSpirit.ConfinedHouseId))
        {
            return;
        }

        SpiritInteractionAudio targetInteractionAudio =
            targetSpirit.GetComponent
                <SpiritInteractionAudio>();

        if (targetInteractionAudio != null)
        {
            targetInteractionAudio
                .PlayServerSpatialSound(
                    SpiritInteractionSoundType
                        .SpiritVanish,
                    targetSpirit.transform.position
                );
        }

        AcceptRoleAction(
            exorcistClientId,
            RoleActionType.Exorcism,
            targetClientId,
            targetSpirit.ConfinedHouseId
        );

        SaveForensicsEvidence(
            targetClientId,
            NightKillMethod.Exorcism,
            -1
        );

        EliminatePlayer(targetClientId);

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Exorcism Success - Exorcist: {exorcistClientId}, " +
                $"Target: {targetClientId}, Cage: {targetSpirit.ConfinementType}, " +
                $"House: {targetSpirit.ConfinedHouseId}"
            );
        }
    }

    private bool ValidateSpiritRoleAction(
    ulong actorClientId,
    ulong targetClientId,
    out PlayerSpirit targetSpirit,
    out string rejectionReason)
    {
        targetSpirit = null;
        rejectionReason = string.Empty;

        if (actorClientId == targetClientId)
        {
            rejectionReason = "자기 자신에게 사용할 수 없습니다.";
            return false;
        }

        if (!TryGetPlayerSpirit(actorClientId, out PlayerSpirit actorSpirit))
        {
            rejectionReason = "행동자의 영체를 찾지 못했습니다.";
            return false;
        }

        if (!TryGetPlayerSpirit(targetClientId, out targetSpirit))
        {
            rejectionReason = "대상의 영체를 찾지 못했습니다.";
            return false;
        }

        Collider targetCollider = targetSpirit.GetComponentInChildren<Collider>();
        Vector3 targetPoint = targetCollider != null
            ? targetCollider.ClosestPoint(actorSpirit.transform.position)
            : targetSpirit.transform.position;

        Vector3 actorPosition = actorSpirit.transform.position;

        actorPosition.y = 0f;
        targetPoint.y = 0f;

        float distance = Vector3.Distance(actorPosition, targetPoint);
        float maximumDistance = roleActionDistance + roleActionServerTolerance;

        if (distance > maximumDistance)
        {
            rejectionReason = "대상과의 거리가 너무 멉니다.";
            return false;
        }

        return true;
    }

    private void InitializeMafiaDisguiseOwnership(
        PlayerMatchState state)
    {
        if (!IsServer ||
            ((state.team != RoleTeam.Mafia ||
              state.role == RoleId.Spy) &&
             state.role != RoleId.SerialKiller))
        {
            return;
        }

        int currentWeaponIndex =
            GetCurrentWeaponIndex(
                state.clientId
            );

        RoleWeaponId initialWeapon =
            (RoleWeaponId)currentWeaponIndex;

        if (!IsMafiaDisguiseWeapon(
                initialWeapon))
        {
            initialWeapon =
                GetRandomMafiaDisguiseWeapon();

            SetPlayerWeapon(
                state.clientId,
                initialWeapon
            );
        }

        HashSet<RoleWeaponId> ownedWeapons =
            GetOrCreateOwnedMafiaDisguiseWeapons(
                state.clientId
            );

        ownedWeapons.Add(initialWeapon);

        mafiaDisguiseWeaponByClient[
            state.clientId
        ] = initialWeapon;
    }

    private void PrepareMafiaDisguiseSelections()
    {
        if (!IsServer)
            return;

        foreach (PlayerMatchState state
                 in playerMatchStates.Values)
        {
            if (!state.isAlive ||
                (state.team != RoleTeam.Mafia &&
                 state.role != RoleId.SerialKiller))
            {
                continue;
            }

            if (state.role == RoleId.Spy)
            {
                SendMafiaDisguiseOptions(
                    state.clientId
                );

                continue;
            }

            HashSet<RoleWeaponId> ownedWeapons =
                GetOrCreateOwnedMafiaDisguiseWeapons(
                    state.clientId
                );

            if (ownedWeapons.Count == 0)
            {
                RoleWeaponId initialWeapon =
                    GetRandomMafiaDisguiseWeapon();

                ownedWeapons.Add(initialWeapon);

                mafiaDisguiseWeaponByClient[
                    state.clientId
                ] = initialWeapon;
            }

            RoleWeaponId selectedWeapon =
                GetValidSelectedMafiaDisguiseWeapon(
                    state,
                    ownedWeapons
                );

            if (!SetPlayerWeapon(
                    state.clientId,
                    selectedWeapon))
            {
                Debug.LogWarning(
                    $"마녀 진영 위장 무기 적용 실패 - " +
                    $"Client: {state.clientId}, " +
                    $"Weapon: {selectedWeapon}"
                );

                continue;
            }

            mafiaDisguiseWeaponByClient[
                state.clientId
            ] = selectedWeapon;

            SendMafiaDisguiseOptions(
                state.clientId
            );
        }
    }

    private RoleWeaponId
        GetValidSelectedMafiaDisguiseWeapon(
            PlayerMatchState state,
            HashSet<RoleWeaponId> ownedWeapons)
    {
        if (mafiaDisguiseWeaponByClient
                .TryGetValue(
                    state.clientId,
                    out RoleWeaponId selectedWeapon))
        {
            bool selectedIsValid =
                ownedWeapons.Contains(
                    selectedWeapon
                );

            if (selectedIsValid)
                return selectedWeapon;
        }

        foreach (RoleWeaponId weaponId
                 in mafiaDisguiseWeaponPool)
        {
            if (ownedWeapons.Contains(weaponId))
                return weaponId;
        }

        RoleWeaponId fallbackWeapon =
            GetRandomMafiaDisguiseWeapon();

        ownedWeapons.Add(fallbackWeapon);

        return fallbackWeapon;
    }

    private List<RoleWeaponId>
        BuildMafiaDisguiseOptions(
            PlayerMatchState mafiaState)
    {
        List<RoleWeaponId> options =
            new List<RoleWeaponId>();

        if (!mafiaState.isAlive ||
            ((mafiaState.team != RoleTeam.Mafia ||
              mafiaState.role == RoleId.Spy) &&
             mafiaState.role != RoleId.SerialKiller))
        {
            return options;
        }

        /*
         * 마녀 진영은 첫날 무작위 무기를 사용하고,
         * 둘째 날부터 보유 무기가 2종 이상일 때만
         * 수동 선택할 수 있다.
         */
        if (currentDay.Value < 2)
            return options;

        if (!ownedMafiaDisguiseWeaponsByClient
                .TryGetValue(
                    mafiaState.clientId,
                    out HashSet<RoleWeaponId>
                        ownedWeapons) ||
            ownedWeapons.Count < 2)
        {
            return options;
        }

        /*
         * HashSet 순회 순서에 의존하지 않고,
         * 모든 클라이언트에서 같은 도구 순서를 유지한다.
         */
        for (int i = 0;
             i < mafiaDisguiseWeaponPool.Length;
             i++)
        {
            RoleWeaponId weaponId =
                mafiaDisguiseWeaponPool[i];

            if (ownedWeapons.Contains(weaponId))
                options.Add(weaponId);
        }

        return options;
    }

    private HashSet<RoleWeaponId>
        GetOrCreateOwnedMafiaDisguiseWeapons(
            ulong clientId)
    {
        if (ownedMafiaDisguiseWeaponsByClient
                .TryGetValue(
                    clientId,
                    out HashSet<RoleWeaponId>
                        ownedWeapons))
        {
            return ownedWeapons;
        }

        ownedWeapons =
            new HashSet<RoleWeaponId>();

        ownedMafiaDisguiseWeaponsByClient[
            clientId
        ] = ownedWeapons;

        return ownedWeapons;
    }

    private bool IsMafiaDisguiseWeapon(
        RoleWeaponId weaponId)
    {
        for (int i = 0;
             i < mafiaDisguiseWeaponPool.Length;
             i++)
        {
            if (mafiaDisguiseWeaponPool[i] ==
                weaponId)
            {
                return true;
            }
        }

        return false;
    }

    private RoleWeaponId
        GetRandomMafiaDisguiseWeapon()
    {
        int randomIndex =
            UnityEngine.Random.Range(
                0,
                mafiaDisguiseWeaponPool.Length
            );

        return mafiaDisguiseWeaponPool[
            randomIndex
        ];
    }

    public bool IsCurseDollInstalledAtPoint(
        int houseId,
        CurseDollPointType pointType)
    {
        CurseDollStateData state =
            curseDollState.Value;

        return state.location ==
                   CurseDollLocation.Installed &&
               state.installedHouseId == houseId &&
               state.installedPointType == pointType;
    }

    public bool CanLocalInteractWithCurseDollPoint(
        CurseDollInteractionPoint point)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction ||
            !hasLocalPlayerMatchState ||
            !localPlayerMatchState.isAlive ||
            point == null ||
            point.HouseId < 0 ||
            NetworkManager == null)
        {
            return false;
        }

        CurseDollStateData state =
            curseDollState.Value;

        if (state.location ==
                CurseDollLocation.Installed)
        {
            return state.installedHouseId ==
                       point.HouseId &&
                   state.installedPointType ==
                       point.PointType;
        }

        return state.location ==
                   CurseDollLocation.Held &&
               state.holderClientId ==
                   NetworkManager.LocalClientId;
    }

    public void SubmitInitialCurseDollPlacement(
        ulong targetClientId)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction)
        {
            return;
        }

        SubmitInitialCurseDollPlacementRpc(
            targetClientId
        );
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Everyone
    )]
    private void SubmitInitialCurseDollPlacementRpc(
        ulong targetClientId,
        RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong curseCasterClientId =
            rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction ||
            NetworkManager == null ||
            NetworkManager.ServerTime.Time >=
                phaseEndServerTime.Value)
        {
            RejectRoleAction(
                curseCasterClientId,
                RoleActionType.CurseDoll,
                "밤 행동 시간이 끝나 최초 설치 요청이 거절되었습니다."
            );
            return;
        }

        if (curseCasterClientId !=
                currentNightKillerClientId ||
            !IsAliveRole(
                curseCasterClientId,
                RoleId.CurseCaster))
        {
            RejectRoleAction(
                curseCasterClientId,
                RoleActionType.CurseDoll,
                "이번 밤의 생존한 저주술사 살해 담당자가 아닙니다."
            );
            return;
        }

        CurseDollStateData state =
            curseDollState.Value;

        if (!curseDollInitialPlacementPending ||
            state.location != CurseDollLocation.None)
        {
            RejectRoleAction(
                curseCasterClientId,
                RoleActionType.CurseDoll,
                "최초 설치가 이미 처리되었거나 인형 상태가 변경되었습니다."
            );
            return;
        }

        if (completedNightRoleActions.Contains(
                curseCasterClientId))
        {
            RejectRoleAction(
                curseCasterClientId,
                RoleActionType.CurseDoll,
                "이미 이번 밤 직업 행동을 완료했습니다."
            );
            return;
        }

        if (!CanPlayerStartRoleAction(
                curseCasterClientId))
        {
            RejectRoleAction(
                curseCasterClientId,
                RoleActionType.CurseDoll,
                "행동 불가 또는 감금 상태입니다."
            );
            return;
        }

        if (targetClientId == curseCasterClientId ||
            !IsAlivePlayer(targetClientId))
        {
            RejectRoleAction(
                curseCasterClientId,
                RoleActionType.CurseDoll,
                "다른 생존 플레이어의 육체를 선택해야 합니다."
            );
            return;
        }

        if (!ValidateBodyRoleAction(
                curseCasterClientId,
                targetClientId,
                out string rejectionReason))
        {
            RejectRoleAction(
                curseCasterClientId,
                RoleActionType.CurseDoll,
                rejectionReason
            );
            return;
        }

        CurseDollPointType pointType =
            CurseDollPointType.Home;

        int houseId;

        if (activeDrunkardSleepHouseByClient
            .TryGetValue(
                targetClientId,
                out int sleepHouseId))
        {
            houseId = sleepHouseId;
            pointType = CurseDollPointType.Drunkard;
        }
        else
        {
            houseId = GetOwnedHouseId(
                targetClientId
            );
        }

        House house = GetHouse(houseId);

        if (house == null ||
            house.GetCurseDollPoint(pointType) == null ||
            !TryGetCurseDollInstallationOwner(
                curseCasterClientId,
                house,
                pointType,
                out ulong installedOwnerClientId,
                out rejectionReason) ||
            installedOwnerClientId != targetClientId)
        {
            if (string.IsNullOrEmpty(rejectionReason))
            {
                rejectionReason =
                    "대상의 현재 저주 인형 설치 포인트를 찾지 못했습니다.";
            }

            RejectRoleAction(
                curseCasterClientId,
                RoleActionType.CurseDoll,
                rejectionReason
            );
            return;
        }

        state.location = CurseDollLocation.Installed;
        state.holderClientId = NoClientId;
        state.installedHouseId = houseId;
        state.installedPointType = pointType;
        state.installedOwnerClientId =
            installedOwnerClientId;
        curseDollState.Value = state;

        CommitInitialCurseDollPlacementIfNeeded(
            curseCasterClientId
        );

        SendPrivateNotification(
            curseCasterClientId,
            "저주 인형을 설치했습니다."
        );
    }

    public bool TryInteractCurseDollServer(
        ulong actorClientId,
        int houseId,
        CurseDollPointType pointType,
        float maximumDistance)
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.NightAction ||
            NetworkManager == null ||
            NetworkManager.ServerTime.Time >=
                phaseEndServerTime.Value)
        {
            SendPrivateNotification(
                actorClientId,
                "밤 행동 시간이 끝나 저주 인형 요청이 거절되었습니다."
            );

            return false;
        }

        if (!IsAlivePlayer(actorClientId) ||
            !CanPlayerStartRoleAction(actorClientId))
        {
            SendPrivateNotification(
                actorClientId,
                "현재 상태에서는 저주 인형을 다룰 수 없습니다."
            );

            return false;
        }

        if (pointType != CurseDollPointType.Home &&
            pointType != CurseDollPointType.Drunkard)
        {
            return false;
        }

        House house = GetHouse(houseId);
        CurseDollInteractionPoint point =
            house != null
                ? house.GetCurseDollPoint(pointType)
                : null;

        if (point == null ||
            !TryGetPlayerSpirit(
                actorClientId,
                out PlayerSpirit actorSpirit) ||
            !point.IsNear(
                actorSpirit.transform.position,
                maximumDistance))
        {
            return false;
        }

        CurseDollStateData state =
            curseDollState.Value;

        if (state.location ==
                CurseDollLocation.Installed &&
            state.installedHouseId == houseId &&
            state.installedPointType == pointType)
        {
            ulong previousInstalledOwnerClientId =
                state.installedOwnerClientId;

            state.location = CurseDollLocation.Held;
            state.holderClientId = actorClientId;
            state.installedHouseId = -1;
            state.installedOwnerClientId = NoClientId;
            curseDollState.Value = state;

            SendSpecialPersonalActionRecord(
                actorClientId,
                PersonalActionRecordKind.CurseDollPickup,
                previousInstalledOwnerClientId,
                houseId
            );

            SendPrivateNotification(
                actorClientId,
                "저주 인형을 집어 들었습니다. 기절·감금 시 인형이 집으로 다시 돌아갑니다."
            );

            return true;
        }

        if (state.location != CurseDollLocation.Held ||
            state.holderClientId != actorClientId)
        {
            return false;
        }

        if (!TryGetCurseDollInstallationOwner(
                actorClientId,
                house,
                pointType,
                out ulong installedOwnerClientId,
                out _))
        {
            return false;
        }

        state.location = CurseDollLocation.Installed;
        state.holderClientId = NoClientId;
        state.installedHouseId = houseId;
        state.installedPointType = pointType;
        state.installedOwnerClientId =
            installedOwnerClientId;
        curseDollState.Value = state;

        SendSpecialPersonalActionRecord(
            actorClientId,
            PersonalActionRecordKind.CurseDollInstall,
            installedOwnerClientId,
            houseId
        );

        CommitInitialCurseDollPlacementIfNeeded(
            actorClientId
        );

        SendPrivateNotification(
            actorClientId,
            "저주 인형을 설치했습니다."
        );

        return true;
    }

    private bool TryGetCurseDollInstallationOwner(
        ulong actorClientId,
        House house,
        CurseDollPointType pointType,
        out ulong installedOwnerClientId,
        out string rejectionReason)
    {
        installedOwnerClientId = NoClientId;
        rejectionReason = string.Empty;

        if (house == null)
        {
            rejectionReason =
                "대상 집을 찾지 못했습니다.";

            return false;
        }

        if (pointType == CurseDollPointType.Home)
        {
            installedOwnerClientId =
                house.OwnerClientId;

            if (installedOwnerClientId ==
                    House.NoOwnerClientId ||
                !IsAlivePlayer(
                    installedOwnerClientId))
            {
                rejectionReason =
                    "집주인이 사망했거나 없는 집에는 인형을 설치할 수 없습니다.";

                return false;
            }

            if (activeDrunkardSleepHouseByClient
                .ContainsKey(installedOwnerClientId))
            {
                rejectionReason =
                    "주정뱅이가 집을 비운 동안에는 그 집에 인형을 설치할 수 없습니다.";

                return false;
            }
        }
        else
        {
            foreach (KeyValuePair<ulong, int> sleep in
                     activeDrunkardSleepHouseByClient)
            {
                if (sleep.Value != house.Id ||
                    !IsAliveRole(
                        sleep.Key,
                        RoleId.Drunkard))
                {
                    continue;
                }

                installedOwnerClientId = sleep.Key;
                break;
            }

            if (installedOwnerClientId == NoClientId)
            {
                rejectionReason =
                    "이 위치에는 현재 잠든 주정뱅이가 없습니다.";

                return false;
            }
        }

        if (curseDollInitialPlacementPending &&
            installedOwnerClientId == actorClientId)
        {
            rejectionReason =
                "저주술사의 최초 설치는 다른 생존자의 위치에 해야 합니다.";

            return false;
        }

        return true;
    }

    private void PrepareCurseDollForNight()
    {
        if (!IsServer ||
            currentNightKillerClientId == NoClientId ||
            !playerMatchStates.TryGetValue(
                currentNightKillerClientId,
                out PlayerMatchState killerState) ||
            !killerState.isAlive ||
            killerState.role != RoleId.CurseCaster)
        {
            return;
        }

        if (curseDollState.Value.location !=
            CurseDollLocation.None)
        {
            completedNightRoleActions.Add(
                currentNightKillerClientId
            );

            return;
        }

        curseDollInitialPlacementPending = true;

        curseDollState.Value =
            new CurseDollStateData
            {
                location = CurseDollLocation.None,
                holderClientId = NoClientId,
                installedHouseId = -1,
                installedPointType =
                    CurseDollPointType.Home,
                installedOwnerClientId =
                    NoClientId
            };

    }

    private void CommitInitialCurseDollPlacementIfNeeded(
        ulong holderClientId)
    {
        if (!curseDollInitialPlacementPending)
            return;

        curseDollInitialPlacementPending = false;
        currentNightKillMethod =
            NightKillMethod.CurseDoll;

        completedNightRoleActions.Add(
            holderClientId
        );

        CurseDollStateData state =
            curseDollState.Value;

        AcceptRoleAction(
            holderClientId,
            RoleActionType.CurseDoll,
            state.installedOwnerClientId,
            state.installedHouseId
        );
    }

    private void ValidateCurseDollHolderState()
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.NightAction)
        {
            return;
        }

        CurseDollStateData state =
            curseDollState.Value;

        if (state.location != CurseDollLocation.Held)
            return;

        ulong holderClientId =
            state.holderClientId;

        PlayerSpirit holderSpirit = null;

        if (!IsAlivePlayer(holderClientId) ||
            !TryGetPlayerSpirit(
                holderClientId,
                out holderSpirit))
        {
            ResetCurseDollForInvalidatedPlayer(
                holderClientId,
                "사망 또는 연결 해제"
            );

            return;
        }

        if (holderSpirit.IsConfinedForNight)
        {
            ReturnCurseDollIfHeld(
                holderClientId,
                "감금"
            );
        }
    }

    public bool IsCurseDollHeldBy(
        ulong clientId)
    {
        CurseDollStateData state =
            curseDollState.Value;

        return state.location ==
                   CurseDollLocation.Held &&
               state.holderClientId == clientId;
    }

    public void ReturnCurseDollAfterOutsideHits(
        ulong holderClientId)
    {
        ReturnCurseDollIfHeld(
            holderClientId,
            "집 밖에서 3회 피격",
            "집 밖에서 3회 피격되어 저주 인형이 당신의 집으로 돌아갔습니다."
        );
    }

    public void ReturnCurseDollAfterSuppression(
        ulong holderClientId)
    {
        ReturnCurseDollIfHeld(
            holderClientId,
            "기절"
        );
    }

    private void ReturnCurseDollIfHeld(
        ulong holderClientId,
        string reason,
        string notificationMessage = null)
    {
        if (!IsServer)
            return;

        CurseDollStateData state =
            curseDollState.Value;

        if (state.location != CurseDollLocation.Held ||
            state.holderClientId != holderClientId)
        {
            return;
        }

        CurseDollPointType returnPointType =
            CurseDollPointType.Home;

        int returnHouseId;

        if (activeDrunkardSleepHouseByClient
            .TryGetValue(
                holderClientId,
                out int sleepHouseId))
        {
            returnHouseId = sleepHouseId;
            returnPointType =
                CurseDollPointType.Drunkard;
        }
        else
        {
            returnHouseId =
                GetOwnedHouseId(holderClientId);
        }

        if (returnHouseId < 0)
        {
            Debug.LogError(
                $"Curse Doll Return Point Missing - Holder: {holderClientId}, Reason: {reason}"
            );

            ResetCurseDollState();
            return;
        }

        state.location =
            CurseDollLocation.Installed;
        state.holderClientId = NoClientId;
        state.installedHouseId = returnHouseId;
        state.installedPointType =
            returnPointType;
        state.installedOwnerClientId =
            holderClientId;
        curseDollState.Value = state;

        CommitInitialCurseDollPlacementIfNeeded(
            holderClientId
        );

        if (!string.IsNullOrWhiteSpace(
                notificationMessage) &&
            NetworkManager != null &&
            NetworkManager.ConnectedClients
                .ContainsKey(holderClientId))
        {
            SendPrivateNotification(
                holderClientId,
                notificationMessage
            );
        }
    }

    private void ResetCurseDollForInvalidatedPlayer(
        ulong clientId,
        string reason)
    {
        if (!IsServer)
            return;

        CurseDollStateData state =
            curseDollState.Value;

        bool heldByPlayer =
            state.location == CurseDollLocation.Held &&
            state.holderClientId == clientId;

        bool installedForPlayer =
            state.location == CurseDollLocation.Installed &&
            state.installedOwnerClientId == clientId;

        bool pendingInitialPlacementByPlayer =
            curseDollInitialPlacementPending &&
            currentNightKillerClientId == clientId;

        if (!heldByPlayer &&
            !installedForPlayer &&
            !pendingInitialPlacementByPlayer)
            return;

        ResetCurseDollState();

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Curse Doll Removed - Client: {clientId}, " +
                $"Reason: {reason}, PreviousLocation: {state.location}"
            );
        }
    }

    private void ResetCurseDollState()
    {
        curseDollInitialPlacementPending = false;

        if (!IsServer)
            return;

        curseDollState.Value =
            new CurseDollStateData
            {
                location = CurseDollLocation.None,
                holderClientId = NoClientId,
                installedHouseId = -1,
                installedPointType =
                    CurseDollPointType.Home,
                installedOwnerClientId =
                    NoClientId
            };
    }

    public bool CanLocalPickupDroppedRoleTool(
        DroppedRoleTool droppedRoleTool)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction ||
            !hasLocalPlayerMatchState ||
            !localPlayerMatchState.isAlive ||
            droppedRoleTool == null ||
            !droppedRoleTool.IsSpawned)
        {
            return false;
        }

        if (localPlayerMatchState.role == RoleId.Thief)
        {
            return droppedRoleTool.HasSourceRoleData &&
                   droppedRoleTool.PreviousOwnerClientId !=
                       localPlayerMatchState.clientId;
        }

        if (((localPlayerMatchState.team != RoleTeam.Mafia ||
              localPlayerMatchState.role == RoleId.Spy) &&
             localPlayerMatchState.role != RoleId.SerialKiller) ||
            !droppedRoleTool.TryGetWeaponId(out RoleWeaponId weaponId))
        {
            return false;
        }

        return IsMafiaDisguiseWeapon(weaponId);
    }

    public bool CanLocalPickupMafiaDisguiseTool(
        DroppedRoleTool droppedRoleTool)
    {
        return CanLocalPickupDroppedRoleTool(droppedRoleTool);
    }

    public bool TryPickupDroppedRoleToolServer(
        ulong clientId,
        ulong droppedToolNetworkObjectId,
        float maximumDistance)
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.NightAction ||
            NetworkManager == null ||
            NetworkManager.SpawnManager == null)
        {
            return false;
        }

        if (!playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state) ||
            !state.isAlive)
        {
            return false;
        }

        bool isThief =
            state.role == RoleId.Thief &&
            state.team == RoleTeam.Neutral;

        bool isMafiaCollector =
            (state.team == RoleTeam.Mafia &&
             state.role != RoleId.Spy) ||
            state.role == RoleId.SerialKiller;

        if (!isThief && !isMafiaCollector)
            return false;

        if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(
                droppedToolNetworkObjectId,
                out NetworkObject targetNetworkObject) ||
            targetNetworkObject == null ||
            !targetNetworkObject.IsSpawned ||
            !spawnedDroppedRoleTools.Contains(targetNetworkObject))
        {
            return false;
        }

        DroppedRoleTool droppedRoleTool =
            targetNetworkObject.GetComponent<DroppedRoleTool>();

        if (droppedRoleTool == null ||
            !droppedRoleTool.TryGetWeaponId(out RoleWeaponId weaponId))
        {
            return false;
        }

        if (!TryGetPlayerSpirit(clientId, out PlayerSpirit playerSpirit) ||
            playerSpirit == null)
        {
            return false;
        }

        Vector3 actorPosition = playerSpirit.transform.position;
        Vector3 toolPosition = targetNetworkObject.transform.position;

        actorPosition.y = 0f;
        toolPosition.y = 0f;

        float safeMaximumDistance = Mathf.Max(0.1f, maximumDistance);

        if ((actorPosition - toolPosition).sqrMagnitude >
            safeMaximumDistance * safeMaximumDistance)
        {
            return false;
        }

        if (isThief)
        {
            return TryInheritDroppedRoleAsThief(
                clientId,
                state,
                droppedRoleTool,
                targetNetworkObject,
                weaponId
            );
        }

        if (!IsMafiaDisguiseWeapon(weaponId))
            return false;

        HashSet<RoleWeaponId> ownedWeapons =
            GetOrCreateOwnedMafiaDisguiseWeapons(clientId);

        bool addedNewWeapon = ownedWeapons.Add(weaponId);

        spawnedDroppedRoleTools.Remove(targetNetworkObject);
        targetNetworkObject.Despawn(true);

        string weaponName = GetWeaponDisplayName((int)weaponId);

        SendPrivateNotification(
            clientId,
            addedNewWeapon
                ? $"위장 도구를 획득했습니다: {weaponName}"
                : $"이미 보유한 위장 도구를 회수했습니다: {weaponName}"
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Mafia Disguise Tool Picked Up - " +
                $"Client: {clientId}, Weapon: {weaponId}, New: {addedNewWeapon}"
            );
        }

        return true;
    }

    public bool TryPickupMafiaDisguiseToolServer(
        ulong clientId,
        ulong droppedToolNetworkObjectId,
        float maximumDistance)
    {
        return TryPickupDroppedRoleToolServer(
            clientId,
            droppedToolNetworkObjectId,
            maximumDistance
        );
    }

    private bool TryInheritDroppedRoleAsThief(
        ulong thiefClientId,
        PlayerMatchState thiefState,
        DroppedRoleTool droppedRoleTool,
        NetworkObject targetNetworkObject,
        RoleWeaponId droppedWeapon)
    {
        if (!IsServer ||
            thiefState.role != RoleId.Thief ||
            thiefState.team != RoleTeam.Neutral ||
            droppedRoleTool == null ||
            targetNetworkObject == null ||
            droppedRoleTool.PreviousOwnerClientId == thiefClientId ||
            !droppedRoleTool.TryGetSourceRoleData(
                out RoleId sourceRole,
                out RoleTeam sourceTeam))
        {
            return false;
        }

        if (GetRoleTeam(sourceRole) != sourceTeam)
            return false;

        RoleId inheritedRole = sourceRole;
        RoleTeam inheritedTeam = sourceTeam;

        bool sourceExorcismConsumed =
            sourceRole == RoleId.Exorcist &&
            consumedExorcismPlayers.Contains(
                droppedRoleTool.PreviousOwnerClientId
            );

        if (!SetPlayerWeapon(thiefClientId, droppedWeapon))
            return false;

        EndMediumCommunicationForClient(thiefClientId);

        completedNightRoleActions.Add(thiefClientId);
        doctorProtectTargets.Remove(thiefClientId);
        drunkardSleepTargets.Remove(thiefClientId);
        activeDrunkardSleepHouseByClient.Remove(thiefClientId);
        hunterRouteRecordsByHunter.Remove(thiefClientId);
        spyCitizenAbilityUseCountByClient.Remove(thiefClientId);
        peddlerRealActionByClient.Remove(thiefClientId);
        mafiaDisguiseWeaponByClient.Remove(thiefClientId);
        ownedMafiaDisguiseWeaponsByClient.Remove(thiefClientId);
        consumedExorcismPlayers.Remove(thiefClientId);

        if (inheritedRole == RoleId.Exorcist &&
            sourceExorcismConsumed)
        {
            consumedExorcismPlayers.Add(thiefClientId);
        }

        thiefState.role = inheritedRole;
        thiefState.team = inheritedTeam;
        playerMatchStates[thiefClientId] = thiefState;

        if (inheritedTeam == RoleTeam.Mafia ||
            inheritedRole == RoleId.SerialKiller)
            InitializeMafiaDisguiseOwnership(thiefState);

        ulong sourceOwnerClientId =
            droppedRoleTool.PreviousOwnerClientId;

        spawnedDroppedRoleTools.Remove(targetNetworkObject);
        targetNetworkObject.Despawn(true);

        SendLocalMatchState(thiefState);
        SendSpectatorRoleSnapshotsToDeadPlayers();

        if (inheritedTeam == RoleTeam.Mafia)
        {
            SendMafiaRosterToPlayers();
            SendCurrentNightKillerToMafia();
        }

        SendPrivateNotification(
            thiefClientId,
            "사망자의 도구를 훔쳐 직업과 진영을 그대로 계승했습니다. " +
            "새 역할 행동은 다음 밤부터 사용할 수 있습니다."
        );

        SendPersonalActionRecord(
            thiefClientId,
            PersonalActionRecordKind.ThiefInheritance,
            RoleId.Thief,
            RoleActionType.None,
            sourceOwnerClientId,
            GetOwnedHouseId(sourceOwnerClientId),
            inheritedRole,
            inheritedTeam,
            true,
            false,
            0f,
            0f,
            0f
        );

        pendingWinner = MatchWinner.None;
        pendingIndividualWinnerClientId = NoClientId;
        pendingFinalDuel = false;
        pendingFinalDuelSerialKillerClientId = NoClientId;
        pendingFinalDuelMafiaClientId = NoClientId;
        EvaluateVictory();

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Thief Role Inherited - Thief: {thiefClientId}, " +
                $"SourceOwner: {sourceOwnerClientId}, " +
                $"SourceRole: {sourceRole}, SourceTeam: {sourceTeam}, " +
                $"InheritedRole: {inheritedRole}, InheritedTeam: {inheritedTeam}, " +
                $"Weapon: {droppedWeapon}"
            );
        }

        return true;
    }

    private bool TryGetFixedCitizenWeapon(
        RoleId role,
        out RoleWeaponId weaponId)
    {
        weaponId = RoleWeaponId.None;

        switch (role)
        {
            case RoleId.Farmer: weaponId = RoleWeaponId.Pitchfork; return true;
            case RoleId.Cleaner: weaponId = RoleWeaponId.Broom; return true;
            case RoleId.Blacksmith: weaponId = RoleWeaponId.Hammer; return true;
            case RoleId.Bailiff: weaponId = RoleWeaponId.Mace; return true;
            case RoleId.Doctor: weaponId = RoleWeaponId.LargeSyringe; return true;
            case RoleId.Detective: weaponId = RoleWeaponId.WoodenStaff; return true;
            case RoleId.Drunkard: weaponId = RoleWeaponId.Bottle; return true;
            case RoleId.Exorcist: weaponId = RoleWeaponId.Cross; return true;
            case RoleId.Forensics: weaponId = RoleWeaponId.Torch; return true;
            case RoleId.Medium: weaponId = RoleWeaponId.MediumStaff; return true;
            case RoleId.Hunter: weaponId = RoleWeaponId.HuntingAxe; return true;
            case RoleId.Undertaker: weaponId = RoleWeaponId.Shovel; return true;
            default: return false;
        }
    }

    public void SubmitMafiaDisguiseWeapon(
        RoleWeaponId weaponId)
    {
        if (!IsClient ||
            currentPhase.Value !=
                MatchPhase.NightPreparation)
        {
            return;
        }

        SubmitMafiaDisguiseWeaponRpc(
            (sbyte)weaponId
        );
    }

    [Rpc(
        SendTo.Server,
        InvokePermission =
            RpcInvokePermission.Everyone
    )]
    private void SubmitMafiaDisguiseWeaponRpc(
        sbyte weaponValue,
        RpcParams rpcParams = default)
    {
        if (!IsServer ||
            currentPhase.Value !=
                MatchPhase.NightPreparation)
        {
            return;
        }

        ulong clientId =
            rpcParams.Receive.SenderClientId;

        if (!playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state) ||
            !state.isAlive ||
            ((state.team != RoleTeam.Mafia ||
              state.role == RoleId.Spy) &&
             state.role != RoleId.SerialKiller))
        {
            return;
        }

        RoleWeaponId weaponId =
            (RoleWeaponId)weaponValue;

        List<RoleWeaponId> options =
            BuildMafiaDisguiseOptions(state);

        if (!options.Contains(weaponId))
            return;

        ApplyMafiaDisguiseWeapon(
            clientId,
            weaponId
        );
    }

    private void ApplyMafiaDisguiseWeapon(
        ulong clientId,
        RoleWeaponId weaponId)
    {
        if (!IsServer ||
            !IsMafiaDisguiseWeapon(weaponId) ||
            !SetPlayerWeapon(
                clientId,
                weaponId))
        {
            return;
        }

        mafiaDisguiseWeaponByClient[
            clientId
        ] = weaponId;

        SendMafiaDisguiseOptions(
            clientId
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Mafia Disguise Selected - " +
                $"Client: {clientId}, " +
                $"Weapon: {weaponId}"
            );
        }
    }

    private void SendMafiaDisguiseOptions(
        ulong clientId)
    {
        if (!IsServer ||
            !playerMatchStates.TryGetValue(
                clientId,
            out PlayerMatchState state) ||
            (state.team != RoleTeam.Mafia &&
             state.role != RoleId.SerialKiller))
        {
            return;
        }

        List<RoleWeaponId> options =
            BuildMafiaDisguiseOptions(state);

        bool canSelect =
            options.Count >= 2;

        StringBuilder builder =
            new StringBuilder();

        for (int i = 0;
             i < options.Count;
             i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append(
                (sbyte)options[i]
            );
        }

        RoleWeaponId selected =
            mafiaDisguiseWeaponByClient
                .TryGetValue(
                    clientId,
                    out RoleWeaponId saved)
                ? saved
                : RoleWeaponId.None;

        ReceiveMafiaDisguiseOptionsRpc(
            new FixedString128Bytes(
                builder.ToString()
            ),
            (sbyte)selected,
            canSelect,
            RpcTarget.Single(
                clientId,
                RpcTargetUse.Temp
            )
        );
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission =
            RpcInvokePermission.Server
    )]
    private void ReceiveMafiaDisguiseOptionsRpc(
        FixedString128Bytes optionValues,
        sbyte selectedWeaponValue,
        bool canSelect,
        RpcParams rpcParams = default)
    {
        localMafiaDisguiseOptions.Clear();

        string optionText =
            optionValues.ToString();

        if (!string.IsNullOrWhiteSpace(
                optionText))
        {
            string[] values =
                optionText.Split(',');

            for (int i = 0;
                 i < values.Length;
                 i++)
            {
                if (!sbyte.TryParse(
                        values[i],
                        out sbyte parsed))
                {
                    continue;
                }

                localMafiaDisguiseOptions.Add(
                    (RoleWeaponId)parsed
                );
            }
        }

        localMafiaDisguiseWeapon =
            (RoleWeaponId)
                selectedWeaponValue;

        localCanSelectMafiaDisguise =
            canSelect;

        LocalMafiaDisguiseOptionsChanged
            ?.Invoke();
    }

    public void SubmitMafiaKillerVote(ulong candidateClientId)
    {
        if (!IsClient || currentPhase.Value != MatchPhase.NightPreparation)
            return;

        SubmitMafiaKillerVoteRpc(candidateClientId);
    }

    public void ClearMafiaKillerVote()
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightPreparation)
        {
            return;
        }

        ClearMafiaKillerVoteRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ClearMafiaKillerVoteRpc(
        RpcParams rpcParams = default)
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.NightPreparation)
        {
            return;
        }

        ulong voterClientId =
            rpcParams.Receive.SenderClientId;

        if (!IsAliveMafia(voterClientId))
            return;

        bool removed =
            mafiaKillerVotes.Remove(voterClientId);

        if (removed)
            SendMafiaKillerVoteSnapshotToMafia();

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Mafia Killer Vote Cleared - " +
                $"Voter: {voterClientId}"
            );
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitMafiaKillerVoteRpc(ulong candidateClientId, RpcParams rpcParams = default)
    {
        if (!IsServer || currentPhase.Value != MatchPhase.NightPreparation)
            return;

        ulong voterClientId = rpcParams.Receive.SenderClientId;

        if (!IsAliveMafia(voterClientId) || !IsAliveMafia(candidateClientId))
            return;

        mafiaKillerVotes[voterClientId] = candidateClientId;
        SendMafiaKillerVoteSnapshotToMafia();

        if (showNightActionLogs)
            Debug.Log($"Mafia Killer Vote - Voter: {voterClientId}, Candidate: {candidateClientId}");
    }

    public void SubmitDrunkardSleepTarget(ulong targetClientId)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightPreparation)
        {
            return;
        }

        SubmitDrunkardSleepTargetRpc(targetClientId);
    }

    public void ClearDrunkardSleepTarget()
    {
        SubmitDrunkardSleepTarget(NoClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitDrunkardSleepTargetRpc(
        ulong targetClientId,
        RpcParams rpcParams = default)
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.NightPreparation)
        {
            return;
        }

        ulong drunkardClientId =
            rpcParams.Receive.SenderClientId;

        if (!IsAliveRole(
                drunkardClientId,
                RoleId.Drunkard))
        {
            return;
        }

        if (targetClientId == NoClientId)
        {
            drunkardSleepTargets.Remove(
                drunkardClientId
            );

            ReceiveDrunkardSleepTargetRpc(
                NoClientId,
                RpcTarget.Single(
                    drunkardClientId,
                    RpcTargetUse.Temp
                )
            );

            return;
        }

        if (targetClientId == drunkardClientId ||
            !IsAlivePlayer(targetClientId))
        {
            return;
        }

        int targetHouseId =
            GetOwnedHouseId(targetClientId);

        House targetHouse =
            GetHouse(targetHouseId);

        if (targetHouse == null ||
            targetHouse.DrunkardSleepPoint == null)
        {
            return;
        }

        drunkardSleepTargets[drunkardClientId] =
            targetClientId;

        SendSpecialPersonalActionRecord(
            drunkardClientId,
            PersonalActionRecordKind.DrunkardSleepSelection,
            targetClientId,
            targetHouseId
        );

        ReceiveDrunkardSleepTargetRpc(
            targetClientId,
            RpcTarget.Single(
                drunkardClientId,
                RpcTargetUse.Temp
            )
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Drunkard Sleep Target - " +
                $"Drunkard: {drunkardClientId}, " +
                $"Target: {targetClientId}, " +
                $"House: {targetHouseId}"
            );
        }
    }

    private void ResetDrunkardSleepSelectionsForClients()
    {
        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (!state.isAlive ||
                state.role != RoleId.Drunkard)
            {
                continue;
            }

            ReceiveDrunkardSleepTargetRpc(
                NoClientId,
                RpcTarget.Single(
                    state.clientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveDrunkardSleepTargetRpc(
        ulong targetClientId,
        RpcParams rpcParams = default)
    {
        localDrunkardSleepTargetClientId =
            targetClientId;

        LocalDrunkardSleepTargetChanged?.Invoke(
            targetClientId
        );
    }


    public int GetLocalMafiaKillerVoteCount(
        ulong candidateClientId)
    {
        int voteCount = 0;

        foreach (KeyValuePair<ulong, ulong> vote in
                 localMafiaKillerVotes)
        {
            if (vote.Value == candidateClientId)
                voteCount++;
        }

        return voteCount;
    }

    public string GetLocalMafiaKillerVoterNames(
        ulong candidateClientId)
    {
        StringBuilder names = new StringBuilder();

        foreach (KeyValuePair<ulong, ulong> vote in
                 localMafiaKillerVotes)
        {
            if (vote.Value != candidateClientId)
                continue;

            if (!TryGetPublicPlayerName(
                    vote.Key,
                    out string voterName) ||
                string.IsNullOrWhiteSpace(voterName))
            {
                voterName = $"Player {vote.Key}";
            }

            if (names.Length > 0)
                names.Append(", ");

            names.Append(voterName);
        }

        return names.ToString();
    }

    private void SendMafiaKillerVoteSnapshotToMafia()
    {
        if (!IsServer || NetworkManager == null)
            return;

        StringBuilder snapshotBuilder =
            new StringBuilder();

        foreach (KeyValuePair<ulong, ulong> vote in
                 mafiaKillerVotes)
        {
            if (snapshotBuilder.Length > 0)
                snapshotBuilder.Append(';');

            snapshotBuilder
                .Append(vote.Key)
                .Append(',')
                .Append(vote.Value);
        }

        FixedString512Bytes snapshot =
            new FixedString512Bytes(
                snapshotBuilder.ToString()
            );

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (!state.isAlive ||
                state.team != RoleTeam.Mafia ||
                !NetworkManager.ConnectedClients
                    .ContainsKey(state.clientId))
            {
                continue;
            }

            ReceiveMafiaKillerVoteSnapshotRpc(
                snapshot,
                RpcTarget.Single(
                    state.clientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveMafiaKillerVoteSnapshotRpc(
        FixedString512Bytes snapshot,
        RpcParams rpcParams = default)
    {
        localMafiaKillerVotes.Clear();

        string value = snapshot.ToString();

        if (!string.IsNullOrWhiteSpace(value))
        {
            string[] votes = value.Split(';');

            for (int i = 0; i < votes.Length; i++)
            {
                string[] pair = votes[i].Split(',');

                if (pair.Length != 2 ||
                    !ulong.TryParse(
                        pair[0],
                        out ulong voterClientId) ||
                    !ulong.TryParse(
                        pair[1],
                        out ulong candidateClientId))
                {
                    continue;
                }

                localMafiaKillerVotes[
                    voterClientId
                ] = candidateClientId;
            }
        }

        LocalMafiaKillerVotesChanged?.Invoke();
    }

    private void ResolveMafiaKillerVote()
    {
        List<ulong> aliveMafiaMembers = GetAliveMafiaClientIds();

        if (aliveMafiaMembers.Count == 0)
        {
            currentNightKillerClientId = NoClientId;
            SendCurrentNightKillerToMafia();
            return;
        }

        Dictionary<ulong, int> voteCounts = new Dictionary<ulong, int>();

        foreach (KeyValuePair<ulong, ulong> vote in mafiaKillerVotes)
        {
            if (!IsAliveMafia(vote.Key) || !IsAliveMafia(vote.Value))
                continue;

            if (!voteCounts.ContainsKey(vote.Value))
                voteCounts[vote.Value] = 0;

            voteCounts[vote.Value]++;
        }

        List<ulong> highestCandidates = new List<ulong>();
        int highestVoteCount = 0;

        foreach (KeyValuePair<ulong, int> voteCount in voteCounts)
        {
            if (voteCount.Value > highestVoteCount)
            {
                highestVoteCount = voteCount.Value;
                highestCandidates.Clear();
                highestCandidates.Add(voteCount.Key);
                continue;
            }

            if (voteCount.Value == highestVoteCount)
                highestCandidates.Add(voteCount.Key);
        }

        bool selectedByVote =
            highestCandidates.Count > 0;

        List<ulong> selectionPool =
            selectedByVote
                ? highestCandidates
                : aliveMafiaMembers;

        currentNightKillerClientId =
            selectionPool[
                UnityEngine.Random.Range(
                    0,
                    selectionPool.Count
                )
            ];

        SendCurrentNightKillerToMafia();

        if (showNightActionLogs)
        {
            string selectionType =
                selectedByVote
                    ? "Vote"
                    : "Random";

            Debug.Log(
                $"Night Killer {selectionType} Selected - " +
                $"Client: {currentNightKillerClientId}"
            );
        }
    }

    private List<ulong> GetAliveMafiaClientIds()
    {
        List<ulong> result = new List<ulong>();

        foreach (PlayerMatchState state in playerMatchStates.Values)
        {
            if (state.isAlive && state.team == RoleTeam.Mafia)
                result.Add(state.clientId);
        }

        return result;
    }

    private void SendCurrentNightKillerToMafia()
    {
        if (!IsServer)
            return;

        foreach (PlayerMatchState state in playerMatchStates.Values)
        {
            if (state.team != RoleTeam.Mafia)
                continue;

            SendCurrentNightKillerToClient(state.clientId);
        }
    }

    private void SendCurrentNightKillerToClient(
        ulong clientId)
    {
        if (!IsServer ||
            NetworkManager == null ||
            !NetworkManager.ConnectedClients.ContainsKey(clientId))
        {
            return;
        }

        ReceiveCurrentNightKillerRpc(
            currentNightKillerClientId,
            RpcTarget.Single(clientId, RpcTargetUse.Temp)
        );
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void ReceiveCurrentNightKillerRpc(ulong killerClientId, RpcParams rpcParams = default)
    {
        localNightKillerClientId = killerClientId;
        LocalNightKillerChanged?.Invoke(killerClientId);

        Debug.Log(killerClientId == NoClientId
            ? "이번 밤 살인 담당자가 아직 결정되지 않았습니다."
            : $"이번 밤 살인 담당자 ClientId: {killerClientId}");
    }

    public void SubmitMediumCommuneTarget(
        ulong targetClientId)
    {
        if (!IsClient)
            return;

        SubmitMediumCommuneTargetRpc(targetClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitMediumCommuneTargetRpc(
        ulong targetClientId,
        RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong mediumClientId =
            rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "현재 밤 행동 페이즈가 아닙니다."
            );

            return;
        }

        if (!CanUseCitizenAction(
                mediumClientId,
                RoleId.Medium))
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "영매사 능력을 사용할 수 없습니다."
            );

            return;
        }

        if (targetClientId == mediumClientId)
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "자기 자신과는 교신할 수 없습니다."
            );

            return;
        }

        if (!playerMatchStates.TryGetValue(
                targetClientId,
                out PlayerMatchState targetState) ||
            targetState.isAlive)
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "사망자만 교신 대상으로 선택할 수 있습니다."
            );

            return;
        }

        if (NetworkManager == null ||
            !NetworkManager.ConnectedClients.ContainsKey(
                targetClientId))
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "교신 대상이 현재 연결되어 있지 않습니다."
            );

            return;
        }

        if (forensicsDeathEvidenceByVictim.TryGetValue(
                targetClientId,
                out NightDeathEvidence deathEvidence) &&
            deathEvidence.killMethod ==
                NightKillMethod.Exorcism)
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "영체가 소멸한 사망자와는 교신할 수 없습니다."
            );

            return;
        }

        if (completedNightRoleActions.Contains(
                mediumClientId))
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "이미 이번 밤 직업 행동을 완료했습니다."
            );

            return;
        }

        if (!CanPlayerStartRoleAction(
                mediumClientId))
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "행동 불가 또는 감금 상태입니다."
            );

            return;
        }

        if (!ValidateBodyRoleAction(
                mediumClientId,
                targetClientId,
                out string rejectionReason))
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                rejectionReason
            );

            return;
        }

        if (!TryGetPlayerSpirit(
                targetClientId,
                out PlayerSpirit deadSpirit) ||
            !deadSpirit.IsRestrictedDeadSpectator)
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "첫 교신 유효 기간의 사망자가 아닙니다."
            );

            return;
        }

        if (activeMediumTargetByMedium.ContainsKey(
                mediumClientId))
        {
            RejectRoleAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                "이미 다른 사망자와 교신 중입니다."
            );

            return;
        }

        if (CompletePeddlerFakeActionIfNeeded(
                mediumClientId,
                RoleActionType.MediumCommune,
                targetClientId))
        {
            return;
        }

        if (!TryCompleteRealCitizenAction(
                mediumClientId,
                RoleActionType.MediumCommune,
                targetClientId))
        {
            return;
        }

        if (!activeMediumsByDeadTarget.TryGetValue(
                targetClientId,
                out HashSet<ulong> mediumClientIds))
        {
            mediumClientIds =
                new HashSet<ulong>();

            activeMediumsByDeadTarget[
                targetClientId
            ] = mediumClientIds;
        }

        bool isFirstMedium =
            mediumClientIds.Count == 0;

        mediumClientIds.Add(
            mediumClientId
        );

        activeMediumTargetByMedium[
            mediumClientId
        ] = targetClientId;

        /*
         * 동일한 사망자를 선택한 영매사들은
         * 사망자 ClientId를 기준으로 같은 채널을 사용한다.
         */
        string channelName =
            $"medium-{villageLayoutSeed.Value}-{currentDay.Value}-{targetClientId}";

        SendMediumCommunicationStart(
            mediumClientId,
            targetClientId,
            channelName
        );

        if (isFirstMedium)
        {
            SendMediumCommunicationStart(
                targetClientId,
                mediumClientId,
                channelName
            );
        }

        SendPrivateNotification(
            mediumClientId,
            $"{targetState.playerName}님과의 교신에 참여했습니다."
        );

        if (isFirstMedium)
        {
            SendPrivateNotification(
                targetClientId,
                "영매사가 교신을 시작했습니다."
            );
        }
        else
        {
            SendPrivateNotification(
                targetClientId,
                "다른 영매사가 교신에 참여했습니다."
            );
        }

        AcceptRoleAction(
            mediumClientId,
            RoleActionType.MediumCommune,
            targetClientId
        );
    }

    private void SendMediumCommunicationStart(
        ulong receiverClientId,
        ulong partnerClientId,
        string channelName)
    {
        if (!IsServer ||
            NetworkManager == null ||
            !NetworkManager.ConnectedClients.ContainsKey(
                receiverClientId) ||
            string.IsNullOrWhiteSpace(channelName))
        {
            return;
        }

        ReceiveMediumCommunicationStartedRpc(
            new FixedString128Bytes(channelName),
            partnerClientId,
            RpcTarget.Single(
                receiverClientId,
                RpcTargetUse.Temp
            )
        );
    }

    private void EndMediumCommunicationForClient(
        ulong clientId)
    {
        if (!IsServer)
            return;

        if (activeMediumTargetByMedium.TryGetValue(
                clientId,
                out ulong deadClientId))
        {
            EndMediumCommunication(
                clientId,
                deadClientId
            );

            return;
        }

        if (!activeMediumsByDeadTarget.TryGetValue(
                clientId,
                out HashSet<ulong> mediumClientIds))
        {
            return;
        }

        List<ulong> copiedMediumClientIds =
            new List<ulong>(
                mediumClientIds
            );

        for (int i = 0;
             i < copiedMediumClientIds.Count;
             i++)
        {
            EndMediumCommunication(
                copiedMediumClientIds[i],
                clientId
            );
        }
    }

    private void EndAllMediumCommunications()
    {
        if (!IsServer || activeMediumTargetByMedium.Count == 0)
            return;

        List<KeyValuePair<ulong, ulong>> communications =
            new List<KeyValuePair<ulong, ulong>>(activeMediumTargetByMedium);

        for (int i = 0; i < communications.Count; i++)
            EndMediumCommunication(communications[i].Key, communications[i].Value);
    }

    private void ValidateActiveMediumCommunications()
    {
        if (!IsServer ||
            activeMediumTargetByMedium.Count == 0)
        {
            return;
        }

        List<KeyValuePair<ulong, ulong>>
            communications =
                new List<KeyValuePair<ulong, ulong>>(
                    activeMediumTargetByMedium
                );

        for (int i = 0;
             i < communications.Count;
             i++)
        {
            ulong mediumClientId =
                communications[i].Key;

            ulong deadClientId =
                communications[i].Value;

            if (CanMaintainMediumCommunication(
                    mediumClientId,
                    deadClientId))
            {
                continue;
            }

            EndMediumCommunication(
                mediumClientId,
                deadClientId
            );
        }
    }

    private bool CanMaintainMediumCommunication(
        ulong mediumClientId,
        ulong deadClientId)
    {
        if (currentPhase.Value !=
            MatchPhase.NightAction)
        {
            return false;
        }

        if (NetworkManager == null ||
            !NetworkManager.ConnectedClients.ContainsKey(
                mediumClientId) ||
            !NetworkManager.ConnectedClients.ContainsKey(
                deadClientId))
        {
            return false;
        }

        if (!playerMatchStates.TryGetValue(
                mediumClientId,
                out PlayerMatchState mediumState) ||
            !mediumState.isAlive)
        {
            return false;
        }

        if (!playerMatchStates.TryGetValue(
                deadClientId,
                out PlayerMatchState deadState) ||
            deadState.isAlive)
        {
            return false;
        }

        if (!TryGetPlayerSpirit(
                mediumClientId,
                out PlayerSpirit mediumSpirit) ||
            !mediumSpirit.CanUseRoleAction ||
            mediumSpirit.IsConfinedForNight)
        {
            return false;
        }

        if (!TryGetPlayerSpirit(
                deadClientId,
                out PlayerSpirit deadSpirit) ||
            !deadSpirit.IsRestrictedDeadSpectator)
        {
            return false;
        }

        return true;
    }


    private void EndMediumCommunication(
        ulong mediumClientId,
        ulong deadClientId)
    {
        activeMediumTargetByMedium.Remove(
            mediumClientId
        );

        bool shouldEndDeadClientChannel =
            false;

        if (activeMediumsByDeadTarget.TryGetValue(
                deadClientId,
                out HashSet<ulong> mediumClientIds))
        {
            mediumClientIds.Remove(
                mediumClientId
            );

            if (mediumClientIds.Count == 0)
            {
                activeMediumsByDeadTarget.Remove(
                    deadClientId
                );

                shouldEndDeadClientChannel =
                    true;
            }
        }

        if (NetworkManager != null &&
            NetworkManager.ConnectedClients.ContainsKey(
                mediumClientId))
        {
            ReceiveMediumCommunicationEndedRpc(
                RpcTarget.Single(
                    mediumClientId,
                    RpcTargetUse.Temp
                )
            );
        }

        if (shouldEndDeadClientChannel &&
            NetworkManager != null &&
            NetworkManager.ConnectedClients.ContainsKey(
                deadClientId))
        {
            ReceiveMediumCommunicationEndedRpc(
                RpcTarget.Single(
                    deadClientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void ReceiveMediumCommunicationStartedRpc(
        FixedString128Bytes channelName,
        ulong partnerClientId,
        RpcParams rpcParams = default)
    {
        hasLocalMediumCommunication = true;

        LocalMediumCommunicationStarted?.Invoke(
            channelName.ToString(),
            partnerClientId
        );
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void ReceiveMediumCommunicationEndedRpc(
        RpcParams rpcParams = default)
    {
        hasLocalMediumCommunication = false;
        LocalMediumCommunicationEnded?.Invoke();
    }

    public void SubmitTextChatMessage(
        TextChatChannel channel,
        string message)
    {
        if (!IsClient ||
            !IsSpawned)
        {
            return;
        }

        string normalizedMessage =
            NormalizeTextChatMessage(message);

        if (string.IsNullOrEmpty(normalizedMessage))
            return;

        SubmitTextChatMessageRpc(
            channel,
            new FixedString512Bytes(
                normalizedMessage
            )
        );
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Everyone
    )]
    private void SubmitTextChatMessageRpc(
        TextChatChannel channel,
        FixedString512Bytes message,
        RpcParams rpcParams = default)
    {
        if (!IsServer ||
            NetworkManager == null)
        {
            return;
        }

        ulong senderClientId =
            rpcParams.Receive.SenderClientId;

        if (!playerMatchStates.TryGetValue(
                senderClientId,
                out PlayerMatchState senderState))
        {
            return;
        }

        double serverTime =
            NetworkManager.ServerTime.Time;

        if (lastTextChatServerTimeByClient.TryGetValue(
                senderClientId,
                out double lastSendTime) &&
            serverTime - lastSendTime <
                MinimumTextChatInterval)
        {
            return;
        }

        string normalizedMessage =
            NormalizeTextChatMessage(
                message.ToString()
            );

        if (string.IsNullOrEmpty(normalizedMessage) ||
            !TryBuildTextChatRecipients(
                channel,
                senderClientId,
                senderState,
                out HashSet<ulong> recipients))
        {
            return;
        }

        lastTextChatServerTimeByClient[
            senderClientId
        ] = serverTime;

        FixedString512Bytes networkMessage =
            new FixedString512Bytes(
                normalizedMessage
            );

        foreach (ulong recipientClientId in recipients)
        {
            if (!NetworkManager.ConnectedClients
                    .ContainsKey(recipientClientId))
            {
                continue;
            }

            ReceiveTextChatMessageRpc(
                channel,
                senderClientId,
                senderState.playerName,
                networkMessage,
                RpcTarget.Single(
                    recipientClientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    private bool TryBuildTextChatRecipients(
        TextChatChannel channel,
        ulong senderClientId,
        PlayerMatchState senderState,
        out HashSet<ulong> recipients)
    {
        recipients = new HashSet<ulong>();

        switch (channel)
        {
            case TextChatChannel.MorningPublic:
                if (currentPhase.Value !=
                        MatchPhase.MorningVote ||
                    !senderState.isAlive)
                {
                    return false;
                }

                foreach (ulong clientId in
                         NetworkManager.ConnectedClients.Keys)
                {
                    recipients.Add(clientId);
                }

                return recipients.Count > 0;

            case TextChatChannel.MafiaNight:
                if ((currentPhase.Value !=
                         MatchPhase.NightPreparation &&
                     currentPhase.Value !=
                         MatchPhase.NightAction) ||
                    !senderState.isAlive ||
                    senderState.team != RoleTeam.Mafia)
                {
                    return false;
                }

                foreach (PlayerMatchState state in
                         playerMatchStates.Values)
                {
                    if (state.isAlive &&
                        state.team == RoleTeam.Mafia)
                    {
                        recipients.Add(state.clientId);
                    }
                }

                return recipients.Count > 0;

            case TextChatChannel.MediumNight:
                if (currentPhase.Value !=
                    MatchPhase.NightAction)
                {
                    return false;
                }

                ulong deadClientId;

                if (activeMediumTargetByMedium.TryGetValue(
                        senderClientId,
                        out deadClientId))
                {
                    recipients.Add(deadClientId);
                }
                else if (activeMediumsByDeadTarget
                         .ContainsKey(senderClientId))
                {
                    deadClientId = senderClientId;
                }
                else
                {
                    return false;
                }

                recipients.Add(deadClientId);

                if (activeMediumsByDeadTarget.TryGetValue(
                        deadClientId,
                        out HashSet<ulong> mediumClientIds))
                {
                    foreach (ulong mediumClientId in
                             mediumClientIds)
                    {
                        recipients.Add(mediumClientId);
                    }
                }

                return recipients.Count > 1;

            case TextChatChannel.Dead:
                if (senderState.isAlive ||
                    currentPhase.Value == MatchPhase.None ||
                    currentPhase.Value == MatchPhase.GameResult)
                {
                    return false;
                }

                /*
                 * 영매사가 아직 교신 대상으로 선택할 수 있는
                 * RestrictedDeadSpectator는 사망자 채팅에서
                 * 완전히 분리한다. 실제 교신 중일 때뿐 아니라
                 * 교신 가능 기간 전체에 적용해, 다른 사망자에게
                 * 얻은 정보를 영매사에게 전달하는 우회를 막는다.
                 */
                if (TryGetPlayerSpirit(
                        senderClientId,
                        out PlayerSpirit senderDeadSpirit) &&
                    senderDeadSpirit.IsRestrictedDeadSpectator)
                {
                    return false;
                }

                foreach (PlayerMatchState state in
                         playerMatchStates.Values)
                {
                    if (state.isAlive ||
                        !NetworkManager.ConnectedClients
                            .ContainsKey(state.clientId))
                    {
                        continue;
                    }

                    if (TryGetPlayerSpirit(
                            state.clientId,
                            out PlayerSpirit recipientDeadSpirit) &&
                        recipientDeadSpirit.IsRestrictedDeadSpectator)
                    {
                        continue;
                    }

                    recipients.Add(state.clientId);
                }

                return recipients.Count > 0;

            default:
                return false;
        }
    }

    private static string NormalizeTextChatMessage(
        string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        string normalized = message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        if (normalized.Length >
            MaximumTextChatCharacterCount)
        {
            normalized = normalized.Substring(
                0,
                MaximumTextChatCharacterCount
            );
        }

        normalized = LimitUtf8Text(
            normalized,
            MaximumTextChatUtf8ByteCount
        );

        return normalized
            .Replace("<", "＜")
            .Replace(">", "＞");
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveTextChatMessageRpc(
        TextChatChannel channel,
        ulong senderClientId,
        FixedString64Bytes senderName,
        FixedString512Bytes message,
        RpcParams rpcParams = default)
    {
        LocalTextChatMessageReceived?.Invoke(
            channel,
            senderClientId,
            senderName.ToString(),
            message.ToString()
        );
    }

    public void SubmitFakeRoleAction(
        RoleActionType actionType,
        ulong targetClientId,
        int targetHouseId)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction)
        {
            return;
        }

        SubmitFakeRoleActionRpc(
            actionType,
            targetClientId,
            targetHouseId
        );
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Everyone
    )]
    private void SubmitFakeRoleActionRpc(
        RoleActionType actionType,
        ulong targetClientId,
        int targetHouseId,
        RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong actorClientId =
            rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(
                actorClientId,
                actionType,
                "현재 밤 행동 페이즈가 아닙니다."
            );

            return;
        }

        if (!playerMatchStates.TryGetValue(
                actorClientId,
                out PlayerMatchState actorState) ||
            !actorState.isAlive ||
            actorState.team != RoleTeam.Mafia ||
            actorState.role == RoleId.Spy)
        {
            RejectRoleAction(
                actorClientId,
                actionType,
                "가짜 주민 행동을 사용할 수 있는 마녀 진영 구성원이 아닙니다."
            );

            return;
        }

        if (actorClientId ==
            currentNightKillerClientId)
        {
            RejectRoleAction(
                actorClientId,
                actionType,
                "살해 담당자는 가짜 주민 행동을 사용할 수 없습니다."
            );

            return;
        }

        if (completedNightRoleActions.Contains(
                actorClientId))
        {
            RejectRoleAction(
                actorClientId,
                actionType,
                "이미 이번 밤 직업 행동을 완료했습니다."
            );

            return;
        }

        if (!CanPlayerStartRoleAction(
                actorClientId))
        {
            RejectRoleAction(
                actorClientId,
                actionType,
                "행동 불가 또는 감금 상태입니다."
            );

            return;
        }

        int weaponIndex =
            GetCurrentWeaponIndex(
                actorClientId
            );

        if (weaponIndex < 0 ||
            !TryGetCitizenActionFromWeapon(
                (RoleWeaponId)weaponIndex,
                out RoleActionType expectedActionType,
                out RoleActionTargetType expectedTargetType) ||
            expectedActionType != actionType)
        {
            RejectRoleAction(
                actorClientId,
                actionType,
                "현재 위장 도구와 행동이 일치하지 않습니다."
            );

            return;
        }

        if (!ValidateFakeRoleActionTarget(
                actorClientId,
                actionType,
                expectedTargetType,
                targetClientId,
                targetHouseId,
                out string rejectionReason))
        {
            RejectRoleAction(
                actorClientId,
                actionType,
                rejectionReason
            );

            return;
        }

        completedNightRoleActions.Add(
            actorClientId
        );

        AcceptRoleAction(
            actorClientId,
            actionType,
            targetClientId,
            targetHouseId,
            true,
            true
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Mafia Fake Action - " +
                $"Actor: {actorClientId}, " +
                $"Role: {actorState.role}, " +
                $"Action: {actionType}, " +
                $"TargetClient: {targetClientId}, " +
                $"TargetHouse: {targetHouseId}"
            );
        }
    }

    private bool ValidateFakeRoleActionTarget(
        ulong actorClientId,
        RoleActionType actionType,
        RoleActionTargetType targetType,
        ulong targetClientId,
        int targetHouseId,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;

        if (targetType == RoleActionTargetType.FrontDoor)
        {
            if (!ValidateFrontDoorRoleAction(
                    actorClientId,
                    targetHouseId,
                    out House targetHouse,
                    out rejectionReason))
            {
                return false;
            }

            ulong ownerClientId =
                targetHouse.OwnerClientId;

            if (actionType == RoleActionType.HunterTrack)
            {
                if (ownerClientId == actorClientId ||
                    !IsAlivePlayer(ownerClientId))
                {
                    rejectionReason =
                        "생존한 다른 플레이어의 집이어야 합니다.";
                    return false;
                }

                return true;
            }

            if (actionType == RoleActionType.UndertakerSeal)
            {
                if (ownerClientId == House.NoOwnerClientId ||
                    IsAlivePlayer(ownerClientId) ||
                    IsFrontDoorSealed(targetHouseId))
                {
                    rejectionReason =
                        "봉쇄되지 않은 사망자의 집 정문이어야 합니다.";
                    return false;
                }

                return true;
            }

            rejectionReason =
                "지원하지 않는 가짜 정문 행동입니다.";
            return false;
        }

        if (targetType == RoleActionTargetType.Spirit)
        {
            if (actionType != RoleActionType.Exorcism ||
                targetClientId == actorClientId ||
                !IsAlivePlayer(targetClientId))
            {
                rejectionReason =
                    "유효한 가짜 퇴마 대상이 아닙니다.";
                return false;
            }

            if (!ValidateSpiritRoleAction(
                    actorClientId,
                    targetClientId,
                    out PlayerSpirit targetSpirit,
                    out rejectionReason))
            {
                return false;
            }

            if (!targetSpirit.IsConfinedForNight)
            {
                rejectionReason =
                    "감옥에 갇힌 영체가 아닙니다.";
                return false;
            }

            return true;
        }

        if (targetType != RoleActionTargetType.Body)
        {
            rejectionReason =
                "지원하지 않는 가짜 행동 대상입니다.";
            return false;
        }

        if (!playerMatchStates.TryGetValue(
                targetClientId,
                out PlayerMatchState targetState))
        {
            rejectionReason =
                "대상 플레이어를 찾지 못했습니다.";
            return false;
        }

        switch (actionType)
        {
            case RoleActionType.DoctorProtect:
                if (!targetState.isAlive)
                {
                    rejectionReason =
                        "생존한 육체만 대상으로 선택할 수 있습니다.";
                    return false;
                }
                break;

            case RoleActionType.BailiffConfine:
            case RoleActionType.DetectiveInspect:
                if (!targetState.isAlive ||
                    targetClientId == actorClientId)
                {
                    rejectionReason =
                        "생존한 다른 플레이어의 육체여야 합니다.";
                    return false;
                }
                break;

            case RoleActionType.ForensicsInspect:
                if (targetState.isAlive)
                {
                    rejectionReason =
                        "사망한 플레이어의 육체여야 합니다.";
                    return false;
                }
                break;

            case RoleActionType.MediumCommune:
                if (targetState.isAlive ||
                    targetClientId == actorClientId ||
                    !TryGetPlayerSpirit(
                        targetClientId,
                        out PlayerSpirit deadSpirit) ||
                    !deadSpirit.IsRestrictedDeadSpectator)
                {
                    rejectionReason =
                        "첫 교신 기간의 사망자 육체여야 합니다.";
                    return false;
                }
                break;

            default:
                rejectionReason =
                    "지원하지 않는 가짜 육체 행동입니다.";
                return false;
        }

        return ValidateBodyRoleAction(
            actorClientId,
            targetClientId,
            out rejectionReason
        );
    }

    public void SubmitMafiaKillTarget(ulong targetClientId)
    {
        if (!IsClient || currentPhase.Value != MatchPhase.NightAction)
            return;

        SubmitMafiaKillTargetRpc(targetClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitMafiaKillTargetRpc(ulong targetClientId, RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong senderClientId = rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(senderClientId, RoleActionType.MafiaKill, "현재 밤 행동 페이즈가 아닙니다.");
            return;
        }

        if (IsAliveRole(
                senderClientId,
                RoleId.SerialKiller))
        {
            SubmitSerialKillerKillTargetServer(
                senderClientId,
                targetClientId
            );

            return;
        }

        if (senderClientId != currentNightKillerClientId)
        {
            RejectRoleAction(senderClientId, RoleActionType.MafiaKill, "살인 담당자가 아닙니다.");
            return;
        }

        if (!IsAliveMafia(senderClientId))
        {
            RejectRoleAction(senderClientId, RoleActionType.MafiaKill, "생존한 마녀 진영 구성원이 아닙니다.");
            return;
        }

        if (playerMatchStates.TryGetValue(senderClientId, out PlayerMatchState senderState) &&
            senderState.role == RoleId.Alchemist)
        {
            RejectRoleAction(senderClientId, RoleActionType.MafiaKill, "마녀는 직접 살해 대신 독살을 사용해야 합니다.");
            return;
        }

        if (senderState.role == RoleId.CurseCaster)
        {
            RejectRoleAction(
                senderClientId,
                RoleActionType.MafiaKill,
                "저주술사는 저주 인형으로 팀 살해를 수행해야 합니다."
            );
            return;
        }

        if (!IsAliveNonMafia(targetClientId))
        {
            RejectRoleAction(senderClientId, RoleActionType.MafiaKill, "유효한 비마녀 생존자가 아닙니다.");
            return;
        }

        if (completedNightRoleActions.Contains(senderClientId))
        {
            RejectRoleAction(senderClientId, RoleActionType.MafiaKill, "이미 이번 밤 직업 행동을 완료했습니다.");
            return;
        }

        if (!CanPlayerStartRoleAction(senderClientId))
        {
            RejectRoleAction(senderClientId, RoleActionType.MafiaKill, "행동 불가 또는 감금 상태입니다.");
            return;
        }

        if (!ValidateBodyRoleAction(senderClientId, targetClientId, out string rejectionReason))
        {
            RejectRoleAction(senderClientId, RoleActionType.MafiaKill, rejectionReason);
            return;
        }

        mafiaKillTargetClientId = targetClientId;
        currentNightKillMethod = NightKillMethod.Direct;

        /*
         * 감식 결과에는 행동이 확정된 순간의 위장 도구를 사용한다.
         */
        currentNightKillerWeaponIndex = GetCurrentWeaponIndex(senderClientId);

        completedNightRoleActions.Add(senderClientId);
        AcceptRoleAction(
            senderClientId,
            RoleActionType.MafiaKill,
            targetClientId
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Mafia Kill Target - Killer: {senderClientId}, " +
                $"Target: {targetClientId}, Weapon: {currentNightKillerWeaponIndex}"
            );
        }
    }

    private void SubmitSerialKillerKillTargetServer(
        ulong serialKillerClientId,
        ulong targetClientId)
    {
        if (serialKillerClientId == targetClientId ||
            !IsAlivePlayer(targetClientId))
        {
            RejectRoleAction(
                serialKillerClientId,
                RoleActionType.MafiaKill,
                "자기 자신이 아닌 생존자만 살해할 수 있습니다."
            );

            return;
        }

        if (completedNightRoleActions.Contains(
                serialKillerClientId))
        {
            RejectRoleAction(
                serialKillerClientId,
                RoleActionType.MafiaKill,
                "이미 이번 밤 살해 행동을 완료했습니다."
            );

            return;
        }

        if (!CanPlayerStartRoleAction(
                serialKillerClientId))
        {
            RejectRoleAction(
                serialKillerClientId,
                RoleActionType.MafiaKill,
                "행동 불가 또는 감금 상태입니다."
            );

            return;
        }

        if (!ValidateBodyRoleAction(
                serialKillerClientId,
                targetClientId,
                out string rejectionReason))
        {
            RejectRoleAction(
                serialKillerClientId,
                RoleActionType.MafiaKill,
                rejectionReason
            );

            return;
        }

        serialKillerTargetClientId =
            targetClientId;

        serialKillerWeaponIndex =
            GetCurrentWeaponIndex(
                serialKillerClientId
            );

        completedNightRoleActions.Add(
            serialKillerClientId
        );

        AcceptRoleAction(
            serialKillerClientId,
            RoleActionType.MafiaKill,
            targetClientId
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Serial Killer Target - " +
                $"Killer: {serialKillerClientId}, " +
                $"Target: {targetClientId}, " +
                $"Weapon: {serialKillerWeaponIndex}"
            );
        }
    }

    public void SubmitAlchemistPoisonTarget(
        int targetHouseId)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction)
        {
            return;
        }

        SubmitAlchemistPoisonTargetRpc(
            targetHouseId
        );
    }

    public void SubmitAlchemistPoisonBodyTarget(
        ulong targetClientId)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction)
        {
            return;
        }

        SubmitAlchemistPoisonBodyTargetRpc(
            targetClientId
        );
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Everyone
    )]
    private void SubmitAlchemistPoisonTargetRpc(
        int targetHouseId,
        RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong alchemistClientId =
            rpcParams.Receive.SenderClientId;

        if (!TryValidateAlchemistPoisonActor(
                alchemistClientId,
                out string rejectionReason))
        {
            RejectRoleAction(
                alchemistClientId,
                RoleActionType.AlchemistPoison,
                rejectionReason
            );
            return;
        }

        if (!ValidateFrontDoorRoleAction(
                alchemistClientId,
                targetHouseId,
                out House targetHouse,
                out rejectionReason))
        {
            RejectRoleAction(
                alchemistClientId,
                RoleActionType.AlchemistPoison,
                rejectionReason
            );
            return;
        }

        ulong targetClientId =
            targetHouse.OwnerClientId;

        if (targetClientId == alchemistClientId)
        {
            RejectRoleAction(
                alchemistClientId,
                RoleActionType.AlchemistPoison,
                "자기 집에는 독살을 사용할 수 없습니다."
            );
            return;
        }

        if (!IsAliveNonMafia(targetClientId))
        {
            RejectRoleAction(
                alchemistClientId,
                RoleActionType.AlchemistPoison,
                "독살할 수 있는 비마녀 생존자의 집이 아닙니다."
            );
            return;
        }

        bool targetBodyIsAway =
            activeDrunkardSleepHouseByClient.ContainsKey(
                targetClientId
            );

        CommitAlchemistPoison(
            alchemistClientId,
            targetBodyIsAway
                ? NoClientId
                : targetClientId,
            targetClientId,
            targetHouseId
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Alchemist Poison House - " +
                $"Alchemist: {alchemistClientId}, " +
                $"House: {targetHouseId}, " +
                $"Owner: {targetClientId}, " +
                $"BodyAway: {targetBodyIsAway}"
            );
        }
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Everyone
    )]
    private void SubmitAlchemistPoisonBodyTargetRpc(
        ulong targetClientId,
        RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong alchemistClientId =
            rpcParams.Receive.SenderClientId;

        if (!TryValidateAlchemistPoisonActor(
                alchemistClientId,
                out string rejectionReason))
        {
            RejectRoleAction(
                alchemistClientId,
                RoleActionType.AlchemistPoison,
                rejectionReason
            );
            return;
        }

        if (!IsAliveNonMafia(targetClientId))
        {
            RejectRoleAction(
                alchemistClientId,
                RoleActionType.AlchemistPoison,
                "유효한 비마녀 생존자의 육체가 아닙니다."
            );
            return;
        }

        if (!ValidateBodyRoleAction(
                alchemistClientId,
                targetClientId,
                out rejectionReason))
        {
            RejectRoleAction(
                alchemistClientId,
                RoleActionType.AlchemistPoison,
                rejectionReason
            );
            return;
        }

        CommitAlchemistPoison(
            alchemistClientId,
            targetClientId,
            targetClientId,
            GetOwnedHouseId(targetClientId)
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Alchemist Poison Body - " +
                $"Alchemist: {alchemistClientId}, " +
                $"Target: {targetClientId}"
            );
        }
    }

    private bool TryValidateAlchemistPoisonActor(
        ulong alchemistClientId,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            rejectionReason =
                "현재 밤 행동 페이즈가 아닙니다.";
            return false;
        }

        if (!IsAliveRole(
                alchemistClientId,
                RoleId.Alchemist))
        {
            rejectionReason =
                "생존한 마녀가 아닙니다.";
            return false;
        }

        if (alchemistClientId !=
            currentNightKillerClientId)
        {
            rejectionReason =
                "이번 밤 살인 담당자가 아닙니다.";
            return false;
        }

        if (completedNightRoleActions.Contains(
                alchemistClientId))
        {
            rejectionReason =
                "이미 이번 밤 직업 행동을 완료했습니다.";
            return false;
        }

        if (!CanPlayerStartRoleAction(
                alchemistClientId))
        {
            rejectionReason =
                "행동 불가 또는 감금 상태입니다.";
            return false;
        }

        return true;
    }

    private void CommitAlchemistPoison(
        ulong alchemistClientId,
        ulong targetClientId,
        ulong recordTargetClientId,
        int recordTargetHouseId)
    {
        mafiaKillTargetClientId =
            targetClientId;

        currentNightKillMethod =
            NightKillMethod.Poison;

        currentNightKillerWeaponIndex = -1;

        completedNightRoleActions.Add(
            alchemistClientId
        );

        AcceptRoleAction(
            alchemistClientId,
            RoleActionType.AlchemistPoison,
            recordTargetClientId,
            recordTargetHouseId
        );
    }

    public void SubmitUndertakerSealTarget(int targetHouseId)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction)
        {
            return;
        }

        SubmitUndertakerSealTargetRpc(targetHouseId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitUndertakerSealTargetRpc(
        int targetHouseId,
        RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong undertakerClientId =
            rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(
                undertakerClientId,
                RoleActionType.UndertakerSeal,
                "현재 밤 행동 페이즈가 아닙니다."
            );
            return;
        }

        if (!CanUseCitizenAction(
                undertakerClientId,
                RoleId.Undertaker))
        {
            RejectRoleAction(
                undertakerClientId,
                RoleActionType.UndertakerSeal,
                "장의사 능력을 사용할 수 없습니다."
            );
            return;
        }

        if (completedNightRoleActions.Contains(undertakerClientId))
        {
            RejectRoleAction(
                undertakerClientId,
                RoleActionType.UndertakerSeal,
                "이미 이번 밤 직업 행동을 완료했습니다."
            );
            return;
        }

        if (!CanPlayerStartRoleAction(undertakerClientId))
        {
            RejectRoleAction(
                undertakerClientId,
                RoleActionType.UndertakerSeal,
                "행동 불가 또는 감금 상태입니다."
            );
            return;
        }

        if (!ValidateFrontDoorRoleAction(
                undertakerClientId,
                targetHouseId,
                out House targetHouse,
                out string rejectionReason))
        {
            RejectRoleAction(
                undertakerClientId,
                RoleActionType.UndertakerSeal,
                rejectionReason
            );
            return;
        }

        ulong ownerClientId = targetHouse.OwnerClientId;

        if (ownerClientId == House.NoOwnerClientId ||
            IsAlivePlayer(ownerClientId))
        {
            RejectRoleAction(
                undertakerClientId,
                RoleActionType.UndertakerSeal,
                "사망자의 집 정문만 봉쇄할 수 있습니다."
            );
            return;
        }

        if (IsFrontDoorSealed(targetHouseId))
        {
            RejectRoleAction(
                undertakerClientId,
                RoleActionType.UndertakerSeal,
                "이미 봉쇄된 정문입니다."
            );
            return;
        }

        if (CompletePeddlerFakeActionIfNeeded(
                undertakerClientId,
                RoleActionType.UndertakerSeal,
                ownerClientId,
                targetHouseId))
        {
            return;
        }

        if (!TryCompleteRealCitizenAction(
                undertakerClientId,
                RoleActionType.UndertakerSeal,
                ownerClientId,
                targetHouseId))
        {
            return;
        }

        SealedFrontDoorHouseIds.Add(
            targetHouseId
        );

        AcceptRoleAction(
            undertakerClientId,
            RoleActionType.UndertakerSeal,
            ownerClientId,
            targetHouseId
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Undertaker Sealed Front Door - " +
                $"Undertaker: {undertakerClientId}, " +
                $"House: {targetHouseId}, " +
                $"Dead Owner: {ownerClientId}"
            );
        }
    }

    public void SubmitHunterTrackTarget(int targetHouseId)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction)
        {
            return;
        }

        SubmitHunterTrackTargetRpc(targetHouseId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitHunterTrackTargetRpc(
        int targetHouseId,
        RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong hunterClientId =
            rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(
                hunterClientId,
                RoleActionType.HunterTrack,
                "현재 밤 행동 페이즈가 아닙니다."
            );

            return;
        }

        if (!CanUseCitizenAction(
                hunterClientId,
                RoleId.Hunter))
        {
            RejectRoleAction(
                hunterClientId,
                RoleActionType.HunterTrack,
                "사냥꾼 능력을 사용할 수 없습니다."
            );

            return;
        }

        if (completedNightRoleActions.Contains(
                hunterClientId))
        {
            RejectRoleAction(
                hunterClientId,
                RoleActionType.HunterTrack,
                "이미 이번 밤 직업 행동을 완료했습니다."
            );

            return;
        }

        if (!CanPlayerStartRoleAction(
                hunterClientId))
        {
            RejectRoleAction(
                hunterClientId,
                RoleActionType.HunterTrack,
                "행동 불가 또는 감금 상태입니다."
            );

            return;
        }

        if (!ValidateFrontDoorRoleAction(
                hunterClientId,
                targetHouseId,
                out House targetHouse,
                out string rejectionReason))
        {
            RejectRoleAction(
                hunterClientId,
                RoleActionType.HunterTrack,
                rejectionReason
            );

            return;
        }

        ulong targetClientId =
            targetHouse.OwnerClientId;

        if (targetClientId == hunterClientId)
        {
            RejectRoleAction(
                hunterClientId,
                RoleActionType.HunterTrack,
                "자기 집에는 사용할 수 없습니다."
            );

            return;
        }

        if (!IsAlivePlayer(targetClientId))
        {
            RejectRoleAction(
                hunterClientId,
                RoleActionType.HunterTrack,
                "생존한 플레이어의 집이 아닙니다."
            );

            return;
        }

        if (CompletePeddlerFakeActionIfNeeded(
                hunterClientId,
                RoleActionType.HunterTrack,
                targetClientId,
                targetHouseId))
        {
            return;
        }

        if (!TryCompleteRealCitizenAction(
                hunterClientId,
                RoleActionType.HunterTrack,
                targetClientId,
                targetHouseId))
        {
            return;
        }

        HunterRouteRecord routeRecord =
            new HunterRouteRecord
            {
                targetClientId =
                    targetClientId
            };

        hunterRouteRecordsByHunter[hunterClientId] =
            routeRecord;

        AcceptRoleAction(
            hunterClientId,
            RoleActionType.HunterTrack,
            targetClientId,
            targetHouseId
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Hunter Track Registered - " +
                $"Hunter: {hunterClientId}, " +
                $"Target: {targetClientId}, " +
                $"House: {targetHouse.Id}"
            );
        }
    }

    public void RegisterNightHouseVisit(
        ulong clientId,
        int houseId)
    {
        if (!IsServer ||
            currentPhase.Value !=
                MatchPhase.NightAction ||
            houseId < 0)
        {
            return;
        }

        if (!nightHouseVisitsByClient
                .TryGetValue(
                    clientId,
                    out List<int> visitedHouseIds))
        {
            visitedHouseIds =
                new List<int>();

            nightHouseVisitsByClient[clientId] =
                visitedHouseIds;
        }

        if (visitedHouseIds.Contains(houseId))
            return;

        visitedHouseIds.Add(houseId);

        if (showNightActionLogs)
        {
            Debug.Log(
                "Night House Visit Recorded - " +
                $"Client: {clientId}, " +
                $"House: {houseId}"
            );
        }
    }

    private void ResolveHunterRouteResults()
    {
        foreach (KeyValuePair<ulong, HunterRouteRecord> pair
                 in hunterRouteRecordsByHunter)
        {
            ulong hunterClientId = pair.Key;

            if (NetworkManager == null ||
                !NetworkManager.ConnectedClients.ContainsKey(
                    hunterClientId))
            {
                continue;
            }

            string resultMessage =
                BuildHunterRouteResultMessage(
                    pair.Value
                );

            ReceiveHunterRouteResultRpc(
                new FixedString4096Bytes(resultMessage),
                RpcTarget.Single(
                    hunterClientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    private string BuildHunterRouteResultMessage(
        HunterRouteRecord routeRecord)
    {
        string targetName =
            GetPlayerDisplayName(
                routeRecord.targetClientId
            );

        StringBuilder builder =
            new StringBuilder();

        builder.AppendLine(
            "<size=30>사냥꾼 추적 결과</size>"
        );

        builder.AppendLine(
            $"<size=38><b>{targetName}의 방문한 집</b></size>"
        );

        builder.AppendLine();

        List<int> visitedHouseIds =
            nightHouseVisitsByClient
                .TryGetValue(
                    routeRecord.targetClientId,
                    out List<int> savedVisits)
                ? savedVisits
                : null;

        if (visitedHouseIds == null ||
            visitedHouseIds.Count == 0)
        {
            builder.AppendLine(
                $"{targetName}은(는) 오늘 밤 방문한 집이 없습니다."
            );

            return builder.ToString();
        }

        for (int i = 0;
             i < visitedHouseIds.Count;
             i++)
        {
            builder.AppendLine(
                $"{i + 1}. " +
                GetHouseRouteDisplayName(
                    visitedHouseIds[i]
                )
            );
        }

        return builder.ToString();
    }

    private string GetHouseRouteDisplayName(
        int houseId)
    {
        House house = GetHouse(houseId);

        if (house == null ||
            house.OwnerClientId ==
                House.NoOwnerClientId)
        {
            return $"House {houseId}";
        }

        return
            $"{GetPlayerDisplayName(house.OwnerClientId)}의 집";
    }

    private string GetPlayerDisplayName(
        ulong clientId)
    {
        return playerMatchStates.TryGetValue(
            clientId,
            out PlayerMatchState state)
            ? state.playerName.ToString()
            : $"Player {clientId}";
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceiveHunterRouteResultRpc(
        FixedString4096Bytes resultMessage,
        RpcParams rpcParams = default)
    {
        LocalHunterRouteResultReceived?.Invoke(
            resultMessage.ToString()
        );
    }


    public void SubmitForensicsInspectTarget(ulong targetClientId)
    {
        if (!IsClient || currentPhase.Value != MatchPhase.NightAction)
            return;

        SubmitForensicsInspectTargetRpc(targetClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitForensicsInspectTargetRpc(
        ulong targetClientId,
        RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong forensicsClientId = rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                "현재 밤 행동 페이즈가 아닙니다."
            );

            return;
        }

        if (!CanUseCitizenAction(
                forensicsClientId,
                RoleId.Forensics))
        {
            RejectRoleAction(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                "검시관 능력을 사용할 수 없습니다."
            );

            return;
        }

        if (!playerMatchStates.TryGetValue(
                targetClientId,
                out PlayerMatchState targetState))
        {
            RejectRoleAction(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                "조사 대상 플레이어를 찾지 못했습니다."
            );

            return;
        }

        if (targetState.isAlive)
        {
            RejectRoleAction(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                "사망한 플레이어의 바디만 조사할 수 있습니다."
            );

            return;
        }

        if (!forensicsDeathEvidenceByVictim.ContainsKey(targetClientId))
        {
            RejectRoleAction(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                "이 바디에는 밤중 사망 증거가 없습니다."
            );

            return;
        }

        if (completedNightRoleActions.Contains(forensicsClientId))
        {
            RejectRoleAction(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                "이미 이번 밤 직업 행동을 완료했습니다."
            );

            return;
        }

        if (!CanPlayerStartRoleAction(forensicsClientId))
        {
            RejectRoleAction(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                "행동 불가 또는 감금 상태입니다."
            );

            return;
        }

        if (!ValidateBodyRoleAction(
                forensicsClientId,
                targetClientId,
                out string rejectionReason))
        {
            RejectRoleAction(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                rejectionReason
            );

            return;
        }

        if (CompletePeddlerFakeActionIfNeeded(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                targetClientId,
                GetOwnedHouseId(targetClientId)))
        {
            return;
        }

        if (!TryCompleteRealCitizenAction(
                forensicsClientId,
                RoleActionType.ForensicsInspect,
                targetClientId,
                GetOwnedHouseId(targetClientId)))
        {
            return;
        }

        string resultMessage =
            BuildForensicsResultMessage(
                targetClientId
            );

        AcceptRoleAction(
            forensicsClientId,
            RoleActionType.ForensicsInspect,
            targetClientId,
            GetOwnedHouseId(targetClientId)
        );

        ReceiveForensicsResultRpc(
            new FixedString4096Bytes(resultMessage),
            RpcTarget.Single(forensicsClientId, RpcTargetUse.Temp)
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Forensics Inspect Success - " +
                $"Forensics: {forensicsClientId}, Corpse: {targetClientId}"
            );
        }
    }

    public void SubmitDoctorProtectTarget(ulong targetClientId)
    {
        if (!IsClient || currentPhase.Value != MatchPhase.NightAction)
            return;

        SubmitDoctorProtectTargetRpc(targetClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitDoctorProtectTargetRpc(ulong targetClientId, RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong doctorClientId = rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(doctorClientId, RoleActionType.DoctorProtect, "현재 밤 행동 페이즈가 아닙니다.");
            return;
        }

        if (!CanUseCitizenAction(
                doctorClientId,
                RoleId.Doctor))
        {
            RejectRoleAction(
                doctorClientId,
                RoleActionType.DoctorProtect,
                "의원 능력을 사용할 수 없습니다."
            );
            return;
        }

        if (!IsAlivePlayer(targetClientId))
        {
            RejectRoleAction(doctorClientId, RoleActionType.DoctorProtect, $"보호 대상이 생존 상태가 아닙니다. Target: {targetClientId}");
            return;
        }

        if (completedNightRoleActions.Contains(doctorClientId))
        {
            RejectRoleAction(doctorClientId, RoleActionType.DoctorProtect, "이미 이번 밤 직업 행동을 완료했습니다.");
            return;
        }

        if (!CanPlayerStartRoleAction(doctorClientId))
        {
            RejectRoleAction(doctorClientId, RoleActionType.DoctorProtect, "행동 불가 또는 감금 상태입니다.");
            return;
        }

        if (!ValidateBodyRoleAction(doctorClientId, targetClientId, out string rejectionReason))
        {
            RejectRoleAction(doctorClientId, RoleActionType.DoctorProtect, rejectionReason);
            return;
        }

        if (CompletePeddlerFakeActionIfNeeded(
                doctorClientId,
                RoleActionType.DoctorProtect,
                targetClientId,
                GetOwnedHouseId(targetClientId)))
        {
            return;
        }

        if (!TryCompleteRealCitizenAction(
                doctorClientId,
                RoleActionType.DoctorProtect,
                targetClientId,
                GetOwnedHouseId(targetClientId)))
        {
            return;
        }

        doctorProtectTargets[doctorClientId] =
            targetClientId;

        AcceptRoleAction(
            doctorClientId,
            RoleActionType.DoctorProtect,
            targetClientId,
            GetOwnedHouseId(targetClientId)
        );

        if (showNightActionLogs)
            Debug.Log($"Doctor Protect Target - Doctor: {doctorClientId}, Target: {targetClientId}");
    }

    private void ResolveNightActions()
    {
        SettleNightDutyResults();

        NightResultData result = new NightResultData
        {
            hasResult = true,
            victimClientId = NoClientId,
            secondVictimClientId = NoClientId
        };

        lastNightDeathWasPoisoned.Value = false;
        pendingWinner = MatchWinner.None;
        pendingIndividualWinnerClientId = NoClientId;
        pendingFinalDuel = false;
        pendingFinalDuelSerialKillerClientId = NoClientId;
        pendingFinalDuelMafiaClientId = NoClientId;

        HashSet<ulong> protectedTargets =
            GetCommittedDoctorProtectionTargets();

        if (mafiaKillTargetClientId == NoClientId)
        {
            if (curseDollState.Value.location ==
                CurseDollLocation.None)
            {
                LogNightFailure(
                    currentNightKillMethod == NightKillMethod.Poison
                        ? "독을 뿌렸지만 대상의 육체가 집에 없었습니다."
                        : "살해 담당자가 살인 대상을 지정하지 않았습니다."
                );
            }
        }
        else if (!IsAliveNonMafia(
                     mafiaKillTargetClientId))
        {
            LogNightFailure(
                "지정된 살인 대상이 유효한 생존 비마녀가 아닙니다."
            );
        }
        else
        {
            if (protectedTargets.Contains(
                    mafiaKillTargetClientId))
            {
                LogNightFailure(
                    "살인 대상이 의원의 보호를 받았습니다."
                );
            }
            else if (playerMatchStates.TryGetValue(
                         mafiaKillTargetClientId,
                         out PlayerMatchState victimState))
            {
                bool wasPoisoned =
                    currentNightKillMethod ==
                    NightKillMethod.Poison;

                SaveForensicsEvidence(
                    victimState.clientId,
                    currentNightKillMethod,
                    currentNightKillerWeaponIndex
                );

                AppendNightDeath(
                    ref result,
                    victimState,
                    wasPoisoned,
                    false
                );

                lastNightDeathWasPoisoned.Value =
                    wasPoisoned;

                EliminatePlayer(
                    victimState.clientId
                );

                if (showNightActionLogs)
                {
                    Debug.Log(
                        $"Night Kill Success - Victim: {victimState.playerName}, " +
                        $"Client: {victimState.clientId}, " +
                        $"Method: {currentNightKillMethod}, " +
                        $"Weapon: {currentNightKillerWeaponIndex}"
                    );
                }
            }
        }

        if (serialKillerTargetClientId != NoClientId)
        {
            if (!IsAlivePlayer(
                    serialKillerTargetClientId))
            {
                LogNightFailure(
                    "살인귀의 대상이 이미 유효한 생존자가 아닙니다."
                );
            }
            else if (protectedTargets.Contains(
                         serialKillerTargetClientId))
            {
                LogNightFailure(
                    "살인귀의 대상이 의원의 보호를 받았습니다."
                );
            }
            else if (playerMatchStates.TryGetValue(
                         serialKillerTargetClientId,
                         out PlayerMatchState
                             serialVictimState))
            {
                SaveForensicsEvidence(
                    serialVictimState.clientId,
                    NightKillMethod.Direct,
                    serialKillerWeaponIndex
                );

                AppendNightDeath(
                    ref result,
                    serialVictimState,
                    false,
                    false
                );

                EliminatePlayer(
                    serialVictimState.clientId
                );

                if (showNightActionLogs)
                {
                    Debug.Log(
                        $"Serial Killer Success - " +
                        $"Victim: {serialVictimState.playerName}, " +
                        $"Client: {serialVictimState.clientId}, " +
                        $"Weapon: {serialKillerWeaponIndex}"
                    );
                }
            }
        }

        CurseDollStateData dollState =
            curseDollState.Value;

        ulong curseVictimClientId =
            dollState.location ==
                CurseDollLocation.Held
                ? dollState.holderClientId
                : dollState.location ==
                    CurseDollLocation.Installed
                    ? dollState.installedOwnerClientId
                    : NoClientId;

        if (curseVictimClientId != NoClientId &&
            IsAlivePlayer(curseVictimClientId) &&
            playerMatchStates.TryGetValue(
                curseVictimClientId,
                out PlayerMatchState curseVictimState))
        {
            SaveForensicsEvidence(
                curseVictimClientId,
                NightKillMethod.CurseDoll,
                -1
            );

            AppendNightDeath(
                ref result,
                curseVictimState,
                false,
                true
            );

            EliminatePlayer(
                curseVictimClientId
            );

            if (showNightActionLogs)
            {
                Debug.Log(
                    $"Curse Doll Kill Success - Victim: {curseVictimState.playerName}, " +
                    $"Client: {curseVictimClientId}, Location: {dollState.location}, " +
                    $"House: {dollState.installedHouseId}, Point: {dollState.installedPointType}"
                );
            }
        }

        nightResult.Value = result;

        pendingWinner = MatchWinner.None;
        pendingIndividualWinnerClientId = NoClientId;
        pendingFinalDuel = false;
        pendingFinalDuelSerialKillerClientId = NoClientId;
        pendingFinalDuelMafiaClientId = NoClientId;
        EvaluateVictory();
    }

    private void AppendNightDeath(
        ref NightResultData result,
        PlayerMatchState victimState,
        bool wasPoisoned,
        bool wasCursed)
    {
        if (!result.hasDeath)
        {
            result.hasDeath = true;
            result.victimClientId =
                victimState.clientId;
            result.victimPlayerName =
                victimState.playerName;
            result.victimWasPoisoned =
                wasPoisoned;
            result.victimWasCursed =
                wasCursed;

            return;
        }

        result.hasSecondDeath = true;
        result.secondVictimClientId =
            victimState.clientId;
        result.secondVictimPlayerName =
            victimState.playerName;
        result.secondVictimWasPoisoned =
            wasPoisoned;
        result.secondVictimWasCursed =
            wasCursed;
    }

    private void SaveForensicsEvidence(
        ulong victimClientId,
        NightKillMethod killMethod,
        int weaponIndex)
    {
        int evidenceHouseId = -1;

        if (TryGetPlayerBody(
                victimClientId,
                out PlayerBody victimBody) &&
            houseRegistry != null)
        {
            House evidenceHouse =
                houseRegistry.GetHouseContainingPosition(
                    victimBody.transform.position
                );

            if (evidenceHouse != null)
                evidenceHouseId = evidenceHouse.Id;
        }

        int intruderCount = 0;
        List<string> damagedPlayerNames =
            new List<string>();

        if (evidenceHouseId >= 0)
        {
            if (nightVisitorsByHouse.TryGetValue(
                    evidenceHouseId,
                    out HashSet<ulong> visitors))
            {
                intruderCount = visitors.Count;
            }

            if (nightBloodPlayersByHouse.TryGetValue(
                    evidenceHouseId,
                    out HashSet<ulong> damagedPlayers))
            {
                foreach (ulong damagedClientId in
                         damagedPlayers)
                {
                    if (playerMatchStates.TryGetValue(
                            damagedClientId,
                            out PlayerMatchState damagedState))
                    {
                        damagedPlayerNames.Add(
                            damagedState.playerName.ToString()
                        );
                    }
                    else
                    {
                        damagedPlayerNames.Add(
                            $"Player {damagedClientId}"
                        );
                    }
                }
            }
        }

        damagedPlayerNames.Sort(
            StringComparer.Ordinal
        );

        forensicsIntruderCountByVictim[victimClientId] =
            intruderCount;

        forensicsDamagedPlayerNamesByVictim[victimClientId] =
            damagedPlayerNames;

        switch (killMethod)
        {
            case NightKillMethod.Direct:
                forensicsDeathEvidenceByVictim[victimClientId] =
                    new NightDeathEvidence
                    {
                        killMethod = NightKillMethod.Direct,
                        weaponIndex = weaponIndex
                    };

                break;

            case NightKillMethod.Poison:
                forensicsDeathEvidenceByVictim[victimClientId] =
                    new NightDeathEvidence
                    {
                        killMethod = NightKillMethod.Poison,
                        weaponIndex = -1
                    };

                break;

            case NightKillMethod.CurseDoll:
                forensicsDeathEvidenceByVictim[victimClientId] =
                    new NightDeathEvidence
                    {
                        killMethod = NightKillMethod.CurseDoll,
                        weaponIndex = -1
                    };

                break;

            case NightKillMethod.Exorcism:
                forensicsDeathEvidenceByVictim[victimClientId] =
                    new NightDeathEvidence
                    {
                        killMethod = NightKillMethod.Exorcism,
                        weaponIndex = -1
                    };

                break;
        }
    }

    private HashSet<ulong> GetCommittedDoctorProtectionTargets()
    {
        HashSet<ulong> protectedTargets = new HashSet<ulong>();

        foreach (KeyValuePair<ulong, ulong> protection in doctorProtectTargets)
        {
            ulong targetClientId = protection.Value;

            if (IsAlivePlayer(targetClientId))
                protectedTargets.Add(targetClientId);
        }

        return protectedTargets;
    }

    private void LogNightFailure(string reason)
    {
        if (showNightActionLogs)
            Debug.Log($"Night Kill Failed - {reason}");
    }

    private bool IsAlivePlayer(ulong clientId)
    {
        return clientId != NoClientId &&
               playerMatchStates.TryGetValue(clientId, out PlayerMatchState state) &&
               state.isAlive;
    }

    private bool IsAliveMafia(ulong clientId)
    {
        return clientId != NoClientId &&
               playerMatchStates.TryGetValue(clientId, out PlayerMatchState state) &&
               state.isAlive &&
               state.team == RoleTeam.Mafia;
    }

    private bool IsAliveNonMafia(ulong clientId)
    {
        return clientId != NoClientId &&
               playerMatchStates.TryGetValue(clientId, out PlayerMatchState state) &&
               state.isAlive &&
               state.team != RoleTeam.Mafia;
    }

    private bool IsAliveRole(ulong clientId, RoleId role)
    {
        return clientId != NoClientId &&
               playerMatchStates.TryGetValue(clientId, out PlayerMatchState state) &&
               state.isAlive &&
               state.role == role;
    }

    public int GetMorningVoteCount(
        ulong targetClientId)
    {
        if (MorningVoteTallies == null)
            return 0;

        for (int i = 0;
             i < MorningVoteTallies.Count;
             i++)
        {
            MorningVoteTallyData tally =
                MorningVoteTallies[i];

            if (tally.targetClientId ==
                targetClientId)
            {
                return tally.voteCount;
            }
        }

        return 0;
    }

    private void InitializeMorningVoteTallies()
    {
        if (!IsServer ||
            MorningVoteTallies == null)
        {
            return;
        }

        MorningVoteTallies.Clear();
        morningAbstainVoteCount.Value = 0;

        for (int i = 0;
             i < PublicPlayerStates.Count;
             i++)
        {
            PlayerPublicState playerState =
                PublicPlayerStates[i];

            if (!playerState.isAlive)
                continue;

            MorningVoteTallies.Add(
                new MorningVoteTallyData(
                    playerState.clientId,
                    0
                )
            );
        }
    }

    private void RebuildMorningVoteTallies()
    {
        if (!IsServer ||
            currentPhase.Value !=
                MatchPhase.MorningVote)
        {
            return;
        }

        InitializeMorningVoteTallies();

        foreach (KeyValuePair<ulong, ulong> vote
                 in morningVotes)
        {
            if (vote.Value ==
                AbstainVoteTarget)
            {
                morningAbstainVoteCount.Value++;
                continue;
            }

            IncrementMorningVoteTally(
                vote.Value
            );
        }
    }

    private void IncrementMorningVoteTally(
        ulong targetClientId)
    {
        if (!IsServer ||
            MorningVoteTallies == null)
        {
            return;
        }

        for (int i = 0;
             i < MorningVoteTallies.Count;
             i++)
        {
            MorningVoteTallyData tally =
                MorningVoteTallies[i];

            if (tally.targetClientId !=
                targetClientId)
            {
                continue;
            }

            tally.voteCount++;
            MorningVoteTallies[i] = tally;
            return;
        }
    }

    private void TryShortenMorningVoteAfterAllConfirmed()
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.MorningVote ||
            NetworkManager == null)
        {
            return;
        }

        int alivePlayerCount = 0;

        foreach (KeyValuePair<ulong, PlayerMatchState> pair
                 in playerMatchStates)
        {
            if (!pair.Value.isAlive)
                continue;

            alivePlayerCount++;

            if (!morningVotes.ContainsKey(pair.Key))
                return;
        }

        if (alivePlayerCount == 0)
            return;

        double shortenedEndServerTime =
            NetworkManager.ServerTime.Time +
            MorningVoteAllConfirmedCountdown;

        if (phaseEndServerTime.Value >
            shortenedEndServerTime)
        {
            phaseEndServerTime.Value =
                shortenedEndServerTime;
        }
    }

    private void FinalizeMorningVotes()
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.MorningVote)
        {
            return;
        }

        Dictionary<ulong, ulong> resolvedVotes =
            new Dictionary<ulong, ulong>();

        foreach (KeyValuePair<ulong, PlayerMatchState> pair
                 in playerMatchStates)
        {
            ulong voterClientId = pair.Key;
            PlayerMatchState voterState = pair.Value;

            if (!voterState.isAlive)
                continue;

            ulong targetClientId = AbstainVoteTarget;

            if (!morningVotes.TryGetValue(
                    voterClientId,
                    out targetClientId) &&
                !morningVoteDrafts.TryGetValue(
                    voterClientId,
                    out targetClientId))
            {
                targetClientId = AbstainVoteTarget;
            }

            if (targetClientId != AbstainVoteTarget &&
                (!playerMatchStates.TryGetValue(
                     targetClientId,
                     out PlayerMatchState targetState) ||
                 !targetState.isAlive))
            {
                targetClientId = AbstainVoteTarget;
            }

            resolvedVotes[voterClientId] =
                targetClientId;
        }

        morningVotes.Clear();

        foreach (KeyValuePair<ulong, ulong> vote
                 in resolvedVotes)
        {
            morningVotes[vote.Key] = vote.Value;
        }

        morningVoteDrafts.Clear();
        RebuildMorningVoteTallies();
    }

    private void ResolveMorningVote()
    {
        FinalizeMorningVotes();

        Dictionary<ulong, int> voteCounts =
            new Dictionary<ulong, int>();

        int abstainCount = 0;

        foreach (KeyValuePair<ulong, ulong> vote
                 in morningVotes)
        {
            ulong targetClientId = vote.Value;

            if (targetClientId ==
                AbstainVoteTarget)
            {
                abstainCount++;
                continue;
            }

            if (!voteCounts.ContainsKey(
                    targetClientId))
            {
                voteCounts[targetClientId] = 0;
            }

            voteCounts[targetClientId]++;
        }

        MorningVoteResultData result =
            new MorningVoteResultData
            {
                hasResult = true,
                exiledClientId = NoClientId,
                abstainVoteCount = abstainCount
            };

        if (voteCounts.Count == 0)
        {
            result.abstainPreventedExile =
                abstainCount > 0;

            morningVoteResult.Value = result;

            Debug.Log(
                $"Day {currentDay.Value} 아침 투표 결과 - " +
                $"추방 대상 없음, 기권 {abstainCount}표"
            );

            return;
        }

        ulong selectedClientId = NoClientId;
        int highestVoteCount = 0;
        bool isTie = false;

        foreach (KeyValuePair<ulong, int> voteCount
                 in voteCounts)
        {
            if (voteCount.Value >
                highestVoteCount)
            {
                selectedClientId =
                    voteCount.Key;

                highestVoteCount =
                    voteCount.Value;

                isTie = false;
                continue;
            }

            if (voteCount.Value ==
                highestVoteCount)
            {
                isTie = true;
            }
        }

        if (highestVoteCount <=
            abstainCount)
        {
            result.abstainPreventedExile = true;
            result.isTie = isTie;
            result.voteCount = highestVoteCount;

            morningVoteResult.Value = result;

            Debug.Log(
                $"Day {currentDay.Value} 아침 투표 결과 - " +
                $"기권 우세 또는 동수, 추방 없음. " +
                $"최다 후보 {highestVoteCount}표, " +
                $"기권 {abstainCount}표"
            );

            return;
        }

        if (isTie ||
            selectedClientId == NoClientId)
        {
            result.isTie = true;
            result.voteCount = highestVoteCount;

            morningVoteResult.Value = result;

            Debug.Log(
                $"Day {currentDay.Value} 아침 투표 결과 - " +
                $"후보 동률, 추방 없음"
            );

            return;
        }

        if (!playerMatchStates.TryGetValue(
                selectedClientId,
                out PlayerMatchState selectedState) ||
            !selectedState.isAlive)
        {
            morningVoteResult.Value = result;
            return;
        }

        result.hasExiledPlayer = true;
        result.exiledClientId =
            selectedClientId;

        result.exiledPlayerName =
            selectedState.playerName;

        if (selectedState.role != RoleId.Martyr &&
            LobbyRoomManager.Instance != null &&
            LobbyRoomManager.Instance
                .CurrentSettings.Value
                .revealExiledTeam)
        {
            result.revealedExiledTeam = true;
            result.exiledTeam = selectedState.team;
        }

        result.voteCount =
            highestVoteCount;

        morningVoteResult.Value = result;

        EliminatePlayer(
            selectedClientId,
            true
        );

        if (selectedState.role == RoleId.Martyr)
        {
            pendingFinalDuel = false;
            pendingFinalDuelSerialKillerClientId = NoClientId;
            pendingFinalDuelMafiaClientId = NoClientId;
            pendingWinner = MatchWinner.Martyr;
            pendingIndividualWinnerClientId =
                selectedClientId;
        }

        Debug.Log(
            $"Day {currentDay.Value} 아침 투표 결과 - " +
            $"{selectedState.playerName} 추방, " +
            $"{highestVoteCount}표 / 기권 {abstainCount}표"
        );
    }

    private void EliminatePlayer(
        ulong clientId,
        bool removeBodyAfterDeath = false)
    {
        if (!IsServer)
            return;

        if (!playerMatchStates.TryGetValue(clientId, out PlayerMatchState state) || !state.isAlive)
            return;

        ResetCurseDollForInvalidatedPlayer(
            clientId,
            "사망"
        );

        EndMediumCommunicationForClient(clientId);
        lastTextChatServerTimeByClient.Remove(clientId);

        int droppedWeaponIndex = GetCurrentWeaponIndex(clientId);

        state.isAlive = false;
        playerMatchStates[clientId] = state;

        int publicStateIndex = FindPublicPlayerStateIndex(clientId);

        if (publicStateIndex >= 0)
        {
            PlayerPublicState publicState = PublicPlayerStates[publicStateIndex];
            publicState.isAlive = false;
            PublicPlayerStates[publicStateIndex] = publicState;
        }

        SendLocalMatchState(state);
        SendSpectatorRoleSnapshot(clientId);
        ApplyDeathPresentation(clientId, droppedWeaponIndex, state.role, state.team);

        if (removeBodyAfterDeath)
            RemovePlayerBody(clientId);

        EvaluateVictory();

        Debug.Log($"Player Eliminated - Client: {clientId}, Name: {state.playerName}, WeaponIndex: {droppedWeaponIndex}");
    }

    private void RemovePlayerBody(ulong clientId)
    {
        if (!IsServer ||
            !spawnedBodies.TryGetValue(
                clientId,
                out NetworkObject bodyObject))
        {
            return;
        }

        spawnedBodies.Remove(clientId);

        if (bodyObject == null)
            return;

        if (bodyObject.IsSpawned)
        {
            bodyObject.Despawn(true);
            return;
        }

        Destroy(bodyObject.gameObject);
    }

    private void EvaluateVictory()
    {
        if (!IsServer ||
            !matchStarted ||
            currentPhase.Value == MatchPhase.None ||
            currentPhase.Value == MatchPhase.GameResult)
        {
            return;
        }

        int aliveMafiaCount = 0;
        int aliveCitizenCount = 0;
        int aliveMafiaParityOpponentCount = 0;
        int alivePlayerCount = 0;
        int aliveSerialKillerCount = 0;
        int aliveUnconvertedThiefCount = 0;
        ulong aliveSerialKillerClientId =
            NoClientId;
        ulong aliveMafiaClientId =
            NoClientId;

        foreach (PlayerMatchState state in playerMatchStates.Values)
        {
            if (!state.isAlive)
                continue;

            alivePlayerCount++;

            if (state.team == RoleTeam.Mafia)
            {
                aliveMafiaCount++;
                aliveMafiaClientId = state.clientId;
            }
            else if (state.team == RoleTeam.Citizen)
            {
                aliveCitizenCount++;
                aliveMafiaParityOpponentCount++;
            }

            if (state.team == RoleTeam.Neutral &&
                (state.role == RoleId.Thief ||
                 state.role == RoleId.Martyr))
            {
                aliveMafiaParityOpponentCount++;
            }

            if (state.role == RoleId.SerialKiller)
            {
                aliveSerialKillerCount++;
                aliveSerialKillerClientId =
                    state.clientId;
            }

            if (state.team == RoleTeam.Neutral &&
                state.role == RoleId.Thief)
            {
                aliveUnconvertedThiefCount++;
            }
        }

        MatchWinner resolvedWinner = MatchWinner.None;
        ulong resolvedIndividualWinnerClientId =
            NoClientId;

        bool shouldBeginFinalDuel =
            aliveSerialKillerCount == 1 &&
            aliveMafiaCount == 1 &&
            aliveCitizenCount == 0 &&
            alivePlayerCount == 2;

        if (shouldBeginFinalDuel)
        {
            pendingWinner = MatchWinner.None;
            pendingIndividualWinnerClientId = NoClientId;
            pendingFinalDuel = true;
            pendingFinalDuelSerialKillerClientId =
                aliveSerialKillerClientId;
            pendingFinalDuelMafiaClientId =
                aliveMafiaClientId;

            bool waitForDuelResultPhase =
                currentPhase.Value == MatchPhase.MorningVote ||
                currentPhase.Value == MatchPhase.MorningVoteResult ||
                currentPhase.Value == MatchPhase.NightAction ||
                currentPhase.Value == MatchPhase.NightResult;

            if (!waitForDuelResultPhase &&
                currentPhase.Value != MatchPhase.FinalDuel)
            {
                BeginFinalDuel(
                    aliveSerialKillerClientId,
                    aliveMafiaClientId
                );
            }

            return;
        }

        if (aliveSerialKillerCount > 0)
        {
            bool isSoleSurvivor =
                alivePlayerCount == 1;

            bool isCitizenOneVersusOne =
                aliveSerialKillerCount == 1 &&
                alivePlayerCount == 2 &&
                aliveCitizenCount == 1 &&
                aliveMafiaCount == 0;

            if (aliveSerialKillerCount == 1 &&
                (isSoleSurvivor ||
                 isCitizenOneVersusOne))
            {
                resolvedWinner =
                    MatchWinner.SerialKiller;

                resolvedIndividualWinnerClientId =
                    aliveSerialKillerClientId;
            }
        }
        else if (aliveUnconvertedThiefCount > 0 &&
                 aliveUnconvertedThiefCount ==
                     alivePlayerCount)
        {
            resolvedWinner = MatchWinner.Thief;
        }
        else if (aliveMafiaCount == 0)
        {
            resolvedWinner = MatchWinner.Citizen;
        }
        else if (aliveMafiaCount >=
                 aliveMafiaParityOpponentCount)
        {
            resolvedWinner = MatchWinner.Mafia;
        }

        if (resolvedWinner == MatchWinner.None)
            return;

        pendingFinalDuel = false;
        pendingFinalDuelSerialKillerClientId = NoClientId;
        pendingFinalDuelMafiaClientId = NoClientId;
        pendingWinner = resolvedWinner;
        pendingIndividualWinnerClientId =
            resolvedIndividualWinnerClientId;

        bool waitForResultPhase =
            currentPhase.Value == MatchPhase.MorningVote ||
            currentPhase.Value == MatchPhase.MorningVoteResult ||
            currentPhase.Value == MatchPhase.NightAction ||
            currentPhase.Value == MatchPhase.NightResult;

        if (!waitForResultPhase)
        {
            BeginGameResult(
                resolvedWinner,
                resolvedIndividualWinnerClientId
            );
        }
    }

    private bool TryBeginPendingGameResult()
    {
        if (pendingWinner == MatchWinner.None)
            return false;

        BeginGameResult(
            pendingWinner,
            pendingIndividualWinnerClientId
        );
        return true;
    }

    private bool TryBeginPendingFinalDuel()
    {
        if (!pendingFinalDuel)
            return false;

        BeginFinalDuel(
            pendingFinalDuelSerialKillerClientId,
            pendingFinalDuelMafiaClientId
        );

        return currentPhase.Value == MatchPhase.FinalDuel;
    }

    private void BeginFinalDuel(
        ulong serialKillerClientId,
        ulong mafiaClientId)
    {
        if (!IsServer ||
            currentPhase.Value == MatchPhase.GameResult ||
            currentPhase.Value == MatchPhase.FinalDuel)
        {
            return;
        }

        if (!IsAliveRole(
                serialKillerClientId,
                RoleId.SerialKiller) ||
            !playerMatchStates.TryGetValue(
                mafiaClientId,
                out PlayerMatchState mafiaState) ||
            !mafiaState.isAlive ||
            mafiaState.team != RoleTeam.Mafia)
        {
            pendingFinalDuel = false;
            pendingFinalDuelSerialKillerClientId = NoClientId;
            pendingFinalDuelMafiaClientId = NoClientId;
            EvaluateVictory();
            return;
        }

        pendingWinner = MatchWinner.None;
        pendingIndividualWinnerClientId = NoClientId;
        pendingFinalDuel = false;
        pendingFinalDuelSerialKillerClientId = NoClientId;
        pendingFinalDuelMafiaClientId = NoClientId;
        finalDuelSerialKillerClientId =
            serialKillerClientId;
        finalDuelMafiaClientId = mafiaClientId;
        finalDuelCombatStarted = false;
        finalDuelCombatStartServerTime.Value =
            NetworkManager.ServerTime.Time +
            Mathf.Max(1f, finalDuelPreparationDuration);

        EndAllMediumCommunications();
        ResetCurseDollState();
        mafiaKillerVotes.Clear();
        doctorProtectTargets.Clear();
        drunkardSleepTargets.Clear();
        completedNightRoleActions.Clear();
        currentNightKillerClientId = NoClientId;
        mafiaKillTargetClientId = NoClientId;
        serialKillerTargetClientId = NoClientId;
        currentNightKillMethod = NightKillMethod.None;

        BeginPhase(MatchPhase.FinalDuel);

        Debug.Log(
            $"Final Duel Started - " +
            $"SerialKiller: {serialKillerClientId}, " +
            $"Mafia: {mafiaClientId}"
        );
    }

    public bool IsFinalDuelOpponent(
        ulong attackerClientId,
        ulong targetClientId)
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.FinalDuel ||
            !finalDuelCombatStarted ||
            attackerClientId == targetClientId)
        {
            return false;
        }

        bool validPair =
            attackerClientId == finalDuelSerialKillerClientId &&
            targetClientId == finalDuelMafiaClientId ||
            attackerClientId == finalDuelMafiaClientId &&
            targetClientId == finalDuelSerialKillerClientId;

        return validPair &&
               IsAlivePlayer(attackerClientId) &&
               IsAlivePlayer(targetClientId);
    }

    public void ResolveFinalDuelDefeat(
        ulong defeatedClientId,
        ulong attackerClientId)
    {
        if (!IsFinalDuelOpponent(
                attackerClientId,
                defeatedClientId))
        {
            return;
        }

        EliminatePlayer(defeatedClientId);
    }

    private void BeginGameResult(
        MatchWinner winner,
        ulong individualWinnerClientId)
    {
        if (!IsServer ||
            winner == MatchWinner.None ||
            currentPhase.Value == MatchPhase.GameResult)
        {
            return;
        }

        pendingWinner = MatchWinner.None;
        pendingIndividualWinnerClientId = NoClientId;
        pendingFinalDuel = false;
        pendingFinalDuelSerialKillerClientId = NoClientId;
        pendingFinalDuelMafiaClientId = NoClientId;
        finalDuelSerialKillerClientId = NoClientId;
        finalDuelMafiaClientId = NoClientId;
        finalDuelCombatStarted = false;
        finalDuelCombatStartServerTime.Value = 0d;
        PopulateGameResultPlayerStates();
        gameResultMafiaNames.Value =
            new FixedString512Bytes(
                LimitUtf8Text(
                    BuildGameResultMafiaNames(),
                    500
                )
            );

        gameResultThiefWinnerNames.Value =
            winner == MatchWinner.Thief
                ? new FixedString512Bytes(
                    LimitUtf8Text(
                        BuildGameResultThiefWinnerNames(),
                        500
                    )
                )
                : default;

        gameResultIndividualWinnerClientId.Value =
            individualWinnerClientId;

        matchWinner.Value = winner;

        BeginPhase(MatchPhase.GameResult);

        Debug.Log($"Game Result - Winner: {winner}");
    }

    private void PopulateGameResultPlayerStates()
    {
        if (!IsServer || GameResultPlayerStates == null)
            return;

        GameResultPlayerStates.Clear();

        List<PlayerMatchState> finalStates =
            new List<PlayerMatchState>(
                playerMatchStates.Values
            );

        finalStates.Sort(
            (left, right) =>
            {
                int nameComparison = string.Compare(
                    left.playerName.ToString(),
                    right.playerName.ToString(),
                    StringComparison.Ordinal
                );

                return nameComparison != 0
                    ? nameComparison
                    : left.clientId.CompareTo(right.clientId);
            }
        );

        for (int i = 0; i < finalStates.Count; i++)
            GameResultPlayerStates.Add(finalStates[i]);
    }

    private string BuildGameResultMafiaNames()
    {
        List<string> mafiaNames = new List<string>();

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (state.team != RoleTeam.Mafia)
                continue;

            mafiaNames.Add(
                state.playerName.ToString()
            );
        }

        mafiaNames.Sort(StringComparer.Ordinal);

        return mafiaNames.Count > 0
            ? string.Join(", ", mafiaNames)
            : "없음";
    }

    private string BuildGameResultThiefWinnerNames()
    {
        List<string> thiefNames = new List<string>();

        foreach (PlayerMatchState state in
                 playerMatchStates.Values)
        {
            if (!state.isAlive ||
                state.team != RoleTeam.Neutral ||
                state.role != RoleId.Thief)
            {
                continue;
            }

            thiefNames.Add(
                state.playerName.ToString()
            );
        }

        thiefNames.Sort(StringComparer.Ordinal);
        return string.Join(", ", thiefNames);
    }

    private static string LimitUtf8Text(
        string value,
        int maximumByteCount)
    {
        if (string.IsNullOrEmpty(value) ||
            maximumByteCount <= 0)
        {
            return string.Empty;
        }

        while (value.Length > 0 &&
               Encoding.UTF8.GetByteCount(value) >
               maximumByteCount)
        {
            value = value.Substring(
                0,
                value.Length - 1
            );
        }

        return value;
    }

    public void PrepareForLobbyReturn()
    {
        if (!IsServer || matchCleanupStarted)
            return;

        matchCleanupStarted = true;
        matchStarted = false;

        StopAllCoroutines();
        SetAllSpiritPhaseControl(false);
        DespawnAllMatchNetworkObjects();

        Debug.Log("Match cleanup completed before returning to lobby.");
    }

    public bool RequestReturnToLobby()
    {
        if (!IsHost ||
            !IsSpawned ||
            currentPhase.Value != MatchPhase.GameResult)
        {
            return false;
        }

        if (LobbyRoomManager.Instance == null)
        {
            Debug.LogError(
                "로비로 돌아갈 LobbyRoomManager가 없습니다."
            );

            return false;
        }

        return LobbyRoomManager.Instance
            .ReturnToLobbyFromGame();
    }

    public bool RequestAbandonMatchAndReturnToLobby()
    {
        if (!IsHost ||
            !IsSpawned ||
            currentPhase.Value == MatchPhase.None)
        {
            return false;
        }

        if (LobbyRoomManager.Instance == null ||
            !LobbyRoomManager.Instance.IsSpawned)
        {
            return false;
        }

        return LobbyRoomManager.Instance
            .ReturnToLobbyFromGame();
    }

    private void ApplyDeathPresentation(
        ulong clientId,
        int droppedWeaponIndex,
        RoleId sourceRole,
        RoleTeam sourceTeam)
    {
        int homeHouseId =
            GetOwnedHouseId(clientId);

        House homeHouse =
            GetHouse(homeHouseId);

        if (homeHouse == null ||
            homeHouse.SpiritPoint == null)
        {
            Debug.LogError(
                $"Client {clientId}의 사망 연출 위치를 찾지 못했습니다."
            );

            return;
        }

        if (TryGetPlayerBody(
                clientId,
                out PlayerBody playerBody))
        {
            PlayerBodyDeathView deathView =
                playerBody.GetComponent
                    <PlayerBodyDeathView>();

            if (deathView != null)
            {
                Vector3 deathPosition =
                    homeHouse.SpiritPoint.position;

                Quaternion deathRotation =
                    homeHouse.SpiritPoint.rotation;

                if (activeDrunkardSleepHouseByClient
                    .TryGetValue(
                        clientId,
                        out int sleepHouseId))
                {
                    House sleepHouse =
                        GetHouse(sleepHouseId);

                    Transform drunkardDeathPoint =
                        sleepHouse != null
                            ? sleepHouse.DrunkardDeathPoint
                            : null;

                    if (drunkardDeathPoint != null)
                    {
                        deathPosition =
                            drunkardDeathPoint.position;

                        deathRotation =
                            drunkardDeathPoint.rotation;
                    }
                    else
                    {
                        deathPosition =
                            playerBody.transform.position;

                        deathRotation =
                            playerBody.transform.rotation;

                        Debug.LogWarning(
                            $"주정뱅이 사망 포인트가 연결되지 않아 현재 육체 위치를 사용합니다. " +
                            $"Client: {clientId}, House: {sleepHouseId}"
                        );
                    }
                }

                deathView.ApplyDeath(
                    deathPosition,
                    deathRotation
                );

                if (droppedWeaponIndex >= 0)
                {
                    SpawnDroppedRoleTool(
                        droppedWeaponIndex,
                        clientId,
                        sourceRole,
                        sourceTeam,
                        deathView.ToolDropPosition,
                        deathView.ToolDropRotation
                    );
                }
            }
            else
            {
                Debug.LogError(
                    $"Client {clientId}의 몸에 " +
                    "PlayerBodyDeathView가 없습니다."
                );
            }
        }

        if (TryGetPlayerSpirit(
                clientId,
                out PlayerSpirit deadSpirit))
        {
            int releaseDay =
                GetDeadSpectatorReleaseDay();

            bool enableMovementNow =
                currentPhase.Value ==
                MatchPhase.NightAction;

            deadSpirit.EnterDeadSpectator(
                homeHouse.SpiritPoint.position,
                homeHouse.SpiritPoint.rotation,
                releaseDay,
                enableMovementNow
            );
        }

        RefreshNightIdentityRevealStates();
    }

    private int GetDeadSpectatorReleaseDay()
    {
        bool diedDuringNight =
            currentPhase.Value == MatchPhase.NightAction ||
            currentPhase.Value == MatchPhase.NightResult;

        return currentDay.Value +
               (diedDuringNight ? 2 : 1);
    }

    private void SpawnDroppedRoleTool(
        int weaponIndex,
        ulong previousOwnerClientId,
        RoleId sourceRole,
        RoleTeam sourceTeam,
        Vector3 position,
        Quaternion rotation)
    {
        if (droppedRoleToolPrefab == null)
        {
            Debug.LogError("Dropped Role Tool Prefab이 연결되지 않았습니다.");
            return;
        }

        GameObject toolObject =
            Instantiate(droppedRoleToolPrefab, position, rotation);

        NetworkObject networkObject =
            toolObject.GetComponent<NetworkObject>();

        DroppedRoleTool droppedRoleTool =
            toolObject.GetComponent<DroppedRoleTool>();

        if (networkObject == null || droppedRoleTool == null)
        {
            Debug.LogError(
                "DroppedRoleTool 프리팹에 NetworkObject 또는 DroppedRoleTool이 없습니다."
            );

            Destroy(toolObject);
            return;
        }

        networkObject.Spawn(true);
        spawnedDroppedRoleTools.Add(networkObject);

        droppedRoleTool.Initialize(
            weaponIndex,
            previousOwnerClientId,
            sourceRole,
            sourceTeam
        );
    }

    private int FindPublicPlayerStateIndex(ulong clientId)
    {
        for (int i = 0; i < PublicPlayerStates.Count; i++)
        {
            if (PublicPlayerStates[i].clientId == clientId)
                return i;
        }

        return -1;
    }

    private void OnCurrentPhaseChanged(MatchPhase previous, MatchPhase current)
    {
        if (current != MatchPhase.NightPreparation &&
            localMafiaKillerVotes.Count > 0)
        {
            localMafiaKillerVotes.Clear();
            LocalMafiaKillerVotesChanged?.Invoke();
        }

        RefreshAllHouseOwnerPresentationsLocal();
        PhaseChanged?.Invoke(previous, current);
        Debug.Log($"Phase Changed - {previous} -> {current}");
    }

    private void OnRoleAssignmentProgressValueChanged(
        int previous,
        int current)
    {
        RoleAssignmentProgressChanged?.Invoke();
    }

    private void OnCurrentDayChanged(int previous, int current)
    {
        DayChanged?.Invoke(previous, current);
        Debug.Log($"Day Changed - {previous} -> {current}");
    }

    private void OnMorningVoteTalliesChanged(
        NetworkListEvent<MorningVoteTallyData>
            changeEvent)
    {
        MorningVoteTalliesChanged?.Invoke();
    }

    private void OnMorningAbstainVoteCountChanged(
        int previous,
        int current)
    {
        MorningVoteTalliesChanged?.Invoke();
    }

    private void OnMorningVoteResultChanged(MorningVoteResultData previous, MorningVoteResultData current)
    {
        MorningVoteResultChanged?.Invoke(previous, current);
    }

    private void OnNightResultChanged(NightResultData previous, NightResultData current)
    {
        NightResultChanged?.Invoke(previous, current);
    }

    private void OnLastNightDeathWasPoisonedChanged(
        bool previous,
        bool current)
    {
        LastNightDeathWasPoisonedChanged?.Invoke(previous, current);
    }

    private void OnPublicPlayerStatesChanged(
        NetworkListEvent<PlayerPublicState>
            changeEvent)
    {
        ApplyAllHouseOwnerLifeStates();
        RefreshAllHouseOwnerPresentationsLocal();
        PublicPlayerStatesChanged?.Invoke();
    }

    public bool TryGetLocalPlayerMatchState(out PlayerMatchState state)
    {
        state = localPlayerMatchState;
        return hasLocalPlayerMatchState;
    }

    public bool TryGetPlayerMatchState(ulong clientId, out PlayerMatchState state)
    {
        if (IsServer)
            return playerMatchStates.TryGetValue(clientId, out state);

        if (hasLocalPlayerMatchState && localPlayerMatchState.clientId == clientId)
        {
            state = localPlayerMatchState;
            return true;
        }

        state = default;
        return false;
    }

    public bool TryGetLocalSpectatorPlayerMatchState(
        ulong clientId,
        out PlayerMatchState state)
    {
        state = default;

        if (!hasLocalPlayerMatchState ||
            localPlayerMatchState.isAlive)
        {
            return false;
        }

        return localSpectatorPlayerStates.TryGetValue(
            clientId,
            out state
        );
    }

    private bool ValidateHousePoints(House house)
    {
        if (house.BodyPoint == null)
        {
            Debug.LogError($"House {house.Id}의 BodyPoint가 없습니다.");
            return false;
        }

        if (house.SpiritPoint == null)
        {
            Debug.LogError($"House {house.Id}의 SpiritPoint가 없습니다.");
            return false;
        }

        return true;
    }

    private NetworkObject SpawnBody(ulong clientId, House house)
    {
        if (playerBodyPrefab == null)
        {
            Debug.LogError("PlayerBody Prefab이 연결되지 않았습니다.");
            return null;
        }

        GameObject bodyObject = Instantiate(playerBodyPrefab, house.BodyPoint.position, house.BodyPoint.rotation);

        NetworkObject networkObject = bodyObject.GetComponent<NetworkObject>();
        PlayerBody playerBody = bodyObject.GetComponent<PlayerBody>();
        RoleActionTarget roleActionTarget = bodyObject.GetComponent<RoleActionTarget>();

        if (networkObject == null || playerBody == null || roleActionTarget == null)
        {
            Debug.LogError("PlayerBody 프리팹에 NetworkObject, PlayerBody 또는 RoleActionTarget이 없습니다.");
            Destroy(bodyObject);
            return null;
        }

        if (roleActionTarget.TargetType != RoleActionTargetType.Body)
        {
            Debug.LogError("PlayerBody 프리팹의 RoleActionTarget 타입이 Body가 아닙니다.");
            Destroy(bodyObject);
            return null;
        }

        networkObject.Spawn(true);

        playerBody.Initialize(clientId, house.Id);
        roleActionTarget.Initialize(clientId);

        spawnedBodies[clientId] = networkObject;

        return networkObject;
    }

    private NetworkObject SpawnSpirit(ulong clientId, House house)
    {
        if (playerSpiritPrefab == null)
        {
            Debug.LogError("PlayerSpirit Prefab이 연결되지 않았습니다.");
            return null;
        }

        GameObject spiritObject = Instantiate(playerSpiritPrefab, house.SpiritPoint.position, house.SpiritPoint.rotation);

        NetworkObject networkObject = spiritObject.GetComponent<NetworkObject>();
        PlayerSpirit playerSpirit = spiritObject.GetComponent<PlayerSpirit>();
        RoleActionTarget roleActionTarget = spiritObject.GetComponent<RoleActionTarget>();

        if (networkObject == null || playerSpirit == null || roleActionTarget == null)
        {
            Debug.LogError("PlayerSpirit 프리팹에 NetworkObject, PlayerSpirit 또는 RoleActionTarget이 없습니다.");
            Destroy(spiritObject);
            return null;
        }

        if (roleActionTarget.TargetType != RoleActionTargetType.Spirit)
        {
            Debug.LogError("PlayerSpirit 프리팹의 RoleActionTarget 타입이 Spirit이 아닙니다.");
            Destroy(spiritObject);
            return null;
        }

        networkObject.SpawnWithOwnership(clientId, true);

        playerSpirit.Initialize(clientId, house.Id);
        roleActionTarget.Initialize(clientId);

        spawnedSpirits[clientId] = networkObject;

        return networkObject;
    }

    private void DespawnAllMatchNetworkObjects()
    {
        if (!IsServer ||
            NetworkManager == null ||
            !NetworkManager.IsListening)
        {
            spawnedBodies.Clear();
            spawnedSpirits.Clear();
            spawnedDroppedRoleTools.Clear();
            return;
        }

        foreach (NetworkObject body in
                 new List<NetworkObject>(
                     spawnedBodies.Values))
        {
            DespawnMatchNetworkObject(body);
        }

        foreach (NetworkObject spirit in
                 new List<NetworkObject>(
                     spawnedSpirits.Values))
        {
            DespawnMatchNetworkObject(spirit);
        }

        foreach (NetworkObject droppedTool in
                 new List<NetworkObject>(
                     spawnedDroppedRoleTools))
        {
            DespawnMatchNetworkObject(droppedTool);
        }

        spawnedBodies.Clear();
        spawnedSpirits.Clear();
        spawnedDroppedRoleTools.Clear();
    }

    private static void DespawnMatchNetworkObject(
        NetworkObject networkObject)
    {
        if (networkObject == null)
            return;

        if (networkObject.IsSpawned)
        {
            networkObject.Despawn(true);
            return;
        }

        Destroy(networkObject.gameObject);
    }

    private void UpdateSpiritHouseLocations()
    {
        foreach (KeyValuePair<ulong, NetworkObject> pair in spawnedSpirits)
        {
            NetworkObject spiritObject = pair.Value;

            if (spiritObject == null || !spiritObject.IsSpawned)
                continue;

            PlayerSpirit playerSpirit =
                spiritObject.GetComponent<PlayerSpirit>();

            if (playerSpirit == null)
                continue;

            House currentHouse =
                houseRegistry.GetHouseContainingPosition(
                    spiritObject.transform.position
                );

            int currentHouseId =
                currentHouse != null
                    ? currentHouse.Id
                    : -1;

            playerSpirit.SetCurrentHouse(currentHouseId);

            if (currentPhase.Value != MatchPhase.NightAction)
                continue;

            if (!playerMatchStates.TryGetValue(
                    pair.Key,
                    out PlayerMatchState state) ||
                !state.isAlive)
            {
                continue;
            }

            if (currentHouseId < 0 ||
                currentHouseId == playerSpirit.HomeHouseId)
            {
                continue;
            }

            RegisterNightVisitor(
                currentHouseId,
                pair.Key
            );
        }

        RefreshNightIdentityRevealStates();
    }

    private void RegisterNightVisitor(int houseId, ulong visitorClientId)
    {
        if (!IsServer || houseId < 0)
            return;

        if (!nightVisitorsByHouse.TryGetValue(houseId, out HashSet<ulong> visitors))
        {
            visitors = new HashSet<ulong>();
            nightVisitorsByHouse[houseId] = visitors;
        }

        visitors.Add(visitorClientId);
    }

    public void RegisterNightBloodEvidence(int houseId, ulong damagedClientId)
    {
        if (!IsServer || currentPhase.Value != MatchPhase.NightAction || houseId < 0)
            return;

        if (!nightBloodPlayersByHouse.TryGetValue(houseId, out HashSet<ulong> bloodPlayers))
        {
            bloodPlayers = new HashSet<ulong>();
            nightBloodPlayersByHouse[houseId] = bloodPlayers;
        }

        bloodPlayers.Add(damagedClientId);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer)
            return;

        bool disconnectedDuringRoleAssignment =
            waitingForRoleAssignmentConfirmations &&
            expectedRoleAssignmentClientIds.Contains(
                clientId
            );

        ResetCurseDollForInvalidatedPlayer(
            clientId,
            "연결 해제"
        );

        completedNightRoleActions.Remove(clientId);
        consumedExorcismPlayers.Remove(clientId);

        EndMediumCommunicationForClient(clientId);
        lastTextChatServerTimeByClient.Remove(clientId);

        RemoveClientFromCurrentNightEvidence(
            nightVisitorsByHouse,
            clientId
        );

        RemoveClientFromCurrentNightEvidence(
            nightBloodPlayersByHouse,
            clientId
        );

        forensicsIntruderCountByVictim.Remove(clientId);
        forensicsDamagedPlayerNamesByVictim.Remove(clientId);
        forensicsDeathEvidenceByVictim.Remove(clientId);

        drunkardSleepTargets.Remove(clientId);
        activeDrunkardSleepHouseByClient.Remove(clientId);

        List<ulong> drunkardSelectionsToRemove =
            new List<ulong>();

        foreach (KeyValuePair<ulong, ulong> selection in
                 drunkardSleepTargets)
        {
            if (selection.Value == clientId)
                drunkardSelectionsToRemove.Add(selection.Key);
        }

        for (int i = 0;
             i < drunkardSelectionsToRemove.Count;
             i++)
        {
            drunkardSleepTargets.Remove(
                drunkardSelectionsToRemove[i]
            );
        }

        List<ulong> hunterRecordsToRemove =
            new List<ulong>();

        foreach (KeyValuePair<ulong, HunterRouteRecord> route in
                 hunterRouteRecordsByHunter)
        {
            if (route.Key == clientId ||
                route.Value.targetClientId == clientId)
            {
                hunterRecordsToRemove.Add(route.Key);
            }
        }

        for (int i = 0;
             i < hunterRecordsToRemove.Count;
             i++)
        {
            hunterRouteRecordsByHunter.Remove(
                hunterRecordsToRemove[i]
            );
        }

        playerMatchStates.Remove(clientId);
        mafiaDisguiseWeaponByClient.Remove(clientId);
        ownedMafiaDisguiseWeaponsByClient.Remove(
            clientId
        );
        spyCitizenAbilityUseCountByClient.Remove(
            clientId
        );
        peddlerRealActionByClient.Remove(
            clientId
        );
        nightDutyAssignmentsByClient.Remove(clientId);
        mafiaInterferenceAssignmentsByClient.Remove(clientId);
        fakeNightDutyUseCountByClient.Remove(clientId);
        RemoveVotesForClient(clientId);

        int publicStateIndex =
            FindPublicPlayerStateIndex(clientId);

        if (publicStateIndex >= 0)
            PublicPlayerStates.RemoveAt(publicStateIndex);

        if (spawnedBodies.TryGetValue(
                clientId,
                out NetworkObject body))
        {
            if (body != null && body.IsSpawned)
                body.Despawn(true);

            spawnedBodies.Remove(clientId);
        }

        if (spawnedSpirits.TryGetValue(
                clientId,
                out NetworkObject spirit))
        {
            if (spirit != null && spirit.IsSpawned)
                spirit.Despawn(true);

            spawnedSpirits.Remove(clientId);
        }

        expectedRoleAssignmentClientIds.Remove(clientId);
        confirmedRoleAssignmentClientIds.Remove(clientId);
        roleAssignmentReadyCount.Value =
            confirmedRoleAssignmentClientIds.Count;
        roleAssignmentExpectedCount.Value =
            expectedRoleAssignmentClientIds.Count;

        if (disconnectedDuringRoleAssignment)
        {
            BeginRoleAssignmentFailureReturn(
                "플레이어 연결이 끊겨 " +
                "게임 시작을 취소했습니다."
            );
        }
        else
        {
            TryStartPhaseAfterRoleAssignmentConfirmations();
        }

        if (!disconnectedDuringRoleAssignment)
            EvaluateVictory();

        if (houseRegistry == null)
            return;

        foreach (House house in houseRegistry.Houses)
        {
            if (house == null ||
                house.OwnerClientId != clientId)
            {
                continue;
            }

            int clearedHouseId = house.Id;

            ClearHouseOwnerDisplayLocal(clearedHouseId);

            ClearHouseOwnerRpc(clearedHouseId);

            break;
        }
    }

    private void RemoveClientFromCurrentNightEvidence(
        Dictionary<int, HashSet<ulong>> evidenceByHouse,
        ulong clientId)
    {
        foreach (HashSet<ulong> clients in evidenceByHouse.Values)
            clients.Remove(clientId);
    }

    private void RemoveVotesForClient(ulong clientId)
    {
        List<ulong> morningVotersToRemove = new List<ulong>();
        List<ulong> morningDraftVotersToRemove = new List<ulong>();
        List<ulong> mafiaVotersToRemove = new List<ulong>();
        List<ulong> doctorsToRemove = new List<ulong>();

        foreach (KeyValuePair<ulong, ulong> vote in morningVotes)
        {
            if (vote.Key == clientId || vote.Value == clientId)
                morningVotersToRemove.Add(vote.Key);
        }

        foreach (KeyValuePair<ulong, ulong> vote in morningVoteDrafts)
        {
            if (vote.Key == clientId || vote.Value == clientId)
                morningDraftVotersToRemove.Add(vote.Key);
        }

        foreach (KeyValuePair<ulong, ulong> vote in mafiaKillerVotes)
        {
            if (vote.Key == clientId || vote.Value == clientId)
                mafiaVotersToRemove.Add(vote.Key);
        }

        foreach (KeyValuePair<ulong, ulong> protection in doctorProtectTargets)
        {
            if (protection.Key == clientId ||
                protection.Value == clientId)
            {
                doctorsToRemove.Add(protection.Key);
            }
        }

        for (int i = 0; i < morningVotersToRemove.Count; i++)
            morningVotes.Remove(morningVotersToRemove[i]);

        for (int i = 0; i < morningDraftVotersToRemove.Count; i++)
            morningVoteDrafts.Remove(morningDraftVotersToRemove[i]);

        for (int i = 0; i < mafiaVotersToRemove.Count; i++)
            mafiaKillerVotes.Remove(mafiaVotersToRemove[i]);

        if (mafiaVotersToRemove.Count > 0)
            SendMafiaKillerVoteSnapshotToMafia();

        for (int i = 0; i < doctorsToRemove.Count; i++)
            doctorProtectTargets.Remove(doctorsToRemove[i]);

        if (currentNightKillerClientId == clientId)
        {
            currentNightKillerClientId = NoClientId;
            SendCurrentNightKillerToMafia();
        }

        if (mafiaKillTargetClientId == clientId)
            mafiaKillTargetClientId = NoClientId;

        RebuildMorningVoteTallies();
        TryShortenMorningVoteAfterAllConfirmed();
    }

    public int HouseCount => houseRegistry != null ? houseRegistry.HouseCount : 0;

    public House GetHouse(int houseId)
    {
        return houseRegistry != null ? houseRegistry.GetHouse(houseId) : null;
    }

    public bool TryGetPlayerBody(ulong clientId, out PlayerBody playerBody)
    {
        playerBody = null;

        if (!spawnedBodies.TryGetValue(clientId, out NetworkObject body) || body == null)
            return false;

        playerBody = body.GetComponent<PlayerBody>();

        return playerBody != null;
    }

    public bool TryGetPlayerSpirit(
        ulong clientId,
        out PlayerSpirit playerSpirit)
    {
        playerSpirit = null;

        if (spawnedSpirits.TryGetValue(
                clientId,
                out NetworkObject spiritObject) &&
            spiritObject != null)
        {
            playerSpirit =
                spiritObject.GetComponent<PlayerSpirit>();

            if (playerSpirit != null)
                return true;
        }

        PlayerSpirit[] spirits =
            FindObjectsByType<PlayerSpirit>(
                FindObjectsSortMode.None
            );

        for (int i = 0; i < spirits.Length; i++)
        {
            PlayerSpirit candidate = spirits[i];

            if (candidate == null ||
                !candidate.IsSpawned ||
                candidate.LinkedClientId != clientId)
            {
                continue;
            }

            playerSpirit = candidate;
            spawnedSpirits[clientId] =
                candidate.NetworkObject;

            return true;
        }

        return false;
    }

    public void SetMorningVoteDraft(ulong targetClientId)
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.MorningVote)
        {
            return;
        }

        SetMorningVoteDraftRpc(targetClientId);
    }

    public void SetMorningAbstainDraft()
    {
        if (!IsClient ||
            currentPhase.Value != MatchPhase.MorningVote)
        {
            return;
        }

        SetMorningVoteDraftRpc(AbstainVoteTarget);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SetMorningVoteDraftRpc(
        ulong targetClientId,
        RpcParams rpcParams = default)
    {
        if (!IsServer ||
            currentPhase.Value != MatchPhase.MorningVote)
        {
            return;
        }

        ulong voterClientId =
            rpcParams.Receive.SenderClientId;

        if (!playerMatchStates.TryGetValue(
                voterClientId,
                out PlayerMatchState voterState) ||
            !voterState.isAlive ||
            morningVotes.ContainsKey(voterClientId))
        {
            return;
        }

        if (targetClientId != AbstainVoteTarget &&
            (!playerMatchStates.TryGetValue(
                 targetClientId,
                 out PlayerMatchState targetState) ||
             !targetState.isAlive))
        {
            return;
        }

        morningVoteDrafts[voterClientId] =
            targetClientId;

        Debug.Log(
            targetClientId == AbstainVoteTarget
                ? $"Morning Vote Draft - Voter: {voterClientId}, Target: Abstain"
                : $"Morning Vote Draft - Voter: {voterClientId}, Target: {targetClientId}"
        );
    }

    public void SubmitMorningVote(ulong targetClientId)
    {
        if (!IsClient || currentPhase.Value != MatchPhase.MorningVote)
            return;

        SubmitMorningVoteRpc(targetClientId);
    }

    public void SubmitMorningAbstain()
    {
        if (!IsClient || currentPhase.Value != MatchPhase.MorningVote)
            return;

        SubmitMorningVoteRpc(AbstainVoteTarget);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitMorningVoteRpc(ulong targetClientId, RpcParams rpcParams = default)
    {
        if (!IsServer || currentPhase.Value != MatchPhase.MorningVote)
            return;

        ulong voterClientId = rpcParams.Receive.SenderClientId;

        if (!playerMatchStates.TryGetValue(voterClientId, out PlayerMatchState voterState) || !voterState.isAlive)
            return;

        if (morningVotes.ContainsKey(
                voterClientId))
        {
            return;
        }

        if (targetClientId != AbstainVoteTarget)
        {
            if (!playerMatchStates.TryGetValue(targetClientId, out PlayerMatchState targetState) || !targetState.isAlive)
                return;
        }

        morningVoteDrafts[voterClientId] =
            targetClientId;

        morningVotes.Add(
            voterClientId,
            targetClientId
        );

        if (targetClientId ==
            AbstainVoteTarget)
        {
            morningAbstainVoteCount.Value++;
        }
        else
        {
            IncrementMorningVoteTally(
                targetClientId
            );
        }

        Debug.Log(targetClientId == AbstainVoteTarget
            ? $"Morning Vote - Voter: {voterClientId}, Target: Abstain"
            : $"Morning Vote - Voter: {voterClientId}, Target: {targetClientId}");

        TryShortenMorningVoteAfterAllConfirmed();
    }

    public bool TryGetLocalNightRoleAction(
        out RoleActionType actionType,
        out RoleActionTargetType targetType,
        out RoleActionExecutionType executionType)
    {
        actionType = RoleActionType.None;
        targetType = RoleActionTargetType.Body;
        executionType = RoleActionExecutionType.None;

        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction ||
            !hasLocalPlayerMatchState ||
            !localPlayerMatchState.isAlive)
        {
            return false;
        }

        if (localPlayerMatchState.role ==
            RoleId.SerialKiller)
        {
            actionType = RoleActionType.MafiaKill;
            targetType = RoleActionTargetType.Body;
            executionType =
                RoleActionExecutionType.Real;

            return true;
        }

        if (localPlayerMatchState.role == RoleId.CurseCaster &&
            IsLocalNightKiller)
        {
            if (curseDollState.Value.location !=
                CurseDollLocation.None)
            {
                return false;
            }

            actionType = RoleActionType.CurseDoll;
            targetType = RoleActionTargetType.Body;
            executionType =
                RoleActionExecutionType.Real;

            return true;
        }

        if (localPlayerMatchState.role == RoleId.Alchemist &&
            IsLocalNightKiller)
        {
            actionType =
                RoleActionType.AlchemistPoison;

            targetType =
                RoleActionTargetType.BodyOrFrontDoor;

            executionType =
                RoleActionExecutionType.Real;

            return true;
        }

        if (localPlayerMatchState.team == RoleTeam.Mafia &&
            IsLocalNightKiller)
        {
            actionType = RoleActionType.MafiaKill;
            targetType = RoleActionTargetType.Body;
            executionType = RoleActionExecutionType.Real;
            return true;
        }

        if (localPlayerMatchState.role == RoleId.Spy)
        {
            return TryGetLocalSpyCitizenNightRoleAction(
                out actionType,
                out targetType,
                out executionType
            );
        }

        if (localPlayerMatchState.role == RoleId.Peddler)
        {
            int weaponIndex = GetCurrentWeaponIndex(
                NetworkManager.LocalClientId
            );

            if (weaponIndex < 0 ||
                !TryGetCitizenActionFromWeapon(
                    (RoleWeaponId)weaponIndex,
                    out actionType,
                    out targetType))
            {
                return false;
            }

            /*
             * 클라이언트에는 실제 진위를 보내지 않는다.
             * 기존 주민 행동 RPC로 요청하고 서버가
             * 진짜/가짜 효과를 최종 판정한다.
             */
            executionType =
                RoleActionExecutionType.Real;

            return true;
        }

        if (localPlayerMatchState.team == RoleTeam.Mafia)
        {
            int weaponIndex =
                GetCurrentWeaponIndex(
                    NetworkManager.LocalClientId
                );

            if (weaponIndex < 0 ||
                !TryGetCitizenActionFromWeapon(
                    (RoleWeaponId)weaponIndex,
                    out actionType,
                    out targetType))
            {
                return false;
            }

            executionType =
                RoleActionExecutionType.Fake;

            return true;
        }

        if (!TryGetCitizenActionForRole(
                localPlayerMatchState.role,
                out actionType,
                out targetType))
        {
            return false;
        }

        executionType = RoleActionExecutionType.Real;
        return true;
    }

    public bool TryGetLocalSpyCitizenNightRoleAction(
        out RoleActionType actionType,
        out RoleActionTargetType targetType,
        out RoleActionExecutionType executionType)
    {
        actionType = RoleActionType.None;
        targetType = RoleActionTargetType.Body;
        executionType = RoleActionExecutionType.None;

        if (!IsClient ||
            currentPhase.Value != MatchPhase.NightAction ||
            !hasLocalPlayerMatchState ||
            !localPlayerMatchState.isAlive ||
            localPlayerMatchState.role != RoleId.Spy ||
            localSpyCitizenAbilityUseCount >=
                MaximumSpyCitizenAbilityUses)
        {
            return false;
        }

        int weaponIndex = GetCurrentWeaponIndex(
            NetworkManager.LocalClientId
        );

        if (weaponIndex < 0 ||
            !TryGetCitizenActionFromWeapon(
                (RoleWeaponId)weaponIndex,
                out actionType,
                out targetType))
        {
            return false;
        }

        executionType = RoleActionExecutionType.Real;
        return true;
    }

    private bool TryGetCitizenActionFromWeapon(
        RoleWeaponId weaponId,
        out RoleActionType actionType,
        out RoleActionTargetType targetType)
    {
        RoleId citizenRole =
            GetCitizenRoleFromWeapon(weaponId);

        return TryGetCitizenActionForRole(
            citizenRole,
            out actionType,
            out targetType
        );
    }

    private bool TryGetCitizenActionForRole(
        RoleId role,
        out RoleActionType actionType,
        out RoleActionTargetType targetType)
    {
        actionType = RoleActionType.None;
        targetType = RoleActionTargetType.Body;

        switch (role)
        {
            case RoleId.Doctor:
                actionType = RoleActionType.DoctorProtect;
                return true;

            case RoleId.Bailiff:
                actionType = RoleActionType.BailiffConfine;
                return true;

            case RoleId.Exorcist:
                actionType = RoleActionType.Exorcism;
                targetType = RoleActionTargetType.Spirit;
                return true;

            case RoleId.Detective:
                actionType = RoleActionType.DetectiveInspect;
                return true;

            case RoleId.Forensics:
                actionType = RoleActionType.ForensicsInspect;
                return true;

            case RoleId.Medium:
                actionType = RoleActionType.MediumCommune;
                return true;

            case RoleId.Hunter:
                actionType = RoleActionType.HunterTrack;
                targetType = RoleActionTargetType.FrontDoor;
                return true;

            case RoleId.Undertaker:
                actionType = RoleActionType.UndertakerSeal;
                targetType = RoleActionTargetType.FrontDoor;
                return true;

            default:
                return false;
        }
    }

    public bool IsPublicPlayerAlive(ulong clientId)
    {
        if (PublicPlayerStates == null)
            return false;

        for (int i = 0; i < PublicPlayerStates.Count; i++)
        {
            PlayerPublicState state = PublicPlayerStates[i];

            if (state.clientId == clientId)
                return state.isAlive;
        }

        return false;
    }

    public bool TryGetPublicPlayerName(
        ulong clientId,
        out string playerName)
    {
        playerName = string.Empty;

        if (PublicPlayerStates == null)
            return false;

        for (int i = 0;
             i < PublicPlayerStates.Count;
             i++)
        {
            PlayerPublicState state =
                PublicPlayerStates[i];

            if (state.clientId != clientId)
                continue;

            playerName =
                state.playerName.ToString();

            return !string.IsNullOrWhiteSpace(
                playerName
            );
        }

        return false;
    }

    public bool IsLocalMafiaMember(ulong clientId)
    {
        for (int i = 0; i < localMafiaMembers.Count; i++)
        {
            if (localMafiaMembers[i].clientId == clientId)
                return true;
        }

        return false;
    }

    private bool CanUseCitizenAction(
        ulong clientId,
        RoleId requiredRole)
    {
        if (!playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state) ||
            !state.isAlive)
        {
            return false;
        }

        if (state.role == requiredRole)
            return true;

        if (state.role != RoleId.Spy &&
            state.role != RoleId.Peddler)
        {
            return false;
        }

        int weaponIndex =
            GetCurrentWeaponIndex(clientId);

        if (weaponIndex < 0)
            return false;

        RoleId citizenRole =
            GetCitizenRoleFromWeapon(
                (RoleWeaponId)weaponIndex
            );

        if (citizenRole != requiredRole)
            return false;

        if (state.role == RoleId.Peddler)
        {
            return peddlerRealActionByClient
                .ContainsKey(clientId);
        }

        int useCount =
            spyCitizenAbilityUseCountByClient.TryGetValue(
                clientId,
                out int savedUseCount)
                ? savedUseCount
                : 0;

        return useCount <
            MaximumSpyCitizenAbilityUses;
    }

    private bool CompletePeddlerFakeActionIfNeeded(
        ulong clientId,
        RoleActionType actionType,
        ulong targetClientId = NoClientId,
        int targetHouseId = -1)
    {
        if (!playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state) ||
            state.role != RoleId.Peddler)
        {
            return false;
        }

        if (!peddlerRealActionByClient.TryGetValue(
                clientId,
                out bool isReal) ||
            isReal)
        {
            return false;
        }

        completedNightRoleActions.Add(clientId);

        AcceptRoleAction(
            clientId,
            actionType,
            targetClientId,
            targetHouseId,
            false,
            true
        );

        SendPrivateNotification(
            clientId,
            "가짜 도구였습니다. 능력 효과가 발생하지 않았습니다."
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Peddler Fake Action - " +
                $"Client: {clientId}, " +
                $"Action: {actionType}"
            );
        }

        return true;
    }

    private bool TryCompleteRealCitizenAction(
        ulong clientId,
        RoleActionType actionType,
        ulong targetClientId = NoClientId,
        int targetHouseId = -1)
    {
        CompleteRealCitizenAction(clientId);

        if (!playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state))
        {
            return true;
        }

        if (actionType == RoleActionType.Exorcism &&
            state.role == RoleId.Exorcist)
        {
            consumedExorcismPlayers.Add(clientId);
        }

        float failureChance =
            GetCitizenAbilityFailureChance(
                clientId,
                out float dutyFailureChance,
                out float stabilityFailureChance
            );

        float failureRoll = failureChance > 0f
            ? UnityEngine.Random.value
            : 1f;

        if (failureRoll < failureChance)
        {
            AcceptRoleAction(
                clientId,
                actionType,
                targetClientId,
                targetHouseId,
                false
            );

            SendPrivateNotification(
                clientId,
                "직무 미완료와 마을의 불안정 영향으로 능력이 실패했습니다.\n" +
                "오늘은 이 능력을 다시 사용할 수 없습니다."
            );

            if (showNightActionLogs)
            {
                Debug.Log(
                    $"Citizen Ability Failed - " +
                    $"Client: {clientId}, Action: {actionType}, " +
                    $"DutyChance: {dutyFailureChance:P1}, " +
                    $"StabilityChance: " +
                    $"{stabilityFailureChance:P1}, " +
                    $"FinalChance: {failureChance:P1}, " +
                    $"Roll: {failureRoll:0.000}"
                );
            }

            return false;
        }

        if (state.role == RoleId.Peddler)
        {
            SendPrivateNotification(
                clientId,
                "진짜 도구였습니다. 능력이 정상적으로 발동했습니다."
            );
        }

        return true;
    }

    private float GetCitizenAbilityFailureChance(
        ulong clientId,
        out float dutyFailureChance,
        out float stabilityFailureChance)
    {
        dutyFailureChance = 0f;
        stabilityFailureChance = 0f;

        if (!playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state) ||
            !state.isAlive ||
            state.team != RoleTeam.Citizen)
        {
            return 0f;
        }

        int missedDutyCount =
            previousNightMissedDutyCountByClient.TryGetValue(
                clientId,
                out int savedMissedDutyCount)
                ? savedMissedDutyCount
                : 0;

        dutyFailureChance = Mathf.Clamp(
            missedDutyCount * 0.25f,
            0f,
            0.5f
        );

        float instabilityRatio =
            (100f - Mathf.Clamp(
                villageStabilityPercent.Value,
                0,
                100
            )) / 100f;

        if (currentDay.Value > 1)
        {
            stabilityFailureChance =
                instabilityRatio *
                MaximumVillageStabilityFailureChance;
        }

        return Mathf.Clamp01(
            dutyFailureChance +
            stabilityFailureChance
        );
    }

    private void CompleteRealCitizenAction(
        ulong clientId)
    {
        completedNightRoleActions.Add(clientId);

        if (!playerMatchStates.TryGetValue(
                clientId,
                out PlayerMatchState state))
        {
            return;
        }

        if (state.role == RoleId.Peddler)
            return;

        if (state.role != RoleId.Spy)
            return;

        int useCount =
            spyCitizenAbilityUseCountByClient.TryGetValue(
                clientId,
                out int savedUseCount)
                ? savedUseCount
                : 0;

        spyCitizenAbilityUseCountByClient[clientId] =
            Mathf.Min(
                MaximumSpyCitizenAbilityUses,
                useCount + 1
            );

        SendSpyCitizenAbilityUsage(clientId);
    }

    private bool CanPlayerStartRoleAction(ulong clientId)
    {
        if (!TryGetPlayerSpirit(clientId, out PlayerSpirit playerSpirit))
            return false;

        SpiritHitReceiver hitReceiver =
            playerSpirit.GetComponent<SpiritHitReceiver>();

        if (hitReceiver != null &&
            hitReceiver.HasRecentAcceptedHitForRoleAction)
        {
            return false;
        }

        return playerSpirit.CanUseRoleAction;
    }

    private bool ValidateBodyRoleAction(
        ulong actorClientId,
        ulong targetClientId,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;

        if (!TryGetPlayerSpirit(
                actorClientId,
                out PlayerSpirit actorSpirit))
        {
            rejectionReason =
                "행동자의 영체를 찾지 못했습니다.";

            return false;
        }

        if (!TryGetPlayerBody(
                targetClientId,
                out PlayerBody targetBody))
        {
            rejectionReason =
                "대상의 육체를 찾지 못했습니다.";

            return false;
        }

        int ownedHouseId =
            GetOwnedHouseId(targetClientId);

        if (ownedHouseId < 0)
        {
            rejectionReason =
                "대상의 집을 찾지 못했습니다.";

            return false;
        }

        Collider targetCollider =
            targetBody.GetComponentInChildren
                <Collider>();

        Vector3 targetPoint =
            targetCollider != null
                ? targetCollider.ClosestPoint(
                    actorSpirit.transform.position
                )
                : targetBody.transform.position;

        Vector3 actorPosition =
            actorSpirit.transform.position;

        actorPosition.y = 0f;
        targetPoint.y = 0f;

        float distance =
            Vector3.Distance(
                actorPosition,
                targetPoint
            );

        float maximumDistance =
            roleActionDistance +
            roleActionServerTolerance;

        if (distance > maximumDistance)
        {
            rejectionReason =
                "대상과의 거리가 너무 멉니다.";

            return false;
        }

        int targetLocationHouseId = -1;

        if (houseRegistry != null)
        {
            House targetLocationHouse =
                houseRegistry.GetHouseContainingPosition(
                    targetBody.transform.position
                );

            if (targetLocationHouse != null)
            {
                targetLocationHouseId =
                    targetLocationHouse.Id;
            }
        }

        if (actorSpirit.CurrentHouseId !=
                targetLocationHouseId &&
            showNightActionLogs)
        {
            Debug.LogWarning(
                $"Role Action House Tracking Delayed - " +
                $"Actor: {actorClientId}, " +
                $"ActorHouse: {actorSpirit.CurrentHouseId}, " +
                $"TargetLocationHouse: {targetLocationHouseId}, " +
                $"TargetHomeHouse: {ownedHouseId}, " +
                $"Distance: {distance:0.00}"
            );
        }

        return true;
    }

    private bool ValidateFrontDoorRoleAction(
        ulong actorClientId,
        int targetHouseId,
        out House targetHouse,
        out string rejectionReason)
    {
        targetHouse = GetHouse(targetHouseId);
        rejectionReason = string.Empty;

        if (targetHouse == null)
        {
            rejectionReason = "대상 집을 찾지 못했습니다.";
            return false;
        }

        if (targetHouse.OwnerClientId ==
            House.NoOwnerClientId)
        {
            rejectionReason = "주인이 없는 집입니다.";
            return false;
        }

        if (!TryGetPlayerSpirit(
                actorClientId,
                out PlayerSpirit actorSpirit))
        {
            rejectionReason = "행동자의 영체를 찾지 못했습니다.";
            return false;
        }

        if (actorSpirit.CurrentHouseId >= 0)
        {
            rejectionReason =
                "집 밖에서만 사용할 수 있는 행동입니다.";

            return false;
        }

        float maximumDistance =
            roleActionDistance +
            roleActionServerTolerance;

        if (!targetHouse.IsNearFrontDoorOutside(
                actorSpirit.transform.position,
                maximumDistance))
        {
            rejectionReason =
                "대상 집의 정문 밖에서 사용해야 합니다.";

            return false;
        }

        return true;
    }

    private int GetOwnedHouseId(ulong clientId)
    {
        if (houseRegistry == null)
            return -1;

        foreach (House house in houseRegistry.Houses)
        {
            if (house != null && house.OwnerClientId == clientId)
                return house.Id;
        }

        return -1;
    }

    private void AcceptRoleAction(
        ulong clientId,
        RoleActionType actionType,
        ulong targetClientId = NoClientId,
        int targetHouseId = -1,
        bool effectSucceeded = true,
        bool wasFake = false)
    {
        RoleId role = playerMatchStates.TryGetValue(
            clientId,
            out PlayerMatchState state)
            ? state.role
            : (RoleId)byte.MaxValue;

        float finalFailureChance =
            GetCitizenAbilityFailureChance(
                clientId,
                out float dutyFailureChance,
                out float stabilityFailureChance
            );

        SendPersonalActionRecord(
            clientId,
            PersonalActionRecordKind.RoleAction,
            role,
            actionType,
            targetClientId,
            targetHouseId,
            (RoleId)byte.MaxValue,
            (RoleTeam)byte.MaxValue,
            effectSucceeded,
            wasFake,
            dutyFailureChance,
            stabilityFailureChance,
            finalFailureChance
        );

        if (effectSucceeded && !wasFake)
        {
            SendMafiaTeamKillActionRecord(
                clientId,
                role,
                actionType,
                targetClientId,
                targetHouseId
            );
        }

        SendRoleActionResult(clientId, actionType, true);
    }

    private void RejectRoleAction(ulong clientId, RoleActionType actionType, string reason)
    {
        if (showNightActionLogs)
            Debug.LogWarning($"Role Action Rejected - Client: {clientId}, Action: {actionType}, Reason: {reason}");

        if (!string.IsNullOrWhiteSpace(reason))
            SendPrivateNotification(clientId, reason);

        SendRoleActionResult(clientId, actionType, false);
    }

    private void SendPersonalActionRecord(
        ulong clientId,
        PersonalActionRecordKind kind,
        RoleId role,
        RoleActionType actionType,
        ulong targetClientId,
        int targetHouseId,
        RoleId inheritedRole,
        RoleTeam inheritedTeam,
        bool effectSucceeded,
        bool wasFake,
        float dutyFailureChance,
        float stabilityFailureChance,
        float finalFailureChance)
    {
        if (!IsServer ||
            NetworkManager == null ||
            !NetworkManager.ConnectedClients.ContainsKey(clientId))
        {
            return;
        }

        PersonalActionRecordData record =
            new PersonalActionRecordData
            {
                day = currentDay.Value,
                kind = kind,
                actorClientId = clientId,
                role = role,
                actionType = actionType,
                targetClientId = targetClientId,
                targetHouseId = targetHouseId,
                inheritedRole = inheritedRole,
                inheritedTeam = inheritedTeam,
                effectSucceeded = effectSucceeded,
                wasFake = wasFake,
                dutyFailureChance = dutyFailureChance,
                stabilityFailureChance = stabilityFailureChance,
                finalFailureChance = finalFailureChance
            };

        ReceivePersonalActionRecordRpc(
            record,
            RpcTarget.Single(
                clientId,
                RpcTargetUse.Temp
            )
        );
    }

    private void SendMafiaTeamKillActionRecord(
        ulong actorClientId,
        RoleId actorRole,
        RoleActionType actionType,
        ulong targetClientId,
        int targetHouseId)
    {
        if (!IsServer ||
            NetworkManager == null ||
            actorClientId != currentNightKillerClientId ||
            !IsAliveMafia(actorClientId) ||
            (actionType != RoleActionType.MafiaKill &&
             actionType != RoleActionType.AlchemistPoison &&
             actionType != RoleActionType.CurseDoll))
        {
            return;
        }

        PersonalActionRecordData record =
            new PersonalActionRecordData
            {
                day = currentDay.Value,
                kind = PersonalActionRecordKind.MafiaTeamKill,
                actorClientId = actorClientId,
                role = actorRole,
                actionType = actionType,
                targetClientId = targetClientId,
                targetHouseId = targetHouseId,
                inheritedRole = (RoleId)byte.MaxValue,
                inheritedTeam = (RoleTeam)byte.MaxValue,
                effectSucceeded = true,
                wasFake = false,
                dutyFailureChance = 0f,
                stabilityFailureChance = 0f,
                finalFailureChance = 0f
            };

        List<ulong> aliveMafiaClientIds =
            GetAliveMafiaClientIds();

        for (int i = 0;
             i < aliveMafiaClientIds.Count;
             i++)
        {
            ulong recipientClientId =
                aliveMafiaClientIds[i];

            if (recipientClientId == actorClientId ||
                !NetworkManager.ConnectedClients.ContainsKey(
                    recipientClientId))
            {
                continue;
            }

            ReceivePersonalActionRecordRpc(
                record,
                RpcTarget.Single(
                    recipientClientId,
                    RpcTargetUse.Temp
                )
            );
        }
    }

    private void SendSpecialPersonalActionRecord(
        ulong clientId,
        PersonalActionRecordKind kind,
        ulong targetClientId = NoClientId,
        int targetHouseId = -1,
        RoleId inheritedRole = (RoleId)byte.MaxValue,
        RoleTeam inheritedTeam = (RoleTeam)byte.MaxValue)
    {
        RoleId role = playerMatchStates.TryGetValue(
            clientId,
            out PlayerMatchState state)
            ? state.role
            : (RoleId)byte.MaxValue;

        SendPersonalActionRecord(
            clientId,
            kind,
            role,
            RoleActionType.None,
            targetClientId,
            targetHouseId,
            inheritedRole,
            inheritedTeam,
            true,
            false,
            0f,
            0f,
            0f
        );
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void ReceivePersonalActionRecordRpc(
        PersonalActionRecordData record,
        RpcParams rpcParams = default)
    {
        LocalPersonalActionRecorded?.Invoke(record);
    }

    private void SendRoleActionResult(ulong clientId, RoleActionType actionType, bool accepted)
    {
        if (!IsServer)
            return;

        ReceiveRoleActionResultRpc(actionType, accepted, RpcTarget.Single(clientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void ReceiveRoleActionResultRpc(RoleActionType actionType, bool accepted, RpcParams rpcParams = default)
    {
        LocalRoleActionResultReceived?.Invoke(actionType, accepted);
    }

    private void AssignInitialRoleWeapon(
        ulong clientId,
        RoleId role,
        HashSet<RoleWeaponId>
            unavailableCitizenCoverWeapons)
    {
        if (!IsServer || !TryGetPlayerSpirit(clientId, out PlayerSpirit playerSpirit))
            return;

        SpiritWeaponView weaponView = playerSpirit.GetComponentInChildren<SpiritWeaponView>(true);

        if (weaponView == null)
        {
            Debug.LogError($"Client {clientId}의 영체에 SpiritWeaponView가 없습니다.");
            return;
        }

        RoleWeaponId weaponId = GetInitialRoleWeapon(
            role,
            unavailableCitizenCoverWeapons
        );

        if (unavailableCitizenCoverWeapons != null &&
            weaponId != RoleWeaponId.None &&
            (role == RoleId.Spy ||
             role == RoleId.CurseCaster ||
             role == RoleId.Alchemist ||
             role == RoleId.Infiltrator ||
             role == RoleId.SerialKiller ||
             role == RoleId.Martyr))
        {
            unavailableCitizenCoverWeapons.Add(
                weaponId
            );
        }

        if (weaponId == RoleWeaponId.None)
        {
            weaponView.HideWeapon();
            return;
        }

        if (!weaponView.SetWeapon((int)weaponId))
            Debug.LogError($"Client {clientId}의 역할 무기를 설정하지 못했습니다. Role: {role}, Weapon: {weaponId}");
    }

    private RoleWeaponId GetInitialRoleWeapon(
        RoleId role,
        HashSet<RoleWeaponId>
            unavailableCitizenCoverWeapons)
    {
        switch (role)
        {
            case RoleId.Farmer:
                return RoleWeaponId.Pitchfork;

            case RoleId.Cleaner:
                return RoleWeaponId.Broom;

            case RoleId.Blacksmith:
                return RoleWeaponId.Hammer;

            case RoleId.Bailiff:
                return RoleWeaponId.Mace;

            case RoleId.Doctor:
                return RoleWeaponId.LargeSyringe;

            case RoleId.Detective:
                return RoleWeaponId.WoodenStaff;

            case RoleId.Drunkard:
                return RoleWeaponId.Bottle;

            case RoleId.Exorcist:
                return RoleWeaponId.Cross;

            case RoleId.Forensics:
                return RoleWeaponId.Torch;

            case RoleId.Medium:
                return RoleWeaponId.MediumStaff;

            case RoleId.Hunter:
                return RoleWeaponId.HuntingAxe;

            case RoleId.Undertaker:
                return RoleWeaponId.Shovel;

            case RoleId.Thief:
                return RoleWeaponId.PryBar;

            case RoleId.Peddler:
                return GetRandomCitizenWeapon();

            case RoleId.Spy:
                return GetRandomCitizenWeapon(
                    unavailableCitizenCoverWeapons
                );

            case RoleId.CurseCaster:
            case RoleId.Alchemist:
            case RoleId.Infiltrator:
            case RoleId.SerialKiller:
                return GetRandomCitizenWeapon(
                    unavailableCitizenCoverWeapons
                );

            case RoleId.Martyr:
                return GetRandomCitizenWeapon(
                    unavailableCitizenCoverWeapons
                );

            default:
                return RoleWeaponId.None;
        }
    }

    private RoleWeaponId GetRandomCitizenWeapon()
    {
        int randomIndex = UnityEngine.Random.Range(0, citizenWeaponPool.Length);
        return citizenWeaponPool[randomIndex];
    }

    private RoleWeaponId GetRandomCitizenWeapon(
        HashSet<RoleWeaponId> unavailableWeapons)
    {
        if (unavailableWeapons == null)
            return GetRandomCitizenWeapon();

        List<RoleWeaponId> availableWeapons =
            new List<RoleWeaponId>();

        for (int i = 0;
             i < citizenWeaponPool.Length;
             i++)
        {
            RoleWeaponId weaponId =
                citizenWeaponPool[i];

            if (!unavailableWeapons.Contains(weaponId))
                availableWeapons.Add(weaponId);
        }

        if (availableWeapons.Count == 0)
            return GetRandomCitizenWeapon();

        return availableWeapons[
            UnityEngine.Random.Range(
                0,
                availableWeapons.Count
            )
        ];
    }

    private bool SetPlayerWeapon(ulong clientId, RoleWeaponId weaponId)
    {
        if (!IsServer || !TryGetPlayerSpirit(clientId, out PlayerSpirit playerSpirit))
            return false;

        SpiritWeaponView weaponView = playerSpirit.GetComponentInChildren<SpiritWeaponView>(true);

        if (weaponView == null)
            return false;

        if (weaponId == RoleWeaponId.None)
        {
            weaponView.HideWeapon();
            return true;
        }

        return weaponView.SetWeapon((int)weaponId);
    }

    private int GetCurrentWeaponIndex(ulong clientId)
    {
        if (!TryGetPlayerSpirit(clientId, out PlayerSpirit playerSpirit))
            return -1;

        SpiritWeaponView weaponView = playerSpirit.GetComponentInChildren<SpiritWeaponView>(true);

        return weaponView != null ? weaponView.CurrentWeaponIndex : -1;
    }

    public void SubmitDetectiveInspectTarget(ulong targetClientId)
    {
        if (!IsClient || currentPhase.Value != MatchPhase.NightAction)
            return;

        SubmitDetectiveInspectTargetRpc(targetClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitDetectiveInspectTargetRpc(ulong targetClientId, RpcParams rpcParams = default)
    {
        if (!IsServer)
            return;

        ulong detectiveClientId = rpcParams.Receive.SenderClientId;

        if (currentPhase.Value != MatchPhase.NightAction)
        {
            RejectRoleAction(detectiveClientId, RoleActionType.DetectiveInspect, "현재 밤 행동 페이즈가 아닙니다.");
            return;
        }

        if (!CanUseCitizenAction(
                detectiveClientId,
                RoleId.Detective))
        {
            RejectRoleAction(
                detectiveClientId,
                RoleActionType.DetectiveInspect,
                "수사관 능력을 사용할 수 없습니다."
            );
            return;
        }

        if (detectiveClientId == targetClientId)
        {
            RejectRoleAction(detectiveClientId, RoleActionType.DetectiveInspect, "자기 육체는 조사할 수 없습니다.");
            return;
        }

        if (!IsAlivePlayer(targetClientId))
        {
            RejectRoleAction(detectiveClientId, RoleActionType.DetectiveInspect, "조사 대상이 생존 상태가 아닙니다.");
            return;
        }

        if (completedNightRoleActions.Contains(detectiveClientId))
        {
            RejectRoleAction(detectiveClientId, RoleActionType.DetectiveInspect, "이미 이번 밤 직업 행동을 완료했습니다.");
            return;
        }

        if (!CanPlayerStartRoleAction(detectiveClientId))
        {
            RejectRoleAction(detectiveClientId, RoleActionType.DetectiveInspect, "행동 불가 또는 감금 상태입니다.");
            return;
        }

        if (!ValidateBodyRoleAction(detectiveClientId, targetClientId, out string rejectionReason))
        {
            RejectRoleAction(detectiveClientId, RoleActionType.DetectiveInspect, rejectionReason);
            return;
        }

        if (!playerMatchStates.TryGetValue(targetClientId, out PlayerMatchState targetState))
        {
            RejectRoleAction(detectiveClientId, RoleActionType.DetectiveInspect, "대상의 직업 데이터를 찾지 못했습니다.");
            return;
        }

        if (CompletePeddlerFakeActionIfNeeded(
                detectiveClientId,
                RoleActionType.DetectiveInspect,
                targetClientId,
                GetOwnedHouseId(targetClientId)))
        {
            return;
        }

        if (!TryCompleteRealCitizenAction(
                detectiveClientId,
                RoleActionType.DetectiveInspect,
                targetClientId,
                GetOwnedHouseId(targetClientId)))
        {
            return;
        }

        RoleId revealedRole =
            GetDetectiveVisibleRole(
                targetClientId,
                targetState
            );

        SendDetectiveResult(
            detectiveClientId,
            targetClientId,
            revealedRole
        );

        AcceptRoleAction(
            detectiveClientId,
            RoleActionType.DetectiveInspect,
            targetClientId,
            GetOwnedHouseId(targetClientId)
        );

        if (showNightActionLogs)
        {
            Debug.Log(
                $"Detective Inspect Success - Detective: {detectiveClientId}, " +
                $"Target: {targetClientId}, ActualRole: {targetState.role}, " +
                $"RevealedRole: {revealedRole}"
            );
        }
    }

    private void SendDetectiveResult(ulong detectiveClientId, ulong targetClientId, RoleId revealedRole)
    {
        if (!IsServer)
            return;

        ReceiveDetectiveResultRpc(
            targetClientId,
            revealedRole,
            RpcTarget.Single(detectiveClientId, RpcTargetUse.Temp)
        );
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    private void ReceiveDetectiveResultRpc(
        ulong targetClientId,
        RoleId revealedRole,
        RpcParams rpcParams = default)
    {
        LocalDetectiveResultReceived?.Invoke(targetClientId, revealedRole);
    }

    private RoleId GetDetectiveVisibleRole(ulong targetClientId, PlayerMatchState targetState)
    {
        if (targetState.role != RoleId.Spy)
            return targetState.role;

        int weaponIndex = GetCurrentWeaponIndex(targetClientId);

        if (weaponIndex < 0)
            return RoleId.Cleaner;

        return GetCitizenRoleFromWeapon((RoleWeaponId)weaponIndex);
    }

    private RoleId GetCitizenRoleFromWeapon(RoleWeaponId weaponId)
    {
        switch (weaponId)
        {
            case RoleWeaponId.Pitchfork:
                return RoleId.Farmer;

            case RoleWeaponId.Broom:
                return RoleId.Cleaner;

            case RoleWeaponId.Hammer:
                return RoleId.Blacksmith;

            case RoleWeaponId.Mace:
                return RoleId.Bailiff;

            case RoleWeaponId.LargeSyringe:
                return RoleId.Doctor;

            case RoleWeaponId.WoodenStaff:
                return RoleId.Detective;

            case RoleWeaponId.Bottle:
                return RoleId.Drunkard;

            case RoleWeaponId.Cross:
                return RoleId.Exorcist;

            case RoleWeaponId.Torch:
                return RoleId.Forensics;

            case RoleWeaponId.MediumStaff:
                return RoleId.Medium;

            case RoleWeaponId.HuntingAxe:
                return RoleId.Hunter;

            case RoleWeaponId.Shovel:
                return RoleId.Undertaker;

            default:
                return RoleId.Cleaner;
        }
    }
}
