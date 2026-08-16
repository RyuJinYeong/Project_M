using UnityEngine;
using Unity.Netcode;

public class PersistentNetworkRoot : MonoBehaviour
{
    private static PersistentNetworkRoot instance;

    private void Awake()
    {
        if (instance != null &&
            instance != this)
        {
            NetworkManager existingManager =
                instance.GetComponent<NetworkManager>();

            if (existingManager != null)
            {
                Destroy(gameObject);
                return;
            }

            Destroy(instance.gameObject);
        }

        instance = this;

        transform.SetParent(null);
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }
}
