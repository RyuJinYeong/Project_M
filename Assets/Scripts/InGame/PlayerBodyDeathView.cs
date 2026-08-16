using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class PlayerBodyDeathView : NetworkBehaviour
{
    [Header("Animation")]
    [SerializeField] private Animator animator;
    [SerializeField] private string deathIdleStateName = "DeathIdle";

    [Header("Tool Drop")]
    [SerializeField] private Transform toolDropPoint;

    private readonly NetworkVariable<bool> isDead = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private int deathIdleStateHash;

    public bool IsDead => isDead.Value;
    public Vector3 ToolDropPosition => toolDropPoint != null ? toolDropPoint.position : transform.position + transform.right * 0.5f;
    public Quaternion ToolDropRotation => toolDropPoint != null ? toolDropPoint.rotation : transform.rotation;

    private void Awake()
    {
        deathIdleStateHash = Animator.StringToHash(deathIdleStateName);
    }

    public override void OnNetworkSpawn()
    {
        isDead.OnValueChanged += OnDeadStateChanged;

        if (isDead.Value)
            PlayDeathIdle();
    }

    public override void OnNetworkDespawn()
    {
        isDead.OnValueChanged -= OnDeadStateChanged;
    }

    public void ApplyDeath(Vector3 position, Quaternion rotation)
    {
        if (!IsServer || isDead.Value)
            return;

        transform.SetPositionAndRotation(position, rotation);
        isDead.Value = true;

        ApplyDeathTransformRpc(position, rotation);
    }

    private void OnDeadStateChanged(bool previous, bool current)
    {
        if (current)
            PlayDeathIdle();
    }

    [Rpc(SendTo.Everyone)]
    private void ApplyDeathTransformRpc(Vector3 position, Quaternion rotation)
    {
        transform.SetPositionAndRotation(position, rotation);
        PlayDeathIdle();
    }

    private void PlayDeathIdle()
    {
        if (animator == null || string.IsNullOrEmpty(deathIdleStateName))
            return;

        animator.Play(deathIdleStateHash, 0, 0f);
        animator.Update(0f);
    }
}