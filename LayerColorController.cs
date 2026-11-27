using System;
using System.Collections.Generic;
using DressUp.Actions;
using DressUp.Motion;
using UnityEngine;
using UnityEngine.UI;

namespace DressUp.Tinting
{
    /// <summary>
    /// 染色只跟「当前选中的那件衣服图层 Image」有关：点哪件改哪件。
    /// </summary>
    public class LayerColorController : MonoBehaviour
    {
        [SerializeField] Scrollbar hueScrollbar;
        [SerializeField] Scrollbar saturationScrollbar;
        [SerializeField] Scrollbar valueScrollbar;
        [SerializeField] float defaultSaturation = 0.55f;
        [SerializeField] float defaultValue = 1f;
        [SerializeField] UiFadeMotion fadeMotion;
        [SerializeField] RectTransform fadeRoot;
        [SerializeField] bool hideUntilSelection = true;
        [SerializeField] float fadeDuration = 0.28f;

        Image tintTarget;
        bool suppressCallback;
        bool subscribed;
        bool pendingApply;
        RectTransform startChip;
        RectTransform endChip;
        Image startSwatch;
        Image endSwatch;
        Image startRing;
        Image endRing;
        bool gradientChipsBound;
        HsvBar hueBar;
        HsvBar satBar;
        HsvBar valBar;
        float shownHue = -1f;
        float shownSat = -1f;
        float shownValue = -1f;

        sealed class HsvBar
        {
            public Image image;
            public Material material;
            public Texture2D gradient;
        }

        public event Action<Color, float> PlayerTintApplied;

        /// <summary>双色的 B。只通知当前选中的衣服，由衣柜按同时变色决定改哪些层。</summary>
        public event Action<Color> PlayerGradientEndApplied;

        /// <summary>明度滑条正中是原亮度。左半变暗，右半把变浅写进颜色本身。</summary>
        public const float OriginalValue = 0.5f;
        const float DarkGamma = 1.3f;
        const float LightGamma = 1f;
        const float SnapRadius = 0.04f;

        public UnityEngine.Color BarColor => BuildColor();

        /// <summary>把明度滑条收成一个颜色。右半是往白色里混，不再另存一个提亮值。</summary>
        public static UnityEngine.Color ResolveTint(float h, float s, float t)
        {
            t = Mathf.Clamp01(t);
            float v = t < 0.5f ? Mathf.Pow(t * 2f, DarkGamma) : 1f;
            float lift = t > 0.5f ? Mathf.Pow((t - 0.5f) * 2f, LightGamma) : 0f;
            UnityEngine.Color color = UnityEngine.Color.HSVToRGB(Mathf.Clamp01(h), Mathf.Clamp01(s), v);
            return UnityEngine.Color.Lerp(color, UnityEngine.Color.white, lift);
        }

        /// <summary>刚打开渐变：色点用当前衣服的颜色，图先保持原样。</summary>
        public void ShowOriginalGradient()
        {
            if (!TintStyle.TwoColor)
            {
                RefreshGradientChips();
                return;
            }

            TintStyle.HoldOriginal();
            if (tintTarget != null && HasVisibleColor(tintTarget.color))
                TintStyle.RememberOriginalColor(tintTarget.color);
            RefreshGradientChips();
        }

        static bool HasVisibleColor(UnityEngine.Color color)
        {
            return color.r <= 0.96f || color.g <= 0.96f || color.b <= 0.96f;
        }

        /// <summary>从已经写进颜色里的明度反推滑条。右半和饱和都会发白，只能回到左半或正中。</summary>
        public static float ValueSliderFrom(UnityEngine.Color color)
        {
            UnityEngine.Color.RGBToHSV(color, out _, out _, out float v);
            if (v >= 0.995f)
                return OriginalValue;
            return OriginalValue * Mathf.Pow(Mathf.Clamp01(v), 1f / DarkGamma);
        }

        public void CollectBars(List<Scrollbar> into)
        {
            AutoWireScrollbars();
            into.Clear();
            if (hueScrollbar != null)
                into.Add(hueScrollbar);
            if (saturationScrollbar != null)
                into.Add(saturationScrollbar);
            if (valueScrollbar != null)
                into.Add(valueScrollbar);
        }

        public static Vector3 HandleCenter(Scrollbar bar)
        {
            if (bar == null || bar.handleRect == null)
                return Vector3.zero;
            return bar.handleRect.TransformPoint(bar.handleRect.rect.center);
        }

        /// <summary>不改当前滑块，算出某个数值时 Handle 中心的世界坐标。</summary>
        public static Vector3 PredictHandleCenter(Scrollbar bar, float value)
        {
            if (bar == null || bar.handleRect == null)
                return Vector3.zero;

            float previous = bar.value;
            bar.SetValueWithoutNotify(Mathf.Clamp01(value));
            Canvas.ForceUpdateCanvases();
            Vector3 center = HandleCenter(bar);
            bar.SetValueWithoutNotify(previous);
            Canvas.ForceUpdateCanvases();
            return center;
        }

        void Awake()
        {
            AutoWireScrollbars();
            EnsureFade();
            if (hideUntilSelection)
                SnapHidden();
        }

        void OnEnable()
        {
            SubscribeScrollbars(true);
            TintStyle.Changed += OnTintStyleChanged;
            RefreshGradientChips();
            EnsureHsvStrips();
            RefreshHsvStrips(true);
        }

        void OnDisable()
        {
            SubscribeScrollbars(false);
            TintStyle.Changed -= OnTintStyleChanged;
        }

        void OnDestroy()
        {
            ReleaseBar(hueScrollbar, hueBar);
            ReleaseBar(saturationScrollbar, satBar);
            ReleaseBar(valueScrollbar, valBar);
        }

        void OnTintStyleChanged()
        {
            RefreshGradientChips();
            RefreshHsvStrips(true);
        }

        /// <summary>选中某件衣服的图层 Image；之后拖 BAR 只改这张图。</summary>
        public void SetTintTarget(Image layerImage, bool syncBars = true)
        {
            AutoWireScrollbars();
            SubscribeScrollbars(true);

            tintTarget = layerImage;
            if (tintTarget == null)
            {
                HidePanel();
                return;
            }

            // 先显示，避免 Sync 过程中被其它逻辑盖掉
            ShowPanel();

            if (syncBars)
            {
                if (TintStyle.TwoColor && TintStyle.EditingEnd)
                    ShowColorOnBars(TintStyle.GradientEnd);
                else
                {
                    SyncScrollbarsFromTarget();
                    // 锁定时点另一件衣服，不能把已经锁定的起色改成这件的颜色。
                    if (TintStyle.TwoColor && tintTarget != null
                        && (RandomSettingsPanel.Current == null || !RandomSettingsPanel.Current.LockGradientActive))
                    {
                        // 还停在「原色」时，只记下这件衣服的颜色，不能解除原色状态，
                        // 否则只是点选衣服，A 就被改成白色，渐变直接消失。
                        if (TintStyle.GradientAtOriginal)
                        {
                            if (HasVisibleColor(tintTarget.color))
                                TintStyle.RememberOriginalColor(tintTarget.color);
                        }
                        else
                        {
                            TintStyle.SetGradientStart(tintTarget.color);
                        }
                    }
                }
            }

            RefreshGradientChips();
        }

        /// <summary>
        /// 切换曲线/色相后，把选中衣服当前的颜色再走一遍和滑条相同的通道，
        /// 让这件衣服按新模式重画。没上色、或渐变还停在原色时不动。
        /// </summary>
        public void ReapplyToTarget()
        {
            if (tintTarget == null || TintStyle.GradientAtOriginal)
                return;
            if (!HasVisibleColor(tintTarget.color))
                return;
            SpriteBrighten.Set(tintTarget, 0f);
            PlayerTintApplied?.Invoke(tintTarget.color, 0f);
        }

        public void ClearTintTarget()
        {
            tintTarget = null;
            // 无选中衣服：立刻淡出，拖动也不再生效（ApplyCurrentColor 见 tintTarget==null）
            HidePanel();
        }

        public void ShowPanel()
        {
            EnsureFade();
            if (fadeRoot == null || fadeMotion == null)
                return;

            fadeRoot.SetAsLastSibling();
            if (!fadeRoot.gameObject.activeSelf)
                fadeRoot.gameObject.SetActive(true);
            fadeMotion.FadeIn();
            RefreshGradientChips();
        }

        public void HidePanel()
        {
            EnsureFade();
            fadeMotion?.FadeOut();
        }

        public void SnapHidden()
        {
            EnsureFade();
            fadeMotion?.SnapHidden();
        }

        void EnsureFade()
        {
            if (fadeRoot == null)
                fadeRoot = FindFadeRoot();
            if (fadeRoot == null)
                return;

            if (fadeMotion == null || fadeMotion.gameObject != fadeRoot.gameObject)
                fadeMotion = fadeRoot.GetComponent<UiFadeMotion>() ?? fadeRoot.gameObject.AddComponent<UiFadeMotion>();

            CanvasGroup group = fadeRoot.GetComponent<CanvasGroup>() ?? fadeRoot.gameObject.AddComponent<CanvasGroup>();
            fadeMotion.SetCanvasGroup(group);
            fadeMotion.Duration = fadeDuration;
        }

        static RectTransform FindFadeRoot()
        {
            RectTransform[] all = UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && string.Equals(all[i].name, "CHANGECOLOR", System.StringComparison.OrdinalIgnoreCase))
                    return all[i];
            }

            return null;
        }

        void AutoWireScrollbars()
        {
            if (hueScrollbar == null)
                hueScrollbar = FindBar("HueBar");
            if (saturationScrollbar == null)
                saturationScrollbar = FindBar("SatBar");
            if (valueScrollbar == null)
                valueScrollbar = FindBar("ValBar");
        }

        Scrollbar FindBar(string childName)
        {
            Transform t = transform.Find(childName);
            return t != null ? t.GetComponent<Scrollbar>() : null;
        }

        void SubscribeScrollbars(bool subscribe)
        {
            if (subscribe && subscribed)
                return;
            if (!subscribe && !subscribed)
                return;

            Bind(hueScrollbar, OnBar, subscribe);
            Bind(saturationScrollbar, OnBar, subscribe);
            Bind(valueScrollbar, OnBar, subscribe);
            subscribed = subscribe;
        }

        static void Bind(Scrollbar bar, UnityEngine.Events.UnityAction<float> handler, bool subscribe)
        {
            if (bar == null)
                return;
            if (subscribe)
                bar.onValueChanged.AddListener(handler);
            else
                bar.onValueChanged.RemoveListener(handler);
        }

        void OnBar(float _)
        {
            // 同一帧里 Scrollbar 可能连发多次；合并到 LateUpdate 只刷一次全身。
            if (!suppressCallback)
                pendingApply = true;
        }

        void LateUpdate()
        {
            if (!pendingApply)
                return;
            pendingApply = false;
            SnapToValue(saturationScrollbar, 0f);
            SnapToPop(valueScrollbar);
            RefreshHsvStrips(false);
            if (tintTarget != null)
                ApplyCurrentColor();
        }

        /// <summary>饱和度的原图点在最左边。明度仍吸到 POP 的中间。</summary>
        bool SnapToValue(Scrollbar bar, float target)
        {
            if (bar == null || target < 0f)
                return false;
            float gap = Mathf.Abs(bar.value - target);
            if (gap > SnapRadius)
                return false;
            if (gap > 0.0005f)
                bar.SetValueWithoutNotify(target);
            return true;
        }

        bool SnapToPop(Scrollbar bar)
        {
            return SnapToValue(bar, PopSliderValue(bar));
        }

        static float PopSliderValue(Scrollbar bar)
        {
            RectTransform pop = FindPop(bar);
            RectTransform area = bar.handleRect != null ? bar.handleRect.parent as RectTransform : null;
            if (pop == null || area == null)
                return -1f;

            Image icon = pop.GetComponent<Image>();
            if (icon != null)
                icon.raycastTarget = false;

            Rect track = area.rect;
            if (track.width < 1f && track.height < 1f)
                return 0.5f;

            Vector3 local = area.InverseTransformPoint(pop.TransformPoint(pop.rect.center));
            bool vertical = bar.direction == Scrollbar.Direction.BottomToTop
                || bar.direction == Scrollbar.Direction.TopToBottom;
            float along = vertical
                ? Mathf.InverseLerp(track.yMin, track.yMax, local.y)
                : Mathf.InverseLerp(track.xMin, track.xMax, local.x);
            if (bar.direction == Scrollbar.Direction.TopToBottom
                || bar.direction == Scrollbar.Direction.RightToLeft)
                along = 1f - along;

            float size = Mathf.Clamp(bar.size, 0f, 0.9f);
            return Mathf.Clamp01((along - size * 0.5f) / Mathf.Max(0.0001f, 1f - size));
        }

        static RectTransform FindPop(Scrollbar bar)
        {
            RectTransform[] rects = bar.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < rects.Length; i++)
            {
                if (rects[i] != null && rects[i].name.Equals("Pop", System.StringComparison.OrdinalIgnoreCase))
                    return rects[i];
            }

            return null;
        }

        void ApplyCurrentColor()
        {
            if (suppressCallback || tintTarget == null)
                return;

            ReadBars(out float h, out float s, out float t);
            UnityEngine.Color color = ResolveTint(h, s, t);
            if (TintStyle.TwoColor && TintStyle.EditingEnd)
            {
                TintStyle.SetGradientEnd(color);
                PlayerGradientEndApplied?.Invoke(color);
                RefreshGradientChips();
                return;
            }

            if (TintStyle.TwoColor)
            {
                TintStyle.SetGradientStart(color);
                RefreshGradientChips();
            }

            color.a = tintTarget.color.a;
            tintTarget.color = color;
            SpriteBrighten.Set(tintTarget, 0f);
            PlayerTintApplied?.Invoke(tintTarget.color, 0f);
        }

        void ReadBars(out float h, out float s, out float t)
        {
            h = hueScrollbar != null ? hueScrollbar.value : 0f;
            s = saturationScrollbar != null ? saturationScrollbar.value : defaultSaturation;
            t = valueScrollbar != null ? valueScrollbar.value : OriginalValue;
        }

        UnityEngine.Color BuildColor()
        {
            ReadBars(out float h, out float s, out float t);
            return ResolveTint(h, s, t);
        }

        void SyncScrollbarsFromTarget()
        {
            if (tintTarget == null)
                return;

            UnityEngine.Color.RGBToHSV(tintTarget.color, out float h, out float s, out float v);
            if (s < 0.08f)
                s = defaultSaturation;
            if (v < 0.05f)
                v = defaultValue;

            suppressCallback = true;
            // 同步期间先卸监听，避免个别版本 SetValueWithoutNotify 仍回调
            bool wasSubscribed = subscribed;
            if (wasSubscribed)
                SubscribeScrollbars(false);

            if (hueScrollbar != null)
                hueScrollbar.SetValueWithoutNotify(h);
            if (saturationScrollbar != null)
                saturationScrollbar.SetValueWithoutNotify(s);
            if (valueScrollbar != null)
                valueScrollbar.SetValueWithoutNotify(ValueSliderFrom(tintTarget.color));

            if (wasSubscribed)
                SubscribeScrollbars(true);
            suppressCallback = false;
            RefreshHsvStrips(true);
        }

        /// <summary>把三根 BAR 设成指定颜色，并写到当前选中的衣服上。</summary>
        public void SetBarColor(UnityEngine.Color color)
        {
            AutoWireScrollbars();
            ShowColorOnBars(color);

            if (TintStyle.TwoColor && TintStyle.EditingEnd)
            {
                TintStyle.SetGradientEnd(color);
                PlayerGradientEndApplied?.Invoke(color);
                return;
            }

            if (TintStyle.TwoColor)
                TintStyle.SetGradientStart(color);

            if (tintTarget != null)
            {
                UnityEngine.Color next = color;
                next.a = tintTarget.color.a;
                tintTarget.color = next;
                SpriteBrighten.Set(tintTarget, 0f);
                PlayerTintApplied?.Invoke(next, 0f);
            }
        }

        void ShowColorOnBars(UnityEngine.Color color)
        {
            AutoWireScrollbars();
            UnityEngine.Color.RGBToHSV(color, out float h, out float s, out float v);
            suppressCallback = true;
            bool wasSubscribed = subscribed;
            if (wasSubscribed)
                SubscribeScrollbars(false);

            if (hueScrollbar != null)
                hueScrollbar.SetValueWithoutNotify(h);
            if (saturationScrollbar != null)
                saturationScrollbar.SetValueWithoutNotify(s < 0.08f ? defaultSaturation : s);
            if (valueScrollbar != null)
                valueScrollbar.SetValueWithoutNotify(ValueSliderFrom(color));

            if (wasSubscribed)
                SubscribeScrollbars(true);
            suppressCallback = false;
            RefreshHsvStrips(true);
        }

        void EnsureHsvStrips()
        {
            AutoWireScrollbars();
            hueBar = EnsureBar(hueScrollbar, hueBar);
            satBar = EnsureBar(saturationScrollbar, satBar);
            valBar = EnsureBar(valueScrollbar, valBar);
        }

        static HsvBar EnsureBar(Scrollbar bar, HsvBar tint)
        {
            if (bar == null)
                return tint;
            Transform old = bar.transform.Find("Hsv");
            if (old != null)
                Destroy(old.gameObject);
            if (tint != null && tint.material != null)
                return tint;

            Image image = bar.GetComponent<Image>();
            if (image == null)
                return tint;

            Shader shader = Resources.Load<Shader>("DressUp/UI-HsvBar");
            if (shader == null)
                shader = Shader.Find("DressUp/UI-HsvBar");
            if (shader == null)
                return tint;

            tint = new HsvBar { image = image };
            tint.gradient = new Texture2D(4, 128, TextureFormat.RGBA32, false, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "HsvBar"
            };
            tint.material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            tint.material.SetTexture("_GradTex", tint.gradient);
            tint.material.SetFloat("_Fade", 0.14f);
            if (image.GetComponent<HsvBarFill>() == null)
                image.gameObject.AddComponent<HsvBarFill>();
            Canvas canvas = image.canvas != null ? image.canvas.rootCanvas : null;
            if (canvas != null)
                canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;
            image.material = tint.material;
            image.color = Color.white;
            image.SetVerticesDirty();
            return tint;
        }

        void RefreshHsvStrips(bool force)
        {
            EnsureHsvStrips();
            ReadBars(out float hue, out float sat, out float value);
            if (!force && Mathf.Abs(hue - shownHue) < 0.001f
                && Mathf.Abs(sat - shownSat) < 0.001f
                && Mathf.Abs(value - shownValue) < 0.001f)
                return;
            shownHue = hue;
            shownSat = sat;
            shownValue = value;
            PaintBar(hueBar, t => ResolveTint(t, sat, value));
            PaintBar(satBar, t => ResolveTint(hue, t, value));
            PaintBar(valBar, t => ResolveTint(hue, sat, t));
        }

        static void PaintBar(HsvBar tint, System.Func<float, UnityEngine.Color> colorAt)
        {
            if (tint?.gradient == null || tint.material == null || tint.image == null)
                return;
            Texture2D tex = tint.gradient;
            int height = tex.height;
            int width = tex.width;
            var pixels = new UnityEngine.Color[width * height];
            for (int y = 0; y < height; y++)
            {
                float t = height <= 1 ? 0f : y / (float)(height - 1);
                UnityEngine.Color color = colorAt(t);
                color.a = 1f;
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = color;
            }

            tex.SetPixels(pixels);
            tex.Apply(false);
            tint.image.SetVerticesDirty();
        }

        static void ReleaseBar(Scrollbar bar, HsvBar tint)
        {
            if (bar != null)
            {
                Image image = bar.GetComponent<Image>();
                if (image != null && tint != null && image.material == tint.material)
                    image.material = null;
            }

            if (tint == null)
                return;
            if (tint.material != null)
                Destroy(tint.material);
            if (tint.gradient != null)
                Destroy(tint.gradient);
        }

        void RefreshGradientChips()
        {
            EnsureGradientChips();
            bool show = TintStyle.TwoColor;
            if (startChip != null)
                startChip.gameObject.SetActive(show);
            if (endChip != null)
                endChip.gameObject.SetActive(show);
            if (!show)
                return;

            PaintStop(startSwatch, TintStyle.GradientStart);
            PaintStop(endSwatch, TintStyle.GradientEnd);
            int point = TintStyle.EditPoint;
            PaintRing(startRing, startSwatch, point == TintStyle.EditStart);
            PaintRing(endRing, endSwatch, point == TintStyle.EditEnd);
        }

        static void PaintStop(Image swatch, UnityEngine.Color color)
        {
            if (swatch == null)
                return;
            if (TintStyle.GradientAtOriginal && !HasVisibleColor(color))
                return;
            swatch.color = color;
        }

        static void PaintRing(Image ring, Image swatch, bool selected)
        {
            if (ring == null || ring == swatch)
                return;
            ring.color = selected ? new Color(0.56f, 0.45f, 0.39f, 1f) : new Color(1f, 1f, 1f, 0.35f);
        }

        /// <summary>用场景里的「渐变起 / 渐变止」。起是 A，止是 B。不改它们的位置和大小。</summary>
        void EnsureGradientChips()
        {
            if (gradientChipsBound)
                return;
            EnsureFade();
            Transform root = fadeRoot != null ? fadeRoot : transform;
            startChip = FindChip(root, "渐变起");
            endChip = FindChip(root, "渐变止");
            if (startChip == null || endChip == null)
                return;

            BindChip(startChip, TintStyle.EditStart, out startRing, out startSwatch);
            BindChip(endChip, TintStyle.EditEnd, out endRing, out endSwatch);
            gradientChipsBound = true;
        }

        static RectTransform FindChip(Transform root, string name)
        {
            if (root == null)
                return null;
            Transform found = root.Find(name);
            return found as RectTransform;
        }

        void BindChip(RectTransform chip, int point, out Image ring, out Image swatch)
        {
            Transform ringTransform = chip.Find("Ring");
            Transform swatchTransform = chip.Find("Swatch");
            ring = ringTransform != null ? ringTransform.GetComponent<Image>() : null;
            swatch = swatchTransform != null ? swatchTransform.GetComponent<Image>() : chip.GetComponent<Image>();
            Button button = chip.GetComponent<Button>();
            if (button == null)
                return;
            button.onClick.RemoveAllListeners();
            int edit = point;
            button.onClick.AddListener(() => SelectGradientPoint(edit));
        }

        void SelectGradientPoint(int point)
        {
            TintStyle.EditPoint = point;
            Color show = point == TintStyle.EditEnd ? TintStyle.GradientEnd : TintStyle.GradientStart;
            ShowColorOnBars(show);
            RefreshGradientChips();
        }
    }
}
