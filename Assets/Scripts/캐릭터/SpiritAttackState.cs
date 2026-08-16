using System;
using Unity.Netcode;

public struct SpiritAttackState : INetworkSerializable, IEquatable<SpiritAttackState>
{
    public uint sequenceId;
    public double startServerTime;

    public SpiritAttackState(uint sequenceId, double startServerTime)
    {
        this.sequenceId = sequenceId;

        this.startServerTime = startServerTime;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref sequenceId);

        serializer.SerializeValue(ref startServerTime);
    }

    public bool Equals(SpiritAttackState other)
    {
        return sequenceId == other.sequenceId && startServerTime.Equals(other.startServerTime);
    }
}