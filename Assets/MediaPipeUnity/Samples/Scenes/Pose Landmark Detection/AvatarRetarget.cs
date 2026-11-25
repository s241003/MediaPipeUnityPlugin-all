using System.Collections.Generic;
using UnityEngine;

// ポーズ（骨の向き）と、腰の横移動をアバターへ反映する。縦は基準面で固定（v1.15〜）。
// 刷新点：
//  - 旧版のキャリブレーション（直立3秒＋1秒平均）は廃止。代わりに「ゲーム開始時の立ち位置」を
//    基準（ホーム）にして、そこからの腰の移動量をそのままアバターの X,Y に写す。
//    位置を勝手に補正はしない（写すのは本人の移動量だけ）。※2026-11-17、宇留野の選択で移動反映を追加。
//  - ルートの rotation には触らない。動かすのは骨の回転と、ルートの X,Y だけ（Z はシーンの置き場所のまま）。
//  - ポーズが古くなったら（カメラから外れたら）更新を止め、IsReady が false になる。
//  - 認識の安定化（v1.12）：ランドマーク自体を平滑化し、信頼度（visibility）の低い点では骨を回さない。
//    頭は「肩→鼻」の向きだけで決め（耳の線で傾ける旧方式はブレの原因だったので廃止）、
//    回転上限＋首への配分つき。手の骨も手首→指の付け根の向きで駆動する。
//  - v1.13（2026-11-21）：左右の鏡像入れ替え（旧 mirrorLeftRight / SwapLR）を廃止し、
//    ランドマークを解剖学どおり（左の骨←左のランドマーク）に使うよう戻した。これが交差の原因だった。
//    理由：画像座標→アバター座標の変換 (0.5 - x) 自体が「向かい合っている」ことの補正であり、
//    その上に左右ラベルまで入れ替えると二重に鏡像化され、左腕の目標方向が反対側（+X）を指して
//    両手を挙げただけで腕が交差する。モデルの実測でも裏付け済み：Y Bot は FBX 内で正面(+Z)向き・
//    解剖学左が +X 側、Unity 取り込みで X が反転するため、ルートのローカル空間では左腕の骨は
//    -X 側にある。(0.5 - x) 変換後の左腕の目標方向は -X 側を向くので、入れ替えなしが幾何学的に正しい。
//    ※シーンに残っている旧 mirrorLeftRight の値は、項目自体が無くなったため無視される（エラーにはならない）。
public class AvatarRetarget : MonoBehaviour
{
    [Header("アバター(Humanoid設定のモデルのAnimator)")]
    public Animator animator;

    [Header("入力の補正")]
    public float aspect = 4f / 3f;     // カメラの 幅/高さ (640x480 なら 4/3)
    public float zScale = 1f;          // Flatten Z がオフのときだけ使う
    public float smoothing = 15f;      // 大きいほど速く追従、小さいほど滑らか

    [Header("奥行き")]
    public bool flattenZ = true;       // オン:骨の向きを平面内で決める（アバター自体は動かない）

    [Header("ポーズの鮮度")]
    public float staleSeconds = 0.5f;  // この秒数より古いポーズは使わない（提案値：実測で調整）

    [Header("認識の安定化")]
    public float landmarkSmoothing = 25f;   // ランドマーク自体の平滑化（大きいほどブレが減るが遅れる）。手首・耳のブレ対策
    public float minVisibility = 0.3f;      // 信頼度がこれ未満の点は骨の更新に使わない（隠れた点のゴミ座標で回さない）
    public float maxHeadAngle = 50f;        // 頭の回転の上限（度）。首があらぬ方向へ曲がらないように
    public float distalSmoothingScale = 0.6f;   // 前腕・手は平滑化を強めにする（ブレが目立つため）

    [Header("位置の追従（横だけ。縦は基準面で固定）")]
    public bool followPosition = true;
    public float moveRangeX = 40f;     // 腰が画面の端（中心から0.5）まで動いたときのアバターの移動量×2。提案値：実測で調整
    public bool flipX = false;         // 左右が逆に動くときだけオン
    public float homeYOffset = 0f;     // 縦の固定位置の微調整（基準面＝シーンで置いた高さ からのずらし量）。壁の穴と縦が合わないときだけ触る

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
        // 手の骨：手首→人差し指の付け根（19/20）の向きで回す。指の骨が無いモデルでは自動でスキップされる
        (HumanBodyBones.LeftHand,      HumanBodyBones.LeftMiddleProximal,  15, 19),
        (HumanBodyBones.RightHand,     HumanBodyBones.RightMiddleProximal, 16, 20),
    };

    class Part
    {
        public Transform bone;
        public int a, b;
        public bool distal;   // 前腕・手（ブレが目立つので平滑化を強める）
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
    Transform _head, _neck;
    Quaternion _headRestRot, _headCurrent, _neckRestRot, _neckCurrent;
    readonly Vector3[] _raw = new Vector3[33];
    readonly Vector3[] _w = new Vector3[33];
    readonly Vector3[] _ws = new Vector3[33];   // 平滑化したランドマーク（実際に使うのはこちら）
    readonly float[] _vis = new float[33];     // ランドマークと同じ番号の信頼度
    bool _wsInit;

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
                distal = m.a == 13 || m.a == 14 || m.a == 15 || m.a == 16,
                restDir = _root.InverseTransformDirection((child.position - bone.position).normalized),
                restRot = Quaternion.Inverse(_root.rotation) * bone.rotation,
                current = bone.rotation
            });
        }
        _parts = list.ToArray();

        // 左右の向きの自己診断（1回だけ）：モデルの左上腕の restDir.x がマイナス、右がプラスなら
        // 「ルートのローカル空間で左腕は -X 側」という想定どおり。逆だったらモデル側の左右が想定と違う。
        foreach (var p in _parts)
        {
            if (p.a == 11) Debug.Log($"[AvatarRetarget] 左上腕 restDir.x = {p.restDir.x:F2}（マイナスが想定どおり）");
            if (p.a == 12) Debug.Log($"[AvatarRetarget] 右上腕 restDir.x = {p.restDir.x:F2}（プラスが想定どおり）");
        }

        _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        _hipsRestRot = Quaternion.Inverse(_root.rotation) * _hips.rotation;
        _hipsCurrent = _hips.rotation;

        _head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (_head != null)
        {
            _headRestRot = Quaternion.Inverse(_root.rotation) * _head.rotation;
            _headCurrent = _head.rotation;
        }

        _neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        if (_neck != null)
        {
            _neckRestRot = Quaternion.Inverse(_root.rotation) * _neck.rotation;
            _neckCurrent = _neck.rotation;
        }
    }

    void LateUpdate()
    {
        // 古いポーズでは更新しない（人が画角から外れたらアバターはその場で止まる）
        if (!PoseBuffer.TryReadFresh(_raw, staleSeconds)) return;

        // 画像座標→アバターのローカル座標へ。(0.5 - x) が「向かい合っている」ことの補正そのもの。
        // ここで左右ラベルは入れ替えない（入れ替えると二重鏡像になり腕が交差する。v1.13 のコメント参照）。
        for (int i = 0; i < 33; i++)
        {
            _w[i] = new Vector3((0.5f - _raw[i].x) * aspect,
                                (0.5f - _raw[i].y),
                                flattenZ ? 0f : -_raw[i].z * aspect * zScale);
            _vis[i] = PoseBuffer.Visibility(i);
        }

        // ランドマーク自体を平滑化する（手首・耳などの細かいブレを、そのまま骨に伝えない）
        float kl = 1f - Mathf.Exp(-landmarkSmoothing * Time.deltaTime);
        if (!_wsInit) { System.Array.Copy(_w, _ws, 33); _wsInit = true; }
        else for (int i = 0; i < 33; i++) _ws[i] = Vector3.Lerp(_ws[i], _w[i], kl);

        float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);

        // 位置の追従：腰（23,24の中点）の画面内位置を、基準からの移動量としてアバターの X にだけ写す。
        // 縦（Y）は基準面＝シーンで置いた高さ（_homePos.y）に固定する。利用者がしゃがんだり背伸びしても
        // 体全体は上下せず、壁の穴との縦位置が常に合う（しゃがむ動き自体は骨の回転で表現される）。
        // 画面の向きの都合：利用者が自分の左へ動くと画像の x は増えるが、画面では左＝world の -X 側なので符号を反転する（骨の変換と同じ流儀）。
        if (followPosition)
        {
            Vector2 hip = new Vector2((_raw[23].x + _raw[24].x) * 0.5f, (_raw[23].y + _raw[24].y) * 0.5f);
            if (!_hasRef) { _refHip = hip; _hasRef = true; }
            float ox = -(hip.x - _refHip.x) * moveRangeX * (flipX ? -1f : 1f);
            ox = Mathf.Clamp(ox, -moveRangeX * 0.6f, moveRangeX * 0.6f);
            Vector3 target = new Vector3(_homePos.x + ox, _homePos.y + homeYOffset, _homePos.z);
            _root.position = Vector3.Lerp(_root.position, target, k);
        }

        // 胴(Hips)の向き：肩と腰の位置関係から決める（位置ではなく向きだけ）
        Vector3 up = (_ws[11] + _ws[12]) * 0.5f - (_ws[23] + _ws[24]) * 0.5f;
        Vector3 right = _ws[12] - _ws[11];
        Vector3 fwd = flattenZ ? Vector3.forward : Vector3.Cross(right, up);
        bool torsoVisible = _vis[11] >= minVisibility && _vis[12] >= minVisibility
                            && _vis[23] >= minVisibility && _vis[24] >= minVisibility;
        if (torsoVisible && up.sqrMagnitude > 1e-6f && fwd.sqrMagnitude > 1e-6f)
        {
            Quaternion target = _root.rotation * Quaternion.LookRotation(fwd, up) * _hipsRestRot;
            _hipsCurrent = Quaternion.Slerp(_hipsCurrent, target, k);
            _hips.rotation = _hipsCurrent;
        }

        // 頭と首：肩の中心→鼻（0）の向きだけで決める。
        // 旧方式の「耳の線の垂線で傾ける」は、耳ランドマーク（7,8）のブレで頭が跳ねる原因だったので廃止。
        // 回転量には上限を付け、首の骨にも回転の一部（45%）を配分して、1か所で折れ曲がらないようにする。
        if (_head != null)
        {
            Vector3 shoulderMid = (_ws[11] + _ws[12]) * 0.5f;
            Vector3 headDir = _ws[0] - shoulderMid;
            bool headOk = _vis[0] >= 0.4f
                          && headDir.magnitude > 0.3f * up.magnitude
                          && headDir.sqrMagnitude > 1e-8f;
            if (headOk)
            {
                Vector3 headUp = headDir.normalized;
                Vector3 headFwd = flattenZ ? Vector3.forward : Vector3.Cross(_ws[8] - _ws[7], headUp);
                if (headFwd.sqrMagnitude < 1e-6f) headFwd = Vector3.forward;
                Quaternion delta = Quaternion.LookRotation(headFwd.normalized, headUp);
                float ang = Quaternion.Angle(Quaternion.identity, delta);
                if (ang > maxHeadAngle) delta = Quaternion.Slerp(Quaternion.identity, delta, maxHeadAngle / ang);
                float kh = 1f - Mathf.Exp(-smoothing * 0.5f * Time.deltaTime);   // 頭はゆっくり追わせる
                Quaternion headTarget = _root.rotation * delta * _headRestRot;
                _headCurrent = Quaternion.Slerp(_headCurrent, headTarget, kh);
                _head.rotation = _headCurrent;
                if (_neck != null)
                {
                    Quaternion neckTarget = _root.rotation * Quaternion.Slerp(Quaternion.identity, delta, 0.45f) * _neckRestRot;
                    _neckCurrent = Quaternion.Slerp(_neckCurrent, neckTarget, kh);
                    _neck.rotation = _neckCurrent;
                }
            }
        }

        // 腕・脚・手
        float torso2D = up.magnitude;
        foreach (var p in _parts)
        {
            // 信頼できない点（隠れている等）では回さない。前回の回転を保つ
            if (_vis[p.a] < minVisibility || _vis[p.b] < minVisibility) continue;
            Vector3 dir = _ws[p.b] - _ws[p.a];
            // 奥行き方向を向いて、ほぼ点に見える骨は更新しない(向きが不安定になるため)
            if (dir.magnitude < 0.08f * torso2D || dir.sqrMagnitude < 1e-8f) continue;
            Quaternion target = _root.rotation *
                (Quaternion.FromToRotation(p.restDir, dir.normalized) * p.restRot);
            float kp = p.distal ? 1f - Mathf.Exp(-smoothing * distalSmoothingScale * Time.deltaTime) : k;
            p.current = Quaternion.Slerp(p.current, target, kp);
            p.bone.rotation = p.current;
        }

        // ※ルートの回転には触らない。位置は上の「位置の追従」で X,Y だけ動かす。
    }
}
