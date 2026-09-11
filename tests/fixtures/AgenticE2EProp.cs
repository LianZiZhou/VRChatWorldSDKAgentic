using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class AgenticE2EProp : UdonSharpBehaviour
{
    public GameObject glow;

    [UdonSynced] public bool held;
    [UdonSynced] public int pickupCount;

    public override void OnPickup()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        held = true;
        pickupCount = pickupCount + 1;
        RequestSerialization();
        _Apply();
    }

    public override void OnDrop()
    {
        held = false;
        RequestSerialization();
        _Apply();
    }

    public override void OnDeserialization()
    {
        _Apply();
    }

    private void _Apply()
    {
        Debug.Log("[AGENTTEL]{\"prop\":{\"held\":" + (held ? "true" : "false") + ",\"pickups\":" + pickupCount + "}}");
        if (glow != null) glow.SetActive(held);
    }
}
