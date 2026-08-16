using System.Collections;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(RoleActionTarget))]
public class PlayerBodyColorView : NetworkBehaviour
{
    [Header("공통 팔레트")]
    [SerializeField]
    private PlayerVisualPalette visualPalette;

    [Header("색상을 적용할 렌더러")]
    [SerializeField]
    private Renderer[] targetRenderers;

    [Header("셰이더 색상 프로퍼티")]
    [SerializeField]
    private string colorPropertyName = "_BaseColor";

    private RoleActionTarget roleActionTarget;
    private MaterialPropertyBlock propertyBlock;

    private void Awake()
    {
        roleActionTarget =
            GetComponent<RoleActionTarget>();

        propertyBlock =
            new MaterialPropertyBlock();

        if (targetRenderers == null ||
            targetRenderers.Length == 0)
        {
            targetRenderers =
                GetComponentsInChildren<Renderer>(true);
        }
    }

    private void Reset()
    {
        roleActionTarget =
            GetComponent<RoleActionTarget>();

        targetRenderers =
            GetComponentsInChildren<Renderer>(true);
    }

    public override void OnNetworkSpawn()
    {
        StartCoroutine(
            ApplyColorWhenTargetReady()
        );
    }

    private IEnumerator ApplyColorWhenTargetReady()
    {
        while (IsSpawned)
        {
            if (roleActionTarget != null &&
                roleActionTarget.TryGetTargetClientId(
                    out ulong targetClientId))
            {
                ApplyPlayerColor(targetClientId);
                yield break;
            }

            yield return null;
        }
    }

    private void ApplyPlayerColor(
        ulong targetClientId)
    {
        if (visualPalette == null)
        {
            Debug.LogError(
                $"{name}의 PlayerVisualPalette가 연결되지 않았습니다."
            );

            return;
        }

        Color playerColor =
            visualPalette.GetColor(targetClientId);

        int requestedPropertyId =
            Shader.PropertyToID(colorPropertyName);

        int fallbackPropertyId =
            Shader.PropertyToID("_Color");

        for (int i = 0;
             i < targetRenderers.Length;
             i++)
        {
            Renderer targetRenderer =
                targetRenderers[i];

            if (targetRenderer == null ||
                targetRenderer.sharedMaterial == null)
            {
                continue;
            }

            int propertyId;

            if (targetRenderer.sharedMaterial.HasProperty(
                    requestedPropertyId))
            {
                propertyId =
                    requestedPropertyId;
            }
            else if (targetRenderer.sharedMaterial.HasProperty(
                         fallbackPropertyId))
            {
                propertyId =
                    fallbackPropertyId;
            }
            else
            {
                continue;
            }

            targetRenderer.GetPropertyBlock(
                propertyBlock
            );

            propertyBlock.SetColor(
                propertyId,
                playerColor
            );

            targetRenderer.SetPropertyBlock(
                propertyBlock
            );
        }
    }
}