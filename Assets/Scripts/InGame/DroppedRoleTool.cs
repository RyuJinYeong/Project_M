using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(SpiritWeaponView))]
public class DroppedRoleTool : NetworkBehaviour
{
    private readonly NetworkVariable<ulong> previousOwnerClientId =
        new NetworkVariable<ulong>(
            ulong.MaxValue,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    private readonly NetworkVariable<bool> hasSourceRoleData =
        new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    private readonly NetworkVariable<RoleId> sourceRole =
        new NetworkVariable<RoleId>(
            RoleId.Farmer,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    private readonly NetworkVariable<RoleTeam> sourceTeam =
        new NetworkVariable<RoleTeam>(
            RoleTeam.Citizen,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    private SpiritWeaponView weaponView;

    public ulong PreviousOwnerClientId => previousOwnerClientId.Value;
    public int WeaponIndex => weaponView != null ? weaponView.CurrentWeaponIndex : -1;
    public bool HasSourceRoleData => hasSourceRoleData.Value;
    public RoleId SourceRole => sourceRole.Value;
    public RoleTeam SourceTeam => sourceTeam.Value;

    public bool TryGetWeaponId(out RoleWeaponId weaponId)
    {
        int weaponIndex = WeaponIndex;

        if (weaponIndex < 0)
        {
            weaponId = RoleWeaponId.None;
            return false;
        }

        weaponId = (RoleWeaponId)weaponIndex;
        return true;
    }

    public bool TryGetSourceRoleData(out RoleId role, out RoleTeam team)
    {
        role = sourceRole.Value;
        team = sourceTeam.Value;
        return hasSourceRoleData.Value;
    }

    private void Awake()
    {
        weaponView = GetComponent<SpiritWeaponView>();
    }

    public void Initialize(
        int weaponIndex,
        ulong ownerClientId,
        RoleId ownerRole,
        RoleTeam ownerTeam)
    {
        if (!IsServer || weaponView == null)
            return;

        previousOwnerClientId.Value = ownerClientId;
        sourceRole.Value = ownerRole;
        sourceTeam.Value = ownerTeam;
        hasSourceRoleData.Value = true;
        weaponView.SetWeapon(weaponIndex);
    }
}
