using Unity.Netcode.Components;
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("Netcode/Owner Network Transform")]
public class OwnerNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative()
    {
        return false;
    }
}