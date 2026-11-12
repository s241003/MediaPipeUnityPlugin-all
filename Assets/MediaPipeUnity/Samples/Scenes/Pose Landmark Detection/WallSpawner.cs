using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 壁を順に(またはランダムに)出す。GameManager が SetActive で開始/停止する
public class WallSpawner : MonoBehaviour
{
    public enum PickMode { Sequential, Random }

    [Header("壁の種類(ここに WallData を追加していく)")]
    public List<WallData> walls = new List<WallData>();
    public PickMode pickMode = PickMode.Sequential;

    [Header("経路(2点のzの差で、進む向きを決める)")]
    public Transform spawnPoint;                // 奥(出現位置。x,yが壁の位置になる)
    public Transform judgePoint;                // プレイヤーの平面(Avatar の Plane Z と同じz)
    public float despawnDistance = 3f;          // 通り過ぎてから消すまでの距離

    [Header("進行")]
    public float firstDelay = 2f;
    public float interval = 2f;                 // 壁が通り過ぎてから次を出すまで
    public float speedUpPerWall = 0.05f;        // 1枚ごとに速度倍率へ足す量
    public float maxSpeedMultiplier = 2f;

    [Header("レイヤー名")]
    public string wallLayerName = "Wall";

    int _index, _count;

    // Start ではなく OnEnable:GameManager が ON/OFF しても、毎回最初から動く
    void OnEnable() { _index = 0; _count = 0; StartCoroutine(Loop()); }
    void OnDisable() { StopAllCoroutines(); }

    IEnumerator Loop()
    {
        yield return new WaitForSeconds(firstDelay);
        while (true)
        {
            var data = Pick();
            if (data == null || data.prefab == null)
            {
                Debug.LogError("WallSpawner: WallData か、その Prefab が未設定です", this);
                yield break;
            }

            float mult = Mathf.Min(1f + speedUpPerWall * _count, maxSpeedMultiplier);
            _count++;
            var runner = Spawn(data, mult);

            yield return new WaitUntil(() => runner == null || runner.PassedPlane);
            yield return new WaitForSeconds(interval);
        }
    }

    WallData Pick()
    {
        if (walls.Count == 0) return null;
        if (pickMode == PickMode.Sequential) return walls[_index++ % walls.Count];

        float total = 0f;
        foreach (var w in walls) if (w != null) total += Mathf.Max(0f, w.weight);
        float r = Random.value * total;
        foreach (var w in walls)
        {
            if (w == null) continue;
            r -= Mathf.Max(0f, w.weight);
            if (r <= 0f) return w;
        }
        return walls[walls.Count - 1];
    }

    WallRunner Spawn(WallData data, float speedMultiplier)
    {
        var pos = spawnPoint.position + new Vector3(data.positionOffset.x, data.positionOffset.y, 0f);
        var go = Instantiate(data.prefab, pos, spawnPoint.rotation * Quaternion.Euler(data.rotationOffset));
        go.name = data.displayName;
        go.transform.localScale *= data.uniformScale;

        int layer = LayerMask.NameToLayer(wallLayerName);
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;

        if (go.GetComponent<WallJudge>() == null) go.AddComponent<WallJudge>();   // 採点役

        var runner = go.AddComponent<WallRunner>();
        float dirSign = Mathf.Sign(judgePoint.position.z - spawnPoint.position.z);
        runner.Init(dirSign, data.speed * speedMultiplier, despawnDistance, judgePoint.position.z);
        return runner;
    }
}