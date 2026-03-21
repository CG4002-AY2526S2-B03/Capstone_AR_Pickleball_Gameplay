using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen-space overlay displaying score, set count, and rally state.
/// Attach to GameFlowManager alongside GameStateManager.
///
/// Layout (portrait phone):
///   ┌──────────────────────────────────────┐
///   │ Set 1 | Serve                        │  ← top-left: state
///   │ Player 0 - 0 Bot  Sets: 0-0         │  ← top-left: score
///   │                                      │
///   │            Net fault — Bot point      │  ← center: message (fades)
///   │                                      │
///   └──────────────────────────────────────┘
/// </summary>
public class ScoreboardUI : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Auto-found if left null.")]
    public GameStateManager gameState;

    [Header("Appearance")]
    public int fontSize = 36;
    public Color textColor = Color.white;
    public Color bgColor = new Color(0f, 0f, 0f, 0.5f);
    public Color messageColor = Color.yellow;

    private Text scoreText;
    private Text stateText;
    private Text messageText;
    private GameObject canvasGO;
    private float messageTimer;

    private void Start()
    {
        if (gameState == null)
            gameState = FindFirstObjectByType<GameStateManager>();

        CreateUI();

        if (gameState != null)
        {
            gameState.OnScoreChanged += UpdateScoreDisplay;
            gameState.OnStateChanged += UpdateStateDisplay;
            gameState.OnMessage += ShowMessage;
            UpdateScoreDisplay();
            UpdateStateDisplay(gameState.State);
        }
    }

    private void Update()
    {
        if (messageTimer > 0f)
        {
            messageTimer -= Time.deltaTime;
            if (messageTimer <= 0f && messageText != null)
                messageText.text = "";
        }
    }

    private void CreateUI()
    {
        canvasGO = new GameObject("ScoreboardCanvas");
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        // ── Score panel (top-left) ───────────────────────────────────────────
        var panelGO = new GameObject("ScorePanel");
        panelGO.transform.SetParent(canvasGO.transform, false);
        Image panelImg = panelGO.AddComponent<Image>();
        panelImg.color = bgColor;
        panelImg.raycastTarget = false;
        RectTransform panelRT = panelGO.GetComponent<RectTransform>();
        panelRT.anchorMin = new Vector2(0.02f, 0.88f);
        panelRT.anchorMax = new Vector2(0.55f, 0.98f);
        panelRT.sizeDelta = Vector2.zero;

        // State label (top row of panel)
        stateText = CreateLabel(panelGO.transform, "StateText",
            new Vector2(0.05f, 0.55f), new Vector2(0.95f, 1f),
            24, new Color(0.85f, 0.85f, 0.85f));

        // Score label (bottom row of panel)
        scoreText = CreateLabel(panelGO.transform, "ScoreText",
            new Vector2(0.05f, 0f), new Vector2(0.95f, 0.55f),
            fontSize, textColor);

        // ── Center message (point awarded, set won, etc.) ────────────────────
        messageText = CreateLabel(canvasGO.transform, "MessageText",
            new Vector2(0.05f, 0.45f), new Vector2(0.95f, 0.55f),
            48, messageColor);
        messageText.alignment = TextAnchor.MiddleCenter;
        messageText.fontStyle = FontStyle.Bold;
        messageText.text = "";
    }

    private Text CreateLabel(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, int size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Text txt = go.AddComponent<Text>();
        txt.fontSize = size;
        txt.color = color;
        txt.alignment = TextAnchor.MiddleLeft;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.raycastTarget = false;
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.sizeDelta = Vector2.zero;
        return txt;
    }

    private void UpdateScoreDisplay()
    {
        if (scoreText == null || gameState == null) return;
        scoreText.text = $"Player {gameState.PlayerScore} - {gameState.BotScore} Bot  |  Sets: {gameState.PlayerSets}-{gameState.BotSets}";
    }

    private void UpdateStateDisplay(GameStateManager.RallyState state)
    {
        if (stateText == null || gameState == null) return;
        stateText.text = state switch
        {
            GameStateManager.RallyState.WaitingToServe => $"Set {gameState.CurrentSet}  |  Serve",
            GameStateManager.RallyState.InPlay => "Rally",
            GameStateManager.RallyState.PointScored => "Point!",
            GameStateManager.RallyState.MatchOver => "Match Over",
            _ => ""
        };
    }

    private void ShowMessage(string msg)
    {
        if (messageText == null) return;
        messageText.text = msg;
        messageTimer = 2f;
    }

    private void OnDestroy()
    {
        if (gameState != null)
        {
            gameState.OnScoreChanged -= UpdateScoreDisplay;
            gameState.OnStateChanged -= UpdateStateDisplay;
            gameState.OnMessage -= ShowMessage;
        }
    }
}
