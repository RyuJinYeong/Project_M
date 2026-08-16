using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Unity.Multiplayer.PlayMode;


public class MppmTestAutoConnect : MonoBehaviour
{
    [SerializeField] private NetworkManager networkManager;
    [SerializeField] private float clientConnectDelay = 1f;

    private IEnumerator Start()
    {
#if UNITY_EDITOR
        IReadOnlyList<string> tags = CurrentPlayer.Tags;

        if (HasPlayerTag(tags, "Host"))
        {
            networkManager.StartHost();
            yield break;
        }

        if (HasPlayerTag(tags, "Client"))
        {
            yield return new WaitForSeconds(clientConnectDelay);

            networkManager.StartClient();
        }
#endif

        yield break;
    }

    private bool HasPlayerTag(IReadOnlyList<string> tags, string targetTag)
    {
        if (tags == null || string.IsNullOrEmpty(targetTag))
            return false;

        for (int i = 0; i < tags.Count; i++)
        {
            if (string.Equals(tags[i], targetTag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}