using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace NpcifyDemo
{
    /// <summary>
    /// RPG-style screen UI, built in code: NPC name labels, the interaction prompt,
    /// and the dialogue box with typewriter text, reply choices and a free-text reply.
    /// </summary>
    public sealed class DialogueView : MonoBehaviour
    {
        private static readonly Color PanelColor = new Color(0.06f, 0.07f, 0.1f, 0.88f);
        private static readonly Color AccentColor = new Color(0.98f, 0.78f, 0.36f, 1f);
        private static readonly Color ChoiceColor = new Color(0.1f, 0.12f, 0.17f, 0.92f);
        private static readonly Color ChoiceHoverColor = new Color(0.2f, 0.24f, 0.33f, 0.96f);
        private static readonly Color MutedTextColor = new Color(0.78f, 0.8f, 0.86f, 1f);

        private readonly List<Button> _choiceButtons = new List<Button>();
        private readonly Dictionary<NpcifyNpc, Text> _labels = new Dictionary<NpcifyNpc, Text>();

        private Font _font;
        private RectTransform _canvasRect;
        private Camera _camera;

        private RectTransform _prompt;
        private Text _promptText;
        private Button _promptButton;

        private GameObject _dialogueRoot;
        private Text _nameText;
        private Text _modeText;
        private Text _bodyText;
        private Text _hintText;
        private RectTransform _choiceList;
        private GameObject _replyRow;
        private InputField _replyField;

        public event Action PromptClicked;
        public event Action<int> ChoiceClicked;
        public event Action<string> CustomReplySubmitted;
        public event Action BodyClicked;
        public event Action CloseClicked;

        public bool IsReplyFieldFocused => _replyField != null && _replyField.isFocused;

        private void Awake()
        {
            _font = Font.CreateDynamicFontFromOSFont(new[]
            {
                "Hiragino Sans", "Hiragino Kaku Gothic ProN", "PingFang SC", "Yu Gothic UI", "Meiryo",
                "Microsoft YaHei", "Noto Sans CJK JP", "Noto Sans JP", "Arial Unicode MS", "Arial",
            }, 32);
            BuildCanvas();
            BuildPrompt();
            BuildDialogue();
            HidePrompt();
            HideDialogue();
        }

        public void RegisterNpc(NpcifyNpc npc)
        {
            if (_labels.ContainsKey(npc))
            {
                return;
            }
            var label = CreateText("Label " + npc.name, _canvasRect, 26, TextAnchor.LowerCenter, Color.white);
            label.rectTransform.sizeDelta = new Vector2(520f, 80f);
            label.rectTransform.pivot = new Vector2(0.5f, 0f);
            label.raycastTarget = false;
            var outline = label.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            label.transform.SetAsFirstSibling();
            _labels[npc] = label;
        }

        public void UpdateNpcLabels(Vector3 viewerPosition, NpcifyNpc hidden)
        {
            foreach (var pair in _labels)
            {
                var npc = pair.Key;
                var label = pair.Value;
                var distance = Vector3.Distance(viewerPosition, npc.transform.position);
                var visible = npc != hidden && distance < 14f && TryPlace(label.rectTransform, npc.LabelPosition);
                label.enabled = visible;
                if (!visible)
                {
                    continue;
                }
                var status = npc.State == NpcLoadState.Ready ? npc.ModeLabel : npc.StatusMessage;
                label.text = $"<b>{npc.DisplayName}</b>\n<size=18><color=#{ColorUtility.ToHtmlStringRGB(AccentColor)}>{status}</color></size>";
            }
        }

        public void ShowPrompt(NpcifyNpc npc, string text, bool interactable)
        {
            _prompt.gameObject.SetActive(TryPlace(_prompt, npc.LabelPosition, 92f));
            _promptText.text = text;
            _promptButton.interactable = interactable;
        }

        public void HidePrompt()
        {
            _prompt.gameObject.SetActive(false);
        }

        public void ShowDialogue(string speaker, string mode, bool allowCustomReply)
        {
            _dialogueRoot.SetActive(true);
            _nameText.text = speaker;
            _modeText.text = mode;
            _replyRow.SetActive(allowCustomReply);
            _replyField.text = string.Empty;
            SetBody(string.Empty);
            SetChoices(Array.Empty<string>());
        }

        public void HideDialogue()
        {
            _dialogueRoot.SetActive(false);
        }

        public void SetBody(string text)
        {
            _bodyText.text = text;
        }

        public void SetHint(string text)
        {
            _hintText.text = text;
        }

        public void SetReplyInteractable(bool interactable)
        {
            _replyField.interactable = interactable;
        }

        public void SetChoices(IReadOnlyList<string> labels)
        {
            for (var i = 0; i < _choiceButtons.Count; i++)
            {
                Destroy(_choiceButtons[i].gameObject);
            }
            _choiceButtons.Clear();

            for (var i = 0; i < labels.Count; i++)
            {
                var index = i;
                var button = CreateButton(_choiceList, $"{i + 1}.  {labels[i]}", 26, ChoiceColor, TextAnchor.MiddleLeft);
                var layout = button.gameObject.AddComponent<LayoutElement>();
                layout.minHeight = 58f;
                button.onClick.AddListener(() => ChoiceClicked?.Invoke(index));
                _choiceButtons.Add(button);
            }
        }

        public void FocusReplyField()
        {
            if (_replyRow.activeSelf)
            {
                _replyField.Select();
                _replyField.ActivateInputField();
            }
        }

        private bool TryPlace(RectTransform rect, Vector3 worldPosition, float yOffset = 0f)
        {
            if (_camera == null || !_camera.isActiveAndEnabled)
            {
                _camera = Camera.main;
            }
            if (_camera == null)
            {
                return false;
            }
            var screen = _camera.WorldToScreenPoint(worldPosition);
            if (screen.z <= 0f)
            {
                return false;
            }
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out var local);
            rect.anchoredPosition = local + new Vector2(0f, yOffset);
            return true;
        }

        private void BuildCanvas()
        {
            var canvasObject = new GameObject("Npcify Dialogue Canvas", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();
            _canvasRect = (RectTransform)canvasObject.transform;

            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                eventSystem.transform.SetParent(transform, false);
            }
        }

        private void BuildPrompt()
        {
            _promptButton = CreateButton(_canvasRect, string.Empty, 26, new Color(0.05f, 0.06f, 0.08f, 0.82f), TextAnchor.MiddleCenter);
            _prompt = (RectTransform)_promptButton.transform;
            _prompt.anchorMin = _prompt.anchorMax = new Vector2(0.5f, 0.5f);
            _prompt.pivot = new Vector2(0.5f, 0f);
            _prompt.sizeDelta = new Vector2(380f, 56f);
            _promptText = _promptButton.GetComponentInChildren<Text>();
            _promptButton.onClick.AddListener(() => PromptClicked?.Invoke());
        }

        private void BuildDialogue()
        {
            _dialogueRoot = new GameObject("Dialogue", typeof(RectTransform));
            var root = (RectTransform)_dialogueRoot.transform;
            root.SetParent(_canvasRect, false);
            Stretch(root);

            var panel = CreateImage("Panel", root, PanelColor);
            panel.rectTransform.anchorMin = new Vector2(0.12f, 0.03f);
            panel.rectTransform.anchorMax = new Vector2(0.88f, 0.3f);
            panel.rectTransform.offsetMin = panel.rectTransform.offsetMax = Vector2.zero;
            var panelButton = panel.gameObject.AddComponent<Button>();
            panelButton.transition = Selectable.Transition.None;
            panelButton.onClick.AddListener(() => BodyClicked?.Invoke());

            var accent = CreateImage("Accent", panel.rectTransform, AccentColor);
            accent.rectTransform.anchorMin = new Vector2(0f, 1f);
            accent.rectTransform.anchorMax = new Vector2(1f, 1f);
            accent.rectTransform.sizeDelta = new Vector2(0f, 3f);
            accent.rectTransform.anchoredPosition = Vector2.zero;
            accent.raycastTarget = false;

            var plate = CreateImage("Name Plate", panel.rectTransform, AccentColor);
            plate.rectTransform.anchorMin = plate.rectTransform.anchorMax = new Vector2(0f, 1f);
            plate.rectTransform.pivot = new Vector2(0f, 0f);
            plate.rectTransform.anchoredPosition = new Vector2(28f, 0f);
            plate.rectTransform.sizeDelta = new Vector2(360f, 52f);
            plate.raycastTarget = false;
            _nameText = CreateText("Name", plate.rectTransform, 30, TextAnchor.MiddleLeft, new Color(0.08f, 0.08f, 0.1f));
            _nameText.fontStyle = FontStyle.Bold;
            Stretch(_nameText.rectTransform, new Vector2(18f, 0f), new Vector2(-18f, 0f));

            _modeText = CreateText("Mode", panel.rectTransform, 20, TextAnchor.UpperRight, MutedTextColor);
            _modeText.rectTransform.anchorMin = new Vector2(0.5f, 1f);
            _modeText.rectTransform.anchorMax = new Vector2(1f, 1f);
            _modeText.rectTransform.pivot = new Vector2(1f, 1f);
            _modeText.rectTransform.anchoredPosition = new Vector2(-72f, -14f);
            _modeText.rectTransform.sizeDelta = new Vector2(0f, 32f);

            _bodyText = CreateText("Body", panel.rectTransform, 32, TextAnchor.UpperLeft, Color.white);
            _bodyText.lineSpacing = 1.15f;
            Stretch(_bodyText.rectTransform, new Vector2(40f, 84f), new Vector2(-40f, -46f));

            _hintText = CreateText("Hint", panel.rectTransform, 19, TextAnchor.LowerRight, MutedTextColor);
            Stretch(_hintText.rectTransform, new Vector2(40f, 14f), new Vector2(-24f, -260f));

            _replyRow = new GameObject("Reply Row", typeof(RectTransform));
            var replyRect = (RectTransform)_replyRow.transform;
            replyRect.SetParent(panel.rectTransform, false);
            replyRect.anchorMin = new Vector2(0f, 0f);
            replyRect.anchorMax = new Vector2(0.62f, 0f);
            replyRect.pivot = new Vector2(0f, 0f);
            replyRect.anchoredPosition = new Vector2(40f, 14f);
            replyRect.sizeDelta = new Vector2(0f, 52f);
            _replyField = CreateInputField(replyRect, "Type your own reply and press Enter");
            _replyField.onSubmit.AddListener(text =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    CustomReplySubmitted?.Invoke(text.Trim());
                }
            });

            var close = CreateButton(panel.rectTransform, "×", 36, new Color(0f, 0f, 0f, 0f), TextAnchor.MiddleCenter);
            var closeRect = (RectTransform)close.transform;
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 0f);
            closeRect.anchoredPosition = new Vector2(-6f, 4f);
            closeRect.sizeDelta = new Vector2(56f, 52f);
            close.onClick.AddListener(() => CloseClicked?.Invoke());

            _choiceList = new GameObject("Choices", typeof(RectTransform)).GetComponent<RectTransform>();
            _choiceList.SetParent(root, false);
            _choiceList.anchorMin = new Vector2(0.52f, 0.3f);
            _choiceList.anchorMax = new Vector2(0.88f, 0.3f);
            _choiceList.pivot = new Vector2(1f, 0f);
            _choiceList.anchoredPosition = new Vector2(0f, 64f);
            _choiceList.sizeDelta = new Vector2(0f, 0f);
            var layout = _choiceList.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.LowerRight;
            var fitter = _choiceList.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private Text CreateText(string name, RectTransform parent, int size, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = color;
            text.supportRichText = true;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static Image CreateImage(string name, RectTransform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private Button CreateButton(RectTransform parent, string label, int size, Color color, TextAnchor anchor)
        {
            // The image is white; the button tint gives the real colors.
            var image = CreateImage("Button", parent, Color.white);
            var button = image.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = color;
            colors.selectedColor = color;
            colors.highlightedColor = ChoiceHoverColor;
            colors.pressedColor = new Color(AccentColor.r * 0.55f, AccentColor.g * 0.55f, AccentColor.b * 0.55f, 0.96f);
            colors.disabledColor = new Color(color.r, color.g, color.b, color.a * 0.6f);
            button.colors = colors;
            // Start at the normal color; the tint otherwise fades in from white.
            image.canvasRenderer.SetColor(color);
            var text = CreateText("Label", image.rectTransform, size, anchor, Color.white);
            Stretch(text.rectTransform, new Vector2(22f, 6f), new Vector2(-22f, -6f));
            text.text = label;
            return button;
        }

        private InputField CreateInputField(RectTransform parent, string placeholder)
        {
            var background = CreateImage("Reply Field", parent, new Color(1f, 1f, 1f, 0.08f));
            Stretch(background.rectTransform);
            var field = background.gameObject.AddComponent<InputField>();
            var text = CreateText("Text", background.rectTransform, 24, TextAnchor.MiddleLeft, Color.white);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            Stretch(text.rectTransform, new Vector2(16f, 4f), new Vector2(-16f, -4f));
            var hint = CreateText("Placeholder", background.rectTransform, 24, TextAnchor.MiddleLeft, new Color(1f, 1f, 1f, 0.4f));
            hint.text = placeholder;
            hint.fontStyle = FontStyle.Italic;
            Stretch(hint.rectTransform, new Vector2(16f, 4f), new Vector2(-16f, -4f));
            field.textComponent = text;
            field.placeholder = hint;
            field.lineType = InputField.LineType.SingleLine;
            return field;
        }

        private static void Stretch(RectTransform rect, Vector2 offsetMin = default, Vector2 offsetMax = default)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }
}
