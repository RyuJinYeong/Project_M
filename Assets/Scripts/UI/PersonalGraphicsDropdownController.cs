using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public sealed class PersonalGraphicsDropdownController : MonoBehaviour
{
    private static readonly string[] DropdownObjectNames =
    {
        "FullScreenModeButton",
        "ResolutionButton",
        "QualityButton",
        "FrameRateButton",
        "ScreenEffectButton"
    };

    private static readonly FullScreenMode[] FullScreenModes =
    {
        FullScreenMode.ExclusiveFullScreen,
        FullScreenMode.FullScreenWindow,
        FullScreenMode.Windowed
    };

    private static readonly Vector2Int[] PreferredResolutions =
    {
        new Vector2Int(1280, 720),
        new Vector2Int(1366, 768),
        new Vector2Int(1600, 900),
        new Vector2Int(1920, 1080),
        new Vector2Int(2560, 1440),
        new Vector2Int(3840, 2160)
    };

    private static readonly int[] FrameRateChoices =
    {
        30,
        60,
        120,
        240,
        -1
    };

    [SerializeField]
    private TMP_Dropdown[] dropdowns =
        new TMP_Dropdown[DropdownObjectNames.Length];

    private readonly List<Vector2Int> resolutionChoices =
        new List<Vector2Int>(PreferredResolutions.Length);

    private bool suppressCallbacks;
    private bool initialized;

    private void Awake()
    {
        Initialize();
    }

    private void OnEnable()
    {
        Initialize();
        Refresh();
    }

    private void OnDestroy()
    {
        UnregisterEvents();
    }

    public void Refresh()
    {
        if (!initialized)
            Initialize();

        suppressCallbacks = true;

        SetDropdownValue(
            0,
            Array.IndexOf(
                FullScreenModes,
                Screen.fullScreenMode
            )
        );
        SetDropdownValue(
            1,
            FindCurrentResolutionIndex()
        );
        SetDropdownValue(
            2,
            QualitySettings.GetQualityLevel()
        );

        RelayConnectionManager relayManager =
            RelayConnectionManager.Instance;
        int frameRate = relayManager != null
            ? relayManager.TargetFrameRate
            : PlayerPrefs.GetInt(
                RelayConnectionManager.TargetFrameRatePreferenceKey,
                Application.targetFrameRate
            );

        SetDropdownValue(
            3,
            Array.IndexOf(FrameRateChoices, frameRate)
        );
        SetDropdownValue(
            4,
            SpiritHitReceiver.UseReducedScreenEffects ? 1 : 0
        );

        suppressCallbacks = false;
    }

    private void Initialize()
    {
        if (initialized)
            return;

        CacheDropdownReferences();
        BuildResolutionChoices();
        PopulateOptions();
        RegisterEvents();
        initialized = true;
    }

    private void CacheDropdownReferences()
    {
        if (dropdowns == null ||
            dropdowns.Length != DropdownObjectNames.Length)
        {
            dropdowns =
                new TMP_Dropdown[DropdownObjectNames.Length];
        }

        for (int i = 0; i < DropdownObjectNames.Length; i++)
        {
            if (dropdowns[i] != null)
                continue;

            Transform target = FindDescendantByName(
                transform,
                DropdownObjectNames[i]
            );

            if (target != null)
                dropdowns[i] = target.GetComponent<TMP_Dropdown>();
        }
    }

    private void BuildResolutionChoices()
    {
        resolutionChoices.Clear();

        Resolution[] supportedResolutions = Screen.resolutions;
        HashSet<long> supportedSizes = new HashSet<long>();

        if (supportedResolutions != null)
        {
            for (int i = 0; i < supportedResolutions.Length; i++)
            {
                supportedSizes.Add(GetResolutionKey(
                    supportedResolutions[i].width,
                    supportedResolutions[i].height
                ));
            }
        }

        for (int i = 0; i < PreferredResolutions.Length; i++)
        {
            Vector2Int resolution = PreferredResolutions[i];

            if (supportedSizes.Count == 0 ||
                supportedSizes.Contains(GetResolutionKey(
                    resolution.x,
                    resolution.y
                )))
            {
                resolutionChoices.Add(resolution);
            }
        }

        if (resolutionChoices.Count == 0)
        {
            resolutionChoices.Add(
                new Vector2Int(Screen.width, Screen.height)
            );
        }
    }

    private void PopulateOptions()
    {
        SetOptions(0, new[]
        {
            "화면 모드  |  전체 화면",
            "화면 모드  |  테두리 없는 창",
            "화면 모드  |  창 모드"
        });

        List<string> resolutionLabels =
            new List<string>(resolutionChoices.Count);

        for (int i = 0; i < resolutionChoices.Count; i++)
        {
            resolutionLabels.Add(
                $"해상도  |  {resolutionChoices[i].x} x " +
                $"{resolutionChoices[i].y}"
            );
        }

        SetOptions(1, resolutionLabels);

        List<string> qualityLabels =
            new List<string>(QualitySettings.names.Length);

        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            qualityLabels.Add(
                $"그래픽 품질  |  {QualitySettings.names[i]}"
            );
        }

        if (qualityLabels.Count == 0)
            qualityLabels.Add("그래픽 품질  |  기본");

        SetOptions(2, qualityLabels);
        SetOptions(3, new[]
        {
            "FPS 제한  |  30 FPS",
            "FPS 제한  |  60 FPS",
            "FPS 제한  |  120 FPS",
            "FPS 제한  |  240 FPS",
            "FPS 제한  |  제한 없음"
        });
        SetOptions(4, new[]
        {
            "화면 효과  |  보통",
            "화면 효과  |  낮음"
        });
    }

    private void RegisterEvents()
    {
        if (dropdowns[0] != null)
            dropdowns[0].onValueChanged.AddListener(SetFullScreenMode);
        if (dropdowns[1] != null)
            dropdowns[1].onValueChanged.AddListener(SetResolution);
        if (dropdowns[2] != null)
            dropdowns[2].onValueChanged.AddListener(SetQualityLevel);
        if (dropdowns[3] != null)
            dropdowns[3].onValueChanged.AddListener(SetTargetFrameRate);
        if (dropdowns[4] != null)
            dropdowns[4].onValueChanged.AddListener(SetScreenEffects);
    }

    private void UnregisterEvents()
    {
        if (dropdowns == null ||
            dropdowns.Length != DropdownObjectNames.Length)
        {
            return;
        }

        if (dropdowns[0] != null)
            dropdowns[0].onValueChanged.RemoveListener(SetFullScreenMode);
        if (dropdowns[1] != null)
            dropdowns[1].onValueChanged.RemoveListener(SetResolution);
        if (dropdowns[2] != null)
            dropdowns[2].onValueChanged.RemoveListener(SetQualityLevel);
        if (dropdowns[3] != null)
            dropdowns[3].onValueChanged.RemoveListener(SetTargetFrameRate);
        if (dropdowns[4] != null)
            dropdowns[4].onValueChanged.RemoveListener(SetScreenEffects);
    }

    private void SetFullScreenMode(int index)
    {
        if (suppressCallbacks ||
            index < 0 ||
            index >= FullScreenModes.Length)
        {
            return;
        }

        FullScreenMode mode = FullScreenModes[index];
        Screen.SetResolution(Screen.width, Screen.height, mode);
        PlayerPrefs.SetInt("Personal.FullScreenMode", (int)mode);
        SaveAndRefresh();
    }

    private void SetResolution(int index)
    {
        if (suppressCallbacks ||
            index < 0 ||
            index >= resolutionChoices.Count)
        {
            return;
        }

        Vector2Int resolution = resolutionChoices[index];
        Screen.SetResolution(
            resolution.x,
            resolution.y,
            Screen.fullScreenMode
        );
        PlayerPrefs.SetInt("Personal.ResolutionWidth", resolution.x);
        PlayerPrefs.SetInt("Personal.ResolutionHeight", resolution.y);
        SaveAndRefresh();
    }

    private void SetQualityLevel(int index)
    {
        if (suppressCallbacks ||
            index < 0 ||
            index >= QualitySettings.names.Length)
        {
            return;
        }

        QualitySettings.SetQualityLevel(index, true);
        PlayerPrefs.SetInt("Personal.QualityLevel", index);
        SaveAndRefresh();
    }

    private void SetTargetFrameRate(int index)
    {
        if (suppressCallbacks ||
            index < 0 ||
            index >= FrameRateChoices.Length)
        {
            return;
        }

        int frameRate = FrameRateChoices[index];
        RelayConnectionManager relayManager =
            RelayConnectionManager.Instance;

        if (relayManager != null)
        {
            relayManager.SetTargetFrameRate(frameRate);
        }
        else
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = frameRate;
            PlayerPrefs.SetInt(
                RelayConnectionManager.TargetFrameRatePreferenceKey,
                frameRate
            );
        }

        SaveAndRefresh();
    }

    private void SetScreenEffects(int index)
    {
        if (suppressCallbacks || index < 0 || index > 1)
            return;

        SpiritHitReceiver.SetReducedScreenEffects(index == 1);
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        PlayerPrefs.Save();
        Refresh();
    }

    private int FindCurrentResolutionIndex()
    {
        for (int i = 0; i < resolutionChoices.Count; i++)
        {
            if (resolutionChoices[i].x == Screen.width &&
                resolutionChoices[i].y == Screen.height)
            {
                return i;
            }
        }

        return 0;
    }

    private void SetDropdownValue(int index, int value)
    {
        if (dropdowns == null ||
            index < 0 ||
            index >= dropdowns.Length ||
            dropdowns[index] == null ||
            dropdowns[index].options.Count == 0)
        {
            return;
        }

        dropdowns[index].SetValueWithoutNotify(
            Mathf.Clamp(
                value,
                0,
                dropdowns[index].options.Count - 1
            )
        );
        dropdowns[index].RefreshShownValue();
    }

    private void SetOptions(int index, IEnumerable<string> labels)
    {
        if (dropdowns == null ||
            index < 0 ||
            index >= dropdowns.Length ||
            dropdowns[index] == null)
        {
            return;
        }

        dropdowns[index].ClearOptions();
        dropdowns[index].AddOptions(new List<string>(labels));
    }

    private static long GetResolutionKey(int width, int height)
    {
        return ((long)width << 32) | (uint)height;
    }

    private static Transform FindDescendantByName(
        Transform root,
        string objectName)
    {
        if (root == null)
            return null;

        if (root.name == objectName)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform result = FindDescendantByName(
                root.GetChild(i),
                objectName
            );

            if (result != null)
                return result;
        }

        return null;
    }
}
