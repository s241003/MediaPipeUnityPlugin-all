using UnityEngine;

public static class PoseBuffer
{
    static readonly object _lock = new object();
    static readonly Vector3[] _latest = new Vector3[33];
    static bool _hasData;

    public static void Write(int i, float x, float y, float z)
    {
        lock (_lock) { _latest[i] = new Vector3(x, y, z); _hasData = true; }
    }

    public static bool TryRead(Vector3[] dst)
    {
        lock (_lock)
        {
            if (!_hasData) return false;
            System.Array.Copy(_latest, dst, 33);
            return true;
        }
    }
}