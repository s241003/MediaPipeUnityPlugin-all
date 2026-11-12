using UnityEngine;

// 壁の種類の定義。Project右クリック > Create > Game > Wall Data で作る
[CreateAssetMenu(menuName = "Game/Wall Data", fileName = "WallData")]
public class WallData : ScriptableObject
{
    public string displayName = "Wall";
    public GameObject prefab;                       // 穴の開いた壁(FBXからPrefabにしたもの)
    public float speed = 3f;                        // 前進速度 (m/s)
    public float uniformScale = 1f;
    public Vector3 rotationOffset = Vector3.zero;   // モデルの向き補正
    public Vector3 positionOffset = Vector3.zero;   // 出現位置の微調整(穴の位置合わせ用。x,yを使う)
    public float weight = 1f;                       // ランダム選択時の重み
}