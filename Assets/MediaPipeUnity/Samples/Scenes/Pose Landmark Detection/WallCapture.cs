using System.Collections.Generic;
using UnityEngine;

// 方式B（キャプチャ方式）の壁作り道具。
//
// 使い方の流れ：
//   1. このコンポーネントをシーンの適当なオブジェクト（例：BodySegments2D と同じ物）に付ける
//   2. 再生して、カメラの前で壁にしたいポーズを取る
//   3. C キー（captureKey）を押す → 判定に使われている骨格カプセルから壁メッシュが作られ、
//      判定平面の位置にプレビューが出る
//   4. Inspector の右クリックメニュー「壁を prefab + WallData として保存」でアセット化する
//   5. できた WallData を WallSpawner の walls に追加する
//
// 穴の形 ＝ 各カプセル（骨の線分＋判定半径）を marginRatio×T だけ太らせた形の合体。
// 輪郭は距離場から marching squares で取り、Chaikin 平滑化で角を落として「なめらか」に見せる。
// 平滑化では、マージンを食いすぎる点は元に戻すガードを掛けている（穴が設計より狭くならない）。
// ふちの丸み（bevel）は奥行きの真ん中で「素材側へ逃がす」向きに付ける。
// 判定は壁メッシュ全体を平面に投影して行われるため、真ん中が広い分には穴は狭くならず安全。
public class WallCapture : MonoBehaviour
{
    [Header("参照")]
    public BodySegments2D body;              // 空なら実行時に BodySegments2D.Instance を探す
    public Material wallMaterial;            // プレビューと保存 prefab に付ける素材（空でも動く）

    [Header("記録")]
    public KeyCode captureKey = KeyCode.C;
    public string wallName = "Wall_01";

    [Header("穴の大きさ（T = 胴の長さ）")]
    public float marginRatio = 0.25f;        // 穴の余裕。判定側の太り（0.12T）と接触のしきい値（0.10T）の合計より大きく要る。0.25 が検証済みの推奨値（下限は約0.23）
    public float framePaddingRatio = 0.55f;  // 穴の外側に残す壁の幅
    public float thickness = 2.5f;           // 壁の厚み（ワールド単位）

    [Header("見た目（なめらかさ）")]
    public int smoothPasses = 2;             // 輪郭を滑らかにする回数。0 で無効
    public float bevelRatio = 0.045f;        // ふちの丸み（×T）。0 で無効
    public float gridCellRatio = 0.045f;     // 輪郭を取る細かさ（×T）。小さいほど精密だが重くなる

    GameObject _preview;
    Mesh _mesh;
    float _capturedT;

    void Update()
    {
        if (captureKey != KeyCode.None && Input.GetKeyDown(captureKey)) Capture();
    }

    [ContextMenu("いまのポーズで壁を作る")]
    public void Capture()
    {
        if (body == null) body = BodySegments2D.Instance;
        if (body == null) { Debug.LogError("WallCapture: BodySegments2D が見つかりません", this); return; }
        body.Refresh();
        float T = body.TorsoLength;
        if (T <= 1e-4f || body.Segments.Count == 0)
        {
            Debug.LogError("WallCapture: 体の線分がまだできていません（カメラに全身が映ってから押してください）", this);
            return;
        }

        // 原点 ＝ アバターの足元（x,y）。WallSpawner が spawnPoint に置く位置と対応させる
        Vector3 root = body.animator != null ? body.animator.transform.position : Vector3.zero;
        Vector2 origin = new Vector2(root.x, root.y);
        var local = new List<BodySegments2D.Segment>();
        foreach (var s in body.Segments)
            local.Add(new BodySegments2D.Segment { label = s.label, a = s.a - origin, b = s.b - origin, radius = s.radius });

        string report;
        _mesh = BuildWallMesh(local, T, marginRatio * T, framePaddingRatio * T,
                              thickness, bevelRatio * T, gridCellRatio * T, smoothPasses, out report);
        if (_mesh == null) { Debug.LogError("WallCapture: メッシュ生成に失敗しました。" + report, this); return; }
        _capturedT = T;

        if (_preview == null)
        {
            _preview = new GameObject("CapturedWallPreview");
            _preview.AddComponent<MeshFilter>();
            _preview.AddComponent<MeshRenderer>();
        }
        _preview.name = "CapturedWallPreview_" + wallName;
        _preview.GetComponent<MeshFilter>().sharedMesh = _mesh;
        var rend = _preview.GetComponent<MeshRenderer>();
        if (wallMaterial != null) rend.sharedMaterial = wallMaterial;
        _preview.transform.position = new Vector3(origin.x, origin.y, body.PlaneZ);
        _preview.transform.rotation = Quaternion.identity;
        _preview.transform.localScale = Vector3.one;

        Debug.Log("[WallCapture] 記録しました: " + wallName + " " + report +
                  " プレビューは判定平面の位置に出ています。そのままのポーズで壁として通れるか確認し、" +
                  "良ければ右クリックメニューから保存してください。", this);
    }

    // 距離場：点 p から「カプセルの表面」までの距離（中なら負）
    static float Field(List<BodySegments2D.Segment> segs, Vector2 p)
    {
        float best = float.MaxValue;
        foreach (var s in segs)
        {
            float d = BodySegments2D.DistancePointSegment(p, s.a, s.b) - s.radius;
            if (d < best) best = d;
        }
        return best;
    }

    // 線分リストから壁メッシュを作る。失敗したら null（理由は report に入る）
    public static Mesh BuildWallMesh(List<BodySegments2D.Segment> segs, float T,
        float margin, float framePad, float thickness, float bevel, float cell, int smoothPasses,
        out string report)
    {
        report = "";
        if (segs == null || segs.Count == 0) { report = "線分がありません"; return null; }
        if (cell <= 1e-5f) cell = 0.045f * T;

        // 1) 穴の範囲（線分の外接矩形＋半径＋マージン）
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        float maxR = 0f;
        foreach (var s in segs)
        {
            minX = Mathf.Min(minX, s.a.x, s.b.x); maxX = Mathf.Max(maxX, s.a.x, s.b.x);
            minY = Mathf.Min(minY, s.a.y, s.b.y); maxY = Mathf.Max(maxY, s.a.y, s.b.y);
            maxR = Mathf.Max(maxR, s.radius);
        }
        float pad = maxR + margin + cell * 2f;
        minX -= pad; minY -= pad; maxX += pad; maxY += pad;

        // 2) 格子に距離場をサンプル（f < margin の内側が穴）
        int nx = Mathf.Clamp(Mathf.CeilToInt((maxX - minX) / cell), 4, 400);
        int ny = Mathf.Clamp(Mathf.CeilToInt((maxY - minY) / cell), 4, 400);
        float cw = (maxX - minX) / nx, ch = (maxY - minY) / ny;
        var f = new float[nx + 1, ny + 1];
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
                f[i, j] = Field(segs, new Vector2(minX + i * cw, minY + j * ch)) - margin;

        // 3) marching squares で輪郭（ループ）を取り出す
        var loops = ExtractLoops(f, nx, ny, minX, minY, cw, ch);
        if (loops.Count == 0) { report = "輪郭が取れませんでした"; return null; }

        // ループの分類：符号付き面積が正（反時計回り）＝穴の外周、負＝穴の中の島（実体）
        var holes = new List<List<Vector2>>();
        var islands = new List<List<Vector2>>();
        foreach (var lp in loops)
        {
            if (lp.Count < 3) continue;
            float area = SignedArea(lp);
            if (Mathf.Abs(area) < cell * cell) continue;
            if (area > 0f) holes.Add(lp); else islands.Add(lp);
        }
        if (holes.Count == 0) { report = "穴の外周が見つかりませんでした"; return null; }

        // 4) 平滑化（Chaikin）。ガード：|f| が 0.3×margin を超える点は元の辺の中点に戻す
        //    その後に Douglas-Peucker で点数を減らす（形の誤差は cell×0.3 以内）。
        //    点が多すぎると耳切りが詰まるため、この削減は見た目でなく正しさのために要る
        for (int k = 0; k < holes.Count; k++) holes[k] = DecimateLoop(SmoothLoop(holes[k], segs, margin, smoothPasses), cell * 0.3f);
        for (int k = 0; k < islands.Count; k++) islands[k] = DecimateLoop(SmoothLoop(islands[k], segs, margin, smoothPasses), cell * 0.3f);

        // 5) 外枠（矩形）
        var rect = new List<Vector2>
        {
            new Vector2(minX - framePad, minY - framePad),
            new Vector2(maxX + framePad, minY - framePad),
            new Vector2(maxX + framePad, maxY + framePad),
            new Vector2(minX - framePad, maxY + framePad),
        };

        // 6) 表面・裏面の三角形化（外枠に穴を橋渡しして耳切り）
        var faceIdx = TriangulateFace(rect, holes, out var facePts, out string triReport);
        if (faceIdx == null) { report = "三角形化に失敗しました。" + triReport; return null; }

        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        float hz = thickness * 0.5f;

        // 表面（z = +hz）
        int baseF = verts.Count;
        foreach (var p in facePts) { verts.Add(new Vector3(p.x, p.y, hz)); uvs.Add(p); }
        foreach (var t in faceIdx) tris.Add(baseF + t);
        // 裏面（z = -hz、巻き方向を反転）
        int baseB = verts.Count;
        foreach (var p in facePts) { verts.Add(new Vector3(p.x, p.y, -hz)); uvs.Add(p); }
        for (int i = 0; i < faceIdx.Count; i += 3)
        {
            tris.Add(baseB + faceIdx[i]); tris.Add(baseB + faceIdx[i + 2]); tris.Add(baseB + faceIdx[i + 1]);
        }

        // 7) ふち（外枠・穴・島）：前面 →（ベベル中間）→ 後面 の帯
        AddRim(rect, true, bevel, hz, verts, uvs, tris);
        foreach (var h in holes) AddRim(h, false, bevel, hz, verts, uvs, tris);
        foreach (var isl in islands)
        {
            // 島は実体の塊なので、面として塗る（表裏）＋ふち
            var islTris = EarClip(isl);
            if (islTris != null)
            {
                int b0 = verts.Count;
                foreach (var p in isl) { verts.Add(new Vector3(p.x, p.y, hz)); uvs.Add(p); }
                foreach (var t in islTris) tris.Add(b0 + t);
                int b1 = verts.Count;
                foreach (var p in isl) { verts.Add(new Vector3(p.x, p.y, -hz)); uvs.Add(p); }
                for (int i = 0; i < islTris.Count; i += 3)
                {
                    tris.Add(b1 + islTris[i]); tris.Add(b1 + islTris[i + 2]); tris.Add(b1 + islTris[i + 1]);
                }
            }
            AddRim(isl, true, bevel, hz, verts, uvs, tris);
        }

        var mesh = new Mesh { name = "CapturedWall" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        report = string.Format("T={0:F1} マージン={1:F1} 穴ループ={2} 島={3} 頂点={4} 三角形={5}",
            T, margin, holes.Count, islands.Count, verts.Count, tris.Count / 3);
        return mesh;
    }

    // ---- 輪郭の取り出し（marching squares） ----

    static float SignedArea(List<Vector2> lp)
    {
        float a = 0f;
        for (int i = 0; i < lp.Count; i++)
        {
            var p = lp[i]; var q = lp[(i + 1) % lp.Count];
            a += p.x * q.y - q.x * p.y;
        }
        return a * 0.5f;
    }

    static List<List<Vector2>> ExtractLoops(float[,] f, int nx, int ny, float minX, float minY, float cw, float ch)
    {
        // 辺上の交点は隣のセルと共有されるので、辺ごとに1回だけ計算して ID で参照する
        // 横辺 h(i,j): 格子点 (i,j)-(i+1,j) の間（i<nx, j<=ny）／ 縦辺 v(i,j): (i,j)-(i,j+1) の間（i<=nx, j<ny）
        int hCount = nx * (ny + 1);
        var hPt = new Vector2[hCount];
        var vPt = new Vector2[(nx + 1) * ny];
        Vector2 H(int i, int j)
        {
            float f0 = f[i, j], f1 = f[i + 1, j];
            float t = f0 / (f0 - f1);
            return new Vector2(minX + (i + t) * cw, minY + j * ch);
        }
        Vector2 V(int i, int j)
        {
            float f0 = f[i, j], f1 = f[i, j + 1];
            float t = f0 / (f0 - f1);
            return new Vector2(minX + i * cw, minY + (j + t) * ch);
        }
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i < nx; i++)
                if ((f[i, j] < 0f) != (f[i + 1, j] < 0f)) hPt[j * nx + i] = H(i, j);
        for (int j = 0; j < ny; j++)
            for (int i = 0; i <= nx; i++)
                if ((f[i, j] < 0f) != (f[i, j + 1] < 0f)) vPt[j * (nx + 1) + i] = V(i, j);
        int Hid(int i, int j) => j * nx + i;
        int Vid(int i, int j) => hCount + j * (nx + 1) + i;
        Vector2 Pt(int id) => id < hCount ? hPt[id] : vPt[id - hCount];

        // セルごとに、内側が左に来る向きで線分を出す（外周は反時計回りになる）
        var segs = new List<(int from, int to)>();
        void Seg(int a, int b) => segs.Add((a, b));
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int c = 0;
                if (f[i, j] < 0f) c |= 1;         // 左下
                if (f[i + 1, j] < 0f) c |= 2;     // 右下
                if (f[i + 1, j + 1] < 0f) c |= 4; // 右上
                if (f[i, j + 1] < 0f) c |= 8;     // 左上
                if (c == 0 || c == 15) continue;
                int B = Hid(i, j), R = Vid(i + 1, j), T = Hid(i, j + 1), L = Vid(i, j);
                switch (c)
                {
                    case 1: Seg(B, L); break;
                    case 2: Seg(R, B); break;
                    case 3: Seg(R, L); break;
                    case 4: Seg(T, R); break;
                    case 5: Seg(B, L); Seg(T, R); break;
                    case 6: Seg(T, B); break;
                    case 7: Seg(T, L); break;
                    case 8: Seg(L, T); break;
                    case 9: Seg(B, T); break;
                    case 10: Seg(R, B); Seg(L, T); break;
                    case 11: Seg(R, T); break;
                    case 12: Seg(L, R); break;
                    case 13: Seg(B, R); break;
                    case 14: Seg(L, B); break;
                }
            }
        }

        // 線分をつないでループにする
        var outgoing = new Dictionary<int, List<int>>();
        for (int s = 0; s < segs.Count; s++)
        {
            if (!outgoing.TryGetValue(segs[s].from, out var list)) { list = new List<int>(); outgoing[segs[s].from] = list; }
            list.Add(s);
        }
        var used = new bool[segs.Count];
        var loops = new List<List<Vector2>>();
        for (int s0 = 0; s0 < segs.Count; s0++)
        {
            if (used[s0]) continue;
            var loop = new List<Vector2>();
            int cur = s0;
            bool closed = false;
            while (true)
            {
                used[cur] = true;
                var sg = segs[cur];
                loop.Add(Pt(sg.from));
                if (sg.to == segs[s0].from) { closed = true; break; }
                if (!outgoing.TryGetValue(sg.to, out var cand)) break;
                int next = -1;
                foreach (var ci in cand) if (!used[ci]) { next = ci; break; }
                if (next < 0) break;
                cur = next;
                if (loop.Count > segs.Count + 1) break;
            }
            if (closed && loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    // ---- 平滑化（Chaikin＋マージンのガード） ----

    static List<Vector2> SmoothLoop(List<Vector2> loop, List<BodySegments2D.Segment> segs, float margin, int passes)
    {
        var cur = loop;
        float guard = 0.3f * margin;
        for (int p = 0; p < passes; p++)
        {
            var next = new List<Vector2>(cur.Count * 2);
            for (int i = 0; i < cur.Count; i++)
            {
                var a = cur[i];
                var b = cur[(i + 1) % cur.Count];
                var mid = (a + b) * 0.5f;
                var q = a * 0.75f + b * 0.25f;
                var r = a * 0.25f + b * 0.75f;
                if (Mathf.Abs(Field(segs, q) - margin) > guard) q = mid;
                if (Mathf.Abs(Field(segs, r) - margin) > guard) r = mid;
                next.Add(q);
                next.Add(r);
            }
            cur = next;
        }
        return cur;
    }

    // ---- 輪郭の点数削減（Douglas-Peucker、閉ループ用） ----
    // 取り除く点は、単純化後の輪郭から tol 以内に収まることが保証される（形とマージンが保たれる）

    static List<Vector2> DecimateLoop(List<Vector2> loop, float tol)
    {
        int n = loop.Count;
        if (n <= 8) return loop;
        int a1 = 0;
        float best = -1f;
        for (int i = 0; i < n; i++)
        {
            float d = (loop[i] - loop[0]).sqrMagnitude;
            if (d > best) { best = d; a1 = i; }
        }
        var result = new List<Vector2>();
        DecimateChain(loop, 0, a1, tol, result);
        var second = new List<Vector2>();
        DecimateChain(loop, a1, 0, tol, second);
        for (int i = 1; i < second.Count - 1; i++) result.Add(second[i]);
        return result.Count >= 8 ? result : loop;
    }

    static void DecimateChain(List<Vector2> loop, int i0, int i1, float tol, List<Vector2> outPts)
    {
        int n = loop.Count;
        var idx = new List<int>();
        int i = i0;
        while (true)
        {
            idx.Add(i);
            if (i == i1) break;
            i = (i + 1) % n;
        }
        var keep = new bool[idx.Count];
        keep[0] = true;
        keep[idx.Count - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, idx.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            if (e <= s + 1) continue;
            var a = loop[idx[s]];
            var b = loop[idx[e]];
            int far = -1;
            float fd = -1f;
            for (int k = s + 1; k < e; k++)
            {
                float d = BodySegments2D.DistancePointSegment(loop[idx[k]], a, b);
                if (d > fd) { fd = d; far = k; }
            }
            if (far >= 0 && fd > tol)
            {
                keep[far] = true;
                stack.Push((s, far));
                stack.Push((far, e));
            }
        }
        for (int k = 0; k < idx.Count; k++) if (keep[k]) outPts.Add(loop[idx[k]]);
    }

    // ---- 三角形化（橋渡し＋耳切り） ----

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        if ((p - a).sqrMagnitude < 1e-12f || (p - b).sqrMagnitude < 1e-12f || (p - c).sqrMagnitude < 1e-12f) return false;
        float d1 = Cross(b - a, p - a);
        float d2 = Cross(c - b, p - b);
        float d3 = Cross(a - c, p - c);
        return d1 > 1e-9f && d2 > 1e-9f && d3 > 1e-9f;
    }

    static List<int> EarClipIndices(List<Vector2> pts, List<int> poly)
    {
        var tris = new List<int>();
        var idx = new List<int>(poly);
        float area = 0f;
        for (int i = 0; i < idx.Count; i++)
        {
            var p = pts[idx[i]]; var q = pts[idx[(i + 1) % idx.Count]];
            area += p.x * q.y - q.x * p.y;
        }
        if (area < 0f) idx.Reverse();

        int guard = 0;
        while (idx.Count > 3 && guard++ < 20000)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int i0 = idx[(i + idx.Count - 1) % idx.Count];
                int i1 = idx[i];
                int i2 = idx[(i + 1) % idx.Count];
                var a = pts[i0]; var b = pts[i1]; var c = pts[i2];
                if (Cross(b - a, c - b) <= 1e-9f) continue;
                bool empty = true;
                for (int j = 0; j < idx.Count; j++)
                {
                    int ij = idx[j];
                    if (ij == i0 || ij == i1 || ij == i2) continue;
                    if (PointInTriangle(pts[ij], a, b, c)) { empty = false; break; }
                }
                if (!empty) continue;
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break;
        }
        if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
        else if (idx.Count > 3)
        {
            // 耳が切れずに残った分は扇形で埋める（形が多少崩れても穴を塞がない側に倒す）
            for (int i = 1; i + 1 < idx.Count; i++) { tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]); }
        }
        return tris;
    }

    static List<int> EarClip(List<Vector2> polygon)
    {
        var idx = new List<int>();
        for (int i = 0; i < polygon.Count; i++) idx.Add(i);
        return EarClipIndices(polygon, idx);
    }

    static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float d1 = Cross(b - a, c - a);
        float d2 = Cross(b - a, d - a);
        float d3 = Cross(d - c, a - c);
        float d4 = Cross(d - c, b - c);
        return ((d1 > 1e-9f && d2 < -1e-9f) || (d1 < -1e-9f && d2 > 1e-9f)) &&
               ((d3 > 1e-9f && d4 < -1e-9f) || (d3 < -1e-9f && d4 > 1e-9f));
    }

    static List<int> TriangulateFace(List<Vector2> rect, List<List<Vector2>> holes, out List<Vector2> points, out string report)
    {
        report = "";
        points = new List<Vector2>(rect);
        var poly = new List<int> { 0, 1, 2, 3 };

        foreach (var hole in holes)
        {
            // 穴は合体多角形の中では外周と逆向き（時計回り）にたどる
            var hIdx = new List<int>();
            for (int i = hole.Count - 1; i >= 0; i--) { points.Add(hole[i]); hIdx.Add(points.Count - 1); }

            // 橋渡し：現在の多角形の頂点と穴の頂点を、他の辺と交差しない最短の組で結ぶ
            var pairs = new List<(float dist, int pv, int hv)>();
            foreach (int pv in poly)
                foreach (int hv in hIdx)
                    pairs.Add(((points[pv] - points[hv]).sqrMagnitude, pv, hv));
            pairs.Sort((x, y) => x.dist.CompareTo(y.dist));

            bool bridged = false;
            int tried = 0;
            foreach (var (dist, pv, hv) in pairs)
            {
                if (tried++ > 300) break;
                var a = points[pv]; var b = points[hv];
                bool cross = false;
                // 穴自身の辺（hv に接する辺は除く）と交差しないか
                for (int i = 0; i < hIdx.Count && !cross; i++)
                {
                    int e0 = hIdx[i], e1 = hIdx[(i + 1) % hIdx.Count];
                    if (e0 == hv || e1 == hv) continue;
                    if (SegmentsCross(a, b, points[e0], points[e1])) cross = true;
                }
                // 現在の多角形の辺（pv に接する辺は除く）と交差しないか
                for (int i = 0; i < poly.Count && !cross; i++)
                {
                    int e0 = poly[i], e1 = poly[(i + 1) % poly.Count];
                    if (e0 == pv || e1 == pv) continue;
                    if (SegmentsCross(a, b, points[e0], points[e1])) cross = true;
                }
                if (cross) continue;

                int pvPos = poly.IndexOf(pv);
                int hvPos = hIdx.IndexOf(hv);
                var merged = new List<int>();
                for (int i = 0; i <= pvPos; i++) merged.Add(poly[i]);
                merged.Add(hv);
                for (int k = 1; k < hIdx.Count; k++) merged.Add(hIdx[(hvPos + k) % hIdx.Count]);
                merged.Add(hv);
                merged.Add(pv);
                for (int i = pvPos + 1; i < poly.Count; i++) merged.Add(poly[i]);
                poly = merged;
                bridged = true;
                break;
            }
            if (!bridged) { report = "穴への橋渡しができませんでした"; return null; }
        }

        return EarClipIndices(points, poly);
    }

    // ---- ふち（側面）の帯 ----

    static void AddRim(List<Vector2> loop, bool materialInside, float bevel, float hz,
                       List<Vector3> verts, List<Vector2> uvs, List<int> tris)
    {
        int n = loop.Count;
        if (n < 3) return;

        // ベベル：奥行きの真ん中の輪は、素材側へ bevel だけ寄せる（重心まわりの拡大縮小で近似）
        Vector2 center = Vector2.zero;
        foreach (var p in loop) center += p;
        center /= n;
        float avgR = 0f;
        foreach (var p in loop) avgR += (p - center).magnitude;
        avgR = Mathf.Max(avgR / n, 1e-4f);
        float scale = materialInside
            ? Mathf.Max(0.5f, 1f - bevel / avgR)   // 素材が内側：輪を縮める＝素材側へ
            : 1f + bevel / avgR;                    // 穴：輪を広げる＝素材側へ

        int baseF = verts.Count;
        foreach (var p in loop) { verts.Add(new Vector3(p.x, p.y, hz)); uvs.Add(p); }
        int baseM = verts.Count;
        foreach (var p in loop)
        {
            var m = center + (p - center) * scale;
            verts.Add(new Vector3(m.x, m.y, 0f)); uvs.Add(m);
        }
        int baseB = verts.Count;
        foreach (var p in loop) { verts.Add(new Vector3(p.x, p.y, -hz)); uvs.Add(p); }

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (materialInside)
            {
                tris.Add(baseF + i); tris.Add(baseM + i); tris.Add(baseM + j);
                tris.Add(baseF + i); tris.Add(baseM + j); tris.Add(baseF + j);
                tris.Add(baseM + i); tris.Add(baseB + i); tris.Add(baseB + j);
                tris.Add(baseM + i); tris.Add(baseB + j); tris.Add(baseM + j);
            }
            else
            {
                tris.Add(baseF + i); tris.Add(baseM + j); tris.Add(baseM + i);
                tris.Add(baseF + i); tris.Add(baseF + j); tris.Add(baseM + j);
                tris.Add(baseM + i); tris.Add(baseB + j); tris.Add(baseB + i);
                tris.Add(baseM + i); tris.Add(baseM + j); tris.Add(baseB + j);
            }
        }
    }

#if UNITY_EDITOR
    [ContextMenu("壁を prefab + WallData として保存")]
    public void SaveAsAssets()
    {
        if (_mesh == null) { Debug.LogError("WallCapture: 先にポーズを記録してください（C キー）", this); return; }
        const string folder = "Assets/Walls";
        if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
            UnityEditor.AssetDatabase.CreateFolder("Assets", "Walls");

        string safe = string.IsNullOrEmpty(wallName) ? "Wall" : wallName;
        string meshPath = folder + "/" + safe + ".mesh.asset";
        var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if (existing != null) UnityEditor.EditorUtility.CopySerialized(_mesh, existing);
        else UnityEditor.AssetDatabase.CreateAsset(_mesh, meshPath);

        var go = new GameObject(safe);
        go.AddComponent<MeshFilter>().sharedMesh = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        var rend = go.AddComponent<MeshRenderer>();
        if (wallMaterial != null) rend.sharedMaterial = wallMaterial;
        string prefabPath = folder + "/" + safe + ".prefab";
        UnityEditor.PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
        DestroyImmediate(go);

        var data = ScriptableObject.CreateInstance<WallData>();
        data.displayName = safe;
        data.prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        data.speed = 10f;
        data.uniformScale = 1f;
        data.rotationOffset = Vector3.zero;
        data.positionOffset = Vector3.zero;
        data.weight = 1f;
        string dataPath = folder + "/" + safe + "_Data.asset";
        var oldData = UnityEditor.AssetDatabase.LoadAssetAtPath<WallData>(dataPath);
        if (oldData != null) UnityEditor.AssetDatabase.DeleteAsset(dataPath);
        UnityEditor.AssetDatabase.CreateAsset(data, dataPath);
        UnityEditor.AssetDatabase.SaveAssets();

        Debug.Log("[WallCapture] 保存しました: " + meshPath + " / " + prefabPath + " / " + dataPath +
                  " ― WallData を WallSpawner の walls に追加すればゲームに出ます。", this);
    }
#endif
}
