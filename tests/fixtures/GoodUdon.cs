using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class GoodUdon : UdonSharpBehaviour
{
    public GameObject target;
    public float speed;
    public const int MaxTries = 3;

    [UdonSynced] public bool isOn;
    [UdonSynced] public Vector3 offset;

    public override void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        isOn = !isOn;
        RequestSerialization();
        _Apply();
    }

    public override void OnDeserialization()
    {
        _Apply();
    }

    private void _Apply()
    {
        if (target != null) target.SetActive(isOn);
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (player != null) _Apply();
    }
}
