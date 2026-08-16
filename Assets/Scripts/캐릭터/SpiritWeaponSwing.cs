using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class SpiritWeaponSwing :
    NetworkBehaviour
{
    public static SpiritWeaponSwing
        LocalInstance
    {
        get;
        private set;
    }

    [Header("Weapon")]
    [SerializeField]
    private Transform weaponPivot;

    [SerializeField]
    private Transform weaponVisualPivot;

    [SerializeField]
    private BoxCollider weaponHitbox;

    [Header("Swing Position")]
    [SerializeField]
    private Vector3 windUpPositionOffset =
        new Vector3(
            0.28f,
            0.34f,
            0f
        );

    [SerializeField]
    private Vector3 strikePositionOffset =
        new Vector3(
            -0.34f,
            -0.32f,
            0f
        );

    [Header("Swing Rotation")]
    [SerializeField]
    private Vector3 windUpEuler =
        new Vector3(
            0f,
            0f,
            -25f
        );

    [SerializeField]
    private Vector3 strikeEuler =
        new Vector3(
            0f,
            0f,
            135f
        );

    [Header("Blade Alignment")]
    [SerializeField]
    private float bladeAngleOffset;

    [Header("Swing Time")]
    [SerializeField]
    private float windUpDuration =
        0.14f;

    [SerializeField]
    private float strikeDuration =
        0.10f;

    [SerializeField]
    private float strikeHoldDuration =
        0.05f;

    [SerializeField]
    private float returnDuration =
        0.22f;

    [SerializeField]
    private float attackCooldown =
        0.75f;

    [Header("Attack Detection")]
    [SerializeField]
    private int damage = 1;

    [SerializeField]
    private int hitCheckSubsteps = 4;

    [SerializeField]
    private LayerMask spiritHitboxLayer;

    [Header("Latency Compensation")]
    [Min(0f)]
    [SerializeField]
    private float baseAttackPositionTolerance = 0.08f;

    [Min(0f)]
    [SerializeField]
    private float maxAttackPositionCompensation = 0.35f;

    [Min(0f)]
    [SerializeField]
    private float assumedMaximumMoveSpeed = 3f;

    [Header("Sound")]
    [SerializeField]
    private AudioSource audioSource;

    [SerializeField]
    private AudioClip swingClip;

    [Header("Hit Confirm")]
    [Min(0f)]
    [SerializeField]
    private float hitConfirmPunchAngle = 8f;

    [Min(0.01f)]
    [SerializeField]
    private float hitConfirmPunchDuration = 0.09f;

    private readonly Collider[] hitBuffer =
        new Collider[32];

    private readonly NetworkVariable
        <SpiritAttackState> attackState =
            new NetworkVariable
                <SpiritAttackState>(
                    new SpiritAttackState(),
                    NetworkVariableReadPermission
                        .Everyone,
                    NetworkVariableWritePermission
                        .Server
                );

    private Vector3 idlePosition;
    private Quaternion idleRotation;
    private Quaternion idleVisualRotation;

    private PlayerSpirit playerSpirit;

    private Coroutine swingCoroutine;
    private Coroutine hitConfirmPunchCoroutine;

    private float currentHitConfirmPunchAngle;

    private double nextLocalAttackTime;
    private double nextServerAttackTime;

    private uint nextAttackSequenceId;
    private uint lastRemoteSequenceId;
    private Vector3 serverAttackPositionOffset;

    private float TotalSwingDuration =>
        windUpDuration +
        strikeDuration +
        strikeHoldDuration +
        returnDuration;

    private void Awake()
    {
        playerSpirit =
            GetComponent<PlayerSpirit>();

        if (weaponPivot != null)
        {
            idlePosition =
                weaponPivot.localPosition;

            idleRotation =
                weaponPivot.localRotation;
        }

        if (weaponVisualPivot != null)
        {
            idleVisualRotation =
                weaponVisualPivot
                    .localRotation;
        }
    }

    public override void OnNetworkSpawn()
    {
        attackState.OnValueChanged +=
            OnAttackStateChanged;

        if (IsOwner)
            LocalInstance = this;

        /*
         * 공격 도중 늦게 생성된 원격 복제본이라면
         * 현재 서버 시각에 맞는 지점부터 재생한다.
         */
        if (!IsServer &&
            !IsOwner &&
            attackState.Value.sequenceId !=
                0)
        {
            TryPlayRemoteAttack(
                attackState.Value
            );
        }
    }

    public override void OnNetworkDespawn()
    {
        attackState.OnValueChanged -=
            OnAttackStateChanged;

        StopHitConfirmPunch();

        if (LocalInstance == this)
            LocalInstance = null;

        CancelCurrentSwing();
    }

    private void Update()
    {
        if (!IsOwner ||
            !IsSpawned)
        {
            return;
        }

        if (SpiritFirstPersonCamera
                .IsLocalUIInteractionActive)
        {
            return;
        }

        if (Mouse.current != null &&
            Mouse.current
                .leftButton
                .wasPressedThisFrame)
        {
            TryStartLocalAttack();
        }
    }

    private void TryStartLocalAttack()
    {
        if (playerSpirit == null ||
            !playerSpirit.CanAttack)
        {
            return;
        }

        double currentLocalTime =
            NetworkManager.LocalTime.Time;

        if (currentLocalTime <
            nextLocalAttackTime)
        {
            return;
        }

        nextLocalAttackTime =
            currentLocalTime +
            attackCooldown;

        /*
         * Host는 서버와 소유 클라이언트가
         * 같은 인스턴스이므로 즉시 서버 공격을 시작한다.
         */
        if (IsServer)
        {
            StartServerAttack(
                transform.position,
                false
            );
            return;
        }

        /*
         * 일반 클라이언트는 서버 응답을 기다리지 않고
         * 자기 화면에서 즉시 공격 모션을 재생한다.
         */
        StartSwing(
            0f,
            false
        );

        RequestAttackRpc(transform.position);
    }

    [Rpc(
        SendTo.Server,
        InvokePermission =
            RpcInvokePermission.Owner
    )]
    private void RequestAttackRpc(
        Vector3 reportedAttackPosition)
    {
        StartServerAttack(
            reportedAttackPosition,
            true
        );
    }

    private void StartServerAttack(
        Vector3 reportedAttackPosition,
        bool applyLatencyCompensation)
    {
        if (!IsServer ||
            playerSpirit == null ||
            !playerSpirit.CanAttack)
        {
            return;
        }

        double currentServerTime =
            NetworkManager.ServerTime.Time;

        if (currentServerTime <
            nextServerAttackTime)
        {
            return;
        }

        nextServerAttackTime =
            currentServerTime +
            attackCooldown;

        serverAttackPositionOffset =
            applyLatencyCompensation
                ? CalculateServerAttackPositionOffset(
                    reportedAttackPosition
                )
                : Vector3.zero;

        nextAttackSequenceId++;

        if (nextAttackSequenceId == 0)
        {
            nextAttackSequenceId = 1;
        }

        /*
         * 서버가 공식 공격 번호와
         * 공격 시작 시각을 상태로 기록한다.
         */
        attackState.Value =
            new SpiritAttackState(
                nextAttackSequenceId,
                currentServerTime
            );

        /*
         * 서버 복제본에서는 실제 궤적과
         * 피해 판정을 함께 실행한다.
         */
        StartSwing(
            0f,
            true
        );
    }

    private Vector3 CalculateServerAttackPositionOffset(
        Vector3 reportedAttackPosition)
    {
        if (!IsFinite(reportedAttackPosition) ||
            NetworkManager == null ||
            NetworkManager.NetworkConfig == null ||
            NetworkManager.NetworkConfig.NetworkTransport == null)
        {
            return Vector3.zero;
        }

        ulong rttMilliseconds =
            NetworkManager.NetworkConfig.NetworkTransport
                .GetCurrentRtt(OwnerClientId);
        float oneWayLatencySeconds =
            rttMilliseconds * 0.0005f;
        float allowedOffset =
            Mathf.Clamp(
                Mathf.Max(0f, baseAttackPositionTolerance) +
                oneWayLatencySeconds *
                Mathf.Max(0f, assumedMaximumMoveSpeed),
                0f,
                Mathf.Max(0f, maxAttackPositionCompensation)
            );

        return Vector3.ClampMagnitude(
            reportedAttackPosition - transform.position,
            allowedOffset
        );
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) &&
               !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) &&
               !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) &&
               !float.IsInfinity(value.z);
    }

    private void OnAttackStateChanged(
        SpiritAttackState previous,
        SpiritAttackState current)
    {
        if (current.sequenceId == 0)
        {
            return;
        }

        /*
         * 서버는 StartServerAttack에서 이미 시작했다.
         */
        if (IsServer)
        {
            return;
        }

        /*
         * 공격자는 입력 순간 로컬에서 이미 시작했다.
         * 서버 상태를 받고 다시 시작하지 않는다.
         */
        if (IsOwner)
        {
            return;
        }

        TryPlayRemoteAttack(
            current
        );
    }

    private void TryPlayRemoteAttack(
        SpiritAttackState state)
    {
        if (state.sequenceId == 0 ||
            state.sequenceId ==
            lastRemoteSequenceId)
        {
            return;
        }

        double elapsedTime =
            NetworkManager.ServerTime.Time -
            state.startServerTime;

        float startElapsed =
            Mathf.Max(
                0f,
                (float)elapsedTime
            );

        if (startElapsed >=
            TotalSwingDuration)
        {
            lastRemoteSequenceId =
                state.sequenceId;

            return;
        }

        lastRemoteSequenceId =
            state.sequenceId;

        StartSwing(
            startElapsed,
            false
        );
    }

    private void StartSwing(
        float startElapsed,
        bool detectHits)
    {
        if (weaponPivot == null)
        {
            return;
        }

        if (swingCoroutine != null)
        {
            StopCoroutine(
                swingCoroutine
            );
        }

        swingCoroutine =
            StartCoroutine(
                SwingRoutine(
                    startElapsed,
                    detectHits
                )
            );
    }

    private IEnumerator SwingRoutine(
        float startElapsed,
        bool detectHits)
    {
        Vector3 windUpPosition =
            idlePosition +
            windUpPositionOffset;

        Vector3 strikePosition =
            idlePosition +
            strikePositionOffset;

        Quaternion windUpRotation =
            idleRotation *
            Quaternion.Euler(
                windUpEuler
            );

        Quaternion strikeRotation =
            idleRotation *
            Quaternion.Euler(
                strikeEuler
            );

        Vector3 swingDirection =
            (
                strikePosition -
                windUpPosition
            ).normalized;

        float swingAngle =
            Mathf.Atan2(
                swingDirection.y,
                swingDirection.x
            ) *
            Mathf.Rad2Deg;

        float bladeAngle =
            swingAngle +
            90f +
            bladeAngleOffset;

        Quaternion strikeVisualRotation =
            idleVisualRotation *
            Quaternion.Euler(
                0f,
                0f,
                bladeAngle
            );

        HashSet<ulong> hitTargets =
            detectHits
                ? new HashSet<ulong>()
                : null;

        float phaseElapsed =
            Mathf.Max(
                0f,
                startElapsed
            );

        /*
         * 준비 동작
         */
        if (phaseElapsed <
            windUpDuration)
        {
            yield return MoveWeapon(
                idlePosition,
                idleRotation,
                windUpPosition,
                windUpRotation,
                idleVisualRotation,
                strikeVisualRotation,
                windUpDuration,
                phaseElapsed,
                false,
                null
            );

            phaseElapsed = 0f;
        }
        else
        {
            SetWeaponTransform(
                windUpPosition,
                windUpRotation,
                strikeVisualRotation
            );

            phaseElapsed -=
                windUpDuration;
        }

        /*
         * 공격 동작
         */
        if (phaseElapsed <
            strikeDuration)
        {
            PlaySwingSound();

            yield return MoveWeapon(
                windUpPosition,
                windUpRotation,
                strikePosition,
                strikeRotation,
                strikeVisualRotation,
                strikeVisualRotation,
                strikeDuration,
                phaseElapsed,
                detectHits,
                hitTargets
            );

            phaseElapsed = 0f;
        }
        else
        {
            SetWeaponTransform(
                strikePosition,
                strikeRotation,
                strikeVisualRotation
            );

            phaseElapsed -=
                strikeDuration;
        }

        /*
         * 공격 완료 유지
         */
        if (phaseElapsed <
            strikeHoldDuration)
        {
            float remainingHoldTime =
                strikeHoldDuration -
                phaseElapsed;

            if (remainingHoldTime > 0f)
            {
                yield return
                    new WaitForSeconds(
                        remainingHoldTime
                    );
            }

            phaseElapsed = 0f;
        }
        else
        {
            phaseElapsed -=
                strikeHoldDuration;
        }

        /*
         * 복귀 동작
         */
        if (phaseElapsed <
            returnDuration)
        {
            yield return MoveWeapon(
                strikePosition,
                strikeRotation,
                idlePosition,
                idleRotation,
                strikeVisualRotation,
                idleVisualRotation,
                returnDuration,
                phaseElapsed,
                false,
                null
            );
        }

        SetWeaponTransform(
            idlePosition,
            idleRotation,
            idleVisualRotation
        );

        if (detectHits)
            serverAttackPositionOffset = Vector3.zero;

        swingCoroutine = null;
    }

    private void PlaySwingSound()
    {
        if (audioSource == null ||
            swingClip == null)
        {
            return;
        }

        audioSource.PlayOneShot(
            swingClip
        );
    }

    private IEnumerator MoveWeapon(
        Vector3 startPosition,
        Quaternion startRotation,
        Vector3 endPosition,
        Quaternion endRotation,
        Quaternion startVisualRotation,
        Quaternion endVisualRotation,
        float duration,
        float initialElapsed,
        bool detectHits,
        HashSet<ulong> hitTargets)
    {
        bool canDetectHits =
            detectHits &&
            IsServer &&
            weaponHitbox != null &&
            hitTargets != null;

        if (duration <= 0f)
        {
            SetWeaponTransform(
                endPosition,
                endRotation,
                endVisualRotation
            );

            if (canDetectHits)
            {
                CheckCurrentWeaponHitbox(
                    hitTargets
                );
            }

            yield break;
        }

        float elapsedTime =
            Mathf.Clamp(
                initialElapsed,
                0f,
                duration
            );

        float initialRatio =
            Mathf.Clamp01(
                elapsedTime /
                duration
            );

        float initialEasedRatio =
            1f -
            Mathf.Pow(
                1f - initialRatio,
                2f
            );

        Vector3 initialPosition =
            Vector3.Lerp(
                startPosition,
                endPosition,
                initialEasedRatio
            );

        Quaternion initialRotation =
            Quaternion.Slerp(
                startRotation,
                endRotation,
                initialEasedRatio
            );

        Quaternion initialVisualRotation =
            Quaternion.Slerp(
                startVisualRotation,
                endVisualRotation,
                initialEasedRatio
            );

        SetWeaponTransform(
            initialPosition,
            initialRotation,
            initialVisualRotation
        );

        Vector3 previousHitboxCenter =
            Vector3.zero;

        Quaternion previousHitboxRotation =
            Quaternion.identity;

        Vector3 hitboxHalfExtents =
            Vector3.zero;

        if (canDetectHits)
        {
            GetWeaponHitboxPose(
                out previousHitboxCenter,
                out previousHitboxRotation,
                out hitboxHalfExtents
            );
        }

        while (elapsedTime <
               duration)
        {
            elapsedTime +=
                Time.deltaTime;

            float ratio =
                Mathf.Clamp01(
                    elapsedTime /
                    duration
                );

            float easedRatio =
                1f -
                Mathf.Pow(
                    1f - ratio,
                    2f
                );

            Vector3 currentPosition =
                Vector3.Lerp(
                    startPosition,
                    endPosition,
                    easedRatio
                );

            Quaternion currentRotation =
                Quaternion.Slerp(
                    startRotation,
                    endRotation,
                    easedRatio
                );

            Quaternion currentVisualRotation =
                Quaternion.Slerp(
                    startVisualRotation,
                    endVisualRotation,
                    easedRatio
                );

            SetWeaponTransform(
                currentPosition,
                currentRotation,
                currentVisualRotation
            );

            if (canDetectHits)
            {
                GetWeaponHitboxPose(
                    out Vector3
                        currentHitboxCenter,
                    out Quaternion
                        currentHitboxRotation,
                    out hitboxHalfExtents
                );

                CheckWeaponSweep(
                    previousHitboxCenter,
                    previousHitboxRotation,
                    currentHitboxCenter,
                    currentHitboxRotation,
                    hitboxHalfExtents,
                    hitTargets
                );

                previousHitboxCenter =
                    currentHitboxCenter;

                previousHitboxRotation =
                    currentHitboxRotation;
            }

            yield return null;
        }

        SetWeaponTransform(
            endPosition,
            endRotation,
            endVisualRotation
        );

        if (canDetectHits)
        {
            GetWeaponHitboxPose(
                out Vector3
                    finalHitboxCenter,
                out Quaternion
                    finalHitboxRotation,
                out hitboxHalfExtents
            );

            CheckWeaponSweep(
                previousHitboxCenter,
                previousHitboxRotation,
                finalHitboxCenter,
                finalHitboxRotation,
                hitboxHalfExtents,
                hitTargets
            );
        }
    }

    private void SetWeaponTransform(
        Vector3 position,
        Quaternion rotation,
        Quaternion visualRotation)
    {
        weaponPivot.localPosition =
            position;

        weaponPivot.localRotation =
            rotation;

        if (weaponVisualPivot != null)
        {
            weaponVisualPivot.localRotation =
                visualRotation *
                Quaternion.Euler(
                    0f,
                    0f,
                    currentHitConfirmPunchAngle
                );
        }
    }

    public void PlayLocalHitConfirmReaction()
    {
        if (!IsOwner ||
            hitConfirmPunchAngle <= 0f ||
            hitConfirmPunchDuration <= 0f)
        {
            return;
        }

        if (hitConfirmPunchCoroutine != null)
        {
            StopCoroutine(
                hitConfirmPunchCoroutine
            );
        }

        hitConfirmPunchCoroutine =
            StartCoroutine(
                HitConfirmPunchRoutine()
            );
    }

    private IEnumerator HitConfirmPunchRoutine()
    {
        float elapsed = 0f;

        while (elapsed <
               hitConfirmPunchDuration)
        {
            elapsed +=
                Time.unscaledDeltaTime;

            float normalizedTime =
                Mathf.Clamp01(
                    elapsed /
                    hitConfirmPunchDuration
                );

            currentHitConfirmPunchAngle =
                Mathf.Lerp(
                    hitConfirmPunchAngle,
                    0f,
                    normalizedTime
                );

            yield return null;
        }

        currentHitConfirmPunchAngle = 0f;
        hitConfirmPunchCoroutine = null;
    }

    private void StopHitConfirmPunch()
    {
        if (hitConfirmPunchCoroutine != null)
        {
            StopCoroutine(
                hitConfirmPunchCoroutine
            );

            hitConfirmPunchCoroutine = null;
        }

        currentHitConfirmPunchAngle = 0f;
    }

    private void GetWeaponHitboxPose(
        out Vector3 center,
        out Quaternion rotation,
        out Vector3 halfExtents)
    {
        Transform hitboxTransform =
            weaponHitbox.transform;

        center =
            hitboxTransform.TransformPoint(
                weaponHitbox.center
            ) +
            (IsServer
                ? serverAttackPositionOffset
                : Vector3.zero);

        rotation =
            hitboxTransform.rotation;

        Vector3 scale =
            hitboxTransform.lossyScale;

        scale =
            new Vector3(
                Mathf.Abs(scale.x),
                Mathf.Abs(scale.y),
                Mathf.Abs(scale.z)
            );

        halfExtents =
            Vector3.Scale(
                weaponHitbox.size *
                0.5f,
                scale
            );
    }

    private void CheckWeaponSweep(
        Vector3 previousCenter,
        Quaternion previousRotation,
        Vector3 currentCenter,
        Quaternion currentRotation,
        Vector3 halfExtents,
        HashSet<ulong> hitTargets)
    {
        int substeps =
            Mathf.Max(
                1,
                hitCheckSubsteps
            );

        for (int step = 0;
             step <= substeps;
             step++)
        {
            float ratio =
                step /
                (float)substeps;

            Vector3 sampleCenter =
                Vector3.Lerp(
                    previousCenter,
                    currentCenter,
                    ratio
                );

            Quaternion sampleRotation =
                Quaternion.Slerp(
                    previousRotation,
                    currentRotation,
                    ratio
                );

            CheckWeaponHitbox(
                sampleCenter,
                sampleRotation,
                halfExtents,
                hitTargets
            );
        }
    }

    private void CheckCurrentWeaponHitbox(
        HashSet<ulong> hitTargets)
    {
        GetWeaponHitboxPose(
            out Vector3 center,
            out Quaternion rotation,
            out Vector3 halfExtents
        );

        CheckWeaponHitbox(
            center,
            rotation,
            halfExtents,
            hitTargets
        );
    }

    private void CheckWeaponHitbox(
        Vector3 center,
        Quaternion rotation,
        Vector3 halfExtents,
        HashSet<ulong> hitTargets)
    {
        int overlapCount =
            Physics.OverlapBoxNonAlloc(
                center,
                halfExtents,
                hitBuffer,
                rotation,
                spiritHitboxLayer,
                QueryTriggerInteraction.Collide
            );

        for (int i = 0;
             i < overlapCount;
             i++)
        {
            Collider hitCollider =
                hitBuffer[i];

            if (hitCollider == null)
            {
                continue;
            }

            SpiritHitReceiver target =
                hitCollider
                    .GetComponentInParent
                        <SpiritHitReceiver>();

            if (target == null ||
                !target.IsSpawned)
            {
                continue;
            }

            if (target.NetworkObjectId ==
                NetworkObjectId)
            {
                continue;
            }

            if (!hitTargets.Add(
                    target.NetworkObjectId))
            {
                continue;
            }

            target.ReceiveDamage(
                damage,
                OwnerClientId,
                transform.position +
                serverAttackPositionOffset
            );
        }
    }

    private void OnDisable()
    {
        StopHitConfirmPunch();
        CancelCurrentSwing();
    }

    public void CancelCurrentSwing()
    {
        if (swingCoroutine != null)
        {
            StopCoroutine(
                swingCoroutine
            );

            swingCoroutine = null;
        }

        if (weaponPivot != null)
        {
            weaponPivot.localPosition =
                idlePosition;

            weaponPivot.localRotation =
                idleRotation;
        }

        currentHitConfirmPunchAngle = 0f;
        serverAttackPositionOffset = Vector3.zero;

        if (weaponVisualPivot != null)
        {
            weaponVisualPivot.localRotation =
                idleVisualRotation;
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (weaponHitbox == null)
        {
            return;
        }

        GetWeaponHitboxPose(
            out Vector3 center,
            out Quaternion rotation,
            out Vector3 halfExtents
        );

        Matrix4x4 previousMatrix =
            Gizmos.matrix;

        Gizmos.matrix =
            Matrix4x4.TRS(
                center,
                rotation,
                Vector3.one
            );

        Gizmos.DrawWireCube(
            Vector3.zero,
            halfExtents * 2f
        );

        Gizmos.matrix =
            previousMatrix;
    }
}