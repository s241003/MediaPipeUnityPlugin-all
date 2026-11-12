using System.Collections.Generic;
using UnityEngine;

// 壁(Blenderのプレハブ)に付ける。当たりと、ポーズの一致度(穴の充填率)を測る。
public class WallJudge : MonoBehaviour
{
    public enum Rating { Bad, Good, Great }

    public struct Result
    {
        public string wallName;
        public bool hit;
        public float coverage;   // 0〜1:穴を、体でどれだけ埋めたか
        public Rating rating;
        public string parts;     // 当たった部位(例: 左前腕、頭)
    }

    public static event System.Action<Result> OnWallResolved;
    static readonly List<WallJudge> All = new List<WallJudge>();

    public static void DestroyAll()
    {
        for (int i = All.Count - 1; i >= 0; i--)
            if (All[i] != null) Destroy(All[i].gameObject);
        All.Clear();
    }

    [Header("穴の読み取り")]
    public float cellRatio = 0.2f;           // 格子の大きさ(胴の長さに対する比)。小さいほど精密で重い

    [Header("ポーズの一致度(穴の充填率)")]
    public float coverInflateRatio = 0.10f;  // 体を太らせて測る量。壁の穴の余白に合わせる
    public float greatCoverage = 0.85f;
    public float goodCoverage = 0.65f;

    [Header("判定")]
    public float windowPadRatio = 0.10f;     // 体の厚み分の余裕(胴の長さに対する比)
    public bool hitIsAlwaysBad = true;       // 壁に当たったら、Badにする

    BodyColliders _body;
    int _wallLayer;
    readonly List<Vector3> _hole = new List<Vector3>();   // 穴の格子点(壁のローカル座標)
    float _centerOffsetZ, _halfZ, _covSum;
    int _covCount;
    bool _ready, _entered, _resolved, _hit;
    readonly List<string> _hitNames = new List<string>();
    readonly List<string> _tmpNames = new List<string>();
    float _startSide;
    bool _sideSet;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }
    void OnDestroy() { if (_entered && !_resolved) Resolve(); }

    void Start()
    {
        _body = BodyColliders.Instance;
        _wallLayer = LayerMask.NameToLayer("Wall");
        if (_body == null || _wallLayer < 0)
        {
            Debug.LogError("WallJudge: BodyColliders か、Layer 'Wall' が見つかりません", this);
            enabled = false;
            return;
        }
        PrepareColliders();
        if (!SampleHole()) { enabled = false; return; }
        _ready = true;
    }

    void PrepareColliders()
    {
        foreach (var t in GetComponentsInChildren<Transform>(true)) t.gameObject.layer = _wallLayer;
        foreach (var mf in GetComponentsInChildren<MeshFilter>())
            if (mf.GetComponent<Collider>() == null) mf.gameObject.AddComponent<MeshCollider>();   // 凹形状のまま
        if (GetComponent<Rigidbody>() == null)
        {
            var rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
        }
        Physics.SyncTransforms();
    }

    // 壁の範囲を格子に区切り、壁のコライダーが無いセル=穴として記録する
    bool SampleHole()
    {
        var rends = GetComponentsInChildren<Renderer>();
        if (rends.Length == 0) { Debug.LogError("WallJudge: Renderer がありません", this); return false; }
        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);

        _centerOffsetZ = b.center.z - transform.position.z;
        _halfZ = b.extents.z;

        float cell = Mathf.Max(0.01f, cellRatio * _body.TorsoLength);
        int nx = Mathf.Max(1, Mathf.FloorToInt(b.size.x / cell));
        int ny = Mathf.Max(1, Mathf.FloorToInt(b.size.y / cell));
        float sx = b.size.x / nx, sy = b.size.y / ny;
        Vector3 half = new Vector3(sx * 0.5f, sy * 0.5f, Mathf.Max(b.extents.z, 0.01f));
        int mask = 1 << _wallLayer;

        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                Vector3 c = new Vector3(b.min.x + (i + 0.5f) * sx, b.min.y + (j + 0.5f) * sy, b.center.z);
                bool solid = false;
                foreach (var col in Physics.OverlapBox(c, half, Quaternion.identity, mask, QueryTriggerInteraction.Ignore))
                    if (col.transform.IsChildOf(transform)) { solid = true; break; }
                if (!solid) _hole.Add(transform.InverseTransformPoint(c));
            }
        }

        if (_hole.Count == 0)
            Debug.LogWarning("WallJudge: 穴が見つかりません。壁にコライダーが付いているか、穴が貫通しているか確認してください", this);
        return true;
    }

    void FixedUpdate()
    {
        if (!_ready || _resolved) return;

        float plane = _body.PlaneZ;
        float zc = transform.position.z + _centerOffsetZ;
        float pad = windowPadRatio * _body.TorsoLength;
        bool inWindow = Mathf.Abs(zc - plane) <= _halfZ + pad;

        // 速すぎて判定の窓を1度も踏まずに通り抜けた場合も、結果を出す(充填率0)
        float side = Mathf.Sign(zc - plane);
        if (!_sideSet) { _startSide = side; _sideSet = true; }
        else if (!_entered && !inWindow && side != _startSide) _entered = true;

        if (inWindow)
        {
            _entered = true;
            _tmpNames.Clear();
            if (_body.CountHits(1 << _wallLayer, _tmpNames) > 0)
            {
                _hit = true;
                foreach (var n in _tmpNames) if (!_hitNames.Contains(n)) _hitNames.Add(n);
            }
            _covSum += Coverage(plane);
            _covCount++;
        }
        else if (_entered)
        {
            Resolve();
        }
    }

    // 穴の格子点のうち、体(を少し太らせたもの)が覆っている割合
    float Coverage(float planeZ)
    {
        if (_hole.Count == 0) return 0f;
        float inflate = coverInflateRatio * _body.TorsoLength;
        int covered = 0;
        for (int i = 0; i < _hole.Count; i++)
        {
            Vector3 w = transform.TransformPoint(_hole[i]);
            w.z = planeZ;
            if (_body.DistanceTo(w) <= inflate) covered++;
        }
        return (float)covered / _hole.Count;
    }

    void Resolve()
    {
        if (_resolved) return;
        _resolved = true;

        float cov = _covCount > 0 ? _covSum / _covCount : 0f;
        Rating rating = cov >= greatCoverage ? Rating.Great
                      : cov >= goodCoverage ? Rating.Good : Rating.Bad;
        if (_hit && hitIsAlwaysBad) rating = Rating.Bad;

        Debug.Log($"[Wall] {name}: hit={_hit} 充填率={cov * 100f:F0}% → {rating}");
        OnWallResolved?.Invoke(new Result { wallName = name, hit = _hit, coverage = cov, rating = rating, parts = string.Join("、", _hitNames) });
    }
}