using System;
using System.Globalization;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ParkasaurusResurrection
{
    // Click a number that normally only changes with + / - buttons or a slider, and type a value.
    // - Shop cards (StoreItem, food): the daily order amount next to + and -.
    // - Every SliderUI: e.g. the scenery variant number in the build menu, price sliders, our own
    //   Resurrection tab. Not the time sliders.
    // The number gets an invisible TMP_InputField on top of it in the same font. While typing, the
    // original text is hidden and UICore.IsInputActive reports true, so the game's hotkeys (1-7 for
    // the build tabs, camera keys) don't react. Enter or clicking elsewhere applies, Esc cancels.
    // On a slider the number often sits on the handle: dragging it moves the slider as usual,
    // a click (press and release without dragging) starts typing.
    // The inputs are always made; the setting only switches them on or off.
    public static class NumberInputPatch
    {
        [HarmonyPatch(typeof(StoreItem), "Start")]
        public static class ShopCard
        {
            public static void Postfix(StoreItem __instance)
            {
                StoreItem card = __instance;
                if (!card.m_buyMany || card.m_quanityTxt == null || card.m_parent == null || Global.Get.m_controllerMode)
                    return;
                Attach(card.m_quanityTxt, null, () => card.m_quanity.ToString(), text =>
                {
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
                        return;
                    // Same range as the Daily Delivery list (StoreOrderUI.InputUpdate)
                    card.m_quanity = Mathf.Clamp(amount, 0, 998);
                    card.m_parent.UpdateDaily(card, card.m_quanity);
                    card.UpdateQuanity();
                });
            }
        }

        [HarmonyPatch(typeof(SliderUI), "Awake")]
        public static class Slider
        {
            public static void Postfix(SliderUI __instance)
            {
                SliderUI ui = __instance;
                if (ui.m_type == SliderType.Time || ui.m_sliderTag == null || ui.m_slider == null || Global.Get.m_controllerMode)
                    return;
                Attach(ui.m_sliderTag, ui.m_slider, () => Current(ui), text =>
                {
                    if (!TryParse(text, out float value))
                        return;
                    UnityEngine.UI.Slider s = ui.m_slider;
                    if (ui.m_type == SliderType.Price)
                        value *= 4f; // the price slider counts quarters
                    if (s.wholeNumbers)
                        value = Mathf.Round(value);
                    s.value = Mathf.Clamp(value, s.minValue, s.maxValue);
                });
            }

            private static string Current(SliderUI ui)
            {
                float v = ui.m_slider.value;
                if (ui.m_type == SliderType.Price)
                    return (v / 4f).ToString("0.00", CultureInfo.InvariantCulture);
                return ui.m_slider.wholeNumbers
                    ? Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture)
                    : v.ToString("0.0#", CultureInfo.InvariantCulture);
            }
        }

        // The game only knows its own (legacy) InputField when it decides whether you're typing
        [HarmonyPatch(typeof(UICore), nameof(UICore.IsInputActive))]
        public static class Typing
        {
            public static void Postfix(ref bool __result)
            {
                if (!__result)
                    __result = IsTyping();
            }
        }

        internal static bool IsTyping()
        {
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null)
                return false;
            var field = selected.GetComponent<TMP_InputField>();
            return field != null && field.isFocused;
        }

        private static bool TryParse(string text, out float value)
        {
            string clean = text.Replace("$", "").Replace(',', '.').Trim();
            return float.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private const string InputName = "ModNumberInput";

        // Switched in the options menu: show or hide all inputs made so far (including the ones in
        // closed windows)
        internal static void ApplyToAll()
        {
            bool on = ModConfig.TypeNumbers.Value;
            foreach (TMP_InputField field in UnityEngine.Object.FindObjectsOfType<TMP_InputField>(true))
            {
                if (field.name == InputName)
                    field.gameObject.SetActive(on);
            }
        }

        private static void Attach(TMP_Text display, UnityEngine.UI.Slider slider, Func<string> current, Action<string> apply)
        {
            var rect = display.rectTransform;
            // A copied UI row brings a copy of our input along, without its listeners
            Transform old = rect.parent.Find(InputName);
            if (old != null)
                UnityEngine.Object.DestroyImmediate(old.gameObject);
            var go = new GameObject(InputName, typeof(RectTransform));
            go.SetActive(false);
            var r = (RectTransform)go.transform;
            r.SetParent(rect.parent, false);
            r.SetSiblingIndex(rect.GetSiblingIndex() + 1);
            r.anchorMin = rect.anchorMin;
            r.anchorMax = rect.anchorMax;
            r.pivot = rect.pivot;
            r.anchoredPosition = rect.anchoredPosition;
            r.sizeDelta = rect.sizeDelta;
            r.localRotation = rect.localRotation;
            r.localScale = rect.localScale;
            // Invisible, but catches the click
            go.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0f);

            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            var areaRect = (RectTransform)area.transform;
            areaRect.SetParent(r, false);
            Stretch(areaRect);

            var textGo = new GameObject("Text", typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(areaRect, false);
            Stretch(textRect);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.font = display.font;
            text.fontSharedMaterial = display.fontSharedMaterial;
            text.fontSize = display.fontSize;
            text.fontStyle = display.fontStyle;
            text.color = display.color;
            text.alignment = display.alignment;
            text.enableWordWrapping = false;
            text.raycastTarget = false;

            var field = go.AddComponent<SliderNumberField>();
            field.Slider = slider;
            field.textViewport = areaRect;
            field.textComponent = text;
            field.pointSize = display.fontSize;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.contentType = TMP_InputField.ContentType.Standard;
            field.characterValidation = TMP_InputField.CharacterValidation.CustomValidator;
            field.inputValidator = ScriptableObject.CreateInstance<NumberValidator>();
            field.characterLimit = 8;
            field.caretColor = display.color;
            field.customCaretColor = true;
            field.selectionColor = new Color(display.color.r, display.color.g, display.color.b, 0.35f);
            field.transition = Selectable.Transition.None;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.text = "";

            field.onSelect.AddListener(_ =>
            {
                field.SetTextWithoutNotify(current());
                display.enabled = false;
            });
            field.onEndEdit.AddListener(value =>
            {
                if (!field.wasCanceled && value != current())
                    apply(value);
                field.SetTextWithoutNotify("");
                display.enabled = true;
            });
            go.SetActive(ModConfig.TypeNumbers.Value);
        }

        private static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
        }

        // An input field that passes a drag on to its slider and only starts typing on a click
        public class SliderNumberField : TMP_InputField
        {
            public UnityEngine.UI.Slider Slider;
            private bool m_dragging;

            private bool PassOn => Slider != null && !isFocused;

            public override void OnPointerDown(PointerEventData eventData)
            {
                m_dragging = false;
                if (!PassOn)
                    base.OnPointerDown(eventData);
            }

            public override void OnBeginDrag(PointerEventData eventData)
            {
                if (!PassOn)
                {
                    base.OnBeginDrag(eventData);
                    return;
                }
                m_dragging = true;
                // As if the press had been on the slider: it keeps the grip offset on its handle
                Slider.OnPointerDown(eventData);
            }

            public override void OnDrag(PointerEventData eventData)
            {
                if (m_dragging)
                    Slider.OnDrag(eventData);
                else
                    base.OnDrag(eventData);
            }

            public override void OnEndDrag(PointerEventData eventData)
            {
                if (m_dragging)
                    Slider.OnPointerUp(eventData);
                else
                    base.OnEndDrag(eventData);
            }

            public override void OnPointerClick(PointerEventData eventData)
            {
                if (m_dragging)
                {
                    m_dragging = false;
                    return;
                }
                if (PassOn && eventData.button == PointerEventData.InputButton.Left)
                    EventSystem.current.SetSelectedGameObject(gameObject, eventData); // OnSelect starts typing
                else
                    base.OnPointerClick(eventData);
            }
        }

        // Digits, one decimal point (or comma), a leading minus
        public class NumberValidator : TMP_InputValidator
        {
            public override char Validate(ref string text, ref int pos, char ch)
            {
                bool ok = char.IsDigit(ch)
                    || ((ch == '.' || ch == ',') && text.IndexOf('.') < 0 && text.IndexOf(',') < 0)
                    || (ch == '-' && pos == 0 && !text.Contains("-"));
                if (!ok)
                    return '\0';
                text = text.Insert(pos, ch.ToString());
                pos++;
                return ch;
            }
        }
    }
}
