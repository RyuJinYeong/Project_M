using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class SpiritWeaponView : NetworkBehaviour
{
    [SerializeField] private Transform weaponModelRoot;
    [SerializeField] private int defaultWeaponIndex;

    private readonly NetworkVariable<int> currentWeaponIndex = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private GameObject[] weaponModels;

    public int CurrentWeaponIndex => currentWeaponIndex.Value;
    public int WeaponCount => weaponModels != null ? weaponModels.Length : 0;

    private void Awake()
    {
        CacheWeaponModels();
        ApplyWeapon(defaultWeaponIndex);
    }

    public override void OnNetworkSpawn()
    {
        currentWeaponIndex.OnValueChanged += OnWeaponChanged;

        if (IsServer && !IsValidWeaponIndex(currentWeaponIndex.Value))
            currentWeaponIndex.Value = IsValidWeaponIndex(defaultWeaponIndex) ? defaultWeaponIndex : -1;

        ApplyWeapon(currentWeaponIndex.Value);
    }

    public override void OnNetworkDespawn()
    {
        currentWeaponIndex.OnValueChanged -= OnWeaponChanged;
    }

    public bool SetWeapon(int weaponIndex)
    {
        if (!IsServer || !IsValidWeaponIndex(weaponIndex))
            return false;

        if (currentWeaponIndex.Value == weaponIndex)
        {
            ApplyWeapon(weaponIndex);
            return true;
        }

        currentWeaponIndex.Value = weaponIndex;
        return true;
    }

    public void HideWeapon()
    {
        if (!IsServer)
            return;

        currentWeaponIndex.Value = -1;
    }

    private void CacheWeaponModels()
    {
        if (weaponModelRoot == null)
        {
            weaponModels = new GameObject[0];
            return;
        }

        weaponModels = new GameObject[weaponModelRoot.childCount];

        for (int i = 0; i < weaponModelRoot.childCount; i++)
            weaponModels[i] = weaponModelRoot.GetChild(i).gameObject;
    }

    private void OnWeaponChanged(int previous, int current)
    {
        ApplyWeapon(current);
    }

    private void ApplyWeapon(int weaponIndex)
    {
        if (weaponModels == null)
            return;

        for (int i = 0; i < weaponModels.Length; i++)
        {
            if (weaponModels[i] != null)
                weaponModels[i].SetActive(i == weaponIndex);
        }
    }

    private bool IsValidWeaponIndex(int weaponIndex)
    {
        return weaponModels != null && weaponIndex >= 0 && weaponIndex < weaponModels.Length;
    }
}