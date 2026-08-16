using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(Rigidbody))]
public class SpiritFallRecoveryZone : MonoBehaviour
{
    [Header("복구 조건")]
    [SerializeField] private float recoveryHeight = -10f;

    [Header("복구 대기")]
    [Min(0.1f)]
    [SerializeField] private float retryDuration = 5f;

    [Min(0.02f)]
    [SerializeField] private float retryInterval = 0.1f;

    private readonly HashSet<ulong> recoveringSpirits = new HashSet<ulong>();

    private BoxCollider recoveryCollider;
    private Rigidbody recoveryRigidbody;

    private void Awake()
    {
        recoveryCollider = GetComponent<BoxCollider>();
        recoveryRigidbody = GetComponent<Rigidbody>();

        recoveryCollider.isTrigger = true;

        recoveryRigidbody.useGravity = false;
        recoveryRigidbody.isKinematic = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        TryStartRecovery(other);
    }

    private void OnTriggerStay(Collider other)
    {
        TryStartRecovery(other);
    }

    private void TryStartRecovery(Collider other)
    {
        PlayerSpirit playerSpirit = other.GetComponentInParent<PlayerSpirit>();

        if (playerSpirit == null || !playerSpirit.IsSpawned || !playerSpirit.IsOwner)
            return;

        if (playerSpirit.transform.position.y > recoveryHeight)
            return;

        ulong networkObjectId = playerSpirit.NetworkObjectId;

        if (!recoveringSpirits.Add(networkObjectId))
            return;

        StartCoroutine(RecoverRoutine(playerSpirit, networkObjectId));
    }

    private IEnumerator RecoverRoutine(PlayerSpirit playerSpirit, ulong networkObjectId)
    {
        float timeoutTime = Time.realtimeSinceStartup + retryDuration;
        bool recovered = false;

        while (playerSpirit != null && Time.realtimeSinceStartup < timeoutTime)
        {
            if (!playerSpirit.IsSpawned || !playerSpirit.IsOwner)
                break;

            if (playerSpirit.transform.position.y > recoveryHeight)
                break;

            recovered = playerSpirit.TryRecoverToHomeSpiritPoint();

            if (recovered)
                break;

            yield return new WaitForSecondsRealtime(retryInterval);
        }

        recoveringSpirits.Remove(networkObjectId);

        if (!recovered &&
            playerSpirit != null &&
            playerSpirit.IsSpawned &&
            playerSpirit.IsOwner &&
            playerSpirit.transform.position.y <= recoveryHeight)
        {
            Debug.LogWarning(
                $"영체 복구에 실패했습니다. " +
                $"ClientId: {playerSpirit.OwnerClientId}, " +
                $"HomeHouseId: {playerSpirit.HomeHouseId}, " +
                $"Position: {playerSpirit.transform.position}"
            );
        }
    }

    private void OnDisable()
    {
        StopAllCoroutines();

        recoveringSpirits.Clear();
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 start = new Vector3(transform.position.x - 10f, recoveryHeight, transform.position.z);
        Vector3 end = new Vector3(transform.position.x + 10f, recoveryHeight, transform.position.z);

        Gizmos.DrawLine(start, end);
    }
}