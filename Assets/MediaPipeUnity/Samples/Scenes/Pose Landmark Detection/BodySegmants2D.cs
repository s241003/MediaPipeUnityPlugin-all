using System.Collections.Generic;
using UnityEngine;

// 体を「骨2点＝線分1本＋半径」で表す（旧 BodyColliders の置き換え）。
// コライダーもメッシュフィットも使わない。線分はアバターの骨のワールド座標（x,y）から作る。
// 判定に使う平面は planeZ（Inspectorで JudgePoint の z に合わせる。Shinobi では 52.85）。
// 接触した部位は FlashParts() で一定時間赤く表示する（細い円柱＋球のプロキシをアバターの手前に出す）。
[DefaultExecutionOrder(100)]   // LateUpdate を AvatarRetarget（骨の更新）の後に回すため
public class BodySegments2D : MonoBehaviour
{
    public struct Segment
    {
        public string label;      // 部位名（例：左前腕）。フィードバック表示にも使う
        public Vector2 a, b;      // 線分の両端（ワールドの x,y）
        public float radius;      // 太さ（ワールド単位）
    }

    public static BodySegments2D Instance { get; private set; }

    [Header("アバター(Humanoid設定のモデルのAnimator)")]
    public Animator animator;

    [Header("判定平面")]
    public float planeZ = 52.85f;   // JudgePoint の world z と一致させること

    [Header("太さ（胴の長さに対する比）")]
    public float torsoRadiusRatio = 0.30f;   // 胴（腰中心→肩中心の線分）
    public float headRadiusRatio = 0.20f;
    public float upperArmRadiusRatio = 0.10f;
    public float foreArmRadiusRatio = 0.09f;
    public float handRadiusRatio = 0.07f;
    public float handExtendRatio = 0.20f;    // 手の先までの長さ
    public float thighRadiusRatio = 0.15f;
    public float shinRadiusRatio = 0.11f;
    public float footRadiusRatio = 0.08f;

    [Header("接触部位の赤色表示")]
    public bool showProxies = true;
    public float proxyZOffset = 1.5f;        // アバターよりカメラ側にどれだけ出すか
    public float proxyThin = 0.4f;           // プロキシの太さ（実半径に対する比）
    public float flashSeconds = 1.2f;        // 赤くする時間
    public Color normalColor = new Color(0.25f, 0.85f, 0.85f);
    public Color hitColor = new Color(1f, 0.15f, 0.15f);

    public float TorsoLength { get; private set; }
    public float PlaneZ => planeZ;
    public readonly List<Segment> Segments = new List<Segment>();

    // 線分の定義：ラベル, 始点の骨, 終点の骨, 半径比
    static readonly (string label, HumanBodyBones a, HumanBodyBones b, int kind)[] Defs =
    {
        ("左上腕", HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  0),
        ("左前腕", HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand,      1),
        ("右上腕", HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 0),
        ("右前腕", HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,     1),
        ("左もも", HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  2),
        ("左すね", HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,      3),
        ("右もも", HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, 2),
        ("右すね", HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,     3),
    };
    // kind: 0=上腕 1=前腕（手の延長つき） 2=もも 3=すね（足先つき）

    class Proxy
    {
        public GameObject go;
        public MeshRenderer rend;
        public Material mat;
        public string label;
        public float flashUntil;
    }

    readonly List<Proxy> _proxies = new List<Proxy>();
    Transform _lSh, _rSh, _lHp, _rHp, _head, _neck;
    bool _built;

    void Awake() { Instance = this; }
    void OnDestroy() { if (Instance == this) Instance = null; }

    void Start()
    {
        if (animator == null) { Debug.LogError("BodySegments2D: Animator が未設定です", this); enabled = false; return; }
        _lSh = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        _rSh = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        _lHp = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        _rHp = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        _head = animator.GetBoneTransform(HumanBodyBones.Head);
        _neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        Refresh();
        BuildProxies();
        _built = true;
    }

    float RadiusFor(int kind)
    {
        float T = TorsoLength;
        switch (kind)
        {
            case 0: return upperArmRadiusRatio * T;
            case 1: return foreArmRadiusRatio * T;
            case 2: return thighRadiusRatio * T;
            case 3: return shinRadiusRatio * T;
            default: return 0.1f * T;
        }
    }

    Vector2 P(Transform t) { return new Vector2(t.position.x, t.position.y); }

    // 現在の骨の位置から線分を作り直す
    public void Refresh()
    {
        Segments.Clear();
        if (_lSh == null) return;

        Vector2 sMid = (P(_lSh) + P(_rSh)) * 0.5f;
        Vector2 hMid = (P(_lHp) + P(_rHp)) * 0.5f;
        float T = Vector2.Distance(sMid, hMid);
        if (T > 1e-4f) TorsoLength = T;

        // 胴
        Segments.Add(new Segment { label = "胴", a = hMid, b = sMid, radius = torsoRadiusRatio * TorsoLength });

        // 頭（首から頭の向きに少し上げた点を中心とする円＝点の線分）
        if (_head != null)
        {
            Vector2 hp = P(_head);
            Vector2 center = hp;
            if (_neck != null)
            {
                Vector2 dir = hp - P(_neck);
                if (dir.sqrMagnitude > 1e-8f) center = hp + dir.normalized * (0.15f * TorsoLength);
            }
            Segments.Add(new Segment { label = "頭", a = center, b = center, radius = headRadiusRatio * TorsoLength });
        }

        foreach (var d in Defs)
        {
            var ta = animator.GetBoneTransform(d.a);
            var tb = animator.GetBoneTransform(d.b);
            if (ta == null || tb == null) continue;
            Vector2 a = P(ta), b = P(tb);
            // 前腕(kind=1)は終点が手の骨。手の先は AddHand の「手」線分が担当する
            if (d.kind == 3)   // すね：足先まで含める
            {
                var toes = animator.GetBoneTransform(
                    d.a == HumanBodyBones.LeftLowerLeg ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes);
                var foot = animator.GetBoneTransform(
                    d.a == HumanBodyBones.LeftLowerLeg ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                if (toes != null) b = P(toes);
                else if (foot != null) b = P(foot);
            }
            Segments.Add(new Segment { label = d.label, a = a, b = b, radius = RadiusFor(d.kind) });
        }

        // 手（前腕とは別に小さく）
        AddHand(HumanBodyBones.LeftHand, HumanBodyBones.LeftLowerArm, "左手");
        AddHand(HumanBodyBones.RightHand, HumanBodyBones.RightLowerArm, "右手");
    }

    void AddHand(HumanBodyBones handBone, HumanBodyBones foreBone, string label)
    {
        var h = animator.GetBoneTransform(handBone);
        var f = animator.GetBoneTransform(foreBone);
        if (h == null || f == null) return;
        Vector2 a = P(h);
        Vector2 dir = a - P(f);
        Vector2 b = dir.sqrMagnitude > 1e-8f ? a + dir.normalized * (handExtendRatio * TorsoLength) : a;
        Segments.Add(new Segment { label = label, a = a, b = b, radius = handRadiusRatio * TorsoLength });
    }

    // 点 p への最短距離（符号なし。体の中なら 0 側に寄るよう半径を引く）
    public float DistanceTo(Vector2 p)
    {
        float best = float.MaxValue;
        foreach (var s in Segments)
        {
            float d = DistancePointSegment(p, s.a, s.b) - s.radius;
            if (d < best) best = d;
        }
        return best;
    }

    // 点 p に最も近い部位のラベルと、その距離（半径を引いた値）を返す
    public string ClosestPart(Vector2 p, out float distance)
    {
        string label = null;
        float best = float.MaxValue;
        foreach (var s in Segments)
        {
            float d = DistancePointSegment(p, s.a, s.b) - s.radius;
            if (d < best) { best = d; label = s.label; }
        }
        distance = best;
        return label;
    }

    public static float DistancePointSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.sqrMagnitude > 1e-9f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    // ---- 接触部位の赤色表示 ----

    void BuildProxies()
    {
        if (!showProxies) return;
        var holder = new GameObject("BodyProxies");
        foreach (var s in Segments)
        {
            bool isHead = s.label == "頭";
            var go = GameObject.CreatePrimitive(isHead ? PrimitiveType.Sphere : PrimitiveType.Cylinder);
            go.name = "Proxy_" + s.label;
            go.transform.SetParent(holder.transform, false);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);   // 判定には使わないのでコライダーは外す
            var rend = go.GetComponent<MeshRenderer>();
            var mat = rend.material;         // インスタンス化して色を個別に変える
            mat.color = normalColor;
            _proxies.Add(new Proxy { go = go, rend = rend, mat = mat, label = s.label });
        }
    }

    void LateUpdate()
    {
        Refresh();
        if (!_built || !showProxies) return;
        float now = Time.time;
        int n = Mathf.Min(_proxies.Count, Segments.Count);
        for (int i = 0; i < n; i++)
        {
            var s = Segments[i];
            var px = _proxies[i];
            Vector3 a = new Vector3(s.a.x, s.a.y, planeZ + proxyZOffset);
            Vector3 b = new Vector3(s.b.x, s.b.y, planeZ + proxyZOffset);
            Vector3 mid = (a + b) * 0.5f;
            float len = Vector3.Distance(a, b);
            px.go.transform.position = mid;
            if (px.label == "頭")
            {
                float d = 2f * s.radius * proxyThin;
                px.go.transform.localScale = new Vector3(d, d, d);
            }
            else
            {
                Vector3 dir = b - a;
                if (len > 1e-4f)
                    px.go.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir / len);
                float w = 2f * s.radius * proxyThin;
                px.go.transform.localScale = new Vector3(w, Mathf.Max(len, 0.01f) * 0.5f, w);
            }
            px.mat.color = now < px.flashUntil ? hitColor : normalColor;
        }
    }

    // 指定した部位を赤くする（WallJudge2D の結果を受けて GameManager から呼ぶ）
    public void FlashParts(IEnumerable<string> labels)
    {
        if (labels == null) return;
        float until = Time.time + flashSeconds;
        foreach (var label in labels)
            foreach (var px in _proxies)
                if (px.label == label && until > px.flashUntil) px.flashUntil = until;
    }

    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        foreach (var s in Segments)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(new Vector3(s.a.x, s.a.y, planeZ), new Vector3(s.b.x, s.b.y, planeZ));
            Gizmos.DrawWireSphere(new Vector3(s.a.x, s.a.y, planeZ), s.radius);
            Gizmos.DrawWireSphere(new Vector3(s.b.x, s.b.y, planeZ), s.radius);
        }
    }
}
