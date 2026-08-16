using UnityEngine;

[CreateAssetMenu(
    fileName = "PlayerVisualPalette",
    menuName = "Mafia/Player Visual Palette"
)]
public class PlayerVisualPalette : ScriptableObject
{
    [SerializeField] private Sprite playerIcon;

    [SerializeField]
    private Color[] playerColors =
    {
        new Color32(232, 93, 102, 255),  // 0: »¡°­
        new Color32(91, 155, 213, 255),  // 1: ÆÄ¶û
        new Color32(170, 105, 214, 255), // 2: º¸¶ó
        new Color32(82, 196, 94, 255),   // 3: ÃÊ·Ï
        new Color32(230, 126, 34, 255),  // 4: ÁÖÈ²
        new Color32(67, 176, 189, 255),  // 5: Ã»·Ï
        new Color32(215, 195, 68, 255),  // 6: ³ë¶û
        new Color32(211, 86, 151, 255),  // 7: ºÐÈ«
        new Color32(170, 170, 170, 255), // 8: È¸»ö
        new Color32(126, 87, 194, 255),  // 9 
        new Color32(38, 166, 154, 255),  // 10
        new Color32(141, 110, 99, 255),  // 11
        new Color32(120, 144, 156, 255)  // 12
    };

    public Sprite PlayerIcon => playerIcon;
    public int ColorCount => playerColors != null ? playerColors.Length : 0;

    public Color GetColor(ulong clientId)
    {
        if (playerColors == null || playerColors.Length == 0)
            return Color.white;

        int colorIndex =
            (int)(clientId % (ulong)playerColors.Length);

        return playerColors[colorIndex];
    }
}