using System.Collections.Generic;
using UnityEngine;

// ポーズ（骨の向き）と、腰の横・縦移動をアバターへ反映する。
// 刷新点：
//  - 旧版のキャリブレーション（直立3秒＋1秒平均）は廃止。代わりに「ゲーム開始時の立ち位置」を
//    基準（ホーム）にして、そこからの腰の移動量をそのままアバターの X,Y に写す。
//    位置を勝手に補正はしない（写すのは本人の移動量だけ）。※2026-11-17、宇留野の選択で移動反映を追加。
//  - ルートの rotation には触らない。動かすのは骨の回転と、ルートの X,Y だけ（Z はシーンの置き場所のまま）。
//  - ポーズが古くなったら（カメラから外れたら）更新を止め、IsReady が false になる。
public class AvatarRetarget : MonoBehaviour
{
    [Header("アバター(Humanoid設定のモデルのAnimator)")]
    public Animator animator;

    [Header("入力の補正")]
    public float aspect = 4f / 3f;     // カメラの 幅/高さ (640x480 なら 4/3)
    public float zScale = 1f;          // Flatten Z がオフのときだけ使う
    public float smoothing = 15f;      // 大きいほど速く追従、小さいほど滑らか
    public bool mirrorLeftRight = true;   // アバターと向かい合うので鏡像が正解（右手を挙げたら画面右側の腕が上がる）。逆に感じたらオフ

    [Header("奥行き")]
    public bool flattenZ = true;       // オン:骨の向きを平面内で決める（アバター自体は動かない）

    [Header("ポーズの鮮度")]
    public float staleSeconds = 0.5f;  // この秒数より古いポーズは使わない（提案値：実測で調整）

    [Header("位置の追従（腰の横・縦移動）")]
    public bool followPosition = true;
    public float moveRangeX = 40f;     // 腰が画面の端（中心から0.5）まで動いたときのアバターの移動量×2。提案値：実測で調整
    public float moveRangeY = 24f;     // 縦は横より小さめ。提案値：実測で調整
    public bool flipX = false;         // 左右が逆に動くときだけオン
    public bool flipY = false;         // 上下が逆に動くときだけオン

    // ポーズが生きていれば true。GameManager はこれを見て開始する（キャリブレーションの代わり）
    public bool IsReady => PoseBuffer.HasFreshData;

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

    // ランドマークの左右ペア（鏡像化のために入れ替える）
    static readonly int[] SwapLR = {
        0, 4, 5, 6, 1, 2, 3, 8, 7, 10, 9, 12, 11, 14, 13, 16, 15,
        18, 17, 20, 19, 22, 21, 24, 23, 26, 25, 28, 27, 30, 29, 32, 31
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
    Vector3 _homePos;                  // シーンで置いた場所（ホーム）。Z はずっとこれ
    Vector2 _refHip;                   // 基準にした腰の画面内位置（正規化座標）
    bool _hasRef;
    Part[] _parts;
    Quaternion _hipsRestRot, _hipsCurrent;
    Transform _head;
    Quaternion _headRestRot, _headCurrent;
    readonly Vector3[] _raw = new Vector3[33];
    readonly Vector3[] _w = new Vector3[33];

    // 基準（ホーム）を「いまの立ち位置」に取り直す。GameManager がゲーム開始時に呼ぶ
    public void Recenter() { _hasRef = false; }

    void Start()
    {
        _root = animator.transform;
        _homePos = _root.position;

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

        _head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (_head != null)
        {
            _headRestRot = Quaternion.Inverse(_root.rotation) * _head.rotation;
            _headCurrent = _head.rotation;
        }
    }

    void LateUpdate()
    {
        // 古いポーズでは更新しない（人が画角から外れたらアバターはその場で止まる）
        if (!PoseBuffer.TryReadFresh(_raw, staleSeconds)) return;

        for (int i = 0; i < 33; i++)
        {
            int src = mirrorLeftRight ? SwapLR[i] : i;   // 鏡像：左の骨には右側のランドマークを使う
            _w[i] = new Vector3((0.5f - _raw[src].x) * aspect,
                                (0.5f - _raw[src].y),
                                flattenZ ? 0f : -_raw[src].z * aspect * zScale);
        }

        float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);

        // 位置の追従：腰（23,24の中点）の画面内位置を、基準からの移動量としてアバターの X,Y に写す。
        // 画面の向きの都合：利用者が自分の左へ動くと画像の x は増えるが、画面では左＝world の -X 側なので符号を反転する（骨の変換と同じ流儀）。
        if (followPosition)
        {
            Vector2 hip = new Vector2((_raw[23].x + _raw[24].x) * 0.5f, (_raw[23].y + _raw[24].y) * 0.5f);
            if (!_hasRef) { _refHip = hip; _hasRef = true; }
            float ox = -(hip.x - _refHip.x) * moveRangeX * (flipX ? -1f : 1f);
            float oy = -(hip.y - _refHip.y) * moveRangeY * (flipY ? -1f : 1f);
            ox = Mathf.Clamp(ox, -moveRangeX * 0.6f, moveRangeX * 0.6f);
            oy = Mathf.Clamp(oy, -moveRangeY * 0.6f, moveRangeY * 0.6f);
            Vector3 target = new Vector3(_homePos.x + ox, _homePos.y + oy, _homePos.z);
            _root.position = Vector3.Lerp(_root.position, target, k);
        }

        // 胴(Hips)の向き：肩と腰の位置関係から決める（位置ではなく向きだけ）
        Vector3 up = (_w[11] + _w[12]) * 0.5f - (_w[23] + _w[24]) * 0.5f;
        Vector3 right = _w[12] - _w[11];
        Vector3 fwd = flattenZ ? Vector3.forward : Vector3.Cross(right, up);
        if (up.sqrMagnitude > 1e-6f && fwd.sqrMagnitude > 1e-6f)
        {
            Quaternion target = _root.rotation * Quaternion.LookRotation(fwd, up) * _hipsRestRot;
            _hipsCurrent = Quaternion.Slerp(_hipsCurrent, target, k);
            _hips.rotation = _hipsCurrent;
        }

        // 頭：首の向きと、左右の耳を結ぶ線から傾きを決める
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

        // ※ルートの回転には触らない。位置は上の「位置の追従」で X,Y だけ動かす。
    }
}
