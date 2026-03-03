using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Creates a full-screen "Play" button overlay.
/// When tapped, the overlay disappears entirely.
/// Court placement happens via QR detection (PlaceTrackedImages → PlaceAtAnchor).
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
    [SerializeField] private int fontSize = 52;
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
        canvas.sortingOrder = 999; // on top of everything
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        // --- Semi-transparent background (blocks touches to anything behind) ---
        GameObject bgGO = new GameObject("Background");
        bgGO.transform.SetParent(canvasGO.transform, false);
        Image bgImage = bgGO.AddComponent<Image>();
        bgImage.color = new Color(0, 0, 0, 0.4f);
        bgImage.raycastTarget = true; // blocks touches on lower canvases
        RectTransform bgRect = bgGO.GetComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.sizeDelta = Vector2.zero;

        // Make the entire background also a button so tapping ANYWHERE dismisses the overlay
        Button bgBtn = bgGO.AddComponent<Button>();
        bgBtn.targetGraphic = bgImage;
        bgBtn.onClick.AddListener(OnPlayPressed);

        // --- Instruction text (upper area) ---
        GameObject instructGO = new GameObject("InstructionText");
        instructGO.transform.SetParent(canvasGO.transform, false);
        Text instructText = instructGO.AddComponent<Text>();
        instructText.text = "Point your camera at the Court QR code\nto place the court, then tap Play.";
        instructText.fontSize = 32;
        instructText.color = Color.white;
        instructText.alignment = TextAnchor.MiddleCenter;
        instructText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        instructText.horizontalOverflow = HorizontalWrapMode.Wrap;
        instructText.raycastTarget = false; // don't eat touches
        RectTransform instrRect = instructGO.GetComponent<RectTransform>();
        instrRect.anchorMin = new Vector2(0.05f, 0.60f);
        instrRect.anchorMax = new Vector2(0.95f, 0.80f);
        instrRect.sizeDelta = Vector2.zero;

        // --- Play Button (center of screen, large touch target) ---
        GameObject btnGO = new GameObject("PlayButton");
        btnGO.transform.SetParent(canvasGO.transform, false);
        Image btnImage = btnGO.AddComponent<Image>();
        btnImage.color = buttonColor;
        Button btn = btnGO.AddComponent<Button>();
        btn.targetGraphic = btnImage;
        btn.onClick.AddListener(OnPlayPressed);

        RectTransform btnRect = btnGO.GetComponent<RectTransform>();
        btnRect.anchorMin = new Vector2(0.15f, 0.38f);
        btnRect.anchorMax = new Vector2(0.85f, 0.52f);
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
        label.raycastTarget = false;
        RectTransform labelRect = labelGO.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.sizeDelta = Vector2.zero;
    }

    private void OnPlayPressed()
    {
        Debug.Log("[PlayButtonUI] Start overlay dismissed.");

        // Destroy the entire overlay canvas + this component
        if (canvasGO != null)
            Destroy(canvasGO);

        Destroy(this);
    }
}
