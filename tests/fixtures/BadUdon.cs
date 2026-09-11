using System.Collections.Generic;
using System.Linq;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class BadUdon : UdonSharpBehaviour
{
    public static int sharedCounter;
    [UdonSynced] public int[] scores;
    [UdonSynced] public Vector3Int tile;
    private List<int> items;
    private int[,] grid;

    public BadUdon() { }

    void Start()
    {
        try { Debug.Log("x"); } catch { }
        var filtered = from i in new int[] { 1, 2 } select i;
        System.Func<int, int> add = x => x + 1;
        StartCoroutine("Nope");
        throw new System.Exception("no");
    }

    public T Echo<T>(T value) { return value; }
    public void NetworkReachable() { }
}
