using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PlayerIdentityLabel : MonoBehaviour
{
    [Header("공통 팔레트")]
    [SerializeField] private PlayerVisualPalette visualPalette;

    [Header("표시 오브젝트")]
    [SerializeField] private GameObject contentRoot;
    [SerializeField] private Image playerIconImage;
    [SerializeField] private TMP_Text playerNameText;

    [Tooltip("투표 UI에서 사용할 선택적 득표수 텍스트입니다. 다른 용도의 PlayerIdentityLabel에서는 비워둬도 됩니다.")]
    [SerializeField] private TMP_Text voteCountText;

    [Header("닉네임 색상")]
    [SerializeField] private bool colorNameText;
    [SerializeField] private Color defaultNameColor = Color.white;

    private void Awake()
    {
        CacheReferences();
    }

    private void Reset()
    {
        CacheReferences();
    }

    public void SetPlayer(ulong clientId, string playerName)
    {
        CacheReferences();

        if (string.IsNullOrWhiteSpace(playerName))
        {
            Clear();
            return;
        }

        Color playerColor = visualPalette != null
            ? visualPalette.GetColor(clientId)
            : Color.white;

        if (contentRoot != null)
            contentRoot.SetActive(true);

        if (playerNameText != null)
        {
            playerNameText.text = playerName;
            playerNameText.color = colorNameText
                ? playerColor
                : defaultNameColor;
        }

        if (playerIconImage != null)
        {
            if (visualPalette != null &&
                visualPalette.PlayerIcon != null)
            {
                playerIconImage.sprite =
                    visualPalette.PlayerIcon;
            }

            playerIconImage.color = playerColor;
            playerIconImage.gameObject.SetActive(true);
        }
    }

    public void SetPlainText(string displayText)
    {
        CacheReferences();

        if (string.IsNullOrWhiteSpace(displayText))
        {
            Clear();
            return;
        }

        if (contentRoot != null)
            contentRoot.SetActive(true);

        if (playerNameText != null)
        {
            playerNameText.text = displayText;
            playerNameText.color = defaultNameColor;
        }

        if (playerIconImage != null)
            playerIconImage.gameObject.SetActive(false);

        if (voteCountText != null)
        {
            voteCountText.text = string.Empty;
            voteCountText.gameObject.SetActive(false);
        }
    }

    public void SetVoteCount(
        int voteCount,
        bool visible)
    {
        CacheReferences();

        if (voteCountText == null)
            return;

        voteCountText.gameObject.SetActive(
            visible
        );

        if (visible)
        {
            voteCountText.text =
                $"{Mathf.Max(0, voteCount)}표";
        }
    }

    public void Clear()
    {
        CacheReferences();

        if (playerNameText != null)
            playerNameText.text = string.Empty;

        if (voteCountText != null)
        {
            voteCountText.text =
                string.Empty;

            voteCountText.gameObject
                .SetActive(false);
        }

        if (contentRoot != null)
            contentRoot.SetActive(false);
    }

    private void CacheReferences()
    {
        if (contentRoot == null)
            contentRoot = gameObject;

        if (playerNameText == null)
            playerNameText = FindNameText();

        if (playerIconImage == null)
            playerIconImage = FindIconImage();

        if (voteCountText == null)
            voteCountText = FindVoteCountText();
    }

    private TMP_Text FindVoteCountText()
    {
        TMP_Text[] texts =
            GetComponentsInChildren<TMP_Text>(true);

        for (int i = 0;
             i < texts.Length;
             i++)
        {
            string objectName =
                texts[i].gameObject.name;

            if (objectName.IndexOf(
                    "VoteCount",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return texts[i];
            }
        }

        return null;
    }

    private TMP_Text FindNameText()
    {
        TMP_Text[] texts =
            GetComponentsInChildren<TMP_Text>(true);

        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i].gameObject.name.IndexOf(
                    "Name",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return texts[i];
            }
        }

        return texts.Length > 0 ? texts[0] : null;
    }

    private Image FindIconImage()
    {
        Image[] images =
            GetComponentsInChildren<Image>(true);

        for (int i = 0; i < images.Length; i++)
        {
            string objectName =
                images[i].gameObject.name;

            if (objectName.IndexOf(
                    "Icon",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return images[i];
            }
        }

        return images.Length > 0 ? images[0] : null;
    }
}