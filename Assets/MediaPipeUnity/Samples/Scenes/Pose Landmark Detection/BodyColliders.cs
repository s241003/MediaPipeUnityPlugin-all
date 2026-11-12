using System.Collections.Generic;
using UnityEngine;

// 見た目モデルの骨に、当たり判定を直接付ける(別の判定モデルは不要)
// fitToMesh がオンのとき、スキンメッシュの頂点から各部位のカプセル/箱を割り出し、Y Botの形に合わせる
public class BodyColliders : MonoBehaviour
{
    public static BodyColliders Instance { get; private set; }

    public Animator animator;

    [Header("メッシュへのフィット")]
    public bool fitToMesh = true;               // オン:Y Botの頂点から判定を作る(要 FBXの Read/Write Enabled)
    [Range(0.5f, 1f)]
    public float fitPercentile = 0.9f;          // 大きいほど外側の頂点まで含めて太くなる
    public float radiusScale = 1f;              // 全体の倍率(難易度調整)。変更後はPlayし直す

    [Header("【フィット失敗時のみ】太さ(胴の長さに対する比)")]
    public float upperArmRadius = 0.10f;
    public float foreArmRadius = 0.08f;
    public float thighRadius = 0.16f;
    public float shinRadius = 0.12f;
    public float footRadius = 0.07f;
    public float handExtend = 0.25f;

    [Header("【フィット失敗時のみ】頭(胴の長さに対する比)")]
    public float headWidth = 0.30f;
    public float headHeight = 0.45f;
    public float headCenterOffset = 0.20f;

    [Header("【フィット失敗時のみ】胴(胴の長さに対する比)")]
    public float torsoWidthScale = 1.1f;
    public float torsoWidthMin = 0.40f;
    public float torsoHeightScale = 1.10f;
    public float torsoDepth = 0.40f;

    [Header("表示")]
    public bool drawGizmos = true;              // 判定を表示(当たると赤)。Gameビューでは右上の Gizmos をオン

    public float TorsoLength { get; private set; }
    public float PlaneZ => _hips != null ? _hips.position.z : 0f;

    enum PID { Torso, Head, LUpArm, LLoArm, LHand, RUpArm, RLoArm, RHand, LUpLeg, LLoLeg, LFoot, RUpLeg, RLoLeg, RFoot }

    // 頂点を、どの部位に数えるか(指は手へ、首・鎖骨は胴へ、つま先は足へ、と親をたどって決まる)
    static readonly (HumanBodyBones bone, PID id)[] PartMap = {
        (HumanBodyBones.Hips, PID.Torso), (HumanBodyBones.Spine, PID.Torso), (HumanBodyBones.Chest, PID.Torso),
        (HumanBodyBones.UpperChest, PID.Torso), (HumanBodyBones.Neck, PID.Torso),
        (HumanBodyBones.LeftShoulder, PID.Torso), (HumanBodyBones.RightShoulder, PID.Torso),
        (HumanBodyBones.Head, PID.Head),
        (HumanBodyBones.LeftUpperArm, PID.LUpArm), (HumanBodyBones.LeftLowerArm, PID.LLoArm), (HumanBodyBones.LeftHand, PID.LHand),
        (HumanBodyBones.RightUpperArm, PID.RUpArm), (HumanBodyBones.RightLowerArm, PID.RLoArm), (HumanBodyBones.RightHand, PID.RHand),
        (HumanBodyBones.LeftUpperLeg, PID.LUpLeg), (HumanBodyBones.LeftLowerLeg, PID.LLoLeg),
        (HumanBodyBones.LeftFoot, PID.LFoot), (HumanBodyBones.LeftToes, PID.LFoot),
        (HumanBodyBones.RightUpperLeg, PID.RUpLeg), (HumanBodyBones.RightLowerLeg, PID.RLoLeg),
        (HumanBodyBones.RightFoot, PID.RFoot), (HumanBodyBones.RightToes, PID.RFoot),
    };

    class Part
    {
        public Collider col;
        public bool hit, isBox;
        public string label;
        public Vector3 p0, p1, c, half;
        public float r;
        public Quaternion rot, invRot;
    }

    readonly List<Part> _parts = new List<Part>();
    Dictionary<PID, List<Vector3>> _verts;
    Transform _hips;
    int _layer;

    void Awake() { Instance = this; }

    void Start()
    {
        if (animator == null) { Debug.LogError("BodyColliders: Animator が未設定です", this); enabled = false; return; }
        _layer = Mathf.Max(0, LayerMask.NameToLayer("Player"));
        Build();
    }

    Transform Bone(HumanBodyBones id) { return animator.GetBoneTransform(id); }

    void Build()
    {
        _hips = Bone(HumanBodyBones.Hips);

        Vector3 lSh = Bone(HumanBodyBones.LeftUpperArm).position, rSh = Bone(HumanBodyBones.RightUpperArm).position;
        Vector3 lHp = Bone(HumanBodyBones.LeftUpperLeg).position, rHp = Bone(HumanBodyBones.RightUpperLeg).position;
        Vector3 sMid = (lSh + rSh) * 0.5f, hMid = (lHp + rHp) * 0.5f;
        float T = Vector3.Distance(sMid, hMid);
        TorsoLength = T;
        float k = T * radiusScale;

        _verts = null;
        if (fitToMesh) CollectVertices();
        Debug.Log(_verts != null ? "[Body] メッシュに合わせて判定を作成しました"
                                 : "[Body] 固定の比率で判定を作成しました(メッシュのフィットは無効/失敗)");

        // 腕(手は前腕とは別のカプセルにして、手の大きさに合わせる)
        bool lHand = TryTip(HumanBodyBones.LeftHand, HumanBodyBones.LeftLowerArm, PID.LHand, "左手");
        bool rHand = TryTip(HumanBodyBones.RightHand, HumanBodyBones.RightLowerArm, PID.RHand, "右手");
        Limb(HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  PID.LUpArm, upperArmRadius * k, 0f);
        Limb(HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand,      PID.LLoArm, foreArmRadius * k, lHand ? 0f : handExtend * T);
        Limb(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, PID.RUpArm, upperArmRadius * k, 0f);
        Limb(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,     PID.RLoArm, foreArmRadius * k, rHand ? 0f : handExtend * T);

        // 脚
        Limb(HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  PID.LUpLeg, thighRadius * k, 0f);
        Limb(HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,      PID.LLoLeg, shinRadius * k, 0f);
        Foot(HumanBodyBones.LeftFoot,  HumanBodyBones.LeftToes,  PID.LFoot, footRadius * k);
        Limb(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, PID.RUpLeg, thighRadius * k, 0f);
        Limb(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,     PID.RLoLeg, shinRadius * k, 0f);
        Foot(HumanBodyBones.RightFoot, HumanBodyBones.RightToes, PID.RFoot, footRadius * k);

        if (!(_verts != null && FitHead())) AddHead(T, k);
        if (!(_verts != null && FitTorso())) AddTorso(sMid, hMid, Vector3.Distance(lSh, rSh), T);
    }

    // ---------- メッシュからのフィット ----------

    // スキンメッシュの全頂点を現在のポーズで世界座標にし、部位ごとに振り分ける
    bool CollectVertices()
    {
        var map = new Dictionary<Transform, PID>();
        foreach (var m in PartMap) { var t = Bone(m.bone); if (t != null) map[t] = m.id; }

        var smrs = animator.GetComponentsInChildren<SkinnedMeshRenderer>();
        if (smrs.Length == 0) { Debug.LogWarning("[Body] SkinnedMeshRenderer が見つかりません"); return false; }

        var result = new Dictionary<PID, List<Vector3>>();
        foreach (var smr in smrs)
        {
            var mesh = smr.sharedMesh;
            if (mesh == null) continue;
            if (!mesh.isReadable)
            {
                Debug.LogWarning("[Body] メッシュが読み取り不可です。FBXの Inspector > Model > 'Read/Write' をオンにして Apply してください: " + mesh.name);
                return false;
            }
            Vector3[] verts = mesh.vertices;
            BoneWeight[] bw = mesh.boneWeights;
            var bindposes = mesh.bindposes;
            var bones = smr.bones;
            if (bw.Length != verts.Length || bones.Length == 0) continue;

            var mats = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                mats[i] = bones[i] != null ? bones[i].localToWorldMatrix * bindposes[i] : Matrix4x4.identity;

            var cache = new Dictionary<int, int>();   // ボーン番号 -> 部位(なければ-1)
            for (int v = 0; v < verts.Length; v++)
            {
                BoneWeight w = bw[v];
                int di = w.boneIndex0; float dw = w.weight0;
                if (w.weight1 > dw) { di = w.boneIndex1; dw = w.weight1; }
                if (w.weight2 > dw) { di = w.boneIndex2; dw = w.weight2; }
                if (w.weight3 > dw) { di = w.boneIndex3; dw = w.weight3; }
                if (dw <= 0f || di >= bones.Length || bones[di] == null) continue;

                if (!cache.TryGetValue(di, out int pid))
                {
                    pid = -1;
                    for (Transform t = bones[di]; t != null; t = t.parent)
                        if (map.TryGetValue(t, out PID id)) { pid = (int)id; break; }
                    cache[di] = pid;
                }
                if (pid < 0) continue;

                Vector3 p = Vector3.zero;
                if (w.weight0 > 0f) p += w.weight0 * mats[w.boneIndex0].MultiplyPoint3x4(verts[v]);
                if (w.weight1 > 0f) p += w.weight1 * mats[w.boneIndex1].MultiplyPoint3x4(verts[v]);
                if (w.weight2 > 0f) p += w.weight2 * mats[w.boneIndex2].MultiplyPoint3x4(verts[v]);
                if (w.weight3 > 0f) p += w.weight3 * mats[w.boneIndex3].MultiplyPoint3x4(verts[v]);

                if (!result.TryGetValue((PID)pid, out var list)) { list = new List<Vector3>(); result[(PID)pid] = list; }
                list.Add(p);
            }
        }

        if (result.Count == 0) return false;
        _verts = result;
        return true;
    }

    static float Pct(List<float> vals, float p)
    {
        vals.Sort();
        int i = Mathf.Clamp(Mathf.RoundToInt((vals.Count - 1) * p), 0, vals.Count - 1);
        return vals[i];
    }

    // 軸(a から dir 方向)に対する頂点の広がりから、カプセルの太さを決める。tip=trueなら先端まで延ばす
    bool FitCapsule(Transform parent, Vector3 a, Vector3 dir, float len, bool tip, PID id, string name, string label)
    {
        if (_verts == null || !_verts.TryGetValue(id, out var vs) || vs.Count < 8) return false;
        dir.Normalize();
        var rs = new List<float>(vs.Count);
        var ts = new List<float>(vs.Count);
        foreach (var v in vs)
        {
            Vector3 d = v - a;
            float t = Vector3.Dot(d, dir);
            rs.Add((d - dir * t).magnitude);
            ts.Add(t);
        }
        float r = Mathf.Max(Pct(rs, fitPercentile) * radiusScale, 0.005f);
        float end = tip ? Mathf.Max(len, Pct(ts, 0.98f) - r) : len;
        _parts.Add(new Part { col = MakeCapsule(parent, a, a + dir * end, r, name), label = label });
        return true;
    }

    void Limb(HumanBodyBones bone, HumanBodyBones child, PID id, float fallbackRadius, float fallbackExtend)
    {
        var b = Bone(bone); var c = Bone(child);
        if (b == null || c == null) return;
        Vector3 d = c.position - b.position;
        if (_verts != null && FitCapsule(b, b.position, d, d.magnitude, false, id, "Col_" + bone, Label(bone))) return;
        AddLimb(bone, child, fallbackRadius, fallbackExtend);
    }

    bool TryTip(HumanBodyBones tipBone, HumanBodyBones prevBone, PID id, string label)
    {
        if (_verts == null) return false;
        var h = Bone(tipBone); var f = Bone(prevBone);
        if (h == null || f == null) return false;
        return FitCapsule(h, h.position, h.position - f.position, 0f, true, id, "Col_" + tipBone, label);
    }

    void Foot(HumanBodyBones foot, HumanBodyBones toes, PID id, float fallbackRadius)
    {
        var f = Bone(foot); var t = Bone(toes);
        if (f == null || t == null) return;
        Vector3 d = t.position - f.position;
        if (_verts != null && FitCapsule(f, f.position, d, d.magnitude, true, id, "Col_" + foot, Label(foot))) return;
        AddLimb(foot, toes, fallbackRadius, 0f);
    }

    bool FitHead()
    {
        if (!_verts.TryGetValue(PID.Head, out var vs) || vs.Count < 8) return false;
        var head = Bone(HumanBodyBones.Head);
        if (head == null) return false;
        var neck = Bone(HumanBodyBones.Neck);
        Vector3 up = neck != null ? (head.position - neck.position).normalized : animator.transform.up;
        Vector3 basePos = head.position;

        Vector3 cen = Vector3.zero;
        foreach (var v in vs) cen += v;
        cen /= vs.Count;
        Vector3 off = (cen - basePos) - up * Vector3.Dot(cen - basePos, up);   // 軸からの中心のずれ

        var rs = new List<float>(vs.Count);
        var ts = new List<float>(vs.Count);
        foreach (var v in vs)
        {
            Vector3 d = v - basePos;
            float t = Vector3.Dot(d, up);
            rs.Add((d - up * t - off).magnitude);
            ts.Add(t);
        }
        float r = Mathf.Max(Pct(rs, fitPercentile) * radiusScale, 0.01f);
        float lo = Pct(ts, 0.02f) + r, hi = Pct(ts, 0.98f) - r;
        if (hi < lo) lo = hi = (lo + hi) * 0.5f;
        _parts.Add(new Part
        {
            col = MakeCapsule(head, basePos + off + up * lo, basePos + off + up * hi, r, "Col_Head"),
            label = "頭"
        });
        return true;
    }

    bool FitTorso()
    {
        if (!_verts.TryGetValue(PID.Torso, out var vs) || vs.Count < 16) return false;
        Quaternion R = animator.transform.rotation, inv = Quaternion.Inverse(R);
        var xs = new List<float>(vs.Count); var ys = new List<float>(vs.Count); var zs = new List<float>(vs.Count);
        foreach (var v in vs) { Vector3 q = inv * v; xs.Add(q.x); ys.Add(q.y); zs.Add(q.z); }
        float x0 = Pct(xs, 0.02f), x1 = Pct(xs, 0.98f);
        float y0 = Pct(ys, 0.02f), y1 = Pct(ys, 0.98f);
        float z0 = Pct(zs, 0.02f), z1 = Pct(zs, 0.98f);
        Vector3 center = R * new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
        Vector3 size = new Vector3(x1 - x0, y1 - y0, z1 - z0) * radiusScale;
        AddTorsoBox(center, size);
        return true;
    }

    // ---------- 固定比率(フィット失敗時) ----------

    void AddLimb(HumanBodyBones bone, HumanBodyBones child, float radius, float extend)
    {
        var b = Bone(bone);
        var c = Bone(child);
        if (b == null || c == null) return;
        Vector3 a = b.position, e = c.position;
        e += (e - a).normalized * extend;
        _parts.Add(new Part { col = MakeCapsule(b, a, e, radius, "Col_" + bone), label = Label(bone) });
    }

    void AddHead(float T, float k)
    {
        var head = Bone(HumanBodyBones.Head);
        if (head == null) return;
        var neck = Bone(HumanBodyBones.Neck);
        Vector3 up = neck != null ? (head.position - neck.position).normalized : animator.transform.up;
        Vector3 center = head.position + up * (headCenterOffset * T);
        float r = headWidth * 0.5f * k;
        float h = Mathf.Max(headHeight * k, 2f * r + 0.01f);
        Vector3 half = up * (h * 0.5f - r);
        _parts.Add(new Part { col = MakeCapsule(head, center - half, center + half, r, "Col_Head"), label = "頭" });
    }

    void AddTorso(Vector3 sMid, Vector3 hMid, float shoulderW, float T)
    {
        Vector3 size = new Vector3(Mathf.Max(shoulderW * torsoWidthScale, torsoWidthMin * T),
                                   T * torsoHeightScale, torsoDepth * T);
        AddTorsoBox((sMid + hMid) * 0.5f, size);
    }

    void AddTorsoBox(Vector3 centerWorld, Vector3 size)
    {
        var go = new GameObject("Col_Torso") { layer = _layer };
        go.transform.position = centerWorld;
        go.transform.rotation = animator.transform.rotation;
        go.transform.SetParent(_hips, true);                 // 胴(腰)の回転に追従する
        var box = go.AddComponent<BoxCollider>();
        Vector3 ls = go.transform.lossyScale;
        box.size = new Vector3(size.x / Mathf.Abs(ls.x), size.y / Mathf.Abs(ls.y), size.z / Mathf.Abs(ls.z));
        _parts.Add(new Part { col = box, label = "胴" });
    }

    CapsuleCollider MakeCapsule(Transform parent, Vector3 a, Vector3 b, float radius, string name)
    {
        var go = new GameObject(name) { layer = _layer };
        Vector3 d = b - a;
        Vector3 axis = d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up;
        go.transform.position = (a + b) * 0.5f;
        go.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
        go.transform.SetParent(parent, true);                // 骨の回転に追従する
        var c = go.AddComponent<CapsuleCollider>();
        c.direction = 1;
        Vector3 ls = go.transform.lossyScale;
        c.radius = radius / Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z));
        c.height = (d.magnitude + 2f * radius) / Mathf.Abs(ls.y);
        return c;
    }

    static string Label(HumanBodyBones b)
    {
        switch (b)
        {
            case HumanBodyBones.LeftUpperArm: return "左上腕";
            case HumanBodyBones.LeftLowerArm: return "左前腕";
            case HumanBodyBones.RightUpperArm: return "右上腕";
            case HumanBodyBones.RightLowerArm: return "右前腕";
            case HumanBodyBones.LeftUpperLeg: return "左もも";
            case HumanBodyBones.LeftLowerLeg: return "左すね";
            case HumanBodyBones.RightUpperLeg: return "右もも";
            case HumanBodyBones.RightLowerLeg: return "右すね";
            case HumanBodyBones.LeftFoot: return "左足先";
            case HumanBodyBones.RightFoot: return "右足先";
            default: return b.ToString();
        }
    }

    // ---- 判定 ----

    // 現在の形(ワールド座標)を計算し直す
    public void Refresh(bool syncPhysics = true)
    {
        if (syncPhysics) Physics.SyncTransforms();
        foreach (var p in _parts)
        {
            var t = p.col.transform;
            Vector3 ls = t.lossyScale;
            if (p.col is CapsuleCollider cap)
            {
                p.isBox = false;
                p.r = cap.radius * Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z));
                float h = Mathf.Max(cap.height * Mathf.Abs(ls.y), 2f * p.r);
                Vector3 center = t.TransformPoint(cap.center);
                float half = h * 0.5f - p.r;
                p.p0 = center + t.up * half;
                p.p1 = center - t.up * half;
            }
            else if (p.col is BoxCollider box)
            {
                p.isBox = true;
                p.c = t.TransformPoint(box.center);
                p.half = Vector3.Scale(box.size * 0.5f, new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                p.rot = t.rotation;
                p.invRot = Quaternion.Inverse(p.rot);
            }
        }
    }

    // 壁(指定のレイヤー)に当たっている部位の数。names を渡すと部位名も返す
    public int CountHits(int layerMask, List<string> names = null)
    {
        Refresh();
        int hits = 0;
        foreach (var p in _parts)
        {
            p.hit = p.isBox
                ? Physics.CheckBox(p.c, p.half, p.rot, layerMask, QueryTriggerInteraction.Ignore)
                : Physics.CheckCapsule(p.p0, p.p1, p.r, layerMask, QueryTriggerInteraction.Ignore);
            if (p.hit) { hits++; names?.Add(p.label); }
        }
        return hits;
    }

    // 点から、体のどこかまでの最短距離(体の中なら0)。直前の Refresh の結果を使う
    public float DistanceTo(Vector3 pt)
    {
        float best = float.MaxValue;
        foreach (var p in _parts)
        {
            float d;
            if (p.isBox)
            {
                Vector3 q = p.invRot * (pt - p.c);
                d = new Vector3(Mathf.Max(Mathf.Abs(q.x) - p.half.x, 0f),
                                Mathf.Max(Mathf.Abs(q.y) - p.half.y, 0f),
                                Mathf.Max(Mathf.Abs(q.z) - p.half.z, 0f)).magnitude;
            }
            else
            {
                Vector3 ab = p.p1 - p.p0;
                float t = Mathf.Clamp01(Vector3.Dot(pt - p.p0, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
                d = Mathf.Max(0f, Vector3.Distance(pt, p.p0 + ab * t) - p.r);
            }
            if (d < best) best = d;
        }
        return best;
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos || !Application.isPlaying || _parts.Count == 0) return;
        Refresh(false);
        foreach (var p in _parts)
        {
            Gizmos.color = p.hit ? Color.red : Color.green;
            if (p.isBox)
            {
                Gizmos.matrix = Matrix4x4.TRS(p.c, p.rot, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero, p.half * 2f);
                Gizmos.matrix = Matrix4x4.identity;
            }
            else
            {
                Gizmos.DrawWireSphere(p.p0, p.r);
                Gizmos.DrawWireSphere(p.p1, p.r);
                Gizmos.DrawLine(p.p0, p.p1);
            }
        }
    }
}