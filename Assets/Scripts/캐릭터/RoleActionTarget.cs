using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class RoleActionTarget : NetworkBehaviour
{
    [SerializeField] private RoleActionTargetType targetType;

    private readonly NetworkVariable<ulong> targetClientId = new NetworkVariable<ulong>(
        ulong.MaxValue,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public RoleActionTargetType TargetType => targetType;
    public ulong TargetClientId => targetClientId.Value;
    public bool HasValidTargetClientId => targetClientId.Value != ulong.MaxValue;

    public void Initialize(ulong clientId)
    {
        if (!IsServer)
            return;

        targetClientId.Value = clientId;
    }

    public bool TryGetTargetClientId(out ulong clientId)
    {
        clientId = targetClientId.Value;
        return IsSpawned && clientId != ulong.MaxValue;
    }
}