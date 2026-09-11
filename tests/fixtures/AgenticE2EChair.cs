using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AgenticE2EChair : UdonSharpBehaviour
{
    public GameObject menu;
    public int sitCount;

    public override void OnStationEntered(VRCPlayerApi player)
    {
        // Station events fire for remote players too, and the menu is local UI, so only the local player's
        // entry should touch it.
        if (player == null || !player.isLocal) return;
        sitCount = sitCount + 1;
        if (menu != null) menu.SetActive(true);
        Debug.Log("[AGENTTEL]{\"chair\":{\"seated\":true,\"sits\":" + sitCount + "}}");
    }

    public override void OnStationExited(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        if (menu != null) menu.SetActive(false);
        Debug.Log("[AGENTTEL]{\"chair\":{\"seated\":false,\"sits\":" + sitCount + "}}");
    }
}
