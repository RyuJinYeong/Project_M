using System;
using Unity.Collections;
using Unity.Netcode;

public enum MatchPhase : byte
{
    None,
    MorningDiscussion,
    MorningVote,
    MorningVoteResult,
    NightPreparation,
    NightAction,
    NightResult,
    GameResult,
    FinalDuel
}

public enum RoleTeam : byte
{
    Citizen,
    Mafia,
    Neutral
}

public enum RoleId : byte
{
    Farmer = 0,
    Cleaner = 1,
    Blacksmith = 2,

    Bailiff = 3,
    Doctor = 4,
    Detective = 5,
    Drunkard = 6,
    Exorcist = 7,
    Forensics = 8,
    Medium = 9,
    Hunter = 10,
    Undertaker = 11,
    Peddler = 12,

    CurseCaster = 13,
    Spy = 15,
    Alchemist = 16,
    Infiltrator = 17,

    Thief = 18,
    SerialKiller = 19,
    Martyr = 20
}

public enum RoleWeaponId : sbyte
{
    None = -1,
    Pitchfork = 0,
    Broom = 1,
    Hammer = 2,
    Mace = 3,
    LargeSyringe = 4,
    WoodenStaff = 5,
    Bottle = 6,
    Cross = 7,
    Torch = 8,
    MediumStaff = 9,
    HuntingAxe = 10,
    Shovel = 11,
    PryBar = 12
}

public enum RoleActionType : byte
{
    None,
    MafiaKill,
    DoctorProtect,
    BailiffConfine,
    Exorcism,
    DetectiveInspect,
    AlchemistPoison,
    ForensicsInspect,
    HunterTrack,
    UndertakerSeal,
    MediumCommune,
    CurseDoll
}

public enum CurseDollLocation : byte
{
    None,
    Held,
    Installed
}

public enum CurseDollPointType : byte
{
    Home,
    Drunkard
}

public struct CurseDollStateData :
    INetworkSerializable,
    IEquatable<CurseDollStateData>
{
    public CurseDollLocation location;
    public ulong holderClientId;
    public int installedHouseId;
    public CurseDollPointType installedPointType;
    public ulong installedOwnerClientId;

    public void NetworkSerialize<T>(
        BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref location);
        serializer.SerializeValue(ref holderClientId);
        serializer.SerializeValue(ref installedHouseId);
        serializer.SerializeValue(ref installedPointType);
        serializer.SerializeValue(ref installedOwnerClientId);
    }

    public bool Equals(CurseDollStateData other)
    {
        return location == other.location &&
               holderClientId == other.holderClientId &&
               installedHouseId == other.installedHouseId &&
               installedPointType == other.installedPointType &&
               installedOwnerClientId == other.installedOwnerClientId;
    }
}

public enum RoleActionTargetType : byte
{
    Body,
    Spirit,
    FrontDoor,
    BodyOrFrontDoor
}

public enum RoleActionExecutionType : byte
{
    None,
    Real,
    Fake
}

public enum SpiritConfinementType : byte
{
    None,
    InternalCage,
    ExternalCage
}

public struct PlayerMatchState : INetworkSerializable, IEquatable<PlayerMatchState>
{
    public ulong clientId;
    public FixedString64Bytes playerName;
    public RoleId role;
    public RoleTeam team;
    public bool isAlive;

    public PlayerMatchState(ulong clientId, FixedString64Bytes playerName, RoleId role, RoleTeam team)
    {
        this.clientId = clientId;
        this.playerName = playerName;
        this.role = role;
        this.team = team;
        isAlive = true;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref clientId);
        serializer.SerializeValue(ref playerName);
        serializer.SerializeValue(ref role);
        serializer.SerializeValue(ref team);
        serializer.SerializeValue(ref isAlive);
    }

    public bool Equals(PlayerMatchState other)
    {
        return clientId == other.clientId &&
               playerName == other.playerName &&
               role == other.role &&
               team == other.team &&
               isAlive == other.isAlive;
    }
}

public struct PlayerPublicState : INetworkSerializable, IEquatable<PlayerPublicState>
{
    public ulong clientId;
    public FixedString64Bytes playerName;
    public bool isAlive;

    public PlayerPublicState(ulong clientId, FixedString64Bytes playerName)
    {
        this.clientId = clientId;
        this.playerName = playerName;
        isAlive = true;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref clientId);
        serializer.SerializeValue(ref playerName);
        serializer.SerializeValue(ref isAlive);
    }

    public bool Equals(PlayerPublicState other)
    {
        return clientId == other.clientId &&
               playerName == other.playerName &&
               isAlive == other.isAlive;
    }
}

public struct MafiaMemberData : INetworkSerializable, IEquatable<MafiaMemberData>
{
    public ulong clientId;
    public FixedString64Bytes playerName;

    public MafiaMemberData(ulong clientId, FixedString64Bytes playerName)
    {
        this.clientId = clientId;
        this.playerName = playerName;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref clientId);
        serializer.SerializeValue(ref playerName);
    }

    public bool Equals(MafiaMemberData other)
    {
        return clientId == other.clientId && playerName == other.playerName;
    }
}

public struct MorningVoteTallyData :
    INetworkSerializable,
    IEquatable<MorningVoteTallyData>
{
    public ulong targetClientId;
    public int voteCount;

    public MorningVoteTallyData(
        ulong targetClientId,
        int voteCount)
    {
        this.targetClientId = targetClientId;
        this.voteCount = voteCount;
    }

    public void NetworkSerialize<T>(
        BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(
            ref targetClientId
        );

        serializer.SerializeValue(
            ref voteCount
        );
    }

    public bool Equals(
        MorningVoteTallyData other)
    {
        return targetClientId ==
                   other.targetClientId &&
               voteCount ==
                   other.voteCount;
    }
}

public struct MorningVoteResultData : INetworkSerializable, IEquatable<MorningVoteResultData>
{
    public bool hasResult;
    public bool hasExiledPlayer;
    public bool isTie;
    public bool abstainPreventedExile;
    public ulong exiledClientId;
    public FixedString64Bytes exiledPlayerName;
    public bool revealedExiledTeam;
    public RoleTeam exiledTeam;
    public int voteCount;
    public int abstainVoteCount;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref hasResult);
        serializer.SerializeValue(ref hasExiledPlayer);
        serializer.SerializeValue(ref isTie);
        serializer.SerializeValue(
            ref abstainPreventedExile
        );
        serializer.SerializeValue(ref exiledClientId);
        serializer.SerializeValue(ref exiledPlayerName);
        serializer.SerializeValue(ref revealedExiledTeam);
        serializer.SerializeValue(ref exiledTeam);
        serializer.SerializeValue(ref voteCount);
        serializer.SerializeValue(
            ref abstainVoteCount
        );
    }

    public bool Equals(MorningVoteResultData other)
    {
        return hasResult == other.hasResult &&
               hasExiledPlayer == other.hasExiledPlayer &&
               isTie == other.isTie &&
               abstainPreventedExile ==
                   other.abstainPreventedExile &&
               exiledClientId == other.exiledClientId &&
               exiledPlayerName == other.exiledPlayerName &&
               revealedExiledTeam ==
                   other.revealedExiledTeam &&
               exiledTeam == other.exiledTeam &&
               voteCount == other.voteCount &&
               abstainVoteCount ==
                   other.abstainVoteCount;
    }
}

public struct NightResultData : INetworkSerializable, IEquatable<NightResultData>
{
    public bool hasResult;
    public bool hasDeath;
    public ulong victimClientId;
    public FixedString64Bytes victimPlayerName;
    public bool victimWasPoisoned;
    public bool victimWasCursed;
    public bool hasSecondDeath;
    public ulong secondVictimClientId;
    public FixedString64Bytes secondVictimPlayerName;
    public bool secondVictimWasPoisoned;
    public bool secondVictimWasCursed;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref hasResult);
        serializer.SerializeValue(ref hasDeath);
        serializer.SerializeValue(ref victimClientId);
        serializer.SerializeValue(ref victimPlayerName);
        serializer.SerializeValue(ref victimWasPoisoned);
        serializer.SerializeValue(ref victimWasCursed);
        serializer.SerializeValue(ref hasSecondDeath);
        serializer.SerializeValue(ref secondVictimClientId);
        serializer.SerializeValue(ref secondVictimPlayerName);
        serializer.SerializeValue(ref secondVictimWasPoisoned);
        serializer.SerializeValue(ref secondVictimWasCursed);
    }

    public bool Equals(NightResultData other)
    {
        return hasResult == other.hasResult &&
               hasDeath == other.hasDeath &&
               victimClientId == other.victimClientId &&
               victimPlayerName == other.victimPlayerName &&
               victimWasPoisoned == other.victimWasPoisoned &&
               victimWasCursed == other.victimWasCursed &&
               hasSecondDeath == other.hasSecondDeath &&
               secondVictimClientId == other.secondVictimClientId &&
               secondVictimPlayerName == other.secondVictimPlayerName &&
               secondVictimWasPoisoned == other.secondVictimWasPoisoned &&
               secondVictimWasCursed == other.secondVictimWasCursed;
    }
}

public enum LobbyCountChangeSource : byte
{
    Automatic,
    Mafia,
    Citizen,
    Neutral
}

public struct LobbySettings : INetworkSerializable, IEquatable<LobbySettings>
{
    public int mafiaCount;
    public int citizenCount;
    public int neutralCount;
    public uint requiredRoleMask;

    public int morningDiscussionSeconds;
    public int morningVoteSeconds;
    public int morningVoteResultSeconds;
    public int nightPreparationSeconds;
    public int nightActionSeconds;
    public int nightResultSeconds;

    public bool allowDuplicateRoles;
    public bool revealExiledTeam;

    public int TotalPlayerCount =>
        mafiaCount +
        citizenCount +
        neutralCount;

    public bool IsRoleRequired(RoleId role)
    {
        int bitIndex = (int)role;

        if (bitIndex < 0 || bitIndex >= 32)
            return false;

        return (requiredRoleMask & (1u << bitIndex)) != 0u;
    }

    public void SetRoleRequired(RoleId role, bool required)
    {
        int bitIndex = (int)role;

        if (bitIndex < 0 || bitIndex >= 32)
            return;

        uint roleBit = 1u << bitIndex;

        if (required)
            requiredRoleMask |= roleBit;
        else
            requiredRoleMask &= ~roleBit;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref mafiaCount);
        serializer.SerializeValue(ref citizenCount);
        serializer.SerializeValue(ref neutralCount);
        serializer.SerializeValue(ref requiredRoleMask);
        serializer.SerializeValue(
            ref morningDiscussionSeconds
        );
        serializer.SerializeValue(
            ref morningVoteSeconds
        );
        serializer.SerializeValue(
            ref morningVoteResultSeconds
        );
        serializer.SerializeValue(
            ref nightPreparationSeconds
        );
        serializer.SerializeValue(
            ref nightActionSeconds
        );
        serializer.SerializeValue(
            ref nightResultSeconds
        );
        serializer.SerializeValue(
            ref allowDuplicateRoles
        );
        serializer.SerializeValue(
            ref revealExiledTeam
        );
    }

    public bool Equals(LobbySettings other)
    {
        return mafiaCount == other.mafiaCount &&
               citizenCount == other.citizenCount &&
               neutralCount == other.neutralCount &&
               requiredRoleMask == other.requiredRoleMask &&
               morningDiscussionSeconds ==
                   other.morningDiscussionSeconds &&
               morningVoteSeconds ==
                   other.morningVoteSeconds &&
               morningVoteResultSeconds ==
                   other.morningVoteResultSeconds &&
               nightPreparationSeconds ==
                   other.nightPreparationSeconds &&
               nightActionSeconds ==
                   other.nightActionSeconds &&
               nightResultSeconds ==
                   other.nightResultSeconds &&
               allowDuplicateRoles ==
                   other.allowDuplicateRoles &&
               revealExiledTeam ==
                   other.revealExiledTeam;
    }
}

public struct LobbyPlayer : INetworkSerializable, IEquatable<LobbyPlayer>
{
    public ulong clientId;
    public FixedString64Bytes playerName;
    public bool isHost;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref clientId);
        serializer.SerializeValue(ref playerName);
        serializer.SerializeValue(ref isHost);
    }

    public bool Equals(LobbyPlayer other)
    {
        return clientId == other.clientId &&
               playerName == other.playerName &&
               isHost == other.isHost;
    }
}
