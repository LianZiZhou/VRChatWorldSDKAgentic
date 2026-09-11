using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components.Video;
using VRC.SDK3.Video.Components.Base;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AgenticE2EVideo : UdonSharpBehaviour
{
    public BaseVRCVideoPlayer videoPlayer;
    public GameObject playingIndicator;

    // Udon cannot build a VRCUrl at runtime, which is why every video world carries its URLs as fields.
    public VRCUrl url;

    public bool isPlaying;

    public override void Interact()
    {
        if (videoPlayer == null) return;
        if (isPlaying) videoPlayer.Stop();
        else videoPlayer.PlayURL(url);
    }

    public override void OnVideoStart()
    {
        isPlaying = true;
        _Apply();
    }

    public override void OnVideoEnd()
    {
        isPlaying = false;
        _Apply();
    }

    public override void OnVideoError(VideoError videoError)
    {
        isPlaying = false;
        Debug.Log("[AGENTTEL]{\"video\":{\"error\":\"" + videoError.ToString() + "\"}}");
        _Apply();
    }

    private void _Apply()
    {
        Debug.Log("[AGENTTEL]{\"video\":{\"playing\":" + (isPlaying ? "true" : "false") + "}}");
        if (playingIndicator != null) playingIndicator.SetActive(isPlaying);
    }
}
