using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerSpiritMovement : NetworkBehaviour
{
    [Header("이동")]
    [SerializeField]
    private float moveSpeed = 3f;

    [SerializeField]
    private float gravity = -20f;

    [Header("점프")]
    [Min(0.1f)]
    [SerializeField]
    private float jumpHeight = 0.6f;

    [SerializeField]
    private Key jumpKey = Key.Space;

    [Header("시점 참조")]
    [Tooltip("이동 방향의 기준이 되는 PlayerCamera Transform입니다.")]
    [SerializeField]
    private Transform viewTransform;

    private CharacterController characterController;
    private PlayerSpirit playerSpirit;
    private SpiritHitReceiver hitReceiver;
    private float verticalVelocity;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
        playerSpirit = GetComponent<PlayerSpirit>();
        hitReceiver = GetComponent<SpiritHitReceiver>();
    }

    public override void OnNetworkSpawn()
    {
        characterController.enabled = IsOwner;
    }

    public override void OnNetworkDespawn()
    {
        if (characterController != null)
        {
            characterController.enabled = false;
        }
    }

    private void Update()
    {
        if (!IsOwner ||
            !IsSpawned ||
            playerSpirit == null ||
            !playerSpirit.CanMove)
        {
            return;
        }

        HandleMovement();
    }

    private void HandleMovement()
    {
        Keyboard keyboard = Keyboard.current;

        if (keyboard == null)
        {
            ApplyVerticalMovement(
                Vector3.zero,
                false
            );

            return;
        }

        Vector2 input = Vector2.zero;

        if (keyboard.wKey.isPressed)
        {
            input.y += 1f;
        }

        if (keyboard.sKey.isPressed)
        {
            input.y -= 1f;
        }

        if (keyboard.aKey.isPressed)
        {
            input.x -= 1f;
        }

        if (keyboard.dKey.isPressed)
        {
            input.x += 1f;
        }

        if (input.sqrMagnitude > 1f)
        {
            input.Normalize();
        }

        Vector3 forward = GetViewForward();

        Vector3 right = GetViewRight();

        Vector3 moveDirection = forward * input.y + right * input.x;

        if (moveDirection.sqrMagnitude > 1f)
        {
            moveDirection.Normalize();
        }

        bool jumpPressed =
            keyboard[jumpKey].wasPressedThisFrame;

        ApplyVerticalMovement(
            moveDirection,
            jumpPressed
        );
    }

    private Vector3 GetViewForward()
    {
        Vector3 forward = viewTransform != null ? viewTransform.forward : transform.forward;

        forward.y = 0f;

        if (forward.sqrMagnitude < 0.001f)
        {
            forward = transform.forward;

            forward.y = 0f;
        }

        return forward.normalized;
    }

    private Vector3 GetViewRight()
    {
        Vector3 right = viewTransform != null ? viewTransform.right : transform.right;

        right.y = 0f;

        if (right.sqrMagnitude < 0.001f)
        {
            right = transform.right;

            right.y = 0f;
        }

        return right.normalized;
    }

    private void ApplyVerticalMovement(
        Vector3 moveDirection,
        bool jumpPressed)
    {
        bool isGrounded =
            characterController.isGrounded;

        if (isGrounded &&
            verticalVelocity < 0f)
        {
            verticalVelocity = -2f;
        }

        if (isGrounded &&
            jumpPressed)
        {
            verticalVelocity =
                Mathf.Sqrt(
                    jumpHeight *
                    2f *
                    Mathf.Abs(gravity)
                );
        }
        else
        {
            verticalVelocity +=
                gravity *
                Time.deltaTime;
        }

        Vector3 movement =
            moveDirection *
            moveSpeed *
            (
                hitReceiver != null
                    ? hitReceiver.LocalMovementSpeedMultiplier
                    : 1f
            );

        movement.y =
            verticalVelocity;

        characterController.Move(
            movement *
            Time.deltaTime
        );
    }
}
