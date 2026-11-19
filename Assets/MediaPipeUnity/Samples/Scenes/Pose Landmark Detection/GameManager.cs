using System.Collections.Generic;
using UnityEngine;

// ゲームの進行管理。
// 刷新点：
//  - キャリブレーション廃止。ポーズが生きていれば（カメラに映っていれば）すぐ開始できる。
//    ユーザーの位置はゲーム側では調整しない。アバターに合わせてユーザーが立つ。
//  - 評価は Perfect / Good / Bad。接触したときは部位名を文字で出さず、その部位を赤く点滅させる。
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
        if (_heartTex == null) _heartTex = MakeHeartTexture();   // OnGUI の中で作ると Editor の Assertion が出ることがあるため、ここで作る
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
        if (retarget != null) retarget.Recenter();   // カウントダウン時点の立ち位置をホームにする
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
        _popup = null;                        // 終了時に BAD 等のポップアップを残さない
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
            ShowPopup("BAD", new Color(1f, 0.3f, 0.3f));   // 接触箇所は文字で出さず、部位の赤色表示だけで伝える
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
        int w = Screen.width;
        // フォント基準：幅と高さの両方から決める（横長・小型の画面でも文字がはみ出さないように）
        int fh = Mathf.Max(16, Mathf.Min(w / 45, Screen.height / 26));

        GUIStyle Big(Color c) => new GUIStyle(GUI.skin.label)
        { fontSize = fh * 2, alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = c } };
        GUIStyle MidR(Color c) => new GUIStyle(GUI.skin.label)
        { fontSize = fh, alignment = TextAnchor.MiddleRight, wordWrap = true, normal = { textColor = c } };
        GUIStyle Pop(Color c) => new GUIStyle(GUI.skin.label)
        { fontSize = Mathf.RoundToInt(fh * 1.6f), alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = c } };

        // ハート（ライフ）
        float hs = fh * 1.4f;
        for (int i = 0; i < maxLives; i++)
            DrawHeart(new Rect(20 + i * (hs + 8), 16, hs, hs), i < _lives);

        GUI.Label(new Rect(w - 380, 10, 360, fh + 8), $"SCORE {_score}", MidR(Color.white));
        if (_state == GameState.Playing || _state == GameState.Countdown)
            DrawTimer(w, fh);
        if (_combo >= 2)
            GUI.Label(new Rect(w - 380, 18 + fh, 360, fh + 8), $"COMBO x{_combo} (x{Multiplier():F1})", MidR(new Color(1f, 0.85f, 0.3f)));

        if (_state == GameState.WaitingPose)
            GUI.Label(new Rect(w * 0.05f, Screen.height * 0.38f, w * 0.9f, fh * 3.6f),
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

        // 壁ごとの結果ポップアップ（プレイ中だけ。GAME OVER 画面には出さない）
        // 幅に余裕を持たせ、フォントも少し小さくして長い文でも見切れないようにする
        if (_state == GameState.Playing && _popup != null && Time.time < _popupUntil)
            GUI.Label(new Rect(w * 0.05f, Screen.height * 0.66f, w * 0.9f, fh * 2.4f), _popup, Pop(_popupColor));
    }

    // 残り時間のタイマー（画面上部中央の円形ゲージ）。真上から時計回りに減り、色は 緑→黄→赤。真ん中に秒数
    void DrawTimer(int w, int fh)
    {
        float d = fh * 3.4f;
        var rect = new Rect((w - d) * 0.5f, 10f, d, d);
        GUI.DrawTexture(rect, TimerTexture());
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(fh * 1.25f), alignment = TextAnchor.MiddleCenter,
            normal = { textColor = (_state == GameState.Playing && _timeLeft <= 10f) ? new Color(1f, 0.45f, 0.45f) : Color.white }
        };
        GUI.Label(rect, $"{Mathf.Max(0f, _timeLeft):F0}", style);
    }

    static Texture2D _timerTex;
    static int _timerKey = int.MinValue;

    // 円形ゲージのテクスチャ。秒が変わったときだけ作り直す（毎フレーム作ると重いため）
    Texture2D TimerTexture()
    {
        float frac = timeLimit > 0f ? Mathf.Clamp01(_timeLeft / timeLimit) : 0f;
        int band = frac > 0.5f ? 0 : frac > 0.25f ? 1 : 2;
        int key = Mathf.CeilToInt(_timeLeft) * 4 + band;
        if (_timerTex != null && key == _timerKey) return _timerTex;
        _timerKey = key;

        const int N = 128;
        if (_timerTex == null)
        {
            _timerTex = new Texture2D(N, N, TextureFormat.ARGB32, false);
            _timerTex.hideFlags = HideFlags.HideAndDontSave;
        }
        Color bandColor = band == 0 ? new Color(0.35f, 0.9f, 0.45f, 0.95f)
                        : band == 1 ? new Color(1f, 0.85f, 0.3f, 0.95f)
                        : new Color(1f, 0.3f, 0.3f, 0.95f);
        var dark = new Color(0f, 0f, 0f, 0.35f);
        var px = new Color[N * N];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float u = (x / (float)(N - 1)) * 2f - 1f;
            float v = (y / (float)(N - 1)) * 2f - 1f;
            float r = Mathf.Sqrt(u * u + v * v);
            Color c = Color.clear;
            if (r <= 1f)
            {
                if (r < 0.62f) c = new Color(0f, 0f, 0f, 0.45f);   // 中央（秒数の下地）
                else
                {
                    float ang = Mathf.Atan2(u, v);
                    if (ang < 0f) ang += Mathf.PI * 2f;            // 真上を 0 として時計回り
                    c = ang <= frac * Mathf.PI * 2f ? bandColor : dark;
                }
            }
            px[y * N + x] = c;
        }
        _timerTex.SetPixels(px);
        _timerTex.Apply();
        return _timerTex;
    }

    static Texture2D _heartTex;
    void DrawHeart(Rect r, bool filled)
    {
        if (_heartTex == null) return;
        var prev = GUI.color;
        GUI.color = filled ? new Color(1f, 0.25f, 0.45f) : new Color(0.35f, 0.35f, 0.35f, 0.6f);
        GUI.DrawTexture(r, _heartTex);
        GUI.color = prev;
    }

    static Texture2D MakeHeartTexture()
    {
        // 図形の合成で描く：上の丸み＝2つの円、下の尖り＝三角形。
        // 陰関数のハート曲線は中央のくぼみが浅く上側が平坦になるため、この方式にした（くぼみがくっきり出る）。
        const int N = 64;
        var tex = new Texture2D(N, N, TextureFormat.ARGB32, false);
        var px = new Color[N * N];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float u = (x / (float)(N - 1)) * 2.3f - 1.15f;
            float v = (y / (float)(N - 1)) * 2.3f - 1.15f;
            px[y * N + x] = InHeart(u, v) ? Color.white : Color.clear;
        }
        tex.SetPixels(px);
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;   // 実行時生成のテクスチャは保存対象にしない
        return tex;
    }

    static bool InHeart(float u, float v)
    {
        const float cx = 0.46f, cy = 0.34f, r = 0.54f;
        float dx = u + cx; if (dx * dx + (v - cy) * (v - cy) <= r * r) return true;
        dx = u - cx; if (dx * dx + (v - cy) * (v - cy) <= r * r) return true;
        return InTri(u, v, -0.95f, 0.30f, 0.95f, 0.30f, 0f, -1.02f);
    }

    static bool InTri(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float S(float x1, float y1, float x2, float y2, float x3, float y3) => (x1 - x3) * (y2 - y3) - (x2 - x3) * (y1 - y3);
        float d1 = S(px, py, ax, ay, bx, by), d2 = S(px, py, bx, by, cx, cy), d3 = S(px, py, cx, cy, ax, ay);
        bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
        bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
        return !(neg && pos);
    }
}
