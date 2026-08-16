using UnityEngine;
using UnityEngine.UI;

public class RoleActionProgressUI : MonoBehaviour
{
    public static RoleActionProgressUI Instance { get; private set; }

    [Header("Cursor")]
    [SerializeField] private GameObject cursorRoot;
    [SerializeField] private Image cursorBaseImage;
    [SerializeField] private Image cursorFillImage;

    [Header("Appearance")]
    [Range(0f, 1f)]
    [SerializeField] private float idleCursorAlpha = 0.3f;

    [Range(0.25f, 1f)]
    [SerializeField] private float roleActionCursorScale = 0.75f;

    [Range(0.25f, 1f)]
    [SerializeField] private float nightDutyCursorScale = 0.5f;

    private Sprite currentActionIcon;
    private Vector3 defaultCursorScale = Vector3.one;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (cursorRoot != null)
            defaultCursorScale = cursorRoot.transform.localScale;

        ConfigureImages();
        Hide();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void ConfigureImages()
    {
        if (cursorBaseImage != null)
        {
            cursorBaseImage.type = Image.Type.Simple;
            cursorBaseImage.preserveAspect = true;
            cursorBaseImage.raycastTarget = false;

            Color baseColor = cursorBaseImage.color;
            baseColor.a = idleCursorAlpha;
            cursorBaseImage.color = baseColor;
        }

        if (cursorFillImage != null)
        {
            cursorFillImage.type = Image.Type.Filled;
            cursorFillImage.fillMethod = Image.FillMethod.Radial360;
            cursorFillImage.fillOrigin = (int)Image.Origin360.Top;
            cursorFillImage.fillClockwise = true;
            cursorFillImage.fillAmount = 0f;
            cursorFillImage.preserveAspect = true;
            cursorFillImage.raycastTarget = false;

            Color fillColor = cursorFillImage.color;
            fillColor.a = 1f;
            cursorFillImage.color = fillColor;
        }
    }

    public void Show(Sprite actionIcon)
    {
        Show(actionIcon, roleActionCursorScale);
    }

    public void ShowNightDuty(Sprite actionIcon)
    {
        Show(actionIcon, nightDutyCursorScale);
    }

    private void Show(
        Sprite actionIcon,
        float scaleMultiplier)
    {
        if (actionIcon == null)
        {
            Hide();
            return;
        }

        if (currentActionIcon != actionIcon)
        {
            currentActionIcon = actionIcon;

            if (cursorBaseImage != null)
                cursorBaseImage.sprite = currentActionIcon;

            if (cursorFillImage != null)
                cursorFillImage.sprite = currentActionIcon;
        }

        if (cursorRoot != null)
        {
            cursorRoot.transform.localScale =
                defaultCursorScale * Mathf.Clamp(
                    scaleMultiplier,
                    0.25f,
                    1f
                );
        }

        if (cursorRoot != null && !cursorRoot.activeSelf)
            cursorRoot.SetActive(true);

        if (cursorBaseImage != null)
            cursorBaseImage.enabled = true;

        if (cursorFillImage != null)
            cursorFillImage.enabled = true;
    }

    public void SetProgress(float progress)
    {
        if (cursorFillImage != null)
            cursorFillImage.fillAmount = Mathf.Clamp01(progress);
    }

    public void Hide()
    {
        currentActionIcon = null;

        if (cursorRoot != null)
        {
            cursorRoot.transform.localScale =
                defaultCursorScale;
        }

        if (cursorFillImage != null)
            cursorFillImage.fillAmount = 0f;

        if (cursorRoot != null)
            cursorRoot.SetActive(false);
    }
}
