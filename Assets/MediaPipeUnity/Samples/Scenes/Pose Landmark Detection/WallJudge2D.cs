using System.Collections.Generic;
using UnityEngine;

// 壁の判定（旧 WallJudge の置き換え）。Physics / MeshCollider / FixedUpdate は使わない。
//  - スポーン時に1回だけ、壁メッシュを「ワールド座標の X,Y 平面」の2D格子に焼く。
//    壁はスポーン時の回転でプレイヤーに正対し、その後は world Z へまっすぐ進むだけなので、
//    ワールドの X,Y への投影がそのまま壁の正面図になる。
//    （旧方式は壁ローカル空間で焼いていて、FBXのノード構成によって格子が潰れる問題があった）
//    格子の端からたどれる「外側の空気」は穴と数えない（外形が四角くない壁でも充填率の上限が下がらない）。
//  - 壁がプレイヤー平面を跨いだ瞬間に1回だけ、その時点のポーズで採点する。
//  - 評価は Bad / Good / Perfect。壁の実体セルに体が触れていたら接触（部位名つき）で Bad。
public class WallJudge2D : MonoBehaviour
{
    public enum Rating { Bad, Good, Perfect }

    public struct Result
    {
        public string wallName;
        public bool hit;                  // 壁に接触したか
        public float coverage;            // 0〜1：穴を体でどれだけ埋めたか
        public Rating rating;
        public string parts;              // 接触した部位（表示用、例：左前腕、頭）
        public List<string> hitLabels;    // 接触した部位（赤色表示用）
    }

    public static event System.Action<Result> OnWallResolved;
    static readonly List<WallJudge2D> All = new List<WallJudge2D>();

    // silent=true のときは採点せずに消す（ゲーム終了・開始時の後片付け用）
    public static void DestroyAll(bool silent = false)
    {
        for (int i = All.Count - 1; i >= 0; i--)
        {
            if (All[i] == null) continue;
            if (silent) All[i]._resolved = true;
            Destroy(All[i].gameObject);
        }
        All.Clear();
    }

    [Header("穴の読み取り")]
    public float cellRatio = 0.1f;            // 格子の大きさ（胴の長さに対する比）。v1.6 で 0.2→0.1 に細分化：焼き込みの太り（0.6セル）と接触マージン（0.5セル）が半分になり、キャプチャ側のマージンを 0.15 まで下げられる
    public float solidDilateRatio = 0.6f;     // 実体を太らせる量（格子1辺に対する比）。薄い縁の隙間から「外側の空気」が穴へ漏れるのを防ぐ

    [Header("ポーズの一致度（穴の充填率）")]
    public float coverInflateRatio = 0.10f;   // 充填率を測るとき体を太らせる量（胴比）。旧と同じ初期値
    public float perfectCoverage = 0.85f;
    public float goodCoverage = 0.65f;

    [Header("接触の判定")]
    public float contactMarginRatio = 0.5f;   // 実体セル中心から、格子1辺のこの割合までは接触とみなす

    BodySegments2D _body;
    bool _baked, _resolved, _sideSet;
    float _prevSideSign, _centerOffsetZ;
    Vector3 _spawnPos;                        // 焼いたときの壁の位置（セルはこの時点のワールド座標で持つ）

    // 焼いた格子（スポーン時点のワールド X,Y）
    float _minX, _minY, _cell;
    int _nx, _ny;
    readonly List<Vector2> _holeCells = new List<Vector2>();   // 穴のセル中心（ワールド x,y）
    readonly List<Vector2> _solidCells = new List<Vector2>();  // 壁の実体のセル中心（ワールド x,y）

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }
    void OnDestroy() { if (!_resolved) Resolve(); }   // 万一消えた場合の保険（通常は通過時に採点済み）

    void Update()
    {
        if (_resolved) return;
        if (_body == null) _body = BodySegments2D.Instance;
        if (_body == null) return;
        if (!_baked)
        {
            if (_body.TorsoLength <= 0f) return;   // 体の寸法が決まるまで待つ
            Bake();
            if (!_baked) { enabled = false; return; }
        }

        // 壁の中心がプレイヤー平面を跨いだ瞬間に1回だけ採点する（窓は無いので速くてもすり抜けない）
        float side = Mathf.Sign(transform.position.z + _centerOffsetZ - _body.PlaneZ);
        if (!_sideSet) { _prevSideSign = side; _sideSet = true; return; }
        if (side != _prevSideSign && _prevSideSign != 0f) Resolve();
        _prevSideSign = side;
    }

    // 壁メッシュをワールド X,Y 平面の2D格子に焼く（1回だけ）
    void Bake()
    {
        var filters = GetComponentsInChildren<MeshFilter>();
        if (filters.Length == 0) { Debug.LogError("WallJudge2D: MeshFilter がありません", this); return; }

        // 全頂点をワールドへ写し、範囲を決める
        var worldVerts = new List<Vector3[]>();
        var worldTris = new List<int[]>();
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        string unreadable = null;
        foreach (var mf in filters)
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Vector3[] verts = mesh.vertices;
            if (verts.Length == 0 && mesh.vertexCount > 0)
            {
                // 頂点があるはずなのに読めない＝FBXの Read/Write が無効
                unreadable = mesh.name;
                continue;
            }
            Matrix4x4 m = mf.transform.localToWorldMatrix;
            var wv = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                wv[i] = m.MultiplyPoint3x4(verts[i]);
                min = Vector3.Min(min, wv[i]);
                max = Vector3.Max(max, wv[i]);
            }
            // 三角形はサブメッシュごとに取る（mesh.triangles 決め打ちより確実）
            var trisAll = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
                trisAll.AddRange(mesh.GetTriangles(s));
            worldVerts.Add(wv);
            worldTris.Add(trisAll.ToArray());
        }
        if (unreadable != null)
        {
            Debug.LogError($"WallJudge2D: メッシュ「{unreadable}」を読み取れません。FBX を選択し、Inspector の Model タブで Read/Write を有効にして Apply してください", this);
            return;
        }
        if (worldVerts.Count == 0) { Debug.LogError("WallJudge2D: メッシュがありません", this); return; }

        _spawnPos = transform.position;
        // 厚み中心の world z は、壁が world Z へまっすぐ進む限り一定のずれで表せる
        _centerOffsetZ = (min.z + max.z) * 0.5f - _spawnPos.z;
        _minX = min.x; _minY = min.y;

        // 三角形をワールド X,Y へ投影する（壁の正面図）
        var tris = new List<(Vector2 a, Vector2 b, Vector2 c)>();
        for (int f = 0; f < worldVerts.Count; f++)
        {
            Vector3[] wv = worldVerts[f];
            int[] idx = worldTris[f];
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                Vector3 A = wv[idx[t]], B = wv[idx[t + 1]], C = wv[idx[t + 2]];
                tris.Add((new Vector2(A.x, A.y), new Vector2(B.x, B.y), new Vector2(C.x, C.y)));
            }
        }
        if (tris.Count == 0) { Debug.LogError($"WallJudge2D: 三角形がありません（メッシュ数={worldVerts.Count}）。FBX の中身を確認してください", this); return; }

        _cell = Mathf.Max(0.01f, cellRatio * _body.TorsoLength);
        _nx = Mathf.Clamp(Mathf.CeilToInt((max.x - min.x) / _cell), 1, 512);
        _ny = Mathf.Clamp(Mathf.CeilToInt((max.y - min.y) / _cell), 1, 512);

        var solid = new bool[_nx * _ny];
        foreach (var tri in tris) Rasterize(tri.a, tri.b, tri.c, solid);

        // 外側の空気を洪水塗りで除外する（端からたどれる非実体セルは「穴」ではない）
        var exterior = new bool[_nx * _ny];
        var queue = new Queue<int>();
        System.Action<int> seed = i =>
        {
            if (!solid[i] && !exterior[i]) { exterior[i] = true; queue.Enqueue(i); }
        };
        for (int i = 0; i < _nx; i++) { seed(i); seed((_ny - 1) * _nx + i); }
        for (int j = 0; j < _ny; j++) { seed(j * _nx); seed(j * _nx + _nx - 1); }
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int x = cur % _nx, y = cur / _nx;
            if (x > 0) seed(cur - 1);
            if (x < _nx - 1) seed(cur + 1);
            if (y > 0) seed(cur - _nx);
            if (y < _ny - 1) seed(cur + _nx);
        }

        for (int j = 0; j < _ny; j++)
        for (int i = 0; i < _nx; i++)
        {
            int id = j * _nx + i;
            Vector2 c = new Vector2(_minX + (i + 0.5f) * _cell, _minY + (j + 0.5f) * _cell);
            if (solid[id]) _solidCells.Add(c);
            else if (!exterior[id]) _holeCells.Add(c);
        }

        Debug.Log($"[WallJudge2D] {name}: 格子={_nx}x{_ny} 穴={_holeCells.Count} 実体={_solidCells.Count}", this);
        if (_holeCells.Count == 0)
            Debug.LogWarning("WallJudge2D: 穴が見つかりません。壁に穴が貫通しているか確認してください", this);
        _baked = true;
    }

    void Rasterize(Vector2 a, Vector2 b, Vector2 c, bool[] solid)
    {
        // 保守的に塗る：セル中心が三角形の近く（solidDilateRatio × セル）でも実体にする。
        // 中心が入ったときだけ塗る方式だと、薄い縁で1セル分の隙間ができ、
        // 洪水塗りが穴へ漏れて「穴=0」になってしまうため。
        float dilate = _cell * solidDilateRatio;
        float minx = Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - dilate;
        float maxx = Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + dilate;
        float miny = Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - dilate;
        float maxy = Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + dilate;
        int i0 = Mathf.Clamp(Mathf.FloorToInt((minx - _minX) / _cell), 0, _nx - 1);
        int i1 = Mathf.Clamp(Mathf.FloorToInt((maxx - _minX) / _cell), 0, _nx - 1);
        int j0 = Mathf.Clamp(Mathf.FloorToInt((miny - _minY) / _cell), 0, _ny - 1);
        int j1 = Mathf.Clamp(Mathf.FloorToInt((maxy - _minY) / _cell), 0, _ny - 1);
        for (int j = j0; j <= j1; j++)
        for (int i = i0; i <= i1; i++)
        {
            Vector2 p = new Vector2(_minX + (i + 0.5f) * _cell, _minY + (j + 0.5f) * _cell);
            if (DistanceToTriangle(p, a, b, c) <= dilate) solid[j * _nx + i] = true;
        }
    }

    static float DistanceToTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        if (InTriangle(p, a, b, c)) return 0f;
        float d = BodySegments2D.DistancePointSegment(p, a, b);
        d = Mathf.Min(d, BodySegments2D.DistancePointSegment(p, b, c));
        d = Mathf.Min(d, BodySegments2D.DistancePointSegment(p, c, a));
        return d;
    }

    static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

    static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
        bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
        bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
        return !(neg && pos);
    }

    Vector2 CellWorldXY(Vector2 cell)
    {
        // 壁は Z 方向にしか動かないので、セルの x,y はスポーン時から変わらない。
        // 念のため x,y の移動量だけ足しておく（通常は 0）。
        Vector3 d = transform.position - _spawnPos;
        return new Vector2(cell.x + d.x, cell.y + d.y);
    }

    void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        if (_body == null) _body = BodySegments2D.Instance;

        float cov = 0f;
        bool hit = false;
        var hitLabels = new List<string>();
        if (_body != null && _baked)
        {
            _body.Refresh();
            float inflate = coverInflateRatio * _body.TorsoLength;
            float contactMargin = contactMarginRatio * _cell;

            int covered = 0;
            foreach (var cell in _holeCells)
                if (_body.DistanceTo(CellWorldXY(cell)) <= inflate) covered++;
            if (_holeCells.Count > 0) cov = (float)covered / _holeCells.Count;

            foreach (var cell in _solidCells)
            {
                string part = _body.ClosestPart(CellWorldXY(cell), out float d);
                if (part != null && d <= contactMargin)
                {
                    hit = true;
                    if (!hitLabels.Contains(part)) hitLabels.Add(part);
                }
            }
        }

        Rating rating = hit ? Rating.Bad
                      : cov >= perfectCoverage ? Rating.Perfect
                      : cov >= goodCoverage ? Rating.Good : Rating.Bad;

        Debug.Log($"[Wall] {name}: 接触={hit}（{string.Join("、", hitLabels)}） 充填率={cov * 100f:F0}% → {rating}");
        OnWallResolved?.Invoke(new Result
        {
            wallName = name,
            hit = hit,
            coverage = cov,
            rating = rating,
            parts = string.Join("、", hitLabels),
            hitLabels = hitLabels,
        });
    }
}
