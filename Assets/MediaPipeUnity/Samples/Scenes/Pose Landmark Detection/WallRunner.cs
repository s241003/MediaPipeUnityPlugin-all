using UnityEngine;

// 壁をz方向に動かすだけ。判定・採点は WallJudge が行う。WallSpawner が自動で付ける(手動で付けない)
public class WallRunner : MonoBehaviour
{
    public bool PassedPlane { get; private set; }   // 壁全体がプレイヤーの平面を通り過ぎた

    Vector3 _dir;
    float _speed, _despawnDistance, _fallbackPlaneZ, _dirSign;
    Renderer[] _rends;
    bool _ready;

    public void Init(float dirSign, float speed, float despawnDistance, float fallbackPlaneZ)
    {
        _dirSign = dirSign;
        _dir = new Vector3(0f, 0f, dirSign);
        _speed = speed;
        _despawnDistance = despawnDistance;
        _fallbackPlaneZ = fallbackPlaneZ;
        _rends = GetComponentsInChildren<Renderer>();
        _ready = true;
    }

    void Update()
    {
        if (!_ready) return;
        transform.position += _dir * _speed * Time.deltaTime;

        float plane = BodyColliders.Instance != null ? BodyColliders.Instance.PlaneZ : _fallbackPlaneZ;
        float minZ = transform.position.z, maxZ = minZ;
        if (_rends.Length > 0)
        {
            Bounds b = _rends[0].bounds;
            for (int i = 1; i < _rends.Length; i++) b.Encapsulate(_rends[i].bounds);
            minZ = b.min.z; maxZ = b.max.z;
        }

        // 壁の背面が、平面をどれだけ越えたか
        float passed = _dirSign > 0 ? minZ - plane : plane - maxZ;
        if (passed > 0f) PassedPlane = true;
        if (passed > _despawnDistance) Destroy(gameObject);
    }
}