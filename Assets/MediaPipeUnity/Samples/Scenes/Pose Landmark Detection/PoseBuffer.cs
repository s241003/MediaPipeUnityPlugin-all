using UnityEngine;

// MediaPipe のコールバック（別スレッド）から33点を受け取り、メインスレッドへ渡す。
// 刷新点：書き込み時刻を持つ。更新が止まったら「ポーズ喪失」として扱えるようにする。
// （旧版は一度データが来たら二度と無効にならず、人が画角から外れても最後のポーズで固まった）
public static class PoseBuffer
{
    static readonly object _lock = new object();
    static readonly Vector3[] _latest = new Vector3[33];
    // 単調時計（Environment.TickCount64 は Unity の .NET プロファイルに無いため Stopwatch を使う）
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static long _lastWriteMs = -1;

    public static void Write(int i, float x, float y, float z)
    {
        lock (_lock)
        {
            _latest[i] = new Vector3(x, y, z);
            _lastWriteMs = _clock.ElapsedMilliseconds;
        }
    }

    // データがあれば時刻を問わずコピーする（後方互換用）
    public static bool TryRead(Vector3[] dst)
    {
        lock (_lock)
        {
            if (_lastWriteMs < 0) return false;
            System.Array.Copy(_latest, dst, 33);
            return true;
        }
    }

    // maxAgeSeconds 以内に書かれたデータだけを読む。古ければポーズ喪失として false
    public static bool TryReadFresh(Vector3[] dst, float maxAgeSeconds)
    {
        lock (_lock)
        {
            if (_lastWriteMs < 0) return false;
            if ((_clock.ElapsedMilliseconds - _lastWriteMs) / 1000f > maxAgeSeconds) return false;
            System.Array.Copy(_latest, dst, 33);
            return true;
        }
    }

    // いまポーズが生きているか（既定の鮮度 0.5 秒。提案値：実測で調整すること）
    public static bool HasFreshData => SecondsSinceLastWrite <= 0.5f;

    public static float SecondsSinceLastWrite
    {
        get
        {
            lock (_lock)
            {
                if (_lastWriteMs < 0) return float.MaxValue;
                return (_clock.ElapsedMilliseconds - _lastWriteMs) / 1000f;
            }
        }
    }
}
