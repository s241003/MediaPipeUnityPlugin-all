using System.Collections.Generic;
using UnityEngine;

// ゲームの進行管理。
// 刷新点：
//  - キャリブレーション廃止。ポーズが生きていれば（カメラに映っていれば）すぐ開始できる。
//    ユーザーの位置はゲーム側では調整しない。アバターに合わせてユーザーが立つ。
//  - 評価は Perfect / Good / Bad。接触したときは部位名を出して、その部位を赤く点滅させる。
//  - 終了処理は「状態を先に変えてから」壁を消す（消去時の誤採点を防ぐ）。
public class GameManager : MonoBehaviour
{
    public enum GameState { WaitingPose, Countdown, Playing, GameOver }

    [Header("参照")]
    public AvatarRetarget retarget;
    public BodySegments2D body;
    public GameObject spawnerObject;

    [Header("ルール")]
    public float timeLimit = 60f;
    public int maxLives = 5;
    public int countdownSeconds = 3;

    [Header("スコア")]
    public int perfectPoints = 100;
    public int goodPoints = 50;
    public float comboBonus = 0.1f;
    public float maxMultiplier = 2f;

    GameState _state;
    float _timeLeft, _count;
    int _lives, _score, _combo;
    string _popup;
    Color _popupColor;
    float _popupUntil;
    readonly List<WallJudge2D.Result> _pending = new List<WallJudge2D.Result>();

    void OnEnable() { WallJudge2D.OnWallResolved += QueueResult; }
    void OnDisable() { WallJudge2D.OnWallResolved -= QueueResult; }
    void QueueResult(WallJudge2D.Result r) { _pending.Add(r); }

    void Start()
    {
        _lives = maxLives;
        SetSpawner(false);
        _state = GameState.WaitingPose;
    }

    void SetSpawner(bool on)
    {
        if (spawnerObject != null && spawnerObject.activeSelf != on)
            spawnerObject.SetActive(on);
    }

    void BeginCountdown()
    {
        _lives = maxLives;
        _score = 0;
        _combo = 0;
        _timeLeft = timeLimit;
        _count = countdownSeconds;
        _popup = null;
        _state = GameState.Countdown;
    }

    void StartPlay()
    {
        WallJudge2D.DestroyAll(silent: true);
        _pending.Clear();
        SetSpawner(true);
        _state = GameState.Playing;
    }

    void EndGame()
    {
        _state = GameState.GameOver;          // 先に状態を変える
        SetSpawner(false);
        WallJudge2D.DestroyAll(silent: true); // 採点せずに消す
        _pending.Clear();
    }

    void Update()
    {
        // 壁の結果はここでまとめて処理する（イベントの発生タイミングに依存しない）
        if (_state == GameState.Playing && _pending.Count > 0)
        {
            var results = _pending.ToArray();
            _pending.Clear();
            foreach (var r in results) OnWall(r);
        }
        else _pending.Clear();

        switch (_state)
        {
            case GameState.WaitingPose:
                if (retarget != null && retarget.IsReady) BeginCountdown();
                break;

            case GameState.Countdown:
                _count -= Time.deltaTime;
                if (_count <= 0f) StartPlay();
                break;

            case GameState.Playing:
                _timeLeft -= Time.deltaTime;
                if (_timeLeft <= 0f || _lives <= 0) EndGame();
                break;

            case GameState.GameOver:
                break;
        }
    }

    void OnWall(WallJudge2D.Result r)
    {
        if (r.hit)
        {
            _lives--;
            _combo = 0;
            if (body != null) body.FlashParts(r.hitLabels);   // 接触部位を赤くする
            ShowPopup($"ぶつかりました：{r.parts}", new Color(1f, 0.3f, 0.3f));
        }
        else if (r.rating == WallJudge2D.Rating.Perfect)
        {
            int pts = Mathf.RoundToInt(perfectPoints * Multiplier());
            _score += pts;
            _combo++;
            ShowPopup($"PERFECT! +{pts}（充填率 {r.coverage * 100f:F0}%）", new Color(0.4f, 1f, 0.5f));
        }
        else if (r.rating == WallJudge2D.Rating.Good)
        {
            int pts = Mathf.RoundToInt(goodPoints * Multiplier());
            _score += pts;
            _combo++;
            ShowPopup($"GOOD +{pts}（充填率 {r.coverage * 100f:F0}%）", new Color(0.5f, 0.9f, 1f));
        }
        else   // 接触はないがポーズが合っていない
        {
            _combo = 0;
            ShowPopup($"BAD… ポーズが合いませんでした（充填率 {r.coverage * 100f:F0}%）", new Color(1f, 0.75f, 0.3f));
        }
        Debug.Log($"[Game] score={_score} lives={_lives} combo={_combo} ({r.wallName})");
    }

    float Multiplier() => Mathf.Min(maxMultiplier, 1f + _combo * comboBonus);

    void ShowPopup(string msg, Color color)
    {
        _popup = msg;
        _popupColor = color;
        _popupUntil = Time.time + 1.5f;
    }

    void OnGUI()
    {
        int w = Screen.width, fh = Mathf.Max(18, w / 45);

        GUIStyle Big(Color c) => new GUIStyle(GUI.skin.label)
        { fontSize = fh * 2, alignment = TextAnchor.MiddleCenter, normal = { textColor = c } };
        GUIStyle Mid(Color c) => new GUIStyle(GUI.skin.label)
        { fontSize = fh, alignment = TextAnchor.MiddleLeft, normal = { textColor = c } };

        // ハート（ライフ）
        float hs = fh * 1.4f;
        for (int i = 0; i < maxLives; i++)
            DrawHeart(new Rect(20 + i * (hs + 8), 16, hs, hs), i < _lives);

        GUI.Label(new Rect(w - 340, 10, 320, fh + 8), $"SCORE {_score}", Mid(Color.white));
        if (_state == GameState.Playing || _state == GameState.Countdown)
            GUI.Label(new Rect(w - 340, 14 + fh, 320, fh + 8), $"TIME {Mathf.Max(0f, _timeLeft):F0}", Mid(Color.white));
        if (_combo >= 2)
            GUI.Label(new Rect(w - 340, 18 + fh * 2, 320, fh + 8), $"COMBO x{_combo} (x{Multiplier():F1})", Mid(new Color(1f, 0.85f, 0.3f)));

        if (_state == GameState.WaitingPose)
            GUI.Label(new Rect(0, Screen.height * 0.4f, w, fh * 3),
                "カメラの前に立ってください\n（アバターの位置に合わせて立とう）", Big(Color.white));
        else if (_state == GameState.Countdown)
            GUI.Label(new Rect(0, Screen.height * 0.4f, w, fh * 3),
                Mathf.CeilToInt(Mathf.Max(0f, _count)).ToString(), Big(Color.white));
        else if (_state == GameState.GameOver)
        {
            GUI.Label(new Rect(0, Screen.height * 0.35f, w, fh * 3), "GAME OVER", Big(new Color(1f, 0.35f, 0.35f)));
            GUI.Label(new Rect(0, Screen.height * 0.35f + fh * 3, w, fh * 2), $"SCORE {_score}", Big(Color.white));
        }

        // ポーズ喪失の注意（プレイ中のみ）
        if (_state == GameState.Playing && retarget != null && !retarget.IsReady)
            GUI.Label(new Rect(0, Screen.height * 0.12f, w, fh * 2),
                "カメラに映っていません", Big(new Color(1f, 0.6f, 0.2f)));

        // 壁ごとの結果ポップアップ
        if (_popup != null && Time.time < _popupUntil)
            GUI.Label(new Rect(0, Screen.height * 0.68f, w, fh * 2), _popup, Big(_popupColor));
    }

    static Texture2D _heartTex;
    void DrawHeart(Rect r, bool filled)
    {
        if (_heartTex == null) _heartTex = MakeHeartTexture();
        var prev = GUI.color;
        GUI.color = filled ? new Color(1f, 0.25f, 0.45f) : new Color(0.35f, 0.35f, 0.35f, 0.6f);
        GUI.DrawTexture(r, _heartTex);
        GUI.color = prev;
    }

    static Texture2D MakeHeartTexture()
    {
        const int N = 64;
        var tex = new Texture2D(N, N, TextureFormat.ARGB32, false);
        var px = new Color[N * N];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float u = (x / (float)(N - 1)) * 2f - 1f;
            float v = (y / (float)(N - 1)) * 2f - 1f;
            float a = u * u + v * v - 1f;
            bool inside = a * a * a + u * u * v * v * v < 0f;
            px[y * N + x] = inside ? Color.white : Color.clear;
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }
}
