using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class PlayerBody : NetworkBehaviour
{
    private readonly NetworkVariable<ulong> linkedClientId =
        new NetworkVariable<ulong>(
            ulong.MaxValue,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    private readonly NetworkVariable<int> houseId =
        new NetworkVariable<int>(
            -1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    public ulong LinkedClientId =>
        linkedClientId.Value;

    public int HouseId =>
        houseId.Value;

    public void Initialize(
        ulong targetClientId,
        int assignedHouseId)
    {
        if (!IsServer)
        {
            return;
        }

        linkedClientId.Value =
            targetClientId;

        houseId.Value =
            assignedHouseId;
    }
}