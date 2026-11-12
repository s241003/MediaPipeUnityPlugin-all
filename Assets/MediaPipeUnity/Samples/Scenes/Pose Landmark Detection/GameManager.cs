using UnityEngine;

public class GameManager : MonoBehaviour
{
    public enum State { WaitingCalibration, Countdown, Playing, GameOver }

    [Header("参照")]
    public AvatarRetarget retarget;
    public GameObject spawnerObject;   // Wall_Spawner を付けたオブジェクト

    [Header("ルール")]
    public float timeLimit = 60f;
    public int maxLives = 5;
    public float countdownSeconds = 3f;

    [Header("得点")]
    public int greatPoints = 100;
    public int goodPoints = 50;
    public float comboBonus = 0.1f;    // コンボ1回ごとに、倍率へ足す量
    public float maxMultiplier = 2f;

    [Header("状態(確認用)")]
    public State state = State.WaitingCalibration;
    public int lives, score, combo;

    float _remaining, _countdownEnd, _popupUntil;
    string _popup = "", _reason = "";
    Color _popupColor = Color.white;
    GUIStyle _big, _left, _right;
    Texture2D _heart;
    int _lostIndex = -1;      // 直前に失ったハートの番号(消える演出用)
    float _lostAt;

    void OnEnable() { WallJudge.OnWallResolved += OnWall; }
    void OnDisable() { WallJudge.OnWallResolved -= OnWall; }

    void Start()
    {
        _remaining = timeLimit;
        lives = maxLives;
        if (spawnerObject != null) spawnerObject.SetActive(false);   // 開始まで、壁を出さない
    }

    void Update()
    {
        switch (state)
        {
            case State.WaitingCalibration:
                if (retarget != null && retarget.IsCalibrated)
                {
                    state = State.Countdown;
                    _countdownEnd = Time.time + countdownSeconds;
                }
                break;

            case State.Countdown:
                if (Time.time >= _countdownEnd) StartGame();
                break;

            case State.Playing:
                _remaining -= Time.deltaTime;
                if (_remaining <= 0f) { _remaining = 0f; EndGame("TIME UP"); }
                break;
        }
    }

    void StartGame()
    {
        score = 0;
        combo = 0;
        lives = maxLives;
        _remaining = timeLimit;
        WallJudge.DestroyAll();
        if (spawnerObject != null) spawnerObject.SetActive(true);
        _lostIndex = -1;
        state = State.Playing;
    }

    void EndGame(string reason)
    {
        if (spawnerObject != null) spawnerObject.SetActive(false);
        WallJudge.DestroyAll();
        _reason = reason;
        state = State.GameOver;
    }

    void Restart()
    {
        state = State.Countdown;
        _countdownEnd = Time.time + countdownSeconds;
    }

    void OnWall(WallJudge.Result r)
    {
        if (state != State.Playing) return;

        int basePts = r.rating == WallJudge.Rating.Great ? greatPoints
                    : r.rating == WallJudge.Rating.Good ? goodPoints : 0;
        float mult = Mathf.Min(1f + comboBonus * combo, maxMultiplier);
        int gained = Mathf.RoundToInt(basePts * mult);
        score += gained;

        if (r.hit) { lives--; combo = 0; _lostIndex = lives; _lostAt = Time.time; }                              // 1枚の壁につき、最大1回
        else if (r.rating == WallJudge.Rating.Bad) combo = 0;
        else combo++;

        float percent = r.coverage * 100f;
        _popup = r.hit ? $"HIT!  {r.parts}" : $"{r.rating.ToString().ToUpper()}  {percent:F0}%";
        _popupColor = r.hit || r.rating == WallJudge.Rating.Bad ? new Color(1f, 0.35f, 0.35f)
                    : r.rating == WallJudge.Rating.Good ? new Color(0.4f, 1f, 0.4f)
                    : new Color(1f, 0.85f, 0.2f);
        _popupUntil = Time.time + 1.2f;

        Debug.Log($"[Game] {r.wallName} / {r.rating} / 充填率 {percent:F0}% / 当たり={r.hit} / +{gained} / ライフ {lives}");

        if (lives <= 0) EndGame("GAME OVER");
    }

    // ライフ:ハートを最大数ぶん並べ、残っているものは赤、失ったものは灰色
    void DrawLives(float H)
    {
        float size = H * 0.06f, gap = size * 0.2f, x0 = 20f, y0 = 10f + H * 0.06f;
        for (int i = 0; i < maxLives; i++)
        {
            bool alive = i < lives;
            Color c = alive ? new Color(1f, 0.2f, 0.3f) : new Color(0.3f, 0.3f, 0.3f, 0.6f);
            if (!alive && i == _lostIndex)   // 失った瞬間は白から灰色へ変わる
                c = Color.Lerp(Color.white, c, Mathf.Clamp01((Time.time - _lostAt) / 0.5f));
            GUI.color = c;
            GUI.DrawTexture(new Rect(x0 + i * (size + gap), y0, size, size), _heart, ScaleMode.ScaleToFit);
        }
        GUI.color = Color.white;
    }

    // ハート形の白いテクスチャを数式で作る((x²+y²-1)³ - x²y³ ≤ 0)。画像ファイルは不要
    static Texture2D MakeHeart(int n)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        var px = new Color32[n * n];
        const int S = 3;   // 1ピクセルを3x3に分けて、輪郭をなめらかにする
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int cnt = 0;
                for (int sy = 0; sy < S; sy++)
                    for (int sx = 0; sx < S; sx++)
                    {
                        float u = ((x + (sx + 0.5f) / S) / n * 2f - 1f) * 1.3f;
                        float v = ((y + (sy + 0.5f) / S) / n * 2f - 1f) * 1.3f + 0.12f;
                        float a = u * u + v * v - 1f;
                        if (a * a * a - u * u * v * v * v <= 0f) cnt++;
                    }
                px[y * n + x] = new Color32(255, 255, 255, (byte)(255 * cnt / (S * S)));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    void OnGUI()
    {
        if (_heart == null) _heart = MakeHeart(96);
        if (_big == null)
        {
            _big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _left = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontStyle = FontStyle.Bold };
            _right = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight, fontStyle = FontStyle.Bold };
        }
        float W = Screen.width, H = Screen.height;
        _big.fontSize = Mathf.RoundToInt(H * 0.09f);
        _left.fontSize = _right.fontSize = Mathf.RoundToInt(H * 0.045f);
        _big.normal.textColor = _left.normal.textColor = _right.normal.textColor = Color.white;

        int shown = state == State.Playing ? Mathf.CeilToInt(_remaining) : Mathf.CeilToInt(timeLimit);
        GUI.Label(new Rect(20, 10, W * 0.5f, H * 0.12f), $"TIME {shown}", _left);
        GUI.Label(new Rect(W * 0.5f - 20, 10, W * 0.5f, H * 0.12f), $"SCORE {score}\nCOMBO {combo}", _right);

        DrawLives(H);

        Rect center = new Rect(0, H * 0.28f, W, H * 0.25f);
        switch (state)
        {
            case State.WaitingCalibration:
                GUI.Label(center, "Stand upright\nand face the camera", _big);
                break;

            case State.Countdown:
            {
                int n = Mathf.CeilToInt(_countdownEnd - Time.time);
                GUI.Label(center, n > 0 ? n.ToString() : "GO!", _big);
                break;
            }

            case State.GameOver:
            {
                GUI.Label(center, $"{_reason}\nSCORE {score}", _big);
                bool clicked = GUI.Button(new Rect(W * 0.5f - 120, H * 0.60f, 240, 60), "RESTART (R)");
                Event e = Event.current;
                bool key = e.type == EventType.KeyDown && e.keyCode == KeyCode.R;
                if (clicked || key) Restart();
                break;
            }
        }

        if (Time.time < _popupUntil)
        {
            GUI.color = _popupColor;
            GUI.Label(new Rect(0, H * 0.58f, W, H * 0.15f), _popup, _big);
            GUI.color = Color.white;
        }
    }
}