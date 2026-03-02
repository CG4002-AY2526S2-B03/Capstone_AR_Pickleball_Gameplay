using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Creates a full-screen "Play" button overlay.
/// When tapped, the court is placed on the detected AR plane
/// and the button disappears.
/// 
/// Setup: Attach to any GameObject. Drag ARPlaneGameSpacePlacer
/// into the "gamePlacer" field in the Inspector.
/// </summary>
public class PlayButtonUI : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag the XR Origin (AR Rig) that has ARPlaneGameSpacePlacer.")]
    [SerializeField] private ARPlaneGameSpacePlacer gamePlacer;

    [Header("Button Appearance")]
    [SerializeField] private string buttonText = "TAP TO PLAY";
    [SerializeField] private int fontSize = 48;
    [SerializeField] private Color buttonColor = new Color(0.1f, 0.7f, 0.3f, 0.9f);
    [SerializeField] private Color textColor = Color.white;

    private GameObject canvasGO;

    private void Start()
    {
        CreateUI();
    }

    private void CreateUI()
    {
        // --- Canvas (Screen Space - Overlay) ---
        canvasGO = new GameObject("PlayButtonCanvas");
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        canvasGO.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGO.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1080, 1920);
        canvasGO.AddComponent<GraphicRaycaster>();

        // --- Semi-transparent background hint ---
        GameObject bgGO = new GameObject("Background");
        bgGO.transform.SetParent(canvasGO.transform, false);
        Image bgImage = bgGO.AddComponent<Image>();
        bgImage.color = new Color(0, 0, 0, 0.3f);
        RectTransform bgRect = bgGO.GetComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.sizeDelta = Vector2.zero;

        // --- Instruction text (top) ---
        GameObject instructGO = new GameObject("InstructionText");
        instructGO.transform.SetParent(canvasGO.transform, false);
        Text instructText = instructGO.AddComponent<Text>();
        instructText.text = "Point your phone at the floor.\nWhen you see a plane, tap Play!";
        instructText.fontSize = 28;
        instructText.color = Color.white;
        instructText.alignment = TextAnchor.MiddleCenter;
        instructText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        instructText.horizontalOverflow = HorizontalWrapMode.Wrap;
        RectTransform instrRect = instructGO.GetComponent<RectTransform>();
        instrRect.anchorMin = new Vector2(0.1f, 0.7f);
        instrRect.anchorMax = new Vector2(0.9f, 0.85f);
        instrRect.sizeDelta = Vector2.zero;

        // --- Play Button (center-bottom) ---
        GameObject btnGO = new GameObject("PlayButton");
        btnGO.transform.SetParent(canvasGO.transform, false);
        Image btnImage = btnGO.AddComponent<Image>();
        btnImage.color = buttonColor;
        Button btn = btnGO.AddComponent<Button>();
        btn.targetGraphic = btnImage;

        RectTransform btnRect = btnGO.GetComponent<RectTransform>();
        btnRect.anchorMin = new Vector2(0.2f, 0.15f);
        btnRect.anchorMax = new Vector2(0.8f, 0.25f);
        btnRect.sizeDelta = Vector2.zero;

        // --- Button label ---
        GameObject labelGO = new GameObject("Label");
        labelGO.transform.SetParent(btnGO.transform, false);
        Text label = labelGO.AddComponent<Text>();
        label.text = buttonText;
        label.fontSize = fontSize;
        label.color = textColor;
        label.alignment = TextAnchor.MiddleCenter;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontStyle = FontStyle.Bold;
        RectTransform labelRect = labelGO.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.sizeDelta = Vector2.zero;

        // --- Wire button click ---
        btn.onClick.AddListener(OnPlayPressed);
    }

    private void OnPlayPressed()
    {
        if (gamePlacer != null)
        {
            gamePlacer.AllowPlacement();
        }
        else
        {
            // Fallback: try to find it in the scene
            var placer = FindFirstObjectByType<ARPlaneGameSpacePlacer>();
            if (placer != null)
            {
                placer.AllowPlacement();
            }
            else
            {
                Debug.LogError("[PlayButtonUI] No ARPlaneGameSpacePlacer found!");
            }
        }

        // Destroy the overlay
        Destroy(canvasGO);
        Destroy(this);
    }
}
