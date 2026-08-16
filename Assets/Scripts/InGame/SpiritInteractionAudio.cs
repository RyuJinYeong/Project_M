using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public enum SpiritInteractionSoundType : byte
{
    Door,
    Body,
    Exorcism,
    SpiritVanish,
    Lamp
}

[RequireComponent(typeof(PlayerSpirit))]
public class SpiritInteractionAudio : NetworkBehaviour
{
    private const ulong NoClientId = ulong.MaxValue;
    private const float PublicInteractionVolumeScale = 0.35f;

    [Header("효과음")]
    [SerializeField] private AudioClip doorInteractionClip;
    [SerializeField] private AudioClip bodyInteractionClip;
    [SerializeField] private AudioClip exorcismInteractionClip;
    [SerializeField] private AudioClip spiritVanishClip;
    [SerializeField] private AudioClip lampInteractionClip;
    [SerializeField] private AudioClip heartbeatLoopClip;

    [Header("주변 효과음")]
    [Range(0f, 1f)]
    [SerializeField] private float spatialVolume = 1f;

    [Min(0.1f)]
    [SerializeField] private float spatialMinDistance = 2f;

    [Min(0.1f)]
    [SerializeField] private float spatialMaxDistance = 15f;

    [Min(0f)]
    [SerializeField] private float minimumSpatialSoundInterval = 0.35f;

    [Header("대상자 심장박동")]
    [SerializeField] private AudioSource heartbeatAudioSource;

    [Range(0f, 1f)]
    [SerializeField] private float heartbeatVolume = 0.75f;

    [Min(1f)]
    [SerializeField] private float heartbeatSafetyDuration = 10f;

    private readonly HashSet<ulong> heartbeatActors =
        new HashSet<ulong>();

    private readonly Dictionary<ulong, float> heartbeatExpireTimes =
        new Dictionary<ulong, float>();

    private PlayerSpirit playerSpirit;

    private bool serverRoleInteractionActive;
    private ulong serverHeartbeatTargetClientId = NoClientId;
    private double nextServerSpatialSoundTime;

    private void Awake()
    {
        playerSpirit = GetComponent<PlayerSpirit>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
            ConfigureHeartbeatAudioSource();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
            StopServerHeartbeat();

        ClearHeartbeatLocal();
    }

    private void Update()
    {
        if (!IsOwner || heartbeatActors.Count == 0)
            return;

        if (MatchManager.Instance != null &&
            MatchManager.Instance.CurrentPhase != MatchPhase.NightAction)
        {
            ClearHeartbeatLocal();
            return;
        }

        float currentTime = Time.unscaledTime;
        List<ulong> expiredActors = null;

        foreach (KeyValuePair<ulong, float> pair in heartbeatExpireTimes)
        {
            if (currentTime < pair.Value)
                continue;

            expiredActors ??= new List<ulong>();
            expiredActors.Add(pair.Key);
        }

        if (expiredActors == null)
            return;

        for (int i = 0; i < expiredActors.Count; i++)
        {
            ulong actorClientId = expiredActors[i];
            heartbeatActors.Remove(actorClientId);
            heartbeatExpireTimes.Remove(actorClientId);
        }

        RefreshHeartbeatPlayback();
    }

    public void BeginRoleInteraction(
        SpiritInteractionSoundType soundType,
        ulong targetClientId)
    {
        if (!IsOwner || !IsSpawned)
            return;

        BeginRoleInteractionRpc(
            soundType,
            targetClientId
        );
    }

    public void EndRoleInteraction()
    {
        if (!IsOwner || !IsSpawned)
            return;

        EndRoleInteractionRpc();
    }

    public void PlayServerSpatialSound(
        SpiritInteractionSoundType soundType,
        Vector3 position)
    {
        if (!IsServer || !IsSpawned)
            return;

        PlaySpatialSoundRpc(
            soundType,
            position
        );
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Owner
    )]
    private void BeginRoleInteractionRpc(
        SpiritInteractionSoundType soundType,
        ulong targetClientId)
    {
        if (!IsServer ||
            playerSpirit == null ||
            MatchManager.Instance == null ||
            MatchManager.Instance.CurrentPhase != MatchPhase.NightAction ||
            !playerSpirit.CanUseRoleAction)
        {
            return;
        }

        if (soundType != SpiritInteractionSoundType.Door &&
            soundType != SpiritInteractionSoundType.Body &&
            soundType != SpiritInteractionSoundType.Exorcism)
        {
            return;
        }

        if (serverRoleInteractionActive)
            StopServerHeartbeat();

        serverRoleInteractionActive = true;

        double currentTime =
            NetworkManager.ServerTime.Time;

        if (currentTime >= nextServerSpatialSoundTime)
        {
            nextServerSpatialSoundTime =
                currentTime +
                minimumSpatialSoundInterval;

            PlaySpatialSoundRpc(
                soundType,
                transform.position
            );
        }

        if (soundType != SpiritInteractionSoundType.Body ||
            targetClientId == NoClientId ||
            !MatchManager.Instance.IsPublicPlayerAlive(targetClientId))
        {
            return;
        }

        if (!MatchManager.Instance.TryGetPlayerSpirit(
                targetClientId,
                out PlayerSpirit targetSpirit))
        {
            return;
        }

        SpiritInteractionAudio targetAudio =
            targetSpirit.GetComponent<SpiritInteractionAudio>();

        if (targetAudio == null)
            return;

        serverHeartbeatTargetClientId =
            targetClientId;

        targetAudio.SetHeartbeatFromServer(
            OwnerClientId,
            true,
            targetClientId
        );
    }

    [Rpc(
        SendTo.Server,
        InvokePermission = RpcInvokePermission.Owner
    )]
    private void EndRoleInteractionRpc()
    {
        if (!IsServer)
            return;

        serverRoleInteractionActive = false;
        StopServerHeartbeat();
    }

    private void StopServerHeartbeat()
    {
        if (!IsServer ||
            serverHeartbeatTargetClientId == NoClientId ||
            MatchManager.Instance == null)
        {
            serverHeartbeatTargetClientId = NoClientId;
            return;
        }

        ulong targetClientId =
            serverHeartbeatTargetClientId;

        serverHeartbeatTargetClientId =
            NoClientId;

        if (!MatchManager.Instance.TryGetPlayerSpirit(
                targetClientId,
                out PlayerSpirit targetSpirit))
        {
            return;
        }

        SpiritInteractionAudio targetAudio =
            targetSpirit.GetComponent<SpiritInteractionAudio>();

        if (targetAudio == null)
            return;

        targetAudio.SetHeartbeatFromServer(
            OwnerClientId,
            false,
            targetClientId
        );
    }

    private void SetHeartbeatFromServer(
        ulong actorClientId,
        bool active,
        ulong targetClientId)
    {
        if (!IsServer || !IsSpawned)
            return;

        SetHeartbeatStateRpc(
            actorClientId,
            active,
            RpcTarget.Single(
                targetClientId,
                RpcTargetUse.Temp
            )
        );
    }

    [Rpc(
        SendTo.SpecifiedInParams,
        InvokePermission = RpcInvokePermission.Server
    )]
    private void SetHeartbeatStateRpc(
        ulong actorClientId,
        bool active,
        RpcParams rpcParams = default)
    {
        if (!IsOwner)
            return;

        if (active)
        {
            heartbeatActors.Add(actorClientId);
            heartbeatExpireTimes[actorClientId] =
                Time.unscaledTime +
                heartbeatSafetyDuration;
        }
        else
        {
            heartbeatActors.Remove(actorClientId);
            heartbeatExpireTimes.Remove(actorClientId);
        }

        RefreshHeartbeatPlayback();
    }

    [Rpc(SendTo.Everyone)]
    private void PlaySpatialSoundRpc(
        SpiritInteractionSoundType soundType,
        Vector3 position)
    {
        AudioClip clip = GetClip(soundType);

        if (clip == null)
            return;

        GameObject soundObject =
            new GameObject(
                $"InteractionSound_{soundType}"
            );

        soundObject.transform.position =
            position;

        AudioSource source =
            soundObject.AddComponent<AudioSource>();

        source.playOnAwake = false;
        source.loop = false;
        source.clip = clip;
        source.volume = Mathf.Clamp01(
            spatialVolume * PublicInteractionVolumeScale
        );
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.rolloffMode =
            AudioRolloffMode.Logarithmic;
        source.minDistance =
            Mathf.Max(0.1f, spatialMinDistance);
        source.maxDistance =
            Mathf.Max(
                source.minDistance,
                spatialMaxDistance
            );

        source.Play();

        Destroy(
            soundObject,
            clip.length + 0.25f
        );
    }

    private AudioClip GetClip(
        SpiritInteractionSoundType soundType)
    {
        switch (soundType)
        {
            case SpiritInteractionSoundType.Door:
                return doorInteractionClip;

            case SpiritInteractionSoundType.Body:
                return bodyInteractionClip;

            case SpiritInteractionSoundType.Exorcism:
                return exorcismInteractionClip;

            case SpiritInteractionSoundType.SpiritVanish:
                return spiritVanishClip;

            case SpiritInteractionSoundType.Lamp:
                return lampInteractionClip;

            default:
                return null;
        }
    }

    private void ConfigureHeartbeatAudioSource()
    {
        if (heartbeatAudioSource == null)
            heartbeatAudioSource =
                gameObject.AddComponent<AudioSource>();

        heartbeatAudioSource.playOnAwake = false;
        heartbeatAudioSource.loop = true;
        heartbeatAudioSource.spatialBlend = 0f;
        heartbeatAudioSource.clip =
            heartbeatLoopClip;
        heartbeatAudioSource.volume =
            heartbeatVolume;
    }

    private void RefreshHeartbeatPlayback()
    {
        ConfigureHeartbeatAudioSource();

        bool shouldPlay =
            heartbeatActors.Count > 0 &&
            heartbeatLoopClip != null;

        if (shouldPlay)
        {
            if (!heartbeatAudioSource.isPlaying)
                heartbeatAudioSource.Play();

            return;
        }

        heartbeatAudioSource.Stop();
    }

    private void ClearHeartbeatLocal()
    {
        heartbeatActors.Clear();
        heartbeatExpireTimes.Clear();

        if (heartbeatAudioSource != null)
            heartbeatAudioSource.Stop();
    }
}
