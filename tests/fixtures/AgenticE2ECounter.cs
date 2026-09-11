using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class AgenticE2ECounter : UdonSharpBehaviour
{
    public GameObject display;
    public int step;

    [UdonSynced] public int count;

    public override void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        count = count + step;
        RequestSerialization();
        _Apply();
    }

    public override void OnDeserialization()
    {
        _Apply();
    }

    private void _Apply()
    {
        // Telemetry for real-client verification: Udon cannot send data out, so a tagged log line is the
        // only channel verify_multiclient can read back.
        Debug.Log("[AGENTTEL]{\"counter\":" + count + "}");
        if (display != null) display.SetActive(count > 0);
    }
}
