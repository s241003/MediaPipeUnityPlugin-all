using System.Collections.Generic;
using UnityEngine;

public class AvatarRetarget : MonoBehaviour
{
    [Header("アバター(Humanoid設定のモデルのAnimator)")]
    public Animator animator;

    [Header("入力の補正")]
    public float aspect = 4f / 3f;     // カメラの 幅/高さ (640x480 なら 4/3)
    public float zScale = 1f;          // Flatten Z がオフのときだけ使う
    public float smoothing = 15f;      // 大きいほど速く追従、小さいほど滑らか

    [Header("奥行き")]
    public bool flattenZ = true;       // オン:見た目も判定も z = Plane Z の平面に固定する
    public float planeZ = 0f;

    [Header("位置の追従(本人の大きさに左右されない)")]
    public bool followPosition = true;
    public float calibrateDelay = 3f;      // 最初の検出から、基準の登録を始めるまでの秒数
    public float calibrateDuration = 1f;   // 基準の登録にかける秒数
    public bool recalibrate;               // Play中にチェックを入れると、基準を登録し直す

    public bool IsCalibrated => _calibrated;

    static readonly (HumanBodyBones bone, HumanBodyBones child, int a, int b)[] Map = {
        (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  11, 13),
        (HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand,      13, 15),
        (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 12, 14),
        (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,     14, 16),
        (HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  23, 25),
        (HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,      25, 27),
        (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, 24, 26),
        (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,     26, 28),
        (HumanBodyBones.LeftFoot,      HumanBodyBones.LeftToes,      27, 31),
        (HumanBodyBones.RightFoot,     HumanBodyBones.RightToes,     28, 32),
    };

    class Part
    {
        public Transform bone;
        public int a, b;
        public Vector3 restDir;
        public Quaternion restRot;
        public Quaternion current;
    }

    Transform _root, _hips;
    Part[] _parts;
    Quaternion _hipsRestRot, _hipsCurrent;
    Transform _head;
    Quaternion _headRestRot, _headCurrent;
    readonly Vector3[] _raw = new Vector3[33];
    readonly Vector3[] _w = new Vector3[33];

    // 位置の基準
    Vector3 _hipsAnchor;          // モデルの腰の初期位置(基準の姿勢のとき、ここに来る)
    float _modelTorso;            // モデルの胴の長さ
    bool _calibrated, _calibrating;
    float _firstDataTime = -1f, _calibEnd, _posScale, _torsoSum;
    Vector2 _hipRef, _hipSum;
    int _calibCount;

    void Start()
    {
        _root = animator.transform;

        var list = new List<Part>();
        foreach (var m in Map)
        {
            var bone = animator.GetBoneTransform(m.bone);
            var child = animator.GetBoneTransform(m.child);
            if (bone == null || child == null) continue;
            list.Add(new Part
            {
                bone = bone, a = m.a, b = m.b,
                restDir = _root.InverseTransformDirection((child.position - bone.position).normalized),
                restRot = Quaternion.Inverse(_root.rotation) * bone.rotation,
                current = bone.rotation
            });
        }
        _parts = list.ToArray();

        _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        _hipsRestRot = Quaternion.Inverse(_root.rotation) * _hips.rotation;
        _hipsCurrent = _hips.rotation;

        _hipsAnchor = _hips.position;
        Vector3 sMid = (animator.GetBoneTransform(HumanBodyBones.LeftUpperArm).position
                      + animator.GetBoneTransform(HumanBodyBones.RightUpperArm).position) * 0.5f;
        Vector3 hMid = (animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg).position
                      + animator.GetBoneTransform(HumanBodyBones.RightUpperLeg).position) * 0.5f;
        _modelTorso = Vector3.Distance(sMid, hMid);
        
        _head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (_head != null)
        {
            _headRestRot = Quaternion.Inverse(_root.rotation) * _head.rotation;
            _headCurrent = _head.rotation;
        }
    }

    void LateUpdate()
    {
        if (!PoseBuffer.TryRead(_raw)) return;

        for (int i = 0; i < 33; i++)
            _w[i] = new Vector3((0.5f - _raw[i].x) * aspect,
                                (0.5f - _raw[i].y),
                                flattenZ ? 0f : -_raw[i].z * aspect * zScale);

        float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);

        // 画像上の腰・肩の位置(画像の高さ=1の単位)
        Vector2 hip2 = new Vector2((0.5f - (_raw[23].x + _raw[24].x) * 0.5f) * aspect,
                                   0.5f - (_raw[23].y + _raw[24].y) * 0.5f);
        Vector2 sh2 = new Vector2((0.5f - (_raw[11].x + _raw[12].x) * 0.5f) * aspect,
                                  0.5f - (_raw[11].y + _raw[12].y) * 0.5f);
        UpdateCalibration(hip2, Vector2.Distance(sh2, hip2));

        // 胴(Hips)
        Vector3 up = (_w[11] + _w[12]) * 0.5f - (_w[23] + _w[24]) * 0.5f;
        Vector3 right = _w[12] - _w[11];
        Vector3 fwd = flattenZ ? Vector3.forward : Vector3.Cross(right, up);
        if (up.sqrMagnitude > 1e-6f && fwd.sqrMagnitude > 1e-6f)
        {
            Quaternion target = _root.rotation * Quaternion.LookRotation(fwd, up) * _hipsRestRot;
            _hipsCurrent = Quaternion.Slerp(_hipsCurrent, target, k);
            _hips.rotation = _hipsCurrent;
        }

                // 頭:首の向きと、左右の耳を結ぶ線から、頭の傾きを決める
        if (_head != null)
        {
            Vector3 earMid = (_w[7] + _w[8]) * 0.5f;
            Vector3 neckDir = earMid - (_w[11] + _w[12]) * 0.5f;
            Vector3 earLine = _w[8] - _w[7];
            Vector3 headUp = neckDir.sqrMagnitude > 1e-8f ? neckDir.normalized : Vector3.up;
            if (earLine.magnitude > 0.12f * up.magnitude)   // 耳が見えているときだけ使う
            {
                Vector3 perp = new Vector3(-earLine.y, earLine.x, 0f).normalized;
                headUp = (headUp + perp).normalized;
            }
            Vector3 headFwd = flattenZ ? Vector3.forward : Vector3.Cross(earLine, headUp);
            if (headFwd.sqrMagnitude > 1e-6f)
            {
                Quaternion target = _root.rotation * Quaternion.LookRotation(headFwd, headUp) * _headRestRot;
                _headCurrent = Quaternion.Slerp(_headCurrent, target, k);
                _head.rotation = _headCurrent;
            }
        }

        // 腕・脚
        float torso2D = up.magnitude;
        foreach (var p in _parts)
        {
            Vector3 dir = _w[p.b] - _w[p.a];
            // 奥行き方向を向いて、ほぼ点に見える骨は更新しない(向きが不安定になるため)
            if (dir.magnitude < 0.08f * torso2D || dir.sqrMagnitude < 1e-8f) continue;
            Quaternion target = _root.rotation *
                (Quaternion.FromToRotation(p.restDir, dir.normalized) * p.restRot);
            p.current = Quaternion.Slerp(p.current, target, k);
            p.bone.rotation = p.current;
        }

        // 位置:基準からの移動量を、胴の長さの比で拡大・縮小して写す
        if (followPosition && _calibrated)
        {
            Vector2 d = hip2 - _hipRef;
            Vector3 target = _hipsAnchor + new Vector3(d.x, d.y, 0f) * _posScale;
            target.z = flattenZ ? planeZ : _hips.position.z;
            _root.position += (target - _hips.position) * k;
        }
    }

    void UpdateCalibration(Vector2 hip2, float torso2)
    {
        float now = Time.time;
        if (_firstDataTime < 0f)
        {
            _firstDataTime = now;
            Debug.Log($"[Retarget] {calibrateDelay}秒後に基準を登録します。直立して正面を向いてください");
        }

        if (recalibrate) { recalibrate = false; BeginCalibration(now); }
        else if (!_calibrated && !_calibrating && now - _firstDataTime >= calibrateDelay)
            BeginCalibration(now);

        if (_calibrating)
        {
            _hipSum += hip2;
            _torsoSum += torso2;
            _calibCount++;
            if (now >= _calibEnd) FinishCalibration();
        }
    }

    void BeginCalibration(float now)
    {
        _calibrating = true;
        _calibEnd = now + calibrateDuration;
        _hipSum = Vector2.zero;
        _torsoSum = 0f;
        _calibCount = 0;
        Debug.Log("[Retarget] 基準を登録中です。動かないでください");
    }

    void FinishCalibration()
    {
        _calibrating = false;
        float refTorso = _calibCount > 0 ? _torsoSum / _calibCount : 0f;
        if (refTorso < 1e-3f)
        {
            Debug.LogWarning("[Retarget] 基準の登録に失敗しました(上半身が検出できていません)");
            return;
        }
        _hipRef = _hipSum / _calibCount;
        _posScale = _modelTorso / refTorso;
        _calibrated = true;
        Debug.Log($"[Retarget] 基準登録完了(倍率 {_posScale:F2})");
    }
}